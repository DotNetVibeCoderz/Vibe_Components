namespace GeoPandas.Net.Geometry.Crs;

/// <summary>Map projection: geodetic (radians) ⇄ projected (metres). Implementations are immutable and thread-safe.</summary>
public abstract class Projection
{
    protected const double HalfPi = Math.PI / 2;
    protected const double Deg = Math.PI / 180.0;

    protected Projection(Ellipsoid ellipsoid, ProjectionParameters p)
    {
        Ellipsoid = ellipsoid;
        Parameters = p;
    }

    public Ellipsoid Ellipsoid { get; }
    public ProjectionParameters Parameters { get; }

    /// <summary>Short PROJ name (tmerc, merc, lcc, aea, webmerc).</summary>
    public abstract string ProjName { get; }
    /// <summary>OGC WKT1 projection name.</summary>
    public abstract string WktName { get; }

    /// <summary>Forward projection. <paramref name="lon"/>/<paramref name="lat"/> in radians; output metres (before unit scaling).</summary>
    public abstract void Forward(double lon, double lat, out double x, out double y);
    /// <summary>Inverse projection. Input metres; output radians.</summary>
    public abstract void Inverse(double x, double y, out double lon, out double lat);

    /// <summary>Normalizes a longitude difference to [-π, π].</summary>
    protected static double AdjustLon(double lon)
    {
        if (Math.Abs(lon) <= Math.PI) return lon;
        return lon - 2 * Math.PI * Math.Floor((lon + Math.PI) / (2 * Math.PI));
    }

    // Snyder's t(φ) and m(φ) helpers shared by conformal conics / Mercator.
    protected static double Tsfn(double phi, double e)
    {
        double s = e * Math.Sin(phi);
        return Math.Tan(Math.PI / 4 - phi / 2) / Math.Pow((1 - s) / (1 + s), e / 2);
    }

    protected static double Msfn(double phi, double e2)
    {
        double s = Math.Sin(phi);
        return Math.Cos(phi) / Math.Sqrt(1 - e2 * s * s);
    }

    protected static double PhiFromTs(double ts, double e)
    {
        double phi = HalfPi - 2 * Math.Atan(ts);
        for (int i = 0; i < 15; i++)
        {
            double s = e * Math.Sin(phi);
            double next = HalfPi - 2 * Math.Atan(ts * Math.Pow((1 - s) / (1 + s), e / 2));
            if (Math.Abs(next - phi) < 1e-12) return next;
            phi = next;
        }
        return phi;
    }

    public static Projection Create(string projName, Ellipsoid e, ProjectionParameters p) => projName switch
    {
        "tmerc" or "utm" => new TransverseMercator(e, p),
        "merc" => e.IsSphere ? new WebMercator(e, p) : new Mercator(e, p),
        "webmerc" => new WebMercator(e, p),
        "lcc" => new LambertConformalConic(e, p),
        "aea" => new AlbersEqualArea(e, p),
        _ => throw new NotSupportedException($"Projection '{projName}' is not supported yet. Supported: tmerc/utm, merc, webmerc, lcc, aea.")
    };
}

/// <summary>Projection parameters in degrees (angles) and metres (offsets).</summary>
public sealed record ProjectionParameters
{
    public double Lat0 { get; init; }
    public double Lon0 { get; init; }
    public double K0 { get; init; } = 1.0;
    public double FalseEasting { get; init; }
    public double FalseNorthing { get; init; }
    public double Lat1 { get; init; } = double.NaN;
    public double Lat2 { get; init; } = double.NaN;
    /// <summary>Latitude of true scale (Mercator <c>lat_ts</c>).</summary>
    public double LatTs { get; init; } = double.NaN;
    /// <summary>UTM zone (informational, for PROJ string output).</summary>
    public int? UtmZone { get; init; }
    public bool UtmSouth { get; init; }

