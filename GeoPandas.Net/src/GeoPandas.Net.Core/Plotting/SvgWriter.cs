using System.Globalization;
using System.Security;
using System.Text;

namespace GeoPandas.Net.Plotting;

/// <summary>Serializes a <see cref="RenderedMap"/> to standalone SVG.</summary>
public static class SvgWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Write(RenderedMap map, string fontFamily)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {map.Width} {map.Height}\" width=\"{map.Width}\" height=\"{map.Height}\" font-family=\"{SecurityElement.Escape(fontFamily)}\">");
        sb.Append(Inv, $"<rect width=\"100%\" height=\"100%\" fill=\"{map.Background.ToHex()}\"/>");
        int gradId = 0;
        foreach (var shape in map.Shapes)
        {
            switch (shape)
            {
                case PathShape p:
                    sb.Append("<path d=\"");
                    foreach (var part in p.Parts)
                    {
                        for (int i = 0; i + 1 < part.Length; i += 2)
                            sb.Append(i == 0 ? 'M' : 'L').Append(part[i].ToString(Inv)).Append(',').Append(part[i + 1].ToString(Inv));
                        if (p.Closed) sb.Append('Z');
                    }
                    sb.Append('"');
                    Paint(sb, "fill", p.Fill, defaultNone: true);
                    if (p.Closed) sb.Append(" fill-rule=\"evenodd\"");
                    Paint(sb, "stroke", p.Stroke, defaultNone: true);
                    if (p.Stroke is not null) sb.Append(Inv, $" stroke-width=\"{p.StrokeWidth}\" stroke-linejoin=\"round\" stroke-linecap=\"round\"");
                    if (p.Dash is { Length: > 0 }) sb.Append(" stroke-dasharray=\"").Append(string.Join(",", p.Dash.Select(d => d.ToString(Inv)))).Append('"');
                    sb.Append("/>");
                    break;
                case CircleShape c:
                    sb.Append(Inv, $"<circle cx=\"{c.X:0.##}\" cy=\"{c.Y:0.##}\" r=\"{c.Radius:0.##}\"");
                    Paint(sb, "fill", c.Fill, defaultNone: true);
                    Paint(sb, "stroke", c.Stroke, defaultNone: true);
                    if (c.Stroke is not null) sb.Append(Inv, $" stroke-width=\"{c.StrokeWidth}\"");
                    sb.Append("/>");
                    break;
                case RectShape r:
                    sb.Append(Inv, $"<rect x=\"{r.X:0.##}\" y=\"{r.Y:0.##}\" width=\"{r.Width:0.##}\" height=\"{r.Height:0.##}\" rx=\"{r.CornerRadius}\"");
                    Paint(sb, "fill", r.Fill, defaultNone: true);
                    Paint(sb, "stroke", r.Stroke, defaultNone: true);
                    if (r.Stroke is not null) sb.Append(Inv, $" stroke-width=\"{r.StrokeWidth}\"");
                    sb.Append("/>");
                    break;
                case TextShape t:
                    string anchor = t.Anchor switch { TextAnchor.Middle => "middle", TextAnchor.End => "end", _ => "start" };
                    string halo = t.Halo is { } h ? $" stroke=\"{h.ToHex()}\" stroke-opacity=\"{h.Opacity.ToString("0.##", Inv)}\" stroke-width=\"3\" paint-order=\"stroke\"" : "";
                    sb.Append(Inv, $"<text x=\"{t.X:0.##}\" y=\"{t.Y:0.##}\" font-size=\"{t.Size}\" text-anchor=\"{anchor}\"{(t.Bold ? " font-weight=\"700\"" : "")} fill=\"{t.Color.ToHex()}\"");
                    if (t.Color.A < 255) sb.Append(Inv, $" fill-opacity=\"{t.Color.Opacity:0.##}\"");
                    sb.Append(halo).Append('>').Append(SecurityElement.Escape(t.Text)).Append("</text>");
                    break;
                case ImageShape im:
                    var png = Convert.ToBase64String(PngEncoder.Encode(im.Rgba, im.PixelWidth, im.PixelHeight));
                    sb.Append(Inv, $"<image x=\"{im.X:0.##}\" y=\"{im.Y:0.##}\" width=\"{im.Width:0.##}\" height=\"{im.Height:0.##}\" opacity=\"{im.Opacity:0.##}\" preserveAspectRatio=\"none\" style=\"image-rendering:pixelated\" href=\"data:image/png;base64,{png}\"/>");
                    break;
                case GradientShape g:
                    string id = $"g{gradId++}";
                    sb.Append($"<defs><linearGradient id=\"{id}\">");
                    for (int i = 0; i < g.Stops.Count; i++)
                        sb.Append(Inv, $"<stop offset=\"{(g.Stops.Count == 1 ? 0 : i / (double)(g.Stops.Count - 1)):0.###}\" stop-color=\"{g.Stops[i].ToHex()}\"/>");
                    sb.Append("</linearGradient></defs>");
                    sb.Append(Inv, $"<rect x=\"{g.X:0.##}\" y=\"{g.Y:0.##}\" width=\"{g.Width:0.##}\" height=\"{g.Height:0.##}\" rx=\"3\" fill=\"url(#{id})\"/>");
                    break;
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Paint(StringBuilder sb, string attr, Rgba? color, bool defaultNone)
    {
        if (color is { } c && c.A > 0)
        {
            sb.Append(' ').Append(attr).Append("=\"").Append(c.ToHex()).Append('"');
            if (c.A < 255) sb.Append(' ').Append(attr).Append("-opacity=\"").Append(c.Opacity.ToString("0.###", Inv)).Append('"');
        }
        else if (defaultNone) sb.Append(' ').Append(attr).Append("=\"none\"");
    }
}
