using System.Xml.Linq;
using Electrical.Core.Network;

namespace Electrical.CIM;

/// <summary>Result of <see cref="CgmesImporter"/>: the network plus version, model headers, warnings and the node → bus map.</summary>
public sealed class CgmesImportResult
{
    internal CgmesImportResult(CimImportResult inner, CgmesVersion version, IReadOnlyList<CgmesModelHeader> models)
    {
        Network = inner.Network; Warnings = inner.Warnings; NodeToBus = inner.NodeToBus; Version = version; Models = models;
    }

    /// <summary>Imported bus-branch network (SV voltages, when present, are stored as initial bus voltages).</summary>
    public PowerNetwork Network { get; }
    /// <summary>CGMES version detected from the namespaces.</summary>
    public CgmesVersion Version { get; }
    /// <summary>Model headers of the imported documents.</summary>
    public IReadOnlyList<CgmesModelHeader> Models { get; }
    /// <summary>Unsupported or inconsistent content that was skipped.</summary>
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>ConnectivityNode / TopologicalNode mRID → bus id after topology processing.</summary>
    public IReadOnlyDictionary<string, int> NodeToBus { get; }
}

/// <summary>
/// Imports a CGMES 2.4.15 (CIM16) or CGMES 3.0 (CIM100) instance set — EQ + TP + SSH (+ SV), as separate documents, a merged
/// document or a zip archive — into a <see cref="PowerNetwork"/>. The version is detected from the namespaces; attribute names are
/// matched by local name, so both releases share one mapping (see <see cref="CimImporter"/>). Topology processing uses
/// TopologicalNodes when TP is present and otherwise ConnectivityNodes merged across closed switches.
/// </summary>
public static class CgmesImporter
{
    /// <summary>Imports a set of documents.</summary>
    public static CgmesImportResult Import(IEnumerable<XDocument> documents, CimImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var docs = documents.ToList();
        var models = docs.Select(CgmesModelHeader.Read).Where(h => h is not null).Select(h => h!).ToList();
        return new CgmesImportResult(CimImporter.ImportDetailed(docs, options), Cgmes3Namespaces.DetectVersion(docs), models);
    }

    /// <summary>Imports a zip archive holding the profile documents.</summary>
    public static CgmesImportResult ImportZip(Stream zip, CimImportOptions? options = null) => Import(CgmesDocumentSet.ReadZip(zip), options);

    /// <summary>Imports an exported document set.</summary>
    public static CgmesImportResult Import(CgmesDocumentSet set, CimImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Import(set.All, options);
    }

    /// <summary>Loads a <c>.zip</c> archive, a single <c>.xml</c> file or a directory of <c>.xml</c> files.</summary>
    public static CgmesImportResult Load(string path, CimImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Directory.Exists(path))
            return Import(Directory.GetFiles(path, "*.xml").OrderBy(p => p, StringComparer.Ordinal).Select(XDocument.Load).ToList(), options);
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            return ImportZip(fs, options);
        }
        return Import([XDocument.Load(path)], options);
    }
}
