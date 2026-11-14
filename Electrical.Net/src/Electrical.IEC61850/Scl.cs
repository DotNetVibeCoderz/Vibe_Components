using System.Globalization;
using System.Xml.Linq;

namespace Electrical.IEC61850;

/// <summary>Instantiated data attribute value from a DOI/SDI/DAI chain. <see cref="Path"/> is relative to the LN (e.g. "Mod.ctlModel").</summary>
public sealed record SclDai(string Path, string? Value, string? SAddr = null);

/// <summary>One FCDA (functional-constrained data/attribute) of a dataset.</summary>
public sealed record SclFcda(string? LdInst, string? Prefix, string? LnClass, string? LnInst, string? DoName, string? DaName, FunctionalConstraint? Fc)
{
    /// <summary>Object reference of this member within <paramref name="iedName"/>.</summary>
    public ObjectReference ToReference(string iedName)
    {
        var path = new List<string>();
        if (DoName is not null) path.AddRange(DoName.Split('.'));
        if (DaName is not null) path.AddRange(DaName.Split('.'));
        return new ObjectReference(iedName + LdInst, $"{Prefix}{LnClass}{LnInst}", path, Fc);
    }
}

public sealed record SclDataSet(string Name, string? Desc, IReadOnlyList<SclFcda> Members);

/// <summary>GOOSE control block.</summary>
public sealed record SclGseControl(string Name, string? DatSet, string? AppId, uint ConfRev, string Type, string? Desc);

/// <summary>Sampled-value control block. <see cref="SmpRate"/> in samples per period (or per second when SmpMod says so).</summary>
public sealed record SclSampledValueControl(string Name, string? DatSet, string? SmvId, int SmpRate, int NofAsdu, uint ConfRev, bool Multicast, string? SmpMod);

/// <summary>Report control block (buffered or unbuffered).</summary>
public sealed record SclReportControl(string Name, string? DatSet, string? RptId, bool Buffered, uint ConfRev);

/// <summary>Logical node instance (LN0 or LN).</summary>
public sealed class SclLogicalNode
{
    public string? Prefix { get; init; }
    public required string LnClass { get; init; }
    public string? Inst { get; init; }
    /// <summary>Reference to an <see cref="SclLNodeType"/> id.</summary>
    public required string LnType { get; init; }
    public string? Desc { get; init; }
    public List<SclDai> Values { get; } = [];
    public List<SclDataSet> DataSets { get; } = [];
    public List<SclGseControl> GseControls { get; } = [];
    public List<SclSampledValueControl> SampledValueControls { get; } = [];
    public List<SclReportControl> ReportControls { get; } = [];

    /// <summary>LN name = prefix + lnClass + inst (e.g. "Q0XCBR1", "LLN0").</summary>
    public string Name => $"{Prefix}{LnClass}{Inst}";

    /// <summary>Configured value of a DAI by relative path (e.g. "Mod.ctlModel"), or null.</summary>
    public string? GetValue(string path) => Values.FirstOrDefault(v => v.Path == path)?.Value;

    public override string ToString() => Name;
}

public sealed class SclLDevice
{
    public required string Inst { get; init; }
    public string? Desc { get; init; }
    public required SclLogicalNode Ln0 { get; init; }
    public List<SclLogicalNode> LogicalNodes { get; } = [];

    /// <summary>LN0 followed by all other LNs.</summary>
    public IEnumerable<SclLogicalNode> AllLogicalNodes => LogicalNodes.Prepend(Ln0);

    public SclLogicalNode? FindLogicalNode(string name) => AllLogicalNodes.FirstOrDefault(l => l.Name == name);
}

public sealed class SclAccessPoint
{
    public required string Name { get; init; }
    public List<SclLDevice> LDevices { get; } = [];
}

public sealed class SclIed
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public string? Manufacturer { get; init; }
    public string? ConfigVersion { get; init; }
    public string? Desc { get; init; }
    public List<SclAccessPoint> AccessPoints { get; } = [];

    public IEnumerable<SclLDevice> LDevices => AccessPoints.SelectMany(a => a.LDevices);

    /// <summary>Logical device by instance name ("CTRL") or full LD name ("IED1CTRL").</summary>
    public SclLDevice? FindLDevice(string instOrName) =>
        LDevices.FirstOrDefault(l => l.Inst == instOrName || Name + l.Inst == instOrName);
}

