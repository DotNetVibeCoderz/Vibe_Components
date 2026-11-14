using System.Globalization;
using System.Net;
using System.Text;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.GIS;

/// <summary>
/// Map colors. Chrome and banded data colors are CSS variables with literal fallbacks so a host page can theme the SVG:
/// <c>--eln-bg</c> (#0b1626), <c>--eln-fg</c> (#e6edf7), <c>--eln-muted</c> (#8aa0b8), <c>--eln-grid</c> (#1e3350),
/// <c>--eln-accent</c> (#00b8d9), <c>--eln-ok</c> (#2bb673), <c>--eln-amber</c> (#f5a524), <c>--eln-danger</c> (#e5484d),
/// <c>--eln-v-low</c> (#2f6bff), <c>--eln-v-lowish</c> (#6fa8ff).
/// </summary>
public static class GisColors
{
    /// <summary>Background.</summary>
    public const string Background = "var(--eln-bg, #0b1626)";
    /// <summary>Foreground text.</summary>
    public const string Foreground = "var(--eln-fg, #e6edf7)";
    /// <summary>Secondary text, unenergized and out-of-service elements.</summary>
    public const string Muted = "var(--eln-muted, #8aa0b8)";
    /// <summary>Grid / graticule.</summary>
    public const string Grid = "var(--eln-grid, #1e3350)";
    /// <summary>Accent (buses/lines without results).</summary>
    public const string Accent = "var(--eln-accent, #00b8d9)";
    /// <summary>Normal state.</summary>
    public const string Ok = "var(--eln-ok, #2bb673)";
    /// <summary>Warning state.</summary>
    public const string Amber = "var(--eln-amber, #f5a524)";
    /// <summary>Alarm state / outage.</summary>
    public const string Danger = "var(--eln-danger, #e5484d)";
    /// <summary>Under-voltage below 0.95 pu.</summary>
    public const string VoltageLow = "var(--eln-v-low, #2f6bff)";
    /// <summary>Slight under-voltage 0.95–0.98 pu.</summary>
    public const string VoltageLowish = "var(--eln-v-lowish, #6fa8ff)";

    /// <summary>Banded (CSS-variable) voltage color: &lt;0.95 low, 0.95–0.98 lowish, 0.98–1.02 ok, 1.02–1.05 amber, &gt;1.05 danger [pu].</summary>
    public static string VoltageBand(double vm) => vm switch
    {
        < 0.95 => VoltageLow,
        < 0.98 => VoltageLowish,
        <= 1.02 => Ok,
        <= 1.05 => Amber,
        _ => Danger,
    };

    /// <summary>Banded (CSS-variable) loading color: &lt;70 % ok, 70–100 % amber, &gt;100 % danger.</summary>
    public static string LoadingBand(double percent) => percent switch { < 70 => Ok, <= 100 => Amber, _ => Danger };

    /// <summary>Continuous diverging voltage ramp centered at 1.0 pu (blue −0.1 pu → green 1.0 → red +0.1 pu), literal hex.</summary>
    public static string VoltageRamp(double vm)
    {
        double t = Math.Clamp((vm - 1.0) / 0.1, -1, 1);
        return t < 0 ? Lerp((0x2b, 0xb6, 0x73), (0x2f, 0x6b, 0xff), -t) : Lerp((0x2b, 0xb6, 0x73), (0xe5, 0x48, 0x4d), t);
    }

    /// <summary>Continuous loading ramp green (0 %) → amber (70 %) → red (≥100 %), literal hex.</summary>
    public static string LoadingRamp(double percent)
    {
        double p = Math.Max(0, percent);
        return p <= 70 ? Lerp((0x2b, 0xb6, 0x73), (0xf5, 0xa5, 0x24), p / 70) : Lerp((0xf5, 0xa5, 0x24), (0xe5, 0x48, 0x4d), Math.Min(1, (p - 70) / 30));
    }

