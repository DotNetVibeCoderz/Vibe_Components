using System.Globalization;
using System.Text.RegularExpressions;

namespace Electrical.Circuit;

/// <summary>
/// Parser for a SPICE-like netlist subset. Supported cards (case-insensitive, one per line, '+' continues a line, '*' / ';' comments):
/// <code>
/// Rname n1 n2 value
/// Cname n1 n2 value [IC=v]
/// Lname n1 n2 value [IC=i]
/// Vname n+ n- [DC] value | SIN(vo va f [td theta phase]) | PULSE(v1 v2 td tr tf pw per) | PWL(t1 v1 t2 v2 ...) [AC mag [phase]]
/// Iname n+ n- (same source syntax)
/// Dname anode cathode [IS=..] [N=..]
/// Sname n1 n2 ON|OFF [T=toggle_time] [RON=..] [ROFF=..]
/// Kname Lname1 Lname2 k
/// Ename n+ n- nc+ nc- gain      Gname n+ n- nc+ nc- gm
/// Xname p1 p2 s1 s2 ratio       (ideal transformer, Np/Ns; non-standard card)
/// </code>
/// Values accept SPICE suffixes f p n u m k meg g t (and "mil"); dot-directives (.tran, .ac, .op, .end …) are ignored.
/// </summary>
public static partial class Netlist
{
    [GeneratedRegex(@"^(?<kind>SIN|PULSE|PWL)\s*\((?<args>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionRegex();

    /// <summary>Parses netlist text into a <see cref="ElectricCircuit"/>.</summary>
    /// <param name="text">Netlist text.</param>
    /// <param name="firstLineIsTitle">Treat the first line as the title (SPICE convention).</param>
    public static ElectricCircuit Parse(string text, bool firstLineIsTitle = false)
    {
        var c = new ElectricCircuit();
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var l = raw;
            int sc = l.IndexOf(';');
            if (sc >= 0) l = l[..sc];
            l = l.Trim();
            if (l.StartsWith('+') && lines.Count > 0) { lines[^1] += " " + l[1..]; continue; }
            lines.Add(l);
        }
        int lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            if (lineNo == 1 && firstLineIsTitle) { c.Title = line; continue; }
            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith('.')) continue;
            try { ParseCard(c, line); }
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                throw new FormatException($"Netlist line {lineNo}: '{line}': {e.Message}", e);
            }
        }
        return c;
    }

    private static void ParseCard(ElectricCircuit c, string line)
    {
        var tok = Regex.Split(line, @"\s+");
        string name = tok[0];
        char kind = char.ToUpperInvariant(name[0]);
        switch (kind)
        {
            case 'R': c.AddResistor(name, tok[1], tok[2], Value(tok[3])); break;
            case 'C': c.AddCapacitor(name, tok[1], tok[2], Value(tok[3]), Param(tok, 4, "IC")); break;
            case 'L': c.AddInductor(name, tok[1], tok[2], Value(tok[3]), Param(tok, 4, "IC")); break;
            case 'V':
            case 'I':
                {
                    string rest = string.Join(' ', tok.Skip(3));
                    var (wf, acMag, acPh) = ParseSource(rest);
                    if (kind == 'V') c.AddVoltageSource(name, tok[1], tok[2], wf, acMag, acPh);
                    else c.AddCurrentSource(name, tok[1], tok[2], wf, acMag, acPh);
                    break;
                }
            case 'D': c.AddDiode(name, tok[1], tok[2], Param(tok, 3, "IS") ?? 1e-14, Param(tok, 3, "N") ?? 1.0); break;
            case 'S':
                {
                    bool on = tok.Skip(3).Any(t => t.Equals("ON", StringComparison.OrdinalIgnoreCase));
                    double toggle = Param(tok, 3, "T") ?? double.PositiveInfinity;
                    c.AddSwitch(name, tok[1], tok[2], on, toggle, Param(tok, 3, "RON") ?? 1e-3, Param(tok, 3, "ROFF") ?? 1e9);
                    break;
                }
            case 'K': c.AddCoupling(name, tok[1], tok[2], Value(tok[3])); break;
            case 'E': c.AddVcvs(name, tok[1], tok[2], tok[3], tok[4], Value(tok[5])); break;
            case 'G': c.AddVccs(name, tok[1], tok[2], tok[3], tok[4], Value(tok[5])); break;
            case 'X': c.AddIdealTransformer(name, tok[1], tok[2], tok[3], tok[4], Value(tok[5])); break;
            default: throw new FormatException($"Unsupported element type '{kind}'.");
        }
    }

    private static double? Param(string[] tok, int from, string key)
    {
        for (int i = from; i < tok.Length; i++)
        {
            var t = tok[i];
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Equals(key, StringComparison.OrdinalIgnoreCase)) return Value(t[(eq + 1)..]);
        }
        return null;
    }

    private static (Waveform, double, double) ParseSource(string rest)
    {
        rest = rest.Trim();
        Waveform wf = Waveform.Dc(0);
        double acMag = 0, acPh = 0;
        var m = FunctionRegex().Match(rest);
        if (m.Success)
        {
            var a = Regex.Split(m.Groups["args"].Value.Trim().Replace(',', ' '), @"\s+").Where(s => s.Length > 0).Select(Value).ToArray();
            double A(int i, double d) => i < a.Length ? a[i] : d;
            wf = m.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "SIN" => Waveform.Sine(A(0, 0), A(1, 0), A(2, 0), A(3, 0), A(4, 0), A(5, 0)),
                "PULSE" => Waveform.Pulse(A(0, 0), A(1, 0), A(2, 0), A(3, 1e-9), A(4, 1e-9), A(5, double.PositiveInfinity), A(6, 0)),
                _ => Waveform.PiecewiseLinear(Enumerable.Range(0, a.Length / 2).Select(i => (a[2 * i], a[2 * i + 1])).ToArray()),
            };
            rest = rest[m.Length..].Trim();
        }
        var tok = Regex.Split(rest, @"\s+").Where(s => s.Length > 0).ToArray();
        for (int i = 0; i < tok.Length; i++)
        {
            if (tok[i].Equals("DC", StringComparison.OrdinalIgnoreCase) && i + 1 < tok.Length) { wf = Waveform.Dc(Value(tok[++i])); continue; }
            if (tok[i].Equals("AC", StringComparison.OrdinalIgnoreCase))
            {
                acMag = i + 1 < tok.Length ? Value(tok[++i]) : 1;
                if (i + 1 < tok.Length && TryValue(tok[i + 1], out double ph)) { acPh = ph; i++; }
                continue;
            }
            if (TryValue(tok[i], out double v)) wf = Waveform.Dc(v);
        }
        return (wf, acMag, acPh);
    }

    /// <summary>Parses a SPICE number with optional scale suffix (f p n u m k meg g t mil) and trailing unit letters.</summary>
    public static double Value(string s) => TryValue(s, out double v) ? v : throw new FormatException($"Invalid value '{s}'.");

    public static bool TryValue(string s, out double value)
    {
        value = 0;
        s = s.Trim();
        int k = 0;
        while (k < s.Length && (char.IsDigit(s[k]) || s[k] is '.' or '-' or '+' ||
               ((s[k] is 'e' or 'E') && k + 1 < s.Length && (char.IsDigit(s[k + 1]) || s[k + 1] is '-' or '+')))) k++;
        if (k == 0 || !double.TryParse(s[..k], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return false;
        string suf = s[k..].ToLowerInvariant();
        double scale = suf switch
        {
            _ when suf.StartsWith("meg") => 1e6,
            _ when suf.StartsWith("mil") => 25.4e-6,
            _ when suf.StartsWith('t') => 1e12,
            _ when suf.StartsWith('g') => 1e9,
            _ when suf.StartsWith('k') => 1e3,
            _ when suf.StartsWith('m') => 1e-3,
            _ when suf.StartsWith('u') || suf.StartsWith('µ') => 1e-6,
            _ when suf.StartsWith('n') => 1e-9,
            _ when suf.StartsWith('p') => 1e-12,
            _ when suf.StartsWith('f') => 1e-15,
            _ => 1,
        };
        value = num * scale;
        return true;
    }
}
