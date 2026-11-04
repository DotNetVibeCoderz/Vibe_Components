using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using OpenSceneGraph.Net.Manipulators;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Rendering.OpenGL;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;
using Silk.NET.OpenGL;
using AvKey = Avalonia.Input.Key;
using OsgButtons = OpenSceneGraph.Net.Manipulators.MouseButtons;
using OsgMods = OpenSceneGraph.Net.Manipulators.KeyModifiers;

namespace OpenSceneGraph.Net.Avalonia;

/// <summary>
/// Avalonia control that renders an OpenSceneGraph.Net <see cref="Net.Viewer"/> with OpenGL / OpenGL ES (ANGLE).
/// <code>
/// &lt;osg:OsgView x:Name="View" /&gt;
/// View.Scene = myScene;
/// </code>
/// Input is forwarded to the viewer's event queue, so manipulators and <see cref="IEventHandler"/>s work as in OSG.
/// </summary>
public class OsgView : OpenGlControlBase
{
    public static readonly StyledProperty<Node?> SceneProperty = AvaloniaProperty.Register<OsgView, Node?>(nameof(Scene));
    public static readonly StyledProperty<bool> ContinuousRenderingProperty = AvaloniaProperty.Register<OsgView, bool>(nameof(ContinuousRendering), true);

    private GL? _gl;
    private GlRenderer? _renderer;
    private OsgButtons _buttons;
    private Viewer _viewer = new();
    private TaskCompletionSource<Image>? _capture;

    static OsgView()
    {
        FocusableProperty.OverrideDefaultValue<OsgView>(true);
        SceneProperty.Changed.AddClassHandler<OsgView>((v, e) => { v._viewer.SceneData = e.NewValue as Node; v.RequestNextFrameRendering(); });
    }

    /// <summary>The viewer driving this view; replace it to share a viewer configuration.</summary>
    public Viewer Viewer
    {
        get => _viewer;
        set { _viewer = value ?? throw new ArgumentNullException(nameof(value)); RequestNextFrameRendering(); }
    }

    public Node? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    /// <summary>Render every display refresh (default) or only when <see cref="Invalidate"/> / input requests it.</summary>
    public bool ContinuousRendering
    {
        get => GetValue(ContinuousRenderingProperty);
        set => SetValue(ContinuousRenderingProperty, value);
    }

    /// <summary>Name of the active renderer (available after the first frame).</summary>
    public string? RendererName => _renderer?.Name;

    /// <summary>Raised after each frame with that frame's statistics (on the UI thread).</summary>
    public event Action<OsgView, RenderStats>? FrameRendered;

    /// <summary>Raised when OpenGL initialisation fails (e.g. no GL support in a VM).</summary>
    public event Action<OsgView, Exception>? RenderingFailed;

    public void Invalidate() => RequestNextFrameRendering();