    private static string Lerp((int R, int G, int B) a, (int R, int G, int B) b, double t)
    {
        int r = (int)Math.Round(a.R + (b.R - a.R) * t), g = (int)Math.Round(a.G + (b.G - a.G) * t), bl = (int)Math.Round(a.B + (b.B - a.B) * t);
        return $"#{r:x2}{g:x2}{bl:x2}";
    }
}

/// <summary>Options for <see cref="GisSvgRenderer"/>.</summary>
public sealed record GisSvgOptions
{
    /// <summary>SVG width [px]. Default 800.</summary>
    public int Width { get; init; } = 800;
    /// <summary>SVG height [px]. Default 560.</summary>
    public int Height { get; init; } = 560;
    /// <summary>Inner padding [px]. Default 28.</summary>
    public int Padding { get; init; } = 28;
    /// <summary>Optional title drawn top-left and used as the accessible name.</summary>
    public string? Title { get; init; }
    /// <summary>Bus labels: null = automatic (shown up to 60 buses).</summary>
    public bool? ShowLabels { get; init; }
    /// <summary>Draw the color legend. Default true.</summary>
    public bool ShowLegend { get; init; } = true;
    /// <summary>Use continuous literal-color ramps instead of CSS-variable bands for data colors. Default false.</summary>
    public bool ContinuousColors { get; init; }
    /// <summary>Buses to highlight as an outage area (convex hull, see <see cref="OutageArea"/>); null = automatic de-energized detection.</summary>
    public IReadOnlyCollection<int>? OutageBuses { get; init; }
    /// <summary>Buffer [km] around outage buses. Default 0.5 km.</summary>
    public double OutageBufferKm { get; init; } = 0.5;
    /// <summary>Routed branch paths (branch index → vertices).</summary>
    public IReadOnlyDictionary<int, GeoPoint[]>? BranchPaths { get; init; }
    /// <summary>Bus marker radius [px]. Default 5.</summary>
    public double BusRadius { get; init; } = 5;
}

