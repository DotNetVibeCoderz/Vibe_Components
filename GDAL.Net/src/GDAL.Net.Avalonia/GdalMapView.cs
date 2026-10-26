using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GDAL.Net.Rendering;
using GdalRenderOptions = GDAL.Net.Rendering.RenderOptions;

namespace GDAL.Net.Avalonia;

/// <summary>A raster layer displayed by <see cref="GdalMapView"/>.</summary>
/// <param name="Dataset">Raster dataset (north-up). The map reads it on a background thread; don't use it concurrently.</param>
/// <param name="Options">Stretch, color ramp, band selection, hillshade, opacity...</param>
/// <param name="Title">Optional display name.</param>
public sealed record MapRasterLayer(Dataset Dataset, GdalRenderOptions Options, string? Title = null);

/// <summary>Arguments of <see cref="GdalMapView.FeatureClicked"/>.</summary>
public sealed class MapFeatureEventArgs(StyledVector layer, VectorFeature feature, double x, double y) : EventArgs
{
    public StyledVector Layer { get; } = layer;
    public VectorFeature Feature { get; } = feature;
    /// <summary>World X of the click (map CRS).</summary>
    public double X { get; } = x;
    /// <summary>World Y of the click (map CRS).</summary>
    public double Y { get; } = y;
}

/// <summary>
/// Map control for GDAL.Net: raster layers rendered by <see cref="RasterRenderer"/> for the visible extent (background thread,
/// debounced, cancellable) plus vector layers drawn natively. Wheel zooms at the cursor, drag pans, double-click fits,
/// click raises <see cref="FeatureClicked"/>. Draws its own zoom buttons, scale bar and coordinate readout.
/// </summary>
public class GdalMapView : Control
{
    // ------------------------------------------------------------------ properties

    public static readonly StyledProperty<IReadOnlyList<MapRasterLayer>?> RastersProperty =
        AvaloniaProperty.Register<GdalMapView, IReadOnlyList<MapRasterLayer>?>(nameof(Rasters));

    public static readonly StyledProperty<IReadOnlyList<StyledVector>?> VectorsProperty =
        AvaloniaProperty.Register<GdalMapView, IReadOnlyList<StyledVector>?>(nameof(Vectors));

    /// <summary>Extent to fit when layers change (union of the layers when null).</summary>
    public static readonly StyledProperty<Envelope?> ExtentProperty =
        AvaloniaProperty.Register<GdalMapView, Envelope?>(nameof(Extent));

    public static readonly StyledProperty<IBrush?> MapBackgroundProperty =
        AvaloniaProperty.Register<GdalMapView, IBrush?>(nameof(MapBackground), new SolidColorBrush(Color.Parse("#E4EAE7")));

    /// <summary>Text / outline color of the overlays (scale bar, readout, buttons).</summary>
    public static readonly StyledProperty<IBrush?> OverlayForegroundProperty =
        AvaloniaProperty.Register<GdalMapView, IBrush?>(nameof(OverlayForeground), new SolidColorBrush(Color.Parse("#12263A")));

    public static readonly StyledProperty<IBrush?> OverlayBackgroundProperty =
        AvaloniaProperty.Register<GdalMapView, IBrush?>(nameof(OverlayBackground), new SolidColorBrush(Color.Parse("#E6FFFFFF")));

    /// <summary>Highlight color of the selected feature and the busy indicator.</summary>
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<GdalMapView, IBrush?>(nameof(Accent), new SolidColorBrush(Color.Parse("#2A9D8F")));

    public static readonly StyledProperty<FontFamily> OverlayFontFamilyProperty =
        AvaloniaProperty.Register<GdalMapView, FontFamily>(nameof(OverlayFontFamily), FontFamily.Default);

    /// <summary>CRS label shown in the coordinate readout, e.g. "EPSG:32748".</summary>
    public static readonly StyledProperty<string?> CrsLabelProperty =
        AvaloniaProperty.Register<GdalMapView, string?>(nameof(CrsLabel));

    public static readonly StyledProperty<bool> ShowControlsProperty =
        AvaloniaProperty.Register<GdalMapView, bool>(nameof(ShowControls), true);

    public static readonly StyledProperty<bool> ShowScaleBarProperty =
        AvaloniaProperty.Register<GdalMapView, bool>(nameof(ShowScaleBar), true);

