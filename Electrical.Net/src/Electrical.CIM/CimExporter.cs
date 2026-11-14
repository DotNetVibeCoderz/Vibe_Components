using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Electrical.Core;
using Electrical.Core.Network;

namespace Electrical.CIM;

/// <summary>Options for <see cref="CimExporter"/>.</summary>
public sealed record CimExportOptions
{
    /// <summary>Write Substation/VoltageLevel containers (one per bus) and bus locations (Location/PositionPoint). Default true.</summary>
    public bool IncludeContainers { get; init; } = true;
    /// <summary>Write the <c>eln:</c> extension attributes (bus ids/types, base MVA, area/zone, costs, inertia) that make a lossless round trip possible. Default true.</summary>
    public bool IncludeExtensions { get; init; } = true;
    /// <summary>Write SvVoltage objects with the bus voltages stored in the network. Default true.</summary>
    public bool IncludeStateVariables { get; init; } = true;
    /// <summary>Scenario time written in the model header (UTC). Default 2026-01-01T00:00:00Z for deterministic output.</summary>
    public DateTimeOffset ScenarioTime { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>Indent the XML output. Default true.</summary>
    public bool Indent { get; init; } = true;
}

/// <summary>The four CGMES instance files produced by <see cref="CimExporter.ExportProfiles"/>.</summary>
public sealed record CimProfiles(XDocument Equipment, XDocument Topology, XDocument SteadyStateHypothesis, XDocument StateVariables)
{
    /// <summary>All documents (EQ, TP, SSH, SV), e.g. to pass to <see cref="CimImporter.Import(XDocument[])"/>.</summary>
    public XDocument[] All => [Equipment, Topology, SteadyStateHypothesis, StateVariables];
}

/// <summary>
/// Exports a <see cref="PowerNetwork"/> as IEC 61970-301 CIM (CGMES 2.4.15 style) RDF/XML. Mapping:
/// bus → ConnectivityNode (+ TopologicalNode, VoltageLevel, Substation, BaseVoltage); line → ACLineSegment (r, x [Ω], bch [S], length [km]);
/// transformer → PowerTransformer + two PowerTransformerEnds (r, x [Ω] and b [S] referred to end 1, ratedU [kV], ratedS [MVA]) with
/// RatioTapChanger / PhaseTapChangerLinear for off-nominal ratio and phase shift; generator → SynchronousMachine + GeneratingUnit +
/// RegulatingControl (SSH p/q in load sign convention, i.e. negative for generation); load → EnergyConsumer (+ LoadResponseCharacteristic for ZIP);
/// bus shunt → LinearShuntCompensator. Ratings are written as OperationalLimitSet/ApparentPowerLimit [MVA].
/// </summary>
public static class CimExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exports a single merged RDF/XML document containing EQ, TP, SSH and SV content.</summary>
    public static XDocument Export(PowerNetwork net, CimExportOptions? options = null) => XDocument.Parse(ExportString(net, options));

    /// <summary>Exports a single merged RDF/XML document as a string.</summary>
    public static string ExportString(PowerNetwork net, CimExportOptions? options = null)
    {
        options ??= new CimExportOptions();
        var objects = Build(net, options);
        var sb = new Utf8StringWriter();
        using (var w = XmlWriter.Create(sb, Settings(options)))
            CimXmlWriter.Write(w, objects, _ => true, [Header(net, options, "merged", "http://entsoe.eu/CIM/EquipmentCore/3/1")], aboutForForeign: false);
        return sb.ToString();
    }

    /// <summary>Writes the merged document to a stream (UTF-8).</summary>
    public static void Write(PowerNetwork net, Stream stream, CimExportOptions? options = null)
    {
        options ??= new CimExportOptions();
        var objects = Build(net, options);
        using var w = XmlWriter.Create(stream, Settings(options));
        CimXmlWriter.Write(w, objects, _ => true, [Header(net, options, "merged", "http://entsoe.eu/CIM/EquipmentCore/3/1")], aboutForForeign: false);
    }

