using System.Globalization;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Plotting;

/// <summary>Styling for a vector layer. Set <see cref="Column"/> for a choropleth / categorical map.</summary>
public sealed record LayerStyle
{
    public Rgba? Fill { get; init; } = "#4e79a7";
    public Rgba? Stroke { get; init; } = "#1f2933";
    public double StrokeWidth { get; init; } = 0.8;
    public double FillOpacity { get; init; } = 0.85;
    public double PointRadius { get; init; } = 4;
    public double[]? DashArray { get; init; }
    /// <summary>Attribute used for colouring.</summary>
    public string? Column { get; init; }
    public ColorMap ColorMap { get; init; } = ColorMaps.Viridis;
    public ClassificationScheme Scheme { get; init; } = ClassificationScheme.Quantiles;
    public int Classes { get; init; } = 5;
    /// <summary>Treat <see cref="Column"/> as categories (qualitative palette) even when numeric.</summary>
    public bool Categorical { get; init; }
    /// <summary>Attribute drawn as text labels at representative points.</summary>
    public string? LabelColumn { get; init; }
    public double LabelSize { get; init; } = 10;
    public Rgba LabelColor { get; init; } = "#111827";
    public string? LegendTitle { get; init; }
    public bool ShowInLegend { get; init; } = true;
    /// <summary>Optional per-row size column for proportional symbols.</summary>
    public string? SizeColumn { get; init; }
    public double MaxPointRadius { get; init; } = 18;
}

public abstract record MapLayer(string Name);
public sealed record VectorLayer(string Name, GeoSeries Geometry, DataFrame? Attributes, LayerStyle Style) : MapLayer(Name);
public sealed record ImageLayer(string Name, byte[] Rgba, int PixelWidth, int PixelHeight, Envelope Bounds, double Opacity, LegendEntry[]? Legend) : MapLayer(Name);

public sealed record LegendEntry(string Label, Rgba Color, LegendSymbol Symbol = LegendSymbol.Box);
public enum LegendSymbol { Box, Line, Point, Gradient }

/// <summary>
/// Renderer-agnostic map composition (GeoPandas <c>.plot()</c> / <c>.explore()</c> counterpart).
/// <see cref="Render"/> produces screen-space shapes that the SVG writer, Blazor and Avalonia renderers draw.
/// </summary>
public sealed class MapScene
{
    private readonly List<MapLayer> _layers = [];

    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public Rgba Background { get; set; } = "#f8fafc";
    public bool ShowLegend { get; set; } = true;
    public bool ShowScaleBar { get; set; } = true;
    public bool ShowNorthArrow { get; set; } = true;
    /// <summary>Draws a map-sheet frame with coordinate ticks and labels (topographic map collar).</summary>
    public bool ShowFrame { get; set; }
    /// <summary>Colour of frame ticks / grid labels.</summary>
    public Rgba FrameColor { get; set; } = "#b0306a";
    public string? Attribution { get; set; } = "GeoPandas.Net · Gravicode Studios";
    /// <summary>Explicit extent; defaults to the union of all layers.</summary>
    public Envelope? Extent { get; set; }
    /// <summary>Whether coordinates are in degrees (affects the scale bar).</summary>
    public bool Geographic { get; set; }
    public double Padding { get; set; } = 24;
    public string FontFamily { get; set; } = "Inter, Segoe UI, Helvetica, Arial, sans-serif";

    public IReadOnlyList<MapLayer> Layers => _layers;

    public MapScene AddLayer(GeoDataFrame gdf, LayerStyle? style = null, string? name = null)
    {
        Geographic |= gdf.IsGeographic;
        _layers.Add(new VectorLayer(name ?? $"layer{_layers.Count + 1}", gdf.Geometry, gdf, style ?? new LayerStyle()));
        return this;
    }

    public MapScene AddLayer(GeoSeries geometries, LayerStyle? style = null, string? name = null)
    {
        Geographic |= geometries.Crs?.IsGeographic ?? false;
        _layers.Add(new VectorLayer(name ?? $"layer{_layers.Count + 1}", geometries, null, style ?? new LayerStyle()));
        return this;
    }

