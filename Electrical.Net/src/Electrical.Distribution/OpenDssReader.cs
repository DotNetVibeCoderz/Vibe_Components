using System.Globalization;
using System.Numerics;
using System.Text;

namespace Electrical.Distribution;

/// <summary>
/// Reader for a practical subset of OpenDSS scripts: <c>Clear</c>, <c>New Circuit</c>/<c>Vsource</c> (ideal source), <c>LineCode</c>
/// (rmatrix/xmatrix/cmatrix or r1/x1/r0/x0/c1/c0, units), <c>WireData</c> + <c>LineGeometry</c> (Carson's equations), <c>Line</c> (node notation
/// bus.1.2.3, linecode/geometry/switch, length, units), <c>Transformer</c> (2 windings, wye/delta, kVs, kVAs, %R, XHL, taps), <c>Load</c>
/// (models 1/2/5, kW/kvar/pf/kVA), <c>Capacitor</c>, <c>RegControl</c> (turns its transformer into a <see cref="VoltageRegulator"/>),
/// <c>CapControl</c>, <c>Edit</c>, <c>Class.Name.Prop=value</c>, <c>Set VoltageBases</c>/<c>DefaultBaseFrequency</c>/<c>ControlMode</c>,
/// <c>CalcV</c>, <c>Redirect</c>/<c>Compile</c> (relative paths), comments (<c>!</c>, <c>//</c>, <c>/* */</c>), continuation (<c>~</c>, <c>more</c>)
/// and in-line RPN expressions such as <c>(8 1000 /)</c>. Unsupported objects are skipped with a warning in
/// <see cref="DistributionNetwork.Warnings"/>.
/// </summary>
public sealed class OpenDssReader
{
    private sealed class DssObject(string cls, string name)
    {
        public string Class { get; } = cls;
        public string Name { get; } = name;
        public List<(string Key, string Value)> Props { get; } = [];
        public string? Get(string key)
        {
            for (int i = Props.Count - 1; i >= 0; i--) if (Props[i].Key == key) return Props[i].Value;
            return null;
        }
        public bool Has(string key) => Get(key) is not null;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly List<DssObject> _objects = [];
    private readonly Dictionary<string, DssObject> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private readonly List<double> _voltageBases = [];
    private double _frequency = 60;
    private bool _controlsOff;
    private int _depth;

    /// <summary>Parses a script; <paramref name="baseDirectory"/> resolves <c>Redirect</c>/<c>Compile</c> paths.</summary>
    public static DistributionNetwork Parse(string script, string? baseDirectory = null)
    {
        var r = new OpenDssReader();
        r.Run(script, baseDirectory ?? Directory.GetCurrentDirectory());
        return r.Build();
    }

    /// <summary>Parses a .dss file (redirects are resolved relative to its folder).</summary>
    public static DistributionNetwork ParseFile(string path)
    {
        var full = Path.GetFullPath(path);
        return Parse(File.ReadAllText(full), Path.GetDirectoryName(full));
    }

    // ------------------------------------------------------------------ lexing

    private void Run(string script, string dir)
    {
        if (++_depth > 16) throw new InvalidOperationException("Redirect nesting too deep.");
        foreach (var cmd in Commands(script)) Execute(cmd, dir);
        _depth--;
    }

    private static IEnumerable<string> Commands(string script)
    {
        var text = StripBlockComments(script);
        StringBuilder? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            bool cont = line.StartsWith('~') || line.StartsWith("more ", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("m ", StringComparison.OrdinalIgnoreCase);
            if (cont && current is not null)
            {
                current.Append(' ').Append(line[(line.StartsWith('~') ? 1 : line.IndexOf(' '))..]);
                continue;
            }
            if (current is not null) yield return current.ToString();
            current = new StringBuilder(line);
        }
        if (current is not null) yield return current.ToString();
    }