    public static ProjectionParameters Utm(int zone, bool south) => new()
    {
        Lon0 = zone * 6 - 183,
        K0 = 0.9996,
        FalseEasting = 500000,
        FalseNorthing = south ? 10000000 : 0,
        UtmZone = zone,
        UtmSouth = south
    };
}

/// <summary>Transverse Mercator (Snyder, USGS PP-1395) — accurate to millimetres within ±4° of the central meridian; used for UTM and national grids.</summary>
public sealed class TransverseMercator : Projection
{
    private readonly double _a, _e2, _ep2, _k0, _lon0, _m0, _e1;
    private readonly double _m1, _m2, _m3, _m4;

    public TransverseMercator(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _a = e.A; _e2 = e.E2; _ep2 = _e2 / (1 - _e2); _k0 = p.K0; _lon0 = p.Lon0 * Deg;
        double e4 = _e2 * _e2, e6 = e4 * _e2;
        _m1 = 1 - _e2 / 4 - 3 * e4 / 64 - 5 * e6 / 256;
        _m2 = 3 * _e2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024;
        _m3 = 15 * e4 / 256 + 45 * e6 / 1024;
        _m4 = 35 * e6 / 3072;
        _m0 = Meridian(p.Lat0 * Deg);
        double s = Math.Sqrt(1 - _e2);
        _e1 = (1 - s) / (1 + s);
    }

    public override string ProjName => Parameters.UtmZone is not null ? "utm" : "tmerc";
    public override string WktName => "Transverse_Mercator";

    private double Meridian(double phi) =>
        _a * (_m1 * phi - _m2 * Math.Sin(2 * phi) + _m3 * Math.Sin(4 * phi) - _m4 * Math.Sin(6 * phi));

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double sinP = Math.Sin(lat), cosP = Math.Cos(lat), tanP = Math.Tan(lat);
        double n = _a / Math.Sqrt(1 - _e2 * sinP * sinP);
        double t = tanP * tanP;
        double c = _ep2 * cosP * cosP;
        double a = AdjustLon(lon - _lon0) * cosP;
        double a2 = a * a, a3 = a2 * a, a4 = a3 * a, a5 = a4 * a, a6 = a5 * a;
        double m = Meridian(lat);

        x = Parameters.FalseEasting + _k0 * n * (a + (1 - t + c) * a3 / 6 + (5 - 18 * t + t * t + 72 * c - 58 * _ep2) * a5 / 120);
        y = Parameters.FalseNorthing + _k0 * (m - _m0 + n * tanP * (a2 / 2 + (5 - t + 9 * c + 4 * c * c) * a4 / 24
                                                                    + (61 - 58 * t + t * t + 600 * c - 330 * _ep2) * a6 / 720));
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double m = _m0 + (y - Parameters.FalseNorthing) / _k0;
        double mu = m / (_a * _m1);
        double e1 = _e1, e12 = e1 * e1, e13 = e12 * e1, e14 = e13 * e1;
        double phi1 = mu + (3 * e1 / 2 - 27 * e13 / 32) * Math.Sin(2 * mu)
                         + (21 * e12 / 16 - 55 * e14 / 32) * Math.Sin(4 * mu)
                         + (151 * e13 / 96) * Math.Sin(6 * mu)
                         + (1097 * e14 / 512) * Math.Sin(8 * mu);

        double sin1 = Math.Sin(phi1), cos1 = Math.Cos(phi1), tan1 = Math.Tan(phi1);
        double c1 = _ep2 * cos1 * cos1;
        double t1 = tan1 * tan1;
        double denom = 1 - _e2 * sin1 * sin1;
        double n1 = _a / Math.Sqrt(denom);
        double r1 = _a * (1 - _e2) / Math.Pow(denom, 1.5);
        double d = (x - Parameters.FalseEasting) / (n1 * _k0);
        double d2 = d * d, d3 = d2 * d, d4 = d3 * d, d5 = d4 * d, d6 = d5 * d;

