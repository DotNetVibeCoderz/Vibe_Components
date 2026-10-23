using CesiumNet.Interop;
using Microsoft.JSInterop;

namespace CesiumNet.Blazor;

/// <summary><see cref="ICesiumTransport"/> over Blazor JS interop (works for Server and WebAssembly).</summary>
internal sealed class BlazorCesiumTransport(IJSObjectReference module, string viewerId) : ICesiumTransport
{
    private const string LargePrefix = "\u0001cesium-net-large:";
    private const long MaxLargeResultBytes = 256L * 1024 * 1024;

    public string ViewerId { get; } = viewerId;

    public async ValueTask<string?> InvokeAsync(string method, string argsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await module.InvokeAsync<string?>("dispatch", cancellationToken, ViewerId, method, argsJson).ConfigureAwait(false);
            if (result is not null && result.StartsWith(LargePrefix, StringComparison.Ordinal))
            {
                // Large results (e.g. screenshots) are streamed: inline they would exceed the
                // Blazor Server SignalR message limit and drop the circuit.
                var stream = await module.InvokeAsync<IJSStreamReference>("takeLargeResult", cancellationToken, result[LargePrefix.Length..]).ConfigureAwait(false);
                await using var data = await stream.OpenReadStreamAsync(MaxLargeResultBytes, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(data, System.Text.Encoding.UTF8);
                result = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
        catch (JSException ex)
        {
            throw new CesiumInteropException(method, ex.Message);
        }
    }
}
