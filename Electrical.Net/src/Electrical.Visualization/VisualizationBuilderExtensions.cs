using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Visualization;

/// <summary>Injectable facade over the static renderers (for apps that resolve services from DI).</summary>
public interface IDiagramRenderer
{
    /// <summary>Single-line diagram SVG with an optional power-flow overlay.</summary>
    string SingleLine(PowerNetwork network, PowerFlowResult? result = null, SldOptions? options = null);
    /// <summary>Replaces theme CSS variables with literal colors (for non-browser renderers).</summary>
    string Resolve(string svg, bool dark);
}

internal sealed class DiagramRenderer : IDiagramRenderer
{
    public string SingleLine(PowerNetwork network, PowerFlowResult? result = null, SldOptions? options = null) => SldRenderer.Render(network, result, options);
    public string Resolve(string svg, bool dark) => SvgTheme.Resolve(svg, dark);
}

public static class VisualizationBuilderExtensions
{
    /// <summary>Registers <see cref="IDiagramRenderer"/>.</summary>
    public static ElectricalBuilder AddVisualization(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Visualization")) b.Services.TryAddSingleton<IDiagramRenderer, DiagramRenderer>();
        return b;
    }
}
