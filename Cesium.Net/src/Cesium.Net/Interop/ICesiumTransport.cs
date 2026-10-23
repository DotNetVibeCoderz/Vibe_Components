namespace CesiumNet.Interop;

/// <summary>
/// The bridge between <see cref="CesiumController"/> and a CesiumJS instance running in a browser engine.
/// Implemented by each UI host (Blazor uses <c>IJSRuntime</c>; Avalonia uses a native WebView).
/// </summary>
/// <remarks>
/// The protocol is deliberately tiny: every call is a method name plus a JSON array of arguments,
/// and every result is a JSON string (or null). Events flow back through
/// <see cref="CesiumController.HandleHostEvent"/>.
/// </remarks>
public interface ICesiumTransport
{
    /// <summary>Invokes a bridge method (see <c>cesium-net.js</c>) and returns its JSON result.</summary>
    ValueTask<string?> InvokeAsync(string method, string argsJson, CancellationToken cancellationToken = default);
}

/// <summary>Access to the shared JavaScript bridge (<c>cesium-net.js</c>) shipped with Cesium.Net.</summary>
public static class CesiumScripts
{
    private static string? s_bridge;

    /// <summary>The bridge script source, loaded once from the embedded resource.</summary>
    public static string BridgeScript => s_bridge ??= Load("CesiumNet.Scripts.cesium-net.js");

    /// <summary>Default CDN base URL for the configured CesiumJS version.</summary>
    public static string DefaultCesiumBaseUrl(string? version = null)
        => $"https://cdn.jsdelivr.net/npm/cesium@{version ?? CesiumViewerOptions.DefaultCesiumVersion}/Build/Cesium/";

    /// <summary>Encodes a string as a JavaScript string literal (safe to splice into script source).</summary>
    public static string ToJsLiteral(string? value)
        => System.Text.Json.JsonSerializer.Serialize(value, CesiumJsonContext.Default.String);

    private static string Load(string name)
    {
        using var stream = typeof(CesiumScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
