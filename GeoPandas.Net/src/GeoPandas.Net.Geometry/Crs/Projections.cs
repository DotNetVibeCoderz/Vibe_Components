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
        "tmerc" or "utm" or "etmerc" => new TransverseMercator(e, p),
        "merc" => e.IsSphere ? new WebMercator(e, p) : new Mercator(e, p),
        "webmerc" => new WebMercator(e, p),
        "lcc" => new LambertConformalConic(e, p),
        "aea" => new AlbersEqualArea(e, p),
        "laea" => new LambertAzimuthalEqualArea(e, p),
        "stere" or "ups" => new PolarStereographic(e, p),
        "sterea" => new ObliqueStereographic(e, p),
        "omerc" => new HotineObliqueMercator(e, p, variantB: !p.NoUOffset),
        "cass" => new CassiniSoldner(e, p),
        "eqc" => new EquidistantCylindrical(e, p),
        "sinu" => new Sinusoidal(e, p),
        "cea" => new CylindricalEqualArea(e, p),
        "somerc" => new HotineObliqueMercator(e, p with { Azimuth = 90, RectifiedGridAngle = 90 }, variantB: true),
        "moll" => new Mollweide(e, p),
        "robin" => new Robinson(e, p),
        "eqearth" => new EqualEarth(e, p),
        _ => throw new NotSupportedException($"Projection '{projName}' is not supported yet. Supported: tmerc/utm, merc, webmerc, lcc, aea, laea, stere, sterea, omerc, somerc, cass, eqc, cea, sinu, moll, robin, eqearth.")
    };

    /// <summary>Method description used for WKT / PROJJSON export (null for the original five projections, which are handled explicitly).</summary>
    internal virtual ProjectionMethod? Method => null;
    /// <summary>PROJ string body (+proj=… parameters) for projections that need more than the generic keys.</summary>
    internal virtual string? Proj4Body => null;

    private protected MethodParam FE() => new("False easting", 8806, Parameters.FalseEasting, ParamUnit.Metre);
    private protected MethodParam FN() => new("False northing", 8807, Parameters.FalseNorthing, ParamUnit.Metre);
    protected static string Inv(FormattableString s) => s.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    /// <summary>Azimuth of the initial line (Hotine Oblique Mercator), degrees.</summary>
    public double Azimuth { get; init; } = double.NaN;
    /// <summary>Angle from the rectified to the skew grid γc (Hotine Oblique Mercator), degrees; defaults to the azimuth.</summary>
    public double RectifiedGridAngle { get; init; } = double.NaN;
    /// <summary>Hotine variant A (PROJ <c>+no_uoff</c>): false easting/northing at the natural origin instead of the projection centre.</summary>
    public bool NoUOffset { get; init; }
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

/// <summary>
/// Transverse Mercator with Krüger's n-series to order 6 (Karney 2011 / Poder–Engsager, the algorithm behind PROJ's default <c>tmerc</c>):
/// nanometre accuracy within 3 900 km of the central meridian — used for UTM, TM-3 and national grids.
/// </summary>
public sealed class TransverseMercator : Projection
{
    private readonly double _a, _k0, _lon0, _e, _xi0;
    private readonly double[] _alpha = new double[7], _beta = new double[7];

