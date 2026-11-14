using System.Globalization;
using System.Net;
using System.Text;

namespace Electrical.Visualization;

/// <summary>Low-level helpers shared by the SVG renderers (invariant number formatting, escaping, axis ticks).</summary>
public static class SvgFormat
{
    /// <summary>Formats a coordinate with up to two decimals using the invariant culture.</summary>
    public static string N(double v) => double.IsFinite(v) ? Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture) : "0";

    /// <summary>Formats a value with a .NET format string using the invariant culture.</summary>
    public static string V(double v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>XML-escapes text content and attribute values.</summary>
    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>"Nice" axis range covering [min, max] with about <paramref name="ticks"/> ticks.</summary>
    public static (double Min, double Max, double Step) NiceRange(double min, double max, int ticks = 5)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) (min, max) = (0, 1);
        if (max - min < 1e-12) { double d = Math.Abs(min) > 1e-12 ? Math.Abs(min) * 0.1 : 1; min -= d; max += d; }
        double range = Nice(max - min, false);
        double step = Nice(range / Math.Max(1, ticks - 1), true);
        return (Math.Floor(min / step + 1e-9) * step, Math.Ceiling(max / step - 1e-9) * step, step);
    }

    private static double Nice(double x, bool round)
    {
        double e = Math.Floor(Math.Log10(x)), f = x / Math.Pow(10, e);
        double nf = round ? f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10 : f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * Math.Pow(10, e);
    }

    /// <summary>Ticks between min and max (inclusive) at a fixed step.</summary>
    public static IEnumerable<double> Ticks(double min, double max, double step)
    {
        int n = (int)Math.Round((max - min) / step);
        for (int i = 0; i <= n; i++) yield return Math.Round(min + i * step, 12);
    }

    /// <summary>Tick label formatter that adapts the number of decimals to the step.</summary>
    public static string TickLabel(double v, double step)
    {
        if (Math.Abs(v) < Math.Abs(step) * 1e-9) v = 0;
        int dec = step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        if (Math.Abs(v) >= 1e4 && step >= 1000) return V(v / 1000, "0.#") + "k";
        return v.ToString("F" + dec, CultureInfo.InvariantCulture);
    }

    /// <summary>Stable 32-bit FNV-1a hash used for deterministic element ids.</summary>
    public static string StableId(string prefix, string seed)
    {
        uint h = 2166136261;
        foreach (char c in seed) { h ^= c; h *= 16777619; }
        return $"{prefix}{h:x8}";
    }

    internal static StringBuilder Open(int w, int h, string? title, string? cssClass = null, int fontSize = 11)
    {
        var sb = new StringBuilder(4096);
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\" class=\"eln-svg{(cssClass is null ? "" : " " + cssClass)}\" font-size=\"{fontSize}\" style=\"font-family:{SvgTheme.Var("eln-font")}\">");
        if (title is not null) sb.Append("<title>").Append(Esc(title)).Append("</title>");
        return sb;
    }

    internal static void SvgText(StringBuilder sb, double x, double y, string text, string fill, string anchor = "start", string? extra = null) =>
        sb.Append($"<text x=\"{N(x)}\" y=\"{N(y)}\" text-anchor=\"{anchor}\" fill=\"{fill}\"{(extra is null ? "" : " " + extra)}>{Esc(text)}</text>");

    internal static void SvgLine(StringBuilder sb, double x1, double y1, double x2, double y2, string stroke, double width = 1, string? extra = null) =>
        sb.Append($"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" stroke=\"{stroke}\" stroke-width=\"{N(width)}\"{(extra is null ? "" : " " + extra)}/>");

    /// <summary>SVG arc path from angle a0 to a1 (radians, math convention, y up) around (cx, cy).</summary>
    internal static string Arc(double cx, double cy, double r, double a0, double a1)
    {
        double x0 = cx + r * Math.Cos(a0), y0 = cy - r * Math.Sin(a0), x1 = cx + r * Math.Cos(a1), y1 = cy - r * Math.Sin(a1);
        int large = Math.Abs(a1 - a0) > Math.PI ? 1 : 0;
        int sweep = a1 < a0 ? 1 : 0;
        return $"M {N(x0)} {N(y0)} A {N(r)} {N(r)} 0 {large} {sweep} {N(x1)} {N(y1)}";
    }

    /// <summary>Time-axis ticks for a span: returns tick instants and a label format.</summary>
    public static (List<DateTimeOffset> Ticks, string Format) TimeTicks(DateTimeOffset start, DateTimeOffset end, int maxTicks = 8)
    {
        TimeSpan[] steps =
        [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
            TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7), TimeSpan.FromDays(14), TimeSpan.FromDays(30),
        ];
        var span = end - start;
        if (span <= TimeSpan.Zero) return ([start], "HH:mm");
        var step = steps.FirstOrDefault(s => span.Ticks / s.Ticks <= maxTicks, TimeSpan.FromDays(Math.Ceiling(span.TotalDays / maxTicks)));
        var midnight = new DateTimeOffset(start.Date, start.Offset);
        long since = (start - midnight).Ticks;
        long first = (since + step.Ticks - 1) / step.Ticks * step.Ticks;
        var list = new List<DateTimeOffset>();
        for (var t = midnight.AddTicks(first); t <= end; t += step) list.Add(t);
        string fmt = step >= TimeSpan.FromDays(1) ? "dd MMM" : step >= TimeSpan.FromMinutes(1) ? (span > TimeSpan.FromDays(1) ? "dd HH:mm" : "HH:mm") : "HH:mm:ss";
        return (list, fmt);
    }
}
