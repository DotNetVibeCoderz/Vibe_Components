using System.Collections.Concurrent;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>
/// EPSG registry: hand-verified definitions for the most used CRSs (WGS 84, Web/World Mercator, UTM, DGN95 / Indonesia TM-3,
/// Lambert-93, CONUS Albers, British National Grid…) backed by the full embedded EPSG dataset (<see cref="EpsgDatabase"/>, ~5 800 CRSs).
/// Custom CRSs can be registered with <see cref="Register"/>.
/// </summary>
public static class CrsRegistry
{
    private static readonly ConcurrentDictionary<int, CoordinateReferenceSystem> s_cache = new();
    private static readonly ConcurrentDictionary<int, Func<CoordinateReferenceSystem>> s_custom = new();

    private static readonly Datum Nad83 = new("North_American_Datum_1983", Ellipsoid.Grs80, [0, 0, 0, 0, 0, 0, 0]);
    private static readonly Datum Etrs89 = new("European_Terrestrial_Reference_System_1989", Ellipsoid.Grs80, [0, 0, 0, 0, 0, 0, 0]);
    private static readonly Datum Rgf93 = new("Reseau_Geodesique_Francais_1993", Ellipsoid.Grs80, [0, 0, 0, 0, 0, 0, 0]);
    private static readonly Datum Nad27 = new("North_American_Datum_1927", Ellipsoid.Clarke1866, [-8, 160, 176, 0, 0, 0, 0]);
    private static readonly Datum Ed50 = new("European_Datum_1950", Ellipsoid.International1924, [-87, -98, -121, 0, 0, 0, 0]);
    private static readonly Datum Dgn95 = new("Datum_Geodesi_Nasional_1995", Ellipsoid.Wgs84, [0, 0, 0, 0, 0, 0, 0]);
    private static readonly Datum Osgb36 = new("OSGB_1936", Ellipsoid.Airy1830, [446.448, -125.157, 542.06, 0.15, 0.247, 0.842, -20.489]);

    /// <summary>Registers (or overrides) an EPSG code with a factory.</summary>
    public static void Register(int code, Func<CoordinateReferenceSystem> factory)
    {
        s_custom[code] = factory;
        s_cache.TryRemove(code, out _);
    }

    public static bool IsKnown(int code) => TryGet(code, out _);

    public static CoordinateReferenceSystem Get(int code) =>
        TryGet(code, out var crs) ? crs! : throw new KeyNotFoundException($"EPSG:{code} is not in the built-in registry. Use CrsRegistry.Register or a PROJ/WKT definition.");

    public static bool TryGet(int code, out CoordinateReferenceSystem? crs)
    {
        if (s_cache.TryGetValue(code, out crs)) return true;
        crs = s_custom.TryGetValue(code, out var f) ? f() : Build(code);
        if (crs is null) return false;
        s_cache[code] = crs;
        return true;
    }

    /// <summary>EPSG codes known to the built-in registry (excluding the UTM ranges, which are generated).</summary>
    public static IReadOnlyList<int> WellKnownCodes { get; } =
        [4326, 4269, 4258, 4267, 4230, 4755, 4171, 4277, 3857, 3395, 2154, 5070, 27700,
         .. Enumerable.Range(23830, 16), .. Enumerable.Range(32601, 60), .. Enumerable.Range(32701, 60), .. Enumerable.Range(26901, 23)];

