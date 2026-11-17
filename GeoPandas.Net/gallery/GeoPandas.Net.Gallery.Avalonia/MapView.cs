using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GeoPandas.Net.Plotting;

namespace GeoPandas.Net.Gallery.Desktop;

/// <summary>
/// Draws a GeoPandas.Net <see cref="MapScene"/> natively with Avalonia's DrawingContext
/// (same display list the SVG writer consumes, so web and desktop maps are identical).
/// </summary>
public sealed class MapView : Control
{
    public static readonly StyledProperty<MapScene?> SceneProperty = AvaloniaProperty.Register<MapView, MapScene?>(nameof(Scene));
    private readonly Dictionary<ImageShape, Bitmap> _bitmaps = new(ReferenceEqualityComparer.Instance);
    private RenderedMap? _cache;
    private Size _cacheSize;

    static MapView() => AffectsRender<MapView>(SceneProperty);

    public MapScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty) { _cache = null; DisposeBitmaps(); }
    }

    private void DisposeBitmaps()
    {
        foreach (var b in _bitmaps.Values) b.Dispose();
        _bitmaps.Clear();
    }

    private static IBrush? Brush(Rgba? c) => c is { } v && v.A > 0 ? new SolidColorBrush(Color.FromArgb(v.A, v.R, v.G, v.B)) : null;
    private static Color Col(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public override void Render(DrawingContext ctx)
    {
        var scene = Scene;
        if (scene is null || Bounds.Width < 10 || Bounds.Height < 10) return;
        if (_cache is null || _cacheSize != Bounds.Size)
        {
            DisposeBitmaps();
            _cache = scene.Render(Bounds.Width, Bounds.Height);
            _cacheSize = Bounds.Size;
        }
        var map = _cache;
        ctx.FillRectangle(Brush(map.Background)!, new Rect(Bounds.Size));
        var body = new Typeface("Inter");
        var bold = new Typeface("Inter", FontStyle.Normal, FontWeight.Bold);

        foreach (var shape in map.Shapes)
        {
            switch (shape)
            {
                case PathShape p:
                {
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.SetFillRule(FillRule.EvenOdd);
                        foreach (var part in p.Parts)
                        {
                            if (part.Length < 4) continue;
                            g.BeginFigure(new Point(part[0], part[1]), p.Closed && p.Fill is not null);
                            for (int i = 2; i + 1 < part.Length; i += 2) g.LineTo(new Point(part[i], part[i + 1]));
                            g.EndFigure(p.Closed);
                        }
                    }
                    IPen? pen = p.Stroke is { } s && s.A > 0
                        ? new Pen(Brush(s), p.StrokeWidth, p.Dash is { Length: > 0 } d ? new DashStyle(d.Select(v => v / Math.Max(1, p.StrokeWidth)), 0) : null, PenLineCap.Round, PenLineJoin.Round)
                        : null;
                    ctx.DrawGeometry(Brush(p.Fill), pen, geo);
                    break;
                }
                case CircleShape c:
                    ctx.DrawEllipse(Brush(c.Fill), c.Stroke is { } cs ? new Pen(Brush(cs), c.StrokeWidth) : null, new Point(c.X, c.Y), c.Radius, c.Radius);
                    break;
                case RectShape r:
                    ctx.DrawRectangle(Brush(r.Fill), r.Stroke is { } rs ? new Pen(Brush(rs), r.StrokeWidth) : null, new Rect(r.X, r.Y, r.Width, r.Height), r.CornerRadius, r.CornerRadius);
                    break;
                case GradientShape gr:
                {
                    var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative) };
                    for (int i = 0; i < gr.Stops.Count; i++) brush.GradientStops.Add(new GradientStop(Col(gr.Stops[i]), gr.Stops.Count == 1 ? 0 : i / (double)(gr.Stops.Count - 1)));
                    ctx.DrawRectangle(brush, null, new Rect(gr.X, gr.Y, gr.Width, gr.Height), 3, 3);
                    break;
                }
                case TextShape t:
                {
                    var ft = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, t.Bold ? bold : body, t.Size, Brush(t.Color));
                    double x = t.Anchor switch { TextAnchor.Middle => t.X - ft.Width / 2, TextAnchor.End => t.X - ft.Width, _ => t.X };
                    var origin = new Point(x, t.Y - ft.Baseline);
                    if (t.Halo is { } h)
                    {
                        var outline = ft.BuildGeometry(origin);
                        if (outline is not null) ctx.DrawGeometry(null, new Pen(Brush(h), 3, lineJoin: PenLineJoin.Round), outline);
                    }
                    ctx.DrawText(ft, origin);
                    break;
                }
                case ImageShape im:
                {
                    if (!_bitmaps.TryGetValue(im, out var bmp))
                    {
                        var wb = new WriteableBitmap(new PixelSize(im.PixelWidth, im.PixelHeight), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                        using (var fb = wb.Lock())
                        {
                            for (int row = 0; row < im.PixelHeight; row++)
                                Marshal.Copy(im.Rgba, row * im.PixelWidth * 4, fb.Address + row * fb.RowBytes, im.PixelWidth * 4);
                        }
                        _bitmaps[im] = bmp = wb;
                    }
                    using (ctx.PushOpacity(im.Opacity))
                    using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                        ctx.DrawImage(bmp, new Rect(0, 0, im.PixelWidth, im.PixelHeight), new Rect(im.X, im.Y, im.Width, im.Height));
                    break;
                }
            }
        }
    }
}
