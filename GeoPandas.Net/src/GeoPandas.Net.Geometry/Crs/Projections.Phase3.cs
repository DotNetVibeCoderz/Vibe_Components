namespace GeoPandas.Net.Geometry.Crs;

// Phase 3 projections. Formulas follow IOGP/EPSG Guidance Note 7-2 (ellipsoidal) and, where EPSG and PROJ differ
// (Equidistant Cylindrical, Robinson, Mollweide, Sinusoidal, Equal Earth), PROJ's implementation so results match GDAL/pyproj.
// Every class is checked against pyproj in tests (forward and inverse).

/// <summary>Shared ellipsoid helpers: exact meridian arc, authalic q, conformal t.</summary>
internal static class EllipsoidMath
{
    /// <summary>Meridian arc length from the equator (Helmert series in the third flattening, sub-µm for Earth ellipsoids).</summary>
    public static double MeridianArc(Ellipsoid e, double phi)
    {
        if (e.IsSphere) return e.A * phi;
        double f = e.F, n = f / (2 - f), n2 = n * n, n3 = n2 * n, n4 = n3 * n;
        return e.A / (1 + n) * ((1 + n2 / 4 + n4 / 64) * phi
            - (1.5 * n - 3.0 / 16 * n3) * Math.Sin(2 * phi)
            + (15.0 / 16 * n2 - 15.0 / 64 * n4) * Math.Sin(4 * phi)
            - 35.0 / 48 * n3 * Math.Sin(6 * phi)
            + 315.0 / 512 * n4 * Math.Sin(8 * phi));
    }

    /// <summary>Latitude from meridian arc (Newton).</summary>
    public static double InverseMeridianArc(Ellipsoid e, double m)
    {
        if (e.IsSphere) return m / e.A;
        double phi = m / (e.A * (1 - e.E2 / 4));
        for (int i = 0; i < 20; i++)
        {
            double s = Math.Sin(phi);
            double d = e.A * (1 - e.E2) / Math.Pow(1 - e.E2 * s * s, 1.5);
            double step = (MeridianArc(e, phi) - m) / d;
            phi -= step;
            if (Math.Abs(step) < 1e-14) break;
        }
        return phi;
    }

    /// <summary>Authalic q(φ) (Snyder 3-12).</summary>
    public static double Q(Ellipsoid e, double sinPhi)
    {
        if (e.IsSphere) return 2 * sinPhi;
        double ee = e.E, es = e.E2;
        return (1 - es) * (sinPhi / (1 - es * sinPhi * sinPhi) - 1 / (2 * ee) * Math.Log((1 - ee * sinPhi) / (1 + ee * sinPhi)));
    }

    /// <summary>Latitude from authalic q (Snyder 3-16, Newton-like fixed point).</summary>
    public static double PhiFromQ(Ellipsoid e, double q)
    {
        if (e.IsSphere) return Math.Asin(Math.Clamp(q / 2, -1, 1));
        double ee = e.E, es = e.E2;
        double phi = Math.Asin(Math.Clamp(q / 2, -1, 1));
        for (int i = 0; i < 30; i++)
        {
            double s = Math.Sin(phi), c = Math.Cos(phi);
            if (Math.Abs(c) < 1e-12) return phi;
            double one = 1 - es * s * s;
            double dphi = one * one / (2 * c) * (q / (1 - es) - s / one + 1 / (2 * ee) * Math.Log((1 - ee * s) / (1 + ee * s)));
            phi += dphi;
            if (Math.Abs(dphi) < 1e-14) break;
        }
        return phi;
    }

    /// <summary>Conformal t(φ) = tan(π/4 − φ/2) / ((1 − e sinφ)/(1 + e sinφ))^(e/2).</summary>
    public static double T(Ellipsoid e, double phi)
    {
        double s = e.E * Math.Sin(phi);
        return Math.Tan(Math.PI / 4 - phi / 2) / Math.Pow((1 - s) / (1 + s), e.E / 2);
    }

    /// <summary>φ from t (iterative; the inverse of <see cref="T"/>).</summary>
    public static double PhiFromT(Ellipsoid e, double t)
    {
        double phi = Math.PI / 2 - 2 * Math.Atan(t);
        for (int i = 0; i < 30; i++)
        {
            double s = e.E * Math.Sin(phi);
            double next = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - s) / (1 + s), e.E / 2));
            if (Math.Abs(next - phi) < 1e-14) return next;
            phi = next;
        }
        return phi;
    }

    public static double Nu(Ellipsoid e, double phi) { double s = Math.Sin(phi); return e.A / Math.Sqrt(1 - e.E2 * s * s); }
    public static double Rho(Ellipsoid e, double phi) { double s = Math.Sin(phi); return e.A * (1 - e.E2) / Math.Pow(1 - e.E2 * s * s, 1.5); }
}

/// <summary>Lambert Azimuthal Equal-Area (EPSG 9820 ellipsoidal / 1027 spherical), e.g. ETRS89-LAEA Europe (EPSG:3035).</summary>
public sealed class LambertAzimuthalEqualArea : Projection
{
    private readonly double _qp, _beta0, _rq, _d, _lat0, _lon0;
    private readonly int _polar; // +1 north pole, -1 south pole, 0 oblique/equatorial

