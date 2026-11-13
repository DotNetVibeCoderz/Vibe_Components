using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Medical.Clinical;
using Medical.Core;

namespace Medical.Visualization;

/// <summary>Theme tokens. SVG uses <c>var(--token, fallback)</c> so the web app can restyle it; desktop apps call <see cref="SvgTheme.Resolve"/>.</summary>
public static partial class SvgTheme
{
    public const string Ink = "var(--ink, #14233A)";
    public const string Muted = "var(--muted, #5B6B7E)";
    public const string Paper = "var(--paper, #F3F6F7)";
    public const string Rule = "var(--rule, #D5DDE3)";
    public const string GridMinor = "var(--ecg-minor, #F6DAD8)";
    public const string GridMajor = "var(--ecg-major, #E39C97)";
    public const string Trace = "var(--ecg-trace, #14233A)";
    public const string Teal = "var(--teal, #0F6E6E)";
    public const string TealSoft = "var(--teal-soft, #CFE5E3)";
    public const string Amber = "var(--amber, #E8A33D)";
    public const string Red = "var(--red, #C8372D)";
    public const string Score1 = "var(--score1, #FBE8B4)";
    public const string Score2 = "var(--score2, #F4B36B)";
    public const string Score3 = "var(--score3, #E0675C)";

    public const string MonoFont = "'IBM Plex Mono', ui-monospace, monospace";
    public const string LabelFont = "'IBM Plex Sans Condensed', 'Atkinson Hyperlegible', sans-serif";

    [GeneratedRegex(@"var\(--[a-z0-9-]+,\s*([^)]+)\)")] private static partial Regex VarRx();

    /// <summary>Replaces CSS variables with their fallback colours (for renderers without CSS, e.g. Svg.Skia).</summary>
    public static string Resolve(string svg) => VarRx().Replace(svg, m => m.Groups[1].Value.Trim());
}

/// <summary>A named time series.</summary>
public sealed record Series(string Name, IReadOnlyList<(DateTimeOffset T, double V)> Points, string Color);

