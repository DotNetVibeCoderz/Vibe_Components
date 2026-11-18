using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>
/// A geoid (or any vertical offset) grid: geoid undulation N in metres on a regular lon/lat grid, bilinear interpolation.
/// Orthometric height H = h − N (h = ellipsoidal/GNSS height). Loads NOAA/PROJ GTX, ISG 1.0/2.0 (the format of national geoids such as
/// InaGEOID2020, EGM2008 ISG releases) and GeoTIFF grids (PROJ CDN, via <c>GeoPandas.Net.Raster</c>); ships a coarse 1° EGM96.
/// </summary>
public sealed class GeoidModel
{
    public GeoidModel(string name, double west, double south, double lonInc, double latInc, int cols, int rows, float[] undulation, bool globalLongitude = false)
    {
        if (undulation.Length != rows * cols) throw new ArgumentException("Grid size mismatch.");
        Name = name; West = west; South = south; LonInc = lonInc; LatInc = latInc; Cols = cols; Rows = rows; Values = undulation;
        GlobalLongitude = globalLongitude || Math.Abs((cols - 1) * lonInc - 360) < lonInc * 0.51 || Math.Abs(cols * lonInc - 360) < 1e-9;
    }

    public string Name { get; }
    public double West { get; }
    public double South { get; }
    public double LonInc { get; }
    public double LatInc { get; }
    public int Cols { get; }
    public int Rows { get; }
    /// <summary>Row-major from the south-west node (metres).</summary>
    public float[] Values { get; }
    public bool GlobalLongitude { get; }
    public double North => South + (Rows - 1) * LatInc;
    public double East => West + (Cols - 1) * LonInc;

    /// <summary>Geoid undulation N (m) at a lon/lat in degrees; NaN outside the grid or on nodata nodes.</summary>
    public double Undulation(double lon, double lat)
    {
        double fy = (lat - South) / LatInc;
        if (fy < -1e-9 || fy > Rows - 1 + 1e-9) return double.NaN;
        double fx = (lon - West) / LonInc;
        if (GlobalLongitude)
        {
            double period = 360 / LonInc;
            fx = ((fx % period) + period) % period;
        }
        else if (fx < -1e-9 || fx > Cols - 1 + 1e-9) return double.NaN;
        fy = Math.Clamp(fy, 0, Rows - 1);
        int r = Math.Min((int)fy, Math.Max(Rows - 2, 0));
        int c = (int)Math.Floor(fx);
        double ty = fy - r, tx = fx - c;
        int C(int k) => GlobalLongitude ? ((k % Cols) + Cols) % Cols : Math.Clamp(k, 0, Cols - 1);
        int r1 = Math.Min(r + 1, Rows - 1);
        double v00 = Values[r * Cols + C(c)], v01 = Values[r * Cols + C(c + 1)], v10 = Values[r1 * Cols + C(c)], v11 = Values[r1 * Cols + C(c + 1)];
        if (!GlobalLongitude && c >= Cols - 1) { v01 = v00; v11 = v10; }
        return v00 * (1 - tx) * (1 - ty) + v01 * tx * (1 - ty) + v10 * (1 - tx) * ty + v11 * tx * ty;
    }

    /// <summary>H = h − N.</summary>
    public double OrthometricHeight(double lon, double lat, double ellipsoidalHeight) => ellipsoidalHeight - Undulation(lon, lat);
    /// <summary>h = H + N.</summary>
    public double EllipsoidalHeight(double lon, double lat, double orthometricHeight) => orthometricHeight + Undulation(lon, lat);

    // ================================================================== built-in

