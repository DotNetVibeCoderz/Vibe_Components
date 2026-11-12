using System.Globalization;
using System.Text;
using AquaCulture.Core.Geo;

namespace AquaCulture.Visualization;

/// <summary>A data series for charts.</summary>
public sealed record Series(string Name, IReadOnlyList<(double X, double Y)> Points, string? Color = null, bool Dashed = false, bool Area = false);

/// <summary>Horizontal reference line (threshold) or band.</summary>
public sealed record Reference(double Y, string Label, string Color = "#e0614a", double? Y2 = null);

/// <summary>Chart options.</summary>
public sealed record ChartOptions
{
    public int Width { get; init; } = 720;
    public int Height { get; init; } = 300;
    public string? Title { get; init; }
    public string? XLabel { get; init; }
    public string? YLabel { get; init; }
    public double? YMin { get; init; }
    public double? YMax { get; init; }
    /// <summary>Formats X tick labels (e.g. day numbers or hours).</summary>
    public Func<double, string>? XFormat { get; init; }
    public string YFormat { get; init; } = "0.#";
    public IReadOnlyList<Reference> References { get; init; } = [];
    public bool Legend { get; init; } = true;
}

/// <summary>
/// Dependency-free SVG charts. Colours use CSS custom properties with literal fallbacks (<c>var(--aq-ink, #0b2e36)</c>)
/// so web pages can theme them; desktop renderers can resolve the fallbacks.
/// </summary>
public static class SvgCharts
{
    public static readonly string[] Palette = ["#12a4b6", "#e0614a", "#3e8e5e", "#d9a13b", "#6c5ca8", "#2f6fb0", "#8a6a4a"];
    private const string Ink = "var(--aq-ink, #0b2e36)", Muted = "var(--aq-muted, #5b7279)", Grid = "var(--aq-grid, #d3e0de)";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.##", Inv);
    public static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>"Nice" tick values covering [min, max].</summary>
    public static double[] Ticks(double min, double max, int count = 5)
    {
        if (max <= min) max = min + 1;
        double range = max - min, rough = range / count, mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= rough);
        double start = Math.Ceiling(min / step) * step;
        var t = new List<double>();
        for (double v = start; v <= max + step * 1e-9; v += step) { double r = Math.Round(v, 10); t.Add(Math.Abs(r) < step * 1e-9 ? 0 : r); }
        return t.ToArray();
    }

    public static string Line(IReadOnlyList<Series> series, ChartOptions? o = null)
    {
        o ??= new ChartOptions();
        var all = series.SelectMany(s => s.Points).Where(p => double.IsFinite(p.Y)).ToList();
        if (all.Count == 0) return Empty(o);
        double xMin = all.Min(p => p.X), xMax = all.Max(p => p.X);
        double yMin = o.YMin ?? Math.Min(all.Min(p => p.Y), o.References.Count > 0 ? o.References.Min(r => r.Y) : double.MaxValue);
        double yMax = o.YMax ?? Math.Max(all.Max(p => p.Y), o.References.Count > 0 ? o.References.Max(r => r.Y2 ?? r.Y) : double.MinValue);
        if (o.YMin is null) yMin -= (yMax - yMin) * 0.05;
        if (o.YMax is null) yMax += (yMax - yMin) * 0.08;
        if (xMax <= xMin) xMax = xMin + 1;
        var yt = Ticks(yMin, yMax);
        if (o.YMin is null) yMin = Math.Min(yMin, yt[0]);
        double l = 52, r = 16, t = o.Title is null ? 14 : 34, b = 40 + (o.Legend && series.Count > 1 ? 18 : 0);
        double w = o.Width - l - r, h = o.Height - t - b;
        double X(double x) => l + (x - xMin) / (xMax - xMin) * w;
        double Y(double y) => t + h - (y - yMin) / (yMax - yMin) * h;

        var sb = Begin(o);
        foreach (var ref_ in o.References.Where(x => x.Y2 is not null))
            sb.Append(Inv, $"<rect x=\"{F(l)}\" y=\"{F(Y(ref_.Y2!.Value))}\" width=\"{F(w)}\" height=\"{F(Math.Abs(Y(ref_.Y) - Y(ref_.Y2.Value)))}\" fill=\"{ref_.Color}\" opacity=\"0.10\"/>");
        foreach (var v in yt.Where(v => v >= yMin && v <= yMax))
            sb.Append(Inv, $"<line x1=\"{F(l)}\" x2=\"{F(l + w)}\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\" stroke=\"{Grid}\" stroke-width=\"1\"/><text x=\"{F(l - 6)}\" y=\"{F(Y(v) + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{Muted}\">{v.ToString(o.YFormat, Inv)}</text>");
        foreach (var v in Ticks(xMin, xMax, 6).Where(v => v >= xMin && v <= xMax))
            sb.Append(Inv, $"<text x=\"{F(X(v))}\" y=\"{F(t + h + 16)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Muted}\">{Esc(o.XFormat?.Invoke(v) ?? v.ToString("0.#", Inv))}</text>");
        sb.Append(Inv, $"<line x1=\"{F(l)}\" x2=\"{F(l + w)}\" y1=\"{F(t + h)}\" y2=\"{F(t + h)}\" stroke=\"{Muted}\" stroke-width=\"1\"/>");
        foreach (var ref_ in o.References.Where(x => x.Y2 is null))
            sb.Append(Inv, $"<line x1=\"{F(l)}\" x2=\"{F(l + w)}\" y1=\"{F(Y(ref_.Y))}\" y2=\"{F(Y(ref_.Y))}\" stroke=\"{ref_.Color}\" stroke-width=\"1.5\" stroke-dasharray=\"6 4\"/><text x=\"{F(l + w - 4)}\" y=\"{F(Y(ref_.Y) - 5)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{ref_.Color}\">{Esc(ref_.Label)}</text>");

        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i]; string col = s.Color ?? Palette[i % Palette.Length];
            var pts = s.Points.Where(p => double.IsFinite(p.Y)).ToList();
            if (pts.Count == 0) continue;
            string path = string.Join(" ", pts.Select((p, k) => $"{(k == 0 ? "M" : "L")}{F(X(p.X))},{F(Y(p.Y))}"));
            if (s.Area) sb.Append(Inv, $"<path d=\"{path} L{F(X(pts[^1].X))},{F(t + h)} L{F(X(pts[0].X))},{F(t + h)} Z\" fill=\"{col}\" opacity=\"0.14\"/>");
            sb.Append(Inv, $"<path d=\"{path}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"2.2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"{(s.Dashed ? " stroke-dasharray=\"5 4\"" : "")}/>");
            if (pts.Count <= 24) foreach (var p in pts) sb.Append(Inv, $"<circle cx=\"{F(X(p.X))}\" cy=\"{F(Y(p.Y))}\" r=\"2.6\" fill=\"{col}\"/>");
        }
        Axes(sb, o, l, t, w, h);
        if (o.Legend && series.Count > 1) Legend(sb, series.Select((s, i) => (s.Name, s.Color ?? Palette[i % Palette.Length], s.Dashed)).ToList(), l, o.Height - 12);
        return sb.Append("</svg>").ToString();
    }

    public static string Bars(IReadOnlyList<(string Label, double Value)> bars, ChartOptions? o = null, IReadOnlyList<string>? colors = null)
    {
        o ??= new ChartOptions();
        if (bars.Count == 0) return Empty(o);
        double max = o.YMax ?? bars.Max(b => b.Value) * 1.12, min = o.YMin ?? Math.Min(0, bars.Min(b => b.Value));
        double l = 52, r = 12, t = o.Title is null ? 14 : 34, bt = 44;
        double w = o.Width - l - r, h = o.Height - t - bt, bw = w / bars.Count;
        double Y(double y) => t + h - (y - min) / (max - min) * h;
        var sb = Begin(o);
        foreach (var v in Ticks(min, max))
            sb.Append(Inv, $"<line x1=\"{F(l)}\" x2=\"{F(l + w)}\" y1=\"{F(Y(v))}\" y2=\"{F(Y(v))}\" stroke=\"{Grid}\"/><text x=\"{F(l - 6)}\" y=\"{F(Y(v) + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{Muted}\">{v.ToString(o.YFormat, Inv)}</text>");
        for (int i = 0; i < bars.Count; i++)
        {
            var (label, value) = bars[i];
            string col = colors?[i % colors.Count] ?? Palette[0];
            double x = l + i * bw + bw * 0.18, y0 = Y(Math.Max(0, value)), y1 = Y(Math.Min(0, value));
            sb.Append(Inv, $"<rect x=\"{F(x)}\" y=\"{F(y0)}\" width=\"{F(bw * 0.64)}\" height=\"{F(Math.Max(1, y1 - y0))}\" rx=\"3\" fill=\"{col}\"><title>{Esc(label)}: {value.ToString(o.YFormat, Inv)}</title></rect>");
            sb.Append(Inv, $"<text x=\"{F(x + bw * 0.32)}\" y=\"{F(y0 - 5)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Ink}\">{value.ToString(o.YFormat, Inv)}</text>");
            sb.Append(Inv, $"<text x=\"{F(x + bw * 0.32)}\" y=\"{F(t + h + 16)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Muted}\">{Esc(label)}</text>");
        }
        Axes(sb, o, l, t, w, h);
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Semicircular gauge for a 0–max value with coloured zones.</summary>
    public static string Gauge(double value, double max, string label, string unit = "", ColorRamp? ramp = null, int size = 220)
    {
        ramp ??= ColorRamp.Score.WithRange(0, max);
        double cx = size / 2.0, cy = size * 0.58, rad = size * 0.4;
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {F(size * 0.75)}\" font-family=\"inherit\" role=\"img\" aria-label=\"{Esc(label)} {F(value)} {Esc(unit)}\">");
        int segs = 40;
        for (int i = 0; i < segs; i++)
        {
            double a0 = Math.PI * (1 - (double)i / segs), a1 = Math.PI * (1 - (double)(i + 1) / segs);
            var c = ramp[max * (i + 0.5) / segs];
            sb.Append(Inv, $"<path d=\"M{F(cx + rad * Math.Cos(a0))},{F(cy - rad * Math.Sin(a0))} A{F(rad)},{F(rad)} 0 0 1 {F(cx + rad * Math.Cos(a1))},{F(cy - rad * Math.Sin(a1))}\" stroke=\"{c.Hex}\" stroke-width=\"{F(size * 0.07)}\" fill=\"none\"/>");
        }
        double v = Math.Clamp(value / max, 0, 1), an = Math.PI * (1 - v);
        sb.Append(Inv, $"<line x1=\"{F(cx)}\" y1=\"{F(cy)}\" x2=\"{F(cx + rad * 0.86 * Math.Cos(an))}\" y2=\"{F(cy - rad * 0.86 * Math.Sin(an))}\" stroke=\"{Ink}\" stroke-width=\"3\" stroke-linecap=\"round\"/><circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"6\" fill=\"{Ink}\"/>");
        sb.Append(Inv, $"<text x=\"{F(cx)}\" y=\"{F(cy + size * 0.13)}\" text-anchor=\"middle\" font-size=\"{F(size * 0.13)}\" font-weight=\"700\" fill=\"{Ink}\">{F(value)}<tspan font-size=\"{F(size * 0.06)}\" fill=\"{Muted}\"> {Esc(unit)}</tspan></text>");
        sb.Append(Inv, $"<text x=\"{F(cx)}\" y=\"{F(size * 0.1)}\" text-anchor=\"middle\" font-size=\"{F(size * 0.055)}\" fill=\"{Muted}\">{Esc(label)}</text>");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Tiny inline trend line.</summary>
    public static string Sparkline(IReadOnlyList<double> values, int width = 120, int height = 32, string color = "#12a4b6")
    {
        var v = values.Where(double.IsFinite).ToList();
        if (v.Count < 2) return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\"></svg>";
        double min = v.Min(), max = v.Max(); if (max == min) max = min + 1;
        string path = string.Join(" ", v.Select((y, i) => $"{(i == 0 ? "M" : "L")}{F(2 + i * (width - 4.0) / (v.Count - 1))},{F(height - 3 - (y - min) / (max - min) * (height - 6))}"));
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\"><path d=\"{path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.8\"/></svg>";
    }

    /// <summary>Category × column heat map (e.g. pond × hour DO).</summary>
    public static string Heatmap(IReadOnlyList<string> rows, IReadOnlyList<string> cols, double[,] values, ColorRamp ramp, ChartOptions? o = null)
    {
        o ??= new ChartOptions { Height = 40 + rows.Count * 26 };
        double l = 70, t = o.Title is null ? 8 : 30, b = 24, w = o.Width - l - 10, h = o.Height - t - b;
        double cw = w / cols.Count, ch = h / rows.Count;
        var sb = Begin(o);
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append(Inv, $"<text x=\"{F(l - 6)}\" y=\"{F(t + r * ch + ch / 2 + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"{Muted}\">{Esc(rows[r])}</text>");
            for (int c = 0; c < cols.Count; c++)
                sb.Append(Inv, $"<rect x=\"{F(l + c * cw)}\" y=\"{F(t + r * ch)}\" width=\"{F(cw - 1)}\" height=\"{F(ch - 1)}\" fill=\"{ramp[values[r, c]].Hex}\"><title>{Esc(rows[r])} · {Esc(cols[c])}: {F(values[r, c])}</title></rect>");
        }
        int step = Math.Max(1, cols.Count / 12);
        for (int c = 0; c < cols.Count; c += step)
            sb.Append(Inv, $"<text x=\"{F(l + c * cw + cw / 2)}\" y=\"{F(t + h + 15)}\" text-anchor=\"middle\" font-size=\"10\" fill=\"{Muted}\">{Esc(cols[c])}</text>");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Plan-view map of polygons (ponds) and points (sensors) coloured by value.</summary>
    public static string Map(IReadOnlyList<(string Id, GeoPolygon Polygon, double Value, string Label)> polygons, ColorRamp ramp,
        IReadOnlyList<(GeoCoordinate Point, string Label, string Color)>? points = null, int width = 640, int height = 420, string? background = null)
    {
        var all = polygons.SelectMany(p => p.Polygon.Ring).Concat(points?.Select(p => p.Point) ?? []).ToList();
        var bounds = new GeoBounds(all.Min(c => c.Latitude), all.Min(c => c.Longitude), all.Max(c => c.Latitude), all.Max(c => c.Longitude));
        var o = bounds.Center;
        var loc = all.Select(c => GeoMath.ToLocal(o, c)).ToList();
        double minE = loc.Min(p => p.East), maxE = loc.Max(p => p.East), minN = loc.Min(p => p.North), maxN = loc.Max(p => p.North);
        double pad = 16, sc = Math.Min((width - 2 * pad) / Math.Max(1, maxE - minE), (height - 2 * pad) / Math.Max(1, maxN - minN));
        double ox = (width - (maxE - minE) * sc) / 2, oy = (height - (maxN - minN) * sc) / 2;
        (double X, double Y) P(GeoCoordinate c) { var lp = GeoMath.ToLocal(o, c); return (ox + (lp.East - minE) * sc, height - oy - (lp.North - minN) * sc); }
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" font-family=\"inherit\" role=\"img\" aria-label=\"Farm map\">");
        sb.Append(Inv, $"<rect width=\"{width}\" height=\"{height}\" fill=\"{background ?? "var(--aq-land, #dfe9d8)"}\" rx=\"8\"/>");
        foreach (var (id, poly, value, label) in polygons)
        {
            string d = string.Join(" ", poly.Ring.Select((c, i) => { var (x, y) = P(c); return $"{(i == 0 ? "M" : "L")}{F(x)},{F(y)}"; })) + " Z";
            sb.Append(Inv, $"<path d=\"{d}\" fill=\"{ramp[value].Hex}\" stroke=\"var(--aq-dike, #8a7a5a)\" stroke-width=\"3\" stroke-linejoin=\"round\"><title>{Esc(label)}</title></path>");
            var (cx, cy) = P(poly.Centroid);
            sb.Append(Inv, $"<text x=\"{F(cx)}\" y=\"{F(cy - 2)}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"700\" fill=\"#0b2e36\">{Esc(id)}</text>");
            sb.Append(Inv, $"<text x=\"{F(cx)}\" y=\"{F(cy + 13)}\" text-anchor=\"middle\" font-size=\"10.5\" fill=\"#0b2e36\">{Esc(label)}</text>");
        }
        if (points is not null)
            foreach (var (pt, label, color) in points)
            {
                var (x, y) = P(pt);
                sb.Append(Inv, $"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"5\" fill=\"{color}\" stroke=\"#fff\" stroke-width=\"1.5\"><title>{Esc(label)}</title></circle>");
            }
        return sb.Append("</svg>").ToString();
    }

    private static StringBuilder Begin(ChartOptions o)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {o.Width} {o.Height}\" font-family=\"inherit\" role=\"img\" aria-label=\"{Esc(o.Title ?? "chart")}\">");
        if (o.Title is not null) sb.Append(Inv, $"<text x=\"12\" y=\"20\" font-size=\"13\" font-weight=\"600\" fill=\"{Ink}\">{Esc(o.Title)}</text>");
        return sb;
    }

    private static void Axes(StringBuilder sb, ChartOptions o, double l, double t, double w, double h)
    {
        if (o.YLabel is not null) sb.Append(Inv, $"<text x=\"12\" y=\"{F(t + h / 2)}\" transform=\"rotate(-90 12 {F(t + h / 2)})\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Muted}\">{Esc(o.YLabel)}</text>");
        if (o.XLabel is not null) sb.Append(Inv, $"<text x=\"{F(l + w / 2)}\" y=\"{F(t + h + 32)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"{Muted}\">{Esc(o.XLabel)}</text>");
    }

    private static void Legend(StringBuilder sb, IReadOnlyList<(string Name, string Color, bool Dashed)> items, double x, double y)
    {
        foreach (var (name, color, dashed) in items)
        {
            sb.Append(Inv, $"<line x1=\"{F(x)}\" x2=\"{F(x + 18)}\" y1=\"{F(y - 4)}\" y2=\"{F(y - 4)}\" stroke=\"{color}\" stroke-width=\"2.5\"{(dashed ? " stroke-dasharray=\"4 3\"" : "")}/><text x=\"{F(x + 23)}\" y=\"{F(y)}\" font-size=\"11\" fill=\"{Muted}\">{Esc(name)}</text>");
            x += 32 + name.Length * 6.2;
        }
    }

    private static string Empty(ChartOptions o) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {o.Width} {o.Height}\"><text x=\"{o.Width / 2}\" y=\"{o.Height / 2}\" text-anchor=\"middle\" fill=\"#5b7279\">No data</text></svg>";
}
