using Microsoft.Extensions.DependencyInjection;

namespace CesiumNet.Blazor;

/// <summary>Application-wide defaults applied to every <see cref="CesiumViewer"/> (per-viewer options win).</summary>
public sealed class CesiumNetBlazorOptions
{
    /// <summary>Cesium ion access token used when a viewer does not set one.</summary>
    public string? IonAccessToken { get; set; }

    /// <summary>Self-hosted CesiumJS build URL (e.g. <c>/cesium/</c>). Defaults to the jsDelivr CDN.</summary>
    public string? CesiumBaseUrl { get; set; }
}

public static class CesiumNetServiceCollectionExtensions
{
    /// <summary>Registers Cesium.Net defaults (optional — components also work without registration).</summary>
    /// <example><code>builder.Services.AddCesiumNet(o =&gt; o.IonAccessToken = builder.Configuration["Cesium:IonToken"]);</code></example>
    public static IServiceCollection AddCesiumNet(this IServiceCollection services, Action<CesiumNetBlazorOptions>? configure = null)
    {
        var options = new CesiumNetBlazorOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        return services;
    }
}
