using System.Numerics;

namespace OpenSceneGraph.Net.Geo;

/// <summary>Double precision vector for planet-scale coordinates (ECEF metres).</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vector3d Normalized() { double l = Length; return l > 0 ? this * (1 / l) : this; }
    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Converts to single precision relative to an origin (camera-relative rendering avoids float jitter).</summary>
    public Vector3 ToVector3(Vector3d origin = default) => new((float)(X - origin.X), (float)(Y - origin.Y), (float)(Z - origin.Z));
}

/// <summary>Geodetic position: latitude/longitude in degrees, height in metres above the ellipsoid.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude, double Height = 0)
{
    public override string ToString() =>
        $"{System.Math.Abs(Latitude):F6}°{(Latitude >= 0 ? 'N' : 'S')} {System.Math.Abs(Longitude):F6}°{(Longitude >= 0 ? 'E' : 'W')} {Height:F1} m";
}

/// <summary>
/// Reference ellipsoid and conversions (<c>osg::EllipsoidModel</c>): geodetic ↔ ECEF, local ENU frames,
/// geodesic distance and bearing.
/// </summary>
public sealed class Ellipsoid
{
    public static readonly Ellipsoid Wgs84 = new(6378137.0, 1 / 298.257223563);

    public Ellipsoid(double semiMajorAxis, double flattening)
    {
        A = semiMajorAxis;
        F = flattening;
        B = A * (1 - F);
        E2 = F * (2 - F);
        Ep2 = E2 / (1 - E2);
    }

    public double A { get; }
    public double B { get; }
    public double F { get; }
    public double E2 { get; }
    public double Ep2 { get; }

    private const double Deg = System.Math.PI / 180;

    public Vector3d ToEcef(GeoPoint p)
    {
        double lat = p.Latitude * Deg, lon = p.Longitude * Deg;
        double sl = System.Math.Sin(lat), cl = System.Math.Cos(lat);
        double n = A / System.Math.Sqrt(1 - E2 * sl * sl);
        return new Vector3d((n + p.Height) * cl * System.Math.Cos(lon), (n + p.Height) * cl * System.Math.Sin(lon), (n * (1 - E2) + p.Height) * sl);
    }

    /// <summary>ECEF to geodetic using Bowring's method refined by iteration (sub-millimetre accuracy).</summary>
    public GeoPoint FromEcef(Vector3d e)
    {
        double lon = System.Math.Atan2(e.Y, e.X);
        double p = System.Math.Sqrt(e.X * e.X + e.Y * e.Y);
        if (p < 1e-9) return new GeoPoint(e.Z >= 0 ? 90 : -90, 0, System.Math.Abs(e.Z) - B);
        double theta = System.Math.Atan2(e.Z * A, p * B);
        double st = System.Math.Sin(theta), ct = System.Math.Cos(theta);
        double lat = System.Math.Atan2(e.Z + Ep2 * B * st * st * st, p - E2 * A * ct * ct * ct);
        double h = 0;
        for (int i = 0; i < 3; i++)
        {
            double sl = System.Math.Sin(lat);
            double n = A / System.Math.Sqrt(1 - E2 * sl * sl);
            h = p / System.Math.Cos(lat) - n;
            lat = System.Math.Atan2(e.Z, p * (1 - E2 * n / (n + h)));
        }
        return new GeoPoint(lat / Deg, lon / Deg, h);
    }

    /// <summary>East, North, Up unit vectors at a geodetic position.</summary>
    public (Vector3d East, Vector3d North, Vector3d Up) EnuAxes(GeoPoint p)
    {
        double lat = p.Latitude * Deg, lon = p.Longitude * Deg;
        double sl = System.Math.Sin(lat), cl = System.Math.Cos(lat), so = System.Math.Sin(lon), co = System.Math.Cos(lon);
        return (new Vector3d(-so, co, 0), new Vector3d(-sl * co, -sl * so, cl), new Vector3d(cl * co, cl * so, sl));
    }

