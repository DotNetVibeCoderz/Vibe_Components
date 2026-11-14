namespace Electrical.Renewable;

/// <summary>Sun position at an instant [degrees]. Azimuth is measured clockwise from north (180 = south).</summary>
/// <param name="ZenithDeg">Apparent (refraction-corrected) zenith angle.</param>
/// <param name="AzimuthDeg">Solar azimuth, clockwise from north.</param>
/// <param name="DeclinationDeg">Solar declination.</param>
/// <param name="EquationOfTimeMin">Equation of time [minutes].</param>
/// <param name="HourAngleDeg">Hour angle (negative before solar noon).</param>
public readonly record struct SunPosition(double ZenithDeg, double AzimuthDeg, double DeclinationDeg, double EquationOfTimeMin, double HourAngleDeg)
{
    public double ElevationDeg => 90 - ZenithDeg;
}

/// <summary>
/// Solar position with the NOAA Solar Calculator algorithm (Meeus, "Astronomical Algorithms"), accurate to ≈0.01° for
/// 1800–2100, and the simpler Spencer (1971) Fourier series. Latitude/longitude in degrees (north/east positive).
/// </summary>
public static class SolarPosition
{
    private const double D2R = Math.PI / 180, R2D = 180 / Math.PI;

    /// <summary>NOAA algorithm, including the NOAA standard atmospheric refraction correction.</summary>
    public static SunPosition Compute(DateTimeOffset time, double latitudeDeg, double longitudeDeg, bool refraction = true)
    {
        var utc = time.UtcDateTime;
        double jd = utc.ToOADate() + 2415018.5;
        double jc = (jd - 2451545) / 36525;
        double l0 = Mod(280.46646 + jc * (36000.76983 + jc * 0.0003032), 360);
        double m = 357.52911 + jc * (35999.05029 - 0.0001537 * jc);
        double e = 0.016708634 - jc * (0.000042037 + 0.0000001267 * jc);
        double c = Math.Sin(m * D2R) * (1.914602 - jc * (0.004817 + 0.000014 * jc)) + Math.Sin(2 * m * D2R) * (0.019993 - 0.000101 * jc)
                   + Math.Sin(3 * m * D2R) * 0.000289;
        double trueLong = l0 + c;
        double omega = 125.04 - 1934.136 * jc;
        double appLong = trueLong - 0.00569 - 0.00478 * Math.Sin(omega * D2R);
        double eps0 = 23 + (26 + (21.448 - jc * (46.815 + jc * (0.00059 - jc * 0.001813))) / 60) / 60;
        double eps = eps0 + 0.00256 * Math.Cos(omega * D2R);
        double decl = Math.Asin(Math.Sin(eps * D2R) * Math.Sin(appLong * D2R)) * R2D;
        double y = Math.Tan(eps / 2 * D2R); y *= y;
        double eot = 4 * R2D * (y * Math.Sin(2 * l0 * D2R) - 2 * e * Math.Sin(m * D2R) + 4 * e * y * Math.Sin(m * D2R) * Math.Cos(2 * l0 * D2R)
                                - 0.5 * y * y * Math.Sin(4 * l0 * D2R) - 1.25 * e * e * Math.Sin(2 * m * D2R));
        double minutes = utc.TimeOfDay.TotalMinutes;
        double tst = Mod(minutes + eot + 4 * longitudeDeg, 1440);
        double ha = tst / 4 < 0 ? tst / 4 + 180 : tst / 4 - 180;
        double lat = latitudeDeg * D2R, dr = decl * D2R;
        double cosZ = Math.Clamp(Math.Sin(lat) * Math.Sin(dr) + Math.Cos(lat) * Math.Cos(dr) * Math.Cos(ha * D2R), -1, 1);
        double zen = Math.Acos(cosZ) * R2D;
        double az;
        double sinZ = Math.Sin(zen * D2R);
        if (Math.Abs(Math.Cos(lat) * sinZ) < 1e-12) az = 180;
        else
        {
            double a = Math.Acos(Math.Clamp((Math.Sin(lat) * cosZ - Math.Sin(dr)) / (Math.Cos(lat) * sinZ), -1, 1)) * R2D;
            az = ha > 0 ? Mod(a + 180, 360) : Mod(540 - a, 360);
        }
        if (refraction) zen -= Refraction(90 - zen);
        return new SunPosition(zen, az, decl, eot, ha);
    }