    public LambertAzimuthalEqualArea(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _lat0 = p.Lat0 * Deg; _lon0 = p.Lon0 * Deg;
        _qp = EllipsoidMath.Q(e, 1);
        _rq = e.A * Math.Sqrt(_qp / 2);
        double q0 = EllipsoidMath.Q(e, Math.Sin(_lat0));
        _beta0 = Math.Asin(Math.Clamp(q0 / _qp, -1, 1));
        _polar = Math.Abs(p.Lat0 - 90) < 1e-10 ? 1 : Math.Abs(p.Lat0 + 90) < 1e-10 ? -1 : 0;
        _d = _polar != 0 ? 1 : e.A * (Math.Cos(_lat0) / Math.Sqrt(1 - e.E2 * Math.Sin(_lat0) * Math.Sin(_lat0))) / (_rq * Math.Cos(_beta0));
    }

    public override string ProjName => "laea";
    public override string WktName => "Lambert_Azimuthal_Equal_Area";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double q = EllipsoidMath.Q(Ellipsoid, Math.Sin(lat));
        double dl = AdjustLon(lon - _lon0);
        if (_polar != 0)
        {
            double rho = Ellipsoid.A * Math.Sqrt(Math.Max(0, _qp - _polar * q));
            x = Parameters.FalseEasting + rho * Math.Sin(dl);
            y = Parameters.FalseNorthing - _polar * rho * Math.Cos(dl);
            return;
        }
        double beta = Math.Asin(Math.Clamp(q / _qp, -1, 1));
        double b = _rq * Math.Sqrt(2 / (1 + Math.Sin(_beta0) * Math.Sin(beta) + Math.Cos(_beta0) * Math.Cos(beta) * Math.Cos(dl)));
        x = Parameters.FalseEasting + b * _d * Math.Cos(beta) * Math.Sin(dl);
        y = Parameters.FalseNorthing + b / _d * (Math.Cos(_beta0) * Math.Sin(beta) - Math.Sin(_beta0) * Math.Cos(beta) * Math.Cos(dl));
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double dx = x - Parameters.FalseEasting, dy = y - Parameters.FalseNorthing;
        if (_polar != 0)
        {
            double rho = Math.Sqrt(dx * dx + dy * dy);
            double q = _polar * (_qp - rho * rho / (Ellipsoid.A * Ellipsoid.A));
            lat = EllipsoidMath.PhiFromQ(Ellipsoid, q);
            lon = _lon0 + Math.Atan2(dx, -_polar * dy);
            return;
        }
        double r = Math.Sqrt(dx / _d * (dx / _d) + _d * dy * (_d * dy));
        if (r < 1e-12) { lat = _lat0; lon = _lon0; return; }
        double c = 2 * Math.Asin(Math.Clamp(r / (2 * _rq), -1, 1));
        double betaP = Math.Asin(Math.Clamp(Math.Cos(c) * Math.Sin(_beta0) + _d * dy * Math.Sin(c) * Math.Cos(_beta0) / r, -1, 1));
        lon = _lon0 + Math.Atan2(dx * Math.Sin(c), _d * r * Math.Cos(_beta0) * Math.Cos(c) - _d * _d * dy * Math.Sin(_beta0) * Math.Sin(c));
        lat = EllipsoidMath.PhiFromQ(Ellipsoid, _qp * Math.Sin(betaP));
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=laea +lat_0={Parameters.Lat0:R} +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Lambert Azimuthal Equal Area", Ellipsoid.IsSphere ? 1027 : 9820,
        [new("Latitude of natural origin", 8801, Parameters.Lat0, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree),
         FE(), FN()], [("latitude_of_center", Parameters.Lat0), ("longitude_of_center", Parameters.Lon0)]);
}

/// <summary>Polar Stereographic, variant A (scale at pole, EPSG 9810) and variant B (standard parallel, EPSG 9829), e.g. EPSG:3413, 3031, 32661.</summary>
public sealed class PolarStereographic : Projection
{
    private readonly double _k0, _lon0, _ecoef;
    private readonly bool _south;

    public PolarStereographic(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _south = p.Lat0 < 0;
        _lon0 = p.Lon0 * Deg;
        double ee = e.E;
        _ecoef = Math.Sqrt(Math.Pow(1 + ee, 1 + ee) * Math.Pow(1 - ee, 1 - ee));
        if (!double.IsNaN(p.LatTs) && Math.Abs(Math.Abs(p.LatTs) - 90) > 1e-10)
        {
            double phiF = Math.Abs(p.LatTs) * Deg;
            double tF = EllipsoidMath.T(e, phiF);
            double mF = Math.Cos(phiF) / Math.Sqrt(1 - e.E2 * Math.Sin(phiF) * Math.Sin(phiF));
            _k0 = mF * _ecoef / (2 * tF);
        }
        else _k0 = p.K0;
    }

