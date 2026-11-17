using System.Collections.Concurrent;
using GeoPandas.Net.Lazy;

namespace GeoPandas.Net.Data;

/// <summary>A file-format driver: read and/or write GeoDataFrames.</summary>
public sealed record GeoDriver(string Name, string[] Extensions, Func<string, string?, GeoDataFrame>? Reader, Action<GeoDataFrame, string, string?>? Writer);

/// <summary>
/// Unified IO entry point (GeoPandas <c>read_file</c> / <c>to_file</c>). Dispatches on file extension.
/// Other packages (e.g. GeoPandas.Net.CAD for DXF) register extra drivers via <see cref="Register"/>.
/// </summary>
public static class GeoIO
{
    private static readonly ConcurrentDictionary<string, GeoDriver> s_drivers = new(StringComparer.OrdinalIgnoreCase);

    static GeoIO()
    {
        Register(new GeoDriver("GeoJSON", [".geojson", ".json"], (p, _) => GeoJson.ReadFile(p), (g, p, _) => GeoJson.WriteFile(g, p)));
        Register(new GeoDriver("ESRI Shapefile", [".shp"], (p, _) => Shapefile.Read(p), (g, p, _) => Shapefile.Write(g, p)));
        Register(new GeoDriver("GPKG", [".gpkg"], (p, l) => GeoPackage.Read(p, l), (g, p, l) => GeoPackage.Write(g, p, l ?? Path.GetFileNameWithoutExtension(p))));
        Register(new GeoDriver("CSV", [".csv"], (p, _) => Csv.Read(p), (g, p, _) => Csv.Write(g, p)));
        Register(new GeoDriver("FlatGeobuf", [".fgb"], (p, _) => FlatGeobuf.Read(p), (g, p, _) => FlatGeobuf.Write(g, p, new FlatGeobufWriteOptions { LayerName = Path.GetFileNameWithoutExtension(p) })));
        RegisterScanner([".fgb"], (p, _) => new FlatGeobufScanSource(p));
        RegisterScanner([".gpkg"], (p, l) => new GeoPackageScanSource(p, l));
        RegisterOptionalDrivers();
    }

    /// <summary>
    /// Registers drivers from optional packages that are deployed with the application (GeoPandas.Net.Arrow → GeoParquet/Arrow IPC,
    /// GeoPandas.Net.CAD → DXF), so <c>GeoIO.Read("x.parquet")</c> works without calling their <c>Register()</c> first.
    /// </summary>
    private static void RegisterOptionalDrivers()
    {
        foreach (var (assembly, type) in new[] { ("GeoPandas.Net.Arrow", "GeoPandas.Net.Arrow.ArrowDrivers"), ("GeoPandas.Net.CAD", "GeoPandas.Net.CAD.Dxf") })
        {
            try
            {
                var asm = System.Reflection.Assembly.Load(assembly);
                asm.GetType(type)?.GetMethod("Register", Type.EmptyTypes)?.Invoke(null, null);
            }
            catch (Exception) { /* package not deployed */ }
        }
    }

    private static readonly ConcurrentDictionary<string, Func<string, string?, IGeoScanSource>> s_scanners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a lazy scan source factory (pushdown-aware reader) for file extensions.</summary>
    public static void RegisterScanner(string[] extensions, Func<string, string?, IGeoScanSource> factory)
    {
        foreach (var e in extensions) s_scanners[e] = factory;
    }

    /// <summary>
    /// Lazy read (Polars <c>scan_*</c> / Dask style): returns a query plan; filters, column selection and limits are pushed
    /// into the reader when the format supports it (FlatGeobuf R-tree, GeoPackage R-tree, GeoParquet row groups).
    /// Formats without a scanner are read eagerly and wrapped.
    /// </summary>
    public static LazyGeoDataFrame Scan(string path, string? layer = null, int batchSize = 65_536)
    {
        _ = Drivers; // make sure static registrations ran
        var source = s_scanners.TryGetValue(Path.GetExtension(path), out var f)
            ? f(path, layer)
            : new InMemoryScanSource(Read(path, layer), $"{DriverFor(path).Name} '{Path.GetFileName(path)}' (read eagerly)");
        return LazyGeoDataFrame.From(source, batchSize);
    }

    public static void Register(GeoDriver driver)
    {
        foreach (var e in driver.Extensions) s_drivers[e] = driver;
    }

    public static IReadOnlyCollection<GeoDriver> Drivers => s_drivers.Values.Distinct().ToArray();

    public static GeoDriver DriverFor(string path) =>
        s_drivers.TryGetValue(Path.GetExtension(path), out var d) ? d : throw new NotSupportedException($"No driver for '{Path.GetExtension(path)}'. Registered: {string.Join(", ", s_drivers.Keys)}");

    /// <summary>Reads any supported vector file. <paramref name="layer"/> selects a GeoPackage table.</summary>
    public static GeoDataFrame Read(string path, string? layer = null) =>
        (DriverFor(path).Reader ?? throw new NotSupportedException($"{DriverFor(path).Name} is write-only."))(path, layer);

    /// <summary>Writes to any supported vector file (format from extension).</summary>
    public static void Write(GeoDataFrame gdf, string path, string? layer = null) =>
        (DriverFor(path).Writer ?? throw new NotSupportedException($"{DriverFor(path).Name} is read-only."))(gdf, path, layer);
}

/// <summary>Fluent IO extensions: <c>gdf.ToFile("out.gpkg")</c>.</summary>
public static class GeoIOExtensions
{
    public static void ToFile(this GeoDataFrame gdf, string path, string? layer = null) => GeoIO.Write(gdf, path, layer);
    public static string ToGeoJson(this GeoDataFrame gdf, bool indented = false) => GeoJson.Write(gdf, indented);
    public static void ToCsv(this DataFrame df, string path) => Csv.Write(df, path);
}