/// <summary>Address of a GOOSE (GSE) or SV (SMV) control block from the Communication section.</summary>
public sealed record SclControlBlockAddress(string LdInst, string CbName, IReadOnlyDictionary<string, string> Address, double? MinTimeMs, double? MaxTimeMs)
{
    public MacAddress? MacAddress => Address.TryGetValue("MAC-Address", out var m) ? IEC61850.MacAddress.Parse(m) : null;
    public ushort? AppId => Address.TryGetValue("APPID", out var a) ? ushort.Parse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    public ushort? VlanId => Address.TryGetValue("VLAN-ID", out var v) ? ushort.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    public byte? VlanPriority => Address.TryGetValue("VLAN-PRIORITY", out var p) ? byte.Parse(p, CultureInfo.InvariantCulture) : null;
}

public sealed class SclConnectedAp
{
    public required string IedName { get; init; }
    public required string ApName { get; init; }
    /// <summary>P-type address parameters (IP, IP-SUBNET, …).</summary>
    public Dictionary<string, string> Address { get; } = [];
    public List<SclControlBlockAddress> Gse { get; } = [];
    public List<SclControlBlockAddress> Smv { get; } = [];
}

public sealed class SclSubNetwork
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public List<SclConnectedAp> ConnectedAps { get; } = [];
}

/// <summary>DO (or SDO) declaration inside a type.</summary>
public sealed record SclDoDecl(string Name, string Type);

/// <summary>DA / BDA declaration. <see cref="Fc"/> is null for BDAs.</summary>
public sealed record SclDaDecl(string Name, string BType, string? Type, FunctionalConstraint? Fc, string? Value);

public sealed record SclLNodeType(string Id, string LnClass, IReadOnlyList<SclDoDecl> DataObjects);
public sealed record SclDoType(string Id, string Cdc, IReadOnlyList<SclDoDecl> SubDataObjects, IReadOnlyList<SclDaDecl> DataAttributes);
public sealed record SclDaType(string Id, IReadOnlyList<SclDaDecl> Bdas);
public sealed record SclEnumType(string Id, IReadOnlyDictionary<int, string> Values);

public sealed class SclDataTypeTemplates
{
    public Dictionary<string, SclLNodeType> LNodeTypes { get; } = [];
    public Dictionary<string, SclDoType> DoTypes { get; } = [];
    public Dictionary<string, SclDaType> DaTypes { get; } = [];
    public Dictionary<string, SclEnumType> EnumTypes { get; } = [];
}

/// <summary>A leaf data attribute of the expanded data model.</summary>
public sealed record SclDataAttribute(ObjectReference Reference, string BType, string? EnumType, string? Value);

/// <summary>Parsed SCL file (ICD / CID / IID / SCD / SSD), IEC 61850-6.</summary>
public sealed class SclDocument
{
    public string? HeaderId { get; init; }
    public string? Version { get; init; }
    public string? Revision { get; init; }
    public List<SclIed> Ieds { get; } = [];
    public List<SclSubNetwork> SubNetworks { get; } = [];
    public SclDataTypeTemplates Templates { get; } = new();

    public SclIed? FindIed(string name) => Ieds.FirstOrDefault(i => i.Name == name);

    /// <summary>Communication entry of an IED access point.</summary>
    public SclConnectedAp? FindConnectedAp(string iedName, string? apName = null) =>
        SubNetworks.SelectMany(s => s.ConnectedAps).FirstOrDefault(c => c.IedName == iedName && (apName is null || c.ApName == apName));

    /// <summary>GSE address (MAC, APPID, VLAN, MinTime/MaxTime) of a GOOSE control block.</summary>
    public SclControlBlockAddress? FindGseAddress(string iedName, string ldInst, string cbName) =>
        SubNetworks.SelectMany(s => s.ConnectedAps).Where(c => c.IedName == iedName).SelectMany(c => c.Gse)
            .FirstOrDefault(g => g.LdInst == ldInst && g.CbName == cbName);

