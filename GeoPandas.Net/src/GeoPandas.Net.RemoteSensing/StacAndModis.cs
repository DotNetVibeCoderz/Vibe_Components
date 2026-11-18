using System.Globalization;
using System.Text.Json;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Raster;
using RasterGrid = GeoPandas.Net.Raster.Raster;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.RemoteSensing;

/// <summary>An asset of a STAC item.</summary>
public sealed record StacAsset(string Key, string Href, string? Type, string? Title, IReadOnlyList<string> Roles);

/// <summary>
/// SpatioTemporal Asset Catalog item (Sentinel-2 / Landsat COGs on AWS, Planetary Computer, Earth Search…): footprint, datetime, properties and
/// assets. Assets are read as rasters from local paths or downloaded over HTTP(S).
/// </summary>
public sealed class StacItem
{
    private readonly JsonElement _root;
    private readonly string? _baseDir;

    private StacItem(JsonElement root, string? baseDir) { _root = root; _baseDir = baseDir; }

    public static StacItem Load(string pathOrJson)
    {
        bool isJson = pathOrJson.TrimStart().StartsWith('{');
        var json = isJson ? pathOrJson : File.ReadAllText(pathOrJson);
        return new StacItem(JsonDocument.Parse(json).RootElement.Clone(), isJson ? null : Path.GetDirectoryName(Path.GetFullPath(pathOrJson)));
    }

    public static async Task<StacItem> LoadAsync(Uri url, HttpClient? http = null, CancellationToken ct = default)
    {
        http ??= Shared;
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return new StacItem(JsonDocument.Parse(json).RootElement.Clone(), null);
    }

    private static readonly HttpClient Shared = new();

