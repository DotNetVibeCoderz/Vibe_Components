using System.Text;
using Medical.Clinical;
using Medical.Core;

namespace Medical.Visualization;

/// <summary>An XY series for <see cref="Charts.XyChart"/>.</summary>
public sealed record XySeries(string Name, IReadOnlyList<(double X, double Y)> Points, string Color, bool Dashed = false, bool Markers = false);

public static partial class Charts
{
    /// <summary>Round tick values (steps of 1, 2, 2.5 or 5 × 10ⁿ) covering [min, max].</summary>
    public static IReadOnlyList<double> NiceTicks(double min, double max, int target = 5)
    {
        double raw = (max - min) / Math.Max(1, target), mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-12))));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw * 0.999);
        var res = new List<double>();
        for (double v = Math.Ceiling(min / step - 1e-9) * step; v <= max + step * 1e-9; v += step) res.Add(Math.Round(v, 10) + 0.0);   // + 0.0 turns −0 into 0
        return res;
    }

    /// <summary>Line chart over numeric X (e.g. training rounds, age, privacy noise), with optional vertical markers.</summary>
    public static string XyChart(IReadOnlyList<XySeries> series, double xMin, double xMax, double yMin, double yMax, string? xLabel = null, string? yLabel = null,
        IReadOnlyList<(double At, string Label)>? markers = null, int width = 560, int height = 240, string? title = null, string yFormat = "0.##")
    {
        const int padL = 48, padR = 16, padT = 16, padB = 34;
        double X(double x) => padL + (width - padL - padR) * (x - xMin) / Math.Max(1e-12, xMax - xMin);
        double Y(double y) => padT + (height - padT - padB) * (1 - (Math.Clamp(y, yMin, yMax) - yMin) / Math.Max(1e-12, yMax - yMin));
        var sb = Open(width, height, title ?? "chart");
        foreach (var v in NiceTicks(yMin, yMax, 4))
        {
            sb.Append(Inv, $"<path d=\"M{padL} {F(Y(v))}H{width - padR}\" stroke=\"{SvgTheme.Rule}\" stroke-width=\"0.8\"/>");
            sb.Append(Inv, $"<text x=\"{padL - 6}\" y=\"{F(Y(v) + 4)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{v.ToString(yFormat, Inv)}</text>");
        }
        foreach (var v in NiceTicks(xMin, xMax, 6))
            sb.Append(Inv, $"<text x=\"{F(X(v))}\" y=\"{height - 18}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"middle\">{F(v)}</text>");
        foreach (var (at, label) in markers ?? [])
        {
            bool right = X(at) > width - padR - 50;
            sb.Append(Inv, $"<path d=\"M{F(X(at))} {padT}V{height - padB}\" stroke=\"{SvgTheme.Muted}\" stroke-dasharray=\"3 3\"/>");
            sb.Append(Inv, $"<text x=\"{F(right ? X(at) - 4 : X(at) + 4)}\" y=\"{padT + 10}\" font-size=\"10.5\" fill=\"{SvgTheme.Muted}\" text-anchor=\"{(right ? "end" : "start")}\">{E(label)}</text>");
        }
        foreach (var s in series)
        {
            if (s.Points.Count == 0) continue;
            var pts = string.Join(' ', s.Points.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"));
            sb.Append(Inv, $"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"2\" stroke-linejoin=\"round\"{(s.Dashed ? " stroke-dasharray=\"5 4\"" : "")}/>");
            if (s.Markers) foreach (var p in s.Points) sb.Append(Inv, $"<circle cx=\"{F(X(p.X))}\" cy=\"{F(Y(p.Y))}\" r=\"3\" fill=\"{s.Color}\"/>");
            var last = s.Points[^1];
            sb.Append(Inv, $"<text x=\"{F(X(last.X) - 4)}\" y=\"{F(Y(last.Y) - 7)}\" font-size=\"11.5\" font-weight=\"600\" fill=\"{s.Color}\" text-anchor=\"end\">{E(s.Name)}</text>");
        }
        if (yLabel is not null) sb.Append(Inv, $"<text x=\"{padL}\" y=\"{padT - 4}\" font-size=\"10.5\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\">{E(yLabel)}</text>");
        if (xLabel is not null) sb.Append(Inv, $"<text x=\"{width - padR}\" y=\"{height - 3}\" font-size=\"10.5\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{E(xLabel)}</text>");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Horizontal point bars, one per score component (e.g. the six SOFA organ systems, 0–4 each).</summary>
    public static string ScoreBars(IReadOnlyList<(string Label, double Points, double Max)> items, int width = 520, string? title = null)
    {
        const int rowH = 28, labelW = 190;
        int h = rowH * items.Count + 8;
        var sb = Open(width, h, title ?? "score components");
        double barW = width - labelW - 40;
        for (int i = 0; i < items.Count; i++)
        {
            var (label, pts, max) = items[i];
            double y = 4 + i * rowH, frac = max <= 0 ? 0 : Math.Clamp(pts / max, 0, 1);
            string color = frac >= 0.75 ? SvgTheme.Score3 : frac >= 0.5 ? SvgTheme.Score2 : frac > 0 ? SvgTheme.Score1 : SvgTheme.Paper;
            sb.Append(Inv, $"<text x=\"0\" y=\"{F(y + 17)}\" font-size=\"12.5\" fill=\"{SvgTheme.Ink}\">{E(label)}</text>");
            sb.Append(Inv, $"<rect x=\"{labelW}\" y=\"{F(y + 4)}\" width=\"{F(barW)}\" height=\"18\" rx=\"3\" fill=\"{SvgTheme.Paper}\" stroke=\"{SvgTheme.Rule}\"/>");
            if (frac > 0) sb.Append(Inv, $"<rect x=\"{labelW}\" y=\"{F(y + 4)}\" width=\"{F(barW * frac)}\" height=\"18\" rx=\"3\" fill=\"{color}\"/>");
            for (int k = 1; k < max; k++) sb.Append(Inv, $"<path d=\"M{F(labelW + barW * k / max)} {F(y + 4)}v18\" stroke=\"{SvgTheme.Rule}\" stroke-width=\"1\"/>");
            sb.Append(Inv, $"<text x=\"{F(labelW + barW + 8)}\" y=\"{F(y + 17)}\" font-size=\"12\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Ink}\">{F(pts)}/{F(max)}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>
    /// WHO growth chart for an age-based indicator (0–5 years): the −3 … +3 SD curves from the loaded WHO tables, the
    /// normal range shaded, and the child's measurements plotted. Ages in days; the axis shows months.
    /// </summary>
    public static string GrowthChart(WhoGrowthStandards who, GrowthIndicator indicator, Gender sex, double fromDays, double toDays,
        IReadOnlyList<(double AgeDays, double Value)> points, string lang = "en", int width = 640, int height = 360)
    {
        const int padL = 46, padR = 40, padT = 18, padB = 36;
        int step = Math.Max(1, (int)((toDays - fromDays) / 120));
        var ages = new List<double>();
        for (double d = fromDays; d <= toDays; d += step) ages.Add(Math.Round(d));
        var lines = ages.Select(d => who.SdLines(indicator, sex, d)).ToList();
        double yMin = Math.Min(lines.Min(l => l[0].Value), points.Count == 0 ? double.MaxValue : points.Min(p => p.Value));
        double yMax = Math.Max(lines.Max(l => l[^1].Value), points.Count == 0 ? double.MinValue : points.Max(p => p.Value));
        double pad = (yMax - yMin) * 0.05;
        var yt = NiceTicks(yMin - pad, yMax + pad, 6);
        double tstep = yt.Count > 1 ? yt[1] - yt[0] : 1;
        yMin = Math.Floor((yMin - pad) / tstep) * tstep; yMax = Math.Ceiling((yMax + pad) / tstep) * tstep;
        double X(double d) => padL + (width - padL - padR) * (d - fromDays) / (toDays - fromDays);
        double Y(double v) => padT + (height - padT - padB) * (1 - (v - yMin) / (yMax - yMin));
        var sb = Open(width, height, $"WHO {indicator}");
        // Shaded −2 … +2 SD band.
        var upper = ages.Select((d, i) => $"{F(X(d))},{F(Y(lines[i][5].Value))}");
        var lower = ages.Select((d, i) => $"{F(X(d))},{F(Y(lines[i][1].Value))}").Reverse();
        sb.Append(Inv, $"<polygon points=\"{string.Join(' ', upper.Concat(lower))}\" fill=\"{SvgTheme.TealSoft}\" opacity=\"0.45\"/>");
        foreach (var v in NiceTicks(yMin, yMax, 6))
        {
            sb.Append(Inv, $"<path d=\"M{padL} {F(Y(v))}H{width - padR}\" stroke=\"{SvgTheme.Rule}\" stroke-width=\"0.6\"/>");
            sb.Append(Inv, $"<text x=\"{padL - 6}\" y=\"{F(Y(v) + 4)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{F(v)}</text>");
        }
        for (double m = Math.Ceiling(fromDays / 30.4375); m * 30.4375 <= toDays; m += (toDays - fromDays) > 900 ? 6 : 3)
            sb.Append(Inv, $"<text x=\"{F(X(m * 30.4375))}\" y=\"{height - 18}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"middle\">{F(m)}</text>");
        sb.Append(Inv, $"<text x=\"{width - padR}\" y=\"{height - 3}\" font-size=\"10.5\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{(lang == "id" ? "usia (bulan)" : "age (months)")}</text>");
        for (int k = 0; k < 7; k++)
        {
            int sd = k - 3;
            string color = sd == 0 ? SvgTheme.Teal : Math.Abs(sd) == 3 ? SvgTheme.Red : Math.Abs(sd) == 2 ? SvgTheme.Amber : SvgTheme.Muted;
            var pts = string.Join(' ', ages.Select((d, i) => $"{F(X(d))},{F(Y(lines[i][k].Value))}"));
            sb.Append(Inv, $"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{(sd == 0 ? 2.2 : 1.3)}\"{(Math.Abs(sd) == 1 ? " stroke-dasharray=\"4 3\"" : "")}/>");
            sb.Append(Inv, $"<text x=\"{F(X(ages[^1]) + 4)}\" y=\"{F(Y(lines[^1][k].Value) + 4)}\" font-size=\"10.5\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{color}\">{(sd > 0 ? "+" : "")}{sd}</text>");
        }
        if (points.Count > 1)
            sb.Append(Inv, $"<polyline points=\"{string.Join(' ', points.Select(p => $"{F(X(p.AgeDays))},{F(Y(p.Value))}"))}\" fill=\"none\" stroke=\"{SvgTheme.Ink}\" stroke-width=\"1.6\"/>");
        foreach (var p in points) sb.Append(Inv, $"<circle cx=\"{F(X(p.AgeDays))}\" cy=\"{F(Y(p.Value))}\" r=\"4.2\" fill=\"{SvgTheme.Ink}\" stroke=\"white\" stroke-width=\"1.2\"/>");
        string unit = indicator switch { GrowthIndicator.WeightForAge => "kg", GrowthIndicator.BmiForAge => "kg/m²", _ => "cm" };
        sb.Append(Inv, $"<text x=\"{padL}\" y=\"{padT - 5}\" font-size=\"10.5\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\">{unit}</text>");
        return sb.Append("</svg>").ToString();
    }
}
