using System.Globalization;
using System.Net;
using System.Text;
using GDAL.Net.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GDAL.Net.Blazor;

/// <summary>
/// Interactive map for Blazor Server. Rasters are rendered server-side (only the visible window, at screen resolution,
/// through <see cref="RasterRenderer"/>) and streamed as PNG; vectors are drawn as SVG. Panning and zooming happen
/// instantly on the client as a CSS transform and are committed to the server when the gesture settles.
/// </summary>
public partial class GdalMap
{
    private const string ModulePath = "./_content/Gravicode.GDAL.Net.Blazor/gdalMap.js";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>Raster layers, bottom to top.</summary>
    [Parameter] public IReadOnlyList<MapRaster>? Rasters { get; set; }

    /// <summary>Vector layers, bottom to top.</summary>
    [Parameter] public IEnumerable<StyledVector>? Vectors { get; set; }

    /// <summary>Extent shown initially and by the "fit" button (defaults to the union of all layers).</summary>
    [Parameter] public Envelope? Extent { get; set; }

    /// <summary>When this value changes the view is reset to <see cref="Extent"/>; otherwise the user's view is kept across data updates.</summary>
    [Parameter] public object? ViewKey { get; set; }

    /// <summary>CRS label shown next to the coordinate readout (e.g. "EPSG:32748").</summary>
    [Parameter] public string? CrsLabel { get; set; }

    /// <summary>Coordinates are degrees (affects readout precision and scale bar). Auto-detected from <see cref="CrsLabel"/> when null.</summary>
    [Parameter] public bool? Geographic { get; set; }

    /// <summary>Fraction of the viewport rendered beyond each edge so panning reveals data immediately.</summary>
    [Parameter] public double Overscan { get; set; } = 0.25;

    [Parameter] public bool ShowControls { get; set; } = true;

    /// <summary>Raised when a vector feature is clicked (topmost layer first).</summary>
    [Parameter] public EventCallback<MapFeatureClick> OnFeatureClick { get; set; }

    /// <summary>Raised for every click on the map.</summary>
    [Parameter] public EventCallback<MapClick> OnClick { get; set; }

    /// <summary>Raised after a render that shows the current <see cref="Rasters"/>/<see cref="Vectors"/> completed.</summary>
    [Parameter] public EventCallback OnRendered { get; set; }

    [Parameter] public string? Class { get; set; }
    [Parameter] public string? Style { get; set; }
    [Parameter] public string? AriaLabel { get; set; }

    private ElementReference _host;
    private ElementReference _stage;
    private ElementReference _readout;
    private IJSObjectReference? _module;
    private DotNetObjectReference<GdalMap>? _self;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScreenTransform? _view;
    private int _width;
    private int _height;
    private int _extWidth;
    private int _extHeight;
    private int _padX;
    private int _padY;
    private string? _image;
    private MarkupString? _svg;
    private string? _error;
    private string? _scaleLabel;
    private double _scalePixels;

    private IReadOnlyList<MapRaster>? _lastRasters;
    private IEnumerable<StyledVector>? _lastVectors;
    private object? _lastViewKey = new();
    private Envelope? _lastExtent;
    private (double S, double Tx, double Ty)? _pendingCommit;   // shown in the DOM, JS not yet told
    private bool _resetClient;
    private (double S, double Tx, double Ty)? _requestedCommit; // requested, not rendered yet
    private bool _requestedReset;
    private bool _notifyRendered;
    private bool _fireRendered;
    private long _renderSeq;
    private bool _disposed;

