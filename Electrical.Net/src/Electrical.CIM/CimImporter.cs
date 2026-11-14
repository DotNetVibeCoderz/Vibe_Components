using System.Globalization;
using System.Xml.Linq;
using Electrical.Core.Network;

namespace Electrical.CIM;

/// <summary>Options for <see cref="CimImporter"/>.</summary>
public sealed record CimImportOptions
{
    /// <summary>System base [MVA] used when the file carries no <c>eln:Network.baseMva</c> extension. Default 100.</summary>
    public double BaseMva { get; init; } = 100;
    /// <summary>System frequency [Hz] used when the file carries no extension. Default 50.</summary>
    public double FrequencyHz { get; init; } = 50;
    /// <summary>Honour <c>eln:</c> extension attributes (bus ids, bus types, area/zone, costs). Default true.</summary>
    public bool UseExtensions { get; init; } = true;
    /// <summary>Network name used when the file has none.</summary>
    public string Name { get; init; } = "cim";
}

/// <summary>Result of a detailed CIM import.</summary>
public sealed class CimImportResult
{
    internal CimImportResult(PowerNetwork network, IReadOnlyList<string> warnings, IReadOnlyDictionary<string, int> nodeToBus)
    {
        Network = network; Warnings = warnings; NodeToBus = nodeToBus;
    }

    /// <summary>Imported bus-branch network.</summary>
    public PowerNetwork Network { get; }
    /// <summary>Unsupported or inconsistent content that was skipped.</summary>
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>ConnectivityNode / TopologicalNode mRID → bus id after topology processing.</summary>
    public IReadOnlyDictionary<string, int> NodeToBus { get; }
}