    /// <summary>Exports separate EQ, TP, SSH and SV documents (objects declared with rdf:ID in their home profile, referenced with rdf:about elsewhere).</summary>
    public static CimProfiles ExportProfiles(PowerNetwork net, CimExportOptions? options = null)
    {
        options ??= new CimExportOptions();
        var objects = Build(net, options);
        XDocument Doc(CimProfile p, string tag, string uri)
        {
            var sb = new Utf8StringWriter();
            using (var w = XmlWriter.Create(sb, Settings(options)))
                CimXmlWriter.Write(w, objects, q => q == p, [Header(net, options, tag, uri)], aboutForForeign: true);
            return XDocument.Parse(sb.ToString());
        }
        return new CimProfiles(
            Doc(CimProfile.Equipment, "EQ", "http://entsoe.eu/CIM/EquipmentCore/3/1"),
            Doc(CimProfile.Topology, "TP", "http://entsoe.eu/CIM/Topology/4/1"),
            Doc(CimProfile.SteadyStateHypothesis, "SSH", "http://entsoe.eu/CIM/SteadyStateHypothesis/1/1"),
            Doc(CimProfile.StateVariables, "SV", "http://entsoe.eu/CIM/StateVariables/4/1"));
    }

    private static XmlWriterSettings Settings(CimExportOptions o) => new() { Indent = o.Indent, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };

    private static CimObject Header(PowerNetwork net, CimExportOptions o, string tag, string profileUri)
    {
        var h = new CimObject("md:FullModel", "urn:uuid:" + DeterministicUuid(net.Name + "|" + tag), CimProfile.Equipment);
        h.Lit("md:Model.scenarioTime", o.ScenarioTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv));
        h.Lit("md:Model.created", o.ScenarioTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv));
        h.Lit("md:Model.description", $"{net.Name} — exported by {ElectricalInfo.Name} {ElectricalInfo.Version} ({ElectricalInfo.CreditEn})");
        h.Lit("md:Model.modelingAuthoritySet", "http://gravicode.studio/electrical-net");
        h.Lit("md:Model.profile", profileUri);
        h.Lit("md:Model.version", 1);
        if (o.IncludeExtensions)
        {
            h.Lit("eln:Network.name", net.Name);
            h.Lit("eln:Network.baseMva", net.BaseMva);
            h.Lit("eln:Network.frequencyHz", net.FrequencyHz);
        }
        return h;
    }

    private static string DeterministicUuid(string s)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(s), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true).ToString();
    }

    private static string Sid(int id) => id < 0 ? "m" + (-id).ToString(Inv) : id.ToString(Inv);

    internal static List<CimObject> Build(PowerNetwork net, CimExportOptions o)
    {
        const CimProfile EQ = CimProfile.Equipment, TP = CimProfile.Topology, SSH = CimProfile.SteadyStateHypothesis, SV = CimProfile.StateVariables;
        var list = new List<CimObject>();
        CimObject Add(string cls, string id, CimProfile home) { var c = new CimObject(cls, id, home); list.Add(c); return c; }
        bool ext = o.IncludeExtensions;
        double sBase = net.BaseMva;

        // Base voltages (one per distinct nominal kV, in order of first appearance).
        var bvIds = new Dictionary<double, string>();
        foreach (var bus in net.Buses)
        {
            if (bvIds.ContainsKey(bus.BaseKv)) continue;
            string id = "_BV" + (bvIds.Count + 1).ToString(Inv);
            bvIds[bus.BaseKv] = id;
            Add("cim:BaseVoltage", id, EQ).Lit("cim:IdentifiedObject.name", bus.BaseKv.ToString("0.###", Inv) + " kV").Lit("cim:BaseVoltage.nominalVoltage", bus.BaseKv);
        }

        // Buses.
        string? region = null;
        if (o.IncludeContainers)
        {
            region = "_GR1";
            Add("cim:GeographicalRegion", region, EQ).Lit("cim:IdentifiedObject.name", net.Name);
            Add("cim:SubGeographicalRegion", "_SGR1", EQ).Lit("cim:IdentifiedObject.name", net.Name).Ref("cim:SubGeographicalRegion.Region", region);
        }
        foreach (var bus in net.Buses)
        {
            string s = Sid(bus.Id), name = bus.Name ?? $"Bus {bus.Id}";
            string cn = "_CN" + s, tn = "_TN" + s;
            if (o.IncludeContainers)
            {
                var ss = Add("cim:Substation", "_SS" + s, EQ).Lit("cim:IdentifiedObject.name", name).Ref("cim:Substation.Region", "_SGR1");
                if (bus.Location is GeoPoint g)
                {
                    ss.Ref("cim:PowerSystemResource.Location", "_LOC" + s);
                    Add("cim:Location", "_LOC" + s, EQ).Lit("cim:IdentifiedObject.name", name).Ref("cim:Location.CoordinateSystem", "_CRS1");
                    Add("cim:PositionPoint", "_PP" + s, EQ).Ref("cim:PositionPoint.Location", "_LOC" + s)
                        .Lit("cim:PositionPoint.sequenceNumber", 1).Lit("cim:PositionPoint.xPosition", g.Longitude).Lit("cim:PositionPoint.yPosition", g.Latitude);
                }
                Add("cim:VoltageLevel", "_VL" + s, EQ).Lit("cim:IdentifiedObject.name", name)
                    .Ref("cim:VoltageLevel.Substation", "_SS" + s).Ref("cim:VoltageLevel.BaseVoltage", bvIds[bus.BaseKv])
                    .Lit("cim:VoltageLevel.highVoltageLimit", bus.Vmax * bus.BaseKv).Lit("cim:VoltageLevel.lowVoltageLimit", bus.Vmin * bus.BaseKv);
            }
            var c = Add("cim:ConnectivityNode", cn, EQ).Lit("cim:IdentifiedObject.name", name);
            if (o.IncludeContainers) c.Ref("cim:ConnectivityNode.ConnectivityNodeContainer", "_VL" + s);
            c.Ref("cim:ConnectivityNode.TopologicalNode", tn, TP);
            if (ext)
            {
                c.Lit("eln:ConnectivityNode.busId", bus.Id).Lit("eln:ConnectivityNode.busType", bus.Type.ToString())
                 .Lit("eln:ConnectivityNode.baseKv", bus.BaseKv).Lit("eln:ConnectivityNode.area", bus.Area).Lit("eln:ConnectivityNode.zone", bus.Zone)
                 .Lit("eln:ConnectivityNode.vmax", bus.Vmax).Lit("eln:ConnectivityNode.vmin", bus.Vmin);
            }
            Add("cim:TopologicalNode", tn, TP).Lit("cim:IdentifiedObject.name", name).Ref("cim:TopologicalNode.BaseVoltage", bvIds[bus.BaseKv]);
            if (o.IncludeStateVariables)
                Add("cim:SvVoltage", "_SV" + s, SV).Ref("cim:SvVoltage.TopologicalNode", tn).Lit("cim:SvVoltage.v", bus.Vm * bus.BaseKv).Lit("cim:SvVoltage.angle", bus.VaDeg);
            if (bus.Gs != 0 || bus.Bs != 0)
            {
                double kv2 = bus.BaseKv * bus.BaseKv;
                Add("cim:LinearShuntCompensator", "_SH" + s, EQ).Lit("cim:IdentifiedObject.name", $"Shunt {bus.Id}")
                    .Lit("cim:ShuntCompensator.nomU", bus.BaseKv).Lit("cim:ShuntCompensator.maximumSections", 1).Lit("cim:ShuntCompensator.normalSections", 1)
                    .Lit("cim:LinearShuntCompensator.bPerSection", bus.Bs / kv2).Lit("cim:LinearShuntCompensator.gPerSection", bus.Gs / kv2)
                    .Lit("cim:ShuntCompensator.sections", 1, SSH);
                Terminal("_SH" + s, 1, bus.Id, true);
            }
        }
        if (o.IncludeContainers && net.Buses.Any(b => b.Location is not null))
            Add("cim:CoordinateSystem", "_CRS1", EQ).Lit("cim:IdentifiedObject.name", "WGS 84").Lit("cim:CoordinateSystem.crsUrn", "urn:ogc:def:crs:EPSG::4326");

        void Terminal(string eq, int seq, int busId, bool connected, string? suffix = null)
        {
            string id = eq + "_T" + (suffix ?? seq.ToString(Inv));
            Add("cim:Terminal", id, EQ).Lit("cim:IdentifiedObject.name", id.TrimStart('_'))
                .Ref("cim:Terminal.ConductingEquipment", eq).Lit("cim:ACDCTerminal.sequenceNumber", seq)
                .Ref("cim:Terminal.ConnectivityNode", "_CN" + Sid(busId))
                .Lit("cim:ACDCTerminal.connected", connected, SSH);
        }

        void Limit(string eq, double rateMva)
        {
            if (rateMva <= 0) return;
            Add("cim:OperationalLimitSet", eq + "_OLS", EQ).Lit("cim:IdentifiedObject.name", "Ratings").Ref("cim:OperationalLimitSet.Terminal", eq + "_T1");
            Add("cim:ApparentPowerLimit", eq + "_APL", EQ).Lit("cim:IdentifiedObject.name", "RateA")
                .Ref("cim:OperationalLimit.OperationalLimitSet", eq + "_OLS").Lit("cim:ApparentPowerLimit.value", rateMva);
        }

        // Branches.
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            var from = net.GetBus(br.From);
            var to = net.GetBus(br.To);
            if (!br.IsTransformer)
            {
                string id = "_LN" + (k + 1).ToString(Inv);
                double zb = from.BaseKv * from.BaseKv / sBase;
                var l = Add("cim:ACLineSegment", id, EQ).Lit("cim:IdentifiedObject.name", br.Name ?? $"{br.From}-{br.To}")
                    .Ref("cim:ConductingEquipment.BaseVoltage", bvIds[from.BaseKv])
                    .Lit("cim:ACLineSegment.r", br.R * zb).Lit("cim:ACLineSegment.x", br.X * zb)
                    .Lit("cim:ACLineSegment.bch", br.B / zb).Lit("cim:ACLineSegment.gch", 0.0)
                    .Lit("cim:Conductor.length", br.LengthKm);
                if (!double.IsNaN(br.R0)) l.Lit("cim:ACLineSegment.r0", br.R0 * zb);
                if (!double.IsNaN(br.X0)) l.Lit("cim:ACLineSegment.x0", br.X0 * zb);
                if (!double.IsNaN(br.B0)) l.Lit("cim:ACLineSegment.b0ch", br.B0 / zb);
                if (ext) l.Lit("eln:ACLineSegment.kind", br.Kind.ToString());
                Terminal(id, 1, br.From, br.InService);
                Terminal(id, 2, br.To, br.InService);
                Limit(id, br.RateA);
            }
            else
            {
                string id = "_TR" + (k + 1).ToString(Inv);
                double u1 = from.BaseKv, u2 = to.BaseKv, zb = u1 * u1 / sBase;
                var (c1, c2) = Connection(br.Connection);
                Add("cim:PowerTransformer", id, EQ).Lit("cim:IdentifiedObject.name", br.Name ?? $"{br.From}-{br.To}");
                var e1 = Add("cim:PowerTransformerEnd", id + "_E1", EQ).Lit("cim:IdentifiedObject.name", $"{id.TrimStart('_')} HV")
                    .Ref("cim:PowerTransformerEnd.PowerTransformer", id).Lit("cim:TransformerEnd.endNumber", 1)
                    .Ref("cim:TransformerEnd.Terminal", id + "_T1").Ref("cim:TransformerEnd.BaseVoltage", bvIds[from.BaseKv])
                    .Lit("cim:PowerTransformerEnd.ratedU", u1).Lit("cim:PowerTransformerEnd.ratedS", br.RateA)
                    .Lit("cim:PowerTransformerEnd.r", br.R * zb).Lit("cim:PowerTransformerEnd.x", br.X * zb)
                    .Lit("cim:PowerTransformerEnd.b", br.B / zb).Lit("cim:PowerTransformerEnd.g", 0.0)
                    .Enum("cim:PowerTransformerEnd.connectionKind", "WindingConnection." + c1);
                if (!double.IsNaN(br.R0)) e1.Lit("cim:PowerTransformerEnd.r0", br.R0 * zb);
                if (!double.IsNaN(br.X0)) e1.Lit("cim:PowerTransformerEnd.x0", br.X0 * zb);
                Add("cim:PowerTransformerEnd", id + "_E2", EQ).Lit("cim:IdentifiedObject.name", $"{id.TrimStart('_')} LV")
                    .Ref("cim:PowerTransformerEnd.PowerTransformer", id).Lit("cim:TransformerEnd.endNumber", 2)
                    .Ref("cim:TransformerEnd.Terminal", id + "_T2").Ref("cim:TransformerEnd.BaseVoltage", bvIds[to.BaseKv])
                    .Lit("cim:PowerTransformerEnd.ratedU", u2).Lit("cim:PowerTransformerEnd.ratedS", br.RateA)
                    .Lit("cim:PowerTransformerEnd.r", 0.0).Lit("cim:PowerTransformerEnd.x", 0.0)
                    .Lit("cim:PowerTransformerEnd.b", 0.0).Lit("cim:PowerTransformerEnd.g", 0.0)
                    .Enum("cim:PowerTransformerEnd.connectionKind", "WindingConnection." + c2);
                if (br.EffectiveTap != 1)
                {
                    // Off-nominal ratio as a one-step tap changer on end 1: ratio = 1 + step · increment/100.
                    Add("cim:RatioTapChanger", id + "_RTC", EQ).Lit("cim:IdentifiedObject.name", "RTC")
                        .Ref("cim:RatioTapChanger.TransformerEnd", id + "_E1")
                        .Lit("cim:TapChanger.lowStep", -1).Lit("cim:TapChanger.highStep", 1).Lit("cim:TapChanger.neutralStep", 0)
                        .Lit("cim:TapChanger.normalStep", 1).Lit("cim:TapChanger.neutralU", u1).Lit("cim:TapChanger.ltcFlag", false)
                        .Lit("cim:RatioTapChanger.stepVoltageIncrement", (br.EffectiveTap - 1) * 100)
                        .Lit("cim:TapChanger.step", 1.0, SSH).Lit("cim:TapChanger.controlEnabled", false, SSH);
                }
                if (br.ShiftDeg != 0)
                {
                    Add("cim:PhaseTapChangerLinear", id + "_PTC", EQ).Lit("cim:IdentifiedObject.name", "PTC")
                        .Ref("cim:PhaseTapChanger.TransformerEnd", id + "_E1")
                        .Lit("cim:TapChanger.lowStep", -1).Lit("cim:TapChanger.highStep", 1).Lit("cim:TapChanger.neutralStep", 0)
                        .Lit("cim:TapChanger.normalStep", 1).Lit("cim:TapChanger.neutralU", u1).Lit("cim:TapChanger.ltcFlag", false)
                        .Lit("cim:PhaseTapChangerLinear.stepPhaseShiftIncrement", br.ShiftDeg)
                        .Lit("cim:PhaseTapChangerLinear.xMin", br.X * zb).Lit("cim:PhaseTapChangerLinear.xMax", br.X * zb)
                        .Lit("cim:TapChanger.step", 1.0, SSH).Lit("cim:TapChanger.controlEnabled", false, SSH);
                }
                Terminal(id, 1, br.From, br.InService);
                Terminal(id, 2, br.To, br.InService);
                Limit(id, br.RateA);
            }
        }

        // Generators.
        for (int i = 0; i < net.Generators.Count; i++)
        {
            var g = net.Generators[i];
            var bus = net.GetBus(g.Bus);
            string n = (i + 1).ToString(Inv), sm = "_SM" + n, gu = "_GU" + n, rc = "_RC" + n;
            string guClass = g.Kind switch
            {
                GeneratorKind.Thermal or GeneratorKind.Gas or GeneratorKind.Diesel => "cim:ThermalGeneratingUnit",
                GeneratorKind.Hydro => "cim:HydroGeneratingUnit",
                GeneratorKind.Nuclear => "cim:NuclearGeneratingUnit",
                GeneratorKind.Wind => "cim:WindGeneratingUnit",
                GeneratorKind.Solar => "cim:SolarGeneratingUnit",
                _ => "cim:GeneratingUnit",
            };
            var u = Add(guClass, gu, EQ).Lit("cim:IdentifiedObject.name", g.Name ?? $"GU {n}")
                .Lit("cim:GeneratingUnit.maxOperatingP", g.Pmax).Lit("cim:GeneratingUnit.minOperatingP", g.Pmin)
                .Lit("cim:GeneratingUnit.initialP", g.P).Lit("cim:GeneratingUnit.normalPF", 1.0, SSH);
            if (ext) u.Lit("eln:GeneratingUnit.kind", g.Kind.ToString()).Lit("eln:GeneratingUnit.costC2", g.Cost.C2)
                      .Lit("eln:GeneratingUnit.costC1", g.Cost.C1).Lit("eln:GeneratingUnit.costC0", g.Cost.C0);
            if (!double.IsPositiveInfinity(g.RampRate) && ext) u.Lit("eln:GeneratingUnit.rampRate", g.RampRate);
            Add("cim:RegulatingControl", rc, EQ).Lit("cim:IdentifiedObject.name", $"V control {n}")
                .Ref("cim:RegulatingControl.Terminal", sm + "_T1").Enum("cim:RegulatingControl.mode", "RegulatingControlModeKind.voltage")
                .Lit("cim:RegulatingControl.discrete", false, SSH).Lit("cim:RegulatingControl.enabled", true, SSH)
                .Lit("cim:RegulatingControl.targetValue", g.Vset * bus.BaseKv, SSH)
                .Lit("cim:RegulatingControl.targetDeadband", 0.0, SSH)
                .Enum("cim:RegulatingControl.targetValueUnitMultiplier", "UnitMultiplier.k", SSH);
            var s = Add("cim:SynchronousMachine", sm, EQ).Lit("cim:IdentifiedObject.name", g.Name ?? $"SM {n}")
                .Ref("cim:RotatingMachine.GeneratingUnit", gu).Ref("cim:RegulatingCondEq.RegulatingControl", rc)
                .Ref("cim:Equipment.EquipmentContainer", "_VL" + Sid(g.Bus))
                .Lit("cim:RotatingMachine.ratedS", g.MBase).Lit("cim:RotatingMachine.ratedU", bus.BaseKv)
                .Lit("cim:SynchronousMachine.maxQ", g.Qmax).Lit("cim:SynchronousMachine.minQ", g.Qmin)
                .Enum("cim:SynchronousMachine.type", g.Kind == GeneratorKind.Other && g.Pmax <= 0 ? "SynchronousMachineKind.condenser" : "SynchronousMachineKind.generator")
                .Lit("cim:SynchronousMachine.satDirectSyncX", g.Xd).Lit("cim:SynchronousMachine.satDirectTransX", g.XdPrime)
                .Lit("cim:SynchronousMachine.satDirectSubtransX", g.XdSubtransient)
                .Lit("cim:SynchronousMachine.x0", g.X0).Lit("cim:SynchronousMachine.x2", g.X2).Lit("cim:SynchronousMachine.r", g.Ra)
                .Lit("cim:RotatingMachine.p", -g.P, SSH).Lit("cim:RotatingMachine.q", -g.Q, SSH)
                .Lit("cim:RegulatingCondEq.controlEnabled", true, SSH)
                .Enum("cim:SynchronousMachine.operatingMode", "SynchronousMachineOperatingMode.generator", SSH)
                .Lit("cim:SynchronousMachine.referencePriority", bus.Type == BusType.Slack ? 1 : 0, SSH);
            if (!o.IncludeContainers) s.Properties.RemoveAll(p => p.Name == "cim:Equipment.EquipmentContainer");
            if (ext) s.Lit("eln:SynchronousMachine.inertia", g.H).Lit("eln:SynchronousMachine.damping", g.D);
            Terminal(sm, 1, g.Bus, g.InService);
        }

        // Loads.
        for (int i = 0; i < net.Loads.Count; i++)
        {
            var l = net.Loads[i];
            string n = (i + 1).ToString(Inv), ec = "_EC" + n;
            var e = Add("cim:EnergyConsumer", ec, EQ).Lit("cim:IdentifiedObject.name", l.Name ?? $"Load {n}")
                .Lit("cim:EnergyConsumer.p", l.P, SSH).Lit("cim:EnergyConsumer.q", l.Q, SSH);
            if (o.IncludeContainers) e.Ref("cim:Equipment.EquipmentContainer", "_VL" + Sid(l.Bus));
            if (l.Category is not null && ext) e.Lit("eln:EnergyConsumer.category", l.Category);
            if (l.Model != ZipModel.ConstantPower)
            {
                e.Ref("cim:EnergyConsumer.LoadResponse", "_LRC" + n);
                Add("cim:LoadResponseCharacteristic", "_LRC" + n, EQ).Lit("cim:IdentifiedObject.name", "ZIP")
                    .Lit("cim:LoadResponseCharacteristic.exponentModel", false)
                    .Lit("cim:LoadResponseCharacteristic.pConstantImpedance", l.Model.Z).Lit("cim:LoadResponseCharacteristic.pConstantCurrent", l.Model.I)
                    .Lit("cim:LoadResponseCharacteristic.pConstantPower", l.Model.P)
                    .Lit("cim:LoadResponseCharacteristic.qConstantImpedance", l.Model.Z).Lit("cim:LoadResponseCharacteristic.qConstantCurrent", l.Model.I)
                    .Lit("cim:LoadResponseCharacteristic.qConstantPower", l.Model.P);
            }
            Terminal(ec, 1, l.Bus, l.InService);
        }
        return list;
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
