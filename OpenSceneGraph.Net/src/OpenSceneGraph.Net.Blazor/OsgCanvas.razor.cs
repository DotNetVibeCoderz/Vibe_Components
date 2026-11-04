using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Blazor;

/// <summary>
/// Blazor component that runs an OpenSceneGraph.Net <see cref="Net.Viewer"/> in .NET and draws its
/// <see cref="RenderList"/> with a WebGL2 renderer (the same GLSL as the desktop renderer).
/// <code>
/// &lt;OsgCanvas Scene="_scene" Viewer="_viewer" ContinuousRendering="true" OnFrame="stats =&gt; _fps = stats.FramesPerSecond" /&gt;
/// </code>
/// The browser drives the frame loop with <c>requestAnimationFrame</c>; each tick calls back into .NET, which runs
/// <see cref="Net.Viewer.Frame"/> and returns one binary packet (see <see cref="WebGlFrameEncoder"/>). A new frame is
/// never requested while the previous one is still in flight. Pointer, wheel, keyboard and resize events are batched
/// in JavaScript and delivered to <see cref="Net.Viewer.EventQueue"/> with the next frame, in device pixels.
/// Works in Blazor WebAssembly and in interactive server rendering (one round trip per frame).
/// </summary>
public partial class OsgCanvas : ComponentBase, IAsyncDisposable
{
    private const string ModulePath = "./_content/OpenSceneGraph.Net.Blazor/osgCanvas.js";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ElementReference _canvas;
    private IJSObjectReference? _module;
    private IJSObjectReference? _renderer;
    private DotNetObjectReference<OsgCanvas>? _self;
    private readonly WebGlFrameEncoder _encoder = new();
    private Viewer? _ownViewer;
    private Viewer? _active;
    private Node? _appliedScene;
    private bool _continuousApplied = true;
    private int _width, _height;
    private bool _disposed;
    private int _errors;

    /// <summary>Scene root shown by the viewer. Changing it sets <see cref="Net.Viewer.SceneData"/> (and re-homes the camera).</summary>
    [Parameter] public Node? Scene { get; set; }

    /// <summary>Viewer to drive. When null the component creates (and disposes) its own.</summary>
    [Parameter] public Viewer? Viewer { get; set; }

    /// <summary>Render on every animation frame (default) or only after input / <see cref="Invalidate"/>.</summary>
    [Parameter] public bool ContinuousRendering { get; set; } = true;

    /// <summary>Called after every frame with that frame's statistics. Plain delegate: it does not re-render the parent.</summary>
    [Parameter] public Action<RenderStats>? OnFrame { get; set; }

    /// <summary>Raised once WebGL2 is initialised.</summary>
    [Parameter] public EventCallback<WebGlInfo> OnReady { get; set; }

    /// <summary>Raised when WebGL2 is unavailable or a frame fails.</summary>
    [Parameter] public EventCallback<string> OnError { get; set; }

    /// <summary>Upper bound for devicePixelRatio scaling (keeps fill rate in check on 3x displays).</summary>
    [Parameter] public double MaxPixelRatio { get; set; } = 2;

    /// <summary>Request an antialiased (MSAA) default framebuffer.</summary>
    [Parameter] public bool Antialias { get; set; } = true;

    [Parameter] public string? Class { get; set; }
    [Parameter] public string? Style { get; set; }
    [Parameter] public string AriaLabel { get; set; } = "3D view";
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>The viewer actually being driven (the <see cref="Viewer"/> parameter or the internal one).</summary>
    public Viewer CurrentViewer => _active ??= Viewer ?? (_ownViewer ??= new Viewer());

    /// <summary>Features this renderer implements (no custom GLSL programs, wireframe is emulated with lines).</summary>
    public static RendererCapabilities Capabilities =>
        RendererCapabilities.Lighting | RendererCapabilities.Textures | RendererCapabilities.Shadows | RendererCapabilities.Fog |
        RendererCapabilities.RenderToTexture | RendererCapabilities.PostProcessing | RendererCapabilities.Skinning;

    /// <summary>GPU / context information, available after <see cref="OnReady"/>.</summary>
    public WebGlInfo? Info { get; private set; }

    /// <summary>Renderer name, e.g. "WebGL2 (ANGLE ...)"; null until initialised.</summary>
    public string? RendererName => Info is { } i ? $"WebGL2 ({i.Renderer})" : null;

    /// <summary>Draw calls issued by the GPU side for the previous frame.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Milliseconds spent in <see cref="Net.Viewer.Frame"/> (event, update, cull) for the last frame.</summary>
    public double FrameMs { get; private set; }

    /// <summary>Milliseconds spent packing the last frame for WebGL.</summary>
    public double EncodeMs { get; private set; }

    /// <summary>Milliseconds the JavaScript renderer spent issuing the previous frame's WebGL calls.</summary>
    public double RenderMs { get; private set; }

    /// <summary>Size of the last frame packet in bytes.</summary>
    public int PacketBytes { get; private set; }

    /// <summary>Statistics of the most recent frame.</summary>
    public RenderStats? LastStats { get; private set; }

    /// <summary>Bytes of geometry/texture data uploaded with the most recent frame.</summary>
    public int LastUploadBytes => _encoder.LastUploadBytes;

    /// <summary>Geometries currently cached on the GPU.</summary>
    public int ResidentGeometries => _encoder.ResidentGeometries;

    /// <summary>Raised after each frame (same thread as the component).</summary>
    public event Action<OsgCanvas, RenderStats>? FrameRendered;

    /// <summary>Raised when a frame throws (for example from an update callback).</summary>
    public event Action<OsgCanvas, Exception>? RenderingFailed;