    private static string Px(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private bool IsGeographic => Geographic ?? (CrsLabel is { } c && (c.Contains("4326") || c.Contains("4755") || c.Contains("geographic", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The current view (world ↔ screen), or null before the first render.</summary>
    public ScreenTransform? View => _view;

    protected override async Task OnParametersSetAsync()
    {
        var dataChanged = !ReferenceEquals(Rasters, _lastRasters) || !ReferenceEquals(Vectors, _lastVectors);
        var viewChanged = !Equals(ViewKey, _lastViewKey) || (ViewKey is null && Extent != _lastExtent);
        _lastRasters = Rasters;
        _lastVectors = Vectors;
        _lastViewKey = ViewKey;
        _lastExtent = Extent;
        if (viewChanged && _width > 0)
        {
            _view = ScreenTransform.Fit(DataExtent(), _width, _height, 24);
            _requestedReset = true;
        }
        if ((dataChanged || viewChanged) && _width > 0)
        {
            _notifyRendered = true;
            await RedrawAsync();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            var size = await _module.InvokeAsync<SizeResult>("init", _host, _stage, _readout, _self);
            await ResizeCore(size.Width, size.Height);
            return;
        }

        if (_module is null) return;
        var viewInfo = ViewInfo();
        if (_resetClient)
        {
            _resetClient = false;
            _pendingCommit = null;
            await _module.InvokeVoidAsync("reset", _host, viewInfo);
        }
        else if (_pendingCommit is { } c)
        {
            _pendingCommit = null;
            await _module.InvokeVoidAsync("rendered", _host, c.S, c.Tx, c.Ty, viewInfo);
        }
        if (_fireRendered)
        {
            _fireRendered = false;
            await OnRendered.InvokeAsync();
        }
    }

    private object? ViewInfo() => _view is { } v ? new { originX = v.OriginX, originY = v.OriginY, scale = v.Scale, geographic = IsGeographic } : null;

    private sealed record SizeResult(int Width, int Height);

    // ------------------------------------------------------------------ JS callbacks

    [JSInvokable]
    public Task Resize(int width, int height) => InvokeAsync(() => ResizeCore(width, height));

    private async Task ResizeCore(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == _width && height == _height)) return;
        var first = _width == 0;
        _width = width;
        _height = height;
        if (first || _view is null)
        {
            _view = ScreenTransform.Fit(DataExtent(), width, height, 24);
            _requestedReset = true;
            _notifyRendered = true;
        }
        else
        {
            _view = _view.Value.Resize(width, height);
        }
        await RedrawAsync();
    }

    [JSInvokable]
    public Task Commit(double s, double tx, double ty) => InvokeAsync(async () =>
    {
        if (_view is { } v && s > 0)
        {
            // The client shows the last render through p' = s·p + t; the new view starts at p = -t/s.
            var (ox, oy) = v.ToWorld(-tx / s, -ty / s);
            _view = v with { Scale = v.Scale * s, OriginX = ox, OriginY = oy };
        }
        _requestedCommit = (s, tx, ty);
        await RedrawAsync();
    });

    [JSInvokable]
    public Task Click(double x, double y) => InvokeAsync(async () =>
    {
        if (_view is not { } v) return;
        var (wx, wy) = v.ToWorld(x, y);
        var tolerance = 6 / v.Scale;
        var hits = new List<MapFeatureClick>();
        if (Vectors is not null)
        {
            foreach (var layer in Vectors.Reverse())
            {
                var t = tolerance + layer.Style.PointRadius / v.Scale;
                foreach (var f in layer.Data.HitTest(wx, wy, t).Take(5))
                {
                    hits.Add(new MapFeatureClick(layer, f, wx, wy));
                }
            }
        }
        if (hits.Count > 0) await OnFeatureClick.InvokeAsync(hits[0]);
        await OnClick.InvokeAsync(new MapClick(wx, wy, hits));
    });

    // ------------------------------------------------------------------ public API

    /// <summary>Zooms around the center (factor &gt; 1 zooms in).</summary>
    public async Task ZoomBy(double factor)
    {
        if (_module is not null) await _module.InvokeVoidAsync("zoomBy", _host, factor);
    }

    /// <summary>Resets the view to <see cref="Extent"/> (or the data extent).</summary>
    public async Task FitAsync()
    {
        if (_width == 0) return;
        _view = ScreenTransform.Fit(DataExtent(), _width, _height, 24);
        _requestedReset = true;
        await RedrawAsync();
    }

    /// <summary>Re-renders the layers (call after mutating datasets in place).</summary>
    public Task RefreshAsync() => RedrawAsync();

    private Envelope DataExtent()
    {
        if (Extent is { IsEmpty: false } e) return e;
        var env = Envelope.Empty;
        if (Rasters is not null)
        {
            foreach (var r in Rasters)
            {
                try { env = env.Union(RasterRenderer.FullView(r.Dataset)); } catch (ObjectDisposedException) { }
            }
        }
        if (Vectors is not null)
        {
            foreach (var v in Vectors) env = env.Union(v.Data.Extent);
        }
        if (env.IsEmpty) return new Envelope(-1, -1, 1, 1);
        if (env.Width == 0 || env.Height == 0)
        {
            var d = Math.Max(Math.Max(env.Width, env.Height), 1) * 0.5;
            env = new Envelope(env.MinX - d, env.MinY - d, env.MaxX + d, env.MaxY + d);
        }
        return env;
    }

    // ------------------------------------------------------------------ rendering

    private async Task RedrawAsync()
    {
        if (_view is not { } view || _width == 0 || _disposed) return;
        var seq = Interlocked.Increment(ref _renderSeq);
        await _gate.WaitAsync();
        try
        {
            if (seq != Interlocked.Read(ref _renderSeq) && _requestedCommit is null && !_requestedReset)
            {
                return; // a newer redraw is queued
            }
            var commit = _requestedCommit;
            var reset = _requestedReset;
            _requestedCommit = null;
            _requestedReset = false;
            view = _view!.Value;
            var padX = (int)Math.Round(_width * Overscan);
            var padY = (int)Math.Round(_height * Overscan);
            var extW = _width + 2 * padX;
            var extH = _height + 2 * padY;
            var ext = new ScreenTransform(view.OriginX - padX / view.Scale, view.OriginY + padY / view.Scale, view.Scale, extW, extH);
            var rastersRef = Rasters;
            var vectorsRef = Vectors;
            var rasters = rastersRef?.ToArray() ?? [];
            var vectors = vectorsRef?.ToArray() ?? [];

            string? image = null;
            string? error = null;
            MarkupString? svg = null;
            await Task.Run(() =>
            {
                try
                {
                    image = RenderRasters(rasters, ext, extW, extH);
                }
                catch (Exception e) when (e is GdalException or ObjectDisposedException or NotSupportedException)
                {
                    error = e.Message;
                }
                svg = vectors.Length == 0 ? null : new MarkupString(BuildSvg(vectors, ext));
            });

            _image = image;
            _svg = svg;
            _error = error;
            _padX = padX;
            _padY = padY;
            _extWidth = extW;
            _extHeight = extH;
            UpdateScaleBar(view);
            if (_notifyRendered && ReferenceEquals(rastersRef, Rasters) && ReferenceEquals(vectorsRef, Vectors))
            {
                _notifyRendered = false;
                _fireRendered = true;
            }
            if (reset) _resetClient = true;
            else if (commit is { } c)
            {
                // Compose with a commit already shown but not yet acknowledged by the client.
                _pendingCommit = _pendingCommit is { } p ? (p.S * c.S, p.Tx * c.S + c.Tx, p.Ty * c.S + c.Ty) : c;
            }
            StateHasChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? RenderRasters(MapRaster[] rasters, ScreenTransform ext, int width, int height)
    {
        if (rasters.Length == 0) return null;
        var world = ext.WorldExtent;
        RgbaImage? composite = null;
        foreach (var r in rasters)
        {
            var img = RasterRenderer.Render(r.Dataset, r.Options, world, width, height);
            if (composite is null)
            {
                composite = img;
                continue;
            }
            AlphaBlend(composite.Pixels, img.Pixels);
        }
        return composite is null ? null : "data:image/png;base64," + Convert.ToBase64String(composite.EncodePng(1));
    }

    private static void AlphaBlend(byte[] dst, byte[] src)
    {
        for (var i = 0; i < dst.Length; i += 4)
        {
            var a = src[i + 3];
            if (a == 0) continue;
            if (a == 255 || dst[i + 3] == 0)
            {
                dst[i] = src[i];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i + 2];
                dst[i + 3] = Math.Max(a, dst[i + 3]);
                continue;
            }
            var inv = 255 - a;
            dst[i] = (byte)((src[i] * a + dst[i] * inv) / 255);
            dst[i + 1] = (byte)((src[i + 1] * a + dst[i + 1] * inv) / 255);
            dst[i + 2] = (byte)((src[i + 2] * a + dst[i + 2] * inv) / 255);
            dst[i + 3] = (byte)Math.Max(dst[i + 3], a);
        }
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string BuildSvg(StyledVector[] layers, ScreenTransform st)
    {
        var sb = new StringBuilder(64 * 1024);
        var visible = st.WorldExtent;
        var labels = new StringBuilder();
        var placed = new List<(double X0, double Y0, double X1, double Y1)>();
        foreach (var layer in layers)
        {
            var style = layer.Style;
            var stroke = style.Stroke.ToCss();
            var sw = F(style.StrokeWidth);
            sb.Append("<g class=\"gdal-layer\" data-layer=\"").Append(WebUtility.HtmlEncode(layer.Title ?? layer.Data.Name)).Append("\">");
            foreach (var f in layer.Data.Features)
            {
                if (!f.Envelope.IsEmpty && !f.Envelope.Intersects(visible)) continue;
                var fill = style.FillFor(f).ToCss();
                var title = Tooltip(f);
                foreach (var shape in f.Shapes)
                {
                    switch (shape.Kind)
                    {
                        case ShapeKind.Point:
                        {
                            var (x, y) = st.ToScreen(shape.Rings[0][0], shape.Rings[0][1]);
                            sb.Append("<circle class=\"gdal-feature\" cx=\"").Append(F(x)).Append("\" cy=\"").Append(F(y))
                              .Append("\" r=\"").Append(F(style.RadiusFor(f))).Append("\" fill=\"").Append(fill)
                              .Append("\" stroke=\"").Append(stroke).Append("\" stroke-width=\"").Append(sw).Append("\">")
                              .Append(title).Append("</circle>");
                            AppendLabel(labels, placed, style, f, x + style.RadiusFor(f) + 3, y + 4);
                            break;
                        }
                        case ShapeKind.Line:
                            sb.Append("<path class=\"gdal-feature\" d=\"").Append(st.ToSvgPath(shape)).Append("\" fill=\"none\" stroke=\"")
                              .Append(stroke).Append("\" stroke-width=\"").Append(sw).Append("\" stroke-linejoin=\"round\" stroke-linecap=\"round\">")
                              .Append(title).Append("</path>");
                            break;
                        default:
                            sb.Append("<path class=\"gdal-feature\" d=\"").Append(st.ToSvgPath(shape)).Append("\" fill=\"").Append(fill)
                              .Append("\" fill-rule=\"evenodd\" stroke=\"").Append(stroke).Append("\" stroke-width=\"").Append(sw)
                              .Append("\" stroke-linejoin=\"round\">").Append(title).Append("</path>");
                            if (style.LabelField is not null)
                            {
                                var (cx, cy) = st.ToScreen(f.Envelope.CenterX, f.Envelope.CenterY);
                                AppendLabel(labels, placed, style, f, cx, cy);
                            }
                            break;
                    }
                }
            }
            sb.Append("</g>");
        }
        if (labels.Length > 0) sb.Append("<g class=\"gdal-labels\">").Append(labels).Append("</g>");
        return sb.ToString();
    }

    private static void AppendLabel(StringBuilder sb, List<(double X0, double Y0, double X1, double Y1)> placed, VectorStyle style, VectorFeature f, double x, double y)
    {
        if (style.LabelField is null || f[style.LabelField] is not { } v) return;
        // Greedy collision avoidance: skip labels overlapping one already placed.
        var text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
        var box = (X0: x - 1, Y0: y - 11, X1: x + text.Length * 6.4 + 1, Y1: y + 3);
        foreach (var p in placed)
        {
            if (box.X0 < p.X1 && box.X1 > p.X0 && box.Y0 < p.Y1 && box.Y1 > p.Y0) return;
        }
        placed.Add(box);
        sb.Append("<text class=\"gdal-label\" x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\">")
          .Append(WebUtility.HtmlEncode(Convert.ToString(v, CultureInfo.InvariantCulture))).Append("</text>");
    }

    private static string Tooltip(VectorFeature f)
    {
        if (f.Attributes.Count == 0) return string.Empty;
        var sb = new StringBuilder("<title>");
        foreach (var kv in f.Attributes.Take(6))
        {
            var value = kv.Value switch
            {
                null => "∅",
                double d => d.ToString("#,0.###", CultureInfo.InvariantCulture),
                long l => l.ToString("#,0", CultureInfo.InvariantCulture),
                int i => i.ToString("#,0", CultureInfo.InvariantCulture),
                _ => Convert.ToString(kv.Value, CultureInfo.InvariantCulture),
            };
            sb.Append(WebUtility.HtmlEncode(kv.Key)).Append(": ").Append(WebUtility.HtmlEncode(value)).Append('\n');
        }
        return sb.Append("</title>").ToString();
    }

    private void UpdateScaleBar(ScreenTransform view)
    {
        var unitsPerPixel = 1 / view.Scale;
        var metersPerPixel = unitsPerPixel;
        if (IsGeographic)
        {
            var (_, lat) = view.ToWorld(_width / 2.0, _height / 2.0);
            metersPerPixel = unitsPerPixel * 111_320 * Math.Cos(Math.Clamp(lat, -85, 85) * Math.PI / 180);
        }
        if (!(metersPerPixel > 0) || double.IsInfinity(metersPerPixel))
        {
            _scaleLabel = null;
            return;
        }
        var target = metersPerPixel * 110;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var nice = new[] { 1, 2, 5, 10 }.Select(m => m * pow).Last(v => v <= target);
        _scalePixels = nice / metersPerPixel;
        _scaleLabel = nice >= 1000 ? $"{(nice / 1000).ToString("0.###", CultureInfo.InvariantCulture)} km" : $"{nice.ToString("0.###", CultureInfo.InvariantCulture)} m";
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _host);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (TaskCanceledException) { }
        }
        _self?.Dispose();
        _gate.Dispose();
    }
}
