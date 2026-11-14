using System.Numerics;
using System.Text;
using static Electrical.Visualization.SvgFormat;

namespace Electrical.Visualization;

/// <summary>Quantity type of a phasor: voltages and currents are scaled independently.</summary>
public enum PhasorKind { Voltage, Current, Other }

/// <summary>One phasor arrow.</summary>
/// <param name="Label">Label, e.g. "Va".</param>
/// <param name="Magnitude">Magnitude in any unit (kV, kA, pu…).</param>
/// <param name="AngleDeg">Angle [degrees], counter-clockwise from the positive real axis.</param>
public sealed record PhasorItem(string Label, double Magnitude, double AngleDeg, PhasorKind Kind = PhasorKind.Voltage, string? Color = null)
{
    /// <summary>Creates a phasor from a complex value.</summary>
    public static PhasorItem FromComplex(string label, Complex value, PhasorKind kind = PhasorKind.Voltage, string? color = null) =>
        new(label, value.Magnitude, value.Phase * 180 / Math.PI, kind, color);
}

/// <summary>Series for a polar chart (angle [degrees], radius).</summary>
public sealed record PolarSeries(string Name, IReadOnlyList<(double AngleDeg, double Radius)> Points, string? Color = null)
{
    /// <summary>Close the polygon back to the first point.</summary>
    public bool Closed { get; init; }
    /// <summary>Fill the closed polygon with a translucent tint.</summary>
    public bool Fill { get; init; }
}

/// <summary>Time–current characteristic: points (current [A], operating time [s]).</summary>
public sealed record TccCurve(string Name, IReadOnlyList<(double Current, double Time)> Points, string? Color = null)
{
    public bool Dashed { get; init; }

    /// <summary>Samples <paramref name="timeOfCurrent"/> at log-spaced currents in [iMin, iMax] [A]; non-finite or non-positive times are skipped.</summary>
    public static TccCurve FromFunction(string name, Func<double, double> timeOfCurrent, double iMin, double iMax, int points = 80, string? color = null)
    {
        var list = new List<(double, double)>(points);
        double l0 = Math.Log10(iMin), l1 = Math.Log10(iMax);
        for (int k = 0; k < points; k++)
        {
            double i = Math.Pow(10, l0 + (l1 - l0) * k / (points - 1));
            double t = timeOfCurrent(i);
            if (double.IsFinite(t) && t > 0) list.Add((i, t));
        }
        return new TccCurve(name, list, color);
    }
}

/// <summary>Vertical marker on a TCC plot (e.g. maximum fault current, inrush).</summary>
public sealed record TccMarker(double Current, string Label, string? Color = null);

/// <summary>Options for <see cref="SvgCharts.Tcc"/>. Axis ranges are rounded to decades when not specified.</summary>
public sealed record TccOptions
{
    public int Width { get; init; } = 560;
    public int Height { get; init; } = 480;
    public string? Title { get; init; }
    /// <summary>Current axis range [A].</summary>
    public double? CurrentMin { get; init; }
    public double? CurrentMax { get; init; }
    /// <summary>Time axis range [s].</summary>
    public double? TimeMin { get; init; }
    public double? TimeMax { get; init; }
    public string CurrentUnit { get; init; } = "A";
}