    protected override void OnParametersSet()
    {
        var v = Viewer ?? (_ownViewer ??= new Viewer());
        if (!ReferenceEquals(v, _active))
        {
            if (_active is not null) _encoder.EvictUnusedOnNextFrame();
            _active = v;
            _appliedScene = null;
            if (_width > 0) v.SetViewport(_width, _height);
        }
        if (Scene is not null && !ReferenceEquals(Scene, _appliedScene))
        {
            if (_appliedScene is not null) _encoder.EvictUnusedOnNextFrame();
            // assigning SceneData re-homes the camera, so leave an already attached scene alone
            if (!ReferenceEquals(v.SceneData, Scene)) v.SceneData = Scene;
            _appliedScene = Scene;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_renderer is not null && _continuousApplied != ContinuousRendering)
        {
            _continuousApplied = ContinuousRendering;
            await _renderer.InvokeVoidAsync("setContinuous", ContinuousRendering);
        }
        if (_renderer is not null) await _renderer.InvokeVoidAsync("invalidate");
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            _self = DotNetObjectReference.Create(this);
            var options = new Dictionary<string, object>
            {
                ["continuous"] = ContinuousRendering,
                ["maxPixelRatio"] = MaxPixelRatio,
                ["antialias"] = Antialias,
            };
            _renderer = await _module.InvokeAsync<IJSObjectReference>("create", _canvas, _self, ShaderSources(), options);
            _continuousApplied = ContinuousRendering;
            Info = await _renderer.InvokeAsync<WebGlInfo>("getInfo");
            _encoder.MaxShadowMapSize = System.Math.Min(4096, Info.MaxTextureSize);
            if (_disposed) { await DisposeJsAsync(); return; }
            await _renderer.InvokeVoidAsync("start");
            await OnReady.InvokeAsync(Info);
        }
        catch (JSException ex)
        {
            await OnError.InvokeAsync(ex.Message);
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
    }

    private static Dictionary<string, string> ShaderSources()
    {
        string h = ShaderLibrary.HeaderEs;
        return new()
        {
            ["mainVs"] = h + ShaderLibrary.MainVertex,
            ["mainFs"] = h + ShaderLibrary.MainFragment,
            ["depthVs"] = h + ShaderLibrary.DepthVertex,
            ["depthFs"] = h + ShaderLibrary.DepthFragment,
            ["fullscreenVs"] = h + ShaderLibrary.FullscreenVertex,
            ["backgroundFs"] = h + ShaderLibrary.BackgroundFragment,
            ["brightFs"] = h + ShaderLibrary.BrightFragment,
            ["blurFs"] = h + ShaderLibrary.BlurFragment,
            ["compositeFs"] = h + ShaderLibrary.CompositeFragment,
        };
    }

    /// <summary>Requests a frame (useful with <see cref="ContinuousRendering"/> = false after changing the scene).</summary>
    public async ValueTask Invalidate()
    {
        if (_renderer is not null) await _renderer.InvokeVoidAsync("invalidate");
    }

    /// <summary>
    /// Called by the JavaScript frame loop. Applies batched input, runs one viewer frame and returns the packet to draw.
    /// <paramref name="events"/> holds 7 numbers per event: type, x, y, buttons, scroll delta, modifiers, key index into <paramref name="keys"/> (-1 for none).
    /// </summary>
    [JSInvokable]
    public byte[]? RenderFrame(int width, int height, int flags, int drawCalls, double renderMs, double[]? events, string[]? keys)
    {
        if (_disposed) return null;
        var viewer = CurrentViewer;
        if ((flags & 1) != 0) _encoder.Reset();
        DrawCalls = drawCalls;
        RenderMs = renderMs;
        width = System.Math.Max(1, width);
        height = System.Math.Max(1, height);
        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            viewer.SetViewport(width, height);
            viewer.EventQueue.Push(new InputEvent(InputEventType.Resize, Width: width, Height: height));
        }
        else viewer.SetViewport(width, height);

        if (events is { Length: >= 7 })
        {
            for (int i = 0; i + 6 < events.Length; i += 7)
            {
                int k = (int)events[i + 6];
                viewer.EventQueue.Push(new InputEvent(
                    (InputEventType)(int)events[i],
                    (float)events[i + 1], (float)events[i + 2],
                    (MouseButtons)(int)events[i + 3],
                    (float)events[i + 4],
                    keys is not null && k >= 0 && k < keys.Length ? keys[k] : null,
                    (KeyModifiers)(int)events[i + 5],
                    width, height));
            }
        }

        try
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var list = viewer.Frame();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            var packet = _encoder.Encode(list, width, height, viewer.FrameStamp.SimulationTime);
            FrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            EncodeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
            PacketBytes = packet.Length;
            LastStats = list.Stats;
            OnFrame?.Invoke(list.Stats);
            FrameRendered?.Invoke(this, list.Stats);
            return packet;
        }
        catch (Exception ex)
        {
            if (_errors++ < 3)
            {
                RenderingFailed?.Invoke(this, ex);
                _ = OnError.InvokeAsync(ex.Message);
            }
            throw;
        }
    }

    private async ValueTask DisposeJsAsync()
    {
        try
        {
            if (_renderer is not null) { await _renderer.InvokeVoidAsync("dispose"); await _renderer.DisposeAsync(); }
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
        _renderer = null;
        _module = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeJsAsync();
        _self?.Dispose();
        _ownViewer?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>WebGL2 context information reported by the browser.</summary>
public sealed record WebGlInfo(
    string Renderer,
    string Vendor,
    string Version,
    string ShadingLanguage,
    int MaxTextureSize,
    bool FloatColorBuffers,
    bool Antialias,
    double PixelRatio);
