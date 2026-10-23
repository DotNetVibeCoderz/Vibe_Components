using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CesiumNet.Interop;

namespace CesiumNet;

/// <summary>
/// The host-agnostic API for one CesiumJS viewer. Obtain it from the Blazor <c>CesiumViewer</c>
/// component or the Avalonia <c>CesiumView</c> control — the same code runs on web and desktop.
/// </summary>
/// <remarks>
/// Calls made before the viewer is ready are queued until it is. Events are raised on the
/// host's synchronization context (the Blazor renderer or the Avalonia UI thread).
/// </remarks>
public sealed class CesiumController
{
    private readonly ICesiumTransport _transport;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CesiumController(ICesiumTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Camera = new CameraApi(this);
        Entities = new EntitiesApi(this);
        Imagery = new ImageryApi(this);
        Terrain = new TerrainApi(this);
        Tilesets = new TilesetsApi(this);
        DataSources = new DataSourcesApi(this);
        Primitives = new PrimitivesApi(this);
        Clock = new ClockApi(this);
        Scene = new SceneApi(this);
        Drawing = new DrawingApi(this);
    }

    /// <summary>Camera control: fly-to, look-at, orbit, first-person flight, state.</summary>
    public CameraApi Camera { get; }
    /// <summary>Entities: points, billboards, labels, shapes, volumes, models, time-dynamic tracks.</summary>
    public EntitiesApi Entities { get; }
    /// <summary>Imagery layers (OSM, satellite, WMS, WMTS, TMS, XYZ, Bing, ion…).</summary>
    public ImageryApi Imagery { get; }
    /// <summary>Terrain provider, exaggeration, height sampling, profiles and clipping.</summary>
    public TerrainApi Terrain { get; }
    /// <summary>OGC 3D Tiles: buildings, photogrammetry, BIM, point clouds.</summary>
    public TilesetsApi Tilesets { get; }
    /// <summary>GeoJSON, TopoJSON, KML/KMZ, CZML and GPX.</summary>
    public DataSourcesApi DataSources { get; }
    /// <summary>High-volume GPU primitives (hundreds of thousands of points).</summary>
    public PrimitivesApi Primitives { get; }
    /// <summary>Simulation clock and timeline.</summary>
    public ClockApi Clock { get; }
    /// <summary>Scene mode, lighting, shadows, atmosphere, post-processing, weather, screenshots.</summary>
    public SceneApi Scene { get; }
    /// <summary>Interactive drawing, measuring and shape editing.</summary>
    public DrawingApi Drawing { get; }

    /// <summary>True once CesiumJS has loaded and the viewer is created.</summary>
    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>Completes when the viewer is ready.</summary>
    public Task WhenReady => _ready.Task;

    /// <summary>Raised once when the viewer is ready.</summary>
    public event EventHandler? Ready;
    public event EventHandler<PickResult>? LeftClick;
    public event EventHandler<PickResult>? RightClick;
    public event EventHandler<PickResult>? DoubleClick;
    public event EventHandler<PickResult>? MouseMove;
    /// <summary>Raised when the camera stops moving.</summary>
    public event EventHandler<CameraState>? CameraChanged;
    /// <summary>Raised when the selected entity changes (id, or null when deselected).</summary>
    public event EventHandler<string?>? SelectedEntityChanged;
    public event EventHandler<ClockState>? ClockTick;
    public event EventHandler<DrawResult>? DrawCompleted;
    public event EventHandler? DrawCancelled;
    public event EventHandler<EntityEditedResult>? EntityEdited;
    /// <summary>Number of tiles still loading (0 = idle).</summary>
    public event EventHandler<int>? LoadingProgress;
    public event EventHandler<CesiumError>? Error;

    /// <summary>
    /// Advanced escape hatch: evaluates JavaScript with <c>viewer</c> and <c>Cesium</c> in scope
    /// and returns the JSON-serialized result. Use for CesiumJS features not yet wrapped.
    /// </summary>
    /// <example><code>await cesium.ExecuteScriptAsync("viewer.scene.globe.showGroundAtmosphere = false");</code></example>
    public ValueTask<string?> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
        => CallAsync("eval", JsArgs.Rent().Str(script).ToJson(), cancellationToken);