    private static CoordinateReferenceSystem? Build(int code)
    {
        switch (code)
        {
            case 4326: return Geographic("WGS 84", Datum.Wgs84, code);
            case 4269: return Geographic("NAD83", Nad83, code);
            case 4258: return Geographic("ETRS89", Etrs89, code);
            case 4171: return Geographic("RGF93", Rgf93, code);
            case 4267: return Geographic("NAD27", Nad27, code);
            case 4230: return Geographic("ED50", Ed50, code);
            case 4755: return Geographic("DGN95", Dgn95, code);
            case 4277: return Geographic("OSGB 1936", Osgb36, code);
            case 3857:
                return Projected("WGS 84 / Pseudo-Mercator", new Datum("WGS_1984", Ellipsoid.Wgs84),
                    new WebMercator(Ellipsoid.Sphere, new ProjectionParameters()), code);
            case 3395:
                return Projected("WGS 84 / World Mercator", Datum.Wgs84,
                    new Mercator(Ellipsoid.Wgs84, new ProjectionParameters()), code);
            case 2154:
                return Projected("RGF93 v1 / Lambert-93", Rgf93, new LambertConformalConic(Ellipsoid.Grs80, new ProjectionParameters
                {
                    Lat0 = 46.5, Lon0 = 3, Lat1 = 49, Lat2 = 44, FalseEasting = 700000, FalseNorthing = 6600000
                }), code);
            case 5070:
                return Projected("NAD83 / Conus Albers", Nad83, new AlbersEqualArea(Ellipsoid.Grs80, new ProjectionParameters
                {
                    Lat0 = 23, Lon0 = -96, Lat1 = 29.5, Lat2 = 45.5
                }), code);
            case 27700:
                return Projected("OSGB36 / British National Grid", Osgb36, new TransverseMercator(Ellipsoid.Airy1830, new ProjectionParameters
                {
                    Lat0 = 49, Lon0 = -2, K0 = 0.9996012717, FalseEasting = 400000, FalseNorthing = -100000
                }), code);
        }

        if (code is >= 32601 and <= 32660 or >= 32701 and <= 32760)
        {
            bool south = code > 32700;
            int zone = code % 100;
            return Projected($"WGS 84 / UTM zone {zone}{(south ? 'S' : 'N')}", Datum.Wgs84,
                new TransverseMercator(Ellipsoid.Wgs84, ProjectionParameters.Utm(zone, south)), code);
        }

        if (code is >= 26901 and <= 26923)
        {
            int zone = code - 26900;
            return Projected($"NAD83 / UTM zone {zone}N", Nad83,
                new TransverseMercator(Ellipsoid.Grs80, ProjectionParameters.Utm(zone, false)), code);
        }

        if (code is >= 23830 and <= 23845)
        {
            // DGN95 / Indonesia TM-3: 3° zones, k0 = 0.9999, FE = 200 000, FN = 1 500 000.
            int idx = code - 23830;
            double cm = 94.5 + 3 * idx;
            int major = 46 + (idx + 1) / 2;
            int minor = idx % 2 == 0 ? 2 : 1;
            return Projected($"DGN95 / Indonesia TM-3 zone {major}.{minor}", Dgn95, new TransverseMercator(Ellipsoid.Wgs84, new ProjectionParameters
            {
                Lon0 = cm, K0 = 0.9999, FalseEasting = 200000, FalseNorthing = 1500000
            }), code);
        }

        // everything else: the embedded EPSG dataset (~5 800 CRSs)
        return EpsgDatabase.Build(code);
    }

    /// <summary>Searches the EPSG dataset by name, datum or code words (e.g. "Indonesia TM-3", "GDA2020 MGA").</summary>
    public static IReadOnlyList<EpsgEntry> Search(string text, int max = 50) => EpsgDatabase.Search(text, max);

    /// <summary>Projected CRSs whose area of use contains a lon/lat point, most local first.</summary>
    public static IReadOnlyList<EpsgEntry> AtLocation(double lon, double lat, int max = 100) => EpsgDatabase.AtLocation(lon, lat, true, max);

    internal static CoordinateReferenceSystem Geographic(string name, Datum datum, int? code) =>
        new(name, CrsKind.Geographic, datum, null, code);

    internal static CoordinateReferenceSystem Projected(string name, Datum datum, Projection projection, int? code, double toMeter = 1.0, string? unit = null) =>
        new(name, CrsKind.Projected, datum, projection, code, toMeter, unit);
}
