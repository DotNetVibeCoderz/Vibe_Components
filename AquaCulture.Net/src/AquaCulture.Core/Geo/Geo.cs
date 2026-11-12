namespace AquaCulture.Core.Geo;

/// <summary>WGS-84 coordinate in decimal degrees, with optional elevation (m).</summary>
public readonly record struct GeoCoordinate(double Latitude, double Longitude, double Elevation = 0)
{
    public override string ToString() => $"{Latitude:0.00000}, {Longitude:0.00000}";
}

/// <summary>Local east/north offset in metres.</summary>
public readonly record struct LocalPoint(double East, double North);

/// <summary>Axis-aligned latitude/longitude bounds.</summary>
public readonly record struct GeoBounds(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public GeoCoordinate Center => new((MinLat + MaxLat) / 2, (MinLon + MaxLon) / 2);
    public bool Contains(GeoCoordinate c) => c.Latitude >= MinLat && c.Latitude <= MaxLat && c.Longitude >= MinLon && c.Longitude <= MaxLon;
    public GeoBounds Expand(double degrees) => new(MinLat - degrees, MinLon - degrees, MaxLat + degrees, MaxLon + degrees);
}

/// <summary>Geodesy helpers on a spherical Earth (accurate to ~0.3 % for farm-scale distances).</summary>
public static class GeoMath
{
    public const double EarthRadiusM = 6_371_008.8;
    private const double Deg = Math.PI / 180;

    /// <summary>Great-circle distance in metres.</summary>
    public static double Haversine(GeoCoordinate a, GeoCoordinate b)
    {
        double dLat = (b.Latitude - a.Latitude) * Deg, dLon = (b.Longitude - a.Longitude) * Deg;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(a.Latitude * Deg) * Math.Cos(b.Latitude * Deg) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing (degrees from north).</summary>
    public static double Bearing(GeoCoordinate a, GeoCoordinate b)
    {
        double φ1 = a.Latitude * Deg, φ2 = b.Latitude * Deg, Δλ = (b.Longitude - a.Longitude) * Deg;
        double y = Math.Sin(Δλ) * Math.Cos(φ2), x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        return (Math.Atan2(y, x) / Deg + 360) % 360;
    }

    /// <summary>Equirectangular projection around an origin (metres east/north).</summary>
    public static LocalPoint ToLocal(GeoCoordinate origin, GeoCoordinate p) =>
        new((p.Longitude - origin.Longitude) * Deg * EarthRadiusM * Math.Cos(origin.Latitude * Deg), (p.Latitude - origin.Latitude) * Deg * EarthRadiusM);

    public static GeoCoordinate FromLocal(GeoCoordinate origin, LocalPoint p) =>
        new(origin.Latitude + p.North / EarthRadiusM / Deg, origin.Longitude + p.East / (EarthRadiusM * Math.Cos(origin.Latitude * Deg)) / Deg, origin.Elevation);

    /// <summary>Destination point given distance (m) and bearing (degrees).</summary>
    public static GeoCoordinate Destination(GeoCoordinate from, double distanceM, double bearingDeg)
    {
        double δ = distanceM / EarthRadiusM, θ = bearingDeg * Deg, φ1 = from.Latitude * Deg, λ1 = from.Longitude * Deg;
        double φ2 = Math.Asin(Math.Sin(φ1) * Math.Cos(δ) + Math.Cos(φ1) * Math.Sin(δ) * Math.Cos(θ));
        double λ2 = λ1 + Math.Atan2(Math.Sin(θ) * Math.Sin(δ) * Math.Cos(φ1), Math.Cos(δ) - Math.Sin(φ1) * Math.Sin(φ2));
        return new(φ2 / Deg, (λ2 / Deg + 540) % 360 - 180, from.Elevation);
    }
}

/// <summary>Simple polygon (one ring, WGS-84). Area and perimeter use a local equal-distance projection.</summary>
public sealed class GeoPolygon
{
    public GeoPolygon(IReadOnlyList<GeoCoordinate> ring)
    {
        if (ring.Count < 3) throw new ArgumentException("A polygon needs at least three vertices.", nameof(ring));
        Ring = ring[0] == ring[^1] ? ring.Take(ring.Count - 1).ToArray() : ring.ToArray();
    }

    public IReadOnlyList<GeoCoordinate> Ring { get; }

    public GeoBounds Bounds => new(Ring.Min(c => c.Latitude), Ring.Min(c => c.Longitude), Ring.Max(c => c.Latitude), Ring.Max(c => c.Longitude));

    public GeoCoordinate Centroid
    {
        get
        {
            var o = Bounds.Center;
            var pts = Ring.Select(p => GeoMath.ToLocal(o, p)).ToArray();
            double a = 0, cx = 0, cy = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Length];
                double cross = p.East * q.North - q.East * p.North;
                a += cross; cx += (p.East + q.East) * cross; cy += (p.North + q.North) * cross;
            }
            a /= 2;
            return a == 0 ? o : GeoMath.FromLocal(o, new LocalPoint(cx / (6 * a), cy / (6 * a)));
        }
    }

    /// <summary>Area in square metres.</summary>
    public double AreaM2
    {
        get
        {
            var o = Bounds.Center;
            var pts = Ring.Select(p => GeoMath.ToLocal(o, p)).ToArray();
            double s = 0;
            for (int i = 0; i < pts.Length; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Length]; s += p.East * q.North - q.East * p.North; }
            return Math.Abs(s) / 2;
        }
    }

    public double PerimeterM
    {
        get { double s = 0; for (int i = 0; i < Ring.Count; i++) s += GeoMath.Haversine(Ring[i], Ring[(i + 1) % Ring.Count]); return s; }
    }

    /// <summary>Ray-casting point-in-polygon test.</summary>
    public bool Contains(GeoCoordinate p)
    {
        bool inside = false;
        for (int i = 0, j = Ring.Count - 1; i < Ring.Count; j = i++)
        {
            var a = Ring[i]; var b = Ring[j];
            if ((a.Latitude > p.Latitude) != (b.Latitude > p.Latitude) &&
                p.Longitude < (b.Longitude - a.Longitude) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + a.Longitude)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Rectangle centred on a point, rotated by <paramref name="rotationDeg"/> clockwise from north.</summary>
    public static GeoPolygon Rectangle(GeoCoordinate center, double widthM, double lengthM, double rotationDeg = 0)
    {
        double r = rotationDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        LocalPoint Rot(double e, double n) => new(e * c + n * s, -e * s + n * c);
        double w = widthM / 2, l = lengthM / 2;
        return new([GeoMath.FromLocal(center, Rot(-w, -l)), GeoMath.FromLocal(center, Rot(w, -l)), GeoMath.FromLocal(center, Rot(w, l)), GeoMath.FromLocal(center, Rot(-w, l))]);
    }

    /// <summary>Regular polygon approximating a circle (e.g. a round HDPE cage or a circular RAS tank).</summary>
    public static GeoPolygon Circle(GeoCoordinate center, double radiusM, int segments = 32) =>
        new(Enumerable.Range(0, segments).Select(i => GeoMath.FromLocal(center, new LocalPoint(radiusM * Math.Sin(2 * Math.PI * i / segments), radiusM * Math.Cos(2 * Math.PI * i / segments)))).ToArray());
}
