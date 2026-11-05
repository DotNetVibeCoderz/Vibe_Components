using System.Globalization;
using System.Net;
using System.Text;

namespace Agritech.Visualization;

public sealed record ChartSeries(string Name, IReadOnlyList<double> Values, string? Color = null, bool Area = false, bool Dashed = false);

/// <summary>Options shared by SVG charts. Colors default to CSS variables so host themes (light/dark) apply.</summary>
public sealed record ChartOptions
{
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 260;
    public string? Title { get; init; }
    public string? YLabel { get; init; }
    public IReadOnlyList<string>? XLabels { get; init; }
    public double? YMin { get; init; }
    public double? YMax { get; init; }
    public string AxisColor { get; init; } = "var(--agt-muted, #6b6f66)";
    public string GridColor { get; init; } = "var(--agt-grid, #e3e0d6)";
    public string TextColor { get; init; } = "var(--agt-ink, #23261f)";
    public string FontFamily { get; init; } = "inherit";
}

/// <summary>Dependency-free SVG chart builders (line/area, bar, sparkline, gauge, donut) producing accessible markup.</summary>
public static class SvgCharts
{
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Line(IReadOnlyList<ChartSeries> series, ChartOptions? o = null)
    {
        o ??= new ChartOptions();
        const int padL = 48, padR = 16, padB = 34;
        int padT = (series.Count > 1 ? 22 : 0) + (o.YLabel is not null ? 22 : 0) + 12;
        int w = o.Width, h = o.Height, pw = w - padL - padR, ph = h - padT - padB;
        var all = series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v)).ToList();
        double min = o.YMin ?? (all.Count == 0 ? 0 : all.Min()), max = o.YMax ?? (all.Count == 0 ? 1 : all.Max());
        if (max - min < 1e-9) { max += 1; min -= 1; }
        (min, max, double step) = NiceRange(min, max, 5);
        int n = series.Max(s => s.Values.Count);
        double X(int i) => padL + (n <= 1 ? pw / 2.0 : i * pw / (double)(n - 1));
        double Y(double v) => padT + ph - (v - min) / (max - min) * ph;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\" font-family=\"{o.FontFamily}\" font-size=\"11\">");
        if (o.Title is not null) sb.Append($"<title>{Esc(o.Title)}</title>");
        for (double v = min; v <= max + step / 2; v += step)
        {
            double y = Y(v);
            sb.Append($"<line x1=\"{padL}\" x2=\"{w - padR}\" y1=\"{N(y)}\" y2=\"{N(y)}\" stroke=\"{o.GridColor}\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{padL - 6}\" y=\"{N(y + 4)}\" text-anchor=\"end\" fill=\"{o.AxisColor}\">{N(v)}</text>");
        }
        if (o.XLabels is { Count: > 0 } xl)
        {
            int every = Math.Max(1, xl.Count / 8);
            for (int i = 0; i < xl.Count; i += every)
                sb.Append($"<text x=\"{N(X(i))}\" y=\"{h - 10}\" text-anchor=\"middle\" fill=\"{o.AxisColor}\">{Esc(xl[i])}</text>");
        }
        if (o.YLabel is not null) sb.Append($"<text x=\"{padL - 40}\" y=\"14\" fill=\"{o.AxisColor}\" font-weight=\"600\">{Esc(o.YLabel)}</text>");
        int k = 0;
        foreach (var s in series)
        {
            string color = s.Color ?? ColorMap.Categorical[k++ % ColorMap.Categorical.Length];
            var pts = s.Values.Select((v, i) => (v, i)).Where(p => !double.IsNaN(p.v)).Select(p => $"{N(X(p.i))},{N(Y(p.v))}").ToList();
            if (pts.Count == 0) continue;
            if (s.Area)
                sb.Append($"<polygon points=\"{N(X(0))},{N(Y(min))} {string.Join(' ', pts)} {N(X(s.Values.Count - 1))},{N(Y(min))}\" fill=\"{color}\" fill-opacity=\"0.14\"/>");
            sb.Append($"<polyline points=\"{string.Join(' ', pts)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2.2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"{(s.Dashed ? " stroke-dasharray=\"5 4\"" : "")}><title>{Esc(s.Name)}</title></polyline>");
        }
        // legend
        double lx = padL + 4, ly = o.YLabel is not null ? 34 : 14; k = 0;
        if (series.Count > 1)
            foreach (var s in series)
            {
                string color = s.Color ?? ColorMap.Categorical[k++ % ColorMap.Categorical.Length];
                sb.Append($"<rect x=\"{N(lx)}\" y=\"{N(ly - 4)}\" width=\"10\" height=\"3\" fill=\"{color}\" rx=\"1\"/><text x=\"{N(lx + 14)}\" y=\"{N(ly)}\" fill=\"{o.TextColor}\">{Esc(s.Name)}</text>");
                lx += 22 + s.Name.Length * 6.2;
            }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string Bars(IReadOnlyList<(string Label, double Value)> bars, ChartOptions? o = null, string? color = null, string valueFormat = "0.#")
    {
        o ??= new ChartOptions();
        const int padR = 56, padT = 10, padB = 10;
        int padL = (int)Math.Clamp(16 + bars.Max(b => b.Label.Length) * 6.4, 60, o.Width * 0.45);
        int w = o.Width, rowH = 26, h = Math.Max(o.Height, padT + padB + rowH * bars.Count);
        double max = o.YMax ?? Math.Max(1e-9, bars.Max(b => b.Value));
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\" font-family=\"{o.FontFamily}\" font-size=\"12\">");
        for (int i = 0; i < bars.Count; i++)
        {
            double y = padT + i * rowH;
            double bw = Math.Max(1, bars[i].Value / max * (w - padL - padR));
            string c = color ?? ColorMap.Categorical[i % ColorMap.Categorical.Length];
            sb.Append($"<text x=\"{padL - 8}\" y=\"{N(y + 17)}\" text-anchor=\"end\" fill=\"{o.TextColor}\">{Esc(bars[i].Label)}</text>");
            sb.Append($"<rect x=\"{padL}\" y=\"{N(y + 5)}\" width=\"{N(bw)}\" height=\"16\" rx=\"3\" fill=\"{c}\"/>");
            sb.Append($"<text x=\"{N(padL + bw + 6)}\" y=\"{N(y + 17)}\" fill=\"{o.AxisColor}\">{bars[i].Value.ToString(valueFormat, CultureInfo.InvariantCulture)}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    public static string Sparkline(IReadOnlyList<double> v, int width = 120, int height = 32, string color = "currentColor")
    {
        if (v.Count < 2) return "";
        double min = v.Min(), max = v.Max(); if (max - min < 1e-9) max = min + 1;
        var pts = v.Select((x, i) => $"{N(i * (width - 2) / (double)(v.Count - 1) + 1)},{N(height - 2 - (x - min) / (max - min) * (height - 4))}");
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" aria-hidden=\"true\"><polyline points=\"{string.Join(' ', pts)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.8\" stroke-linecap=\"round\"/></svg>";
    }

    /// <summary>Semicircular gauge (0–1 fraction) with label.</summary>
    public static string Gauge(double fraction, string label, string valueText, string color = "#2f6b3a", int size = 160)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double r = size * 0.38, cx = size / 2.0, cy = size * 0.56;
        double a = Math.PI * (1 - fraction);
        double ex = cx + r * Math.Cos(a), ey = cy - r * Math.Sin(a);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {N(size * 0.72)}\" width=\"100%\" role=\"img\">" +
               $"<path d=\"M {N(cx - r)} {N(cy)} A {N(r)} {N(r)} 0 0 1 {N(cx + r)} {N(cy)}\" fill=\"none\" stroke=\"var(--agt-grid, #e3e0d6)\" stroke-width=\"12\" stroke-linecap=\"round\"/>" +
               $"<path d=\"M {N(cx - r)} {N(cy)} A {N(r)} {N(r)} 0 0 1 {N(ex)} {N(ey)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"12\" stroke-linecap=\"round\"/>" +
               $"<text x=\"{N(cx)}\" y=\"{N(cy - 4)}\" text-anchor=\"middle\" font-size=\"{size / 7}\" font-weight=\"700\" fill=\"var(--agt-ink, #23261f)\">{Esc(valueText)}</text>" +
               $"<text x=\"{N(cx)}\" y=\"{N(cy + 16)}\" text-anchor=\"middle\" font-size=\"{size / 13}\" fill=\"var(--agt-muted, #6b6f66)\">{Esc(label)}</text></svg>";
    }

    public static (double Min, double Max, double Step) NiceRange(double min, double max, int ticks)
    {
        double range = Nice(max - min, false);
        double step = Nice(range / (ticks - 1), true);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    private static double Nice(double x, bool round)
    {
        double e = Math.Floor(Math.Log10(x)), f = x / Math.Pow(10, e);
        double nf = round ? f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10 : f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * Math.Pow(10, e);
    }

    internal static string Esc(string s) => WebUtility.HtmlEncode(s);
}
