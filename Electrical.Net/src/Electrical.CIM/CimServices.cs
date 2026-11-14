using System.Xml.Linq;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
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

/// <summary>CGMES 3.0 export, CGMES 2.4.15/3.0 import and validation, registered by <see cref="CimBuilderExtensions.AddCim"/>.</summary>
public interface ICgmesService
{
    /// <summary>Exports EQ/TP/SSH(/SV) CGMES 3.0 documents (see <see cref="Cgmes3Exporter.Export"/>).</summary>
    CgmesDocumentSet Export(PowerNetwork network, PowerFlowResult? result = null, Cgmes3ExportOptions? options = null);
    /// <summary>Imports a CGMES set (version auto-detected).</summary>
    CgmesImportResult Import(IEnumerable<XDocument> documents, CimImportOptions? options = null);
    /// <summary>Validates a CGMES set against the <see cref="CgmesValidator.Rules"/>.</summary>
    CgmesValidationReport Validate(IEnumerable<XDocument> documents, CgmesValidationOptions? options = null);
}

internal sealed class CgmesService : ICgmesService
{
    public CgmesDocumentSet Export(PowerNetwork network, PowerFlowResult? result = null, Cgmes3ExportOptions? options = null) => Cgmes3Exporter.Export(network, result, options);
    public CgmesImportResult Import(IEnumerable<XDocument> documents, CimImportOptions? options = null) => CgmesImporter.Import(documents, options);
    public CgmesValidationReport Validate(IEnumerable<XDocument> documents, CgmesValidationOptions? options = null) => CgmesValidator.Validate(documents, options);
}

/// <summary>DI registration for the CIM module.</summary>
public static class CimBuilderExtensions
{
    /// <summary>Registers <see cref="ICimService"/> and <see cref="ICgmesService"/>.</summary>
    public static ElectricalBuilder AddCim(this ElectricalBuilder b)
    {
        if (b.TryAddModule("CIM"))
        {
            b.Services.TryAddSingleton<ICimService, CimService>();
            b.Services.TryAddSingleton<ICgmesService, CgmesService>();
        }
        return b;
    }
}
