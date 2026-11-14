using Electrical.Core.Network;

namespace Electrical.GIS;

/// <summary>Spherical-earth helpers (WGS-84 coordinates in degrees, distances in km).</summary>
public static class Geo
{
    /// <summary>Mean earth radius [km] (IUGG).</summary>
    public const double EarthRadiusKm = 6371.0088;

    /// <summary>Great-circle distance [km] (haversine).</summary>
    public static double HaversineKm(GeoPoint a, GeoPoint b) => a.DistanceKm(b);

    /// <summary>Length [km] of a polyline (sum of haversine segment lengths).</summary>
    public static double PathLengthKm(IReadOnlyList<GeoPoint> path)
    {
        double s = 0;
        for (int i = 1; i < path.Count; i++) s += path[i - 1].DistanceKm(path[i]);
        return s;
    }

    /// <summary>Initial bearing from <paramref name="a"/> to <paramref name="b"/> [degrees clockwise from north, 0–360).</summary>
    public static double BearingDeg(GeoPoint a, GeoPoint b)
    {
        double f1 = a.Latitude * Math.PI / 180, f2 = b.Latitude * Math.PI / 180, dl = (b.Longitude - a.Longitude) * Math.PI / 180;
        double y = Math.Sin(dl) * Math.Cos(f2), x = Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Point reached from <paramref name="origin"/> after moving east/north by the given offsets [km] (local tangent-plane approximation).</summary>
    public static GeoPoint Offset(GeoPoint origin, double eastKm, double northKm)
    {
        double degPerKm = 180 / (Math.PI * EarthRadiusKm);
        double lat = origin.Latitude + northKm * degPerKm;
        double lon = origin.Longitude + eastKm * degPerKm / Math.Cos(origin.Latitude * Math.PI / 180);
        return new GeoPoint(lat, lon);
    }

    /// <summary>East/north offsets [km] of <paramref name="p"/> relative to <paramref name="origin"/> (inverse of <see cref="Offset"/>).</summary>
    public static (double EastKm, double NorthKm) LocalKm(GeoPoint origin, GeoPoint p)
    {
        double kmPerDeg = Math.PI * EarthRadiusKm / 180;
        return ((p.Longitude - origin.Longitude) * kmPerDeg * Math.Cos(origin.Latitude * Math.PI / 180), (p.Latitude - origin.Latitude) * kmPerDeg);
    }
}

/// <summary>Spherical Web Mercator (EPSG:3857) projection used by slippy-map tiles.</summary>
public static class WebMercator
{
    /// <summary>Sphere radius [m] (WGS-84 semi-major axis).</summary>
    public const double Radius = 6378137.0;
    /// <summary>Latitude limit [degrees] of the square Web Mercator world.</summary>
    public const double MaxLatitude = 85.05112877980659;