    public static readonly StyledProperty<bool> ShowCoordinatesProperty =
        AvaloniaProperty.Register<GdalMapView, bool>(nameof(ShowCoordinates), true);

    /// <summary>Highlights the feature last clicked.</summary>
    public static readonly StyledProperty<bool> HighlightSelectionProperty =
        AvaloniaProperty.Register<GdalMapView, bool>(nameof(HighlightSelection), true);

    public static readonly DirectProperty<GdalMapView, bool> IsRenderingProperty =
        AvaloniaProperty.RegisterDirect<GdalMapView, bool>(nameof(IsRendering), o => o.IsRendering);

    public IReadOnlyList<MapRasterLayer>? Rasters { get => GetValue(RastersProperty); set => SetValue(RastersProperty, value); }
    public IReadOnlyList<StyledVector>? Vectors { get => GetValue(VectorsProperty); set => SetValue(VectorsProperty, value); }
    public Envelope? Extent { get => GetValue(ExtentProperty); set => SetValue(ExtentProperty, value); }
    public IBrush? MapBackground { get => GetValue(MapBackgroundProperty); set => SetValue(MapBackgroundProperty, value); }
    public IBrush? OverlayForeground { get => GetValue(OverlayForegroundProperty); set => SetValue(OverlayForegroundProperty, value); }
    public IBrush? OverlayBackground { get => GetValue(OverlayBackgroundProperty); set => SetValue(OverlayBackgroundProperty, value); }
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public FontFamily OverlayFontFamily { get => GetValue(OverlayFontFamilyProperty); set => SetValue(OverlayFontFamilyProperty, value); }
    public string? CrsLabel { get => GetValue(CrsLabelProperty); set => SetValue(CrsLabelProperty, value); }
    public bool ShowControls { get => GetValue(ShowControlsProperty); set => SetValue(ShowControlsProperty, value); }
    public bool ShowScaleBar { get => GetValue(ShowScaleBarProperty); set => SetValue(ShowScaleBarProperty, value); }
    public bool ShowCoordinates { get => GetValue(ShowCoordinatesProperty); set => SetValue(ShowCoordinatesProperty, value); }
    public bool HighlightSelection { get => GetValue(HighlightSelectionProperty); set => SetValue(HighlightSelectionProperty, value); }

    private bool _isRendering;

    /// <summary>True while rasters are being rendered in the background.</summary>
    public bool IsRendering
    {
        get => _isRendering;
        private set => SetAndRaise(IsRenderingProperty, ref _isRendering, value);
    }

    /// <summary>Raised when the user clicks a vector feature.</summary>
    public event EventHandler<MapFeatureEventArgs>? FeatureClicked;

    /// <summary>Raised after every pan/zoom/resize.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised when the pointer moves over the map (world coordinates), or null when it leaves.</summary>
    public event EventHandler<(double X, double Y)?>? PointerCoordinateChanged;

    static GdalMapView()
    {
        AffectsRender<GdalMapView>(MapBackgroundProperty, OverlayForegroundProperty, OverlayBackgroundProperty, AccentProperty,
            ShowControlsProperty, ShowScaleBarProperty, ShowCoordinatesProperty, CrsLabelProperty);
        FocusableProperty.OverrideDefaultValue<GdalMapView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<GdalMapView>(true);
    }