    public string Id => _root.GetProperty("id").GetString() ?? "";
    public string? Collection => _root.TryGetProperty("collection", out var c) ? c.GetString() : null;
    public DateTime? DateTime => _root.GetProperty("properties").TryGetProperty("datetime", out var d) && d.ValueKind == JsonValueKind.String
        ? System.DateTime.Parse(d.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) : null;
    public double? CloudCover => _root.GetProperty("properties").TryGetProperty("eo:cloud_cover", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
    /// <summary>EPSG of the assets (projection extension), when present.</summary>
    public int? Epsg => _root.GetProperty("properties").TryGetProperty("proj:epsg", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32()
        : _root.GetProperty("properties").TryGetProperty("proj:code", out var pc) && pc.GetString() is { } s && s.StartsWith("EPSG:", StringComparison.Ordinal) ? int.Parse(s[5..], CultureInfo.InvariantCulture) : null;

    public IReadOnlyDictionary<string, string> Properties =>
        _root.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());

    /// <summary>Item footprint (GeoJSON geometry, WGS 84).</summary>
    public NetTopologySuite.Geometries.Geometry? Footprint =>
        _root.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object
            ? GeoPandas.Net.Data.GeoJson.Read($"{{\"type\":\"Feature\",\"properties\":{{}},\"geometry\":{g.GetRawText()}}}").Geometry[0] : null;

    public IReadOnlyList<StacAsset> Assets => _root.GetProperty("assets").EnumerateObject().Select(a => new StacAsset(a.Name,
        a.Value.GetProperty("href").GetString()!, a.Value.TryGetProperty("type", out var t) ? t.GetString() : null,
        a.Value.TryGetProperty("title", out var ti) ? ti.GetString() : null,
        a.Value.TryGetProperty("roles", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToArray() : [])).ToList();

    /// <summary>Reads a GeoTIFF/COG asset (local path, file:// or relative to the item; http(s) is downloaded).</summary>
    public async Task<RasterGrid> ReadAssetAsync(string key, HttpClient? http = null, CancellationToken ct = default)
    {
        var a = Assets.FirstOrDefault(x => x.Key == key) ?? throw new KeyNotFoundException($"Asset '{key}' not in item. Available: {string.Join(", ", Assets.Select(x => x.Key))}");
        if (Uri.TryCreate(a.Href, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            var bytes = await (http ?? Shared).GetByteArrayAsync(u, ct).ConfigureAwait(false);
            var tmp = Path.Combine(Path.GetTempPath(), $"gpd-stac-{Guid.NewGuid():N}.tif");
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            try { return GeoTiff.Read(tmp); } finally { File.Delete(tmp); }
        }
        var local = u is not null && u.IsFile ? u.LocalPath : Path.IsPathRooted(a.Href) || _baseDir is null ? a.Href : Path.Combine(_baseDir, a.Href);
        return GeoTiff.Read(local);
    }

    public RasterGrid ReadAsset(string key) => Task.Run(() => ReadAssetAsync(key)).GetAwaiter().GetResult();

    public override string ToString() => $"STAC {Collection}/{Id} {DateTime:yyyy-MM-dd} cloud {CloudCover:F1}% ({Assets.Count} assets)";
}

/// <summary>
/// The MODIS / VIIRS global sinusoidal tile grid (36 × 18 tiles of 10° at the equator, sphere R = 6 371 007.181 m) and HDF-EOS5 grid
/// datasets (VIIRS VNP/VJ1 *.h5, MODIS HDF5 conversions) read through PureHDF.
/// </summary>
public static class ModisGrid
{
    public const double SphereRadius = 6_371_007.181;
    private const double TileSize = 1_111_950.5196666666;   // 10° of arc on the sphere, metres
    private const double XMin = -20_015_109.354, YMax = 10_007_554.677;

    /// <summary>The MODIS sinusoidal CRS (SR-ORG:6842).</summary>
    public static CoordinateReferenceSystem Crs { get; } = CoordinateReferenceSystem.Parse($"+proj=sinu +lon_0=0 +x_0=0 +y_0=0 +R={SphereRadius.ToString("R", CultureInfo.InvariantCulture)} +units=m +no_defs");

    /// <summary>Tile (h, v) containing a lon/lat.</summary>
    public static (int H, int V) TileFor(double lon, double lat)
    {
        Crs.Projection!.Forward(lon * Math.PI / 180, lat * Math.PI / 180, out var x, out var y);
        return ((int)Math.Floor((x - XMin) / TileSize), (int)Math.Floor((YMax - y) / TileSize));
    }

    /// <summary>Geotransform of a tile at a pixel count per side (2400 = 463 m, 1200 = 926 m, 4800 = 232 m, 3000 = 375 m VIIRS…).</summary>
    public static GeoTransform TileTransform(int h, int v, int pixelsPerSide)
    {
        double cell = TileSize / pixelsPerSide;
        return new GeoTransform(XMin + h * TileSize, cell, 0, YMax - v * TileSize, 0, -cell);
    }

    /// <summary>Tile outline as a polygon in the sinusoidal CRS.</summary>
    public static Polygon TileBounds(int h, int v)
    {
        double x0 = XMin + h * TileSize, y1 = YMax - v * TileSize;
        return Geo.Box(x0, y1 - TileSize, x0 + TileSize, y1);
    }

    /// <summary>
    /// Reads a field of an HDF-EOS5 grid file (e.g. VIIRS VNP09GA: grid "VNP_Grid_1km_2D", field "SurfaceReflect_M5_1") as a georeferenced
    /// raster, applying <c>scale_factor</c>/<c>add_offset</c>/<c>_FillValue</c> attributes.
    /// </summary>
    public static RasterGrid ReadHdfEos5(string path, string field, string? grid = null)
    {
        using var f = PureHDF.H5File.OpenRead(path);
        var meta = ((PureHDF.IH5Dataset)f.Get("/HDFEOS INFORMATION/StructMetadata.0")).Read<string>();
        var grids = ParseGrids(meta);
        var g = grid is null ? grids.First(x => x.Fields.Contains(field)) : grids.First(x => x.Name == grid);
        var ds = (PureHDF.IH5Dataset)f.Get($"/HDFEOS/GRIDS/{g.Name}/Data Fields/{field}");
        var dims = ds.Space.Dimensions;
        int rows = (int)dims[^2], cols = (int)dims[^1];
        double[] raw = ds.Type.Class == PureHDF.H5DataTypeClass.FloatingPoint
            ? (ds.Type.Size == 4 ? ds.Read<float[]>().Select(x => (double)x).ToArray() : ds.Read<double[]>())
            : ds.Type.Size switch
            {
                1 => ds.Type.FixedPoint.IsSigned ? ds.Read<sbyte[]>().Select(x => (double)x).ToArray() : ds.Read<byte[]>().Select(x => (double)x).ToArray(),
                2 => ds.Type.FixedPoint.IsSigned ? ds.Read<short[]>().Select(x => (double)x).ToArray() : ds.Read<ushort[]>().Select(x => (double)x).ToArray(),
                4 => ds.Type.FixedPoint.IsSigned ? ds.Read<int[]>().Select(x => (double)x).ToArray() : ds.Read<uint[]>().Select(x => (double)x).ToArray(),
                _ => ds.Read<long[]>().Select(x => (double)x).ToArray()
            };
        double A(string n, double def)
        {
            if (!ds.AttributeExists(n)) return def;
            var a = ds.Attribute(n);
            return a.Type.Class == PureHDF.H5DataTypeClass.FloatingPoint ? (a.Type.Size == 4 ? a.Read<float[]>()[0] : a.Read<double[]>()[0])
                : a.Type.Size == 2 ? (a.Type.FixedPoint.IsSigned ? a.Read<short[]>()[0] : a.Read<ushort[]>()[0]) : a.Type.Size == 1 ? a.Read<byte[]>()[0] : a.Read<int[]>()[0];
        }
        double scale = A("scale_factor", 1), offset = A("add_offset", 0), fill = A("_FillValue", double.NaN);
        var band = new float[rows * cols];
        for (int i = 0; i < band.Length; i++) band[i] = raw[i] == fill ? float.NaN : (float)(raw[i] * scale + offset);
        double cellX = (g.LowerRight.X - g.UpperLeft.X) / cols, cellY = (g.UpperLeft.Y - g.LowerRight.Y) / rows;
        var crs = g.Projection.Contains("SNSOID") ? Crs : g.Projection.Contains("GEO") ? CoordinateReferenceSystem.Wgs84 : null;
        var ul = g.UpperLeft;
        if (g.Projection.Contains("GEO") && Math.Abs(ul.X) > 360) ul = (ul.X / 1e6, ul.Y / 1e6); // DMS-packed degrees in some products
        return new RasterGrid(cols, rows, [band], new GeoTransform(ul.X, cellX, 0, ul.Y, 0, -cellY), crs, float.NaN) { BandNames = [field] };
    }

    /// <summary>HDF-EOS grid structures from StructMetadata (ODL).</summary>
    public static IReadOnlyList<(string Name, (double X, double Y) UpperLeft, (double X, double Y) LowerRight, string Projection, IReadOnlyList<string> Fields)> ParseGrids(string structMetadata)
    {
        var res = new List<(string, (double, double), (double, double), string, IReadOnlyList<string>)>();
        string? name = null, proj = ""; (double, double) ul = default, lr = default; var fields = new List<string>();
        bool inGrid = false;
        foreach (var raw in structMetadata.Split('\n'))
        {
            var l = raw.Trim().TrimEnd('\0');
            if (l.StartsWith("GROUP=GRID_", StringComparison.Ordinal)) { inGrid = true; name = null; fields = []; proj = ""; continue; }
            if (inGrid && l.StartsWith("END_GROUP=GRID_", StringComparison.Ordinal))
            {
                if (name is not null) res.Add((name, ul, lr, proj, fields));
                inGrid = false;
                continue;
            }
            if (!inGrid) continue;
            int eq = l.IndexOf('=');
            if (eq < 0) continue;
            string k = l[..eq], v = l[(eq + 1)..].Trim('"');
            switch (k)
            {
                case "GridName": name = v; break;
                case "UpperLeftPointMtrs": ul = Pair(v); break;
                case "LowerRightMtrs": lr = Pair(v); break;
                case "Projection": proj = v; break;
                case "DataFieldName": fields.Add(v); break;
            }
        }
        return res;

        static (double, double) Pair(string s)
        {
            var p = s.Trim('(', ')').Split(',');
            return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
        }
    }
}