/// <summary>Dependency-free SVG chart builders.</summary>
public static partial class Charts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v) => v.ToString("0.##", Inv);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static StringBuilder Open(int w, int h, string label) =>
        new StringBuilder().Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"{w}\" height=\"{h}\" role=\"img\" aria-label=\"{E(label)}\" font-family=\"{SvgTheme.LabelFont}\">");

    // ------------------------------------------------------------------ ECG strip

    /// <summary>
    /// ECG strip on standard paper: 25 mm/s, 10 mm/mV; minor squares 1 mm (40 ms / 0.1 mV), major 5 mm (200 ms / 0.5 mV).
    /// Detected R peaks are marked with ticks and the instantaneous heart rate.
    /// </summary>
    public static string EcgStrip(ReadOnlySpan<float> samples, double sampleRate, IReadOnlyList<int>? rPeaks = null, double seconds = 6, double mmPx = 5, string? title = null)
    {
        int n = Math.Min(samples.Length, (int)(seconds * sampleRate));
        double mmW = seconds * 25, mmH = 30; // ±1.5 mV
        int w = (int)(mmW * mmPx), h = (int)(mmH * mmPx);
        var sb = Open(w, h, title ?? "ECG strip");
        sb.Append(Inv, $"<rect width=\"{w}\" height=\"{h}\" fill=\"{SvgTheme.Paper}\"/>");
        var minor = new StringBuilder();
        var major = new StringBuilder();
        for (int i = 0; i <= mmW; i++) (i % 5 == 0 ? major : minor).Append(Inv, $"M{F(i * mmPx)} 0V{h}");
        for (int j = 0; j <= mmH; j++) (j % 5 == 0 ? major : minor).Append(Inv, $"M0 {F(j * mmPx)}H{w}");
        sb.Append(Inv, $"<path d=\"{minor}\" stroke=\"{SvgTheme.GridMinor}\" stroke-width=\"0.6\"/>");
        sb.Append(Inv, $"<path d=\"{major}\" stroke=\"{SvgTheme.GridMajor}\" stroke-width=\"1\"/>");
        double baseline = h * 0.6;
        double pxPerSample = 25 * mmPx / sampleRate, pxPerMv = 10 * mmPx;
        var path = new StringBuilder();
        int step = Math.Max(1, (int)(1 / pxPerSample / 1.5));
        for (int i = 0; i < n; i += step)
            path.Append(i == 0 ? 'M' : 'L').Append(F(i * pxPerSample)).Append(' ').Append(F(baseline - samples[i] * pxPerMv));
        sb.Append(Inv, $"<path d=\"{path}\" fill=\"none\" stroke=\"{SvgTheme.Trace}\" stroke-width=\"1.6\" stroke-linejoin=\"round\"/>");
        // 1 mV calibration pulse
        sb.Append(Inv, $"<path d=\"M{F(2 * mmPx)} {F(baseline)}h{F(mmPx)}v{F(-pxPerMv)}h{F(5 * mmPx)}v{F(pxPerMv)}h{F(mmPx)}\" fill=\"none\" stroke=\"{SvgTheme.Trace}\" stroke-width=\"1.4\" opacity=\"0.55\"/>");
        if (rPeaks is not null)
        {
            int? prev = null;
            foreach (var p in rPeaks.Where(p => p < n))
            {
                double x = p * pxPerSample;
                sb.Append(Inv, $"<path d=\"M{F(x)} {F(6)}v{F(10)}\" stroke=\"{SvgTheme.Teal}\" stroke-width=\"2\"/>");
                if (prev is { } q)
                    sb.Append(Inv, $"<text x=\"{F(x)}\" y=\"{F(28)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Teal}\" text-anchor=\"middle\">{F(Math.Round(60.0 * sampleRate / (p - q)))}</text>");
                prev = p;
            }
        }
        sb.Append(Inv, $"<text x=\"{F(w - 8)}\" y=\"{F(h - 8)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">25 mm/s · 10 mm/mV</text>");
        return sb.Append("</svg>").ToString();
    }

    // ------------------------------------------------------------------ NEWS2 observation chart

    /// <summary>
    /// NEWS2-style observation chart: one row per parameter, one column per observation set, each cell shaded by the points
    /// it scores (0 none, 1 pale, 2 amber, 3 red), with the aggregate score in the bottom row.
    /// </summary>
    public static string ObservationChart(IReadOnlyList<VitalSigns> vitals, string lang = "en", int maxColumns = 12)
    {
        var cols = vitals.OrderBy(v => v.Timestamp).TakeLast(maxColumns).ToArray();
        string[] rows = lang == "id"
            ? ["Frek. napas", "SpO₂ %", "Udara/O₂", "TD sistolik", "Nadi", "Kesadaran", "Suhu °C", "NEWS2"]
            : ["Resp. rate", "SpO₂ %", "Air/O₂", "Systolic BP", "Pulse", "Conscious.", "Temp °C", "NEWS2"];
        const int labelW = 110, cellW = 62, cellH = 28, headH = 34;
        int w = labelW + cellW * Math.Max(1, cols.Length) + 2, h = headH + cellH * rows.Length + 2;
        var sb = Open(w, h, "NEWS2 observation chart");
        sb.Append(Inv, $"<rect width=\"{w}\" height=\"{h}\" fill=\"{SvgTheme.Paper}\"/>");
        for (int c = 0; c < cols.Length; c++)
            sb.Append(Inv, $"<text x=\"{labelW + c * cellW + cellW / 2}\" y=\"22\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"middle\">{cols[c].Timestamp:HH:mm}</text>");
        for (int r = 0; r < rows.Length; r++)
        {
            int y = headH + r * cellH;
            bool total = r == rows.Length - 1;
            sb.Append(Inv, $"<text x=\"8\" y=\"{y + 18}\" font-size=\"12.5\" font-weight=\"{(total ? 700 : 500)}\" fill=\"{SvgTheme.Ink}\">{E(rows[r])}</text>");
            for (int c = 0; c < cols.Length; c++)
            {
                var s = ClinicalScores.News2(cols[c]);
                int points = total ? -1 : s.Components[r].Points;
                string value = total ? s.Score.ToString(Inv) : r switch
                {
                    0 => F(cols[c].RespiratoryRate), 1 => F(cols[c].SpO2), 2 => cols[c].OnSupplementalOxygen ? "O₂" : "A",
                    3 => F(cols[c].SystolicBp), 4 => F(cols[c].HeartRate), 5 => cols[c].Consciousness.ToString()[..1], _ => cols[c].TemperatureC.ToString("0.0", Inv),
                };
                string fill = total
                    ? s.Score >= 7 ? SvgTheme.Score3 : s.Score >= 5 ? SvgTheme.Score2 : s.Score >= 1 ? SvgTheme.Score1 : SvgTheme.Paper
                    : points switch { 3 => SvgTheme.Score3, 2 => SvgTheme.Score2, 1 => SvgTheme.Score1, _ => SvgTheme.Paper };
                int x = labelW + c * cellW;
                sb.Append(Inv, $"<rect x=\"{x + 1}\" y=\"{y + 1}\" width=\"{cellW - 2}\" height=\"{cellH - 2}\" rx=\"3\" fill=\"{fill}\"/>");
                sb.Append(Inv, $"<text x=\"{x + cellW / 2}\" y=\"{y + 18}\" font-size=\"12.5\" font-family=\"{SvgTheme.MonoFont}\" font-weight=\"{(total || points >= 2 ? 700 : 400)}\" fill=\"{SvgTheme.Ink}\" text-anchor=\"middle\">{E(value)}</text>");
            }
            sb.Append(Inv, $"<path d=\"M0 {y + cellH}H{w}\" stroke=\"{SvgTheme.Rule}\" stroke-width=\"{(r == rows.Length - 2 ? 1.6 : 0.8)}\"/>");
        }
        return sb.Append("</svg>").ToString();
    }

    // ------------------------------------------------------------------ line chart

    /// <summary>Time-series line chart with an optional shaded reference band.</summary>
    public static string Trend(IReadOnlyList<Series> series, double yMin, double yMax, (double Lo, double Hi)? band = null, string? unit = null,
        int width = 640, int height = 220, string? title = null)
    {
        const int padL = 44, padR = 14, padT = 14, padB = 28;
        var all = series.SelectMany(s => s.Points).ToArray();
        if (all.Length == 0) return Open(width, height, title ?? "trend").Append("</svg>").ToString();
        var t0 = all.Min(p => p.T); var t1 = all.Max(p => p.T);
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        double X(DateTimeOffset t) => padL + (width - padL - padR) * (t - t0).TotalSeconds / span;
        double Y(double v) => padT + (height - padT - padB) * (1 - (v - yMin) / (yMax - yMin));
        var sb = Open(width, height, title ?? "trend");
        if (band is { } b)
            sb.Append(Inv, $"<rect x=\"{padL}\" y=\"{F(Y(b.Hi))}\" width=\"{width - padL - padR}\" height=\"{F(Y(b.Lo) - Y(b.Hi))}\" fill=\"{SvgTheme.TealSoft}\" opacity=\"0.55\"/>");
        for (int i = 0; i <= 4; i++)
        {
            double v = yMin + (yMax - yMin) * i / 4;
            sb.Append(Inv, $"<path d=\"M{padL} {F(Y(v))}H{width - padR}\" stroke=\"{SvgTheme.Rule}\" stroke-width=\"0.8\"/>");
            sb.Append(Inv, $"<text x=\"{padL - 6}\" y=\"{F(Y(v) + 4)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{F(v)}</text>");
        }
        var stamps = all.Select(p => p.T).Distinct().Order().ToArray();
        int every = Math.Max(1, (int)Math.Ceiling(stamps.Length / 6.0));
        for (int i = 0; i < stamps.Length; i += every)
        {
            var t = stamps[i];
            string anchor = i == 0 ? "start" : i + every >= stamps.Length && i == stamps.Length - 1 ? "end" : "middle";
            sb.Append(Inv, $"<text x=\"{F(X(t))}\" y=\"{height - 8}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"{anchor}\">{t:HH:mm}</text>");
        }
        foreach (var s in series)
        {
            var pts = string.Join(' ', s.Points.Select(p => $"{F(X(p.T))},{F(Y(p.V))}"));
            sb.Append(Inv, $"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
            foreach (var p in s.Points) sb.Append(Inv, $"<circle cx=\"{F(X(p.T))}\" cy=\"{F(Y(p.V))}\" r=\"2.6\" fill=\"{s.Color}\"/>");
            var last = s.Points[^1];
            sb.Append(Inv, $"<text x=\"{F(X(last.T) - 4)}\" y=\"{F(Y(last.V) - 8)}\" font-size=\"11.5\" font-weight=\"600\" fill=\"{s.Color}\" text-anchor=\"end\">{E(s.Name)}</text>");
        }
        if (unit is not null) sb.Append(Inv, $"<text x=\"{padL}\" y=\"{padT - 2}\" font-size=\"10.5\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\">{E(unit)}</text>");
        return sb.Append("</svg>").ToString();
    }

    // ------------------------------------------------------------------ bars

    /// <summary>Diverging horizontal bars (e.g. risk-model feature contributions: positive raises risk).</summary>
    public static string DivergingBars(IReadOnlyList<(string Label, double Value)> items, int width = 520, string? title = null)
    {
        const int rowH = 26, labelW = 150;
        int h = rowH * items.Count + 10;
        double max = Math.Max(1e-9, items.Max(i => Math.Abs(i.Value)));
        double mid = labelW + (width - labelW) / 2.0, half = (width - labelW) / 2.0 - 40;
        var sb = Open(width, h, title ?? "contributions");
        sb.Append(Inv, $"<path d=\"M{F(mid)} 0V{h}\" stroke=\"{SvgTheme.Rule}\" stroke-width=\"1\"/>");
        for (int i = 0; i < items.Count; i++)
        {
            var (label, v) = items[i];
            double len = Math.Abs(v) / max * half, y = 6 + i * rowH;
            double x = v >= 0 ? mid : mid - len;
            sb.Append(Inv, $"<text x=\"0\" y=\"{F(y + 15)}\" font-size=\"12.5\" fill=\"{SvgTheme.Ink}\">{E(label)}</text>");
            sb.Append(Inv, $"<rect x=\"{F(x)}\" y=\"{F(y + 3)}\" width=\"{F(Math.Max(1, len))}\" height=\"16\" rx=\"2\" fill=\"{(v >= 0 ? SvgTheme.Red : SvgTheme.Teal)}\" opacity=\"0.85\"/>");
            sb.Append(Inv, $"<text x=\"{F(v >= 0 ? mid + len + 5 : mid - len - 5)}\" y=\"{F(y + 15)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"{(v >= 0 ? "start" : "end")}\">{(v >= 0 ? "+" : "")}{v.ToString("0.00", Inv)}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Histogram (e.g. Hounsfield units) with optional labelled vertical markers.</summary>
    public static string Histogram(IReadOnlyList<int> bins, double min, double max, IReadOnlyList<(double At, string Label)>? markers = null, int width = 520, int height = 180, bool logScale = true)
    {
        const int padB = 24, padT = 10;
        double top = bins.Count == 0 ? 1 : bins.Max(b => logScale ? Math.Log10(b + 1) : b);
        double bw = (double)width / Math.Max(1, bins.Count);
        var sb = Open(width, height, "histogram");
        for (int i = 0; i < bins.Count; i++)
        {
            double v = logScale ? Math.Log10(bins[i] + 1) : bins[i];
            double bh = (height - padB - padT) * v / Math.Max(1e-9, top);
            sb.Append(Inv, $"<rect x=\"{F(i * bw)}\" y=\"{F(height - padB - bh)}\" width=\"{F(Math.Max(1, bw - 1))}\" height=\"{F(bh)}\" fill=\"{SvgTheme.Ink}\" opacity=\"0.78\"/>");
        }
        foreach (var (at, label) in markers ?? [])
        {
            double x = (at - min) / (max - min) * width;
            sb.Append(Inv, $"<path d=\"M{F(x)} {padT}V{height - padB}\" stroke=\"{SvgTheme.Teal}\" stroke-dasharray=\"3 3\"/>");
            sb.Append(Inv, $"<text x=\"{F(x + 3)}\" y=\"{padT + 10}\" font-size=\"10.5\" fill=\"{SvgTheme.Teal}\">{E(label)}</text>");
        }
        sb.Append(Inv, $"<text x=\"0\" y=\"{height - 6}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\">{F(min)}</text>");
        sb.Append(Inv, $"<text x=\"{width}\" y=\"{height - 6}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{F(max)} HU</text>");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Semicircular gauge with coloured bands (e.g. NEWS2 0–20, probability 0–1).</summary>
    public static string Gauge(double value, double max, IReadOnlyList<(double Upto, string Color)> bands, string label, string? display = null, int size = 220)
    {
        double cx = size / 2.0, cy = size * 0.62, r = size * 0.42;
        (double X, double Y) P(double v, double rr) { double a = Math.PI * (1 - Math.Clamp(v / max, 0, 1)); return (cx + rr * Math.Cos(a), cy - rr * Math.Sin(a)); }
        var sb = Open(size, (int)(size * 0.78), label);
        double from = 0;
        foreach (var (upto, color) in bands)
        {
            var a = P(from, r); var b = P(upto, r);
            sb.Append(Inv, $"<path d=\"M{F(a.X)} {F(a.Y)}A{F(r)} {F(r)} 0 0 1 {F(b.X)} {F(b.Y)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(size * 0.08)}\"/>");
            from = upto;
        }
        var n = P(value, r * 0.88);
        sb.Append(Inv, $"<path d=\"M{F(cx)} {F(cy)}L{F(n.X)} {F(n.Y)}\" stroke=\"{SvgTheme.Ink}\" stroke-width=\"3\" stroke-linecap=\"round\"/>");
        sb.Append(Inv, $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"5\" fill=\"{SvgTheme.Ink}\"/>");
        sb.Append(Inv, $"<text x=\"{F(cx)}\" y=\"{F(cy + size * 0.13)}\" font-size=\"{F(size * 0.13)}\" font-weight=\"700\" fill=\"{SvgTheme.Ink}\" text-anchor=\"middle\" font-family=\"{SvgTheme.MonoFont}\">{E(display ?? F(value))}</text>");
        return sb.Append("</svg>").ToString();
    }
}