        lat = phi1 - (n1 * tan1 / r1) * (d2 / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * _ep2) * d4 / 24
                                       + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * _ep2 - 3 * c1 * c1) * d6 / 720);
        lon = _lon0 + (d - (1 + 2 * t1 + c1) * d3 / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * _ep2 + 24 * t1 * t1) * d5 / 120) / cos1;
    }
}

/// <summary>Ellipsoidal Mercator (EPSG:3395 style), with optional latitude of true scale.</summary>
public sealed class Mercator : Projection
{
    private readonly double _a, _e, _k0, _lon0;

    public Mercator(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _a = e.A; _e = e.E; _lon0 = p.Lon0 * Deg;
        _k0 = double.IsNaN(p.LatTs) ? p.K0 : Msfn(p.LatTs * Deg, e.E2);
    }

    public override string ProjName => "merc";
    public override string WktName => "Mercator_1SP";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        lat = Math.Clamp(lat, -89.5 * Deg, 89.5 * Deg);
        x = Parameters.FalseEasting + _a * _k0 * AdjustLon(lon - _lon0);
        y = Parameters.FalseNorthing - _a * _k0 * Math.Log(Tsfn(lat, _e));
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double ts = Math.Exp(-(y - Parameters.FalseNorthing) / (_a * _k0));
        lat = PhiFromTs(ts, _e);
        lon = (x - Parameters.FalseEasting) / (_a * _k0) + _lon0;
    }
}

/// <summary>Spherical "Web Mercator" / Pseudo-Mercator (EPSG:3857).</summary>
public sealed class WebMercator : Projection
{
    private const double MaxLat = 85.06 * Math.PI / 180;
    private readonly double _r, _lon0;

    public WebMercator(Ellipsoid e, ProjectionParameters p) : base(e, p) { _r = e.A; _lon0 = p.Lon0 * Deg; }

    public override string ProjName => "webmerc";
    public override string WktName => "Mercator_Auxiliary_Sphere";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        lat = Math.Clamp(lat, -MaxLat, MaxLat);
        x = Parameters.FalseEasting + _r * AdjustLon(lon - _lon0);
        y = Parameters.FalseNorthing + _r * Math.Log(Math.Tan(Math.PI / 4 + lat / 2));
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        lon = (x - Parameters.FalseEasting) / _r + _lon0;
        lat = HalfPi - 2 * Math.Atan(Math.Exp(-(y - Parameters.FalseNorthing) / _r));
    }
}

/// <summary>Lambert Conformal Conic (1SP when Lat2 is NaN/equal to Lat1, otherwise 2SP).</summary>
public sealed class LambertConformalConic : Projection
{
    private readonly double _a, _e, _n, _f, _rho0, _lon0, _k0;

    public LambertConformalConic(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _a = e.A; _e = e.E; _lon0 = p.Lon0 * Deg; _k0 = p.K0;
        double phi1 = (double.IsNaN(p.Lat1) ? p.Lat0 : p.Lat1) * Deg;
        double phi2 = (double.IsNaN(p.Lat2) ? phi1 / Deg : p.Lat2) * Deg;
        double m1 = Msfn(phi1, e.E2), t1 = Tsfn(phi1, _e);
        double t0 = Tsfn(p.Lat0 * Deg, _e);
        _n = Math.Abs(phi1 - phi2) < 1e-10
            ? Math.Sin(phi1)
            : (Math.Log(m1) - Math.Log(Msfn(phi2, e.E2))) / (Math.Log(t1) - Math.Log(Tsfn(phi2, _e)));
        _f = m1 / (_n * Math.Pow(t1, _n));
        _rho0 = _a * _k0 * _f * Math.Pow(t0, _n);
    }

