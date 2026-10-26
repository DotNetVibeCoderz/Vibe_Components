using GDAL.Net.Rendering;

namespace GDAL.Net.Gallery;

/// <summary>Identity shared by the Blazor and Avalonia galleries ("cartographic print" palette).</summary>
public static class Branding
{
    public const string Product = "GDAL.Net";
    public const string Gallery = "GDAL.Net Gallery";
    public static readonly Text Credit = new("Created by Gravicode Studios, led by Kang Fadhil", "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil");
    public static readonly Text Tagline = new("Every GDAL capability, running live in .NET 10", "Setiap kemampuan GDAL, berjalan langsung di .NET 10");

    // Palette tokens
    public const string Ink = "#12263A";
    public const string Paper = "#EEF2F0";
    public const string Contour = "#C8553D";
    public const string Shoal = "#2A9D8F";
    public const string Lichen = "#8AB17D";
    public const string Graticule = "#C9D3CF";
    public const string InkDark = "#0E1B26";
    public const string PanelDark = "#15283A";
    public const string TextDark = "#E6EEEA";

    // Type
    public const string DisplayFont = "Bricolage Grotesque";
    public const string BodyFont = "IBM Plex Sans";
    public const string MonoFont = "JetBrains Mono";

    /// <summary>
    /// The gallery's signature: real contour lines computed by GDAL.Net from the sample DEM, normalized to a
    /// 0..1 box (x right, y down) so either UI can scale them into its header.
    /// </summary>
    public static IReadOnlyList<(double Elevation, double[] Points)> SignatureContours(SampleContext context, double interval = 80, int maxLines = 400)
    {
        using var dem = context.OpenDem();
        using var small = GdalUtils.Translate(dem, "", new TranslateOptions { Format = "MEM", OutputSize = (400, 0), Resampling = ResampleAlgorithm.Average });
        using var contours = RasterAlgorithms.GenerateContours(small.GetBand(1), interval);
        var data = VectorData.Load(contours.GetLayer(0));
        var ext = small.Extent;
        var list = new List<(double, double[])>();
        foreach (var f in data.Features.Take(maxLines))
        {
            var elev = Convert.ToDouble(f["elev"] ?? 0);
            foreach (var shape in f.Shapes)
            {
                foreach (var ring in shape.Rings)
                {
                    var pts = new double[ring.Length];
                    for (var i = 0; i + 1 < ring.Length; i += 2)
                    {
                        pts[i] = (ring[i] - ext.MinX) / ext.Width;
                        pts[i + 1] = (ext.MaxY - ring[i + 1]) / ext.Height;
                    }
                    list.Add((elev, pts));
                }
            }
        }
        return list;
    }
}