    public MapScene AddLayer(IEnumerable<NtsGeometry> geometries, LayerStyle? style = null, string? name = null) =>
        AddLayer(new GeoSeries(geometries.Cast<NtsGeometry?>()), style, name);

    /// <summary>Adds an RGBA image (e.g. a rendered raster) georeferenced to <paramref name="bounds"/>.</summary>
    public MapScene AddImage(byte[] rgba, int width, int height, Envelope bounds, double opacity = 1, LegendEntry[]? legend = null, string? name = null)
    {
        _layers.Add(new ImageLayer(name ?? $"image{_layers.Count + 1}", rgba, width, height, bounds, opacity, legend));
        return this;
    }

    public Envelope ComputeExtent()
    {
        if (Extent is { IsNull: false } e) return e;
        var env = new Envelope();
        foreach (var l in _layers)
        {
            if (l is VectorLayer v) env.ExpandToInclude(v.Geometry.TotalBounds);
            else if (l is ImageLayer im) env.ExpandToInclude(im.Bounds);
        }
        if (env.IsNull) env = new Envelope(0, 1, 0, 1);
        if (env.Width == 0 || env.Height == 0) env.ExpandBy(Math.Max(env.Width, env.Height) * 0.05 + 1e-6);
        return env;
    }

    /// <summary>Renders to screen-space shapes.</summary>
    public RenderedMap Render(double width, double height) => new MapRenderer(this, width, height).Render();

    public string ToSvg(double width = 900, double height = 600) => SvgWriter.Write(Render(width, height), FontFamily);

    public void SaveSvg(string path, double width = 900, double height = 600) => File.WriteAllText(path, ToSvg(width, height));
}

// ------------------------------------------------------------------ rendered output

public abstract record Shape;
/// <summary>A path of one or more rings / parts (screen coordinates, interleaved x,y).</summary>
public sealed record PathShape(IReadOnlyList<double[]> Parts, bool Closed, Rgba? Fill, Rgba? Stroke, double StrokeWidth, double[]? Dash = null) : Shape;
public sealed record CircleShape(double X, double Y, double Radius, Rgba? Fill, Rgba? Stroke, double StrokeWidth) : Shape;
public sealed record RectShape(double X, double Y, double Width, double Height, Rgba? Fill, Rgba? Stroke, double StrokeWidth, double CornerRadius = 0) : Shape;
public enum TextAnchor { Start, Middle, End }
public sealed record TextShape(double X, double Y, string Text, double Size, Rgba Color, TextAnchor Anchor = TextAnchor.Start, bool Bold = false, Rgba? Halo = null) : Shape;
public sealed record ImageShape(double X, double Y, double Width, double Height, byte[] Rgba, int PixelWidth, int PixelHeight, double Opacity) : Shape;
public sealed record GradientShape(double X, double Y, double Width, double Height, IReadOnlyList<Rgba> Stops) : Shape;

/// <summary>Result of rendering a <see cref="MapScene"/>: an ordered display list.</summary>
public sealed class RenderedMap(double width, double height, Rgba background, List<Shape> shapes)
{
    public double Width { get; } = width;
    public double Height { get; } = height;
    public Rgba Background { get; } = background;
    public IReadOnlyList<Shape> Shapes { get; } = shapes;

    public string ToSvg(string fontFamily = "Inter, Segoe UI, Helvetica, Arial, sans-serif") => SvgWriter.Write(this, fontFamily);
}

internal sealed class MapRenderer
{
    private readonly MapScene _scene;
    private readonly double _w, _h;
    private readonly double _scale, _offX, _offY;
    private readonly Envelope _extent;
    private readonly List<Shape> _shapes = [];
    private readonly List<(string Title, LegendEntry[] Entries)> _legends = [];