    public override string ProjName => "lcc";
    public override string WktName => double.IsNaN(Parameters.Lat2) ? "Lambert_Conformal_Conic_1SP" : "Lambert_Conformal_Conic_2SP";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double rho = Math.Abs(Math.Abs(lat) - HalfPi) < 1e-12 ? 0 : _a * _k0 * _f * Math.Pow(Tsfn(lat, _e), _n);
        double theta = _n * AdjustLon(lon - _lon0);
        x = Parameters.FalseEasting + rho * Math.Sin(theta);
        y = Parameters.FalseNorthing + _rho0 - rho * Math.Cos(theta);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double dx = x - Parameters.FalseEasting;
        double dy = _rho0 - (y - Parameters.FalseNorthing);
        double sign = Math.Sign(_n);
        double rho = sign * Math.Sqrt(dx * dx + dy * dy);
        double theta = Math.Atan2(sign * dx, sign * dy);
        if (rho == 0) { lat = sign * HalfPi; lon = _lon0; return; }
        double ts = Math.Pow(rho / (_a * _k0 * _f), 1 / _n);
        lat = PhiFromTs(ts, _e);
        lon = theta / _n + _lon0;
    }
}

/// <summary>Albers Equal-Area Conic (e.g. EPSG:5070 CONUS Albers).</summary>
public sealed class AlbersEqualArea : Projection
{
    private readonly double _a, _e, _e2, _n, _c, _rho0, _lon0;

    public AlbersEqualArea(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _a = e.A; _e = e.E; _e2 = e.E2; _lon0 = p.Lon0 * Deg;
        double phi1 = p.Lat1 * Deg, phi2 = (double.IsNaN(p.Lat2) ? p.Lat1 : p.Lat2) * Deg;
        double m1 = Msfn(phi1, _e2), m2 = Msfn(phi2, _e2);
        double q1 = Q(phi1), q2 = Q(phi2), q0 = Q(p.Lat0 * Deg);
        _n = Math.Abs(phi1 - phi2) < 1e-10 ? Math.Sin(phi1) : (m1 * m1 - m2 * m2) / (q2 - q1);
        _c = m1 * m1 + _n * q1;
        _rho0 = _a * Math.Sqrt(_c - _n * q0) / _n;
    }

    public override string ProjName => "aea";
    public override string WktName => "Albers_Conic_Equal_Area";

    private double Q(double phi)
    {
        double s = Math.Sin(phi);
        if (_e < 1e-12) return 2 * s;
        double es = _e * s;
        return (1 - _e2) * (s / (1 - es * es) - (1 / (2 * _e)) * Math.Log((1 - es) / (1 + es)));
    }

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double rho = _a * Math.Sqrt(Math.Max(0, _c - _n * Q(lat))) / _n;
        double theta = _n * AdjustLon(lon - _lon0);
        x = Parameters.FalseEasting + rho * Math.Sin(theta);
        y = Parameters.FalseNorthing + _rho0 - rho * Math.Cos(theta);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double dx = x - Parameters.FalseEasting, dy = _rho0 - (y - Parameters.FalseNorthing);
        double rho = Math.Sqrt(dx * dx + dy * dy);
        double theta = _n < 0 ? Math.Atan2(-dx, -dy) : Math.Atan2(dx, dy);
        double q = (_c - rho * rho * _n * _n / (_a * _a)) / _n;
        double phi = Math.Asin(Math.Clamp(q / 2, -1, 1));
        if (_e > 1e-12)
        {
            for (int i = 0; i < 25; i++)
            {
                double s = Math.Sin(phi), es = _e * s, cosPhi = Math.Cos(phi);
                double d = (1 - es * es) * (1 - es * es) / (2 * cosPhi) *
                           (q / (1 - _e2) - s / (1 - es * es) + (1 / (2 * _e)) * Math.Log((1 - es) / (1 + es)));
                phi += d;
                if (Math.Abs(d) < 1e-12) break;
            }
        }
        lat = phi;
        lon = theta / _n + _lon0;
    }
}
