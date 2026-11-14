using System.Xml.Linq;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.CIM;

/// <summary>CIM import/export service registered by <see cref="CimBuilderExtensions.AddCim"/>.</summary>
public interface ICimService
{
    /// <summary>Exports a network as merged CGMES-style RDF/XML.</summary>
    string Export(PowerNetwork network, CimExportOptions? options = null);
    /// <summary>Exports separate EQ/TP/SSH/SV documents.</summary>
    CimProfiles ExportProfiles(PowerNetwork network, CimExportOptions? options = null);
    /// <summary>Imports RDF/XML documents into a bus-branch network.</summary>
    CimImportResult Import(IEnumerable<XDocument> documents, CimImportOptions? options = null);
}

internal sealed class CimService : ICimService
{
    public string Export(PowerNetwork network, CimExportOptions? options = null) => CimExporter.ExportString(network, options);
    public CimProfiles ExportProfiles(PowerNetwork network, CimExportOptions? options = null) => CimExporter.ExportProfiles(network, options);
    public CimImportResult Import(IEnumerable<XDocument> documents, CimImportOptions? options = null) => CimImporter.ImportDetailed(documents, options);
}

/// <summary>DI registration for the CIM module.</summary>
public static class CimBuilderExtensions
{
    /// <summary>Registers <see cref="ICimService"/>.</summary>
    public static ElectricalBuilder AddCim(this ElectricalBuilder b)
    {
        if (b.TryAddModule("CIM")) b.Services.TryAddSingleton<ICimService, CimService>();
        return b;
    }
}
