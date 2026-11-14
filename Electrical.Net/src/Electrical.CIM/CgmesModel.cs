using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Electrical.CIM;

/// <summary>CGMES release a document set conforms to (detected from the CIM namespace).</summary>
public enum CgmesVersion
{
    /// <summary>No CIM namespace recognised.</summary>
    Unknown,
    /// <summary>CGMES 2.4.15 (CIM16, <c>http://iec.ch/TC57/2013/CIM-schema-cim16#</c>).</summary>
    Cgmes2_4_15,
    /// <summary>CGMES 3.0 (CIM100, <c>http://iec.ch/TC57/CIM100#</c>, IEC 61970-600-1/-2 Ed.2).</summary>
    Cgmes3_0,
}

/// <summary>CGMES instance profile of one document, derived from its <c>Model.profile</c> URI.</summary>
public enum CgmesProfileKind
{
    /// <summary>Profile URI missing or not recognised.</summary>
    Unknown,
    /// <summary>Equipment (EQ, CoreEquipment / Operation / ShortCircuit).</summary>
    Equipment,
    /// <summary>Topology (TP).</summary>
    Topology,
    /// <summary>Steady-state hypothesis (SSH).</summary>
    SteadyStateHypothesis,
    /// <summary>State variables (SV).</summary>
    StateVariables,
    /// <summary>Equipment boundary (EQBD).</summary>
    EquipmentBoundary,
    /// <summary>Topology boundary (TPBD, CGMES 2.4.15 only).</summary>
    TopologyBoundary,
    /// <summary>Diagram layout (DL).</summary>
    DiagramLayout,
    /// <summary>Geographical location (GL).</summary>
    GeographicalLocation,
}

/// <summary>Namespaces and profile URIs of CGMES 3.0 (ENTSO-E CGMES 3.0.x, IEC 61970-600-2 Ed.2).</summary>
public static class Cgmes3Namespaces
{
    /// <summary>CIM100 namespace.</summary>
    public const string Cim = "http://iec.ch/TC57/CIM100#";
    /// <summary>ENTSO-E European extension namespace (e.g. <c>eu:OperationalLimitType.kind</c>).</summary>
    public const string Eu = "http://iec.ch/TC57/CIM100-European#";
    /// <summary>Model header namespace (IEC 61970-552).</summary>
    public const string Md = CimNamespaces.Md;
    /// <summary>W3C DCAT namespace (IEC 61970-552 Ed.2 dataset headers; read, not written).</summary>
    public const string Dcat = "http://www.w3.org/ns/dcat#";
    /// <summary>Dublin Core terms namespace used by DCAT headers.</summary>
    public const string Dcterms = "http://purl.org/dc/terms/";

    /// <summary>CoreEquipment-EU profile URI.</summary>
    public const string EquipmentProfile = "http://iec.ch/TC57/ns/CIM/CoreEquipment-EU/3.0";
    /// <summary>Operation-EU profile URI (additional EQ content).</summary>
    public const string OperationProfile = "http://iec.ch/TC57/ns/CIM/Operation-EU/3.0";
    /// <summary>Topology-EU profile URI.</summary>
    public const string TopologyProfile = "http://iec.ch/TC57/ns/CIM/Topology-EU/3.0";
    /// <summary>SteadyStateHypothesis-EU profile URI.</summary>
    public const string SteadyStateHypothesisProfile = "http://iec.ch/TC57/ns/CIM/SteadyStateHypothesis-EU/3.0";
    /// <summary>StateVariables-EU profile URI.</summary>
    public const string StateVariablesProfile = "http://iec.ch/TC57/ns/CIM/StateVariables-EU/3.0";

    /// <summary>Profile URI of a profile kind (EQ/TP/SSH/SV), or null.</summary>
    public static string? ProfileUri(CgmesProfileKind kind) => kind switch
    {
        CgmesProfileKind.Equipment => EquipmentProfile,
        CgmesProfileKind.Topology => TopologyProfile,
        CgmesProfileKind.SteadyStateHypothesis => SteadyStateHypothesisProfile,
        CgmesProfileKind.StateVariables => StateVariablesProfile,
        _ => null,
    };

