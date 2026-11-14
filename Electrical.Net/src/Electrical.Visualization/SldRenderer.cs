using System.Text;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using static Electrical.Visualization.SvgFormat;

namespace Electrical.Visualization;

/// <summary>
/// Renders a single-line diagram (SLD) of a <see cref="PowerNetwork"/> to SVG: buses as horizontal bars, generators as circles with "~",
/// loads as arrows, shunts as capacitors and transformers as double circles. With a <see cref="PowerFlowResult"/> the bars are colored by
/// voltage (diverging ramp centered at 1.0 pu), branches by loading (green → amber → red) and flow arrows with MW labels are drawn.
/// </summary>
public static class SldRenderer
{
    /// <summary>Lays out and renders <paramref name="net"/>.</summary>
    public static string Render(PowerNetwork net, PowerFlowResult? result = null, SldOptions? options = null)
    {
        var o = options ?? new SldOptions();
        return Render(net, SldLayout.Compute(net, o), result, o);
    }

    /// <summary>Renders using a precomputed layout.</summary>
    public static string Render(PowerNetwork net, SldLayout layout, PowerFlowResult? result, SldOptions? options = null)
    {
        var o = options ?? new SldOptions();
        bool legend = result is not null && o.ShowLegend;
        int w = (int)Math.Ceiling(Math.Max(layout.Width, legend ? 560 : 200));
        int h = (int)Math.Ceiling(layout.Height + (legend ? 56 : 0));
        var sb = Open(w, h, o.Title ?? $"Single-line diagram: {net.Name}", "eln-sld", 11);
        string fg = SvgTheme.Var("eln-fg"), muted = SvgTheme.Var("eln-muted"), surface = SvgTheme.Var("eln-surface"), lineCol = SvgTheme.Var("eln-line");
        if (o.Background) sb.Append($"<rect width=\"{w}\" height=\"{h}\" fill=\"{SvgTheme.Var("eln-bg")}\"/>");
        if (o.Title is not null) SvgText(sb, o.Margin, o.Margin, o.Title, fg, extra: "font-size=\"15\" font-weight=\"700\"");

        var vRamp = ColorRamp.Voltage();
        var busRes = result?.Buses.ToDictionary(b => b.BusId);
        var brRes = result?.Branches.ToDictionary(b => b.Index);

        // branches
        sb.Append("<g class=\"eln-sld-branches\" fill=\"none\" stroke-linejoin=\"round\">");
        foreach (var e in layout.Edges)
        {
            var br = net.Branches[e.BranchIndex];
            BranchResult? r = brRes is not null && brRes.TryGetValue(e.BranchIndex, out var rr) ? rr : null;
            string color = !e.InService ? lineCol
                : r is not null && r.RateA > 0 ? ColorRamps.LoadingHex(r.LoadingPercent)
                : r is not null ? SvgTheme.Var("eln-accent") : fg;
            string pts = string.Join(' ', e.Path.Select(p => $"{N(p.X)},{N(p.Y)}"));
            string tip = $"{br.Name ?? (e.IsTransformer ? "Transformer" : "Line")} {e.FromBus}–{e.ToBus}" +
                         (r is null ? "" : $": {V(r.PFrom, "0.##")} MW / {V(r.QFrom, "0.##")} Mvar" + (r.RateA > 0 ? $", {V(r.LoadingPercent, "0.#")} %" : ""));
            sb.Append($"<g class=\"eln-branch\" data-branch=\"{e.BranchIndex}\"><title>{Esc(tip)}</title>");
            sb.Append($"<polyline points=\"{pts}\" stroke=\"{color}\" stroke-width=\"{(e.IsTransformer ? 2 : 2.4)}\"{(e.InService ? "" : " stroke-dasharray=\"6 4\"")}/>");
            if (e.IsTransformer) TransformerSymbol(sb, e, color, surface);
            if (r is not null && o.ShowFlows && e.InService) FlowArrow(sb, e, r, color, muted);
            sb.Append("</g>");
        }
        sb.Append("</g>");

        // devices
        sb.Append($"<g class=\"eln-sld-devices\" stroke=\"{fg}\" stroke-width=\"1.6\" fill=\"none\">");
        foreach (var d in layout.Devices)
        {
            switch (d.Kind)
            {
                case SldDeviceKind.Generator:
                {
                    var g = net.Generators[d.Index];
                    double cy = d.Y - 40;
                    string gc = g.InService ? fg : lineCol;
                    sb.Append($"<g class=\"eln-gen\" data-gen=\"{d.Index}\"><title>{Esc($"{g.Name ?? "Generator"} @ bus {g.Bus}: {V(result?.Generators.FirstOrDefault(x => x.Index == d.Index)?.P ?? g.P, "0.##")} MW")}</title>");
                    SvgLine(sb, d.X, d.Y, d.X, cy + 13, gc, 1.6);
                    sb.Append($"<circle cx=\"{N(d.X)}\" cy=\"{N(cy)}\" r=\"13\" fill=\"{surface}\" stroke=\"{gc}\"/>");
                    sb.Append($"<path d=\"M {N(d.X - 7)} {N(cy)} q 3.5 -7 7 0 t 7 0\" stroke=\"{gc}\"/>");
                    if (o.ShowLabels)
                    {
                        string gl = g.Name ?? $"G{d.Index + 1}";
                        if (result?.Generators.FirstOrDefault(x => x.Index == d.Index) is { } gr) gl += $" {V(gr.P, "0.#")} MW";
                        sb.Append($"<text x=\"{N(d.X)}\" y=\"{N(cy - 17)}\" text-anchor=\"middle\" fill=\"{muted}\" stroke=\"none\" font-size=\"10\">{Esc(gl)}</text>");
                    }
                    sb.Append("</g>");
                    break;
                }
                case SldDeviceKind.Load:
                {
                    var l = net.Loads[d.Index];
                    string lc = l.InService ? fg : lineCol;
                    double y2 = d.Y + 30;
                    sb.Append($"<g class=\"eln-load\" data-load=\"{d.Index}\"><title>{Esc($"{l.Name ?? "Load"} @ bus {l.Bus}: {V(l.P, "0.##")} MW + j{V(l.Q, "0.##")} Mvar")}</title>");
                    SvgLine(sb, d.X, d.Y, d.X, y2 - 8, lc, 1.6);
                    sb.Append($"<polygon points=\"{N(d.X - 6)},{N(y2 - 9)} {N(d.X + 6)},{N(y2 - 9)} {N(d.X)},{N(y2 + 2)}\" fill=\"{lc}\" stroke=\"none\"/>");
                    if (o.ShowLabels) sb.Append($"<text x=\"{N(d.X)}\" y=\"{N(y2 + 14)}\" text-anchor=\"middle\" fill=\"{muted}\" stroke=\"none\" font-size=\"10\">{V(l.P, "0.#")} MW</text>");
                    sb.Append("</g>");
                    break;
                }
                case SldDeviceKind.Shunt:
                {
                    var b = net.Buses[d.Index];
                    sb.Append($"<g class=\"eln-shunt\"><title>{Esc($"Shunt @ bus {b.Id}: {V(b.Bs, "0.##")} Mvar")}</title>");
                    SvgLine(sb, d.X, d.Y, d.X, d.Y + 16, fg, 1.6);
                    SvgLine(sb, d.X - 8, d.Y + 16, d.X + 8, d.Y + 16, fg, 2);
                    SvgLine(sb, d.X - 8, d.Y + 21, d.X + 8, d.Y + 21, fg, 2);
                    SvgLine(sb, d.X, d.Y + 21, d.X, d.Y + 30, fg, 1.6);
                    SvgLine(sb, d.X - 6, d.Y + 30, d.X + 6, d.Y + 30, fg, 1.4);
                    SvgLine(sb, d.X - 3, d.Y + 33, d.X + 3, d.Y + 33, fg, 1.2);
                    sb.Append("</g>");
                    break;
                }
            }
        }
        sb.Append("</g>");

        // bus bars on top
        sb.Append("<g class=\"eln-sld-buses\">");
        foreach (var nd in layout.Nodes)
        {
            var bus = net.GetBus(nd.BusId);
            BusResult? br = busRes is not null && busRes.TryGetValue(nd.BusId, out var x) ? x : null;
            string color = bus.Type == BusType.Isolated ? lineCol : br is not null ? vRamp.Hex(br.Vm) : fg;
            string tip = $"{bus.Name ?? $"Bus {bus.Id}"} ({V(bus.BaseKv, "0.##")} kV, {bus.Type})" + (br is null ? "" : $": {V(br.Vm, "0.0000")} pu ∠ {V(br.VaDeg, "0.00")}°");
            sb.Append($"<g class=\"eln-bus\" data-bus=\"{nd.BusId}\"><title>{Esc(tip)}</title>");
            sb.Append($"<rect x=\"{N(nd.Left)}\" y=\"{N(nd.Y - 3.5)}\" width=\"{N(nd.Width)}\" height=\"7\" rx=\"2\" fill=\"{color}\"/>");
            if (bus.Type == BusType.Slack)
                sb.Append($"<rect x=\"{N(nd.Left - 2)}\" y=\"{N(nd.Y - 5.5)}\" width=\"{N(nd.Width + 4)}\" height=\"11\" rx=\"3\" fill=\"none\" stroke=\"{SvgTheme.Var("eln-accent")}\" stroke-width=\"1.2\"/>");
            if (o.ShowLabels)
            {
                SvgText(sb, nd.Left, nd.Y - 9, bus.Name ?? $"Bus {bus.Id}", fg, extra: "font-size=\"11\" font-weight=\"600\"");
                string sub = br is not null ? $"{V(br.Vm, "0.000")} pu ∠{V(br.VaDeg, "0.0")}°" : $"{V(bus.BaseKv, "0.##")} kV";
                SvgText(sb, nd.Right, nd.Y - 9, sub, br is not null ? color : muted, "end", "font-size=\"10\"");
            }
            sb.Append("</g>");
        }
        sb.Append("</g>");

        if (legend)
        {
            double ly = layout.Height + 6;
            SvgText(sb, o.Margin, ly + 6, "Voltage [pu]", muted, extra: "font-size=\"10\" font-weight=\"600\"");
            SvgCharts.ColorLegend(sb, vRamp, o.Margin, ly + 12, 200, 0.9, 1.1, "pu", "0.00");
            SvgText(sb, o.Margin + 260, ly + 6, "Branch loading [%]", muted, extra: "font-size=\"10\" font-weight=\"600\"");
            SvgCharts.ColorLegend(sb, ColorRamp.Loading, o.Margin + 260, ly + 12, 200, 0, 120, "%", "0");
        }
        return sb.Append("</svg>").ToString();
    }

    private static (double X, double Y, double Dx, double Dy, double Len) LongestSegment(SldEdge e)
    {
        (double, double, double, double, double) best = (e.Path[0].X, e.Path[0].Y, 0, 1, 0);
        for (int i = 1; i < e.Path.Count; i++)
        {
            double dx = e.Path[i].X - e.Path[i - 1].X, dy = e.Path[i].Y - e.Path[i - 1].Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len > best.Item5 + 1e-9) best = ((e.Path[i].X + e.Path[i - 1].X) / 2, (e.Path[i].Y + e.Path[i - 1].Y) / 2, dx / len, dy / len, len);
        }
        return best;
    }

