using System.Text;
using Electrical.Core.Data;
using static Electrical.Visualization.SvgFormat;

namespace Electrical.Visualization;

/// <summary>A data series for line, bar and area charts.</summary>
/// <param name="Name">Legend label.</param>
/// <param name="Values">Y values (NaN = gap).</param>
/// <param name="Color">CSS color; defaults to the categorical theme variable for the series index.</param>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, string? Color = null)
{
    /// <summary>Optional explicit X values (same length as <see cref="Values"/>); otherwise the index is used.</summary>
    public IReadOnlyList<double>? X { get; init; }
    public bool Dashed { get; init; }
    /// <summary>Fill the area under the line with a translucent tint.</summary>
    public bool Area { get; init; }
    /// <summary>Stroke width [px].</summary>
    public double StrokeWidth { get; init; } = 2;

    /// <summary>Series from a <see cref="TimeSeries"/> (values by index; use <see cref="ChartOptions.ForTimeSeries"/> for the axis).</summary>
    public static ChartSeries FromTimeSeries(TimeSeries ts, string? color = null) =>
        new(ts.Name ?? "series", ts.Values, color);
}

/// <summary>Options shared by the cartesian SVG charts. All chrome colors are theme CSS variables.</summary>
public sealed record ChartOptions
{
    /// <summary>ViewBox width [px].</summary>
    public int Width { get; init; } = 640;
    /// <summary>ViewBox height [px].</summary>
    public int Height { get; init; } = 280;
    public string? Title { get; init; }
    public string? XLabel { get; init; }
    public string? YLabel { get; init; }
    public double? YMin { get; init; }
    public double? YMax { get; init; }
    /// <summary>Category labels for index-based X (bar charts, hourly profiles).</summary>
    public IReadOnlyList<string>? XLabels { get; init; }
    /// <summary>When set, index i is plotted at <c>TimeStart + i·TimeStep</c> and the X axis shows clock times.</summary>
    public DateTimeOffset? TimeStart { get; init; }
    public TimeSpan? TimeStep { get; init; }
    public bool ShowLegend { get; init; } = true;
    /// <summary>Paint a background rectangle with <c>--eln-bg</c> (default transparent).</summary>
    public bool Background { get; init; }
    /// <summary>Format for value labels.</summary>
    public string ValueFormat { get; init; } = "0.##";
    /// <summary>Horizontal reference lines (e.g. limits) drawn dashed.</summary>
    public IReadOnlyList<(double Y, string Label)>? ReferenceLines { get; init; }

    /// <summary>Options with a time axis matching <paramref name="ts"/>.</summary>
    public static ChartOptions ForTimeSeries(TimeSeries ts, string? title = null) =>
        new() { TimeStart = ts.Start, TimeStep = ts.Step, Title = title, YLabel = ts.Unit };
}

/// <summary>Colored band on a gauge (e.g. normal / alert / alarm ranges).</summary>
public sealed record GaugeBand(double From, double To, string Color);

/// <summary>Options for <see cref="SvgCharts.Heatmap"/>.</summary>
public sealed record HeatmapOptions
{
    public int CellWidth { get; init; } = 22;
    public int CellHeight { get; init; } = 18;
    public string? Title { get; init; }
    public ColorRamp Ramp { get; init; } = ColorRamp.Thermal;
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Print the value inside each cell.</summary>
    public bool ShowValues { get; init; }
    public string ValueFormat { get; init; } = "0.#";
    public string? Unit { get; init; }
    public bool ShowLegend { get; init; } = true;
}

/// <summary>Dependency-free SVG charts: line (multi-series, time axis), bar (grouped/stacked), stacked area, heatmap, gauge, sparkline, KPI tile.</summary>
public static partial class SvgCharts
{
    private static string ColorOf(ChartSeries s, int i) => s.Color ?? SvgTheme.Series(i);

    private sealed class Frame
    {
        public double L, T, W, H, XMin, XMax, YMin, YMax;
        public double X(double v) => L + (XMax - XMin < 1e-12 ? W / 2 : (v - XMin) / (XMax - XMin) * W);
        public double Y(double v) => T + H - (v - YMin) / (YMax - YMin) * H;
    }