    public bool IsVariantB => !double.IsNaN(Parameters.LatTs);
    public override string ProjName => "stere";
    public override string WktName => "Polar_Stereographic";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double phi = _south ? -lat : lat;
        double t = EllipsoidMath.T(Ellipsoid, phi);
        double rho = 2 * Ellipsoid.A * _k0 * t / _ecoef;
        double dl = lon - _lon0;
        x = Parameters.FalseEasting + rho * Math.Sin(dl);
        y = _south ? Parameters.FalseNorthing + rho * Math.Cos(dl) : Parameters.FalseNorthing - rho * Math.Cos(dl);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double dx = x - Parameters.FalseEasting, dy = y - Parameters.FalseNorthing;
        double rho = Math.Sqrt(dx * dx + dy * dy);
        double t = rho * _ecoef / (2 * Ellipsoid.A * _k0);
        double phi = EllipsoidMath.PhiFromT(Ellipsoid, t);
        lat = _south ? -phi : phi;
        lon = _south ? _lon0 + Math.Atan2(dx, dy) : _lon0 + Math.Atan2(dx, -dy);
    }

    internal override string Proj4Body => (IsVariantB ? Inv($"+proj=stere +lat_0={(Parameters.Lat0 < 0 ? -90 : 90)} +lat_ts={Parameters.LatTs:R}") : Inv($"+proj=stere +lat_0={(Parameters.Lat0 < 0 ? -90 : 90)} +k={Parameters.K0:R}")) + Inv($" +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => IsVariantB
        ? new("Polar Stereographic (variant B)", 9829,
            [new("Latitude of standard parallel", 8832, Parameters.LatTs, ParamUnit.Degree), new("Longitude of origin", 8833, Parameters.Lon0, ParamUnit.Degree),
             new("False easting", 8806, Parameters.FalseEasting, ParamUnit.Metre), new("False northing", 8807, Parameters.FalseNorthing, ParamUnit.Metre)],
            [("latitude_of_origin", Parameters.LatTs), ("central_meridian", Parameters.Lon0)])
        : new("Polar Stereographic (variant A)", 9810,
            [new("Latitude of natural origin", 8801, Parameters.Lat0, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree),
             new("Scale factor at natural origin", 8805, Parameters.K0, ParamUnit.Unity), FE(), FN()],
            [("latitude_of_origin", Parameters.Lat0), ("central_meridian", Parameters.Lon0), ("scale_factor", Parameters.K0)]);
}

/// <summary>Oblique Stereographic (EPSG 9809, PROJ <c>sterea</c>), e.g. Amersfoort / RD New (EPSG:28992).</summary>
public sealed class ObliqueStereographic : Projection
{
    private readonly double _r, _n, _c, _chi0, _lam0, _k0;

    public ObliqueStereographic(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        double phi0 = p.Lat0 * Deg, ee = e.E;
        _lam0 = p.Lon0 * Deg; _k0 = p.K0;
        _r = Math.Sqrt(EllipsoidMath.Rho(e, phi0) * EllipsoidMath.Nu(e, phi0));
        _n = Math.Sqrt(1 + e.E2 * Math.Pow(Math.Cos(phi0), 4) / (1 - e.E2));
        double s1 = (1 + Math.Sin(phi0)) / (1 - Math.Sin(phi0));
        double s2 = (1 - ee * Math.Sin(phi0)) / (1 + ee * Math.Sin(phi0));
        double w1 = Math.Pow(s1 * Math.Pow(s2, ee), _n);
        double sinChi = (w1 - 1) / (w1 + 1);
        _c = (_n + Math.Sin(phi0)) * (1 - sinChi) / ((_n - Math.Sin(phi0)) * (1 + sinChi));
        double w2 = _c * w1;
        _chi0 = Math.Asin((w2 - 1) / (w2 + 1));
    }

    public override string ProjName => "sterea";
    public override string WktName => "Oblique_Stereographic";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double lam = _n * (lon - _lam0) + _lam0;
        double sa = (1 + Math.Sin(lat)) / (1 - Math.Sin(lat));
        double sb = (1 - Ellipsoid.E * Math.Sin(lat)) / (1 + Ellipsoid.E * Math.Sin(lat));
        double w = _c * Math.Pow(sa * Math.Pow(sb, Ellipsoid.E), _n);
        double chi = Math.Asin((w - 1) / (w + 1));
        double b = 1 + Math.Sin(chi) * Math.Sin(_chi0) + Math.Cos(chi) * Math.Cos(_chi0) * Math.Cos(lam - _lam0);
        x = Parameters.FalseEasting + 2 * _r * _k0 * Math.Cos(chi) * Math.Sin(lam - _lam0) / b;
        y = Parameters.FalseNorthing + 2 * _r * _k0 * (Math.Sin(chi) * Math.Cos(_chi0) - Math.Cos(chi) * Math.Sin(_chi0) * Math.Cos(lam - _lam0)) / b;
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double dx = x - Parameters.FalseEasting, dy = y - Parameters.FalseNorthing;
        double g = 2 * _r * _k0 * Math.Tan(Math.PI / 4 - _chi0 / 2);
        double h = 4 * _r * _k0 * Math.Tan(_chi0) + g;
        double i = Math.Atan(dx / (h + dy));
        double j = Math.Atan(dx / (g - dy)) - i;
        double chi = _chi0 + 2 * Math.Atan((dy - dx * Math.Tan(j / 2)) / (2 * _r * _k0));
        double lam = j + 2 * i + _lam0;
        lon = (lam - _lam0) / _n + _lam0;
        double psi = 0.5 * Math.Log((1 + Math.Sin(chi)) / (_c * (1 - Math.Sin(chi)))) / _n;
        double phi = 2 * Math.Atan(Math.Exp(psi)) - Math.PI / 2;
        double ee = Ellipsoid.E;
        for (int k = 0; k < 30; k++)
        {
            double s = Math.Sin(phi);
            double psiI = Math.Log(Math.Tan(phi / 2 + Math.PI / 4) * Math.Pow((1 - ee * s) / (1 + ee * s), ee / 2));
            double next = phi - (psiI - psi) * Math.Cos(phi) * (1 - Ellipsoid.E2 * s * s) / (1 - Ellipsoid.E2);
            if (Math.Abs(next - phi) < 1e-14) { phi = next; break; }
            phi = next;
        }
        lat = phi;
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=sterea +lat_0={Parameters.Lat0:R} +lon_0={Parameters.Lon0:R} +k={Parameters.K0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Oblique Stereographic", 9809,
        [new("Latitude of natural origin", 8801, Parameters.Lat0, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree),
         new("Scale factor at natural origin", 8805, Parameters.K0, ParamUnit.Unity), FE(), FN()],
        [("latitude_of_origin", Parameters.Lat0), ("central_meridian", Parameters.Lon0), ("scale_factor", Parameters.K0)]);
}