    public MapRenderer(MapScene scene, double width, double height)
    {
        _scene = scene; _w = width; _h = height;
        _extent = scene.ComputeExtent();
        double pad = scene.Padding;
        double top = pad + (scene.Title is null ? 0 : 34) + (scene.Subtitle is null ? 0 : 18);
        double availW = width - 2 * pad, availH = height - top - pad - 16;
        _scale = Math.Min(availW / _extent.Width, availH / _extent.Height);
        _offX = pad + (availW - _extent.Width * _scale) / 2;
        _offY = top + (availH - _extent.Height * _scale) / 2;
    }

    private (double X, double Y) ToScreen(double x, double y) => (_offX + (x - _extent.MinX) * _scale, _offY + (_extent.MaxY - y) * _scale);

    public RenderedMap Render()
    {
        foreach (var layer in _scene.Layers)
        {
            switch (layer)
            {
                case ImageLayer im: RenderImage(im); break;
                case VectorLayer v: RenderVector(v); break;
            }
        }
        RenderChrome();
        return new RenderedMap(_w, _h, _scene.Background, _shapes);
    }

    private void RenderImage(ImageLayer im)
    {
        var (x0, y0) = ToScreen(im.Bounds.MinX, im.Bounds.MaxY);
        var (x1, y1) = ToScreen(im.Bounds.MaxX, im.Bounds.MinY);
        _shapes.Add(new ImageShape(x0, y0, x1 - x0, y1 - y0, im.Rgba, im.PixelWidth, im.PixelHeight, im.Opacity));
        if (im.Legend is { Length: > 0 }) _legends.Add((im.Name, im.Legend));
    }