    private static string StripBlockComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int start = s.IndexOf("/*", i, StringComparison.Ordinal);
            if (start < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, start - i);
            int end = s.IndexOf("*/", start + 2, StringComparison.Ordinal);
            if (end < 0) break;
            sb.Append('\n', s.AsSpan(start, end - start).Count('\n'));
            i = end + 2;
        }
        return sb.ToString();
    }

    private static string StripComment(string line)
    {
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c is '"' or '\'') { q = c; continue; }
            if (c == '!' || (c == '/' && i + 1 < line.Length && line[i + 1] == '/')) return line[..i];
        }
        return line;
    }

    /// <summary>Splits a command into tokens: whitespace/comma separated, brackets and quotes kept together, '=' as its own token.</summary>
    private static List<string> Tokens(string s)
    {
        var r = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        char q = '\0';
        void Flush() { if (sb.Length > 0) { r.Add(sb.ToString()); sb.Clear(); } }
        foreach (char c in s)
        {
            if (q != '\0') { sb.Append(c); if (c == q) q = '\0'; continue; }
            if (c is '"' or '\'') { q = c; sb.Append(c); continue; }
            if (c is '(' or '[' or '{') { depth++; sb.Append(c); continue; }
            if (c is ')' or ']' or '}') { depth = Math.Max(0, depth - 1); sb.Append(c); continue; }
            if (depth == 0 && (char.IsWhiteSpace(c) || c == ',')) { Flush(); continue; }
            if (depth == 0 && c == '=') { Flush(); r.Add("="); continue; }
            sb.Append(c);
        }
        Flush();
        return r;
    }

    private static List<(string Key, string Value)> Pairs(List<string> tokens, int start)
    {
        var r = new List<(string, string)>();
        for (int i = start; i < tokens.Count; i++)
        {
            if (i + 1 < tokens.Count && tokens[i + 1] == "=")
            {
                string v = i + 2 < tokens.Count ? tokens[i + 2] : "";
                r.Add((tokens[i].ToLowerInvariant(), v));
                i += 2;
            }
            else if (tokens[i] != "=") r.Add(("", tokens[i]));
        }
        return r;
    }

    // ------------------------------------------------------------------ commands

    private void Execute(string cmd, string dir)
    {
        var tokens = Tokens(cmd);
        if (tokens.Count == 0) return;
        string verb = tokens[0].ToLowerInvariant();
        switch (verb)
        {
            case "clear":
                _objects.Clear(); _byKey.Clear(); _voltageBases.Clear(); _controlsOff = false;
                return;
            case "new":
            case "edit":
            {
                if (tokens.Count < 2) return;
                int start = 2;
                string spec = tokens[1];
                if (spec.Equals("object", StringComparison.OrdinalIgnoreCase) && tokens.Count > 3 && tokens[2] == "=") { spec = tokens[3]; start = 4; }
                int dot = spec.IndexOf('.');
                if (dot < 0) { _warnings.Add($"Cannot parse object name '{spec}'."); return; }
                string cls = spec[..dot].ToLowerInvariant(), name = spec[(dot + 1)..].ToLowerInvariant();
                if (cls == "circuit") { cls = "vsource"; name = "source"; }
                var obj = GetOrCreate(cls, name, verb == "new");
                if (obj is null) { _warnings.Add($"Edit of undefined object {spec}."); return; }
                obj.Props.AddRange(Pairs(tokens, start));
                return;
            }
            case "set":
            {
                foreach (var (k, v) in Pairs(tokens, 1))
                {
                    switch (k)
                    {
                        case "voltagebases": _voltageBases.Clear(); _voltageBases.AddRange(Nums(v)); break;
                        case "defaultbasefrequency" or "basefrequency": _frequency = Num(v); break;
                        case "controlmode": _controlsOff = v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
                return;
            }
            case "redirect":
            case "compile":
            {
                if (tokens.Count < 2) return;
                var file = Unwrap(tokens[1]);
                var path = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
                if (!File.Exists(path)) { _warnings.Add($"Redirect file not found: {file}"); return; }
                var full = Path.GetFullPath(path);
                Run(File.ReadAllText(full), Path.GetDirectoryName(full)!);
                return;
            }
            case "calcv" or "calcvoltagebases" or "solve" or "buscoords" or "show" or "export" or "plot" or "batchedit" or "sample" or "reset" or "visualize":
                if (verb == "batchedit") _warnings.Add("BatchEdit is not supported; ignored.");
                return;
        }
        // Class.Name.Property=value
        if (tokens.Count >= 3 && tokens[1] == "=" && tokens[0].Count(c => c == '.') >= 2)
        {
            var parts = tokens[0].Split('.');
            string cls = parts[0].ToLowerInvariant(), name = string.Join('.', parts[1..^1]).ToLowerInvariant();
            var obj = GetOrCreate(cls, name, false);
            if (obj is null) { _warnings.Add($"Assignment to undefined object {tokens[0]}."); return; }
            obj.Props.Add((parts[^1].ToLowerInvariant(), tokens[2]));
            return;
        }
        _warnings.Add($"Unsupported command: {cmd}");
    }

    private DssObject? GetOrCreate(string cls, string name, bool create)
    {
        string key = cls + "." + name;
        if (_byKey.TryGetValue(key, out var o)) return o;
        if (!create) return null;
        o = new DssObject(cls, name);
        _objects.Add(o);
        _byKey[key] = o;
        return o;
    }

    // ------------------------------------------------------------------ values

    private static string Unwrap(string v)
    {
        v = v.Trim();
        while (v.Length >= 2 && ((v[0] == '[' && v[^1] == ']') || (v[0] == '(' && v[^1] == ')') || (v[0] == '{' && v[^1] == '}') ||
                                 (v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            v = v[1..^1].Trim();
        return v;
    }

    private static string[] Split(string v) =>
        Unwrap(v).Split([' ', '\t', ',', '|'], StringSplitOptions.RemoveEmptyEntries);

    private static double Num(string v)
    {
        var parts = Unwrap(v).Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return ParseDouble(parts[0]);
        // RPN expression, e.g. (8 1000 /)
        var stack = new Stack<double>();
        foreach (var p in parts)
        {
            switch (p)
            {
                case "+": { var b = stack.Pop(); stack.Push(stack.Pop() + b); break; }
                case "-": { var b = stack.Pop(); stack.Push(stack.Pop() - b); break; }
                case "*": { var b = stack.Pop(); stack.Push(stack.Pop() * b); break; }
                case "/": { var b = stack.Pop(); stack.Push(stack.Pop() / b); break; }
                case "sqrt": stack.Push(Math.Sqrt(stack.Pop())); break;
                case "sqr": { var a = stack.Pop(); stack.Push(a * a); break; }
                case "pi": stack.Push(Math.PI); break;
                default: stack.Push(ParseDouble(p)); break;
            }
        }
        return stack.Count > 0 ? stack.Peek() : 0;
    }

    private static double ParseDouble(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, Inv, out var d)) return d;
        throw new FormatException($"Invalid number '{s}'.");
    }

    private static double[] Nums(string v) => Split(v).Select(ParseDouble).ToArray();

    private static double[,] Matrix(string v, int n)
    {
        var rows = Unwrap(v).Split('|').Select(r => r.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries).Select(ParseDouble).ToArray()).ToArray();
        var m = new double[n, n];
        if (rows.Length == 1 && rows[0].Length == n * n)
        {
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = rows[0][i * n + j];
            return m;
        }
        if (rows.Length == 1 && rows[0].Length == n * (n + 1) / 2 && n > 1)
        {
            int k = 0;
            for (int i = 0; i < n; i++) for (int j = 0; j <= i; j++) { m[i, j] = m[j, i] = rows[0][k++]; }
            return m;
        }
        if (rows.Length != n) throw new FormatException($"Matrix '{v}' does not have {n} rows.");
        for (int i = 0; i < n; i++)
        {
            if (rows[i].Length == i + 1) for (int j = 0; j <= i; j++) { m[i, j] = rows[i][j]; m[j, i] = rows[i][j]; }
            else if (rows[i].Length == n) for (int j = 0; j < n; j++) m[i, j] = rows[i][j];
            else throw new FormatException($"Matrix row {i + 1} of '{v}' has {rows[i].Length} values.");
        }
        return m;
    }

    private static bool Bool(string v) => Unwrap(v).ToLowerInvariant() is "y" or "yes" or "true" or "t" or "1";

    private static (string Bus, int[] Nodes) BusSpec(string v)
    {
        var s = Unwrap(v).ToLowerInvariant();
        var parts = s.Split('.');
        var nodes = parts.Skip(1).Where(p => p.Length > 0).Select(p => int.Parse(p, Inv)).ToArray();
        return (parts[0], nodes);
    }

    private static int[] PhaseNodes(int[] nodes, int phases)
    {
        var ph = nodes.Where(n => n is >= 1 and <= 3).Select(n => n - 1).ToArray();
        return ph.Length > 0 ? ph.Take(phases).ToArray() : Enumerable.Range(0, Math.Min(phases, 3)).ToArray();
    }

    private static double ToFeet(double value, string? unit) => (unit ?? "ft").Trim().ToLowerInvariant() switch
    {
        "in" or "inch" => value / 12,
        "m" => value / Lengths.MetersPerFoot,
        "cm" => value / 100 / Lengths.MetersPerFoot,
        "mm" => value / 1000 / Lengths.MetersPerFoot,
        "mi" => value * Lengths.FeetPerMile,
        "kft" => value * 1000,
        "km" => value * 1000 / Lengths.MetersPerFoot,
        _ => value,
    };

    // ------------------------------------------------------------------ model build

    private DistributionNetwork Build()
    {
        var net = new DistributionNetwork { FrequencyHz = _frequency };
        var busOrder = new List<string>();
        void UseBus(string b) { if (!busOrder.Contains(b)) busOrder.Add(b); }

        var srcObj = _objects.FirstOrDefault(o => o.Class == "vsource");
        double srcKv = 115, srcPu = 1, srcAng = 0;
        string srcBus = "sourcebus";
        if (srcObj is not null)
        {
            foreach (var (k, v) in srcObj.Props)
            {
                switch (k)
                {
                    case "basekv": srcKv = Num(v); break;
                    case "pu": srcPu = Num(v); break;
                    case "angle": srcAng = Num(v); break;
                    case "bus1": srcBus = BusSpec(v).Bus; break;
                    case "basefreq" or "frequency": net.FrequencyHz = Num(v); break;
                }
            }
            if (srcObj.Has("mvasc3") || srcObj.Has("z1") || srcObj.Has("r1"))
                _warnings.Add("Source impedance ignored: the source is modelled as an ideal (infinite) bus.");
        }
        else _warnings.Add("No circuit defined; using an ideal 115 kV source at 'sourcebus'.");
        UseBus(srcBus);
        foreach (var o in _objects.Where(o => o.Class == "vsource" && o != srcObj)) _warnings.Add($"Additional Vsource.{o.Name} ignored.");

        // line codes and wires
        var codes = new Dictionary<string, (LineCode Code, LengthUnit Unit)>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in _objects.Where(o => o.Class == "linecode")) codes[o.Name] = BuildLineCode(o, o.Name, net.FrequencyHz);
        var wires = new Dictionary<string, ConductorData>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in _objects.Where(o => o.Class == "wiredata")) wires[o.Name] = BuildWire(o);
        var geometries = new Dictionary<string, LineCode>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in _objects.Where(o => o.Class == "linegeometry"))
        {
            try { geometries[o.Name] = BuildGeometry(o, wires, net.FrequencyHz); }
            catch (Exception ex) { _warnings.Add($"LineGeometry.{o.Name}: {ex.Message}"); }
        }
        foreach (var (name, c) in codes) net.LineCodes[name] = c.Code;

        // transformers controlled by RegControls become regulators
        var regControls = _objects.Where(o => o.Class == "regcontrol").ToList();
        var regTransformers = new HashSet<string>(regControls.Select(o => (o.Get("transformer") ?? "").ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        var lines = new List<(DssObject O, string B1, string B2, int[] Ph, LineCode Code, double LenKm, bool Switch, bool Enabled)>();
        foreach (var o in _objects.Where(o => o.Class == "line"))
        {
            try { lines.Add(BuildLine(o, codes, geometries, net.FrequencyHz)); }
            catch (Exception ex) { _warnings.Add($"Line.{o.Name}: {ex.Message}"); }
        }

        var xfmrs = new List<TransformerSpec>();
        foreach (var o in _objects.Where(o => o.Class == "transformer"))
        {
            try { xfmrs.Add(ParseTransformer(o)); }
            catch (Exception ex) { _warnings.Add($"Transformer.{o.Name}: {ex.Message}"); }
        }

        foreach (var l in lines) { UseBus(l.B1); UseBus(l.B2); }
        foreach (var t in xfmrs) { UseBus(t.Bus1); UseBus(t.Bus2); }
        foreach (var o in _objects.Where(o => o.Class is "load" or "capacitor"))
            if (o.Get("bus1") is { } b1) UseBus(BusSpec(b1).Bus);

        // base voltages: propagate from the source through transformer ratios, then snap to the declared voltage bases
        var est = EstimateBaseKv(busOrder, srcBus, srcKv, lines.Select(l => (l.B1, l.B2)).ToList(), xfmrs);
        foreach (var b in busOrder)
        {
            double kv = est.TryGetValue(b, out var e) ? e : srcKv;
            if (_voltageBases.Count > 0) kv = _voltageBases.MinBy(vb => Math.Abs(Math.Log(vb / kv)));
            if (!est.ContainsKey(b)) _warnings.Add($"Bus {b} is not connected to the source.");
            net.AddBus(b, kv, PhaseSet.None);
        }
        net.SetSource(srcBus, srcPu, srcAng);

        foreach (var l in lines)
        {
            if (!l.Enabled) continue;
            net.Lines.Add(new LineSegment { Name = l.O.Name, From = l.B1, To = l.B2, Code = l.Code, LengthKm = l.LenKm, PhaseOrder = l.Ph, IsSwitch = l.Switch });
        }

        var regulatorByXfmr = new Dictionary<string, VoltageRegulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in xfmrs)
        {
            if (regTransformers.Contains(t.Name))
            {
                int np = t.Phases.Count();
                double kvLN = np == 3 ? t.Kv2 / Math.Sqrt(3) : t.Kv2;
                var reg = net.AddRegulator(t.Name, t.Bus1, t.Bus2, kvLN, t.Phases);
                reg.KvaPerPhase = t.Kva / np;
                reg.RPercent = Math.Max(t.RPercent, 1e-6);
                reg.XPercent = Math.Max(t.XPercent, 1e-4);
                foreach (var p in t.Phases.Indices()) reg.SetTap(p, (int)Math.Round((t.Tap2 / t.Tap1 - 1) / (reg.StepPercent / 100)));
                regulatorByXfmr[t.Name] = reg;
                continue;
            }
            var tr = net.AddTransformer(t.Name, t.Bus1, t.Bus2, t.Connection, t.Kva, t.Kv1, t.Kv2, t.RPercent, t.XPercent, t.Phases);
            tr.TapPrimary = t.Tap1;
            tr.TapSecondary = t.Tap2;
        }

        foreach (var o in _objects.Where(o => o.Class == "load"))
        {
            try { BuildLoad(o, net); }
            catch (Exception ex) { _warnings.Add($"Load.{o.Name}: {ex.Message}"); }
        }
        foreach (var o in _objects.Where(o => o.Class == "capacitor"))
        {
            try { BuildCapacitor(o, net); }
            catch (Exception ex) { _warnings.Add($"Capacitor.{o.Name}: {ex.Message}"); }
        }

        if (!_controlsOff)
        {
            foreach (var o in regControls)
            {
                var xname = (o.Get("transformer") ?? "").ToLowerInvariant();
                if (!regulatorByXfmr.TryGetValue(xname, out var reg)) { _warnings.Add($"RegControl.{o.Name}: transformer '{xname}' not found."); continue; }
                if (o.Get("enabled") is { } en && !Bool(en)) continue;
                var c = new VoltageRegulatorController(reg, o.Name)
                {
                    VoltageLevel = Num(o.Get("vreg") ?? "120"),
                    Bandwidth = Num(o.Get("band") ?? "3"),
                    PtRatio = Num(o.Get("ptratio") ?? "60"),
                    CtPrimary = Num(o.Get("ctprim") ?? "300"),
                    R = Num(o.Get("r") ?? "0"),
                    X = Num(o.Get("x") ?? "0"),
                    TimeDelay = Num(o.Get("delay") ?? "15"),
                };
                net.Controllers.Add(c);
            }
            foreach (var o in _objects.Where(o => o.Class == "capcontrol"))
            {
                try { BuildCapControl(o, net); }
                catch (Exception ex) { _warnings.Add($"CapControl.{o.Name}: {ex.Message}"); }
            }
        }

        var known = new HashSet<string> { "vsource", "linecode", "wiredata", "linegeometry", "line", "transformer", "load", "capacitor", "regcontrol", "capcontrol" };
        foreach (var o in _objects.Where(o => !known.Contains(o.Class))) _warnings.Add($"Unsupported object {o.Class}.{o.Name} ignored.");
        net.Name = srcObj is null ? "opendss" : "opendss circuit";
        net.Warnings.AddRange(_warnings);
        return net;
    }

    private (LineCode, LengthUnit) BuildLineCode(DssObject o, string name, double freq)
    {
        int n = (int)Num(o.Get("nphases") ?? "3");
        var unit = o.Get("units") is { } u ? Lengths.Parse(Unwrap(u)) : LengthUnit.None;
        double f = o.Get("basefreq") is { } bf ? Num(bf) : freq;
        LineCode code;
        if (o.Has("rmatrix") || o.Has("xmatrix"))
        {
            var r = o.Get("rmatrix") is { } rm ? Matrix(rm, n) : new double[n, n];
            var x = o.Get("xmatrix") is { } xm ? Matrix(xm, n) : new double[n, n];
            var c = o.Get("cmatrix") is { } cm ? Matrix(cm, n) : null;
            code = LineCode.FromRxc(name, r, x, c, unit, f);
        }
        else
        {
            code = LineCode.FromSequence(name, n, Num(o.Get("r1") ?? "0.058"), Num(o.Get("x1") ?? "0.1206"), Num(o.Get("r0") ?? "0.1784"),
                Num(o.Get("x0") ?? "0.4047"), Num(o.Get("c1") ?? "3.4"), Num(o.Get("c0") ?? "1.6"), unit, f);
        }
        if (o.Get("normamps") is { } na) code.NormalAmps = Num(na);
        return (code, unit);
    }

    private ConductorData BuildWire(DssObject o)
    {
        string rUnit = Unwrap(o.Get("runits") ?? "none");
        double rPerUnit = Num(o.Get("rac") ?? o.Get("rdc") ?? "0");
        double rPerMile = rUnit is "none" ? rPerUnit : rPerUnit / Lengths.ToKm(1, Lengths.Parse(rUnit)) * Lengths.KmPerMile;
        string radUnit = Unwrap(o.Get("radunits") ?? "ft");
        double radiusFt = o.Get("diam") is { } d ? ToFeet(Num(d), radUnit) / 2 : ToFeet(Num(o.Get("radius") ?? "0.01"), radUnit);
        double gmrFt = o.Get("gmrac") is { } g ? ToFeet(Num(g), Unwrap(o.Get("gmrunits") ?? "ft")) : 0.7788 * radiusFt;
        return new ConductorData(o.Name, rPerMile, gmrFt, radiusFt * 24, Num(o.Get("normamps") ?? "0"));
    }

    private static LineCode BuildGeometry(DssObject o, Dictionary<string, ConductorData> wires, double freq)
    {
        int nconds = (int)Num(o.Get("nconds") ?? "4");
        int nph = (int)Num(o.Get("nphases") ?? "3");
        var wire = new ConductorData?[nconds];
        var x = new double[nconds];
        var h = new double[nconds];
        var units = new string?[nconds];
        int cur = 0;
        foreach (var (k, v) in o.Props)
        {
            switch (k)
            {
                case "cond": cur = (int)Num(v) - 1; break;
                case "wire": wire[cur] = wires.TryGetValue(Unwrap(v).ToLowerInvariant(), out var w) ? w : throw new KeyNotFoundException($"WireData '{v}' not found."); break;
                case "wires":
                {
                    var names = Split(v);
                    for (int i = 0; i < Math.Min(names.Length, nconds); i++) wire[i] = wires[names[i].ToLowerInvariant()];
                    break;
                }
                case "x": x[cur] = Num(v); break;
                case "h": h[cur] = Num(v); break;
                case "units": units[cur] = Unwrap(v); break;
            }
        }
        var conds = new List<ConductorPosition>();
        for (int i = 0; i < nconds; i++)
        {
            var role = i < nph ? (ConductorRole)i : ConductorRole.Neutral;
            var wd = wire[i] ?? throw new InvalidOperationException($"conductor {i + 1} has no wire.");
            string? un = units[i] ?? units.Take(i).LastOrDefault(u => u is not null);
            conds.Add(new ConductorPosition(role, ToFeet(x[i], un), ToFeet(h[i], un), wd));
        }
        var z = LineConstants.Overhead(new OverheadConfiguration(o.Name, conds), freq);
        return z.ToLineCode(o.Name);
    }

    private (DssObject, string, string, int[], LineCode, double, bool, bool) BuildLine(DssObject o,
        Dictionary<string, (LineCode Code, LengthUnit Unit)> codes, Dictionary<string, LineCode> geometries, double freq)
    {
        var (b1, n1) = BusSpec(o.Get("bus1") ?? throw new FormatException("bus1 missing"));
        var (b2, _) = BusSpec(o.Get("bus2") ?? throw new FormatException("bus2 missing"));
        bool isSwitch = o.Get("switch") is { } sw && Bool(sw);
        double length = Num(o.Get("length") ?? (isSwitch ? "0.001" : "1"));
        var lineUnit = o.Get("units") is { } u ? Lengths.Parse(Unwrap(u)) : LengthUnit.None;
        bool enabled = o.Get("enabled") is not { } en || Bool(en);
        LineCode code;
        LengthUnit codeUnit = LengthUnit.None;
        bool direct = o.Has("rmatrix") || o.Has("r1") || o.Has("x1") || isSwitch;
        int phases = (int)Num(o.Get("phases") ?? "0");
        if (direct)
        {
            int n = phases > 0 ? phases : 3;
            if (o.Has("rmatrix"))
                code = LineCode.FromRxc(o.Name, Matrix(o.Get("rmatrix")!, n), Matrix(o.Get("xmatrix") ?? o.Get("rmatrix")!, n),
                    o.Get("cmatrix") is { } cm ? Matrix(cm, n) : null, LengthUnit.None, freq);
            else
            {
                string d1 = isSwitch ? "1" : "0.058", dx1 = isSwitch ? "1" : "0.1206", d0 = isSwitch ? "1" : "0.1784", dx0 = isSwitch ? "1" : "0.4047";
                code = LineCode.FromSequence(o.Name, n, Num(o.Get("r1") ?? d1), Num(o.Get("x1") ?? dx1), Num(o.Get("r0") ?? d0),
                    Num(o.Get("x0") ?? dx0), Num(o.Get("c1") ?? (isSwitch ? "0" : "3.4")), Num(o.Get("c0") ?? (isSwitch ? "0" : "1.6")),
                    LengthUnit.None, freq);
            }
        }
        else if (o.Get("linecode") is { } lc)
        {
            if (!codes.TryGetValue(Unwrap(lc), out var c)) throw new KeyNotFoundException($"LineCode '{lc}' not found.");
            code = c.Code;
            codeUnit = c.Unit;
        }
        else if (o.Get("geometry") is { } g)
        {
            if (!geometries.TryGetValue(Unwrap(g), out var gc)) throw new KeyNotFoundException($"LineGeometry '{g}' not found.");
            code = gc;
            codeUnit = LengthUnit.Mile;
        }
        else
        {
            code = LineCode.FromSequence(o.Name, phases > 0 ? phases : 3, 0.058, 0.1206, 0.1784, 0.4047, 3.4, 1.6, LengthUnit.Kft, freq);
            codeUnit = LengthUnit.Kft;
        }
        if (phases == 0) phases = code.PhaseCount;
        if (phases != code.PhaseCount) throw new FormatException($"phases={phases} but its impedance has {code.PhaseCount} conductors.");
        var ph = PhaseNodes(n1, phases);
        if (ph.Length != phases) throw new FormatException($"bus1 has {ph.Length} phase nodes but phases={phases}.");
        // length in km: codes without units take the line's raw length; lines without units use the code's units
        double km = codeUnit == LengthUnit.None ? length : Lengths.ToKm(length, lineUnit == LengthUnit.None ? codeUnit : lineUnit);
        return (o, b1, b2, ph, code, km, isSwitch, enabled);
    }

    private sealed record TransformerSpec(string Name, string Bus1, string Bus2, PhaseSet Phases, TransformerConnection Connection, double Kva,
        double Kv1, double Kv2, double RPercent, double XPercent, double Tap1, double Tap2, bool Wye1, bool Wye2);

    private TransformerSpec ParseTransformer(DssObject o)
    {
        int phases = 3, windings = 2, w = 0;
        var bus = new string?[3]; var conn = new string[] { "wye", "wye", "wye" };
        var kv = new double[] { 12.47, 12.47, 12.47 }; var kva = new double[] { 1000, 1000, 1000 };
        var r = new double[] { 0.2, 0.2, 0.2 }; var tap = new double[] { 1, 1, 1 };
        double xhl = 7, loadLoss = double.NaN;
        foreach (var (k, v) in o.Props)
        {
            switch (k)
            {
                case "phases": phases = (int)Num(v); break;
                case "windings": windings = (int)Num(v); break;
                case "wdg": w = Math.Clamp((int)Num(v) - 1, 0, 2); break;
                case "bus": bus[w] = v; break;
                case "conn": conn[w] = Unwrap(v).ToLowerInvariant(); break;
                case "kv": kv[w] = Num(v); break;
                case "kva": kva[w] = Num(v); break;
                case "%r" or "r": r[w] = Num(v); break;
                case "tap": tap[w] = Num(v); break;
                case "buses": { var a = Split(v); for (int i = 0; i < Math.Min(a.Length, 3); i++) bus[i] = a[i]; break; }
                case "conns": { var a = Split(v); for (int i = 0; i < Math.Min(a.Length, 3); i++) conn[i] = a[i].ToLowerInvariant(); break; }
                case "kvs": { var a = Nums(v); for (int i = 0; i < Math.Min(a.Length, 3); i++) kv[i] = a[i]; break; }
                case "kvas": { var a = Nums(v); for (int i = 0; i < Math.Min(a.Length, 3); i++) kva[i] = a[i]; break; }
                case "%rs": { var a = Nums(v); for (int i = 0; i < Math.Min(a.Length, 3); i++) r[i] = a[i]; break; }
                case "taps": { var a = Nums(v); for (int i = 0; i < Math.Min(a.Length, 3); i++) tap[i] = a[i]; break; }
                case "xhl" or "x12": xhl = Num(v); break;
                case "%loadloss": loadLoss = Num(v); break;
                case "xfmrcode": _warnings.Add($"Transformer.{o.Name}: XfmrCode is not supported."); break;
            }
        }
        if (windings > 2) _warnings.Add($"Transformer.{o.Name}: only the first two of {windings} windings are modelled.");
        if (bus[0] is null || bus[1] is null) throw new FormatException("both winding buses are required");
        var (b1, n1) = BusSpec(bus[0]!);
        var (b2, _) = BusSpec(bus[1]!);
        bool wye1 = conn[0] is "wye" or "y" or "ln", wye2 = conn[1] is "wye" or "y" or "ln";
        var connection = (wye1, wye2) switch
        {
            (true, true) => TransformerConnection.WyeGWyeG,
            (false, true) => TransformerConnection.DeltaWyeG,
            (true, false) => TransformerConnection.WyeGDelta,
            _ => TransformerConnection.DeltaDelta,
        };
        var phaseSet = phases == 3 ? PhaseSet.ABC : PhaseSetExtensions.FromIndices(PhaseNodes(n1, phases));
        if (phases != 3 && connection != TransformerConnection.WyeGWyeG)
            throw new NotSupportedException("single-phase delta-connected windings are not supported");
        double rTotal = double.IsNaN(loadLoss) ? r[0] + r[1] : loadLoss;
        return new TransformerSpec(o.Name, b1, b2, phaseSet, connection, kva[0], kv[0], kv[1], rTotal, xhl, tap[0], tap[1], wye1, wye2);
    }

    private static Dictionary<string, double> EstimateBaseKv(List<string> buses, string src, double srcKv,
        List<(string, string)> lines, List<TransformerSpec> xfmrs)
    {
        var adj = new Dictionary<string, List<(string To, double Ratio)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string a, string b, double ratio)
        {
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = [];
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = [];
            la.Add((b, ratio)); lb.Add((a, 1 / ratio));
        }
        foreach (var (a, b) in lines) Add(a, b, 1);
        foreach (var t in xfmrs) Add(t.Bus1, t.Bus2, t.Kv2 / t.Kv1);
        var kv = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [src] = srcKv };
        var q = new Queue<string>();
        q.Enqueue(src);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            if (!adj.TryGetValue(u, out var list)) continue;
            foreach (var (v, ratio) in list)
                if (!kv.ContainsKey(v)) { kv[v] = kv[u] * ratio; q.Enqueue(v); }
        }
        return kv;
    }

    private void BuildLoad(DssObject o, DistributionNetwork net)
    {
        if (o.Get("enabled") is { } en && !Bool(en)) return;
        var (bus, nodes) = BusSpec(o.Get("bus1") ?? throw new FormatException("bus1 missing"));
        int phases = (int)Num(o.Get("phases") ?? "3");
        string conn = Unwrap(o.Get("conn") ?? "wye").ToLowerInvariant();
        bool delta = conn is "delta" or "d" or "ll";
        double kv = Num(o.Get("kv") ?? "12.47");
        double kw = 10, kvar = double.NaN, pf = 0.88;
        foreach (var (k, v) in o.Props)
        {
            switch (k)
            {
                case "kw": kw = Num(v); break;
                case "kvar": kvar = Num(v); break;
                case "pf": pf = Num(v); kvar = double.NaN; break;
                case "kva": { double sk = Num(v); kw = sk * Math.Abs(pf); break; }
            }
        }
        if (double.IsNaN(kvar)) kvar = kw * Math.Tan(Math.Acos(Math.Min(1, Math.Abs(pf)))) * Math.Sign(pf == 0 ? 1 : pf);
        int model = (int)Num(o.Get("model") ?? "1");
        var lm = model switch { 2 => LoadModel.ConstantImpedance, 5 => LoadModel.ConstantCurrent, 1 => LoadModel.ConstantPower, _ => LoadModel.ConstantPower };
        if (model is not (1 or 2 or 5)) _warnings.Add($"Load.{o.Name}: model {model} treated as constant power.");
        var ph = nodes.Where(n => n is >= 1 and <= 3).Select(n => n - 1).ToArray();
        var s = new Complex(kw, kvar);
        var load = new DistributionLoad { Name = o.Name, Bus = bus, Model = lm };
        if (phases == 1 && (delta || ph.Length == 2))
        {
            // single element between two phases
            if (ph.Length < 2) ph = [0, 1];
            int a = ph[0], b = ph[1];
            int elem = (a + 1) % 3 == b ? a : b;   // pair index ab=0, bc=1, ca=2
            load.Connection = LoadConnection.Delta;
            load.PowerKva[elem] = s;
            load.NominalKv = kv;
        }
        else if (delta)
        {
            load.Connection = LoadConnection.Delta;
            for (int i = 0; i < 3; i++) load.PowerKva[i] = s / 3;
            load.NominalKv = kv;
        }
        else
        {
            if (ph.Length == 0) ph = Enumerable.Range(0, Math.Min(phases, 3)).ToArray();
            ph = ph.Take(phases).ToArray();
            foreach (var p in ph) load.PowerKva[p] = s / ph.Length;
            load.NominalKv = phases == 1 ? kv : kv / Math.Sqrt(3);
        }
        net.Bus(bus);
        net.Loads.Add(load);
    }

    private void BuildCapacitor(DssObject o, DistributionNetwork net)
    {
        if (o.Get("enabled") is { } en && !Bool(en)) return;
        var (bus, nodes) = BusSpec(o.Get("bus1") ?? throw new FormatException("bus1 missing"));
        if (o.Get("bus2") is { } b2 && BusSpec(b2).Nodes.Any(n => n != 0)) _warnings.Add($"Capacitor.{o.Name}: series capacitors are not supported; modelled as shunt.");
        int phases = (int)Num(o.Get("phases") ?? "3");
        string conn = Unwrap(o.Get("conn") ?? "wye").ToLowerInvariant();
        bool delta = conn is "delta" or "d" or "ll";
        double kvar = Nums(o.Get("kvar") ?? "1200").Sum();
        double kv = Num(o.Get("kv") ?? "12.47");
        var cap = new ShuntCapacitor { Name = o.Name, Bus = bus, Connection = delta ? LoadConnection.Delta : LoadConnection.Wye };
        var ph = PhaseNodes(nodes, phases);
        if (delta)
        {
            if (phases == 1 && ph.Length >= 1)
            {
                int a = ph[0], b = ph.Length > 1 ? ph[1] : (a + 1) % 3;
                cap.Kvar[(a + 1) % 3 == b ? a : b] = kvar;
            }
            else for (int i = 0; i < 3; i++) cap.Kvar[i] = kvar / 3;
            cap.RatedKv = kv;
        }
        else
        {
            foreach (var p in ph) cap.Kvar[p] = kvar / ph.Length;
            cap.RatedKv = phases == 1 ? kv : kv / Math.Sqrt(3);
        }
        net.Bus(bus);
        net.Capacitors.Add(cap);
    }

    private void BuildCapControl(DssObject o, DistributionNetwork net)
    {
        var capName = Unwrap(o.Get("capacitor") ?? throw new FormatException("capacitor missing")).ToLowerInvariant();
        var cap = net.Capacitors.FirstOrDefault(c => c.Name == capName) ?? throw new KeyNotFoundException($"capacitor '{capName}' not found");
        string type = Unwrap(o.Get("type") ?? "current").ToLowerInvariant();
        var mode = type switch
        {
            "voltage" => CapacitorControlMode.Voltage,
            "kvar" => CapacitorControlMode.ReactivePower,
            "pf" => CapacitorControlMode.PowerFactor,
            "time" => CapacitorControlMode.Time,
            _ => throw new NotSupportedException($"type '{type}' is not supported"),
        };
        var c = new CapacitorController(cap, mode, o.Name)
        {
            OnSetting = Num(o.Get("onsetting") ?? "300"),
            OffSetting = Num(o.Get("offsetting") ?? "200"),
            OnDelay = Num(o.Get("delay") ?? "15"),
            OffDelay = Num(o.Get("delayoff") ?? "15"),
        };
        if (o.Get("element") is { } el)
        {
            var e = Unwrap(el);
            c.MonitoredElement = e.Contains('.') ? e[(e.IndexOf('.') + 1)..].ToLowerInvariant() : e.ToLowerInvariant();
        }
        if (mode == CapacitorControlMode.Voltage)
        {
            // OpenDSS settings are volts on the PT secondary: convert to pu of the capacitor bus
            double pt = Num(o.Get("ptratio") ?? "60");
            double vb = net.Bus(cap.Bus).BaseVoltsLN;
            c.OnSetting = c.OnSetting * pt / vb;
            c.OffSetting = c.OffSetting * pt / vb;
            if (o.Get("ptphase") is { } pp && int.TryParse(Unwrap(pp), NumberStyles.Integer, Inv, out var ip)) c.MonitoredPhase = ip - 1;
            c.MonitoredBus = cap.Bus;
        }
        if (mode == CapacitorControlMode.PowerFactor)
        {
            // OpenDSS: positive = lagging, negative = leading
            c.OnSetting = Math.Abs(c.OnSetting);
            c.OffSetting = Math.Abs(c.OffSetting);
        }
        net.Controllers.Add(c);
    }
}