/// <summary>
/// Imports IEC 61970 CIM RDF/XML (CGMES EQ/TP/SSH/SV, merged or split into several documents) into a <see cref="PowerNetwork"/>.
/// Topology processing merges ConnectivityNodes joined by closed switches (Switch, Breaker, Disconnector, LoadBreakSwitch, Fuse, Jumper)
/// or assigned to the same TopologicalNode into one bus. Supported equipment: ACLineSegment, two-winding PowerTransformer (RatioTapChanger,
/// PhaseTapChangerLinear/Symmetrical), SynchronousMachine, ExternalNetworkInjection, EnergyConsumer (and Conform/NonConform loads),
/// LinearShuntCompensator, SvVoltage.
/// </summary>
public static class CimImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HashSet<string> SwitchClasses = ["Switch", "Breaker", "Disconnector", "LoadBreakSwitch", "Fuse", "Jumper", "GroundDisconnector"];
    private static readonly HashSet<string> LoadClasses = ["EnergyConsumer", "ConformLoad", "NonConformLoad", "StationSupply"];

    /// <summary>Imports from one RDF/XML string.</summary>
    public static PowerNetwork Import(string xml, CimImportOptions? options = null) => ImportDetailed([XDocument.Parse(xml)], options).Network;

    /// <summary>Imports from one or more documents (e.g. EQ, TP, SSH, SV) that share mRIDs.</summary>
    public static PowerNetwork Import(params XDocument[] documents) => ImportDetailed(documents).Network;

    /// <summary>Imports from a file path.</summary>
    public static PowerNetwork Load(string path, CimImportOptions? options = null) => ImportDetailed([XDocument.Load(path)], options).Network;

    /// <summary>Imports and returns warnings and the node → bus map.</summary>
    public static CimImportResult ImportDetailed(IEnumerable<XDocument> documents, CimImportOptions? options = null)
    {
        options ??= new CimImportOptions();
        var g = new Graph();
        foreach (var d in documents) g.Read(d);
        return new Builder(g, options).Build();
    }

    internal sealed class Obj(string id)
    {
        public string Id { get; } = id;
        public string Class { get; set; } = "";
        public Dictionary<string, string> P { get; } = new(StringComparer.Ordinal);
        public string? S(string k) => P.TryGetValue(k, out var v) ? v : null;
        public double? D(string k) => P.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, Inv, out var d) ? d : null;
        public int? I(string k) => D(k) is double d ? (int)Math.Round(d) : null;
        public bool? B(string k) => P.TryGetValue(k, out var v) ? v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || v.Trim() == "1" : null;
    }

    internal sealed class Graph
    {
        public Dictionary<string, Obj> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = [];
        public Dictionary<string, string> Header { get; } = new(StringComparer.Ordinal);

        public void Read(XDocument doc)
        {
            XNamespace rdf = CimNamespaces.Rdf;
            if (doc.Root is null) return;
            foreach (var e in doc.Root.Elements())
            {
                string? id = (string?)e.Attribute(rdf + "ID") ?? (string?)e.Attribute(rdf + "about");
                if (id is null) continue;
                if (e.Name.LocalName == "FullModel")
                {
                    foreach (var p in e.Elements()) Header[p.Name.LocalName] = p.Value;
                    continue;
                }
                id = Norm(id);
                if (!Objects.TryGetValue(id, out var o))
                {
                    o = new Obj(id);
                    Objects[id] = o;
                    Order.Add(id);
                }
                if (o.Class.Length == 0 || e.Attribute(rdf + "ID") is not null) o.Class = e.Name.LocalName;
                foreach (var p in e.Elements())
                {
                    var res = (string?)p.Attribute(rdf + "resource");
                    string v;
                    if (res is null) v = p.Value;
                    else if (res.StartsWith('#') || res.StartsWith("urn:uuid:", StringComparison.Ordinal) || !res.Contains('#')) v = Norm(res);
                    else { v = res[(res.LastIndexOf('#') + 1)..]; int dot = v.LastIndexOf('.'); if (dot >= 0) v = v[(dot + 1)..]; }
                    o.P[p.Name.LocalName] = v;
                }
            }
        }

        private static string Norm(string id)
        {
            if (id.StartsWith('#')) id = id[1..];
            if (id.StartsWith("urn:uuid:", StringComparison.Ordinal)) id = "_" + id[9..];
            return id;
        }

        public Obj? Get(string? id) => id is not null && Objects.TryGetValue(id, out var o) ? o : null;
        public IEnumerable<Obj> OfClass(params string[] cls) => Order.Select(i => Objects[i]).Where(o => cls.Contains(o.Class));
        public IEnumerable<Obj> All => Order.Select(i => Objects[i]);
    }

    private sealed class Builder(Graph g, CimImportOptions opt)
    {
        private readonly List<string> _warnings = [];
        private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Obj>> _terminals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _nodeBus = new(StringComparer.Ordinal);
        private readonly List<string> _nodeOrder = [];
        private readonly Dictionary<string, int> _rank = new(StringComparer.Ordinal);
        private PowerNetwork _net = null!;

        private string Find(string x)
        {
            while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; }
            return x;
        }

        private void Node(string id)
        {
            if (_parent.ContainsKey(id)) return;
            _parent[id] = id;
            _rank[id] = _nodeOrder.Count;
            _nodeOrder.Add(id);
        }

        private void Union(string a, string b)
        {
            Node(a); Node(b);
            string ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            // keep the root that appeared first so bus order follows the document
            if (_rank[ra] < _rank[rb]) _parent[rb] = ra; else _parent[ra] = rb;
        }

        private static string? TerminalNode(Obj t) => t.S("Terminal.ConnectivityNode") ?? t.S("Terminal.TopologicalNode");
        private static bool Connected(Obj t) => t.B("ACDCTerminal.connected") ?? t.B("Terminal.connected") ?? true;

        private List<Obj> Terminals(Obj eq) => _terminals.TryGetValue(eq.Id, out var l) ? l : [];

        private bool InService(Obj eq) => (eq.B("Equipment.inService") ?? true) && Terminals(eq).All(Connected);

        private int? BusOf(Obj? terminal) => terminal is not null && TerminalNode(terminal) is string n && _nodeBus.TryGetValue(n, out int b) ? b : null;

        public CimImportResult Build()
        {
            bool ext = opt.UseExtensions;
            _net = new PowerNetwork
            {
                Name = (ext ? g.Header.GetValueOrDefault("Network.name") : null) ?? opt.Name,
                BaseMva = ext && double.TryParse(g.Header.GetValueOrDefault("Network.baseMva"), NumberStyles.Float, Inv, out var mva) ? mva : opt.BaseMva,
                FrequencyHz = ext && double.TryParse(g.Header.GetValueOrDefault("Network.frequencyHz"), NumberStyles.Float, Inv, out var hz) ? hz : opt.FrequencyHz,
            };

            // 1. Nodes and terminals.
            foreach (var o in g.All)
            {
                if (o.Class is "ConnectivityNode" or "TopologicalNode") Node(o.Id);
                if (o.Class == "ConnectivityNode" && o.S("ConnectivityNode.TopologicalNode") is string tn) Union(o.Id, tn);
            }
            foreach (var t in g.OfClass("Terminal"))
            {
                if (TerminalNode(t) is string n) Node(n);
                if (t.S("Terminal.ConductingEquipment") is string eq)
                {
                    if (!_terminals.TryGetValue(eq, out var l)) _terminals[eq] = l = [];
                    l.Add(t);
                }
            }
            foreach (var l in _terminals.Values)
                l.Sort((a, b) => (a.I("ACDCTerminal.sequenceNumber") ?? a.I("Terminal.sequenceNumber") ?? 0)
                    .CompareTo(b.I("ACDCTerminal.sequenceNumber") ?? b.I("Terminal.sequenceNumber") ?? 0));

            // 2. Topology processing: closed switches merge their nodes.
            foreach (var sw in g.All.Where(o => SwitchClasses.Contains(o.Class)))
            {
                bool open = sw.B("Switch.open") ?? sw.B("Switch.normalOpen") ?? false;
                var ts = Terminals(sw);
                if (open || ts.Count != 2 || !ts.All(Connected)) continue;
                if (TerminalNode(ts[0]) is string a && TerminalNode(ts[1]) is string b) Union(a, b);
            }

            // 3. Buses.
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var rootOrder = new List<string>();
            foreach (var n in _nodeOrder)
            {
                string r = Find(n);
                if (!groups.TryGetValue(r, out var l)) { groups[r] = l = []; rootOrder.Add(r); }
                l.Add(n);
            }
            var locations = Locations();
            var buses = new List<(Bus Bus, List<string> Nodes)>();
            foreach (var r in rootOrder)
            {
                var nodes = groups[r];
                var objs = nodes.Select(g.Get).Where(o => o is not null).Select(o => o!).ToList();
                var cn = objs.FirstOrDefault(o => o.Class == "ConnectivityNode") ?? objs.FirstOrDefault();
                var bus = new Bus { Name = cn?.S("IdentifiedObject.name") };
                double? kv = null;
                Obj? vl = null;
                foreach (var o in objs)
                {
                    vl ??= g.Get(o.S("ConnectivityNode.ConnectivityNodeContainer")) is { Class: "VoltageLevel" } v ? v : null;
                    kv ??= ext ? o.D("ConnectivityNode.baseKv") : null;
                }
                kv ??= g.Get(vl?.S("VoltageLevel.BaseVoltage"))?.D("BaseVoltage.nominalVoltage");
                foreach (var o in objs) kv ??= g.Get(o.S("TopologicalNode.BaseVoltage"))?.D("BaseVoltage.nominalVoltage");
                bus.BaseKv = kv ?? 1;
                if (vl?.D("VoltageLevel.highVoltageLimit") is double hv && hv > 0) bus.Vmax = hv / bus.BaseKv;
                if (vl?.D("VoltageLevel.lowVoltageLimit") is double lv && lv > 0) bus.Vmin = lv / bus.BaseKv;
                if (ext && cn is not null)
                {
                    if (cn.I("ConnectivityNode.busId") is int id) bus.Id = id;
                    if (Enum.TryParse<BusType>(cn.S("ConnectivityNode.busType"), out var bt)) bus.Type = bt;
                    if (cn.I("ConnectivityNode.area") is int area) bus.Area = area;
                    if (cn.I("ConnectivityNode.zone") is int zone) bus.Zone = zone;
                    if (cn.D("ConnectivityNode.vmax") is double vmax) bus.Vmax = vmax;
                    if (cn.D("ConnectivityNode.vmin") is double vmin) bus.Vmin = vmin;
                }
                var ss = g.Get(vl?.S("VoltageLevel.Substation"));
                if (ss?.S("PowerSystemResource.Location") is string loc && locations.TryGetValue(loc, out var gp)) bus.Location = gp;
                buses.Add((bus, nodes));
            }
            bool idsOk = ext && buses.All(b => b.Bus.Id != 0) && buses.Select(b => b.Bus.Id).Distinct().Count() == buses.Count;
            for (int i = 0; i < buses.Count; i++)
            {
                if (!idsOk) buses[i].Bus.Id = i + 1;
                _net.Buses.Add(buses[i].Bus);
                foreach (var n in buses[i].Nodes) _nodeBus[n] = buses[i].Bus.Id;
            }
            bool typesFromExt = ext && g.OfClass("ConnectivityNode").Any(c => c.S("ConnectivityNode.busType") is not null);

            // 4. Equipment.
            var limits = Limits();
            var ends = g.OfClass("PowerTransformerEnd").GroupBy(e => e.S("PowerTransformerEnd.PowerTransformer") ?? "")
                .ToDictionary(x => x.Key, x => x.OrderBy(e => e.I("TransformerEnd.endNumber") ?? 0).ToList(), StringComparer.Ordinal);
            var tapChangers = g.All.Where(o => o.Class is "RatioTapChanger" or "PhaseTapChangerLinear" or "PhaseTapChangerSymmetrical")
                .ToLookup(o => o.S("RatioTapChanger.TransformerEnd") ?? o.S("PhaseTapChanger.TransformerEnd") ?? "", StringComparer.Ordinal);
            var slackCandidates = new List<(int Bus, int Priority)>();
            var regulating = new HashSet<int>();
            foreach (var o in g.All)
            {
                switch (o.Class)
                {
                    case "ACLineSegment": Line(o, limits); break;
                    case "PowerTransformer": Transformer(o, ends.GetValueOrDefault(o.Id) ?? [], tapChangers, limits); break;
                    case "SynchronousMachine" or "ExternalNetworkInjection": Machine(o, slackCandidates, regulating); break;
                    case var c when LoadClasses.Contains(c): LoadOf(o); break;
                    case "LinearShuntCompensator": Shunt(o); break;
                    case "SeriesCompensator": SeriesCompensator(o); break;
                    case "NonlinearShuntCompensator" or "StaticVarCompensator" or "AsynchronousMachine" or "EquivalentInjection":
                        _warnings.Add($"{o.Class} '{o.Id}' is not supported and was skipped."); break;
                }
            }

            // 5. Bus types.
            if (!typesFromExt)
            {
                foreach (var b in _net.Buses) b.Type = regulating.Contains(b.Id) ? BusType.PV : BusType.PQ;
                int? slack = slackCandidates.Where(c => c.Priority > 0).OrderBy(c => c.Priority).Select(c => (int?)c.Bus).FirstOrDefault();
                slack ??= _net.Generators.Where(x => x.InService).MaxBy(x => x.Pmax)?.Bus;
                if (slack is int s) _net.GetBus(s).Type = BusType.Slack;
            }

            // 6. State variables.
            foreach (var sv in g.OfClass("SvVoltage"))
            {
                if (sv.S("SvVoltage.TopologicalNode") is not string tn || !_nodeBus.TryGetValue(tn, out int busId)) continue;
                var b = _net.GetBus(busId);
                if (sv.D("SvVoltage.v") is double v && v > 0) b.Vm = v / b.BaseKv;
                if (sv.D("SvVoltage.angle") is double a) b.VaDeg = a;
            }
            return new CimImportResult(_net, _warnings, _nodeBus);
        }

        private Dictionary<string, GeoPoint> Locations()
        {
            var d = new Dictionary<string, GeoPoint>(StringComparer.Ordinal);
            foreach (var pp in g.OfClass("PositionPoint").OrderBy(p => p.I("PositionPoint.sequenceNumber") ?? 0))
                if (pp.S("PositionPoint.Location") is string loc && !d.ContainsKey(loc) && pp.D("PositionPoint.xPosition") is double x && pp.D("PositionPoint.yPosition") is double y)
                    d[loc] = new GeoPoint(y, x);
            return d;
        }

        /// <summary>Equipment mRID → apparent power limit [MVA] (via terminal or equipment limit sets).</summary>
        private Dictionary<string, double> Limits()
        {
            var termEq = g.OfClass("Terminal").Where(t => t.S("Terminal.ConductingEquipment") is not null)
                .ToDictionary(t => t.Id, t => t.S("Terminal.ConductingEquipment")!, StringComparer.Ordinal);
            var setEq = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in g.OfClass("OperationalLimitSet"))
            {
                if (s.S("OperationalLimitSet.Terminal") is string t && termEq.TryGetValue(t, out var eq)) setEq[s.Id] = eq;
                else if (s.S("OperationalLimitSet.Equipment") is string e) setEq[s.Id] = e;
            }
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var l in g.OfClass("ApparentPowerLimit"))
                if (l.S("OperationalLimit.OperationalLimitSet") is string set && setEq.TryGetValue(set, out var eq) && l.D("ApparentPowerLimit.value") is double v)
                    d[eq] = d.TryGetValue(eq, out var old) ? Math.Min(old, v) : v;
            return d;
        }

        private bool TwoBuses(Obj o, out int from, out int to)
        {
            var ts = Terminals(o);
            from = to = 0;
            if (ts.Count != 2 || BusOf(ts[0]) is not int f || BusOf(ts[1]) is not int t)
            {
                _warnings.Add($"{o.Class} '{o.Id}' does not have two connected terminals; skipped.");
                return false;
            }
            from = f; to = t;
            return true;
        }

        private void Line(Obj o, Dictionary<string, double> limits)
        {
            if (!TwoBuses(o, out int f, out int t)) return;
            double kv = _net.GetBus(f).BaseKv, zb = kv * kv / _net.BaseMva;
            var br = new Branch
            {
                From = f, To = t, Name = o.S("IdentifiedObject.name"),
                R = (o.D("ACLineSegment.r") ?? 0) / zb, X = (o.D("ACLineSegment.x") ?? 0) / zb, B = (o.D("ACLineSegment.bch") ?? 0) * zb,
                LengthKm = o.D("Conductor.length") ?? 0, RateA = limits.GetValueOrDefault(o.Id), InService = InService(o),
            };
            if (o.D("ACLineSegment.r0") is double r0) br.R0 = r0 / zb;
            if (o.D("ACLineSegment.x0") is double x0) br.X0 = x0 / zb;
            if (o.D("ACLineSegment.b0ch") is double b0) br.B0 = b0 * zb;
            if (opt.UseExtensions && Enum.TryParse<BranchKind>(o.S("ACLineSegment.kind"), out var k)) br.Kind = k;
            _net.Branches.Add(br);
        }

        private void SeriesCompensator(Obj o)
        {
            if (!TwoBuses(o, out int f, out int t)) return;
            double kv = _net.GetBus(f).BaseKv, zb = kv * kv / _net.BaseMva;
            _net.Branches.Add(new Branch
            {
                From = f, To = t, Name = o.S("IdentifiedObject.name"),
                R = (o.D("SeriesCompensator.r") ?? 0) / zb, X = (o.D("SeriesCompensator.x") ?? 0) / zb, InService = InService(o),
            });
        }

        private static double TapRatio(Obj? tc)
        {
            if (tc is null || tc.Class != "RatioTapChanger") return 1;
            double neutral = tc.D("TapChanger.neutralStep") ?? 0;
            double step = tc.D("TapChanger.step") ?? tc.D("TapChanger.normalStep") ?? neutral;
            return 1 + (step - neutral) * (tc.D("RatioTapChanger.stepVoltageIncrement") ?? 0) / 100;
        }

        private static double PhaseShift(Obj? tc)
        {
            if (tc is null || tc.Class is not ("PhaseTapChangerLinear" or "PhaseTapChangerSymmetrical")) return 0;
            double neutral = tc.D("TapChanger.neutralStep") ?? 0;
            double step = tc.D("TapChanger.step") ?? tc.D("TapChanger.normalStep") ?? neutral;
            double inc = tc.D("PhaseTapChangerLinear.stepPhaseShiftIncrement") ?? tc.D("PhaseTapChangerNonLinear.voltageStepIncrement") ?? 0;
            return (step - neutral) * inc;
        }

        private void Transformer(Obj o, List<Obj> ends, ILookup<string, Obj> tcs, Dictionary<string, double> limits)
        {
            if (ends.Count != 2)
            {
                _warnings.Add($"PowerTransformer '{o.Id}' has {ends.Count} ends; only two-winding transformers are supported.");
                return;
            }
            var e1 = ends[0];
            var e2 = ends[1];
            if (BusOf(g.Get(e1.S("TransformerEnd.Terminal"))) is not int f || BusOf(g.Get(e2.S("TransformerEnd.Terminal"))) is not int t)
            {
                _warnings.Add($"PowerTransformer '{o.Id}': end terminals not connected to nodes; skipped.");
                return;
            }
            double bf = _net.GetBus(f).BaseKv, bt = _net.GetBus(t).BaseKv;
            double u1 = e1.D("PowerTransformerEnd.ratedU") ?? bf, u2 = e2.D("PowerTransformerEnd.ratedU") ?? bt;
            double n2 = (u1 / u2) * (u1 / u2);
            double r = (e1.D("PowerTransformerEnd.r") ?? 0) + (e2.D("PowerTransformerEnd.r") ?? 0) * n2;
            double x = (e1.D("PowerTransformerEnd.x") ?? 0) + (e2.D("PowerTransformerEnd.x") ?? 0) * n2;
            double b = (e1.D("PowerTransformerEnd.b") ?? 0) + (e2.D("PowerTransformerEnd.b") ?? 0) / n2;
            // Ω referred to end 1 at ratedU1 → Ω at the to-side base → pu on the system base.
            double zbTo = bt * bt / _net.BaseMva, toSide = (u2 / u1) * (u2 / u1);
            Obj? Tc(Obj end, bool ratio) => tcs[end.Id].FirstOrDefault(c => ratio ? c.Class == "RatioTapChanger" : c.Class != "RatioTapChanger");
            double tap = (u1 / u2) / (bf / bt) * TapRatio(Tc(e1, true)) / TapRatio(Tc(e2, true));
            double shift = PhaseShift(Tc(e1, false)) - PhaseShift(Tc(e2, false));
            var br = new Branch
            {
                From = f, To = t, Name = o.S("IdentifiedObject.name"), Kind = BranchKind.Transformer,
                R = r * toSide / zbTo, X = x * toSide / zbTo, B = b / toSide * zbTo, Tap = tap, ShiftDeg = shift,
                RateA = limits.TryGetValue(o.Id, out var lim) ? lim : e1.D("PowerTransformerEnd.ratedS") ?? 0,
                InService = (o.B("Equipment.inService") ?? true) && Terminals(o).All(Connected),
                Connection = (e1.S("PowerTransformerEnd.connectionKind"), e2.S("PowerTransformerEnd.connectionKind")) switch
                {
                    ("Yn", "Yn") => WindingConnection.YNyn,
                    ("Yn", "D") => WindingConnection.YNd,
                    ("D", "Yn") => WindingConnection.Dyn,
                    ("Y", "Y") => WindingConnection.Yy,
                    ("D", "D") => WindingConnection.Dd,
                    ("Y", "D") => WindingConnection.Yd,
                    _ => WindingConnection.Dyn,
                },
            };
            if (e1.D("PowerTransformerEnd.r0") is double r0) br.R0 = r0 * toSide / zbTo;
            if (e1.D("PowerTransformerEnd.x0") is double x0) br.X0 = x0 * toSide / zbTo;
            _net.Branches.Add(br);
        }

        private void Machine(Obj o, List<(int, int)> slack, HashSet<int> regulating)
        {
            var ts = Terminals(o);
            if (ts.Count == 0 || BusOf(ts[0]) is not int busId) { _warnings.Add($"{o.Class} '{o.Id}' is not connected; skipped."); return; }
            var bus = _net.GetBus(busId);
            bool eni = o.Class == "ExternalNetworkInjection";
            var gu = g.Get(o.S("RotatingMachine.GeneratingUnit"));
            var rc = g.Get(o.S("RegulatingCondEq.RegulatingControl"));
            var gen = new Generator
            {
                Bus = busId, Name = o.S("IdentifiedObject.name"), InService = InService(o),
                P = -(o.D(eni ? "ExternalNetworkInjection.p" : "RotatingMachine.p") ?? -(gu?.D("GeneratingUnit.initialP") ?? 0)),
                Q = -(o.D(eni ? "ExternalNetworkInjection.q" : "RotatingMachine.q") ?? 0),
                Qmax = o.D(eni ? "ExternalNetworkInjection.maxQ" : "SynchronousMachine.maxQ") ?? 9999,
                Qmin = o.D(eni ? "ExternalNetworkInjection.minQ" : "SynchronousMachine.minQ") ?? -9999,
                Pmax = gu?.D("GeneratingUnit.maxOperatingP") ?? o.D("ExternalNetworkInjection.maxP") ?? 9999,
                Pmin = gu?.D("GeneratingUnit.minOperatingP") ?? o.D("ExternalNetworkInjection.minP") ?? 0,
                MBase = o.D("RotatingMachine.ratedS") is double s && s > 0 ? s : _net.BaseMva,
                Kind = eni ? GeneratorKind.Grid : gu?.Class switch
                {
                    "ThermalGeneratingUnit" => GeneratorKind.Thermal,
                    "HydroGeneratingUnit" => GeneratorKind.Hydro,
                    "NuclearGeneratingUnit" => GeneratorKind.Nuclear,
                    "WindGeneratingUnit" => GeneratorKind.Wind,
                    "SolarGeneratingUnit" => GeneratorKind.Solar,
                    _ => GeneratorKind.Other,
                },
            };
            if (o.D("SynchronousMachine.satDirectSyncX") is double xd) gen.Xd = xd;
            if (o.D("SynchronousMachine.satDirectTransX") is double xdp) gen.XdPrime = xdp;
            if (o.D("SynchronousMachine.satDirectSubtransX") is double xdpp) gen.XdSubtransient = xdpp;
            if (o.D("SynchronousMachine.x0") is double x0) gen.X0 = x0;
            if (o.D("SynchronousMachine.x2") is double x2) gen.X2 = x2;
            if (o.D("SynchronousMachine.r") is double ra) gen.Ra = ra;
            if (opt.UseExtensions)
            {
                if (Enum.TryParse<GeneratorKind>(gu?.S("GeneratingUnit.kind"), out var k)) gen.Kind = k;
                if (gu?.D("GeneratingUnit.costC1") is double c1)
                    gen.Cost = new PolynomialCost(gu.D("GeneratingUnit.costC2") ?? 0, c1, gu.D("GeneratingUnit.costC0") ?? 0);
                if (gu?.D("GeneratingUnit.rampRate") is double rr) gen.RampRate = rr;
                if (o.D("SynchronousMachine.inertia") is double h) gen.H = h;
                if (o.D("SynchronousMachine.damping") is double dmp) gen.D = dmp;
            }
            bool controls = (o.B("RegulatingCondEq.controlEnabled") ?? o.B("ExternalNetworkInjection.regulationStatus") ?? rc is not null)
                            && (rc?.B("RegulatingControl.enabled") ?? true);
            if (rc?.D("RegulatingControl.targetValue") is double target)
            {
                var rcBus = BusOf(g.Get(rc.S("RegulatingControl.Terminal"))) ?? busId;
                gen.Vset = target / _net.GetBus(rcBus).BaseKv;
            }
            else gen.Vset = bus.Vm;
            if (gen.InService && controls) regulating.Add(busId);
            int prio = o.I(eni ? "ExternalNetworkInjection.referencePriority" : "SynchronousMachine.referencePriority") ?? 0;
            if (gen.InService) slack.Add((busId, prio));
            _net.Generators.Add(gen);
        }

        private void LoadOf(Obj o)
        {
            var ts = Terminals(o);
            if (ts.Count == 0 || BusOf(ts[0]) is not int busId) { _warnings.Add($"{o.Class} '{o.Id}' is not connected; skipped."); return; }
            var l = new Load
            {
                Bus = busId, Name = o.S("IdentifiedObject.name"), InService = InService(o),
                P = o.D("EnergyConsumer.p") ?? o.D("EnergyConsumer.pfixed") ?? 0, Q = o.D("EnergyConsumer.q") ?? o.D("EnergyConsumer.qfixed") ?? 0,
                Category = opt.UseExtensions ? o.S("EnergyConsumer.category") : null,
            };
            if (g.Get(o.S("EnergyConsumer.LoadResponse")) is { } lrc && !(lrc.B("LoadResponseCharacteristic.exponentModel") ?? false))
            {
                double z = lrc.D("LoadResponseCharacteristic.pConstantImpedance") ?? 0, i = lrc.D("LoadResponseCharacteristic.pConstantCurrent") ?? 0;
                double p = lrc.D("LoadResponseCharacteristic.pConstantPower") ?? 1 - z - i;
                l.Model = new ZipModel(z, i, p);
            }
            _net.Loads.Add(l);
        }

        private void Shunt(Obj o)
        {
            var ts = Terminals(o);
            if (ts.Count == 0 || BusOf(ts[0]) is not int busId) { _warnings.Add($"{o.Class} '{o.Id}' is not connected; skipped."); return; }
            if (!InService(o)) return;
            var bus = _net.GetBus(busId);
            double sections = o.D("ShuntCompensator.sections") ?? o.D("ShuntCompensator.normalSections") ?? 1;
            double kv2 = bus.BaseKv * bus.BaseKv;
            bus.Bs += (o.D("LinearShuntCompensator.bPerSection") ?? 0) * sections * kv2;
            bus.Gs += (o.D("LinearShuntCompensator.gPerSection") ?? 0) * sections * kv2;
        }
    }
}