    private static void TransformerSymbol(StringBuilder sb, SldEdge e, string color, string surface)
    {
        // on the last vertical segment (towards the To bus), or the first one for same-layer routes
        var p = e.Path;
        (double X, double Y) a = p[^2], b = p[^1];
        if (Math.Abs(a.X - b.X) > 0.5 && p.Count >= 2) { a = p[0]; b = p[1]; }
        double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2;
        bool vertical = Math.Abs(a.X - b.X) < 0.5;
        double ox = vertical ? 0 : 6, oy = vertical ? 6 : 0;
        sb.Append($"<circle cx=\"{N(cx - ox)}\" cy=\"{N(cy - oy)}\" r=\"9\" fill=\"{surface}\" stroke=\"{color}\" stroke-width=\"1.8\"/>");
        sb.Append($"<circle cx=\"{N(cx + ox)}\" cy=\"{N(cy + oy)}\" r=\"9\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.8\"/>");
    }

    private static void FlowArrow(StringBuilder sb, SldEdge e, BranchResult r, string color, string muted)
    {
        var (x, y, dx, dy, len) = LongestSegment(e);
        if (len < 18) return;
        if (e.IsTransformer && Math.Abs(dx) < 1e-9 && e.Path.Count == 2) y -= len * 0.25; // keep clear of the symbol
        double sgn = r.PFrom >= 0 ? 1 : -1;
        dx *= sgn; dy *= sgn;
        double hx = x + dx * 6, hy = y + dy * 6, bx = x - dx * 4, by = y - dy * 4, px = -dy * 5, py = dx * 5;
        sb.Append($"<polygon points=\"{N(hx)},{N(hy)} {N(bx + px)},{N(by + py)} {N(bx - px)},{N(by - py)}\" fill=\"{color}\" stroke=\"none\"/>");
        bool horizontal = Math.Abs(dx) > Math.Abs(dy);
        double tx = horizontal ? x : x - 9, ty = horizontal ? y - 7 : y + 4;
        sb.Append($"<text x=\"{N(tx)}\" y=\"{N(ty)}\" text-anchor=\"{(horizontal ? "middle" : "end")}\" fill=\"{muted}\" stroke=\"none\" font-size=\"9.5\">{V(Math.Abs(r.PFrom), "0.#")} MW</text>");
    }
}