    public TransverseMercator(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        double f = e.F, n = f / (2 - f), n2 = n * n, n3 = n2 * n, n4 = n3 * n, n5 = n4 * n, n6 = n5 * n;
        _a = e.A / (1 + n) * (1 + n2 / 4 + n4 / 64 + n6 / 256);
        _e = e.E; _k0 = p.K0; _lon0 = p.Lon0 * Deg;
        _alpha[1] = n / 2 - 2 * n2 / 3 + 5 * n3 / 16 + 41 * n4 / 180 - 127 * n5 / 288 + 7891 * n6 / 37800;
        _alpha[2] = 13 * n2 / 48 - 3 * n3 / 5 + 557 * n4 / 1440 + 281 * n5 / 630 - 1983433 * n6 / 1935360;
        _alpha[3] = 61 * n3 / 240 - 103 * n4 / 140 + 15061 * n5 / 26880 + 167603 * n6 / 181440;
        _alpha[4] = 49561 * n4 / 161280 - 179 * n5 / 168 + 6601661 * n6 / 7257600;
        _alpha[5] = 34729 * n5 / 80640 - 3418889 * n6 / 1995840;
        _alpha[6] = 212378941 * n6 / 319334400;
        _beta[1] = n / 2 - 2 * n2 / 3 + 37 * n3 / 96 - n4 / 360 - 81 * n5 / 512 + 96199 * n6 / 604800;
        _beta[2] = n2 / 48 + n3 / 15 - 437 * n4 / 1440 + 46 * n5 / 105 - 1118711 * n6 / 3870720;
        _beta[3] = 17 * n3 / 480 - 37 * n4 / 840 - 209 * n5 / 4480 + 5569 * n6 / 90720;
        _beta[4] = 4397 * n4 / 161280 - 11 * n5 / 504 - 830251 * n6 / 7257600;
        _beta[5] = 4583 * n5 / 161280 - 108847 * n6 / 3991680;
        _beta[6] = 20648693 * n6 / 638668800;
        // ξ of the latitude of origin on the central meridian
        double t0 = Conformal(p.Lat0 * Deg);
        double xi0p = Math.Atan(t0);
        _xi0 = xi0p;
        for (int j = 1; j <= 6; j++) _xi0 += _alpha[j] * Math.Sin(2 * j * xi0p);
    }

    public override string ProjName => Parameters.UtmZone is not null ? "utm" : "tmerc";
    public override string WktName => "Transverse_Mercator";

    /// <summary>tan of the conformal latitude.</summary>
    private double Conformal(double phi)
    {
        double s = Math.Sin(phi);
        if (Math.Abs(s) >= 1) return Math.Sign(s) * double.PositiveInfinity;
        return Math.Sinh(Math.Atanh(s) - _e * Math.Atanh(_e * s));
    }

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double lam = AdjustLon(lon - _lon0);
        double t = Conformal(lat);
        double xip = Math.Atan2(t, Math.Cos(lam));
        double etap = Math.Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        if (double.IsInfinity(t)) { xip = Math.Sign(t) * Math.PI / 2; etap = 0; }
        double xi = xip, eta = etap;
        for (int j = 1; j <= 6; j++)
        {
            xi += _alpha[j] * Math.Sin(2 * j * xip) * Math.Cosh(2 * j * etap);
            eta += _alpha[j] * Math.Cos(2 * j * xip) * Math.Sinh(2 * j * etap);
        }
        x = Parameters.FalseEasting + _k0 * _a * eta;
        y = Parameters.FalseNorthing + _k0 * _a * (xi - _xi0);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double xi = (y - Parameters.FalseNorthing) / (_k0 * _a) + _xi0;
        double eta = (x - Parameters.FalseEasting) / (_k0 * _a);
        double xip = xi, etap = eta;
        for (int j = 1; j <= 6; j++)
        {
            xip -= _beta[j] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etap -= _beta[j] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        double sx = Math.Sin(xip), cx = Math.Cos(xip), she = Math.Sinh(etap);
        double taup = sx / Math.Sqrt(she * she + cx * cx);
        lon = _lon0 + Math.Atan2(she, cx);
        // invert τ' → τ (Karney 2011, eq. 19–21), Newton
        double es = Ellipsoid.E2, tau = taup;
        for (int i = 0; i < 10; i++)
        {
            double sig = Math.Sinh(_e * Math.Atanh(_e * tau / Math.Sqrt(1 + tau * tau)));
            double taui = tau * Math.Sqrt(1 + sig * sig) - sig * Math.Sqrt(1 + tau * tau);
            double dtau = (taup - taui) / Math.Sqrt(1 + taui * taui) * (1 + (1 - es) * tau * tau) / ((1 - es) * Math.Sqrt(1 + tau * tau));
            tau += dtau;
            if (Math.Abs(dtau) < 1e-15) break;
        }
        lat = Math.Atan(tau);
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