    /// <summary>Resolves the configured value of an attribute reference ("IED1CTRL/XCBR1.Pos.ctlModel"), falling back to the template default.</summary>
    public string? GetValue(string iedName, ObjectReference reference)
    {
        var ied = FindIed(iedName);
        var ln = ied?.FindLDevice(reference.LogicalDevice)?.FindLogicalNode(reference.LogicalNode);
        if (ln is null) return null;
        string path = string.Join('.', reference.Path);
        return ln.GetValue(path) ?? DataModel(ied!).FirstOrDefault(a => a.Reference.LogicalDevice == reference.LogicalDevice &&
            a.Reference.LogicalNode == reference.LogicalNode && string.Join('.', a.Reference.Path) == path)?.Value;
    }

    /// <summary>
    /// Expands the data model of an IED through the DataTypeTemplates into leaf attributes with full object references and
    /// functional constraints (e.g. <c>IED1CTRL/XCBR1.Pos.stVal[ST]</c>, type Dbpos). Instance values (DAI) override template defaults.
    /// </summary>
    public IEnumerable<SclDataAttribute> DataModel(SclIed ied)
    {
        foreach (var ld in ied.LDevices)
            foreach (var ln in ld.AllLogicalNodes)
            {
                if (!Templates.LNodeTypes.TryGetValue(ln.LnType, out var lnt)) continue;
                var baseRef = new ObjectReference(ied.Name + ld.Inst, ln.Name, []);
                foreach (var d in lnt.DataObjects)
                    foreach (var a in ExpandDo(baseRef.Child(d.Name), d.Type, ln, 0))
                        yield return a;
            }
    }

    private IEnumerable<SclDataAttribute> ExpandDo(ObjectReference r, string doType, SclLogicalNode ln, int depth)
    {
        if (depth > 16 || !Templates.DoTypes.TryGetValue(doType, out var dt)) yield break;
        foreach (var sdo in dt.SubDataObjects)
            foreach (var a in ExpandDo(r.Child(sdo.Name), sdo.Type, ln, depth + 1)) yield return a;
        foreach (var da in dt.DataAttributes)
            foreach (var a in ExpandDa(r.Child(da.Name, da.Fc), da, ln, depth + 1)) yield return a;
    }

    private IEnumerable<SclDataAttribute> ExpandDa(ObjectReference r, SclDaDecl da, SclLogicalNode ln, int depth)
    {
        if (da.BType == "Struct" && da.Type is not null && Templates.DaTypes.TryGetValue(da.Type, out var t) && depth < 16)
        {
            foreach (var b in t.Bdas)
                foreach (var a in ExpandDa(r.Child(b.Name), b, ln, depth + 1)) yield return a;
            yield break;
        }
        string path = string.Join('.', r.Path);
        yield return new SclDataAttribute(r, da.BType, da.BType == "Enum" ? da.Type : null, ln.GetValue(path) ?? da.Value);
    }
}

/// <summary>SCL parser (System.Xml.Linq). Matches elements by local name so files with or without the SCL namespace are accepted.</summary>
public static class SclParser
{
    /// <summary>SCL XML namespace.</summary>
    public const string Namespace = "http://www.iec.ch/61850/2003/SCL";

    public static SclDocument Parse(string xml) => Parse(XDocument.Parse(xml));

    public static SclDocument Load(string path) => Parse(XDocument.Load(path));

    public static SclDocument Load(Stream stream) => Parse(XDocument.Load(stream));

    public static SclDocument Parse(XDocument xdoc)
    {
        var root = xdoc.Root ?? throw new FormatException("Empty SCL document.");
        if (root.Name.LocalName != "SCL") throw new FormatException($"Root element is '{root.Name.LocalName}', expected 'SCL'.");
        var header = El(root, "Header");
        var doc = new SclDocument
        {
            HeaderId = Attr(header, "id"), Version = Attr(root, "version"), Revision = Attr(root, "revision"),
        };
        foreach (var ied in Els(root, "IED")) doc.Ieds.Add(ParseIed(ied));
        if (El(root, "Communication") is { } comm)
            foreach (var sn in Els(comm, "SubNetwork")) doc.SubNetworks.Add(ParseSubNetwork(sn));
        if (El(root, "DataTypeTemplates") is { } dtt) ParseTemplates(dtt, doc.Templates);
        return doc;
    }

