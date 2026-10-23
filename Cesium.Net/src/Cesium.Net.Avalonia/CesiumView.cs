using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CesiumNet.Interop;

namespace CesiumNet.Avalonia;

/// <summary>
/// An Avalonia control that hosts a CesiumJS viewer in the platform's native WebView
/// (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux).
/// </summary>
/// <remarks>
/// Native WebViews are drawn by the OS above Avalonia content, so place overlays (toolbars, HUDs)
/// next to the control rather than on top of it.
/// </remarks>
/// <example><code>
/// &lt;cesium:CesiumView x:Name="Globe" Ready="OnGlobeReady" /&gt;
///
/// private async void OnGlobeReady(object? s, CesiumReadyEventArgs e)
///     =&gt; await e.Controller.Camera.FlyToAsync(106.8272, -6.1754, 25_000);
/// </code></example>
public class CesiumView : Decorator
{
    private readonly NativeWebView _webView;
    private readonly NativeTransport _transport;
    private bool _started;
    private bool _navigated;

    /// <summary>Raised once CesiumJS has loaded and the viewer is ready.</summary>
    public event EventHandler<CesiumReadyEventArgs>? Ready;

    /// <summary>Raised if CesiumJS could not be loaded (e.g. no network access to the CDN).</summary>
    public event EventHandler<CesiumError>? LoadFailed;

    public CesiumView()
    {
        _transport = new NativeTransport(this);
        Controller = new CesiumController(_transport);
        _webView = new NativeWebView { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16)) };
        _webView.WebMessageReceived += OnWebMessage;
        _webView.EnvironmentRequested += (_, e) =>
        {
#if DEBUG
            e.EnableDevTools = true;
#endif
        };
        Child = _webView;
    }

    /// <summary>The viewer API. Available immediately; calls are queued until the viewer is ready.</summary>
    public CesiumController Controller { get; }

    /// <summary>Viewer creation options. Set before the control is attached to a window.</summary>
    public CesiumViewerOptions Options { get; set; } = new();

    /// <summary>True once CesiumJS has loaded.</summary>
    public bool IsReady => Controller.IsReady;

    /// <summary>The underlying native WebView (advanced scenarios: dev tools, printing, cookies).</summary>
    public NativeWebView WebView => _webView;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_navigated) return;
        _navigated = true;
        _webView.Navigate(CesiumLocalServer.Shared.BaseUrl);
    }

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Body)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.Body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                // Some engines deliver the JSON double-encoded.
                using var inner = JsonDocument.Parse(root.GetString()!);
                HandleMessage(inner.RootElement);
            }
            else HandleMessage(root);
        }
        catch (JsonException) { /* not ours */ }
    }

    private void HandleMessage(JsonElement msg)
    {
        switch (msg.GetProperty("type").GetString())
        {
            case "loaded":
                if (_started) return; // page reload: viewer state is gone; keep the first session
                _started = true;
                var optionsLiteral = CesiumScripts.ToJsLiteral(CesiumController.SerializeOptions(Options));
                _ = _webView.InvokeScript($"window.__cesiumNetStart({optionsLiteral})");
                break;
            case "created":
                _transport.ViewerId = msg.GetProperty("viewerId").GetString();
                Controller.HandleHostEvent("ready", null);
                Ready?.Invoke(this, new CesiumReadyEventArgs(Controller));
                break;
            case "failed":
                var error = msg.TryGetProperty("error", out var err) ? err.GetString() ?? "Unknown error" : "Unknown error";
                Controller.HandleHostFailure(new InvalidOperationException(error));
                LoadFailed?.Invoke(this, new CesiumError { Message = error, Source = "load" });
                break;
            case "event":
                var name = msg.GetProperty("name").GetString();
                if (name is null or "ready") return;
                var data = msg.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                Controller.HandleHostEvent(name, data);
                break;
            case "result":
                _transport.Complete(msg);
                break;
        }
    }

    /// <summary>Bridges <see cref="CesiumController"/> calls to the WebView via script injection + posted results.</summary>
    private sealed class NativeTransport(CesiumView owner) : ICesiumTransport
    {
        private readonly ConcurrentDictionary<long, (string Method, TaskCompletionSource<string?> Tcs)> _pending = new();
        private long _nextCallId;

        public string? ViewerId { get; set; }

        public async ValueTask<string?> InvokeAsync(string method, string argsJson, CancellationToken cancellationToken = default)
        {
            var viewerId = ViewerId ?? throw new InvalidOperationException("The Cesium viewer is not ready yet.");
            var callId = Interlocked.Increment(ref _nextCallId);
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[callId] = (method, tcs);
            var script = string.Concat(
                "CesiumNet.dispatchFromHost(",
                CesiumScripts.ToJsLiteral(viewerId), ",",
                callId.ToString(System.Globalization.CultureInfo.InvariantCulture), ",",
                CesiumScripts.ToJsLiteral(method), ",",
                CesiumScripts.ToJsLiteral(argsJson), ")");
            try
            {
                if (Dispatcher.UIThread.CheckAccess()) await owner._webView.InvokeScript(script);
                else await Dispatcher.UIThread.InvokeAsync(() => owner._webView.InvokeScript(script));
                using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                return await tcs.Task.ConfigureAwait(true);
            }
            finally
            {
                _pending.TryRemove(callId, out _);
            }
        }

        public void Complete(JsonElement msg)
        {
            var callId = msg.GetProperty("callId").GetInt64();
            if (!_pending.TryRemove(callId, out var entry)) return;
            var (method, tcs) = entry;
            if (msg.GetProperty("ok").GetBoolean())
            {
                tcs.TrySetResult(msg.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
            }
            else
            {
                tcs.TrySetException(new CesiumInteropException(method, msg.TryGetProperty("error", out var e) ? e.GetString() ?? "" : ""));
            }
        }
    }
}

/// <summary>Event data for <see cref="CesiumView.Ready"/>.</summary>
public sealed class CesiumReadyEventArgs(CesiumController controller) : EventArgs
{
    public CesiumController Controller { get; } = controller;
}
