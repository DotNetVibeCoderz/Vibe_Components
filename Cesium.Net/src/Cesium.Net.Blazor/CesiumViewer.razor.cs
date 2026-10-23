using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CesiumNet.Blazor;

/// <summary>
/// Hosts a CesiumJS viewer. Use <see cref="OnReady"/> (or <see cref="Controller"/>) to drive it from C#.
/// </summary>
/// <example><code>
/// &lt;CesiumViewer Style="height:600px" OnReady="OnReady" OnLeftClick="OnClick" /&gt;
/// @code {
///     async Task OnReady(CesiumController cesium) =&gt; await cesium.Camera.FlyToAsync(106.8272, -6.1754, 25_000);
/// }
/// </code></example>
public sealed partial class CesiumViewer : ComponentBase, IAsyncDisposable
{
    private readonly DeferredTransport _transport = new();
    private ElementReference _container;
    private IJSObjectReference? _module;
    private DotNetObjectReference<CesiumViewer>? _selfRef;
    private string? _viewerId;
    private bool _ready;
    private bool _disposed;
    private string? _error;

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    /// <summary>Viewer creation options (read once, on first render).</summary>
    [Parameter] public CesiumViewerOptions? Options { get; set; }
    [Parameter] public string? Class { get; set; }
    [Parameter] public string? Style { get; set; }
    /// <summary>Overlay content rendered above the globe (HUDs, toolbars, legends).</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }
    /// <summary>Shown while CesiumJS loads.</summary>
    [Parameter] public RenderFragment? LoadingContent { get; set; }
    /// <summary>Shown if CesiumJS fails to load.</summary>
    [Parameter] public RenderFragment<string>? ErrorContent { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    [Parameter] public EventCallback<CesiumController> OnReady { get; set; }
    [Parameter] public EventCallback<PickResult> OnLeftClick { get; set; }
    [Parameter] public EventCallback<PickResult> OnRightClick { get; set; }
    [Parameter] public EventCallback<PickResult> OnDoubleClick { get; set; }
    [Parameter] public EventCallback<PickResult> OnMouseMove { get; set; }
    [Parameter] public EventCallback<CameraState> OnCameraChanged { get; set; }
    [Parameter] public EventCallback<string?> OnSelectedEntityChanged { get; set; }
    [Parameter] public EventCallback<ClockState> OnClockTick { get; set; }
    [Parameter] public EventCallback<DrawResult> OnDrawCompleted { get; set; }
    [Parameter] public EventCallback<EntityEditedResult> OnEntityEdited { get; set; }
    [Parameter] public EventCallback<CesiumError> OnError { get; set; }

    /// <summary>The viewer API. Available immediately; calls are queued until the viewer is ready.</summary>
    public CesiumController Controller { get; }

    /// <summary>True once CesiumJS has loaded.</summary>
    public bool IsReady => _ready;

    public CesiumViewer()
    {
        Controller = new CesiumController(_transport);
        Controller.LeftClick += (_, e) => Fire(OnLeftClick, e);
        Controller.RightClick += (_, e) => Fire(OnRightClick, e);
        Controller.DoubleClick += (_, e) => Fire(OnDoubleClick, e);
        Controller.MouseMove += (_, e) => Fire(OnMouseMove, e);
        Controller.CameraChanged += (_, e) => Fire(OnCameraChanged, e);
        Controller.SelectedEntityChanged += (_, e) => Fire(OnSelectedEntityChanged, e);
        Controller.ClockTick += (_, e) => Fire(OnClockTick, e);
        Controller.DrawCompleted += (_, e) => Fire(OnDrawCompleted, e);
        Controller.EntityEdited += (_, e) => Fire(OnEntityEdited, e);
        Controller.Error += (_, e) => Fire(OnError, e);
    }

    private static void Fire<T>(EventCallback<T> callback, T value)
    {
        if (callback.HasDelegate) _ = callback.InvokeAsync(value);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        var options = Options ?? new CesiumViewerOptions();
        if (Services.GetService(typeof(CesiumNetBlazorOptions)) is CesiumNetBlazorOptions defaults)
        {
            options.IonAccessToken ??= defaults.IonAccessToken;
            options.CesiumBaseUrl ??= defaults.CesiumBaseUrl;
        }

        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Cesium.Net.Blazor/cesium-net-blazor.js");
            _selfRef = DotNetObjectReference.Create(this);
            _viewerId = await _module.InvokeAsync<string>("create", _container, CesiumController.SerializeOptions(options), _selfRef);
            if (_disposed) { await DestroyAsync(); return; }
            _transport.Inner = new BlazorCesiumTransport(_module, _viewerId);
            _ready = true;
            // The JS side raised "ready" before the transport was attached; signal it now.
            Controller.HandleHostEvent("ready", null);
            await OnReady.InvokeAsync(Controller);
            StateHasChanged();
        }
        catch (JSDisconnectedException) { }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException)
        {
            _error = ex.Message;
            Controller.HandleHostFailure(ex);
            StateHasChanged();
        }
    }

    /// <summary>Receives events from JavaScript.</summary>
    [JSInvokable]
    public Task OnCesiumEvent(string name, string? json)
    {
        // "ready" is signalled once the transport is attached (see OnAfterRenderAsync).
        if (name != "ready") Controller.HandleHostEvent(name, json);
        return Task.CompletedTask;
    }

    private async ValueTask DestroyAsync()
    {
        try
        {
            if (_module is not null)
            {
                if (_viewerId is not null) await _module.InvokeVoidAsync("destroy", _viewerId);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _module = null;
            _selfRef?.Dispose();
            _selfRef = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DestroyAsync();
    }

    /// <summary>Forwards calls once the real transport exists.</summary>
    private sealed class DeferredTransport : Interop.ICesiumTransport
    {
        public Interop.ICesiumTransport? Inner { get; set; }

        public ValueTask<string?> InvokeAsync(string method, string argsJson, CancellationToken cancellationToken = default)
            => Inner?.InvokeAsync(method, argsJson, cancellationToken)
               ?? throw new InvalidOperationException("The Cesium viewer is not ready yet.");
    }
}
