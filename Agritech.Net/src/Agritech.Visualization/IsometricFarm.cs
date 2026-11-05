using System.Globalization;
using System.Text;

namespace Agritech.Visualization;

/// <summary>One plot in the isometric farm scene: grid position, size and crop canopy (height 0–1 drives extrusion and colour).</summary>
public sealed record FarmTile(int Col, int Row, string Label, double Vigour, double CanopyHeight, string? Accent = null, bool HasSensor = false, bool Alert = false);

/// <summary>
/// Isometric 2.5D farm renderer (SVG). Each tile is an extruded prism whose height and colour encode crop vigour,
/// with optional IoT sensor pins and alert beacons — a lightweight digital-twin overview that runs anywhere SVG does.
/// </summary>
public static class IsometricFarm
{
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static string Render(IReadOnlyList<FarmTile> tiles, int tileSize = 64, double maxHeight = 26, string? title = null)
    {
        if (tiles.Count == 0) return "";
        int cols = tiles.Max(t => t.Col) + 1, rows = tiles.Max(t => t.Row) + 1;
        double hw = tileSize, hh = tileSize / 2.0;
        double width = (cols + rows) * hw + 40, height = (cols + rows) * hh + maxHeight + 60;
        double ox = rows * hw + 20, oy = maxHeight + 30;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(width)} {N(height)}\" width=\"100%\" role=\"img\" font-size=\"11\">");
        if (title is not null) sb.Append($"<title>{SvgCharts.Esc(title)}</title>");
        sb.Append("<defs><filter id=\"agtShadow\" x=\"-20%\" y=\"-20%\" width=\"140%\" height=\"160%\"><feDropShadow dx=\"0\" dy=\"3\" stdDeviation=\"3\" flood-opacity=\"0.18\"/></filter></defs>");
        foreach (var t in tiles.OrderBy(t => t.Col + t.Row).ThenBy(t => t.Col))
        {
            double x = ox + (t.Col - t.Row) * hw, y = oy + (t.Col + t.Row) * hh;
            double hgt = Math.Clamp(t.CanopyHeight, 0, 1) * maxHeight;
            var top = ColorMap.Ndvi.Map(-0.2 + t.Vigour * 1.1);
            var left = Shade(top, 0.78); var right = Shade(top, 0.62);
            var soilL = new Rgba(122, 92, 62); var soilR = new Rgba(98, 72, 48);
            // soil slab
            sb.Append($"<polygon points=\"{P(x - hw, y + hh)} {P(x, y + 2 * hh)} {P(x, y + 2 * hh + 8)} {P(x - hw, y + hh + 8)}\" fill=\"{soilL.Hex}\"/>");
            sb.Append($"<polygon points=\"{P(x, y + 2 * hh)} {P(x + hw, y + hh)} {P(x + hw, y + hh + 8)} {P(x, y + 2 * hh + 8)}\" fill=\"{soilR.Hex}\"/>");
            // canopy prism
            sb.Append($"<g filter=\"url(#agtShadow)\"><title>{SvgCharts.Esc(t.Label)} — vigour {t.Vigour:P0}</title>");
            sb.Append($"<polygon points=\"{P(x - hw, y + hh - hgt)} {P(x, y + 2 * hh - hgt)} {P(x, y + 2 * hh)} {P(x - hw, y + hh)}\" fill=\"{left.Hex}\"/>");
            sb.Append($"<polygon points=\"{P(x, y + 2 * hh - hgt)} {P(x + hw, y + hh - hgt)} {P(x + hw, y + hh)} {P(x, y + 2 * hh)}\" fill=\"{right.Hex}\"/>");
            sb.Append($"<polygon points=\"{P(x, y - hgt)} {P(x + hw, y + hh - hgt)} {P(x, y + 2 * hh - hgt)} {P(x - hw, y + hh - hgt)}\" fill=\"{top.Hex}\" stroke=\"{(t.Accent ?? "#ffffff")}\" stroke-opacity=\"0.35\"/>");
            // crop rows texture
            for (int r = 1; r < 5; r++)
            {
                double f = r / 5.0;
                var a = (X: x - hw + hw * f, Y: y + hh - hgt - hh * f);
                var b = (X: x + hw * f, Y: y + 2 * hh - hgt - hh * f);
                sb.Append($"<line x1=\"{N(a.X)}\" y1=\"{N(a.Y)}\" x2=\"{N(b.X)}\" y2=\"{N(b.Y)}\" stroke=\"{Shade(top, 0.85).Hex}\" stroke-width=\"1.2\"/>");
            }
            sb.Append("</g>");
            if (t.HasSensor)
            {
                double sx = x + hw * 0.35, sy = y + hh * 0.9 - hgt;
                sb.Append($"<line x1=\"{N(sx)}\" y1=\"{N(sy)}\" x2=\"{N(sx)}\" y2=\"{N(sy - 22)}\" stroke=\"#3a3f36\" stroke-width=\"1.6\"/>");
                sb.Append($"<circle cx=\"{N(sx)}\" cy=\"{N(sy - 24)}\" r=\"4.2\" fill=\"#3c7ea6\" stroke=\"#fff\" stroke-width=\"1.2\"/>");
            }
            if (t.Alert)
            {
                double ax = x - hw * 0.3, ay = y + hh * 0.7 - hgt - 18;
                sb.Append($"<circle cx=\"{N(ax)}\" cy=\"{N(ay)}\" r=\"9\" fill=\"#b5513a\" opacity=\"0.25\"><animate attributeName=\"r\" values=\"7;12;7\" dur=\"1.8s\" repeatCount=\"indefinite\"/></circle>");
                sb.Append($"<circle cx=\"{N(ax)}\" cy=\"{N(ay)}\" r=\"5\" fill=\"#b5513a\"/><text x=\"{N(ax)}\" y=\"{N(ay + 3.5)}\" text-anchor=\"middle\" fill=\"#fff\" font-size=\"8\" font-weight=\"700\">!</text>");
            }
        }
        // Labels in a second pass so neighbouring prisms never hide them; pinned to the centre of each top face.
        foreach (var t in tiles)
        {
            double x = ox + (t.Col - t.Row) * hw, y = oy + (t.Col + t.Row) * hh;
            double hgt = Math.Clamp(t.CanopyHeight, 0, 1) * maxHeight;
            double ty = y + hh - hgt + 4;
            double wLabel = t.Label.Length * 6.4 + 14;
            sb.Append($"<rect x=\"{N(x - wLabel / 2)}\" y=\"{N(ty - 12)}\" width=\"{N(wLabel)}\" height=\"17\" rx=\"3\" fill=\"#1c2620\" fill-opacity=\"0.78\"/>");
            sb.Append($"<text x=\"{N(x)}\" y=\"{N(ty)}\" text-anchor=\"middle\" fill=\"#f1f5ef\" font-weight=\"600\">{SvgCharts.Esc(t.Label)}</text>");
        }
        return sb.Append("</svg>").ToString();

        string P(double px, double py) => $"{N(px)},{N(py)}";
    }

    private static Rgba Shade(Rgba c, double f) => new((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f), c.A);
}