/// <summary>
/// Hotine Oblique Mercator, variant A (EPSG 9812, natural origin; PROJ <c>omerc +no_uoff</c>) and variant B (EPSG 9815, projection centre;
/// PROJ <c>omerc</c>) — Rectified Skew Orthomorphic grids of Malaysia (EPSG:3375/3376), Alaska zone 1, Hungary, Switzerland-style grids.
/// Lat0/Lon0 are the projection centre, <see cref="ProjectionParameters.Azimuth"/> the initial line azimuth, <see cref="ProjectionParameters.RectifiedGridAngle"/> γc.
/// </summary>
public sealed class HotineObliqueMercator : Projection
{
    private readonly double _b, _a, _h, _gamma0, _lam0, _uc, _gammaC;

    public HotineObliqueMercator(Ellipsoid e, ProjectionParameters p, bool variantB) : base(e, p)
    {
        VariantB = variantB;
        double phiC = p.Lat0 * Deg, lamC = p.Lon0 * Deg, alphaC = p.Azimuth * Deg, kc = p.K0;
        _gammaC = (double.IsNaN(p.RectifiedGridAngle) ? p.Azimuth : p.RectifiedGridAngle) * Deg;
        double es = e.E2, sinC = Math.Sin(phiC), cosC = Math.Cos(phiC);
        _b = Math.Sqrt(1 + es * Math.Pow(cosC, 4) / (1 - es));
        _a = e.A * _b * kc * Math.Sqrt(1 - es) / (1 - es * sinC * sinC);
        double t0 = EllipsoidMath.T(e, phiC);
        double d = _b * Math.Sqrt(1 - es) / (cosC * Math.Sqrt(1 - es * sinC * sinC));
        double d2 = Math.Max(d * d, 1);
        double f = d + Math.Sqrt(d2 - 1) * Math.Sign(phiC == 0 ? 1 : phiC);
        _h = f * Math.Pow(t0, _b);
        _ = f;
        double sinA = Math.Sin(alphaC);
        _gamma0 = Math.Asin(Math.Clamp(sinA / d, -1, 1));
        // G·tan γ0 written in closed form: G = ±√(D²−1), tan γ0 = sin α / √(D² − sin² α). The direct product is
        // ill-conditioned when sin α / D → 1 (e.g. the Swiss and Hungarian grids with α = 90°).
        double gTan = Math.Sign(phiC == 0 ? 1 : phiC) * Math.Sqrt(d2 - 1) * sinA / Math.Sqrt(Math.Max(d2 - sinA * sinA, 1e-300));
        _lam0 = lamC - Math.Asin(Math.Clamp(gTan, -1, 1)) / _b;
        if (Math.Abs(Math.Abs(p.Azimuth) - 90) < 1e-12) _uc = _a * (lamC - _lam0);
        else _uc = _a / _b * Math.Atan(Math.Sqrt(d2 - 1) / Math.Cos(alphaC)) * Math.Sign(phiC == 0 ? 1 : phiC);
    }