public static partial class SvgCharts
{
    /// <summary>
    /// Phasor diagram: concentric rings, 30° spokes and arrows. Voltages are scaled to 86 % and currents to 66 % of the radius
    /// (each kind against its own maximum) so both are readable together.
    /// </summary>
    public static string Phasor(IReadOnlyList<PhasorItem> phasors, string? title = null, int size = 360)
    {
        int legendH = 16 * phasors.Count + 8;
        int h = size + legendH + (title is null ? 0 : 22);
        double top = title is null ? 0 : 22;
        double cx = size / 2.0, cy = top + size / 2.0, R = size * 0.42;
        var sb = Open(size, h, title ?? "Phasor diagram", "eln-chart eln-phasor");
        string grid = SvgTheme.Var("eln-grid"), muted = SvgTheme.Var("eln-muted"), fg = SvgTheme.Var("eln-fg");
        if (title is not null) SvgText(sb, 8, 16, title, fg, extra: "font-size=\"13\" font-weight=\"600\"");
        for (int k = 1; k <= 4; k++) sb.Append($"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(R * k / 4)}\" fill=\"none\" stroke=\"{grid}\"/>");
        for (int a = 0; a < 360; a += 30)
        {
            double rad = a * Math.PI / 180;
            SvgLine(sb, cx, cy, cx + R * Math.Cos(rad), cy - R * Math.Sin(rad), grid, 1, a % 90 == 0 ? null : "stroke-dasharray=\"2 3\"");
            if (a % 90 == 0) SvgText(sb, cx + (R + 18) * Math.Cos(rad), cy - (R + 18) * Math.Sin(rad) + 4, $"{a}°", muted, "middle", "font-size=\"10\"");
        }
        var maxByKind = phasors.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => Math.Max(1e-12, g.Max(p => Math.Abs(p.Magnitude))));
        for (int i = 0; i < phasors.Count; i++)
        {
            var p = phasors[i];
            string color = p.Color ?? SvgTheme.Series(i);
            double scale = p.Kind switch { PhasorKind.Current => 0.66, PhasorKind.Other => 0.8, _ => 0.86 } * R / maxByKind[p.Kind];
            double rad = p.AngleDeg * Math.PI / 180, len = Math.Abs(p.Magnitude) * scale;
            double ex = cx + len * Math.Cos(rad), ey = cy - len * Math.Sin(rad);
            double head = Math.Min(10, len * 0.3), hw = head * 0.45;
            double bx = ex - head * Math.Cos(rad), by = ey + head * Math.Sin(rad);
            sb.Append($"<g class=\"eln-phasor-arrow\"><title>{Esc($"{p.Label} = {V(p.Magnitude, "0.###")} ∠ {V(p.AngleDeg, "0.#")}°")}</title>");
            SvgLine(sb, cx, cy, bx, by, color, p.Kind == PhasorKind.Current ? 2 : 2.6, p.Kind == PhasorKind.Current ? "stroke-dasharray=\"5 3\"" : null);
            double px = -Math.Sin(rad) * hw, py = -Math.Cos(rad) * hw;
            sb.Append($"<polygon points=\"{N(ex)},{N(ey)} {N(bx + px)},{N(by + py)} {N(bx - px)},{N(by - py)}\" fill=\"{color}\"/>");
            SvgText(sb, ex + 10 * Math.Cos(rad), ey - 10 * Math.Sin(rad) + 4, p.Label, color, Math.Cos(rad) < -0.2 ? "end" : Math.Cos(rad) > 0.2 ? "start" : "middle", "font-weight=\"600\"");
            sb.Append("</g>");
        }
        double ly = top + size + 4;
        for (int i = 0; i < phasors.Count; i++)
        {
            var p = phasors[i];
            sb.Append($"<rect x=\"12\" y=\"{N(ly + i * 16 + 2)}\" width=\"12\" height=\"4\" rx=\"2\" fill=\"{p.Color ?? SvgTheme.Series(i)}\"/>");
            SvgText(sb, 30, ly + i * 16 + 8, $"{p.Label}  {V(p.Magnitude, "0.###")} ∠ {V(p.AngleDeg, "0.#")}°", fg, extra: "font-size=\"11\"");
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>
    /// Polar chart (e.g. generator capability, wind rose, impedance locus). With <paramref name="compass"/> = true, 0° is north and angles grow clockwise;
    /// otherwise 0° is east, counter-clockwise.
    /// </summary>
    public static string Polar(IReadOnlyList<PolarSeries> series, string? title = null, int size = 380, double? radiusMax = null, bool compass = false)
    {
        double rmax = radiusMax ?? Math.Max(1e-12, series.SelectMany(s => s.Points).Select(p => p.Radius).DefaultIfEmpty(1).Max());
        var (_, niceMax, step) = NiceRange(0, rmax, 5);
        int h = size + (title is null ? 0 : 22) + 18 * ((series.Count + 2) / 3);
        double top = title is null ? 0 : 22, cx = size / 2.0, cy = top + size / 2.0, R = size * 0.40;
        var sb = Open(size, h, title ?? "Polar chart", "eln-chart eln-polar");
        string grid = SvgTheme.Var("eln-grid"), muted = SvgTheme.Var("eln-muted"), fg = SvgTheme.Var("eln-fg");
        if (title is not null) SvgText(sb, 8, 16, title, fg, extra: "font-size=\"13\" font-weight=\"600\"");
        double Ang(double deg) => (compass ? 90 - deg : deg) * Math.PI / 180;
        foreach (var r in Ticks(step, niceMax, step))
        {
            sb.Append($"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(R * r / niceMax)}\" fill=\"none\" stroke=\"{grid}\"/>");
            double la = 67.5 * Math.PI / 180, rr = R * r / niceMax;
            SvgText(sb, cx + rr * Math.Cos(la) + 2, cy - rr * Math.Sin(la) - 2, TickLabel(r, step), muted, extra: "font-size=\"9\"");
        }
        for (int a = 0; a < 360; a += 45)
        {
            double rad = Ang(a);
            SvgLine(sb, cx, cy, cx + R * Math.Cos(rad), cy - R * Math.Sin(rad), grid);
            string lbl = compass ? (a switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", _ => $"{a}°" }) : $"{a}°";
            SvgText(sb, cx + (R + 14) * Math.Cos(rad), cy - (R + 14) * Math.Sin(rad) + 4, lbl, muted, "middle", "font-size=\"10\"");
        }
        for (int k = 0; k < series.Count; k++)
        {
            var s = series[k];
            string color = s.Color ?? SvgTheme.Series(k);
            var pts = string.Join(' ', s.Points.Select(p => $"{N(cx + R * p.Radius / niceMax * Math.Cos(Ang(p.AngleDeg)))},{N(cy - R * p.Radius / niceMax * Math.Sin(Ang(p.AngleDeg)))}"));
            string tag = s.Closed ? "polygon" : "polyline";
            sb.Append($"<{tag} points=\"{pts}\" fill=\"{(s.Closed && s.Fill ? color : "none")}\" fill-opacity=\"0.18\" stroke=\"{color}\" stroke-width=\"2\" stroke-linejoin=\"round\"><title>{Esc(s.Name)}</title></{tag}>");
        }
        double ly = top + size;
        for (int k = 0; k < series.Count; k++)
        {
            double lx = 12 + (k % 3) * (size / 3.0), yy = ly + (k / 3) * 18;
            sb.Append($"<rect x=\"{N(lx)}\" y=\"{N(yy - 6)}\" width=\"12\" height=\"4\" rx=\"2\" fill=\"{series[k].Color ?? SvgTheme.Series(k)}\"/>");
            SvgText(sb, lx + 16, yy, series[k].Name, fg);
        }
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Log–log time–current characteristic plot for protection coordination (current [A] vs. time [s]).</summary>
    public static string Tcc(IReadOnlyList<TccCurve> curves, IReadOnlyList<TccMarker>? markers = null, TccOptions? options = null)
    {
        var o = options ?? new TccOptions();
        var pts = curves.SelectMany(c => c.Points).Where(p => p.Current > 0 && p.Time > 0).ToList();
        double Dec(double v, bool up) => Math.Pow(10, up ? Math.Ceiling(Math.Log10(v) - 1e-9) : Math.Floor(Math.Log10(v) + 1e-9));
        double i0 = o.CurrentMin ?? Dec(pts.Count == 0 ? 1 : Math.Min(pts.Min(p => p.Current), markers?.Select(m => m.Current).DefaultIfEmpty(double.MaxValue).Min() ?? double.MaxValue), false);
        double i1 = o.CurrentMax ?? Dec(pts.Count == 0 ? 1e4 : Math.Max(pts.Max(p => p.Current), markers?.Select(m => m.Current).DefaultIfEmpty(0).Max() ?? 0), true);
        double t0 = o.TimeMin ?? Math.Max(0.01, Dec(pts.Count == 0 ? 0.01 : pts.Min(p => p.Time), false));
        double t1 = o.TimeMax ?? Math.Min(1000, Dec(pts.Count == 0 ? 100 : pts.Max(p => p.Time), true));
        if (i1 <= i0) i1 = i0 * 10;
        if (t1 <= t0) t1 = t0 * 10;
        double L = 62, T = o.Title is null ? 14 : 34, Rp = 16, B = 44 + 16 * ((curves.Count + 2) / 3);
        double W = o.Width - L - Rp, H = o.Height - T - B;
        double X(double i) => L + (Math.Log10(i) - Math.Log10(i0)) / (Math.Log10(i1) - Math.Log10(i0)) * W;
        double Y(double t) => T + H - (Math.Log10(t) - Math.Log10(t0)) / (Math.Log10(t1) - Math.Log10(t0)) * H;
        var sb = Open(o.Width, o.Height, o.Title ?? "Time-current characteristics", "eln-chart eln-tcc");
        string grid = SvgTheme.Var("eln-grid"), muted = SvgTheme.Var("eln-muted"), fg = SvgTheme.Var("eln-fg");
        if (o.Title is not null) SvgText(sb, 8, 20, o.Title, fg, extra: "font-size=\"13\" font-weight=\"600\"");
        sb.Append($"<rect x=\"{N(L)}\" y=\"{N(T)}\" width=\"{N(W)}\" height=\"{N(H)}\" fill=\"none\" stroke=\"{muted}\"/>");
        static string Lbl(double d) => d >= 1000 ? V(d / 1000, "0.##") + "k" : V(d, "0.###");
        for (double d = i0; d <= i1 * 1.0001; d *= 10)
            for (int m = 1; m <= 9; m++)
            {
                double v = d * m;
                if (v > i1 * 1.0001) break;
                SvgLine(sb, X(v), T, X(v), T + H, grid, m == 1 ? 1.1 : 0.6);
                if (m == 1) SvgText(sb, X(v), T + H + 14, Lbl(v), muted, "middle");
            }
        for (double d = t0; d <= t1 * 1.0001; d *= 10)
            for (int m = 1; m <= 9; m++)
            {
                double v = d * m;
                if (v > t1 * 1.0001) break;
                SvgLine(sb, L, Y(v), L + W, Y(v), grid, m == 1 ? 1.1 : 0.6);
                if (m == 1) SvgText(sb, L - 6, Y(v) + 4, Lbl(v), muted, "end");
            }
        SvgText(sb, L + W / 2, T + H + 30, $"Current [{o.CurrentUnit}]", muted, "middle", "font-weight=\"600\"");
        sb.Append($"<text transform=\"translate(16,{N(T + H / 2)}) rotate(-90)\" text-anchor=\"middle\" fill=\"{muted}\" font-weight=\"600\">Time [s]</text>");
        string clip = StableId("eln-tcc-clip-", $"{o.Width}x{o.Height}:{i0}:{i1}:{t0}:{t1}");
        sb.Append($"<defs><clipPath id=\"{clip}\"><rect x=\"{N(L)}\" y=\"{N(T)}\" width=\"{N(W)}\" height=\"{N(H)}\"/></clipPath></defs><g clip-path=\"url(#{clip})\">");
        for (int k = 0; k < curves.Count; k++)
        {
            var c = curves[k];
            var p = c.Points.Where(q => q.Current > 0 && q.Time > 0).Select(q => $"{N(X(q.Current))},{N(Y(q.Time))}");
            sb.Append($"<polyline points=\"{string.Join(' ', p)}\" fill=\"none\" stroke=\"{c.Color ?? SvgTheme.Series(k)}\" stroke-width=\"2.2\" stroke-linejoin=\"round\"{(c.Dashed ? " stroke-dasharray=\"6 4\"" : "")}><title>{Esc(c.Name)}</title></polyline>");
        }
        if (markers is not null)
            foreach (var m in markers)
            {
                if (m.Current < i0 || m.Current > i1) continue;
                string col = m.Color ?? SvgTheme.Var("eln-danger");
                SvgLine(sb, X(m.Current), T, X(m.Current), T + H, col, 1.4, "stroke-dasharray=\"4 3\"");
                sb.Append($"<text transform=\"translate({N(X(m.Current) - 4)},{N(T + 6)}) rotate(-90)\" text-anchor=\"end\" fill=\"{col}\" font-size=\"10\">{Esc(m.Label)}</text>");
            }
        sb.Append("</g>");
        double ly = T + H + 48;
        for (int k = 0; k < curves.Count; k++)
        {
            double lx = L + (k % 3) * (W / 3), yy = ly + (k / 3) * 16;
            sb.Append($"<rect x=\"{N(lx)}\" y=\"{N(yy - 6)}\" width=\"14\" height=\"4\" rx=\"2\" fill=\"{curves[k].Color ?? SvgTheme.Series(k)}\"/>");
            SvgText(sb, lx + 18, yy, curves[k].Name, fg);
        }
        return sb.Append("</svg>").ToString();
    }
}
