using System.Globalization;
using System.IO.Compression;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>A CRS entry of the embedded EPSG dataset (for search / listing).</summary>
public sealed record EpsgEntry(int Code, string Name, bool IsProjected, string Datum, int? MethodCode, (double West, double South, double East, double North)? AreaOfUse);

/// <summary>
/// The embedded EPSG Geodetic Parameter Dataset (generated from PROJ's proj.db by <c>tools/epsg/generate_epsg.py</c>):
/// ~5 800 geographic and projected CRSs with Greenwich prime meridians and supported methods, with a WGS 84 Helmert shift per datum.
/// EPSG dataset © IOGP, redistributed under the EPSG terms of use.
/// </summary>
public static class EpsgDatabase
{
    private static readonly Lazy<(string Version, Dictionary<int, string[]> Rows)> s_rows = new(Load);

    private static (string, Dictionary<int, string[]>) Load()
    {
        var asm = typeof(EpsgDatabase).Assembly;
        using var s = asm.GetManifestResourceStream("GeoPandas.Net.Geometry.epsg.tsv.gz")
            ?? throw new InvalidOperationException("Embedded EPSG dataset is missing.");
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        var rows = new Dictionary<int, string[]>(6000);
        string version = "?";
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.StartsWith('#')) { version = line.TrimStart('#', ' '); continue; }
            var f = line.Split('\t');
            if (f.Length >= 13 && int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) rows[code] = f;
        }
        return (version, rows);
    }

    /// <summary>Dataset version line, e.g. "EPSG v12.029 via PROJ proj.db".</summary>
    public static string Version => s_rows.Value.Version;
    public static int Count => s_rows.Value.Rows.Count;
    public static IEnumerable<int> Codes => s_rows.Value.Rows.Keys.Order();

    public static bool Contains(int code) => s_rows.Value.Rows.ContainsKey(code);

    public static EpsgEntry? Entry(int code) =>
        s_rows.Value.Rows.TryGetValue(code, out var f) ? ToEntry(code, f) : null;

    private static EpsgEntry ToEntry(int code, string[] f) =>
        new(code, f[2], f[1] == "P", f[3], f[8].Length > 0 ? int.Parse(f[8], CultureInfo.InvariantCulture) : null, Area(f[12]));

    /// <summary>Case-insensitive search over names, datums and codes (all words must match).</summary>
    public static IReadOnlyList<EpsgEntry> Search(string text, int max = 50)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return s_rows.Value.Rows
            .Where(kv => words.All(w => kv.Key.ToString(CultureInfo.InvariantCulture) == w
                                        || kv.Value[2].Contains(w, StringComparison.OrdinalIgnoreCase)
                                        || kv.Value[3].Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(kv => kv.Key)
            .Take(max)
            .Select(kv => ToEntry(kv.Key, kv.Value))
            .ToArray();
    }

    /// <summary>CRSs whose area of use contains a lon/lat point (e.g. "which national grids cover Jakarta?").</summary>
    public static IReadOnlyList<EpsgEntry> AtLocation(double lon, double lat, bool projectedOnly = true, int max = 100) =>
        s_rows.Value.Rows
            .Where(kv => (!projectedOnly || kv.Value[1] == "P") && Area(kv.Value[12]) is { } a && Inside(a, lon, lat))
            .Select(kv => ToEntry(kv.Key, kv.Value))
            .OrderBy(e => AreaSize(e.AreaOfUse!.Value))
            .Take(max)
            .ToArray();

    private static bool Inside((double W, double S, double E, double N) a, double lon, double lat) =>
        lat >= a.S && lat <= a.N && (a.W <= a.E ? lon >= a.W && lon <= a.E : lon >= a.W || lon <= a.E);

    private static double AreaSize((double W, double S, double E, double N) a) => ((a.E >= a.W ? a.E - a.W : a.E + 360 - a.W) * (a.N - a.S));

    private static (double, double, double, double)? Area(string s)
    {
        var p = s.Split(',');
        if (p.Length != 4 || p.Any(string.IsNullOrEmpty)) return null;
        double D(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
        return (D(0), D(1), D(2), D(3));
    }

    internal static CoordinateReferenceSystem? Build(int code)
    {
        if (!s_rows.Value.Rows.TryGetValue(code, out var f)) return null;
        double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        double a = D(f[5]), rf = D(f[6]);
        var ell = Known(new Ellipsoid(f[4], a, rf));
        double[]? towgs = f[7].Length == 0 ? null : f[7].Split(',').Select(D).ToArray();
        var datum = ell == Ellipsoid.Wgs84 && (towgs is null || towgs.All(v => v == 0)) && f[3].Contains("1984")
            ? Datum.Wgs84
            : new Datum(f[3].Replace(' ', '_'), ell, towgs);
        var area = Area(f[12]);
        if (f[1] == "G")
        {
            var g = CrsRegistry.Geographic(f[2], datum, code);
            g.AreaOfUse = area;
            return g;
        }

        int method = int.Parse(f[8], CultureInfo.InvariantCulture);
        var pars = new Dictionary<int, double>();
        foreach (var kv in f[9].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            pars[int.Parse(kv[..eq], CultureInfo.InvariantCulture)] = D(kv[(eq + 1)..]);
        }
        var projection = ProjectionFactory.FromEpsgMethod(method, ell, (c, def) => pars.TryGetValue(c, out var v) ? v : def);
        if (projection is null) return null;
        double toMeter = D(f[10]);
        var crs = CrsRegistry.Projected(f[2], datum, projection, code, toMeter, toMeter == 1 ? "metre" : f[11]);
        crs.AreaOfUse = area;
        return crs;
    }

    private static Ellipsoid Known(Ellipsoid e)
    {
        foreach (var k in new[] { Ellipsoid.Wgs84, Ellipsoid.Grs80, Ellipsoid.Clarke1866, Ellipsoid.Bessel1841, Ellipsoid.International1924, Ellipsoid.Airy1830, Ellipsoid.Krassowsky1940 })
            if (Math.Abs(k.A - e.A) < 1e-6 && Math.Abs(k.InverseFlattening - e.InverseFlattening) < 1e-9) return k;
        return e;
    }
}

/// <summary>Builds projections from EPSG method codes and EPSG parameter codes (shared by the EPSG dataset and PROJJSON).</summary>
internal static class ProjectionFactory
{
    public static Projection? FromEpsgMethod(int method, Ellipsoid e, Func<int, double, double> p)
    {
        double P(int c, double def = 0) => p(c, def);
        switch (method)
        {
            case 9807:
            {
                var pp = new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) };
                if (pp.K0 == 0.9996 && pp.Lat0 == 0 && pp.FalseEasting == 500000 && pp.FalseNorthing is 0 or 10000000
                    && Math.Abs((pp.Lon0 + 183) / 6 - Math.Round((pp.Lon0 + 183) / 6)) < 1e-9)
                    pp = pp with { UtmZone = (int)Math.Round((pp.Lon0 + 183) / 6), UtmSouth = pp.FalseNorthing == 10000000 };
                return new TransverseMercator(e, pp);
            }
            case 9808: return new TransverseMercatorSouthOrientated(e, new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 1024: return new WebMercator(new Ellipsoid(e.Name, e.A, 0), new ProjectionParameters { Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9804: return new Mercator(e, new ProjectionParameters { Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9805: return new Mercator(e, new ProjectionParameters { LatTs = P(8823), Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9801: return new LambertConformalConic(e, new ProjectionParameters { Lat0 = P(8801), Lat1 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9802: return new LambertConformalConic(e, new ProjectionParameters { Lat0 = P(8821), Lon0 = P(8822), Lat1 = P(8823), Lat2 = P(8824), FalseEasting = P(8826), FalseNorthing = P(8827) });
            case 9822: return new AlbersEqualArea(e, new ProjectionParameters { Lat0 = P(8821), Lon0 = P(8822), Lat1 = P(8823), Lat2 = P(8824), FalseEasting = P(8826), FalseNorthing = P(8827) });
            case 9820 or 1027: return new LambertAzimuthalEqualArea(method == 1027 ? new Ellipsoid(e.Name + " (authalic sphere)", e.A * Math.Sqrt(EllipsoidMath.Q(e, 1) / 2), 0) : e, new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9810: return new PolarStereographic(e, new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9829: return new PolarStereographic(e, new ProjectionParameters { Lat0 = P(8832) < 0 ? -90 : 90, LatTs = P(8832), Lon0 = P(8833), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9809: return new ObliqueStereographic(e, new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9812: return new HotineObliqueMercator(e, new ProjectionParameters { Lat0 = P(8811), Lon0 = P(8812), Azimuth = P(8813), RectifiedGridAngle = P(8814, P(8813)), K0 = P(8815, 1), FalseEasting = P(8806), FalseNorthing = P(8807), NoUOffset = true }, false);
            case 9815: return new HotineObliqueMercator(e, new ProjectionParameters { Lat0 = P(8811), Lon0 = P(8812), Azimuth = P(8813), RectifiedGridAngle = P(8814, P(8813)), K0 = P(8815, 1), FalseEasting = P(8816), FalseNorthing = P(8817) }, true);
            case 9806: return new CassiniSoldner(e, new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 1028 or 1029: return new EquidistantCylindrical(method == 1029 ? new Ellipsoid(e.Name, e.A, 0) : e, new ProjectionParameters { LatTs = P(8823), Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 9835 or 9834: return new CylindricalEqualArea(method == 9834 ? new Ellipsoid(e.Name, e.A, 0) : e, new ProjectionParameters { LatTs = P(8823), Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            case 1078: return new EqualEarth(e, new ProjectionParameters { Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) });
            default: return null;
        }
    }
}

/// <summary>Transverse Mercator (South Orientated), EPSG 9808 (southern Africa "Lo" grids): westing/southing axes.</summary>
public sealed class TransverseMercatorSouthOrientated : Projection
{
    private readonly TransverseMercator _tm;

    public TransverseMercatorSouthOrientated(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _tm = new TransverseMercator(e, p with { FalseEasting = 0, FalseNorthing = 0 });
    }

    public override string ProjName => "tmerc";
    public override string WktName => "Transverse_Mercator_South_Orientated";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        _tm.Forward(lon, lat, out var e, out var n);
        x = Parameters.FalseEasting - e;
        y = Parameters.FalseNorthing - n;
    }

    public override void Inverse(double x, double y, out double lon, out double lat) =>
        _tm.Inverse(Parameters.FalseEasting - x, Parameters.FalseNorthing - y, out lon, out lat);

    internal override string Proj4Body => Inv($"+proj=tmerc +axis=wsu +lat_0={Parameters.Lat0:R} +lon_0={Parameters.Lon0:R} +k={Parameters.K0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Transverse Mercator (South Orientated)", 9808,
        [new("Latitude of natural origin", 8801, Parameters.Lat0, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree),
         new("Scale factor at natural origin", 8805, Parameters.K0, ParamUnit.Unity), FE(), FN()],
        [("latitude_of_origin", Parameters.Lat0), ("central_meridian", Parameters.Lon0), ("scale_factor", Parameters.K0)]);
}
