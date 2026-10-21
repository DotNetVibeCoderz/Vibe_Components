using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Security;
using Blazor.AI.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blazor.AI.Components.Infrastructure;

/// <summary>
/// Base class for Blazor.AI components: provider selection, busy/error state, cancellation of the previous
/// request when a new one starts, and throttled re-rendering while tokens stream in.
/// </summary>
public abstract class AiComponentBase : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private long _lastRender;

    [Inject] protected IAiClientFactory ClientFactory { get; set; } = default!;

    [Inject] protected AiTextService Ai { get; set; } = default!;

    [Inject] protected BlazorAIJs Js { get; set; } = default!;

    /// <summary>Name of the provider to use. <c>null</c> uses the application default.</summary>
    [Parameter] public string? Provider { get; set; }

    /// <summary>Extra CSS classes for the root element.</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>Any other attributes are splatted onto the root element.</summary>
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Raised when a request fails, in addition to the inline error message.</summary>
    [Parameter] public EventCallback<Exception> OnError { get; set; }

    protected bool IsBusy { get; private set; }

    protected string? Error { get; set; }

    /// <summary>Runs an AI operation with busy state, error capture and cancellation of any operation still running.</summary>
    protected async Task RunAsync(Func<CancellationToken, Task> work)
    {
        CancelCurrent();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Error = null;
        StateHasChanged();
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped by the user or superseded by a newer request.
        }
        catch (JSDisconnectedException)
        {
            return;
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
            await OnError.InvokeAsync(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
                _cts = null;
            }

            cts.Dispose();
        }

        StateHasChanged();
    }

    /// <summary>Stops the operation in progress, if any.</summary>
    protected void CancelCurrent()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Re-renders at most every <paramref name="intervalMs"/> ms (for token streaming).</summary>
    protected void RenderThrottled(int intervalMs = 50)
    {
        var now = Environment.TickCount64;
        if (now - _lastRender >= intervalMs)
        {
            _lastRender = now;
            StateHasChanged();
        }
    }

    protected string CssClass(string baseClass) => string.IsNullOrWhiteSpace(Class) ? baseClass : $"{baseClass} {Class}";

    /// <summary>Turns provider exceptions into one actionable sentence.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AiGuardrailException g => g.Message,
        KeyNotFoundException k => k.Message,
        InvalidOperationException i when i.Message.Contains("ApiKey", StringComparison.Ordinal) || i.Message.Contains("Endpoint", StringComparison.Ordinal) => i.Message,
        HttpRequestException h => $"Could not reach the model endpoint ({h.Message}). Check the endpoint URL and your network.",
        _ when ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) =>
            "The provider rejected the API key (401). Check the key in your configuration.",
        _ when ex.Message.Contains("429", StringComparison.Ordinal) => "The provider is rate limiting requests (429). Wait a moment and try again.",
        _ => $"The request failed: {FirstLine(ex.Message)}",
    };

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline];
    }

    public virtual ValueTask DisposeAsync()
    {
        CancelCurrent();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