    /// <summary>Spencer (1971) series for declination and equation of time (accuracy ≈0.5°); no refraction.</summary>
    public static SunPosition Spencer(DateTimeOffset time, double latitudeDeg, double longitudeDeg)
    {
        var utc = time.UtcDateTime;
        double hour = utc.TimeOfDay.TotalHours;
        double g = 2 * Math.PI / 365 * (utc.DayOfYear - 1 + (hour - 12) / 24);
        double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g)
                      + 0.000907 * Math.Sin(2 * g) - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
        double eot = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g) - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
        double ha = (hour * 60 + eot + 4 * longitudeDeg) / 4 - 180;
        ha = Mod(ha + 180, 360) - 180;
        double lat = latitudeDeg * D2R;
        double cz = Math.Clamp(Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(ha * D2R), -1, 1);
        double zen = Math.Acos(cz);
        double sinZ = Math.Sin(zen);
        double az = 180;
        if (Math.Abs(Math.Cos(lat) * sinZ) > 1e-12)
        {
            double a = Math.Acos(Math.Clamp((Math.Sin(lat) * cz - Math.Sin(decl)) / (Math.Cos(lat) * sinZ), -1, 1)) * R2D;
            az = ha > 0 ? Mod(a + 180, 360) : Mod(540 - a, 360);
        }
        return new SunPosition(zen * R2D, az, decl * R2D, eot, ha);
    }

    /// <summary>NOAA approximate atmospheric refraction [degrees] at a true elevation [degrees].</summary>
    public static double Refraction(double elevationDeg)
    {
        if (elevationDeg > 85) return 0;
        double te = Math.Tan(elevationDeg * D2R);
        double r = elevationDeg > 5 ? 58.1 / te - 0.07 / (te * te * te) + 0.000086 / Math.Pow(te, 5)
            : elevationDeg > -0.575 ? 1735 + elevationDeg * (-518.2 + elevationDeg * (103.4 + elevationDeg * (-12.79 + elevationDeg * 0.711)))
            : -20.772 / te;
        return r / 3600;
    }

    private static double Mod(double x, double m) => ((x % m) + m) % m;
}

/// <summary>Irradiance components on horizontal and tilted planes [W/m²].</summary>
/// <param name="Ghi">Global horizontal.</param>
/// <param name="Dni">Direct normal.</param>
/// <param name="Dhi">Diffuse horizontal.</param>
public readonly record struct IrradianceComponents(double Ghi, double Dni, double Dhi);

/// <summary>Plane-of-array irradiance breakdown [W/m²].</summary>
public readonly record struct PoaIrradiance(double Global, double Beam, double SkyDiffuse, double GroundReflected, double AngleOfIncidenceDeg);

/// <summary>Clear-sky models, decomposition and transposition to a tilted plane (isotropic sky, Liu–Jordan).</summary>
public static class Irradiance
{
    private const double D2R = Math.PI / 180;

    /// <summary>Solar constant [W/m²].</summary>
    public const double SolarConstant = 1367;

    /// <summary>Extraterrestrial normal irradiance [W/m²] for a day of year (Spencer).</summary>
    public static double ExtraterrestrialNormal(int dayOfYear)
    {
        double b = 2 * Math.PI * (dayOfYear - 1) / 365;
        return SolarConstant * (1.00011 + 0.034221 * Math.Cos(b) + 0.00128 * Math.Sin(b) + 0.000719 * Math.Cos(2 * b) + 0.000077 * Math.Sin(2 * b));
    }

    /// <summary>Kasten–Young (1989) relative air mass for an apparent zenith [degrees].</summary>
    public static double AirMass(double zenithDeg) =>
        zenithDeg >= 90 ? double.PositiveInfinity : 1 / (Math.Cos(zenithDeg * D2R) + 0.50572 * Math.Pow(96.07995 - zenithDeg, -1.6364));

    /// <summary>Haurwitz clear-sky GHI [W/m²].</summary>
    public static double HaurwitzGhi(double zenithDeg)
    {
        double cz = Math.Cos(zenithDeg * D2R);
        return cz <= 0.01 ? 0 : 1098 * cz * Math.Exp(-0.057 / cz);
    }