    private static SclIed ParseIed(XElement e)
    {
        var ied = new SclIed
        {
            Name = Attr(e, "name") ?? throw new FormatException("IED without name."),
            Type = Attr(e, "type"), Manufacturer = Attr(e, "manufacturer"), ConfigVersion = Attr(e, "configVersion"), Desc = Attr(e, "desc"),
        };
        foreach (var ap in Els(e, "AccessPoint"))
        {
            var a = new SclAccessPoint { Name = Attr(ap, "name") ?? "" };
            if (El(ap, "Server") is { } server)
                foreach (var ld in Els(server, "LDevice")) a.LDevices.Add(ParseLDevice(ld));
            ied.AccessPoints.Add(a);
        }
        return ied;
    }

    private static SclLDevice ParseLDevice(XElement e)
    {
        var ln0 = El(e, "LN0") is { } l0 ? ParseLn(l0) : new SclLogicalNode { LnClass = "LLN0", LnType = "" };
        var ld = new SclLDevice { Inst = Attr(e, "inst") ?? "", Desc = Attr(e, "desc"), Ln0 = ln0 };
        foreach (var ln in Els(e, "LN")) ld.LogicalNodes.Add(ParseLn(ln));
        return ld;
    }

    private static SclLogicalNode ParseLn(XElement e)
    {
        var ln = new SclLogicalNode
        {
            Prefix = NullIfEmpty(Attr(e, "prefix")), LnClass = Attr(e, "lnClass") ?? "", Inst = NullIfEmpty(Attr(e, "inst")),
            LnType = Attr(e, "lnType") ?? "", Desc = Attr(e, "desc"),
        };
        foreach (var doi in Els(e, "DOI")) CollectValues(doi, Attr(doi, "name") ?? "", ln.Values);
        foreach (var ds in Els(e, "DataSet"))
            ln.DataSets.Add(new SclDataSet(Attr(ds, "name") ?? "", Attr(ds, "desc"), Els(ds, "FCDA").Select(f => new SclFcda(
                Attr(f, "ldInst"), NullIfEmpty(Attr(f, "prefix")), Attr(f, "lnClass"), NullIfEmpty(Attr(f, "lnInst")), Attr(f, "doName"),
                Attr(f, "daName"), Attr(f, "fc") is { } fc ? Enum.Parse<FunctionalConstraint>(fc, true) : null)).ToArray()));
        foreach (var g in Els(e, "GSEControl"))
            ln.GseControls.Add(new SclGseControl(Attr(g, "name") ?? "", Attr(g, "datSet"), Attr(g, "appID"), UInt(Attr(g, "confRev")),
                Attr(g, "type") ?? "GOOSE", Attr(g, "desc")));
        foreach (var s in Els(e, "SampledValueControl"))
            ln.SampledValueControls.Add(new SclSampledValueControl(Attr(s, "name") ?? "", Attr(s, "datSet"), Attr(s, "smvID"),
                (int)UInt(Attr(s, "smpRate")), (int)UInt(Attr(s, "nofASDU")), UInt(Attr(s, "confRev")), Attr(s, "multicast") != "false", Attr(s, "smpMod")));
        foreach (var r in Els(e, "ReportControl"))
            ln.ReportControls.Add(new SclReportControl(Attr(r, "name") ?? "", Attr(r, "datSet"), Attr(r, "rptID"), Attr(r, "buffered") == "true",
                UInt(Attr(r, "confRev"))));
        return ln;
    }

    private static void CollectValues(XElement e, string path, List<SclDai> into)
    {
        foreach (var c in e.Elements())
        {
            switch (c.Name.LocalName)
            {
                case "SDI":
                    CollectValues(c, $"{path}.{Attr(c, "name")}", into);
                    break;
                case "DAI":
                    string p = $"{path}.{Attr(c, "name")}";
                    var vals = Els(c, "Val").ToList();
                    into.Add(new SclDai(p, vals.Count > 0 ? vals[0].Value.Trim() : null, Attr(c, "sAddr")));
                    break;
            }
        }
    }

