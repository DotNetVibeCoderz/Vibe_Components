using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.GIS;

/// <summary>GIS service registered by <see cref="GisBuilderExtensions.AddGis"/>.</summary>
public interface IGisService
{
    /// <summary>GeoJSON FeatureCollection of the network (optionally with power-flow results).</summary>
    string ToGeoJson(PowerNetwork network, PowerFlowResult? result = null, GeoJsonOptions? options = null);
    /// <summary>Parses GeoJSON into a network.</summary>
    GeoJsonImportResult FromGeoJson(string json);
    /// <summary>SVG map of the network.</summary>
    string RenderSvg(PowerNetwork network, PowerFlowResult? result = null, GisSvgOptions? options = null);
    /// <summary>Lays out buses without location around a center; returns the number placed.</summary>
    int Layout(PowerNetwork network, GeoPoint center, GeoLayoutOptions? options = null);
    /// <summary>Assigns line lengths [km] from coordinates; returns the number updated.</summary>
    int AssignLengths(PowerNetwork network, bool overwrite = true);
}

internal sealed class GisService : IGisService
{
    public string ToGeoJson(PowerNetwork network, PowerFlowResult? result = null, GeoJsonOptions? options = null) => GeoJsonExporter.Export(network, result, options);
    public GeoJsonImportResult FromGeoJson(string json) => GeoJsonImporter.Import(json);
    public string RenderSvg(PowerNetwork network, PowerFlowResult? result = null, GisSvgOptions? options = null) => GisSvgRenderer.Render(network, result, options);
    public int Layout(PowerNetwork network, GeoPoint center, GeoLayoutOptions? options = null) => GeoLayout.ForceDirected(network, center, options);
    public int AssignLengths(PowerNetwork network, bool overwrite = true) => LineLengths.Assign(network, overwrite: overwrite);
}

/// <summary>DI registration for the GIS module.</summary>
public static class GisBuilderExtensions
{
    /// <summary>Registers <see cref="IGisService"/>.</summary>
    public static ElectricalBuilder AddGis(this ElectricalBuilder b)
    {
        if (b.TryAddModule("GIS")) b.Services.TryAddSingleton<IGisService, GisService>();
        return b;
    }
}
