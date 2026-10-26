using System.Globalization;
using GDAL.Net.Rendering;

namespace GDAL.Net.Gallery;

/// <summary>All gallery use cases. Each sample runs real GDAL.Net code against the generated sample data.</summary>
public static partial class Catalog
{
    private static readonly Lazy<IReadOnlyList<GallerySample>> s_all = new(() =>
    [
        .. RasterSamples(),
        .. TerrainSamples(),
        .. RemoteSensingSamples(),
        .. ProjectionSamples(),
        .. VectorSamples(),
        .. MosaicSamples(),
        .. CloudSamples(),
        .. MultidimensionalSamples(),
        .. UtilitySamples(),
    ]);

    public static IReadOnlyList<GallerySample> All => s_all.Value;

    public static GallerySample? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    public static IEnumerable<IGrouping<SampleCategory, GallerySample>> ByCategory => All.GroupBy(s => s.Category);

    // ------------------------------------------------------------------ shared helpers

    internal static string F(double v, string format = "#,0.##") => v.ToString(format, CultureInfo.InvariantCulture);

    internal static ColorRamp Ramp(string name) => ColorRamp.Find(name) ?? ColorRamp.Viridis;

    internal static string[] RampNames => ColorRamp.All.Select(r => r.Name).ToArray();

    internal static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.00} MB",
    };

    internal static long SizeOf(Dataset ds) => ds.FileList.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    internal static readonly VectorStyle ContourStyle = VectorStyle.Lines(new Rgba(200, 85, 61, 220), 1.1);
    internal static readonly VectorStyle OutlineStyle = new() { Fill = new Rgba(42, 157, 143, 60), Stroke = new Rgba(18, 38, 58, 230), StrokeWidth = 1.6 };

    internal static VectorStyle LandCoverStyle => new()
    {
        Stroke = new Rgba(255, 255, 255, 110),
        StrokeWidth = 0.5,
        FillSelector = f => f["class"] is int c && c is >= 1 and <= 5
            ? Rgba.FromHex(SampleDataGenerator.LandCoverClasses[c - 1].Color) with { A = 200 }
            : null,
        LabelField = null,
    };

    /// <summary>Single observer/marker point layer.</summary>
    internal static StyledVector Marker(string name, double x, double y, Rgba color, double radius = 7, string? label = null)
    {
        using var p = Vector.Geometry.Point(x, y);
        var data = VectorData.FromGeometries(name, [p]);
        return new StyledVector(data, VectorStyle.Points(color, radius), label ?? name);
    }

    internal static TableData Table(IEnumerable<string> columns, IEnumerable<IEnumerable<string>> rows)
        => new([.. columns], rows.Select(r => (IReadOnlyList<string>)[.. r]).ToList());
}