/// <summary>
/// Renders a geo-located network to SVG in Web Mercator: branches colored by loading (with a <see cref="PowerFlowResult"/>), buses
/// colored by voltage, transformers drawn thicker, out-of-service branches dashed, the outage area shaded, plus a legend and scale bar.
/// Buses without a location are skipped (use <see cref="GeoLayout"/> first).
/// </summary>
public static class GisSvgRenderer
{
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Renders the map. Returns a complete <c>&lt;svg&gt;</c> element.</summary>
    public static string Render(PowerNetwork net, PowerFlowResult? result = null, GisSvgOptions? options = null)
    {
        var o = options ?? new GisSvgOptions();
        int w = o.Width, h = o.Height, pad = o.Padding;
        var located = net.Buses.Where(b => b.Location is not null).ToList();
        var pts = located.Select(b => b.Location!.Value).ToList();
        if (o.BranchPaths is not null) pts.AddRange(o.BranchPaths.Values.SelectMany(p => p));
        var proj = pts.Select(WebMercator.Project).ToList();
        double minX = proj.Count > 0 ? proj.Min(p => p.X) : 0, maxX = proj.Count > 0 ? proj.Max(p => p.X) : 1;
        double minY = proj.Count > 0 ? proj.Min(p => p.Y) : 0, maxY = proj.Count > 0 ? proj.Max(p => p.Y) : 1;
        double spanX = Math.Max(maxX - minX, 1), spanY = Math.Max(maxY - minY, 1);
        double top = o.Title is null ? pad : pad + 18;
        double scale = Math.Min((w - 2 * pad) / spanX, (h - top - pad) / spanY);
        double offX = pad + ((w - 2 * pad) - spanX * scale) / 2, offY = top + ((h - top - pad) - spanY * scale) / 2;
        (double X, double Y) P(GeoPoint g)
        {
            var (x, y) = WebMercator.Project(g);
            return (offX + (x - minX) * scale, offY + (maxY - y) * scale);
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\" font-family=\"inherit\" font-size=\"11\" class=\"eln-gis-map\">");
        sb.Append($"<title>{Esc(o.Title ?? net.Name)}</title>");
        sb.Append($"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"{GisColors.Background}\"/>");
        if (o.Title is not null)
            sb.Append($"<text x=\"{pad}\" y=\"{pad}\" fill=\"{GisColors.Foreground}\" font-size=\"14\" font-weight=\"600\">{Esc(o.Title)}</text>");

        // Outage area.
        var outage = o.OutageBuses ?? OutageArea.DeEnergizedBuses(net);
        var outageSet = new HashSet<int>(outage);
        if (outageSet.Count > 0)
        {
            var ring = OutageArea.Polygon(net, outageSet, o.OutageBufferKm);
            if (ring.Count >= 3)
                sb.Append($"<polygon class=\"eln-outage\" points=\"{string.Join(' ', ring.Select(g => { var (x, y) = P(g); return $"{N(x)},{N(y)}"; }))}\" fill=\"{GisColors.Danger}\" fill-opacity=\"0.16\" stroke=\"{GisColors.Danger}\" stroke-dasharray=\"6 4\" stroke-width=\"1.5\"><title>Outage area ({outageSet.Count} buses, {N(OutageArea.AreaKm2(ring))} km²)</title></polygon>");
        }

        // Branches.
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            GeoPoint[]? path = o.BranchPaths is not null && o.BranchPaths.TryGetValue(k, out var bp) && bp.Length >= 2 ? bp
                : net.GetBus(br.From).Location is GeoPoint a && net.GetBus(br.To).Location is GeoPoint c ? [a, c] : null;
            if (path is null) continue;
            double loading = result is not null && k < result.Branches.Count ? result.Branches[k].LoadingPercent : double.NaN;
            bool dead = !br.InService || outageSet.Contains(br.From) || outageSet.Contains(br.To);
            string color = dead ? GisColors.Muted
                : double.IsNaN(loading) || (result is not null && br.RateA <= 0) ? GisColors.Accent
                : o.ContinuousColors ? GisColors.LoadingRamp(loading) : GisColors.LoadingBand(loading);
            double sw = br.IsTransformer ? 3.2 : 2;
            string tip = $"{br.Name ?? $"{br.From}–{br.To}"}{(br.LengthKm > 0 ? $", {N(br.LengthKm)} km" : "")}{(double.IsNaN(loading) ? "" : $", loading {N(loading)} %")}";
            sb.Append($"<polyline class=\"eln-branch\" data-index=\"{k}\" points=\"{string.Join(' ', path.Select(g => { var (x, y) = P(g); return $"{N(x)},{N(y)}"; }))}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(sw)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"{(br.InService ? "" : " stroke-dasharray=\"4 4\"")}><title>{Esc(tip)}</title></polyline>");
        }

        // Buses.
        bool labels = o.ShowLabels ?? located.Count <= 60;
        var busRes = result?.Buses.ToDictionary(b => b.BusId);
        foreach (var bus in located)
        {
            var (x, y) = P(bus.Location!.Value);
            double? vm = busRes is not null && busRes.TryGetValue(bus.Id, out var r) ? r.Vm : null;
            string fill = outageSet.Contains(bus.Id) ? GisColors.Muted
                : vm is double v ? (o.ContinuousColors ? GisColors.VoltageRamp(v) : GisColors.VoltageBand(v)) : GisColors.Accent;
            string tip = $"{bus.Name ?? $"Bus {bus.Id}"} ({N(bus.BaseKv)} kV){(vm is double vv ? $", {vv.ToString("0.0000", CultureInfo.InvariantCulture)} pu" : "")}";
            double rad = o.BusRadius;
            if (bus.Type == BusType.Slack)
                sb.Append($"<rect class=\"eln-bus\" data-bus=\"{bus.Id}\" x=\"{N(x - rad * 1.1)}\" y=\"{N(y - rad * 1.1)}\" width=\"{N(rad * 2.2)}\" height=\"{N(rad * 2.2)}\" fill=\"{fill}\" stroke=\"{GisColors.Background}\" stroke-width=\"1.5\"><title>{Esc(tip)}</title></rect>");
            else
                sb.Append($"<circle class=\"eln-bus\" data-bus=\"{bus.Id}\" cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"{N(bus.Type == BusType.PV ? rad * 1.15 : rad)}\" fill=\"{fill}\" stroke=\"{GisColors.Background}\" stroke-width=\"1.5\"><title>{Esc(tip)}</title></circle>");
            if (labels)
                sb.Append($"<text x=\"{N(x + rad + 3)}\" y=\"{N(y - rad - 2)}\" fill=\"{GisColors.Foreground}\" font-size=\"10\">{Esc(bus.Name ?? bus.Id.ToString(CultureInfo.InvariantCulture))}</text>");
        }

        // Scale bar (nice length near 1/5 of the width).
        if (pts.Count >= 2)
        {
            var mid = new GeoPoint((WebMercator.Unproject(0, (minY + maxY) / 2)).Latitude, 0);
            double mPerPx = Math.Cos(mid.Latitude * Math.PI / 180) / scale; // ground metres per pixel
            double target = (w / 5.0) * mPerPx / 1000; // km
            double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double nice = new[] { 1, 2, 5, 10 }.Select(f => f * mag).Last(v => v <= target * 1.0001);
            double px = nice * 1000 / mPerPx;
            double sx = pad, sy = h - pad / 2.0;
            sb.Append($"<g class=\"eln-scale\"><line x1=\"{N(sx)}\" y1=\"{N(sy)}\" x2=\"{N(sx + px)}\" y2=\"{N(sy)}\" stroke=\"{GisColors.Foreground}\" stroke-width=\"2\"/>");
            sb.Append($"<text x=\"{N(sx + px + 6)}\" y=\"{N(sy + 4)}\" fill=\"{GisColors.Muted}\">{N(nice)} km</text></g>");
        }

        if (o.ShowLegend && result is not null)
        {
            double lx = w - pad - 128, ly = top;
            sb.Append($"<g class=\"eln-legend\" font-size=\"10\"><rect x=\"{N(lx - 8)}\" y=\"{N(ly - 4)}\" width=\"136\" height=\"132\" rx=\"6\" fill=\"{GisColors.Background}\" fill-opacity=\"0.85\" stroke=\"{GisColors.Grid}\"/>");
            sb.Append($"<text x=\"{N(lx)}\" y=\"{N(ly + 10)}\" fill=\"{GisColors.Foreground}\" font-weight=\"600\">Voltage [pu]</text>");
            (string C, string L)[] vItems = [(GisColors.VoltageLow, "< 0.95"), (GisColors.VoltageLowish, "0.95–0.98"), (GisColors.Ok, "0.98–1.02"), (GisColors.Amber, "1.02–1.05"), (GisColors.Danger, "> 1.05")];
            for (int i = 0; i < vItems.Length; i++)
                sb.Append($"<circle cx=\"{N(lx + 5)}\" cy=\"{N(ly + 22 + i * 12)}\" r=\"4\" fill=\"{vItems[i].C}\"/><text x=\"{N(lx + 14)}\" y=\"{N(ly + 26 + i * 12)}\" fill=\"{GisColors.Muted}\">{Esc(vItems[i].L)}</text>");
            double ly2 = ly + 88;
            sb.Append($"<text x=\"{N(lx)}\" y=\"{N(ly2)}\" fill=\"{GisColors.Foreground}\" font-weight=\"600\">Loading [%]</text>");
            (string C, string L)[] lItems = [(GisColors.Ok, "< 70"), (GisColors.Amber, "70–100"), (GisColors.Danger, "> 100")];
            for (int i = 0; i < lItems.Length; i++)
                sb.Append($"<line x1=\"{N(lx + i * 42)}\" y1=\"{N(ly2 + 12)}\" x2=\"{N(lx + i * 42 + 12)}\" y2=\"{N(ly2 + 12)}\" stroke=\"{lItems[i].C}\" stroke-width=\"3\"/><text x=\"{N(lx + i * 42 + 15)}\" y=\"{N(ly2 + 15)}\" fill=\"{GisColors.Muted}\">{Esc(lItems[i].L)}</text>");
            sb.Append("</g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