    private void RenderVector(VectorLayer layer)
    {
        var s = layer.Style;
        var geoms = layer.Geometry;
        Func<int, Rgba?> fillOf = _ => s.Fill?.WithAlpha(s.FillOpacity);
        Func<int, double> radiusOf = _ => s.PointRadius;

        if (s.Column is not null && layer.Attributes is not null)
        {
            var col = layer.Attributes[s.Column];
            if (s.Categorical || !col.IsNumeric)
            {
                var cats = new Dictionary<string, Rgba>();
                for (int i = 0; i < col.Length; i++)
                {
                    var k = col.GetValue(i)?.ToString() ?? "null";
                    if (!cats.ContainsKey(k)) cats[k] = ColorMaps.Categorical[cats.Count % ColorMaps.Categorical.Count];
                }
                fillOf = i => cats[col.GetValue(i)?.ToString() ?? "null"].WithAlpha(s.FillOpacity);
                if (s.ShowInLegend) _legends.Add((s.LegendTitle ?? s.Column, cats.Take(12).Select(kv => new LegendEntry(kv.Key, kv.Value, SymbolFor(geoms))).ToArray()));
            }
            else
            {
                var vals = col.ToDoubleArray();
                if (s.Scheme == ClassificationScheme.Continuous)
                {
                    var (min, max) = (vals.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Min(), vals.Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max());
                    double range = max - min == 0 ? 1 : max - min;
                    fillOf = i => double.IsNaN(vals[i]) ? Rgba.Parse("#d1d5db") : s.ColorMap.Sample((vals[i] - min) / range).WithAlpha(s.FillOpacity);
                    if (s.ShowInLegend)
                        _legends.Add((s.LegendTitle ?? s.Column, Enumerable.Range(0, 11).Select(k =>
                            new LegendEntry(k == 0 ? Fmt(min) : k == 10 ? Fmt(max) : "", s.ColorMap.Sample(k / 10.0), LegendSymbol.Gradient)).ToArray()));
                }
                else
                {
                    var breaks = Classifier.Breaks(vals, s.Classes, s.Scheme);
                    var colors = s.ColorMap.Discrete(Math.Max(1, breaks.Length));
                    fillOf = i => double.IsNaN(vals[i]) ? Rgba.Parse("#d1d5db") : colors[Classifier.ClassOf(vals[i], breaks)].WithAlpha(s.FillOpacity);
                    if (s.ShowInLegend && breaks.Length > 0)
                    {
                        double lo = vals.Where(v => !double.IsNaN(v)).Min();
                        var entries = new LegendEntry[breaks.Length];
                        for (int k = 0; k < breaks.Length; k++)
                        {
                            entries[k] = new LegendEntry($"{Fmt(lo)} – {Fmt(breaks[k])}", colors[k], SymbolFor(geoms));
                            lo = breaks[k];
                        }
                        _legends.Add((s.LegendTitle ?? s.Column, entries));
                    }
                }
            }
        }
        else if (s.ShowInLegend && s.LegendTitle is not null && s.Fill is { } f)
        {
            _legends.Add((s.LegendTitle, [new LegendEntry(layer.Name, f, SymbolFor(geoms))]));
        }

        if (s.SizeColumn is not null && layer.Attributes is not null)
        {
            var sv = layer.Attributes[s.SizeColumn].ToDoubleArray();
            double max = sv.Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max();
            radiusOf = i => double.IsNaN(sv[i]) || max <= 0 ? s.PointRadius : Math.Max(2, s.MaxPointRadius * Math.Sqrt(sv[i] / max));
        }

        var clipBox = _scene.Extent is { IsNull: false } ? Geometry.Geo.Box(_extent.MinX, _extent.MinY, _extent.MaxX, _extent.MaxY) : null;
        for (int i = 0; i < geoms.Length; i++)
        {
            var g = geoms[i];
            if (g is null || g.IsEmpty) continue;
            if (clipBox is not null && !_extent.Contains(g.EnvelopeInternal))
            {
                if (!_extent.Intersects(g.EnvelopeInternal)) continue;
                g = Geometry.GeometryOps.Intersection(g, clipBox);
                if (g.IsEmpty) continue;
            }
            Emit(g, fillOf(i), s, radiusOf(i));
        }

        if (s.LabelColumn is not null && layer.Attributes is not null)
        {
            var lc = layer.Attributes[s.LabelColumn];
            for (int i = 0; i < geoms.Length; i++)
            {
                if (geoms[i] is not { IsEmpty: false } g || lc.IsNull(i)) continue;
                var p = g is Point pt ? pt : g.InteriorPoint;
                var (x, y) = ToScreen(p.X, p.Y);
                if (g is Point) y -= radiusOf(i) + 3;
                _shapes.Add(new TextShape(x, y, lc.Format(i), s.LabelSize, s.LabelColor, TextAnchor.Middle, false, Rgba.White.WithAlpha(0.85)));
            }
        }
    }

    private static LegendSymbol SymbolFor(GeoSeries g)
    {
        var first = g.Values.ToArray().FirstOrDefault(x => x is not null);
        return first switch { IPuntal => LegendSymbol.Point, ILineal => LegendSymbol.Line, _ => LegendSymbol.Box };
    }

    private void Emit(NtsGeometry g, Rgba? fill, LayerStyle s, double radius)
    {
        switch (g)
        {
            case Point p:
                var (x, y) = ToScreen(p.X, p.Y);
                _shapes.Add(new CircleShape(x, y, radius, fill, s.Stroke, s.StrokeWidth));
                break;
            case LineString ls:
                _shapes.Add(new PathShape([Project(ls.CoordinateSequence)], false, null, fill?.WithAlpha(1) ?? s.Stroke, Math.Max(s.StrokeWidth, 1.2), s.DashArray));
                break;
            case Polygon poly:
                var parts = new List<double[]> { Project(poly.ExteriorRing.CoordinateSequence) };
                foreach (var h in poly.InteriorRings) parts.Add(Project(h.CoordinateSequence));
                _shapes.Add(new PathShape(parts, true, fill, s.Stroke, s.StrokeWidth, s.DashArray));
                break;
            case GeometryCollection gc:
                for (int k = 0; k < gc.NumGeometries; k++) Emit(gc.GetGeometryN(k), fill, s, radius);
                break;
        }
    }