    private static SclSubNetwork ParseSubNetwork(XElement e)
    {
        var sn = new SclSubNetwork { Name = Attr(e, "name") ?? "", Type = Attr(e, "type") };
        foreach (var cap in Els(e, "ConnectedAP"))
        {
            var c = new SclConnectedAp { IedName = Attr(cap, "iedName") ?? "", ApName = Attr(cap, "apName") ?? "" };
            if (El(cap, "Address") is { } addr) foreach (var (k, v) in Ps(addr)) c.Address[k] = v;
            foreach (var g in Els(cap, "GSE")) c.Gse.Add(ParseCbAddress(g));
            foreach (var s in Els(cap, "SMV")) c.Smv.Add(ParseCbAddress(s));
            sn.ConnectedAps.Add(c);
        }
        return sn;
    }

    private static SclControlBlockAddress ParseCbAddress(XElement e)
    {
        var addr = El(e, "Address") is { } a ? Ps(a).ToDictionary(p => p.Key, p => p.Value) : [];
        return new SclControlBlockAddress(Attr(e, "ldInst") ?? "", Attr(e, "cbName") ?? "", addr, Time(El(e, "MinTime")), Time(El(e, "MaxTime")));
    }

    private static double? Time(XElement? e)
    {
        if (e is null || !double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        // MinTime/MaxTime carry unit="s" multiplier="m" (ms) by default.
        string mult = Attr(e, "multiplier") ?? "m";
        return mult switch { "" => v * 1000, "m" => v, "u" => v / 1000, _ => v };
    }

    private static IEnumerable<KeyValuePair<string, string>> Ps(XElement addr) =>
        Els(addr, "P").Select(p => new KeyValuePair<string, string>(Attr(p, "type") ?? "", p.Value.Trim()));

    private static void ParseTemplates(XElement e, SclDataTypeTemplates t)
    {
        foreach (var x in Els(e, "LNodeType"))
            t.LNodeTypes[Attr(x, "id") ?? ""] = new SclLNodeType(Attr(x, "id") ?? "", Attr(x, "lnClass") ?? "",
                Els(x, "DO").Select(d => new SclDoDecl(Attr(d, "name") ?? "", Attr(d, "type") ?? "")).ToArray());
        foreach (var x in Els(e, "DOType"))
            t.DoTypes[Attr(x, "id") ?? ""] = new SclDoType(Attr(x, "id") ?? "", Attr(x, "cdc") ?? "",
                Els(x, "SDO").Select(d => new SclDoDecl(Attr(d, "name") ?? "", Attr(d, "type") ?? "")).ToArray(),
                Els(x, "DA").Select(ParseDa).ToArray());
        foreach (var x in Els(e, "DAType"))
            t.DaTypes[Attr(x, "id") ?? ""] = new SclDaType(Attr(x, "id") ?? "", Els(x, "BDA").Select(ParseDa).ToArray());
        foreach (var x in Els(e, "EnumType"))
        {
            var d = new Dictionary<int, string>();
            foreach (var v in Els(x, "EnumVal"))
                if (int.TryParse(Attr(v, "ord"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ord)) d[ord] = v.Value.Trim();
            t.EnumTypes[Attr(x, "id") ?? ""] = new SclEnumType(Attr(x, "id") ?? "", d);
        }
    }

    private static SclDaDecl ParseDa(XElement d) => new(Attr(d, "name") ?? "", Attr(d, "bType") ?? "", Attr(d, "type"),
        Attr(d, "fc") is { } fc ? Enum.Parse<FunctionalConstraint>(fc, true) : null, El(d, "Val")?.Value.Trim());

    private static XElement? El(XElement? e, string local) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == local);
    private static IEnumerable<XElement> Els(XElement e, string local) => e.Elements().Where(x => x.Name.LocalName == local);
    private static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;
    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static uint UInt(string? s) => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : 0;
}