    /// <summary>
    /// Local tangent-plane matrix (ENU → ECEF) at <paramref name="p"/>, expressed relative to <paramref name="renderOrigin"/>
    /// so it fits in single precision (<c>computeLocalToWorldTransformFromLatLongHeight</c>).
    /// </summary>
    public Matrix4x4 LocalToWorld(GeoPoint p, Vector3d renderOrigin = default)
    {
        var (east, north, up) = EnuAxes(p);
        var t = ToEcef(p) - renderOrigin;
        return new Matrix4x4(
            (float)east.X, (float)east.Y, (float)east.Z, 0,
            (float)north.X, (float)north.Y, (float)north.Z, 0,
            (float)up.X, (float)up.Y, (float)up.Z, 0,
            (float)t.X, (float)t.Y, (float)t.Z, 1);
    }

    /// <summary>Converts a geodetic point to local ENU metres around <paramref name="origin"/>.</summary>
    public Vector3 ToEnu(GeoPoint point, GeoPoint origin)
    {
        var d = ToEcef(point) - ToEcef(origin);
        var (e, n, u) = EnuAxes(origin);
        return new Vector3((float)Vector3d.Dot(d, e), (float)Vector3d.Dot(d, n), (float)Vector3d.Dot(d, u));
    }

    public GeoPoint FromEnu(Vector3 enu, GeoPoint origin)
    {
        var (e, n, u) = EnuAxes(origin);
        return FromEcef(ToEcef(origin) + e * enu.X + n * enu.Y + u * enu.Z);
    }