    private double[] Project(CoordinateSequence seq)
    {
        // Screen-space decimation: skip vertices closer than 0.35 px to the previous one.
        var pts = new List<double>(seq.Count * 2);
        double lx = double.NaN, ly = double.NaN;
        for (int i = 0; i < seq.Count; i++)
        {
            var (x, y) = ToScreen(seq.GetX(i), seq.GetY(i));
            if (i > 0 && i < seq.Count - 1 && Math.Abs(x - lx) < 0.35 && Math.Abs(y - ly) < 0.35) continue;
            pts.Add(Math.Round(x, 2)); pts.Add(Math.Round(y, 2));
            lx = x; ly = y;
        }
        return pts.ToArray();
    }

    internal static string Fmt(double v)
    {
        double a = Math.Abs(v);
        string s = a >= 1e6 ? (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M"
                 : a >= 1e4 ? (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                 : a >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
                 : a >= 1 ? v.ToString("0.##", CultureInfo.InvariantCulture)
                 : v.ToString("0.###", CultureInfo.InvariantCulture);
        return s;
    }

    private void RenderChrome()
    {
        var ink = Rgba.Parse("#0f172a");
        var muted = Rgba.Parse("#475569");
        double pad = _scene.Padding;
        if (_scene.Title is not null) _shapes.Add(new TextShape(pad, pad + 20, _scene.Title, 20, ink, TextAnchor.Start, true));
        if (_scene.Subtitle is not null) _shapes.Add(new TextShape(pad, pad + (_scene.Title is null ? 14 : 40), _scene.Subtitle, 12, muted));

        if (_scene.ShowFrame) RenderFrame();
        if (_scene.ShowLegend && _legends.Count > 0) RenderLegends();
        if (_scene.ShowScaleBar) RenderScaleBar();
        if (_scene.ShowNorthArrow) RenderNorthArrow();
        if (_scene.Attribution is not null)
            _shapes.Add(new TextShape(_w - pad, _h - 8, _scene.Attribution, 9, muted.WithAlpha(0.8), TextAnchor.End));
    }

    private void RenderFrame()
    {
        double x0 = _offX, y0 = _offY, x1 = _offX + _extent.Width * _scale, y1 = _offY + _extent.Height * _scale;
        var c = _scene.FrameColor;
        _shapes.Add(new RectShape(x0, y0, x1 - x0, y1 - y0, null, c.WithAlpha(0.9), 1));
        double Nice(double span)
        {
            double raw = span / 5, p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            return new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * p).First(v => v >= raw);
        }
        string Lbl(double v) => _scene.Geographic ? v.ToString("0.###", CultureInfo.InvariantCulture) + "°"
            : Math.Abs(v) >= 1e5 ? (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : v.ToString("0", CultureInfo.InvariantCulture);
        double sx = Nice(_extent.Width);
        for (double v = Math.Ceiling(_extent.MinX / sx) * sx; v <= _extent.MaxX; v += sx)
        {
            var (x, _) = ToScreen(v, _extent.MinY);
            _shapes.Add(new PathShape([[x, y0, x, y0 + 6]], false, null, c, 1));
            _shapes.Add(new PathShape([[x, y1, x, y1 - 6]], false, null, c, 1));
            _shapes.Add(new TextShape(x, y1 + 11, Lbl(v), 8.5, c, TextAnchor.Middle));
        }
        double sy = Nice(_extent.Height);
        for (double v = Math.Ceiling(_extent.MinY / sy) * sy; v <= _extent.MaxY; v += sy)
        {
            var (_, y) = ToScreen(_extent.MinX, v);
            _shapes.Add(new PathShape([[x0, y, x0 + 6, y]], false, null, c, 1));
            _shapes.Add(new PathShape([[x1, y, x1 - 6, y]], false, null, c, 1));
            _shapes.Add(new TextShape(x0 - 3, y + 3, Lbl(v), 8.5, c, TextAnchor.End));
        }
    }

    private void RenderLegends()
    {
        const double row = 18, sw = 14;
        double width = 190;
        double height = _legends.Sum(l => l.Entries.All(e => e.Symbol == LegendSymbol.Gradient) ? 20 + 38 : 24 + l.Entries.Length * row) + 8;
        double x = _w - _scene.Padding - width, y = _h - _scene.Padding - 18 - height;
        _shapes.Add(new RectShape(x, y, width, height, Rgba.White.WithAlpha(0.92), Rgba.Parse("#cbd5e1"), 1, 8));
        double cy = y + 8;
        foreach (var (title, entries) in _legends)
        {
            _shapes.Add(new TextShape(x + 12, cy + 12, title, 11, "#0f172a", TextAnchor.Start, true));
            cy += 20;
            if (entries.All(e => e.Symbol == LegendSymbol.Gradient) && entries.Length >= 2)
            {
                _shapes.Add(new GradientShape(x + 12, cy, width - 24, 12, entries.Select(e => e.Color).ToList()));
                _shapes.Add(new TextShape(x + 12, cy + 26, entries[0].Label, 10, "#334155"));
                _shapes.Add(new TextShape(x + width - 12, cy + 26, entries[^1].Label, 10, "#334155", TextAnchor.End));
                cy += 34;
                continue;
            }
            foreach (var e in entries)
            {
                switch (e.Symbol)
                {
                    case LegendSymbol.Point: _shapes.Add(new CircleShape(x + 12 + sw / 2, cy + sw / 2, 5, e.Color, "#1f2933", 0.8)); break;
                    case LegendSymbol.Line: _shapes.Add(new PathShape([[x + 12, cy + sw / 2, x + 12 + sw, cy + sw / 2]], false, null, e.Color, 3)); break;
                    default: _shapes.Add(new RectShape(x + 12, cy, sw, sw - 2, e.Color, "#334155", 0.6, 2)); break;
                }
                _shapes.Add(new TextShape(x + 34, cy + 11, e.Label, 10, "#334155"));
                cy += row;
            }
            cy += 4;
        }
    }

    private void RenderScaleBar()
    {
        // metres per screen pixel at the map centre
        double unitsPerPx = 1 / _scale;
        double metresPerPx = unitsPerPx;
        if (_scene.Geographic)
        {
            double lat = (_extent.MinY + _extent.MaxY) / 2 * Math.PI / 180;
            metresPerPx = unitsPerPx * 111_320 * Math.Cos(lat);
        }
        double target = metresPerPx * 120;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double nice = new[] { 1, 2, 5, 10 }.Select(m => m * pow).Last(v => v <= target * 1.0001);
        double px = nice / metresPerPx;
        double x = _scene.ShowFrame ? _offX + 12 : _scene.Padding + 4;
        double y = _scene.ShowFrame ? _offY + _extent.Height * _scale - 12 : _h - _scene.Padding - 6;
        var ink = Rgba.Parse("#0f172a");
        _shapes.Add(new RectShape(x, y - 6, px / 2, 6, ink, ink, 0.8));
        _shapes.Add(new RectShape(x + px / 2, y - 6, px / 2, 6, Rgba.White, ink, 0.8));
        string label = nice >= 1000 ? $"{nice / 1000:0.##} km" : $"{nice:0.##} m";
        _shapes.Add(new TextShape(x + px + 6, y, label, 10, ink, TextAnchor.Start, false, Rgba.White.WithAlpha(0.8)));
    }

    private void RenderNorthArrow()
    {
        double x = _w - _scene.Padding - 14, y = _scene.Padding + (_scene.Title is null ? 6 : 10);
        var ink = Rgba.Parse("#0f172a");
        _shapes.Add(new PathShape([[x, y, x + 8, y + 22, x, y + 17, x - 8, y + 22]], true, ink, ink, 0.8));
        _shapes.Add(new TextShape(x, y + 36, "N", 11, ink, TextAnchor.Middle, true));
    }
}