    public bool VariantB { get; }
    public override string ProjName => "omerc";
    public override string WktName => VariantB ? "Hotine_Oblique_Mercator_Azimuth_Center" : "Hotine_Oblique_Mercator";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double t = EllipsoidMath.T(Ellipsoid, lat);
        double q = _h / Math.Pow(t, _b);
        double s = (q - 1 / q) / 2, tt = (q + 1 / q) / 2;
        double bl = _b * AdjustLon(lon - _lam0);
        double v = Math.Sin(bl);
        double u0 = (-v * Math.Cos(_gamma0) + s * Math.Sin(_gamma0)) / tt;
        double vv = _a * Math.Log((1 - u0) / (1 + u0)) / (2 * _b);
        double u = _a * Math.Atan2(s * Math.Cos(_gamma0) + v * Math.Sin(_gamma0), Math.Cos(bl)) / _b;
        if (VariantB) u -= _uc;
        x = Parameters.FalseEasting + vv * Math.Cos(_gammaC) + u * Math.Sin(_gammaC);
        y = Parameters.FalseNorthing + u * Math.Cos(_gammaC) - vv * Math.Sin(_gammaC);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double dx = x - Parameters.FalseEasting, dy = y - Parameters.FalseNorthing;
        double v = dx * Math.Cos(_gammaC) - dy * Math.Sin(_gammaC);
        double u = dy * Math.Cos(_gammaC) + dx * Math.Sin(_gammaC);
        if (VariantB) u += _uc;
        double q = Math.Exp(-_b * v / _a);
        double s = (q - 1 / q) / 2, tt = (q + 1 / q) / 2;
        double vp = Math.Sin(_b * u / _a);
        double up = (vp * Math.Cos(_gamma0) + s * Math.Sin(_gamma0)) / tt;
        if (Math.Abs(Math.Abs(up) - 1) < 1e-14) { lat = up > 0 ? Math.PI / 2 : -Math.PI / 2; lon = _lam0; return; }
        double t = Math.Pow(_h / Math.Sqrt((1 + up) / (1 - up)), 1 / _b);
        lat = EllipsoidMath.PhiFromT(Ellipsoid, t);
        lon = _lam0 - Math.Atan2(s * Math.Cos(_gamma0) - vp * Math.Sin(_gamma0), Math.Cos(_b * u / _a)) / _b;
    }

    internal override string Proj4Body => Inv($"+proj=omerc +lat_0={Parameters.Lat0:R} +lonc={Parameters.Lon0:R} +alpha={Parameters.Azimuth:R} +gamma={GammaDeg:R} +k={Parameters.K0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}") + (VariantB ? "" : " +no_uoff");

    internal override ProjectionMethod Method => VariantB
        ? new("Hotine Oblique Mercator (variant B)", 9815,
            [new("Latitude of projection centre", 8811, Parameters.Lat0, ParamUnit.Degree), new("Longitude of projection centre", 8812, Parameters.Lon0, ParamUnit.Degree),
             new("Azimuth of initial line", 8813, Parameters.Azimuth, ParamUnit.Degree), new("Angle from Rectified to Skew Grid", 8814, GammaDeg, ParamUnit.Degree),
             new("Scale factor on initial line", 8815, Parameters.K0, ParamUnit.Unity),
             new("Easting at projection centre", 8816, Parameters.FalseEasting, ParamUnit.Metre), new("Northing at projection centre", 8817, Parameters.FalseNorthing, ParamUnit.Metre)],
            [("latitude_of_center", Parameters.Lat0), ("longitude_of_center", Parameters.Lon0), ("azimuth", Parameters.Azimuth), ("rectified_grid_angle", GammaDeg), ("scale_factor", Parameters.K0)])
        : new("Hotine Oblique Mercator (variant A)", 9812,
            [new("Latitude of projection centre", 8811, Parameters.Lat0, ParamUnit.Degree), new("Longitude of projection centre", 8812, Parameters.Lon0, ParamUnit.Degree),
             new("Azimuth of initial line", 8813, Parameters.Azimuth, ParamUnit.Degree), new("Angle from Rectified to Skew Grid", 8814, GammaDeg, ParamUnit.Degree),
             new("Scale factor on initial line", 8815, Parameters.K0, ParamUnit.Unity), FE(), FN()],
            [("latitude_of_center", Parameters.Lat0), ("longitude_of_center", Parameters.Lon0), ("azimuth", Parameters.Azimuth), ("rectified_grid_angle", GammaDeg), ("scale_factor", Parameters.K0)]);

    private double GammaDeg => double.IsNaN(Parameters.RectifiedGridAngle) ? Parameters.Azimuth : Parameters.RectifiedGridAngle;
}

/// <summary>Cassini-Soldner (EPSG 9806), e.g. the Malaysian state Cassini grids (EPSG:3377–3393), Trinidad 1903.</summary>
public sealed class CassiniSoldner : Projection
{
    private readonly double _m0, _lon0;

    public CassiniSoldner(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _m0 = EllipsoidMath.MeridianArc(e, p.Lat0 * Deg); _lon0 = p.Lon0 * Deg;
    }

    public override string ProjName => "cass";
    public override string WktName => "Cassini_Soldner";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        var e = Ellipsoid;
        double c = Math.Cos(lat), tn = Math.Tan(lat);
        double a = AdjustLon(lon - _lon0) * c, t = tn * tn, cc = e.E2 * c * c / (1 - e.E2), nu = EllipsoidMath.Nu(e, lat);
        double a2 = a * a, a3 = a2 * a, a4 = a3 * a, a5 = a4 * a;
        x = Parameters.FalseEasting + nu * (a - t * a3 / 6 - (8 - t + 8 * cc) * t * a5 / 120);
        y = Parameters.FalseNorthing + EllipsoidMath.MeridianArc(e, lat) - _m0 + nu * tn * (a2 / 2 + (5 - t + 6 * cc) * a4 / 24);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        var e = Ellipsoid;
        double phi1 = EllipsoidMath.InverseMeridianArc(e, _m0 + (y - Parameters.FalseNorthing));
        double t1 = Math.Pow(Math.Tan(phi1), 2), nu1 = EllipsoidMath.Nu(e, phi1), rho1 = EllipsoidMath.Rho(e, phi1);
        double d = (x - Parameters.FalseEasting) / nu1, d2 = d * d, d3 = d2 * d, d4 = d3 * d, d5 = d4 * d;
        lat = phi1 - nu1 * Math.Tan(phi1) / rho1 * (d2 / 2 - (1 + 3 * t1) * d4 / 24);
        lon = _lon0 + (d - t1 * d3 / 3 + (1 + 3 * t1) * t1 * d5 / 15) / Math.Cos(phi1);
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=cass +lat_0={Parameters.Lat0:R} +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Cassini-Soldner", 9806,
        [new("Latitude of natural origin", 8801, Parameters.Lat0, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()],
        [("latitude_of_origin", Parameters.Lat0), ("central_meridian", Parameters.Lon0)]);
}

/// <summary>Equidistant Cylindrical (EPSG 1028 ellipsoidal / 1029 spherical; PROJ <c>eqc</c>), e.g. World Equidistant Cylindrical EPSG:4087.</summary>
public sealed class EquidistantCylindrical : Projection
{
    private readonly double _nu1cos, _lon0, _m0;