    /// <summary>Projects to EPSG:3857 metres (x east, y north).</summary>
    public static (double X, double Y) Project(GeoPoint p)
    {
        double lat = Math.Clamp(p.Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        return (Radius * p.Longitude * Math.PI / 180, Radius * Math.Log(Math.Tan(Math.PI / 4 + lat / 2)));
    }

    /// <summary>Inverse projection from EPSG:3857 metres to WGS-84 degrees.</summary>
    public static GeoPoint Unproject(double x, double y) =>
        new((2 * Math.Atan(Math.Exp(y / Radius)) - Math.PI / 2) * 180 / Math.PI, x / Radius * 180 / Math.PI);

    /// <summary>Global pixel coordinates at a zoom level (origin top-left, <paramref name="tileSize"/> px tiles).</summary>
    public static (double X, double Y) ToPixel(GeoPoint p, double zoom, int tileSize = 256)
    {
        double world = tileSize * Math.Pow(2, zoom);
        var (x, y) = Project(p);
        double c = Math.PI * Radius;
        return ((x + c) / (2 * c) * world, (c - y) / (2 * c) * world);
    }

    /// <summary>Inverse of <see cref="ToPixel"/>.</summary>
    public static GeoPoint FromPixel(double px, double py, double zoom, int tileSize = 256)
    {
        double world = tileSize * Math.Pow(2, zoom), c = Math.PI * Radius;
        return Unproject(px / world * 2 * c - c, c - py / world * 2 * c);
    }

    /// <summary>Slippy-map tile (x, y) containing a point at an integer zoom.</summary>
    public static (int X, int Y) Tile(GeoPoint p, int zoom)
    {
        var (x, y) = ToPixel(p, zoom, 1);
        int n = 1 << zoom;
        return (Math.Clamp((int)Math.Floor(x), 0, n - 1), Math.Clamp((int)Math.Floor(y), 0, n - 1));
    }

    /// <summary>Ground resolution [m/pixel] at a latitude and zoom.</summary>
    public static double MetersPerPixel(double latitude, double zoom, int tileSize = 256) =>
        Math.Cos(latitude * Math.PI / 180) * 2 * Math.PI * Radius / (tileSize * Math.Pow(2, zoom));
}

/// <summary>Geographic bounding box [degrees].</summary>
public readonly record struct GeoBounds(double South, double West, double North, double East)
{
    /// <summary>True when no point has been included (South &gt; North).</summary>
    public bool IsEmpty => South > North || West > East;

    /// <summary>Box center.</summary>
    public GeoPoint Center => new((South + North) / 2, (West + East) / 2);

    /// <summary>East–west extent [km] at the box center latitude.</summary>
    public double WidthKm => new GeoPoint(Center.Latitude, West).DistanceKm(new GeoPoint(Center.Latitude, East));

    /// <summary>North–south extent [km].</summary>
    public double HeightKm => new GeoPoint(South, Center.Longitude).DistanceKm(new GeoPoint(North, Center.Longitude));

    /// <summary>An empty box (union identity).</summary>
    public static GeoBounds Empty => new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    /// <summary>Smallest box containing all points (empty when there are none).</summary>
    public static GeoBounds Of(IEnumerable<GeoPoint> points)
    {
        var b = Empty;
        foreach (var p in points) b = b.Include(p);
        return b;
    }

    /// <summary>Bounds of all located buses of a network.</summary>
    public static GeoBounds Of(PowerNetwork net) => Of(net.Buses.Where(b => b.Location is not null).Select(b => b.Location!.Value));

    /// <summary>Box extended to contain a point.</summary>
    public GeoBounds Include(GeoPoint p) =>
        new(Math.Min(South, p.Latitude), Math.Min(West, p.Longitude), Math.Max(North, p.Latitude), Math.Max(East, p.Longitude));

    /// <summary>Union of two boxes.</summary>
    public GeoBounds Union(GeoBounds o) => o.IsEmpty ? this : IsEmpty ? o :
        new(Math.Min(South, o.South), Math.Min(West, o.West), Math.Max(North, o.North), Math.Max(East, o.East));

    /// <summary>True when the point lies inside (inclusive).</summary>
    public bool Contains(GeoPoint p) => p.Latitude >= South && p.Latitude <= North && p.Longitude >= West && p.Longitude <= East;

    /// <summary>True when the boxes overlap.</summary>
    public bool Intersects(GeoBounds o) => !IsEmpty && !o.IsEmpty && o.West <= East && o.East >= West && o.South <= North && o.North >= South;

    /// <summary>Box grown by a margin [km] on every side.</summary>
    public GeoBounds Expand(double km)
    {
        double degPerKm = 180 / (Math.PI * Geo.EarthRadiusKm);
        double dLon = km * degPerKm / Math.Max(1e-6, Math.Cos(Center.Latitude * Math.PI / 180));
        return new(South - km * degPerKm, West - dLon, North + km * degPerKm, East + dLon);
    }

    /// <summary>GeoJSON bbox order: [west, south, east, north].</summary>
    public double[] ToGeoJsonBBox() => [West, South, East, North];
}

/// <summary>Line-length assignment from coordinates.</summary>
public static class LineLengths
{
    /// <summary>
    /// Sets <see cref="Branch.LengthKm"/> of non-transformer branches from their geometry: the routed path in <paramref name="paths"/>
    /// (keyed by branch index) when available, otherwise the straight haversine distance between the terminal buses multiplied by
    /// <paramref name="routeFactor"/> (≥ 1 accounts for routing detours). Returns the number of branches updated.
    /// </summary>
    public static int Assign(PowerNetwork net, IReadOnlyDictionary<int, GeoPoint[]>? paths = null, bool overwrite = true, double routeFactor = 1.0)
    {
        int n = 0;
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            if (br.IsTransformer || (!overwrite && br.LengthKm > 0)) continue;
            double? len = null;
            if (paths is not null && paths.TryGetValue(k, out var path) && path.Length >= 2) len = Geo.PathLengthKm(path);
            else if (net.GetBus(br.From).Location is GeoPoint a && net.GetBus(br.To).Location is GeoPoint b) len = a.DistanceKm(b) * routeFactor;
            if (len is double l) { br.LengthKm = Math.Round(l, 6); n++; }
        }
        return n;
    }

    /// <summary>Straight-line length [km] of a branch, or null when a terminal bus has no location.</summary>
    public static double? StraightLengthKm(PowerNetwork net, Branch br) =>
        net.GetBus(br.From).Location is GeoPoint a && net.GetBus(br.To).Location is GeoPoint b ? a.DistanceKm(b) : null;
}