    public GdalMapView()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartRasterRender();
        };
    }

    // ------------------------------------------------------------------ state

    private ScreenTransform _view;
    private bool _hasView;
    private WriteableBitmap? _bitmap;
    private ScreenTransform _bitmapView;
    private CancellationTokenSource? _renderCts;
    private readonly DispatcherTimer _debounce;
    private int _renderVersion;
    private string? _renderError;
    private readonly Dictionary<StyledVector, LayerCache> _caches = [];
    private readonly Dictionary<uint, IBrush> _brushes = [];
    private (StyledVector Layer, VectorFeature Feature)? _selected;

    private Point? _pressPoint;
    private ScreenTransform _pressView;
    private bool _dragging;
    private (double X, double Y)? _hover;

    private static System.Threading.Lock s_gdalLock => GdalUiSync.Lock;

    /// <summary>Current world ↔ screen transform.</summary>
    public ScreenTransform View => _view;

    /// <summary>Visible world extent.</summary>
    public Envelope VisibleExtent => _view.WorldExtent;

    // ------------------------------------------------------------------ public API

    /// <summary>Fits the layers (or <see cref="Extent"/>) into the view.</summary>
    public void ZoomToFit()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        _view = ScreenTransform.Fit(ContentExtent(), w, h, 24);
        _hasView = true;
        OnViewChanged(immediate: true);
    }

    /// <summary>Zooms around the view center.</summary>
    public void ZoomBy(double factor)
    {
        if (!_hasView) return;
        _view = _view.ZoomAt(Bounds.Width / 2, Bounds.Height / 2, factor);
        OnViewChanged();
    }

    /// <summary>Shows a world extent.</summary>
    public void ZoomTo(Envelope extent)
    {
        if (Bounds.Width <= 0) return;
        _view = ScreenTransform.Fit(extent, Bounds.Width, Bounds.Height, 24);
        _hasView = true;
        OnViewChanged(immediate: true);
    }

    /// <summary>Union of the layer extents (or <see cref="Extent"/> when set).</summary>
    public Envelope ContentExtent()
    {
        if (Extent is { IsEmpty: false } e) return e;
        var env = Envelope.Empty;
        if (Rasters is not null)
        {
            foreach (var r in Rasters)
            {
                try
                {
                    lock (s_gdalLock) env = env.Union(RasterRenderer.FullView(r.Dataset));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        if (Vectors is not null)
        {
            foreach (var v in Vectors) env = env.Union(v.Data.Extent);
        }
        return env.IsEmpty ? new Envelope(0, 0, 1, 1) : env;
    }

    // ------------------------------------------------------------------ property changes / layout

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RastersProperty || change.Property == ExtentProperty)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _renderError = null;
            _hasView = false;
            _selected = null;
            EnsureView();
            OnViewChanged(immediate: true);
        }
        else if (change.Property == VectorsProperty)
        {
            _caches.Clear();
            _selected = null;
            if (Rasters is not { Count: > 0 }) _hasView = false;
            EnsureView();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w > 0 && h > 0)
            {
                if (_hasView) _view = _view.Resize(w, h);
                else EnsureView();
                // First layout (no raster image yet): render right away instead of waiting for the debounce.
                OnViewChanged(immediate: _bitmap is null);
            }
        }
    }

    private void EnsureView()
    {
        if (_hasView || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _view = ScreenTransform.Fit(ContentExtent(), Bounds.Width, Bounds.Height, 24);
        _hasView = true;
    }

    private void OnViewChanged(bool immediate = false)
    {
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        if (Rasters is not { Count: > 0 }) return;
        _debounce.Stop();
        if (immediate) StartRasterRender();
        else _debounce.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _debounce.Stop();
        _renderCts?.Cancel();
    }

    // ------------------------------------------------------------------ raster rendering (background)

    private void StartRasterRender()
    {
        var rasters = Rasters;
        if (!_hasView || rasters is not { Count: > 0 } || Bounds.Width < 2 || Bounds.Height < 2) return;
        _renderCts?.Cancel();
        var cts = _renderCts = new CancellationTokenSource();
        var version = ++_renderVersion;
        var view = _view;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var pw = Math.Max(1, (int)Math.Round(view.Width * scaling));
        var ph = Math.Max(1, (int)Math.Round(view.Height * scaling));
        var layers = rasters.ToArray();
        IsRendering = true;
        InvalidateVisual();

        Task.Run(() =>
        {
            byte[]? rgba = null;
            string? error = null;
            try
            {
                foreach (var layer in layers)
                {
                    if (cts.IsCancellationRequested) return;
                    RgbaImage img;
                    lock (s_gdalLock)
                    {
                        if (layer.Dataset.IsDisposed) continue;
                        img = RasterRenderer.Render(layer.Dataset, layer.Options, view.WorldExtent, pw, ph);
                    }
                    if (rgba is null) rgba = img.Pixels;
                    else Over(rgba, img.Pixels);
                }
            }
            catch (Exception ex) when (ex is GdalException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
            {
                error = ex.Message;
            }

            byte[]? bgra = null;
            if (rgba is not null)
            {
                bgra = new byte[pw * ph * 4];
                new RgbaImage(pw, ph, rgba).CopyToBgraPremultiplied(bgra, pw * 4);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || version != _renderVersion) return;
                IsRendering = false;
                _renderError = error;
                if (bgra is not null)
                {
                    var bmp = new WriteableBitmap(new PixelSize(pw, ph), new global::Avalonia.Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using (var fb = bmp.Lock())
                    {
                        for (var y = 0; y < ph; y++)
                        {
                            Marshal.Copy(bgra, y * pw * 4, fb.Address + y * fb.RowBytes, pw * 4);
                        }
                    }
                    _bitmap?.Dispose();
                    _bitmap = bmp;
                    _bitmapView = view;
                }
                InvalidateVisual();
            });
        });
    }

    /// <summary>Straight-alpha "over" compositing of <paramref name="top"/> onto <paramref name="bottom"/>.</summary>
    private static void Over(byte[] bottom, byte[] top)
    {
        for (var i = 0; i < bottom.Length; i += 4)
        {
            var a = top[i + 3];
            if (a == 0) continue;
            if (a == 255)
            {
                bottom[i] = top[i];
                bottom[i + 1] = top[i + 1];
                bottom[i + 2] = top[i + 2];
                bottom[i + 3] = 255;
                continue;
            }
            var ba = bottom[i + 3];
            var outA = a + ba * (255 - a) / 255;
            if (outA == 0) continue;
            for (var c = 0; c < 3; c++)
            {
                bottom[i + c] = (byte)((top[i + c] * a + bottom[i + c] * ba * (255 - a) / 255) / outA);
            }
            bottom[i + 3] = (byte)outA;
        }
    }

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (MapBackground is { } bg) context.FillRectangle(bg, bounds);
        if (!_hasView)
        {
            EnsureView();
            if (!_hasView) return;
        }

        if (_bitmap is not null)
        {
            var ext = _bitmapView.WorldExtent;
            var (x0, y0) = _view.ToScreen(ext.MinX, ext.MaxY);
            var (x1, y1) = _view.ToScreen(ext.MaxX, ext.MinY);
            var src = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
            using (context.PushRenderOptions(new global::Avalonia.Media.RenderOptions { BitmapInterpolationMode = global::Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality }))
            {
                context.DrawImage(_bitmap, src, new Rect(x0, y0, x1 - x0, y1 - y0));
            }
        }

        if (Vectors is { Count: > 0 } vectors)
        {
            var labels = new List<Rect>();
            foreach (var layer in vectors) DrawLayer(context, layer, labels);
        }

        DrawOverlays(context, bounds);
    }

    private IBrush BrushFor(Rgba c)
    {
        var key = (uint)(c.R << 24 | c.G << 16 | c.B << 8 | c.A);
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)).ToImmutable();
            _brushes[key] = brush;
        }
        return brush;
    }

    private sealed class LayerCache
    {
        public double OriginX;
        public double OriginY;
        public readonly List<(VectorFeature Feature, Geometry? Area, Geometry? Lines, List<(double X, double Y)> Points, (double X, double Y)? Anchor)> Items = [];
        public readonly Dictionary<long, FormattedText> Labels = [];
    }

    private LayerCache GetCache(StyledVector layer)
    {
        if (_caches.TryGetValue(layer, out var cache)) return cache;
        cache = new LayerCache();
        var ext = layer.Data.Extent;
        cache.OriginX = ext.IsEmpty ? 0 : ext.MinX;
        cache.OriginY = ext.IsEmpty ? 0 : ext.MinY;
        foreach (var f in layer.Data.Features)
        {
            StreamGeometry? area = null, lines = null;
            var points = new List<(double, double)>();
            foreach (var shape in f.Shapes)
            {
                switch (shape.Kind)
                {
                    case ShapeKind.Point:
                        points.Add((shape.Rings[0][0], shape.Rings[0][1]));
                        break;
                    case ShapeKind.Line:
                        lines ??= new StreamGeometry();
                        break;
                    default:
                        area ??= new StreamGeometry();
                        break;
                }
            }
            if (area is not null) Fill(area, f, ShapeKind.Polygon, cache.OriginX, cache.OriginY);
            if (lines is not null) Fill(lines, f, ShapeKind.Line, cache.OriginX, cache.OriginY);
            (double, double)? anchor = points.Count > 0 ? points[0] : f.Envelope.IsEmpty ? null : (f.Envelope.CenterX, f.Envelope.CenterY);
            cache.Items.Add((f, area, lines, points, anchor));
        }
        _caches[layer] = cache;
        return cache;
    }

    private static void Fill(StreamGeometry geometry, VectorFeature f, ShapeKind kind, double ox, double oy)
    {
        using var g = geometry.Open();
        g.SetFillRule(FillRule.EvenOdd);
        foreach (var shape in f.Shapes)
        {
            if (shape.Kind != kind) continue;
            foreach (var ring in shape.Rings)
            {
                if (ring.Length < 4) continue;
                g.BeginFigure(new Point(ring[0] - ox, ring[1] - oy), kind == ShapeKind.Polygon);
                for (var i = 2; i + 1 < ring.Length; i += 2) g.LineTo(new Point(ring[i] - ox, ring[i + 1] - oy));
                g.EndFigure(kind == ShapeKind.Polygon);
            }
        }
    }

    private void DrawLayer(DrawingContext context, StyledVector layer, List<Rect> labelRects)
    {
        var style = layer.Style;
        var cache = GetCache(layer);
        var s = _view.Scale;
        var matrix = new Matrix(s, 0, 0, -s, (cache.OriginX - _view.OriginX) * s, (_view.OriginY - cache.OriginY) * s);
        var visible = _view.WorldExtent;
        var strokeBrush = BrushFor(style.Stroke);
        var worldPen = new Pen(strokeBrush, style.StrokeWidth / s, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        var selectedPen = new Pen(Accent ?? Brushes.Teal, 3 / s, lineJoin: PenLineJoin.Round);
        var transparent = style.Stroke.A == 0 ? null : worldPen;

        using (context.PushTransform(matrix))
        {
            foreach (var item in cache.Items)
            {
                if (!item.Feature.Envelope.IsEmpty && !visible.Intersects(item.Feature.Envelope)) continue;
                var isSelected = HighlightSelection && _selected is { } sel && sel.Layer == layer && sel.Feature == item.Feature;
                if (item.Area is not null)
                {
                    var fill = style.FillFor(item.Feature);
                    context.DrawGeometry(fill.A == 0 ? null : BrushFor(fill), transparent, item.Area);
                    if (isSelected) context.DrawGeometry(null, selectedPen, item.Area);
                }
                if (item.Lines is not null)
                {
                    context.DrawGeometry(null, isSelected ? selectedPen : worldPen, item.Lines);
                }
            }
        }

        // Points and labels in screen space (constant size).
        var pointPen = style.Stroke.A == 0 ? null : new Pen(strokeBrush, Math.Max(1, style.StrokeWidth));
        foreach (var item in cache.Items)
        {
            if (item.Points.Count == 0) continue;
            var r = style.RadiusFor(item.Feature);
            var fill = BrushFor(style.FillFor(item.Feature));
            foreach (var (x, y) in item.Points)
            {
                var (sx, sy) = _view.ToScreen(x, y);
                if (sx < -r || sy < -r || sx > _view.Width + r || sy > _view.Height + r) continue;
                context.DrawEllipse(fill, pointPen, new Point(sx, sy), r, r);
                if (HighlightSelection && _selected is { } sel && sel.Layer == layer && sel.Feature == item.Feature)
                {
                    context.DrawEllipse(null, new Pen(Accent ?? Brushes.Teal, 2.5), new Point(sx, sy), r + 4, r + 4);
                }
            }
        }

        if (style.LabelField is { } field)
        {
            var typeface = new Typeface(OverlayFontFamily, FontStyle.Normal, FontWeight.Medium);
            var ink = BrushFor(new Rgba(18, 38, 58));
            var halo = new Pen(BrushFor(new Rgba(255, 255, 255, 220)), 3.2, lineJoin: PenLineJoin.Round);
            foreach (var item in cache.Items)
            {
                if (item.Anchor is not { } anchor) continue;
                var (sx, sy) = _view.ToScreen(anchor.X, anchor.Y);
                if (sx < -50 || sy < -20 || sx > _view.Width + 50 || sy > _view.Height + 20) continue;
                if (!cache.Labels.TryGetValue(item.Feature.Id, out var text))
                {
                    var value = item.Feature[field]?.ToString();
                    if (string.IsNullOrEmpty(value)) continue;
                    text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11.5, ink);
                    cache.Labels[item.Feature.Id] = text;
                }
                var offset = item.Points.Count > 0 ? style.RadiusFor(item.Feature) + 4 : -text.Width / 2;
                var origin = new Point(sx + offset, sy - text.Height / 2);
                var rect = new Rect(origin, new Size(text.Width, text.Height)).Inflate(2);
                if (labelRects.Any(l => l.Intersects(rect))) continue;
                labelRects.Add(rect);
                var geometry = text.BuildGeometry(origin);
                if (geometry is not null) context.DrawGeometry(null, halo, geometry);
                context.DrawText(text, origin);
            }
        }
    }

    // ------------------------------------------------------------------ overlays

    private const double ButtonSize = 30;

    private Rect ButtonRect(int index) => new(Bounds.Width - ButtonSize - 12, 12 + index * (ButtonSize + 6), ButtonSize, ButtonSize);

    private void DrawOverlays(DrawingContext context, Rect bounds)
    {
        var fg = OverlayForeground ?? Brushes.Black;
        var panel = OverlayBackground ?? Brushes.White;
        var mono = new Typeface(OverlayFontFamily);

        if (ShowControls)
        {
            string[] glyphs = ["+", "−", "⤢"];
            var pen = new Pen(fg, 1);
            for (var i = 0; i < 3; i++)
            {
                var r = ButtonRect(i);
                context.DrawRectangle(panel, new Pen(new SolidColorBrush(Colors.Black, 0.12), 1), r, 4, 4);
                if (i < 2)
                {
                    context.DrawLine(new Pen(fg, 1.6), new Point(r.X + 9, r.Center.Y), new Point(r.Right - 9, r.Center.Y));
                    if (i == 0) context.DrawLine(new Pen(fg, 1.6), new Point(r.Center.X, r.Y + 9), new Point(r.Center.X, r.Bottom - 9));
                }
                else
                {
                    // Fit: four corner brackets.
                    var d = 8.0;
                    var inset = 8.0;
                    var (l, t, rr, b) = (r.X + inset, r.Y + inset, r.Right - inset, r.Bottom - inset);
                    context.DrawLine(pen, new Point(l, t + d / 2), new Point(l, t)); context.DrawLine(pen, new Point(l, t), new Point(l + d / 2, t));
                    context.DrawLine(pen, new Point(rr - d / 2, t), new Point(rr, t)); context.DrawLine(pen, new Point(rr, t), new Point(rr, t + d / 2));
                    context.DrawLine(pen, new Point(l, b - d / 2), new Point(l, b)); context.DrawLine(pen, new Point(l, b), new Point(l + d / 2, b));
                    context.DrawLine(pen, new Point(rr - d / 2, b), new Point(rr, b)); context.DrawLine(pen, new Point(rr, b), new Point(rr, b - d / 2));
                }
            }
        }

        if (ShowScaleBar && _hasView) DrawScaleBar(context, fg, panel, mono, bounds);

        if (ShowCoordinates)
        {
            var label = _hover is { } h
                ? $"{FormatCoord(h.X)}  {FormatCoord(h.Y)}{(CrsLabel is null ? "" : "   " + CrsLabel)}"
                : CrsLabel ?? string.Empty;
            if (!string.IsNullOrEmpty(label))
            {
                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono, 11, fg);
                var rect = new Rect(bounds.Width - text.Width - 22, bounds.Height - text.Height - 16, text.Width + 12, text.Height + 6);
                context.DrawRectangle(panel, null, rect, 3, 3);
                context.DrawText(text, new Point(rect.X + 6, rect.Y + 3));
            }
        }

        if (IsRendering)
        {
            var accent = Accent ?? Brushes.Teal;
            var phase = (Environment.TickCount64 / 6 % 1000) / 1000.0;
            var w = bounds.Width * 0.25;
            context.FillRectangle(accent, new Rect((bounds.Width + w) * phase - w, 0, w, 2.5));
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }

        if (_renderError is { } err)
        {
            var text = new FormattedText(err, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, mono, 11.5, Brushes.White) { MaxTextWidth = Math.Max(100, bounds.Width - 48) };
            var rect = new Rect(12, 12, text.Width + 16, text.Height + 10);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#C8553D")), null, rect, 4, 4);
            context.DrawText(text, new Point(20, 17));
        }
    }

    private string FormatCoord(double v)
    {
        var geographic = Math.Abs(_view.WorldExtent.Width) < 400 && Math.Abs(_view.WorldExtent.MaxX) <= 360;
        return v.ToString(geographic ? "0.00000" : "#,0.0", CultureInfo.InvariantCulture);
    }

    private void DrawScaleBar(DrawingContext context, IBrush fg, IBrush panel, Typeface typeface, Rect bounds)
    {
        var ext = _view.WorldExtent;
        var geographic = Math.Abs(ext.MaxX) <= 360 && Math.Abs(ext.MaxY) <= 90 && ext.Width < 400;
        // Meters per screen pixel.
        var unitsPerPixel = 1 / _view.Scale;
        var metersPerPixel = geographic ? unitsPerPixel * 111_320 * Math.Cos(ext.CenterY * Math.PI / 180) : unitsPerPixel;
        if (CrsLabel is { } crs && crs.Contains("3857", StringComparison.Ordinal))
        {
            // Web Mercator stretches distances by 1/cos(lat).
            var lat = Math.Atan(Math.Sinh(ext.CenterY / 6378137.0));
            metersPerPixel *= Math.Cos(lat);
        }
        if (!(metersPerPixel > 0) || double.IsInfinity(metersPerPixel)) return;
        var target = 110 * metersPerPixel;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var nice = new[] { 1, 2, 5, 10 }.Select(m => m * pow).Last(v => v <= target);
        var px = nice / metersPerPixel;
        var label = nice >= 1000 ? $"{nice / 1000:0.##} km" : $"{nice:0.##} m";
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, fg);
        var x = 14.0;
        var y = bounds.Height - 18;
        context.DrawRectangle(panel, null, new Rect(x - 6, y - text.Height - 8, Math.Max(px, text.Width) + 12, text.Height + 16), 3, 3);
        var pen = new Pen(fg, 1.5);
        context.DrawLine(pen, new Point(x, y), new Point(x + px, y));
        context.DrawLine(pen, new Point(x, y - 5), new Point(x, y + 2));
        context.DrawLine(pen, new Point(x + px, y - 5), new Point(x + px, y + 2));
        context.FillRectangle(fg, new Rect(x, y - 3, px / 2, 3));
        context.DrawText(text, new Point(x, y - text.Height - 5));
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_hasView) return;
        var p = e.GetPosition(this);
        _view = _view.ZoomAt(p.X, p.Y, Math.Pow(1.2, e.Delta.Y));
        OnViewChanged();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ShowControls)
        {
            for (var i = 0; i < 3; i++)
            {
                if (!ButtonRect(i).Contains(p)) continue;
                if (i == 0) ZoomBy(1.6);
                else if (i == 1) ZoomBy(1 / 1.6);
                else ZoomToFit();
                e.Handled = true;
                return;
            }
        }
        if (e.ClickCount == 2)
        {
            ZoomToFit();
            e.Handled = true;
            return;
        }
        _pressPoint = p;
        _pressView = _view;
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_hasView)
        {
            _hover = _view.ToWorld(p.X, p.Y);
            PointerCoordinateChanged?.Invoke(this, _hover);
        }
        if (_pressPoint is { } start)
        {
            var d = p - start;
            if (_dragging || Math.Abs(d.X) + Math.Abs(d.Y) > 3)
            {
                _dragging = true;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                _view = _pressView.Pan(d.X, d.Y);
                OnViewChanged();
                return;
            }
        }
        if (ShowCoordinates) InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is null) return;
        var p = e.GetPosition(this);
        if (!_dragging) HitTest(p);
        _pressPoint = null;
        _dragging = false;
        Cursor = Cursor.Default;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        PointerCoordinateChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = 60.0;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomBy(1.4); break;
            case Key.Subtract or Key.OemMinus: ZoomBy(1 / 1.4); break;
            case Key.Home: ZoomToFit(); break;
            case Key.Left: _view = _view.Pan(step, 0); OnViewChanged(); break;
            case Key.Right: _view = _view.Pan(-step, 0); OnViewChanged(); break;
            case Key.Up: _view = _view.Pan(0, step); OnViewChanged(); break;
            case Key.Down: _view = _view.Pan(0, -step); OnViewChanged(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void HitTest(Point p)
    {
        if (Vectors is not { Count: > 0 } vectors || !_hasView) return;
        var (x, y) = _view.ToWorld(p.X, p.Y);
        for (var i = vectors.Count - 1; i >= 0; i--)
        {
            var layer = vectors[i];
            var tolerance = (layer.Style.PointRadius + 4) / _view.Scale;
            var hit = layer.Data.HitTest(x, y, tolerance).FirstOrDefault();
            if (hit is null) continue;
            _selected = (layer, hit);
            InvalidateVisual();
            FeatureClicked?.Invoke(this, new MapFeatureEventArgs(layer, hit, x, y));
            return;
        }
        if (_selected is not null)
        {
            _selected = null;
            InvalidateVisual();
        }
    }
}