    public EquidistantCylindrical(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        double phi1 = LatTs * Deg;
        _nu1cos = EllipsoidMath.Nu(e, phi1) * Math.Cos(phi1);
        _lon0 = p.Lon0 * Deg;
        _m0 = EllipsoidMath.MeridianArc(e, p.Lat0 * Deg);
    }

    private double LatTs => double.IsNaN(Parameters.LatTs) ? (double.IsNaN(Parameters.Lat1) ? 0 : Parameters.Lat1) : Parameters.LatTs;
    public override string ProjName => "eqc";
    public override string WktName => "Equirectangular";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        x = Parameters.FalseEasting + _nu1cos * AdjustLon(lon - _lon0);
        y = Parameters.FalseNorthing + EllipsoidMath.MeridianArc(Ellipsoid, lat) - _m0;
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        lon = (x - Parameters.FalseEasting) / _nu1cos + _lon0;
        lat = EllipsoidMath.InverseMeridianArc(Ellipsoid, y - Parameters.FalseNorthing + _m0);
    }

    internal override string Proj4Body => Inv($"+proj=eqc +lat_ts={LatTs:R} +lat_0={Parameters.Lat0:R} +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Equidistant Cylindrical", Ellipsoid.IsSphere ? 1029 : 1028,
        [new("Latitude of 1st standard parallel", 8823, LatTs, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()],
        [("standard_parallel_1", LatTs), ("central_meridian", Parameters.Lon0)]);
}

/// <summary>Lambert Cylindrical Equal Area (EPSG 9835 ellipsoidal / 9834 spherical, PROJ <c>cea</c>), e.g. EASE-Grid 2.0 global (EPSG:6933).</summary>
public sealed class CylindricalEqualArea : Projection
{
    private readonly double _k0, _qp, _lon0;

    public CylindricalEqualArea(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        double phi1 = LatTs * Deg, s = Math.Sin(phi1);
        _k0 = double.IsNaN(p.LatTs) && double.IsNaN(p.Lat1) && p.K0 != 1 ? p.K0 : Math.Cos(phi1) / Math.Sqrt(1 - e.E2 * s * s);
        _qp = EllipsoidMath.Q(e, 1);
        _lon0 = p.Lon0 * Deg;
    }

    private double LatTs => double.IsNaN(Parameters.LatTs) ? (double.IsNaN(Parameters.Lat1) ? 0 : Parameters.Lat1) : Parameters.LatTs;
    public override string ProjName => "cea";
    public override string WktName => "Cylindrical_Equal_Area";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        x = Parameters.FalseEasting + Ellipsoid.A * _k0 * AdjustLon(lon - _lon0);
        y = Parameters.FalseNorthing + Ellipsoid.A * EllipsoidMath.Q(Ellipsoid, Math.Sin(lat)) / (2 * _k0);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double q = 2 * _k0 * (y - Parameters.FalseNorthing) / Ellipsoid.A;
        lat = EllipsoidMath.PhiFromQ(Ellipsoid, Math.Clamp(q, -_qp, _qp));
        lon = _lon0 + (x - Parameters.FalseEasting) / (Ellipsoid.A * _k0);
    }

    internal override string Proj4Body => Inv($"+proj=cea +lat_ts={LatTs:R} +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new(Ellipsoid.IsSphere ? "Lambert Cylindrical Equal Area (Spherical)" : "Lambert Cylindrical Equal Area", Ellipsoid.IsSphere ? 9834 : 9835,
        [new("Latitude of 1st standard parallel", 8823, LatTs, ParamUnit.Degree), new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()],
        [("standard_parallel_1", LatTs), ("central_meridian", Parameters.Lon0)]);
}

/// <summary>Sinusoidal (PROJ <c>sinu</c>; ellipsoidal uses the meridian arc). With a sphere of R = 6 371 007.181 m it is the MODIS tile grid.</summary>
public sealed class Sinusoidal : Projection
{
    private readonly double _lon0;
    public Sinusoidal(Ellipsoid e, ProjectionParameters p) : base(e, p) { _lon0 = p.Lon0 * Deg; }
    public override string ProjName => "sinu";
    public override string WktName => "Sinusoidal";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double s = Math.Sin(lat);
        x = Parameters.FalseEasting + Ellipsoid.A * AdjustLon(lon - _lon0) * Math.Cos(lat) / Math.Sqrt(1 - Ellipsoid.E2 * s * s);
        y = Parameters.FalseNorthing + EllipsoidMath.MeridianArc(Ellipsoid, lat);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        lat = EllipsoidMath.InverseMeridianArc(Ellipsoid, y - Parameters.FalseNorthing);
        double s = Math.Sin(lat), c = Math.Cos(lat);
        lon = Math.Abs(c) < 1e-14 ? _lon0 : _lon0 + (x - Parameters.FalseEasting) * Math.Sqrt(1 - Ellipsoid.E2 * s * s) / (Ellipsoid.A * c);
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=sinu +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Sinusoidal", null,
        [new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()], [("central_meridian", Parameters.Lon0)]);
}