    /// <summary>
    /// Reads back the next rendered frame (RGBA8, rows top to bottom, framebuffer resolution).
    /// Useful for screenshots and documentation; completes on the UI thread after the frame is drawn.
    /// </summary>
    public Task<Image> CaptureNextFrameAsync()
    {
        var tcs = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _capture, tcs)?.TrySetCanceled();
        RequestNextFrameRendering();
        return tcs.Task;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        try
        {
            _gl = GL.GetApi(gl.GetProcAddress);
            bool es = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
            _renderer = new GlRenderer(_gl, es);
        }
        catch (Exception ex)
        {
            _renderer = null;
            RenderingFailed?.Invoke(this, ex);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer?.Dispose();
        _renderer = null;
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is null) { Interlocked.Exchange(ref _capture, null)?.TrySetException(new InvalidOperationException("OpenGL renderer is not available.")); return; }
        double scale = VisualRoot?.RenderScaling ?? 1;
        // same rounding Avalonia uses for the control's framebuffer (PixelSize.FromSize = ceiling)
        var px = PixelSize.FromSize(Bounds.Size, scale);
        int w = System.Math.Max(1, px.Width);
        int h = System.Math.Max(1, px.Height);
        _viewer.SetViewport(w, h);
        var list = _viewer.Frame();
        _renderer.TargetFramebuffer = (uint)fb;
        _renderer.SimulationTime = _viewer.FrameStamp.SimulationTime;
        _renderer.Render(list, w, h);
        if (Interlocked.Exchange(ref _capture, null) is { } capture) ReadBack(capture, (uint)fb, w, h);
        FrameRendered?.Invoke(this, list.Stats);
        if (ContinuousRendering) RequestNextFrameRendering();
    }

    private unsafe void ReadBack(TaskCompletionSource<Image> tcs, uint fb, int w, int h)
    {
        try
        {
            var gl = _gl!;
            var pixels = new byte[w * h * 4];
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fb);
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            fixed (byte* p = pixels) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            // GL rows are bottom-up; Image rows are top-down
            var flipped = new byte[pixels.Length];
            int stride = w * 4;
            for (int y = 0; y < h; y++) System.Buffer.BlockCopy(pixels, (h - 1 - y) * stride, flipped, y * stride, stride);
            for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255; // the control composites opaque
            tcs.TrySetResult(new Image(w, h, flipped));
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }

    // ------------------------------------------------------------------ input
    private (float X, float Y, int W, int H) Pixel(PointerEventArgs e)
    {
        double scale = VisualRoot?.RenderScaling ?? 1;
        var p = e.GetPosition(this);
        return ((float)(p.X * scale), (float)(p.Y * scale), (int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
    }

    private static OsgButtons Buttons(PointerPointProperties p) =>
        (p.IsLeftButtonPressed ? OsgButtons.Left : 0) | (p.IsMiddleButtonPressed ? OsgButtons.Middle : 0) | (p.IsRightButtonPressed ? OsgButtons.Right : 0);

    private static OsgMods Mods(global::Avalonia.Input.KeyModifiers m) =>
        (m.HasFlag(global::Avalonia.Input.KeyModifiers.Shift) ? OsgMods.Shift : 0) |
        (m.HasFlag(global::Avalonia.Input.KeyModifiers.Control) ? OsgMods.Control : 0) |
        (m.HasFlag(global::Avalonia.Input.KeyModifiers.Alt) ? OsgMods.Alt : 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var (x, y, w, h) = Pixel(e);
        _buttons = Buttons(e.GetCurrentPoint(this).Properties);
        var type = e.ClickCount >= 2 ? InputEventType.DoubleClick : InputEventType.Push;
        _viewer.EventQueue.Push(new InputEvent(type, x, y, _buttons, Modifiers: Mods(e.KeyModifiers), Width: w, Height: h));
        e.Pointer.Capture(this);
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (x, y, w, h) = Pixel(e);
        _viewer.EventQueue.Push(new InputEvent(InputEventType.Release, x, y, _buttons, Modifiers: Mods(e.KeyModifiers), Width: w, Height: h));
        _buttons = OsgButtons.None;
        e.Pointer.Capture(null);
        RequestNextFrameRendering();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (x, y, w, h) = Pixel(e);
        var b = Buttons(e.GetCurrentPoint(this).Properties);
        _viewer.EventQueue.Push(new InputEvent(b != OsgButtons.None ? InputEventType.Drag : InputEventType.Move, x, y, b, Modifiers: Mods(e.KeyModifiers), Width: w, Height: h));
        if (b != OsgButtons.None) RequestNextFrameRendering();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var (x, y, w, h) = Pixel(e);
        _viewer.EventQueue.Push(new InputEvent(InputEventType.Scroll, x, y, ScrollDelta: (float)e.Delta.Y, Modifiers: Mods(e.KeyModifiers), Width: w, Height: h));
        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (MapKey(e.Key) is { } k) { _viewer.EventQueue.Push(new InputEvent(InputEventType.KeyDown, Key: k, Modifiers: Mods(e.KeyModifiers))); e.Handled = true; }
        RequestNextFrameRendering();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (MapKey(e.Key) is { } k) _viewer.EventQueue.Push(new InputEvent(InputEventType.KeyUp, Key: k, Modifiers: Mods(e.KeyModifiers)));
    }

    /// <summary>Maps Avalonia keys to the DOM-style names used by the manipulators ("w", " ", "ArrowUp", "Shift"...).</summary>
    public static string? MapKey(AvKey key) => key switch
    {
        >= AvKey.A and <= AvKey.Z => ((char)('a' + (key - AvKey.A))).ToString(),
        >= AvKey.D0 and <= AvKey.D9 => ((char)('0' + (key - AvKey.D0))).ToString(),
        AvKey.Space => " ",
        AvKey.Up => "ArrowUp",
        AvKey.Down => "ArrowDown",
        AvKey.Left => "ArrowLeft",
        AvKey.Right => "ArrowRight",
        AvKey.LeftShift or AvKey.RightShift => "Shift",
        AvKey.Escape => "Escape",
        AvKey.Enter => "Enter",
        AvKey.Home => "Home",
        _ => null,
    };
}
