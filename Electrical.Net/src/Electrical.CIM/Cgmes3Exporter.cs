using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Electrical.Core;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.CIM;

/// <summary>How buses are represented in an exported CGMES set.</summary>
public enum CgmesTopologyStyle
{
    /// <summary>One ConnectivityNode per bus in EQ, mapped to one TopologicalNode per bus in TP (default).</summary>
    ConnectivityNodes,
    /// <summary>Node-breaker: every branch end gets its own bay ConnectivityNode joined to the busbar node by a closed Breaker.</summary>
    NodeBreaker,
    /// <summary>Bus-branch: no ConnectivityNodes; terminals reference TopologicalNodes in TP only.</summary>
    BusBranch,
}

/// <summary>Options for <see cref="Cgmes3Exporter"/>.</summary>
public sealed record Cgmes3ExportOptions
{
    /// <summary>Bus representation. Default <see cref="CgmesTopologyStyle.ConnectivityNodes"/>.</summary>
    public CgmesTopologyStyle Topology { get; init; } = CgmesTopologyStyle.ConnectivityNodes;
    /// <summary>Write the <c>eln:</c> extension attributes (bus ids/types, base MVA, costs, inertia) for a lossless round trip. Default true.</summary>
    public bool IncludeExtensions { get; init; } = true;
    /// <summary>Write the SV document (SvVoltage from the result or the bus voltages stored in the network). Default true.</summary>
    public bool IncludeStateVariables { get; init; } = true;
    /// <summary>Model.scenarioTime (UTC). Default 2026-01-01T00:00:00Z for deterministic output.</summary>
    public DateTimeOffset ScenarioTime { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>Model.created (UTC); defaults to <see cref="ScenarioTime"/> so output is reproducible.</summary>
    public DateTimeOffset? Created { get; init; }
    /// <summary>Model.modelingAuthoritySet URI.</summary>
    public string ModelingAuthoritySet { get; init; } = "http://gravicode.studio/electrical-net";
    /// <summary>Model.version written in every header (also part of the model URN). Default 1.</summary>
    public int ModelVersion { get; init; } = 1;
    /// <summary>Stem for file names (<c>{BaseName}_EQ.xml</c>); defaults to the network name.</summary>
    public string? BaseName { get; init; }
    /// <summary>Indent the XML. Default true.</summary>
    public bool Indent { get; init; } = true;
}

/// <summary>
/// Exports a <see cref="PowerNetwork"/> as a CGMES 3.0 instance set (IEC 61970-600-1/-2 Ed.2, CIM100): separate EQ, TP, SSH and SV
/// RDF/XML documents with <c>md:FullModel</c> headers (profile, scenarioTime, created, modelingAuthoritySet, DependentOn chain
/// TP→EQ, SSH→EQ, SV→TP+SSH). All mRIDs are name-based UUID v5 derived from the network name and element indices, so the output is
/// deterministic. Units: impedances in Ω, susceptances in S, voltages in kV, powers in MW/Mvar (SSH/SV in load sign convention),
/// angles in degrees, ratings in MVA (ApparentPowerLimit).
/// </summary>
/// <remarks>
/// Mapping: bus → ConnectivityNode/TopologicalNode + VoltageLevel (buses joined by transformers share one Substation); line →
/// ACLineSegment (r, x, bch, gch, r0/x0/b0ch); transformer → PowerTransformer + two PowerTransformerEnds (impedance referred to end 1,
/// phaseAngleClock 0 — the positive-sequence model has no vector-group shift) with RatioTapChanger for off-nominal ratio and
/// PhaseTapChangerTabular for phase shift; generator → SynchronousMachine + (Thermal/Hydro/Nuclear/Wind/Solar)GeneratingUnit +
/// RegulatingControl, or ExternalNetworkInjection for <see cref="GeneratorKind.Grid"/>; load → EnergyConsumer (+ LoadResponseCharacteristic);
/// bus shunt → LinearShuntCompensator. SV: SvVoltage per TopologicalNode, and with a <see cref="PowerFlowResult"/> also SvPowerFlow per
/// terminal, SvStatus, SvTapStep and SvShuntCompensatorSections.
/// </remarks>
public static class Cgmes3Exporter
{
    private const CimProfile EQ = CimProfile.Equipment, TP = CimProfile.Topology, SSH = CimProfile.SteadyStateHypothesis, SV = CimProfile.StateVariables;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exports EQ, TP, SSH and (optionally) SV documents.</summary>
    /// <param name="network">Network to export.</param>
    /// <param name="result">Optional solved power flow of <paramref name="network"/>; it supplies SvVoltage, SvPowerFlow and generator set-points.</param>
    /// <param name="options">Export options.</param>
    public static CgmesDocumentSet Export(PowerNetwork network, PowerFlowResult? result = null, Cgmes3ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        options ??= new Cgmes3ExportOptions();
        var objects = new Builder(network, result, options).Build();
        var headers = Headers(network, options);
        var ns = new List<(string, string)> { ("cim", Cgmes3Namespaces.Cim), ("eu", Cgmes3Namespaces.Eu), ("md", Cgmes3Namespaces.Md) };
        if (options.IncludeExtensions) ns.Add(("eln", CimNamespaces.Eln));
        XDocument Doc(CimProfile p)
        {
            var sb = new Utf8StringWriter();
            using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = options.Indent, Encoding = new UTF8Encoding(false) }))
                CimXmlWriter.Write(w, objects, q => q == p, [headers[p]], aboutForForeign: true, ns);
            return XDocument.Parse(sb.ToString());
        }
        return new CgmesDocumentSet(Doc(EQ), Doc(TP), Doc(SSH), options.IncludeStateVariables ? Doc(SV) : null, options.BaseName ?? Safe(network.Name));
    }

    /// <summary>Exports and writes the documents as a zip archive.</summary>
    public static void WriteZip(PowerNetwork network, Stream stream, PowerFlowResult? result = null, Cgmes3ExportOptions? options = null) =>
        Export(network, result, options).WriteZip(stream);

    private static string Safe(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "network" : sb.ToString();
    }

    /// <summary>URN of the model header of a profile (deterministic).</summary>
    internal static string ModelUrn(PowerNetwork net, Cgmes3ExportOptions o, CimProfile p) =>
        "urn:uuid:" + CgmesIds.NameBasedUuid($"{net.Name}/model/{p}/{o.ModelVersion.ToString(Inv)}").ToString();

    private static Dictionary<CimProfile, CimObject> Headers(PowerNetwork net, Cgmes3ExportOptions o)
    {
        string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv);
        var d = new Dictionary<CimProfile, CimObject>();
        foreach (var (p, kind, deps) in new (CimProfile, CgmesProfileKind, CimProfile[])[]
                 {
                     (EQ, CgmesProfileKind.Equipment, []),
                     (TP, CgmesProfileKind.Topology, [EQ]),
                     (SSH, CgmesProfileKind.SteadyStateHypothesis, [EQ]),
                     (SV, CgmesProfileKind.StateVariables, [TP, SSH]),
                 })
        {
            var h = new CimObject("md:FullModel", ModelUrn(net, o, p), EQ);
            h.Lit("md:Model.scenarioTime", Time(o.ScenarioTime)).Lit("md:Model.created", Time(o.Created ?? o.ScenarioTime))
             .Lit("md:Model.description", $"{net.Name} {kind} — exported by {ElectricalInfo.Name} {ElectricalInfo.Version} ({ElectricalInfo.CreditEn})")
             .Lit("md:Model.version", o.ModelVersion).Lit("md:Model.modelingAuthoritySet", o.ModelingAuthoritySet)
             .Lit("md:Model.profile", Cgmes3Namespaces.ProfileUri(kind)!);
            foreach (var dep in deps) h.Ref("md:Model.DependentOn", ModelUrn(net, o, dep));
            if (p == EQ && o.IncludeExtensions)
                h.Lit("eln:Network.name", net.Name).Lit("eln:Network.baseMva", net.BaseMva).Lit("eln:Network.frequencyHz", net.FrequencyHz);
            d[p] = h;
        }
        return d;
    }

    private sealed class Builder(PowerNetwork net, PowerFlowResult? pf, Cgmes3ExportOptions o)
    {
        private readonly List<CimObject> _list = [];
        private readonly Dictionary<double, string> _bv = [];
        private readonly Dictionary<int, int> _substationOf = [];
        private readonly bool _ext = o.IncludeExtensions;
        private readonly bool _sv = o.IncludeStateVariables;
        private readonly bool _cn = o.Topology != CgmesTopologyStyle.BusBranch;
        private string? _patl;

        private string Id(string key) => "_" + CgmesIds.NameBasedUuid(net.Name + "/" + key).ToString();
        private static string Sid(int id) => id < 0 ? "m" + (-id).ToString(Inv) : id.ToString(Inv);

        /// <summary>Adds an IdentifiedObject (mRID + name in its home profile).</summary>
        private CimObject Io(string cls, string key, CimProfile home, string name)
        {
            string id = Id(key);
            var c = new CimObject(cls, id, home);
            c.Lit("cim:IdentifiedObject.mRID", id[1..]).Lit("cim:IdentifiedObject.name", name);
            _list.Add(c);
            return c;
        }

        /// <summary>Adds a non-IdentifiedObject (Sv* classes, table points).</summary>
        private CimObject Plain(string cls, string key, CimProfile home)
        {
            var c = new CimObject(cls, Id(key), home);
            _list.Add(c);
            return c;
        }

        private string Cn(int bus) => Id("CN/" + Sid(bus));
        private string Tn(int bus) => Id("TN/" + Sid(bus));
        private string Vl(int bus) => Id("VL/" + Sid(bus));
        private string Ss(int bus) => Id("SS/" + Sid(_substationOf[bus]));

        public List<CimObject> Build()
        {
            if (pf is not null && (pf.Buses.Count != net.Buses.Count || pf.Branches.Count != net.Branches.Count))
                throw new ArgumentException("The power-flow result does not belong to this network.", nameof(pf));
            Substations();
            Buses();
            for (int k = 0; k < net.Branches.Count; k++)
                if (IsTransformer(net.Branches[k])) Transformer(k); else Line(k);
            for (int i = 0; i < net.Generators.Count; i++)
                if (net.Generators[i].Kind == GeneratorKind.Grid) Injection(i); else Machine(i);
            for (int i = 0; i < net.Loads.Count; i++) Consumer(i);
            return _list;
        }

        /// <summary>A branch between different nominal voltages is written as a transformer even when its ratio is nominal (CGMES forbids such lines).</summary>
        private bool IsTransformer(Branch br) => br.IsTransformer || net.GetBus(br.From).BaseKv != net.GetBus(br.To).BaseKv;

        private void Substations()
        {
            // Buses joined by transformers belong to one substation (CGMES requires a PowerTransformer inside a single Substation).
            var parent = net.Buses.ToDictionary(b => b.Id, b => b.Id);
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (var br in net.Branches.Where(IsTransformer))
            {
                int a = Find(br.From), b = Find(br.To);
                if (a == b) continue;
                if (net.IndexOf(a) < net.IndexOf(b)) parent[b] = a; else parent[a] = b;
            }
            foreach (var b in net.Buses) _substationOf[b.Id] = Find(b.Id);
        }

        private void Buses()
        {
            foreach (var bus in net.Buses)
            {
                if (_bv.ContainsKey(bus.BaseKv)) continue;
                var bv = Io("cim:BaseVoltage", "BV/" + bus.BaseKv.ToString("R", Inv), EQ, bus.BaseKv.ToString("0.###", Inv) + " kV")
                    .Lit("cim:BaseVoltage.nominalVoltage", bus.BaseKv);
                _bv[bus.BaseKv] = bv.Id;
            }
            var gr = Io("cim:GeographicalRegion", "GR", EQ, net.Name);
            var sgr = Io("cim:SubGeographicalRegion", "SGR", EQ, net.Name).Ref("cim:SubGeographicalRegion.Region", gr.Id);
            foreach (var bus in net.Buses.Where(b => _substationOf[b.Id] == b.Id))
                Io("cim:Substation", "SS/" + Sid(bus.Id), EQ, bus.Name ?? $"Substation {bus.Id}").Ref("cim:Substation.Region", sgr.Id);

            foreach (var bus in net.Buses)
            {
                string s = Sid(bus.Id), name = bus.Name ?? $"Bus {bus.Id}";
                Io("cim:VoltageLevel", "VL/" + s, EQ, name)
                    .Ref("cim:VoltageLevel.Substation", Ss(bus.Id)).Ref("cim:VoltageLevel.BaseVoltage", _bv[bus.BaseKv])
                    .Lit("cim:VoltageLevel.highVoltageLimit", bus.Vmax * bus.BaseKv).Lit("cim:VoltageLevel.lowVoltageLimit", bus.Vmin * bus.BaseKv);
                var tn = Io("cim:TopologicalNode", "TN/" + s, TP, name)
                    .Ref("cim:TopologicalNode.BaseVoltage", _bv[bus.BaseKv]).Ref("cim:TopologicalNode.ConnectivityNodeContainer", Vl(bus.Id));
                CimObject holder = tn;
                string prefix = "eln:TopologicalNode.";
                if (_cn)
                {
                    holder = Io("cim:ConnectivityNode", "CN/" + s, EQ, name)
                        .Ref("cim:ConnectivityNode.ConnectivityNodeContainer", Vl(bus.Id)).Ref("cim:ConnectivityNode.TopologicalNode", tn.Id, TP);
                    prefix = "eln:ConnectivityNode.";
                }
                if (_ext)
                    holder.Lit(prefix + "busId", bus.Id).Lit(prefix + "busType", bus.Type.ToString()).Lit(prefix + "baseKv", bus.BaseKv)
                          .Lit(prefix + "area", bus.Area).Lit(prefix + "zone", bus.Zone).Lit(prefix + "vmax", bus.Vmax).Lit(prefix + "vmin", bus.Vmin);
                if (_sv)
                {
                    var (vm, va) = pf is null ? (bus.Vm, bus.VaDeg) : (pf.Bus(bus.Id).Vm, pf.Bus(bus.Id).VaDeg);
                    Plain("cim:SvVoltage", "SVV/" + s, SV).Ref("cim:SvVoltage.TopologicalNode", tn.Id)
                        .Lit("cim:SvVoltage.v", vm * bus.BaseKv).Lit("cim:SvVoltage.angle", va);
                }
            }
            foreach (var bus in net.Buses.Where(b => b.Gs != 0 || b.Bs != 0)) Shunt(bus);
        }

        private string Terminal(string eqKey, string eqId, int seq, int bus, bool connected, string? node = null)
        {
            var t = Io("cim:Terminal", eqKey + "/T" + seq.ToString(Inv), EQ, $"{eqKey} T{seq}")
                .Ref("cim:Terminal.ConductingEquipment", eqId).Lit("cim:ACDCTerminal.sequenceNumber", seq);
            if (_cn) t.Ref("cim:Terminal.ConnectivityNode", node ?? Cn(bus));
            t.Ref("cim:Terminal.TopologicalNode", Tn(bus), TP).Lit("cim:ACDCTerminal.connected", connected, SSH);
            return t.Id;
        }

        /// <summary>Branch terminal; in node-breaker style through a bay node and a closed breaker.</summary>
        private string BranchTerminal(string eqKey, string eqId, int seq, int bus, bool connected)
        {
            if (o.Topology != CgmesTopologyStyle.NodeBreaker) return Terminal(eqKey, eqId, seq, bus, connected);
            string bayKey = $"{eqKey}/BAY{seq}";
            var bay = Io("cim:ConnectivityNode", "CN/" + bayKey, EQ, $"{eqKey} bay {seq}")
                .Ref("cim:ConnectivityNode.ConnectivityNodeContainer", Vl(bus)).Ref("cim:ConnectivityNode.TopologicalNode", Tn(bus), TP);
            string brkKey = $"{eqKey}/BRK{seq}";
            var brk = Io("cim:Breaker", brkKey, EQ, $"{eqKey} CB{seq}").Ref("cim:Equipment.EquipmentContainer", Vl(bus))
                .Lit("cim:Switch.normalOpen", false).Lit("cim:Switch.retained", false)
                .Lit("cim:Switch.open", false, SSH).Lit("cim:Switch.locked", false, SSH);
            Terminal(brkKey, brk.Id, 1, bus, true);
            Terminal(brkKey, brk.Id, 2, bus, true, bay.Id);
            return Terminal(eqKey, eqId, seq, bus, connected, bay.Id);
        }

        private void Status(string key, string eqId, bool inService)
        {
            if (_sv && pf is not null)
                Plain("cim:SvStatus", "SVS/" + key, SV).Ref("cim:SvStatus.ConductingEquipment", eqId).Lit("cim:SvStatus.inService", inService);
        }

        private void Flow(string terminalId, double p, double q)
        {
            if (_sv && pf is not null)
                Plain("cim:SvPowerFlow", "SVPF/" + terminalId, SV).Ref("cim:SvPowerFlow.Terminal", terminalId).Lit("cim:SvPowerFlow.p", p).Lit("cim:SvPowerFlow.q", q);
        }

        private void Limit(string key, string terminalId, double rateMva)
        {
            if (rateMva <= 0) return;
            _patl ??= Io("cim:OperationalLimitType", "OLT/PATL", EQ, "PATL")
                .Lit("cim:OperationalLimitType.isInfiniteDuration", true)
                .Enum("cim:OperationalLimitType.direction", "OperationalLimitDirectionKind.absoluteValue")
                .Enum("eu:OperationalLimitType.kind", "eu:LimitKind.patl").Id;
            var set = Io("cim:OperationalLimitSet", key + "/OLS", EQ, "Ratings").Ref("cim:OperationalLimitSet.Terminal", terminalId);
            Io("cim:ApparentPowerLimit", key + "/APL", EQ, "RateA").Ref("cim:OperationalLimit.OperationalLimitSet", set.Id)
                .Ref("cim:OperationalLimit.OperationalLimitType", _patl)
                .Lit("cim:ApparentPowerLimit.normalValue", rateMva).Lit("cim:ApparentPowerLimit.value", rateMva, SSH);
        }

        private void Line(int k)
        {
            var br = net.Branches[k];
            var from = net.GetBus(br.From);
            string key = "LN/" + (k + 1).ToString(Inv);
            double zb = from.BaseKv * from.BaseKv / net.BaseMva;
            var l = Io("cim:ACLineSegment", key, EQ, br.Name ?? $"{br.From}-{br.To}")
                .Ref("cim:ConductingEquipment.BaseVoltage", _bv[from.BaseKv])
                .Lit("cim:ACLineSegment.r", br.R * zb).Lit("cim:ACLineSegment.x", br.X * zb)
                .Lit("cim:ACLineSegment.bch", br.B / zb).Lit("cim:ACLineSegment.gch", 0.0).Lit("cim:Conductor.length", br.LengthKm);
            if (!double.IsNaN(br.R0)) l.Lit("cim:ACLineSegment.r0", br.R0 * zb);
            if (!double.IsNaN(br.X0)) l.Lit("cim:ACLineSegment.x0", br.X0 * zb);
            if (!double.IsNaN(br.B0)) l.Lit("cim:ACLineSegment.b0ch", br.B0 / zb).Lit("cim:ACLineSegment.g0ch", 0.0);
            if (_ext) l.Lit("eln:ACLineSegment.kind", br.Kind.ToString());
            string t1 = BranchTerminal(key, l.Id, 1, br.From, br.InService);
            string t2 = BranchTerminal(key, l.Id, 2, br.To, br.InService);
            Limit(key, t1, br.RateA);
            BranchState(k, key, l.Id, t1, t2);
        }

        private void BranchState(int k, string key, string eqId, string t1, string t2)
        {
            if (pf is null) return;
            var r = pf.Branches[k];
            Status(key, eqId, net.Branches[k].InService);
            Flow(t1, r.PFrom, r.QFrom);
            Flow(t2, r.PTo, r.QTo);
        }

        private void Transformer(int k)
        {
            var br = net.Branches[k];
            var from = net.GetBus(br.From);
            var to = net.GetBus(br.To);
            string key = "TR/" + (k + 1).ToString(Inv);
            double u1 = from.BaseKv, u2 = to.BaseKv, zb = u1 * u1 / net.BaseMva;
            double ratedS = br.RateA > 0 ? br.RateA : net.BaseMva;
            var (c1, c2) = Connection(br.Connection);
            var tr = Io("cim:PowerTransformer", key, EQ, br.Name ?? $"{br.From}-{br.To}").Ref("cim:Equipment.EquipmentContainer", Ss(br.From));
            if (_ext) tr.Lit("eln:PowerTransformer.rateA", br.RateA).Lit("eln:PowerTransformer.kind", br.Kind.ToString());
            string t1 = BranchTerminal(key, tr.Id, 1, br.From, br.InService);
            string t2 = BranchTerminal(key, tr.Id, 2, br.To, br.InService);
            var e1 = Io("cim:PowerTransformerEnd", key + "/E1", EQ, $"{key} end 1")
                .Ref("cim:PowerTransformerEnd.PowerTransformer", tr.Id).Lit("cim:TransformerEnd.endNumber", 1)
                .Ref("cim:TransformerEnd.Terminal", t1).Ref("cim:TransformerEnd.BaseVoltage", _bv[from.BaseKv])
                .Lit("cim:PowerTransformerEnd.ratedU", u1).Lit("cim:PowerTransformerEnd.ratedS", ratedS)
                .Lit("cim:PowerTransformerEnd.r", br.R * zb).Lit("cim:PowerTransformerEnd.x", br.X * zb)
                .Lit("cim:PowerTransformerEnd.b", br.B / zb).Lit("cim:PowerTransformerEnd.g", 0.0)
                .Enum("cim:PowerTransformerEnd.connectionKind", "WindingConnection." + c1).Lit("cim:PowerTransformerEnd.phaseAngleClock", 0);
            if (!double.IsNaN(br.R0)) e1.Lit("cim:PowerTransformerEnd.r0", br.R0 * zb);
            if (!double.IsNaN(br.X0)) e1.Lit("cim:PowerTransformerEnd.x0", br.X0 * zb);
            Io("cim:PowerTransformerEnd", key + "/E2", EQ, $"{key} end 2")
                .Ref("cim:PowerTransformerEnd.PowerTransformer", tr.Id).Lit("cim:TransformerEnd.endNumber", 2)
                .Ref("cim:TransformerEnd.Terminal", t2).Ref("cim:TransformerEnd.BaseVoltage", _bv[to.BaseKv])
                .Lit("cim:PowerTransformerEnd.ratedU", u2).Lit("cim:PowerTransformerEnd.ratedS", ratedS)
                .Lit("cim:PowerTransformerEnd.r", 0.0).Lit("cim:PowerTransformerEnd.x", 0.0)
                .Lit("cim:PowerTransformerEnd.b", 0.0).Lit("cim:PowerTransformerEnd.g", 0.0)
                .Enum("cim:PowerTransformerEnd.connectionKind", "WindingConnection." + c2).Lit("cim:PowerTransformerEnd.phaseAngleClock", 0);
            if (br.EffectiveTap != 1)
            {
                // Off-nominal ratio as a one-step tap changer on end 1: ratio = 1 + (step − neutral) · increment / 100.
                var rtc = Io("cim:RatioTapChanger", key + "/RTC", EQ, "RTC").Ref("cim:RatioTapChanger.TransformerEnd", e1.Id)
                    .Lit("cim:TapChanger.lowStep", -1).Lit("cim:TapChanger.highStep", 1).Lit("cim:TapChanger.neutralStep", 0)
                    .Lit("cim:TapChanger.normalStep", 1).Lit("cim:TapChanger.neutralU", u1).Lit("cim:TapChanger.ltcFlag", false)
                    .Lit("cim:RatioTapChanger.stepVoltageIncrement", (br.EffectiveTap - 1) * 100)
                    .Lit("cim:TapChanger.step", 1.0, SSH).Lit("cim:TapChanger.controlEnabled", false, SSH);
                if (_sv && pf is not null)
                    Plain("cim:SvTapStep", "SVT/" + key + "/RTC", SV).Ref("cim:SvTapStep.TapChanger", rtc.Id).Lit("cim:SvTapStep.position", 1.0);
            }
            if (br.ShiftDeg != 0)
            {
                var table = Io("cim:PhaseTapChangerTable", key + "/PTT", EQ, "PST table");
                var ptc = Io("cim:PhaseTapChangerTabular", key + "/PTC", EQ, "PTC").Ref("cim:PhaseTapChanger.TransformerEnd", e1.Id)
                    .Ref("cim:PhaseTapChangerTabular.PhaseTapChangerTable", table.Id)
                    .Lit("cim:TapChanger.lowStep", 0).Lit("cim:TapChanger.highStep", 1).Lit("cim:TapChanger.neutralStep", 0)
                    .Lit("cim:TapChanger.normalStep", 1).Lit("cim:TapChanger.neutralU", u1).Lit("cim:TapChanger.ltcFlag", false)
                    .Lit("cim:TapChanger.step", 1.0, SSH).Lit("cim:TapChanger.controlEnabled", false, SSH);
                for (int step = 0; step <= 1; step++)
                    Plain("cim:PhaseTapChangerTablePoint", $"{key}/PTT/{step}", EQ).Ref("cim:PhaseTapChangerTablePoint.PhaseTapChangerTable", table.Id)
                        .Lit("cim:TapChangerTablePoint.step", step).Lit("cim:PhaseTapChangerTablePoint.angle", step * br.ShiftDeg)
                        .Lit("cim:TapChangerTablePoint.ratio", 1.0).Lit("cim:TapChangerTablePoint.r", 0.0).Lit("cim:TapChangerTablePoint.x", 0.0)
                        .Lit("cim:TapChangerTablePoint.b", 0.0).Lit("cim:TapChangerTablePoint.g", 0.0);
                if (_sv && pf is not null)
                    Plain("cim:SvTapStep", "SVT/" + key + "/PTC", SV).Ref("cim:SvTapStep.TapChanger", ptc.Id).Lit("cim:SvTapStep.position", 1.0);
            }
            Limit(key, t1, br.RateA);
            BranchState(k, key, tr.Id, t1, t2);
        }

        private (double P, double Q) GenOutput(int i)
        {
            var g = net.Generators[i];
            if (pf is null) return (g.P, g.Q);
            var r = pf.Generators.FirstOrDefault(x => x.Index == i);
            return r is null ? (0, 0) : (r.P, r.Q);
        }

        private string RegulatingControl(string key, string terminalKey, Generator g, Bus bus)
        {
            // The terminal id is deterministic, so the control can reference it before the terminal is written.
            return Io("cim:RegulatingControl", key + "/RC", EQ, $"V control {key}")
                .Ref("cim:RegulatingControl.Terminal", Id(terminalKey)).Enum("cim:RegulatingControl.mode", "RegulatingControlModeKind.voltage")
                .Lit("cim:RegulatingControl.discrete", false, SSH).Lit("cim:RegulatingControl.enabled", true, SSH)
                .Lit("cim:RegulatingControl.targetDeadband", 0.0, SSH).Lit("cim:RegulatingControl.targetValue", g.Vset * bus.BaseKv, SSH)
                .Enum("cim:RegulatingControl.targetValueUnitMultiplier", "UnitMultiplier.k", SSH).Id;
        }

        private void Machine(int i)
        {
            var g = net.Generators[i];
            var bus = net.GetBus(g.Bus);
            string key = "SM/" + (i + 1).ToString(Inv);
            var (p, q) = GenOutput(i);
            string guClass = g.Kind switch
            {
                GeneratorKind.Thermal or GeneratorKind.Gas or GeneratorKind.Diesel => "cim:ThermalGeneratingUnit",
                GeneratorKind.Hydro => "cim:HydroGeneratingUnit",
                GeneratorKind.Nuclear => "cim:NuclearGeneratingUnit",
                GeneratorKind.Wind => "cim:WindGeneratingUnit",
                GeneratorKind.Solar => "cim:SolarGeneratingUnit",
                _ => "cim:GeneratingUnit",
            };
            var gu = Io(guClass, key + "/GU", EQ, g.Name ?? $"GU {i + 1}").Ref("cim:Equipment.EquipmentContainer", Ss(g.Bus))
                .Lit("cim:GeneratingUnit.maxOperatingP", g.Pmax).Lit("cim:GeneratingUnit.minOperatingP", g.Pmin)
                .Lit("cim:GeneratingUnit.initialP", g.P).Lit("cim:GeneratingUnit.normalPF", 1.0, SSH);
            if (g.Kind == GeneratorKind.Hydro) gu.Enum("cim:HydroGeneratingUnit.energyConversionCapability", "HydroEnergyConversionKind.generator");
            if (g.Kind == GeneratorKind.Wind) gu.Enum("cim:WindGeneratingUnit.windGenUnitType", "WindGenUnitKind.onshore");
            if (_ext)
            {
                gu.Lit("eln:GeneratingUnit.kind", g.Kind.ToString()).Lit("eln:GeneratingUnit.costC2", g.Cost.C2)
                  .Lit("eln:GeneratingUnit.costC1", g.Cost.C1).Lit("eln:GeneratingUnit.costC0", g.Cost.C0);
                if (!double.IsPositiveInfinity(g.RampRate)) gu.Lit("eln:GeneratingUnit.rampRate", g.RampRate);
            }
            string rc = RegulatingControl(key, key + "/T1", g, bus);
            var sm = Io("cim:SynchronousMachine", key, EQ, g.Name ?? $"SM {i + 1}")
                .Ref("cim:Equipment.EquipmentContainer", Vl(g.Bus)).Ref("cim:RotatingMachine.GeneratingUnit", gu.Id)
                .Ref("cim:RegulatingCondEq.RegulatingControl", rc)
                .Lit("cim:RotatingMachine.ratedS", g.MBase).Lit("cim:RotatingMachine.ratedU", bus.BaseKv)
                .Lit("cim:SynchronousMachine.maxQ", g.Qmax).Lit("cim:SynchronousMachine.minQ", g.Qmin)
                .Enum("cim:SynchronousMachine.type", g.Kind == GeneratorKind.Other && g.Pmax <= 0 ? "SynchronousMachineKind.condenser" : "SynchronousMachineKind.generator")
                .Lit("cim:SynchronousMachine.satDirectSyncX", g.Xd).Lit("cim:SynchronousMachine.satDirectTransX", g.XdPrime)
                .Lit("cim:SynchronousMachine.satDirectSubtransX", g.XdSubtransient)
                .Lit("cim:SynchronousMachine.x0", g.X0).Lit("cim:SynchronousMachine.x2", g.X2).Lit("cim:SynchronousMachine.r", g.Ra)
                .Lit("cim:RotatingMachine.p", -p, SSH).Lit("cim:RotatingMachine.q", -q, SSH)
                .Lit("cim:RegulatingCondEq.controlEnabled", true, SSH)
                .Enum("cim:SynchronousMachine.operatingMode", g.Kind == GeneratorKind.Other && g.Pmax <= 0 ? "SynchronousMachineOperatingMode.condenser" : "SynchronousMachineOperatingMode.generator", SSH)
                .Lit("cim:SynchronousMachine.referencePriority", bus.Type == BusType.Slack ? 1 : 0, SSH);
            if (_ext) sm.Lit("eln:SynchronousMachine.inertia", g.H).Lit("eln:SynchronousMachine.damping", g.D);
            string t = Terminal(key, sm.Id, 1, g.Bus, g.InService);
            Status(key, sm.Id, g.InService);
            Flow(t, -p, -q);
        }

        private void Injection(int i)
        {
            var g = net.Generators[i];
            var bus = net.GetBus(g.Bus);
            string key = "ENI/" + (i + 1).ToString(Inv);
            var (p, q) = GenOutput(i);
            string rc = RegulatingControl(key, key + "/T1", g, bus);
            var eni = Io("cim:ExternalNetworkInjection", key, EQ, g.Name ?? $"Grid {i + 1}")
                .Ref("cim:Equipment.EquipmentContainer", Vl(g.Bus)).Ref("cim:RegulatingCondEq.RegulatingControl", rc)
                .Lit("cim:ExternalNetworkInjection.governorSCD", 0.0)
                .Lit("cim:ExternalNetworkInjection.maxP", g.Pmax).Lit("cim:ExternalNetworkInjection.minP", g.Pmin)
                .Lit("cim:ExternalNetworkInjection.maxQ", g.Qmax).Lit("cim:ExternalNetworkInjection.minQ", g.Qmin)
                .Lit("cim:ExternalNetworkInjection.p", -p, SSH).Lit("cim:ExternalNetworkInjection.q", -q, SSH)
                .Lit("cim:ExternalNetworkInjection.referencePriority", bus.Type == BusType.Slack ? 1 : 0, SSH)
                .Lit("cim:ExternalNetworkInjection.regulationStatus", true, SSH).Lit("cim:RegulatingCondEq.controlEnabled", true, SSH);
            if (_ext) eni.Lit("eln:ExternalNetworkInjection.inertia", g.H).Lit("eln:ExternalNetworkInjection.damping", g.D)
                         .Lit("eln:ExternalNetworkInjection.costC2", g.Cost.C2).Lit("eln:ExternalNetworkInjection.costC1", g.Cost.C1)
                         .Lit("eln:ExternalNetworkInjection.costC0", g.Cost.C0);
            string t = Terminal(key, eni.Id, 1, g.Bus, g.InService);
            Status(key, eni.Id, g.InService);
            Flow(t, -p, -q);
        }

        private void Consumer(int i)
        {
            var l = net.Loads[i];
            string key = "EC/" + (i + 1).ToString(Inv);
            var e = Io("cim:EnergyConsumer", key, EQ, l.Name ?? $"Load {i + 1}").Ref("cim:Equipment.EquipmentContainer", Vl(l.Bus))
                .Lit("cim:EnergyConsumer.p", l.P, SSH).Lit("cim:EnergyConsumer.q", l.Q, SSH);
            if (l.Category is not null && _ext) e.Lit("eln:EnergyConsumer.category", l.Category);
            if (l.Model != ZipModel.ConstantPower)
            {
                var lrc = Io("cim:LoadResponseCharacteristic", key + "/LRC", EQ, "ZIP")
                    .Lit("cim:LoadResponseCharacteristic.exponentModel", false)
                    .Lit("cim:LoadResponseCharacteristic.pConstantImpedance", l.Model.Z).Lit("cim:LoadResponseCharacteristic.pConstantCurrent", l.Model.I)
                    .Lit("cim:LoadResponseCharacteristic.pConstantPower", l.Model.P)
                    .Lit("cim:LoadResponseCharacteristic.qConstantImpedance", l.Model.Z).Lit("cim:LoadResponseCharacteristic.qConstantCurrent", l.Model.I)
                    .Lit("cim:LoadResponseCharacteristic.qConstantPower", l.Model.P);
                e.Ref("cim:EnergyConsumer.LoadResponse", lrc.Id);
            }
            string t = Terminal(key, e.Id, 1, l.Bus, l.InService);
            if (pf is not null)
            {
                double f = l.InService ? l.Model.Factor(pf.Bus(l.Bus).Vm) : 0;
                Status(key, e.Id, l.InService);
                Flow(t, l.P * f, l.Q * f);
            }
        }

        private void Shunt(Bus bus)
        {
            string key = "SH/" + Sid(bus.Id);
            double kv2 = bus.BaseKv * bus.BaseKv;
            var sh = Io("cim:LinearShuntCompensator", key, EQ, $"Shunt {bus.Id}").Ref("cim:Equipment.EquipmentContainer", Vl(bus.Id))
                .Lit("cim:ShuntCompensator.nomU", bus.BaseKv).Lit("cim:ShuntCompensator.maximumSections", 1).Lit("cim:ShuntCompensator.normalSections", 1)
                .Lit("cim:LinearShuntCompensator.bPerSection", bus.Bs / kv2).Lit("cim:LinearShuntCompensator.gPerSection", bus.Gs / kv2)
                .Lit("cim:ShuntCompensator.sections", 1.0, SSH).Lit("cim:RegulatingCondEq.controlEnabled", false, SSH);
            string t = Terminal(key, sh.Id, 1, bus.Id, true);
            if (pf is not null && _sv)
            {
                double v2 = pf.Bus(bus.Id).Vm * pf.Bus(bus.Id).Vm;
                Plain("cim:SvShuntCompensatorSections", "SVSC/" + key, SV).Ref("cim:SvShuntCompensatorSections.ShuntCompensator", sh.Id)
                    .Lit("cim:SvShuntCompensatorSections.sections", 1.0);
                Status(key, sh.Id, true);
                Flow(t, bus.Gs * v2, -bus.Bs * v2); // absorbed power, load sign convention
            }
        }

        private static (string, string) Connection(WindingConnection c) => c switch
        {
            WindingConnection.YNyn => ("Yn", "Yn"),
            WindingConnection.YNd => ("Yn", "D"),
            WindingConnection.Dyn => ("D", "Yn"),
            WindingConnection.Yy => ("Y", "Y"),
            WindingConnection.Dd => ("D", "D"),
            _ => ("Y", "D"),
        };
    }
}
