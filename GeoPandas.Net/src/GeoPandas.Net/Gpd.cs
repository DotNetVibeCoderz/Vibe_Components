using GeoPandas.Net.Data;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Native;

namespace GeoPandas.Net;

/// <summary>
/// Python-style facade: <c>Gpd.ReadFile("roads.gpkg")</c> mirrors <c>gpd.read_file</c>.
/// </summary>
public static class Gpd
{
    static Gpd()
    {
        CAD.Dxf.Register();
        Arrow.ArrowDrivers.Register();
    }

    /// <summary>Registers every optional file driver (DXF, GeoParquet, Arrow IPC). Called automatically by the facade.</summary>
    public static void RegisterDrivers() { }

    public static string Version => typeof(Gpd).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Credits shown by the CLI, gallery and docs.</summary>
    public const string Credits = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil";

    public static string About =>
        $"GeoPandas.Net {Version} — native kernels: {NativeKernels.Backend}, runtime: {Environment.Version}, {Credits}";

    /// <summary>Reads GeoJSON, Shapefile, GeoPackage, FlatGeobuf, GeoParquet, Arrow/Feather, CSV or DXF (by extension).</summary>
    public static GeoDataFrame ReadFile(string path, string? layer = null) => GeoIO.Read(path, layer);

    /// <summary>Lazy read with filter/column/limit pushdown (FlatGeobuf, GeoParquet and GeoPackage push into the file).</summary>
    public static Lazy.LazyGeoDataFrame Scan(string path, string? layer = null) => GeoIO.Scan(path, layer);

    /// <summary>Writes by extension (GeoJSON, Shapefile, GeoPackage, FlatGeobuf, GeoParquet, Arrow/Feather, CSV, DXF).</summary>
    public static void ToFile(GeoDataFrame gdf, string path, string? layer = null) => GeoIO.Write(gdf, path, layer);

    public static GeoSeries PointsFromXY(ReadOnlySpan<double> x, ReadOnlySpan<double> y, string? crs = null) =>
        GeoSeries.PointsFromXY(x, y, crs is null ? null : CoordinateReferenceSystem.Parse(crs));

    public static GeoDataFrame Concat(params GeoDataFrame[] frames) => GeoDataFrame.Concat(frames);

    public static CoordinateReferenceSystem Crs(string definition) => CoordinateReferenceSystem.Parse(definition);
}