/// <summary>Mollweide (PROJ <c>moll</c>, spherical with radius a).</summary>
public sealed class Mollweide : Projection
{
    private readonly double _lon0;
    public Mollweide(Ellipsoid e, ProjectionParameters p) : base(e, p) { _lon0 = p.Lon0 * Deg; }
    public override string ProjName => "moll";
    public override string WktName => "Mollweide";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double k = Math.PI * Math.Sin(lat), theta = lat;
        for (int i = 0; i < 50; i++)
        {
            double d = (theta + Math.Sin(theta) - k) / (1 + Math.Cos(theta));
            theta -= d;
            if (Math.Abs(d) < 1e-15) break;
        }
        if (Math.Abs(Math.Abs(lat) - Math.PI / 2) < 1e-12) theta = lat; else theta /= 2;
        x = Parameters.FalseEasting + Ellipsoid.A * 2 * Math.Sqrt(2) / Math.PI * AdjustLon(lon - _lon0) * Math.Cos(theta);
        y = Parameters.FalseNorthing + Ellipsoid.A * Math.Sqrt(2) * Math.Sin(theta);
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double theta = Math.Asin(Math.Clamp((y - Parameters.FalseNorthing) / (Ellipsoid.A * Math.Sqrt(2)), -1, 1));
        lon = _lon0 + Math.PI * (x - Parameters.FalseEasting) / (2 * Math.Sqrt(2) * Ellipsoid.A * Math.Cos(theta));
        lat = Math.Asin(Math.Clamp((2 * theta + Math.Sin(2 * theta)) / Math.PI, -1, 1));
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=moll +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Mollweide", null,
        [new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()], [("central_meridian", Parameters.Lon0)]);
}

/// <summary>Robinson (PROJ <c>robin</c>: PROJ's 5° coefficient table with cubic interpolation, spherical with radius a).</summary>
public sealed class Robinson : Projection
{
    private static readonly float[,] X =
    {
        {1.0f, 2.2199e-17f, -7.15515e-05f, 3.1103e-06f}, {0.9986f, -0.000482243f, -2.4897e-05f, -1.3309e-06f},
        {0.9954f, -0.00083103f, -4.48605e-05f, -9.86701e-07f}, {0.99f, -0.00135364f, -5.9661e-05f, 3.6777e-06f},
        {0.9822f, -0.00167442f, -4.49547e-06f, -5.72411e-06f}, {0.973f, -0.00214868f, -9.03571e-05f, 1.8736e-08f},
        {0.96f, -0.00305085f, -9.00761e-05f, 1.64917e-06f}, {0.9427f, -0.00382792f, -6.53386e-05f, -2.6154e-06f},
        {0.9216f, -0.00467746f, -0.00010457f, 4.81243e-06f}, {0.8962f, -0.00536223f, -3.23831e-05f, -5.43432e-06f},
        {0.8679f, -0.00609363f, -0.000113898f, 3.32484e-06f}, {0.835f, -0.00698325f, -6.40253e-05f, 9.34959e-07f},
        {0.7986f, -0.00755338f, -5.00009e-05f, 9.35324e-07f}, {0.7597f, -0.00798324f, -3.5971e-05f, -2.27626e-06f},
        {0.7186f, -0.00851367f, -7.01149e-05f, -8.6303e-06f}, {0.6732f, -0.00986209f, -0.000199569f, 1.91974e-05f},
        {0.6213f, -0.010418f, 8.83923e-05f, 6.24051e-06f}, {0.5722f, -0.00906601f, 0.000182f, 6.24051e-06f},
        {0.5322f, -0.00677797f, 0.000275608f, 6.24051e-06f}
    };

    private static readonly float[,] Y =
    {
        {-5.20417e-18f, 0.0124f, 1.21431e-18f, -8.45284e-11f}, {0.062f, 0.0124f, -1.26793e-09f, 4.22642e-10f},
        {0.124f, 0.0124f, 5.07171e-09f, -1.60604e-09f}, {0.186f, 0.0123999f, -1.90189e-08f, 6.00152e-09f},
        {0.248f, 0.0124002f, 7.10039e-08f, -2.24e-08f}, {0.31f, 0.0123992f, -2.64997e-07f, 8.35986e-08f},
        {0.372f, 0.0124029f, 9.88983e-07f, -3.11994e-07f}, {0.434f, 0.0123893f, -3.69093e-06f, -4.35621e-07f},
        {0.4958f, 0.0123198f, -1.02252e-05f, -3.45523e-07f}, {0.5571f, 0.0121916f, -1.54081e-05f, -5.82288e-07f},
        {0.6176f, 0.0119938f, -2.41424e-05f, -5.25327e-07f}, {0.6769f, 0.011713f, -3.20223e-05f, -5.16405e-07f},
        {0.7346f, 0.0113541f, -3.97684e-05f, -6.09052e-07f}, {0.7903f, 0.0109107f, -4.89042e-05f, -1.04739e-06f},
        {0.8435f, 0.0103431f, -6.4615e-05f, -1.40374e-09f}, {0.8936f, 0.00969686f, -6.4636e-05f, -8.547e-06f},
        {0.9394f, 0.00840947f, -0.000192841f, -4.2106e-06f}, {0.9761f, 0.00616527f, -0.000256f, -4.2106e-06f},
        {1.0f, 0.00328947f, -0.000319159f, -4.2106e-06f}
    };

    private const double Fxc = 0.8487, Fyc = 1.3523, C1 = 11.45915590261646417544, Rc1 = 0.08726646259971647884;
    private const int Nodes = 18;
    private readonly double _lon0;

    public Robinson(Ellipsoid e, ProjectionParameters p) : base(e, p) { _lon0 = p.Lon0 * Deg; }
    public override string ProjName => "robin";
    public override string WktName => "Robinson";