    private static Frame Layout(ChartOptions o, int legendRows, double xMin, double xMax, double yMin, double yMax)
    {
        double top = 12 + (o.Title is null ? 0 : 20) + legendRows * 18 + (o.YLabel is null ? 0 : 14);
        double bottom = 28 + (o.XLabel is null ? 0 : 16);
        return new Frame { L = 54, T = top, W = o.Width - 54 - 18, H = o.Height - top - bottom, XMin = xMin, XMax = xMax, YMin = yMin, YMax = yMax };
    }

    private static int LegendRows(IReadOnlyList<ChartSeries> series, ChartOptions o)
    {
        if (!o.ShowLegend || series.Count < 2) return 0;
        double x = 0; int rows = 1;
        foreach (var s in series)
        {
            double w = 24 + s.Name.Length * 6.4;
            if (x + w > o.Width - 60) { rows++; x = 0; }
            x += w;
        }
        return rows;
    }

    private static void Chrome(StringBuilder sb, ChartOptions o, Frame f, double yStep, IReadOnlyList<ChartSeries> series, bool xNumeric)
    {
        string muted = SvgTheme.Var("eln-muted"), grid = SvgTheme.Var("eln-grid"), fg = SvgTheme.Var("eln-fg");
        if (o.Background) sb.Append($"<rect width=\"{o.Width}\" height=\"{o.Height}\" fill=\"{SvgTheme.Var("eln-bg")}\"/>");
        if (o.Title is not null) SvgText(sb, f.L - 44, 18, o.Title, fg, extra: "font-size=\"13\" font-weight=\"600\"");
        if (o.YLabel is not null) SvgText(sb, f.L - 44, f.T - 8, o.YLabel, muted, extra: "font-weight=\"600\"");
        foreach (var v in Ticks(f.YMin, f.YMax, yStep))
        {
            double y = f.Y(v);
            SvgLine(sb, f.L, y, f.L + f.W, y, grid);
            SvgText(sb, f.L - 6, y + 4, TickLabel(v, yStep), muted, "end");
        }
        SvgLine(sb, f.L, f.T + f.H, f.L + f.W, f.T + f.H, muted);
        double ly = f.T + f.H + 16;
        if (o.TimeStart is DateTimeOffset t0 && o.TimeStep is TimeSpan step)
        {
            var end = t0 + step * f.XMax;
            var (ticks, fmt) = TimeTicks(t0 + step * f.XMin, end);
            foreach (var t in ticks)
            {
                double x = f.X((t - t0) / step);
                SvgLine(sb, x, f.T + f.H, x, f.T + f.H + 4, muted);
                SvgText(sb, x, ly, t.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture), muted, "middle");
            }
        }
        else if (o.XLabels is { Count: > 0 } xl && !xNumeric)
        {
            int every = Math.Max(1, (int)Math.Ceiling(xl.Count / (f.W / 56)));
            for (int i = 0; i < xl.Count; i += every) SvgText(sb, f.X(i), ly, xl[i], muted, "middle");
        }
        else
        {
            var (_, _, xs) = NiceRange(f.XMin, f.XMax, 7);
            double first = Math.Ceiling(f.XMin / xs - 1e-9) * xs;
            for (double v = first; v <= f.XMax + xs * 1e-6; v += xs)
            {
                double x = f.X(v);
                SvgLine(sb, x, f.T + f.H, x, f.T + f.H + 4, muted);
                SvgText(sb, x, ly, TickLabel(v, xs), muted, "middle");
            }
        }
        if (o.XLabel is not null) SvgText(sb, f.L + f.W / 2, o.Height - 6, o.XLabel, muted, "middle", "font-weight=\"600\"");
        if (o.ReferenceLines is { } refs)
            foreach (var (y, label) in refs)
            {
                if (y < f.YMin || y > f.YMax) continue;
                SvgLine(sb, f.L, f.Y(y), f.L + f.W, f.Y(y), SvgTheme.Var("eln-danger"), 1.2, "stroke-dasharray=\"6 4\"");
                SvgText(sb, f.L + f.W - 4, f.Y(y) - 4, label, SvgTheme.Var("eln-danger"), "end");
            }
        // legend
        if (o.ShowLegend && series.Count > 1)
        {
            double lx = f.L, lyy = 12 + (o.Title is null ? 0 : 20) + 6;
            for (int i = 0; i < series.Count; i++)
            {
                double w = 24 + series[i].Name.Length * 6.4;
                if (lx + w > o.Width - 10 && lx > f.L) { lx = f.L; lyy += 18; }
                sb.Append($"<rect x=\"{N(lx)}\" y=\"{N(lyy - 6)}\" width=\"14\" height=\"4\" rx=\"2\" fill=\"{ColorOf(series[i], i)}\"/>");
                SvgText(sb, lx + 18, lyy, series[i].Name, fg);
                lx += w;
            }
        }
    }

    /// <summary>Multi-series line chart. X is the index (or <see cref="ChartSeries.X"/>); with <see cref="ChartOptions.TimeStart"/> the axis shows times.</summary>
    public static string Line(IReadOnlyList<ChartSeries> series, ChartOptions? options = null)
    {
        var o = options ?? new ChartOptions();
        bool numericX = series.Any(s => s.X is not null);
        var xs = series.SelectMany(s => s.X ?? Enumerable.Range(0, s.Values.Count).Select(i => (double)i).ToList()).ToList();
        var ys = series.SelectMany(s => s.Values).Where(double.IsFinite).ToList();
        if (o.ReferenceLines is { } refs) ys.AddRange(refs.Select(r => r.Y));
        double xmin = xs.Count == 0 ? 0 : xs.Min(), xmax = xs.Count == 0 ? 1 : xs.Max();
        var (ymin, ymax, ystep) = NiceRange(o.YMin ?? (ys.Count == 0 ? 0 : ys.Min()), o.YMax ?? (ys.Count == 0 ? 1 : ys.Max()));
        if (o.YMin is double y0) ymin = y0;
        if (o.YMax is double y1) ymax = y1;
        var f = Layout(o, LegendRows(series, o), xmin, xmax, ymin, ymax);
        var sb = Open(o.Width, o.Height, o.Title, "eln-chart eln-line");
        Chrome(sb, o, f, ystep, series, numericX);
        for (int k = 0; k < series.Count; k++)
        {
            var s = series[k];
            string color = ColorOf(s, k);
            var segs = new List<List<string>> { new() };
            for (int i = 0; i < s.Values.Count; i++)
            {
                double v = s.Values[i];
                if (!double.IsFinite(v)) { if (segs[^1].Count > 0) segs.Add([]); continue; }
                double x = s.X is { } sx ? sx[i] : i;
                segs[^1].Add($"{N(f.X(x))},{N(f.Y(Math.Clamp(v, ymin, ymax)))}");
            }
            sb.Append($"<g class=\"eln-series\"><title>{Esc(s.Name)}</title>");
            foreach (var pts in segs.Where(p => p.Count > 0))
            {
                if (s.Area)
                {
                    string first = pts[0].Split(',')[0], last = pts[^1].Split(',')[0];
                    sb.Append($"<polygon points=\"{first},{N(f.Y(Math.Max(ymin, 0)))} {string.Join(' ', pts)} {last},{N(f.Y(Math.Max(ymin, 0)))}\" fill=\"{color}\" fill-opacity=\"0.15\" stroke=\"none\"/>");
                }
                sb.Append($"<polyline points=\"{string.Join(' ', pts)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(s.StrokeWidth)}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"{(s.Dashed ? " stroke-dasharray=\"6 4\"" : "")}/>");
            }
            sb.Append("</g>");
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Line chart of several aligned <see cref="TimeSeries"/> with a clock-time X axis.</summary>
    public static string TimeSeries(IReadOnlyList<TimeSeries> series, ChartOptions? options = null)
    {
        if (series.Count == 0) throw new ArgumentException("At least one series is required.", nameof(series));
        var o = (options ?? new ChartOptions()) with { TimeStart = series[0].Start, TimeStep = series[0].Step };
        if (o.YLabel is null && series[0].Unit is not null) o = o with { YLabel = series[0].Unit };
        return Line(series.Select(s => ChartSeries.FromTimeSeries(s)).ToList(), o);
    }

    /// <summary>Vertical bar chart: one group per category, one bar per series (or stacked). Supports negative values.</summary>
    public static string Bar(IReadOnlyList<string> categories, IReadOnlyList<ChartSeries> series, ChartOptions? options = null, bool stacked = false)
    {
        var o = (options ?? new ChartOptions()) with { XLabels = categories };
        int n = categories.Count;
        double lo = 0, hi = 0;
        for (int c = 0; c < n; c++)
        {
            if (stacked)
            {
                double pos = 0, neg = 0;
                foreach (var s in series) { double v = c < s.Values.Count && double.IsFinite(s.Values[c]) ? s.Values[c] : 0; if (v >= 0) pos += v; else neg += v; }
                hi = Math.Max(hi, pos); lo = Math.Min(lo, neg);
            }
            else foreach (var s in series) if (c < s.Values.Count && double.IsFinite(s.Values[c])) { hi = Math.Max(hi, s.Values[c]); lo = Math.Min(lo, s.Values[c]); }
        }
        var (ymin, ymax, ystep) = NiceRange(o.YMin ?? lo, o.YMax ?? hi);
        var f = Layout(o, LegendRows(series, o), -0.5, n - 0.5, ymin, ymax);
        var sb = Open(o.Width, o.Height, o.Title, "eln-chart eln-bar");
        Chrome(sb, o, f, ystep, series, false);
        double slot = f.W / Math.Max(1, n), groupW = slot * 0.72;
        double barW = stacked ? groupW : groupW / Math.Max(1, series.Count);
        bool labels = !stacked && n * series.Count <= 16;
        for (int c = 0; c < n; c++)
        {
            double pos = 0, neg = 0;
            for (int k = 0; k < series.Count; k++)
            {
                var s = series[k];
                double v = c < s.Values.Count && double.IsFinite(s.Values[c]) ? s.Values[c] : 0;
                double x = f.X(c) - groupW / 2 + (stacked ? 0 : k * barW);
                double a, b;
                if (stacked) { if (v >= 0) { a = pos; pos += v; b = pos; } else { a = neg; neg += v; b = neg; } }
                else { a = 0; b = v; }
                double ya = f.Y(Math.Max(a, b)), yb = f.Y(Math.Min(a, b));
                sb.Append($"<rect x=\"{N(x + 1)}\" y=\"{N(ya)}\" width=\"{N(Math.Max(1, barW - 2))}\" height=\"{N(Math.Max(0.5, yb - ya))}\" rx=\"2\" fill=\"{ColorOf(s, k)}\"><title>{Esc($"{s.Name} · {categories[c]}: {V(v, o.ValueFormat)}")}</title></rect>");
                if (labels) SvgText(sb, x + barW / 2, v >= 0 ? ya - 4 : yb + 12, V(v, o.ValueFormat), SvgTheme.Var("eln-muted"), "middle", "font-size=\"10\"");
            }
        }
        if (ymin < 0) SvgLine(sb, f.L, f.Y(0), f.L + f.W, f.Y(0), SvgTheme.Var("eln-fg"), 1);
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Stacked area chart (e.g. generation mix over a day). Negative values are treated as zero.</summary>
    public static string StackedArea(IReadOnlyList<ChartSeries> series, ChartOptions? options = null)
    {
        var o = options ?? new ChartOptions();
        int n = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
        var cum = new double[series.Count + 1, n];
        for (int k = 0; k < series.Count; k++)
            for (int i = 0; i < n; i++)
            {
                double v = i < series[k].Values.Count && double.IsFinite(series[k].Values[i]) ? Math.Max(0, series[k].Values[i]) : 0;
                cum[k + 1, i] = cum[k, i] + v;
            }
        double top = 0;
        for (int i = 0; i < n; i++) top = Math.Max(top, cum[series.Count, i]);
        var (ymin, ymax, ystep) = NiceRange(0, o.YMax ?? top);
        ymin = 0;
        var f = Layout(o, LegendRows(series, o), 0, Math.Max(1, n - 1), ymin, ymax);
        var sb = Open(o.Width, o.Height, o.Title, "eln-chart eln-area");
        Chrome(sb, o, f, ystep, series, false);
        for (int k = series.Count - 1; k >= 0; k--)
        {
            var upper = Enumerable.Range(0, n).Select(i => $"{N(f.X(i))},{N(f.Y(cum[k + 1, i]))}");
            var lower = Enumerable.Range(0, n).Reverse().Select(i => $"{N(f.X(i))},{N(f.Y(cum[k, i]))}");
            string color = ColorOf(series[k], k);
            sb.Append($"<polygon points=\"{string.Join(' ', upper.Concat(lower))}\" fill=\"{color}\" fill-opacity=\"0.78\" stroke=\"{color}\" stroke-width=\"1\"><title>{Esc(series[k].Name)}</title></polygon>");
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Heatmap of <c>values[row, col]</c> with optional row/column labels and a color legend (e.g. hour × day loading).</summary>
    public static string Heatmap(double[,] values, IReadOnlyList<string>? rowLabels = null, IReadOnlyList<string>? columnLabels = null, HeatmapOptions? options = null)
    {
        var o = options ?? new HeatmapOptions();
        int rows = values.GetLength(0), cols = values.GetLength(1);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in values) if (double.IsFinite(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (lo > hi) (lo, hi) = (0, 1);
        lo = o.Min ?? lo; hi = o.Max ?? hi;
        if (hi - lo < 1e-12) hi = lo + 1;
        int padL = rowLabels is null ? 10 : (int)Math.Clamp(12 + rowLabels.Max(l => l.Length) * 6.2, 30, 160);
        int padT = (o.Title is null ? 8 : 28) + (columnLabels is null ? 0 : 18);
        int legendH = o.ShowLegend ? 44 : 6;
        int w = padL + cols * o.CellWidth + 12, h = padT + rows * o.CellHeight + legendH;
        w = Math.Max(w, padL + 220);
        var sb = Open(w, h, o.Title, "eln-chart eln-heatmap");
        string muted = SvgTheme.Var("eln-muted"), fg = SvgTheme.Var("eln-fg");
        if (o.Title is not null) SvgText(sb, 8, 18, o.Title, fg, extra: "font-size=\"13\" font-weight=\"600\"");
        double Norm(double v) => o.Ramp.Min + (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * (o.Ramp.Max - o.Ramp.Min);
        if (columnLabels is not null)
        {
            int every = Math.Max(1, (int)Math.Ceiling(28.0 / o.CellWidth));
            for (int c = 0; c < cols && c < columnLabels.Count; c += every)
                SvgText(sb, padL + (c + 0.5) * o.CellWidth, padT - 6, columnLabels[c], muted, "middle", "font-size=\"10\"");
        }
        for (int r = 0; r < rows; r++)
        {
            double y = padT + r * o.CellHeight;
            if (rowLabels is not null && r < rowLabels.Count) SvgText(sb, padL - 6, y + o.CellHeight / 2.0 + 4, rowLabels[r], muted, "end", "font-size=\"10\"");
            for (int c = 0; c < cols; c++)
            {
                double v = values[r, c];
                double x = padL + c * o.CellWidth;
                string fill = double.IsFinite(v) ? o.Ramp.Hex(Norm(v)) : SvgTheme.Var("eln-grid");
                sb.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{o.CellWidth - 1}\" height=\"{o.CellHeight - 1}\" fill=\"{fill}\"><title>{Esc(V(v, o.ValueFormat) + (o.Unit is null ? "" : " " + o.Unit))}</title></rect>");
                if (o.ShowValues && double.IsFinite(v))
                {
                    var c0 = o.Ramp.Map(Norm(v));
                    string ink = 0.299 * c0.R + 0.587 * c0.G + 0.114 * c0.B > 140 ? "#0b1626" : "#ffffff";
                    SvgText(sb, x + o.CellWidth / 2.0, y + o.CellHeight / 2.0 + 3.5, V(v, o.ValueFormat), ink, "middle", "font-size=\"9\"");
                }
            }
        }
        if (o.ShowLegend) ColorLegend(sb, o.Ramp, padL, padT + rows * o.CellHeight + 12, 200, lo, hi, o.Unit, o.ValueFormat);
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Appends a horizontal gradient legend for <paramref name="ramp"/> labelled from <paramref name="lo"/> to <paramref name="hi"/>.</summary>
    internal static void ColorLegend(StringBuilder sb, ColorRamp ramp, double x, double y, double width, double lo, double hi, string? unit, string format = "0.##")
    {
        string id = StableId("eln-grad-", ramp.Name + string.Join(",", ramp.Stops.Select(s => s.Color.Hex)));
        sb.Append($"<defs><linearGradient id=\"{id}\" x1=\"0\" x2=\"1\" y1=\"0\" y2=\"0\">");
        foreach (var (p, c) in ramp.Stops)
            sb.Append($"<stop offset=\"{N((p - ramp.Min) / (ramp.Max - ramp.Min))}\" stop-color=\"{c.Hex}\"/>");
        sb.Append("</linearGradient></defs>");
        sb.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(width)}\" height=\"10\" rx=\"2\" fill=\"url(#{id})\"/>");
        string muted = SvgTheme.Var("eln-muted");
        SvgText(sb, x, y + 24, V(lo, format), muted, extra: "font-size=\"10\"");
        SvgText(sb, x + width, y + 24, V(hi, format) + (unit is null ? "" : " " + unit), muted, "end", "font-size=\"10\"");
    }

    /// <summary>240° gauge for a value in [min, max] with optional colored bands; the value arc takes the color of the band it is in.</summary>
    public static string Gauge(double value, double min, double max, string label, string? unit = null, IReadOnlyList<GaugeBand>? bands = null,
        int size = 200, string format = "0.##")
    {
        if (max <= min) throw new ArgumentException("max must exceed min.", nameof(max));
        double cx = size / 2.0, cy = size * 0.52, r = size * 0.38;
        const double start = 210 * Math.PI / 180, sweep = 240 * Math.PI / 180;
        double A(double v) => start - Math.Clamp((v - min) / (max - min), 0, 1) * sweep;
        int h = (int)(size * 0.86);
        var sb = Open(size, h, $"{label}: {V(value, format)} {unit}", "eln-gauge");
        double sw = size * 0.07;
        sb.Append($"<path d=\"{Arc(cx, cy, r, start, start - sweep)}\" fill=\"none\" stroke=\"{SvgTheme.Var("eln-grid")}\" stroke-width=\"{N(sw)}\" stroke-linecap=\"round\"/>");
        string valueColor = SvgTheme.Var("eln-accent");
        if (bands is not null)
            foreach (var b in bands)
            {
                sb.Append($"<path d=\"{Arc(cx, cy, r + sw * 0.95, A(b.From), A(b.To))}\" fill=\"none\" stroke=\"{b.Color}\" stroke-width=\"{N(sw * 0.3)}\"/>");
                if (value >= b.From && value <= b.To) valueColor = b.Color;
            }
        if (value > min)
            sb.Append($"<path d=\"{Arc(cx, cy, r, start, A(value))}\" fill=\"none\" stroke=\"{valueColor}\" stroke-width=\"{N(sw)}\" stroke-linecap=\"round\"/>");
        double a = A(value);
        double nx = cx + (r - sw) * Math.Cos(a), ny = cy - (r - sw) * Math.Sin(a);
        SvgLine(sb, cx, cy, nx, ny, SvgTheme.Var("eln-fg"), Math.Max(1.5, size / 90.0), "stroke-linecap=\"round\"");
        sb.Append($"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(size * 0.025)}\" fill=\"{SvgTheme.Var("eln-fg")}\"/>");
        string muted = SvgTheme.Var("eln-muted");
        SvgText(sb, cx, cy + r * 0.62, V(value, format) + (unit is null ? "" : " " + unit), SvgTheme.Var("eln-fg"), "middle", $"font-size=\"{N(size / 9.0)}\" font-weight=\"700\"");
        SvgText(sb, cx, cy + r * 0.62 + size / 10.0, label, muted, "middle", $"font-size=\"{N(size / 15.0)}\"");
        SvgText(sb, cx + r * Math.Cos(start), cy - r * Math.Sin(start) + sw + 6, V(min, format), muted, "middle", "font-size=\"9\"");
        SvgText(sb, cx + r * Math.Cos(start - sweep), cy - r * Math.Sin(start - sweep) + sw + 6, V(max, format), muted, "middle", "font-size=\"9\"");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Compact sparkline [px] with an end-point dot; <paramref name="color"/> defaults to the accent variable.</summary>
    public static string Sparkline(IReadOnlyList<double> values, int width = 120, int height = 32, string? color = null, bool area = true)
    {
        color ??= SvgTheme.Var("eln-accent");
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" class=\"eln-sparkline\" aria-hidden=\"true\">");
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count >= 2)
        {
            double min = finite.Min(), max = finite.Max();
            if (max - min < 1e-12) { max += 1; min -= 1; }
            double X(int i) => 2 + i * (width - 4) / (double)(values.Count - 1);
            double Y(double v) => height - 3 - (v - min) / (max - min) * (height - 6);
            var pts = values.Select((v, i) => (v, i)).Where(p => double.IsFinite(p.v)).Select(p => $"{N(X(p.i))},{N(Y(p.v))}").ToList();
            if (area) sb.Append($"<polygon points=\"{N(X(0))},{height} {string.Join(' ', pts)} {N(X(values.Count - 1))},{height}\" fill=\"{color}\" fill-opacity=\"0.14\"/>");
            sb.Append($"<polyline points=\"{string.Join(' ', pts)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.6\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
            int last = values.Count - 1;
            while (last > 0 && !double.IsFinite(values[last])) last--;
            sb.Append($"<circle cx=\"{N(X(last))}\" cy=\"{N(Y(values[last]))}\" r=\"2.4\" fill=\"{color}\"/>");
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>KPI tile (240×110 px): label, big value with unit, optional delta text and sparkline.</summary>
    /// <param name="deltaGood">Colors the delta green when true, red when false, muted when null.</param>
    public static string KpiTile(string label, string value, string? unit = null, IReadOnlyList<double>? trend = null, string? delta = null, bool? deltaGood = null)
    {
        const int w = 240, h = 110;
        var sb = Open(w, h, $"{label}: {value} {unit}", "eln-kpi");
        sb.Append($"<rect x=\"0.5\" y=\"0.5\" width=\"{w - 1}\" height=\"{h - 1}\" rx=\"10\" fill=\"{SvgTheme.Var("eln-surface")}\" stroke=\"{SvgTheme.Var("eln-grid")}\"/>");
        sb.Append($"<rect x=\"0.5\" y=\"10\" width=\"3\" height=\"{h - 20}\" rx=\"1.5\" fill=\"{SvgTheme.Var("eln-accent")}\"/>");
        SvgText(sb, 16, 24, label.ToUpperInvariant(), SvgTheme.Var("eln-muted"), extra: "font-size=\"10\" font-weight=\"600\" letter-spacing=\"0.08em\"");
        sb.Append($"<text x=\"16\" y=\"60\" fill=\"{SvgTheme.Var("eln-fg")}\" font-size=\"28\" font-weight=\"700\">{Esc(value)}");
        if (unit is not null) sb.Append($"<tspan dx=\"4\" font-size=\"13\" font-weight=\"500\" fill=\"{SvgTheme.Var("eln-muted")}\">{Esc(unit)}</tspan>");
        sb.Append("</text>");
        if (delta is not null)
        {
            string c = deltaGood switch { true => SvgTheme.Var("eln-ok"), false => SvgTheme.Var("eln-danger"), _ => SvgTheme.Var("eln-muted") };
            SvgText(sb, 16, 90, delta, c, extra: "font-size=\"11\" font-weight=\"600\"");
        }
        if (trend is { Count: >= 2 })
        {
            string spark = Sparkline(trend, 96, 34);
            sb.Append($"<g transform=\"translate({w - 108},{h - 46})\">").Append(spark.Replace("<svg ", "<svg x=\"0\" y=\"0\" ", StringComparison.Ordinal)).Append("</g>");
        }
        return sb.Append("</svg>").ToString();
    }
}