    /// <summary>Great-circle distance (haversine on the mean radius), metres.</summary>
    public double Distance(GeoPoint a, GeoPoint b)
    {
        double r = (2 * A + B) / 3;
        double dLat = (b.Latitude - a.Latitude) * Deg, dLon = (b.Longitude - a.Longitude) * Deg;
        double h = System.Math.Sin(dLat / 2) * System.Math.Sin(dLat / 2) +
                   System.Math.Cos(a.Latitude * Deg) * System.Math.Cos(b.Latitude * Deg) * System.Math.Sin(dLon / 2) * System.Math.Sin(dLon / 2);
        return 2 * r * System.Math.Asin(System.Math.Min(1, System.Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from a to b, degrees clockwise from north.</summary>
    public static double Bearing(GeoPoint a, GeoPoint b)
    {
        double la1 = a.Latitude * Deg, la2 = b.Latitude * Deg, dLon = (b.Longitude - a.Longitude) * Deg;
        double y = System.Math.Sin(dLon) * System.Math.Cos(la2);
        double x = System.Math.Cos(la1) * System.Math.Sin(la2) - System.Math.Sin(la1) * System.Math.Cos(la2) * System.Math.Cos(dLon);
        return (System.Math.Atan2(y, x) / Deg + 360) % 360;
    }

    /// <summary>Destination point given start, bearing (degrees) and distance (metres) on a sphere of mean radius.</summary>
    public GeoPoint Destination(GeoPoint start, double bearingDeg, double distance)
    {
        double r = (2 * A + B) / 3, d = distance / r, br = bearingDeg * Deg;
        double la1 = start.Latitude * Deg, lo1 = start.Longitude * Deg;
        double la2 = System.Math.Asin(System.Math.Sin(la1) * System.Math.Cos(d) + System.Math.Cos(la1) * System.Math.Sin(d) * System.Math.Cos(br));
        double lo2 = lo1 + System.Math.Atan2(System.Math.Sin(br) * System.Math.Sin(d) * System.Math.Cos(la1), System.Math.Cos(d) - System.Math.Sin(la1) * System.Math.Sin(la2));
        return new GeoPoint(la2 / Deg, ((lo2 / Deg + 540) % 360) - 180, start.Height);
    }
}

/// <summary>Universal Transverse Mercator coordinate.</summary>
public readonly record struct UtmCoordinate(int Zone, bool North, double Easting, double Northing)
{
    public override string ToString() => $"{Zone}{(North ? 'N' : 'S')} {Easting:F1}E {Northing:F1}N";
}

/// <summary>UTM projection on WGS84 (Krüger series, accurate to millimetres within a zone).</summary>
public static class Utm
{
    private const double K0 = 0.9996;
    private const double Deg = System.Math.PI / 180;

    public static int ZoneFor(double latitude, double longitude)
    {
        int zone = (int)System.Math.Floor((longitude + 180) / 6) + 1;
        if (latitude >= 56 && latitude < 64 && longitude >= 3 && longitude < 12) zone = 32; // Norway
        if (latitude >= 72 && latitude < 84)
        {
            if (longitude >= 0 && longitude < 9) zone = 31;
            else if (longitude >= 9 && longitude < 21) zone = 33;
            else if (longitude >= 21 && longitude < 33) zone = 35;
            else if (longitude >= 33 && longitude < 42) zone = 37;
        }
        return System.Math.Clamp(zone, 1, 60);
    }

    public static char LatitudeBand(double latitude)
    {
        const string bands = "CDEFGHJKLMNPQRSTUVWX";
        int i = (int)System.Math.Floor((latitude + 80) / 8);
        return bands[System.Math.Clamp(i, 0, bands.Length - 1)];
    }

    public static UtmCoordinate FromGeo(GeoPoint p, int? forceZone = null)
    {
        var e = Ellipsoid.Wgs84;
        int zone = forceZone ?? ZoneFor(p.Latitude, p.Longitude);
        double lon0 = ((zone - 1) * 6 - 180 + 3) * Deg;
        double lat = p.Latitude * Deg, lon = p.Longitude * Deg;
        double n = e.F / (2 - e.F);
        double a = e.A / (1 + n) * (1 + n * n / 4 + n * n * n * n / 64);
        double[] alpha =
        [
            n / 2 - 2 * n * n / 3 + 5 * n * n * n / 16,
            13 * n * n / 48 - 3 * n * n * n / 5,
            61 * n * n * n / 240,
        ];
        double sinLat = System.Math.Sin(lat);
        double t = System.Math.Sinh(Atanh(sinLat) - 2 * System.Math.Sqrt(n) / (1 + n) * Atanh(2 * System.Math.Sqrt(n) / (1 + n) * sinLat));
        double dl = lon - lon0;
        double xi = System.Math.Atan2(t, System.Math.Cos(dl));
        double eta = Atanh(System.Math.Sin(dl) / System.Math.Sqrt(1 + t * t));
        double sumXi = xi, sumEta = eta;
        for (int j = 1; j <= 3; j++)
        {
            sumXi += alpha[j - 1] * System.Math.Sin(2 * j * xi) * System.Math.Cosh(2 * j * eta);
            sumEta += alpha[j - 1] * System.Math.Cos(2 * j * xi) * System.Math.Sinh(2 * j * eta);
        }
        double easting = 500000 + K0 * a * sumEta;
        double northing = K0 * a * sumXi;
        bool north = p.Latitude >= 0;
        if (!north) northing += 10000000;
        return new UtmCoordinate(zone, north, easting, northing);
    }

    public static GeoPoint ToGeo(UtmCoordinate u)
    {
        var e = Ellipsoid.Wgs84;
        double n = e.F / (2 - e.F);
        double a = e.A / (1 + n) * (1 + n * n / 4 + n * n * n * n / 64);
        double[] beta =
        [
            n / 2 - 2 * n * n / 3 + 37 * n * n * n / 96,
            n * n / 48 + n * n * n / 15,
            17 * n * n * n / 480,
        ];
        double[] delta =
        [
            2 * n - 2 * n * n / 3 - 2 * n * n * n,
            7 * n * n / 3 - 8 * n * n * n / 5,
            56 * n * n * n / 15,
        ];
        double xi = (u.North ? u.Northing : u.Northing - 10000000) / (K0 * a);
        double eta = (u.Easting - 500000) / (K0 * a);
        double xi1 = xi, eta1 = eta;
        for (int j = 1; j <= 3; j++)
        {
            xi1 -= beta[j - 1] * System.Math.Sin(2 * j * xi) * System.Math.Cosh(2 * j * eta);
            eta1 -= beta[j - 1] * System.Math.Cos(2 * j * xi) * System.Math.Sinh(2 * j * eta);
        }
        double chi = System.Math.Asin(System.Math.Sin(xi1) / System.Math.Cosh(eta1));
        double lat = chi;
        for (int j = 1; j <= 3; j++) lat += delta[j - 1] * System.Math.Sin(2 * j * chi);
        double lon0 = ((u.Zone - 1) * 6 - 180 + 3) * Deg;
        double lon = lon0 + System.Math.Atan2(System.Math.Sinh(eta1), System.Math.Cos(xi1));
        return new GeoPoint(lat / Deg, lon / Deg);
    }

    private static double Atanh(double x) => 0.5 * System.Math.Log((1 + x) / (1 - x));
}