    /// <summary>
    /// Ineichen–Perez (2002) clear-sky model with Linke turbidity <paramref name="linkeTurbidity"/> at altitude <paramref name="altitudeM"/> [m].
    /// </summary>
    public static IrradianceComponents IneichenClearSky(double zenithDeg, int dayOfYear, double linkeTurbidity = 3, double altitudeM = 0)
    {
        if (zenithDeg >= 90) return default;
        double i0 = ExtraterrestrialNormal(dayOfYear);
        double am = AirMass(zenithDeg);
        double cz = Math.Cos(zenithDeg * D2R);
        double fh1 = Math.Exp(-altitudeM / 8000), fh2 = Math.Exp(-altitudeM / 1250);
        double cg1 = 5.09e-5 * altitudeM + 0.868, cg2 = 3.92e-5 * altitudeM + 0.0387;
        double ghi = Math.Max(0, cg1 * i0 * cz * Math.Exp(-cg2 * am * (fh1 + fh2 * (linkeTurbidity - 1))) * Math.Exp(0.01 * Math.Pow(am, 1.8)));
        double b = 0.664 + 0.163 / fh1;
        double dni = Math.Max(0, b * i0 * Math.Exp(-0.09 * am * (linkeTurbidity - 1)));
        dni = Math.Min(dni, cz > 0 ? ghi / cz : 0);
        return new IrradianceComponents(ghi, dni, Math.Max(0, ghi - dni * cz));
    }

    /// <summary>Erbs et al. (1982) decomposition of GHI into DNI and DHI.</summary>
    public static IrradianceComponents Erbs(double ghi, double zenithDeg, int dayOfYear)
    {
        double cz = Math.Cos(zenithDeg * D2R);
        if (ghi <= 0 || cz <= 0.065) return new IrradianceComponents(Math.Max(ghi, 0), 0, Math.Max(ghi, 0));
        double kt = Math.Clamp(ghi / (ExtraterrestrialNormal(dayOfYear) * cz), 0, 1);
        double kd = kt <= 0.22 ? 1 - 0.09 * kt
            : kt <= 0.8 ? 0.9511 - 0.1604 * kt + 4.388 * kt * kt - 16.638 * Math.Pow(kt, 3) + 12.336 * Math.Pow(kt, 4)
            : 0.165;
        double dhi = kd * ghi;
        return new IrradianceComponents(ghi, (ghi - dhi) / cz, dhi);
    }

    /// <summary>Angle of incidence [degrees] of beam radiation on a surface with tilt and azimuth (clockwise from north, 180 = south-facing).</summary>
    public static double AngleOfIncidence(double zenithDeg, double sunAzimuthDeg, double tiltDeg, double surfaceAzimuthDeg)
    {
        double c = Math.Cos(zenithDeg * D2R) * Math.Cos(tiltDeg * D2R)
                   + Math.Sin(zenithDeg * D2R) * Math.Sin(tiltDeg * D2R) * Math.Cos((sunAzimuthDeg - surfaceAzimuthDeg) * D2R);
        return Math.Acos(Math.Clamp(c, -1, 1)) / D2R;
    }

    /// <summary>Plane-of-array irradiance with the isotropic sky model: beam·cos θ + DHI(1+cos β)/2 + GHI·ρ(1−cos β)/2.</summary>
    public static PoaIrradiance PlaneOfArray(IrradianceComponents irr, SunPosition sun, double tiltDeg, double surfaceAzimuthDeg, double albedo = 0.2)
    {
        double aoi = AngleOfIncidence(sun.ZenithDeg, sun.AzimuthDeg, tiltDeg, surfaceAzimuthDeg);
        double beam = sun.ZenithDeg < 90 ? Math.Max(0, irr.Dni * Math.Cos(aoi * D2R)) : 0;
        double sky = irr.Dhi * (1 + Math.Cos(tiltDeg * D2R)) / 2;
        double ground = irr.Ghi * albedo * (1 - Math.Cos(tiltDeg * D2R)) / 2;
        return new PoaIrradiance(beam + sky + ground, beam, sky, ground, aoi);
    }
}

/// <summary>PV cell/module temperature models [°C].</summary>
public static class CellTemperature
{
    /// <summary>NOCT model: Tc = Ta + (NOCT − 20)/800 · G_poa.</summary>
    public static double Noct(double ambientC, double poaWm2, double noctC = 45) => ambientC + (noctC - 20) / 800 * poaWm2;

    /// <summary>Faiman (2008) model: Tc = Ta + G/(U0 + U1·ws), default U0 = 25 W/m²K, U1 = 6.84 W·s/m³K.</summary>
    public static double Faiman(double ambientC, double poaWm2, double windSpeedMs = 1, double u0 = 25, double u1 = 6.84) =>
        ambientC + poaWm2 / (u0 + u1 * windSpeedMs);
}