    private static readonly Lazy<GeoidModel> s_egm96 = new(() =>
    {
        using var s = typeof(GeoidModel).Assembly.GetManifestResourceStream("GeoPandas.Net.Geometry.egm96-1deg-int16.gz")
            ?? throw new InvalidOperationException("Embedded EGM96 grid is missing.");
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var b = ms.ToArray();
        int cols = 361, rows = 181;
        var v = new float[cols * rows];
        for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2)) / 100f;
        return new GeoidModel("EGM96 (1° coarse)", -180, -90, 1, 1, cols, rows, v, true);
    });

    /// <summary>
    /// EGM96 sampled at 1° from NGA's 15′ grid (PROJ CDN <c>us_nga_egm96_15.tif</c>): ~130 KB, typically within ±1 m of the full model.
    /// For surveying load the full EGM96/EGM2008 or a national geoid (InaGEOID2020, GEOID18, AUSGeoid2020…).
    /// </summary>
    public static GeoidModel Egm96Coarse => s_egm96.Value;

    // ================================================================== GTX

    /// <summary>NOAA/PROJ .gtx: big-endian header (lat0, lon0, dlat, dlon as doubles; rows, cols as int32), float32 rows from the south.</summary>
    public static GeoidModel LoadGtx(string path)
    {
        var b = File.ReadAllBytes(path);
        double lat0 = BinaryPrimitives.ReadDoubleBigEndian(b), lon0 = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(8));
        double dlat = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(16)), dlon = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(24));
        int rows = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(32)), cols = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(36));
        var v = new float[rows * cols];
        for (int i = 0; i < v.Length; i++)
        {
            float f = BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(40 + i * 4));
            v[i] = f == -88.8888f ? float.NaN : f;
        }
        if (lon0 >= 180) lon0 -= 360;
        return new GeoidModel(Path.GetFileName(path), lon0, lat0, dlon, dlat, cols, rows, v);
    }

    public void WriteGtx(string path)
    {
        using var fs = File.Create(path);
        Span<byte> h = stackalloc byte[40];
        BinaryPrimitives.WriteDoubleBigEndian(h, South); BinaryPrimitives.WriteDoubleBigEndian(h[8..], West);
        BinaryPrimitives.WriteDoubleBigEndian(h[16..], LatInc); BinaryPrimitives.WriteDoubleBigEndian(h[24..], LonInc);
        BinaryPrimitives.WriteInt32BigEndian(h[32..], Rows); BinaryPrimitives.WriteInt32BigEndian(h[36..], Cols);
        fs.Write(h);
        Span<byte> f = stackalloc byte[4];
        foreach (var v in Values) { BinaryPrimitives.WriteSingleBigEndian(f, float.IsNaN(v) ? -88.8888f : v); fs.Write(f); }
    }

    // ================================================================== ISG

    /// <summary>
    /// ISG (International Service for the Geoid) ASCII grid, formats 1.0 and 2.0: header keys (lat min/max, lon min/max, delta lat/lon,
    /// nrows, ncols, nodata, coord type geodetic), then rows from north to south. DMS coordinates (units = dms) are supported.
    /// </summary>
    public static GeoidModel LoadIsg(string path)
    {
        var lines = File.ReadAllLines(path);
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        for (; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("end_of_head", StringComparison.OrdinalIgnoreCase)) { i++; break; }
            int eq = l.IndexOf('=');
            if (eq > 0) h[l[..eq].Trim()] = l[(eq + 1)..].Trim();
        }
        bool dms = h.TryGetValue("units", out var u) && u.Contains("dms", StringComparison.OrdinalIgnoreCase);
        double A(string key)
        {
            var s = h[key];
            if (!dms) return double.Parse(s, CultureInfo.InvariantCulture);
            var p = s.Split(['°', '\'', '"', ' ', ':'], StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            double sign = s.TrimStart().StartsWith('-') ? -1 : 1;
            return sign * (Math.Abs(p[0]) + (p.Length > 1 ? p[1] / 60 : 0) + (p.Length > 2 ? p[2] / 3600 : 0));
        }
        double latMin = A("lat min"), latMax = A("lat max"), lonMin = A("lon min"), lonMax = A("lon max");
        double dLat = A("delta lat"), dLon = A("delta lon");
        int rows = int.Parse(h["nrows"], CultureInfo.InvariantCulture), cols = int.Parse(h["ncols"], CultureInfo.InvariantCulture);
        double nodata = h.TryGetValue("nodata", out var nd) ? double.Parse(nd, CultureInfo.InvariantCulture) : -9999;
        // ISG 2.0 "node" grids have nodes on the bounds; "cell" grids have values at cell centres
        bool cell = h.TryGetValue("data ordering", out _) && h.TryGetValue("grid type", out var gtype) && gtype.Contains("cell", StringComparison.OrdinalIgnoreCase);
        double south = cell ? latMin + dLat / 2 : latMin, west = cell ? lonMin + dLon / 2 : lonMin;
        var v = new float[rows * cols];
        int k = 0;
        for (; i < lines.Length && k < v.Length; i++)
            foreach (var tok in lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (k >= v.Length) break;
                int rFromNorth = k / cols, c = k % cols;
                double val = double.Parse(tok, CultureInfo.InvariantCulture);
                v[(rows - 1 - rFromNorth) * cols + c] = val == nodata ? float.NaN : (float)val;
                k++;
            }
        if (k != v.Length) throw new InvalidDataException($"ISG grid has {k} values, expected {rows}×{cols}.");
        return new GeoidModel(h.TryGetValue("model name", out var mn) ? mn : Path.GetFileName(path), west, south, dLon, dLat, cols, rows, v);
    }

    /// <summary>Writes ISG 2.0 (node grid, decimal degrees).</summary>
    public void WriteIsg(string path, string modelName = "geoid")
    {
        using var w = new StreamWriter(path);
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine("begin_of_head ================================================");
        w.WriteLine($"model name     : {modelName}");
        w.WriteLine("model type     : gravimetric");
        w.WriteLine("units          : meters");
        w.WriteLine("reference      : GRS80");
        w.WriteLine("ISG format     : 2.0");
        w.WriteLine("data type      : geoid");
        w.WriteLine("data ordering  : N-to-S, W-to-E");
        w.WriteLine("grid type      : node");
        w.WriteLine("coord type     : geodetic");
        w.WriteLine("coord units    : deg");
        w.WriteLine(string.Create(inv, $"lat min = {South:R}"));
        w.WriteLine(string.Create(inv, $"lat max = {North:R}"));
        w.WriteLine(string.Create(inv, $"lon min = {West:R}"));
        w.WriteLine(string.Create(inv, $"lon max = {East:R}"));
        w.WriteLine(string.Create(inv, $"delta lat = {LatInc:R}"));
        w.WriteLine(string.Create(inv, $"delta lon = {LonInc:R}"));
        w.WriteLine($"nrows = {Rows}");
        w.WriteLine($"ncols = {Cols}");
        w.WriteLine("nodata = -9999.0000");
        w.WriteLine("end_of_head ==================================================");
        for (int r = Rows - 1; r >= 0; r--)
            w.WriteLine(string.Join(' ', Enumerable.Range(0, Cols).Select(c => Values[r * Cols + c] is var v && float.IsNaN(v) ? "-9999.0000" : v.ToString("F4", inv))));
    }

    /// <summary>Loads a grid by extension: .gtx, .isg / .gri (ISG). GeoTIFF geoids are loaded with <c>Geoids.LoadGeoTiff</c> in GeoPandas.Net.Raster.</summary>
    public static GeoidModel Load(string path)
    {
        if (!s_loaders.ContainsKey(".tif"))
            try { Type.GetType("GeoPandas.Net.Raster.Geoids, GeoPandas.Net.Raster")?.GetMethod("Register")?.Invoke(null, null); }
            catch (Exception) { /* Raster package not deployed */ }
        return LoadCore(path);
    }

    private static GeoidModel LoadCore(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gtx" => LoadGtx(path),
        ".isg" or ".gri" or ".txt" => LoadIsg(path),
        _ => s_loaders.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out var f) ? f(path)
            : throw new NotSupportedException($"Unsupported geoid grid format '{Path.GetExtension(path)}' (gtx, isg; GeoTIFF needs GeoPandas.Net.Raster).")
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string, GeoidModel>> s_loaders = new();

    /// <summary>Lets other packages add loaders (GeoPandas.Net.Raster registers ".tif").</summary>
    public static void RegisterLoader(string extension, Func<string, GeoidModel> loader) => s_loaders[extension.ToLowerInvariant()] = loader;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Geoid '{Name}' {Cols}×{Rows} @ {LonInc}°×{LatInc}°, lon [{West}, {East}] lat [{South}, {North}]");
}