    private static double V(float[,] c, int i, double z) => c[i, 0] + z * (c[i, 1] + z * (c[i, 2] + z * (double)c[i, 3]));
    private static double DV(float[,] c, int i, double z) => c[i, 1] + 2 * z * c[i, 2] + z * z * 3.0 * c[i, 3];

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double lam = AdjustLon(lon - _lon0);
        double dphi = Math.Abs(lat);
        int i = (int)Math.Round(Math.Floor(dphi * C1 + 1e-15));
        if (i >= Nodes) i = Nodes;
        dphi = 180 / Math.PI * (dphi - Rc1 * i);
        double xx = V(X, i, dphi) * Fxc * lam, yy = V(Y, i, dphi) * Fyc;
        if (lat < 0) yy = -yy;
        x = Parameters.FalseEasting + Ellipsoid.A * xx;
        y = Parameters.FalseNorthing + Ellipsoid.A * yy;
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double xx = (x - Parameters.FalseEasting) / Ellipsoid.A, yy = (y - Parameters.FalseNorthing) / Ellipsoid.A;
        double lam = xx / Fxc, phi = Math.Abs(yy / Fyc);
        if (phi >= 1)
        {
            lat = yy < 0 ? -Math.PI / 2 : Math.PI / 2;
            lon = _lon0 + lam / X[Nodes, 0];
            return;
        }
        int i = (int)Math.Floor(phi * Nodes);
        for (; ; )
        {
            if (Y[i, 0] > phi) --i;
            else if (Y[i + 1, 0] <= phi) ++i;
            else break;
        }
        double t = 5.0 * (phi - Y[i, 0]) / (Y[i + 1, 0] - Y[i, 0]);
        for (int it = 0; it < 100; it++)
        {
            double t1 = (V(Y, i, t) - phi) / DV(Y, i, t);
            t -= t1;
            if (Math.Abs(t1) < 1e-10) break;
        }
        lat = (5 * i + t) * Math.PI / 180;
        if (yy < 0) lat = -lat;
        lon = _lon0 + lam / V(X, i, t);
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=robin +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Robinson", null,
        [new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()], [("central_meridian", Parameters.Lon0)]);
}

/// <summary>Equal Earth (EPSG 1078, PROJ <c>eqearth</c>, ellipsoidal via authalic latitude), e.g. EPSG:8857.</summary>
public sealed class EqualEarth : Projection
{
    private const double A1 = 1.340264, A2 = -0.081106, A3 = 0.000893, A4 = 0.003796;
    private static readonly double M = Math.Sqrt(3.0) / 2.0;
    private readonly double _qp, _rqda, _lon0;

    public EqualEarth(Ellipsoid e, ProjectionParameters p) : base(e, p)
    {
        _lon0 = p.Lon0 * Deg;
        _qp = EllipsoidMath.Q(e, 1);
        _rqda = e.IsSphere ? 1 : Math.Sqrt(0.5 * _qp);
    }

    public override string ProjName => "eqearth";
    public override string WktName => "Equal_Earth";

    public override void Forward(double lon, double lat, out double x, out double y)
    {
        double sbeta = Math.Sin(lat);
        if (!Ellipsoid.IsSphere) sbeta = Math.Clamp(EllipsoidMath.Q(Ellipsoid, sbeta) / _qp, -1, 1);
        double psi = Math.Asin(M * sbeta), psi2 = psi * psi, psi6 = psi2 * psi2 * psi2;
        double lam = AdjustLon(lon - _lon0);
        double xx = lam * Math.Cos(psi) / (M * (A1 + 3 * A2 * psi2 + psi6 * (7 * A3 + 9 * A4 * psi2)));
        double yy = psi * (A1 + A2 * psi2 + psi6 * (A3 + A4 * psi2));
        x = Parameters.FalseEasting + Ellipsoid.A * xx * _rqda;
        y = Parameters.FalseNorthing + Ellipsoid.A * yy * _rqda;
    }

    public override void Inverse(double x, double y, out double lon, out double lat)
    {
        double xx = (x - Parameters.FalseEasting) / (Ellipsoid.A * _rqda), yy = (y - Parameters.FalseNorthing) / (Ellipsoid.A * _rqda);
        yy = Math.Clamp(yy, -1.3173627591574, 1.3173627591574);
        double yc = yy;
        for (int i = 0; i < 12; i++)
        {
            double y2 = yc * yc, y6 = y2 * y2 * y2;
            double f = yc * (A1 + A2 * y2 + y6 * (A3 + A4 * y2)) - yy;
            double fd = A1 + 3 * A2 * y2 + y6 * (7 * A3 + 9 * A4 * y2);
            double tol = f / fd;
            yc -= tol;
            if (Math.Abs(tol) < 1e-11) break;
        }
        double yy2 = yc * yc, yy6 = yy2 * yy2 * yy2;
        lon = _lon0 + M * xx * (A1 + 3 * A2 * yy2 + yy6 * (7 * A3 + 9 * A4 * yy2)) / Math.Cos(yc);
        double beta = Math.Asin(Math.Clamp(Math.Sin(yc) / M, -1, 1));
        lat = Ellipsoid.IsSphere ? beta : EllipsoidMath.PhiFromQ(Ellipsoid, _qp * Math.Sin(beta));
    }

    internal override string Proj4Body => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"+proj=eqearth +lon_0={Parameters.Lon0:R} +x_0={Parameters.FalseEasting:R} +y_0={Parameters.FalseNorthing:R}");

    internal override ProjectionMethod Method => new("Equal Earth", 1078,
        [new("Longitude of natural origin", 8802, Parameters.Lon0, ParamUnit.Degree), FE(), FN()], [("central_meridian", Parameters.Lon0)]);
}
