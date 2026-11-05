namespace Agritech.Weather;

/// <summary>FAO Irrigation &amp; Drainage Paper 56 reference evapotranspiration.</summary>
public static class Evapotranspiration
{
    public static double SaturationVapourPressure(double t) => 0.6108 * Math.Exp(17.27 * t / (t + 237.3));
    public static double AtmosphericPressure(double elevation) => 101.3 * Math.Pow((293 - 0.0065 * elevation) / 293, 5.26);
    public static double PsychrometricConstant(double elevation) => 0.000665 * AtmosphericPressure(elevation);
    public static double SlopeVapourPressure(double t) => 4098 * SaturationVapourPressure(t) / Math.Pow(t + 237.3, 2);

    /// <summary>Extraterrestrial radiation Ra (MJ m⁻² day⁻¹), FAO-56 eq. 21.</summary>
    public static double ExtraterrestrialRadiation(double latitudeDeg, int dayOfYear)
    {
        double phi = latitudeDeg * Math.PI / 180;
        double dr = 1 + 0.033 * Math.Cos(2 * Math.PI / 365 * dayOfYear);
        double dec = 0.409 * Math.Sin(2 * Math.PI / 365 * dayOfYear - 1.39);
        double ws = Math.Acos(Math.Clamp(-Math.Tan(phi) * Math.Tan(dec), -1, 1));
        return 24 * 60 / Math.PI * 0.0820 * dr * (ws * Math.Sin(phi) * Math.Sin(dec) + Math.Cos(phi) * Math.Cos(dec) * Math.Sin(ws));
    }

    /// <summary>Daylight hours N (FAO-56 eq. 34).</summary>
    public static double DaylightHours(double latitudeDeg, int dayOfYear)
    {
        double phi = latitudeDeg * Math.PI / 180;
        double dec = 0.409 * Math.Sin(2 * Math.PI / 365 * dayOfYear - 1.39);
        return 24 / Math.PI * Math.Acos(Math.Clamp(-Math.Tan(phi) * Math.Tan(dec), -1, 1));
    }

    /// <summary>Solar radiation from temperature range (Hargreaves, eq. 50); kRs 0.16 interior, 0.19 coastal.</summary>
    public static double SolarRadiationFromTemperature(double tmin, double tmax, double ra, double kRs = 0.17) =>
        kRs * Math.Sqrt(Math.Max(0, tmax - tmin)) * ra;

    /// <summary>FAO-56 Penman–Monteith daily ET0 (mm/day).</summary>
    public static double PenmanMonteith(DailyWeather w, WeatherStation station)
    {
        double tmean = w.TmeanC;
        double gamma = PsychrometricConstant(station.ElevationM);
        double es = (SaturationVapourPressure(w.TmaxC) + SaturationVapourPressure(w.TminC)) / 2;
        // Missing RH → assume Tdew ≈ Tmin (FAO-56 eq. 48, humid/sub-humid regions)
        double ea = w.RhMeanPct is { } rh ? rh / 100 * es : SaturationVapourPressure(w.TminC);
        double delta = SlopeVapourPressure(tmean);
        double ra = ExtraterrestrialRadiation(station.LatitudeDeg, w.DayOfYear);
        double rs = w.SolarRadMjM2 ?? SolarRadiationFromTemperature(w.TminC, w.TmaxC, ra);
        double u2 = w.Wind2mMs ?? 2.0; // FAO-56 recommended default
        double rso = (0.75 + 2e-5 * station.ElevationM) * ra;
        double rns = 0.77 * rs;
        double tk4 = (Math.Pow(w.TmaxC + 273.16, 4) + Math.Pow(w.TminC + 273.16, 4)) / 2;
        double rnl = 4.903e-9 * tk4 * (0.34 - 0.14 * Math.Sqrt(Math.Max(0, ea))) * (1.35 * Math.Min(1, rso > 0 ? rs / rso : 0) - 0.35);
        double rn = rns - rnl;
        double num = 0.408 * delta * rn + gamma * 900 / (tmean + 273) * u2 * (es - ea);
        double den = delta + gamma * (1 + 0.34 * u2);
        return Math.Max(0, num / den);
    }

    /// <summary>Hargreaves–Samani ET0 (mm/day) — temperature only.</summary>
    public static double Hargreaves(double tmin, double tmax, double latitudeDeg, int dayOfYear)
    {
        double ra = ExtraterrestrialRadiation(latitudeDeg, dayOfYear);
        return Math.Max(0, 0.0023 * (((tmin + tmax) / 2) + 17.8) * Math.Sqrt(Math.Max(0, tmax - tmin)) * ra * 0.408);
    }

    /// <summary>ET0 for a sequence, using provided ET0 where present.</summary>
    public static double[] Compute(IReadOnlyList<DailyWeather> days, WeatherStation station)
    {
        var res = new double[days.Count];
        for (int i = 0; i < days.Count; i++) res[i] = days[i].Et0Mm ?? PenmanMonteith(days[i], station);
        return res;
    }
}