    /// <summary>Classifies a CGMES 2.4.15 or 3.0 profile URI.</summary>
    public static CgmesProfileKind ProfileKind(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return CgmesProfileKind.Unknown;
        if (uri.Contains("EquipmentBoundary", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.EquipmentBoundary;
        if (uri.Contains("TopologyBoundary", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.TopologyBoundary;
        if (uri.Contains("Equipment", StringComparison.OrdinalIgnoreCase) || uri.Contains("/Operation", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("ShortCircuit", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.Equipment;
        if (uri.Contains("Topology", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.Topology;
        if (uri.Contains("SteadyStateHypothesis", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.SteadyStateHypothesis;
        if (uri.Contains("StateVariables", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.StateVariables;
        if (uri.Contains("Diagram", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.DiagramLayout;
        if (uri.Contains("Geographical", StringComparison.OrdinalIgnoreCase)) return CgmesProfileKind.GeographicalLocation;
        return CgmesProfileKind.Unknown;
    }

    /// <summary>Detects the CGMES version from the namespaces declared or used by a document.</summary>
    public static CgmesVersion DetectVersion(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root;
        if (root is null) return CgmesVersion.Unknown;
        bool v3 = false, v2 = false;
        foreach (var a in root.Attributes().Where(a => a.IsNamespaceDeclaration)) Classify(a.Value, ref v2, ref v3);
        if (!v2 && !v3) foreach (var e in root.Elements()) Classify(e.Name.NamespaceName, ref v2, ref v3);
        return v3 ? CgmesVersion.Cgmes3_0 : v2 ? CgmesVersion.Cgmes2_4_15 : CgmesVersion.Unknown;

        static void Classify(string ns, ref bool v2, ref bool v3)
        {
            if (ns == Cim) v3 = true;
            else if (ns.StartsWith("http://iec.ch/TC57/2013/CIM-schema-cim16", StringComparison.Ordinal)) v2 = true;
        }
    }

    /// <summary>Detects the version of a document set (the newest version found wins).</summary>
    public static CgmesVersion DetectVersion(IEnumerable<XDocument> documents) =>
        documents.Select(DetectVersion).DefaultIfEmpty(CgmesVersion.Unknown).Max();
}

/// <summary>A parsed model header (<c>md:FullModel</c>, or an IEC 61970-552 Ed.2 <c>dcat:Dataset</c>).</summary>
/// <param name="Id">Model URN (e.g. <c>urn:uuid:…</c>).</param>
/// <param name="Profile">Profile kind of the first recognised <c>Model.profile</c>.</param>
/// <param name="ProfileUris">All <c>Model.profile</c> URIs.</param>
/// <param name="ScenarioTime">Model.scenarioTime (UTC) or null.</param>
/// <param name="Created">Model.created (UTC) or null.</param>
/// <param name="ModelingAuthoritySet">Model.modelingAuthoritySet or null.</param>
/// <param name="DependentOn">Model.DependentOn targets.</param>
/// <param name="Description">Model.description or null.</param>
/// <param name="Version">Model.version or null.</param>
public sealed record CgmesModelHeader(string Id, CgmesProfileKind Profile, IReadOnlyList<string> ProfileUris, DateTimeOffset? ScenarioTime,
    DateTimeOffset? Created, string? ModelingAuthoritySet, IReadOnlyList<string> DependentOn, string? Description, int? Version)
{
    /// <summary>Reads the header of a document, or null when it has none.</summary>
    public static CgmesModelHeader? Read(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var h = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName is "FullModel" or "Dataset" or "DifferenceModel");
        return h is null ? null : Read(h);
    }

    internal static CgmesModelHeader Read(XElement h)
    {
        XNamespace rdf = CimNamespaces.Rdf;
        string id = (string?)h.Attribute(rdf + "about") ?? (string?)h.Attribute(rdf + "ID") ?? "";
        string? Text(params string[] names) => h.Elements().FirstOrDefault(e => names.Contains(e.Name.LocalName))?.Value.Trim();
        IEnumerable<string> Values(params string[] names) => h.Elements().Where(e => names.Contains(e.Name.LocalName))
            .Select(e => ((string?)e.Attribute(rdf + "resource") ?? e.Value).Trim()).Where(s => s.Length > 0);
        var profiles = Values("Model.profile", "conformsTo").ToList();
        var kind = profiles.Select(Cgmes3Namespaces.ProfileKind).FirstOrDefault(k => k != CgmesProfileKind.Unknown);
        return new CgmesModelHeader(id, kind, profiles, Time(Text("Model.scenarioTime", "startDate", "temporal")),
            Time(Text("Model.created", "issued", "generatedAtTime")), Text("Model.modelingAuthoritySet", "publisher"),
            Values("Model.DependentOn", "requires", "references").ToList(), Text("Model.description", "description"),
            int.TryParse(Text("Model.version", "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null);
    }

    private static DateTimeOffset? Time(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}

/// <summary>Deterministic identifiers for CGMES objects.</summary>
public static class CgmesIds
{
    /// <summary>Namespace UUID used for Electrical.Net name-based mRIDs (UUID v5 of "electrical-net/cgmes" in the RFC 4122 URL namespace).</summary>
    public static readonly Guid Namespace = NameBasedUuid(new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8"), "https://gravicode.studio/electrical-net/cgmes");

    /// <summary>RFC 4122 §4.3 name-based UUID, version 5 (SHA-1).</summary>
    public static Guid NameBasedUuid(Guid ns, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> nsBytes = stackalloc byte[16];
        ns.TryWriteBytes(nsBytes, bigEndian: true, out _);
        var data = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        nsBytes.CopyTo(data);
        Encoding.UTF8.GetBytes(name, data.AsSpan(16));
        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // SHA-1 is mandated by RFC 4122 for version-5 UUIDs (not used for security).
        SHA1.HashData(data, hash);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    /// <summary>Name-based UUID v5 in the Electrical.Net namespace.</summary>
    public static Guid NameBasedUuid(string name) => NameBasedUuid(Namespace, name);
}

/// <summary>A CGMES instance set: EQ, TP, SSH and optionally SV documents, each with its own model header.</summary>
/// <param name="Equipment">EQ document.</param>
/// <param name="Topology">TP document.</param>
/// <param name="SteadyStateHypothesis">SSH document.</param>
/// <param name="StateVariables">SV document, or null when no state was exported.</param>
/// <param name="BaseName">File name stem used by <see cref="WriteZip"/> and <see cref="Save"/> (e.g. <c>case9</c> → <c>case9_EQ.xml</c>).</param>
public sealed record CgmesDocumentSet(XDocument Equipment, XDocument Topology, XDocument SteadyStateHypothesis, XDocument? StateVariables, string BaseName)
{
    /// <summary>The documents in dependency order (EQ, TP, SSH[, SV]).</summary>
    public XDocument[] All => StateVariables is null ? [Equipment, Topology, SteadyStateHypothesis] : [Equipment, Topology, SteadyStateHypothesis, StateVariables];

    /// <summary>The documents with their file names (<c>{BaseName}_EQ.xml</c>, …).</summary>
    public IEnumerable<(string FileName, XDocument Document)> Files()
    {
        yield return ($"{BaseName}_EQ.xml", Equipment);
        yield return ($"{BaseName}_TP.xml", Topology);
        yield return ($"{BaseName}_SSH.xml", SteadyStateHypothesis);
        if (StateVariables is not null) yield return ($"{BaseName}_SV.xml", StateVariables);
    }

    /// <summary>Serializes one document as UTF-8 RDF/XML.</summary>
    public static string ToXml(XDocument document)
    {
        var sb = new Utf8StringWriter();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) })) document.Save(w);
        return sb.ToString();
    }

    /// <summary>Writes the documents as a zip archive (one XML entry per profile; entry times are fixed for reproducible output).</summary>
    public void WriteZip(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, doc) in Files())
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(ToXml(doc));
            s.Write(bytes);
        }
    }

    /// <summary>Returns the zip archive as bytes.</summary>
    public byte[] ToZip()
    {
        using var ms = new MemoryStream();
        WriteZip(ms);
        return ms.ToArray();
    }

    /// <summary>Saves the documents as separate XML files in <paramref name="directory"/>; returns the paths.</summary>
    public IReadOnlyList<string> Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var (name, doc) in Files())
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, ToXml(doc), new UTF8Encoding(false));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Reads every <c>.xml</c> entry of a CGMES zip archive (nested zips are expanded too).</summary>
    public static IReadOnlyList<XDocument> ReadZip(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var docs = new List<XDocument>();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var e in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = e.Open();
            if (e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) docs.Add(XDocument.Load(s));
            else if (e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                docs.AddRange(ReadZip(ms));
            }
        }
        return docs;
    }
}