    /// <summary>Requests a new frame (only needed with request-render mode after <see cref="ExecuteScriptAsync"/>).</summary>
    public ValueTask RequestRenderAsync() => CallVoidAsync("scene.requestRender", JsArgs.Empty);

    /// <summary>Serializes viewer options for a host's <c>create</c> call.</summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static string SerializeOptions(CesiumViewerOptions options)
        => JsonSerializer.Serialize(options, CesiumJsonContext.Default.CesiumViewerOptions);

    /// <summary>Called by hosts when the JavaScript side raises an event.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void HandleHostEvent(string name, string? json)
    {
        switch (name)
        {
            case "ready":
                if (_ready.TrySetResult()) Ready?.Invoke(this, EventArgs.Empty);
                break;
            case "leftClick": Raise(LeftClick, json, CesiumJsonContext.Default.PickResult); break;
            case "rightClick": Raise(RightClick, json, CesiumJsonContext.Default.PickResult); break;
            case "doubleClick": Raise(DoubleClick, json, CesiumJsonContext.Default.PickResult); break;
            case "mouseMove": Raise(MouseMove, json, CesiumJsonContext.Default.PickResult); break;
            case "cameraChanged": Raise(CameraChanged, json, CesiumJsonContext.Default.CameraState); break;
            case "clockTick": Raise(ClockTick, json, CesiumJsonContext.Default.ClockState); break;
            case "drawCompleted": Raise(DrawCompleted, json, CesiumJsonContext.Default.DrawResult); break;
            case "drawCancelled": DrawCancelled?.Invoke(this, EventArgs.Empty); break;
            case "entityEdited": Raise(EntityEdited, json, CesiumJsonContext.Default.EntityEditedResult); break;
            case "selectedEntityChanged":
                SelectedEntityChanged?.Invoke(this, json is null or "null" ? null : JsonSerializer.Deserialize(json, CesiumJsonContext.Default.String));
                break;
            case "loadingProgress":
                LoadingProgress?.Invoke(this, json is null ? 0 : JsonSerializer.Deserialize(json, CesiumJsonContext.Default.Int32));
                break;
            case "error":
                var err = json is null ? new CesiumError { Message = "Unknown error" } : JsonSerializer.Deserialize(json, CesiumJsonContext.Default.CesiumError)!;
                Error?.Invoke(this, err);
                break;
        }
    }

    /// <summary>Called by hosts when the viewer could not be created.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void HandleHostFailure(Exception exception)
    {
        _ready.TrySetException(exception);
        Error?.Invoke(this, new CesiumError { Message = exception.Message, Source = "host" });
    }

    private void Raise<T>(EventHandler<T>? handler, string? json, JsonTypeInfo<T> typeInfo)
    {
        if (handler is null || json is null) return;
        var value = JsonSerializer.Deserialize(json, typeInfo);
        if (value is not null) handler(this, value);
    }

    internal async ValueTask<string?> CallAsync(string method, string argsJson, CancellationToken cancellationToken = default)
    {
        if (!_ready.Task.IsCompleted) await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _transport.InvokeAsync(method, argsJson, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask CallVoidAsync(string method, string argsJson, CancellationToken cancellationToken = default)
        => await CallAsync(method, argsJson, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<T?> CallAsync<T>(string method, string argsJson, JsonTypeInfo<T> resultType, CancellationToken cancellationToken = default)
    {
        var json = await CallAsync(method, argsJson, cancellationToken).ConfigureAwait(false);
        return json is null or "null" ? default : JsonSerializer.Deserialize(json, resultType);
    }

    internal async ValueTask<string> CallForIdAsync(string method, string argsJson, CancellationToken cancellationToken = default)
        => await CallAsync(method, argsJson, CesiumJsonContext.Default.String, cancellationToken).ConfigureAwait(false)
           ?? throw new CesiumInteropException(method, "No id returned.");
}
