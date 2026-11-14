using Electrical.Core.Data;

namespace Electrical.Forecasting;

/// <summary>
/// Solar PV power forecast from a clear-sky model scaled by a cloud factor.
/// Solar geometry uses the Spencer (1971) declination / equation-of-time series; clear-sky GHI uses Haurwitz (1945):
/// GHI = 1098·cos Z·exp(−0.057 / cos Z) [W/m²]; cloud attenuation uses Kasten &amp; Czeplak (1980): 1 − 0.75·C^3.4.
/// </summary>
public static class SolarForecast
{
    /// <summary>Solar zenith angle [degrees] at an instant (converted to UTC) and site (latitude/longitude in degrees, east positive).</summary>
    public static double SolarZenithDeg(DateTimeOffset time, double latitudeDeg, double longitudeDeg)
    {
        var utc = time.UtcDateTime;
        double hour = utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0;
        double g = 2 * Math.PI / 365 * (utc.DayOfYear - 1 + (hour - 12) / 24);
        double decl = 0.006918 - 0.399912 * Math.Cos(g) + 0.070257 * Math.Sin(g) - 0.006758 * Math.Cos(2 * g)
                      + 0.000907 * Math.Sin(2 * g) - 0.002697 * Math.Cos(3 * g) + 0.00148 * Math.Sin(3 * g);
        double eot = 229.18 * (0.000075 + 0.001868 * Math.Cos(g) - 0.032077 * Math.Sin(g) - 0.014615 * Math.Cos(2 * g) - 0.040849 * Math.Sin(2 * g));
        double tst = hour * 60 + eot + 4 * longitudeDeg;
        double ha = (tst / 4 - 180) * Math.PI / 180;
        double lat = latitudeDeg * Math.PI / 180;
        double cz = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(ha);
        return Math.Acos(Math.Clamp(cz, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Haurwitz clear-sky global horizontal irradiance [W/m²] for a zenith angle [degrees] (0 at night).</summary>
    public static double HaurwitzGhi(double zenithDeg)
    {
        double cz = Math.Cos(zenithDeg * Math.PI / 180);
        return cz <= 0.01 ? 0 : 1098 * cz * Math.Exp(-0.057 / cz);
    }

    /// <summary>Clear-sky GHI [W/m²] at a time and place.</summary>
    public static double ClearSkyGhi(DateTimeOffset time, double latitudeDeg, double longitudeDeg) =>
        HaurwitzGhi(SolarZenithDeg(time, latitudeDeg, longitudeDeg));

    /// <summary>Kasten–Czeplak cloud transmission factor for cloud cover <paramref name="cloudCover"/> in [0, 1] (oktas/8).</summary>
    public static double CloudFactor(double cloudCover) => 1 - 0.75 * Math.Pow(Math.Clamp(cloudCover, 0, 1), 3.4);

    /// <summary>
    /// PV plant AC power [MW] from horizontal irradiance [W/m²] and ambient temperature [°C], using a NOCT cell temperature
    /// (NOCT 45 °C), a linear power temperature coefficient <paramref name="gammaPerC"/> [1/°C] and a lumped system derate.
    /// </summary>
    public static double PvPowerMw(double ghi, double ambientC, double capacityMw, double gammaPerC = -0.004, double derate = 0.86)
    {
        if (ghi <= 0) return 0;
        double tc = ambientC + (45 - 20) / 800.0 * ghi;
        return Math.Max(0, capacityMw * ghi / 1000 * (1 + gammaPerC * (tc - 25)) * derate);
    }

    /// <summary>
    /// PV power forecast [MW] over the time axis of a cloud-cover forecast (fraction 0–1) with an optional temperature forecast [°C]
    /// (25 °C when omitted). Samples are evaluated at the middle of each step.
    /// </summary>
    public static TimeSeries Forecast(TimeSeries cloudCover, double latitudeDeg, double longitudeDeg, double capacityMw,
        TimeSeries? temperature = null, double gammaPerC = -0.004, double derate = 0.86)
    {
        var v = new double[cloudCover.Count];
        for (int i = 0; i < v.Length; i++)
        {
            double ghi = ClearSkyGhi(cloudCover.TimeAt(i) + cloudCover.Step / 2, latitudeDeg, longitudeDeg) * CloudFactor(cloudCover[i]);
            v[i] = PvPowerMw(ghi, temperature is null ? 25 : temperature[i], capacityMw, gammaPerC, derate);
        }
        return new TimeSeries(cloudCover.Start, cloudCover.Step, v, "PV forecast", "MW");
    }
}

/// <summary>Generic wind turbine power curve: zero below cut-in, cubic between cut-in and rated, flat to cut-out, zero above.</summary>
/// <param name="CutInMs">Cut-in speed [m/s].</param>
/// <param name="RatedMs">Rated speed [m/s].</param>
/// <param name="CutOutMs">Cut-out speed [m/s].</param>
/// <param name="RatedMw">Rated power [MW].</param>
public sealed record WindPowerCurve(double CutInMs = 3, double RatedMs = 12, double CutOutMs = 25, double RatedMw = 2)
{
    /// <summary>Electrical output [MW] at hub-height wind speed <paramref name="v"/> [m/s].</summary>
    public double Power(double v)
    {
        if (v < CutInMs || v >= CutOutMs) return 0;
        if (v >= RatedMs) return RatedMw;
        double a = CutInMs * CutInMs * CutInMs, b = RatedMs * RatedMs * RatedMs;
        return RatedMw * (v * v * v - a) / (b - a);
    }
}

/// <summary>Wind power forecasting: physical (speed forecast through a power curve) and persistence benchmark.</summary>
public static class WindForecast
{
    /// <summary>Farm output [MW] = turbines × curve(v) × (1 − losses) for a wind-speed forecast [m/s].</summary>
    public static TimeSeries Forecast(TimeSeries windSpeed, WindPowerCurve curve, int turbines = 1, double losses = 0.1)
    {
        var v = new double[windSpeed.Count];
        for (int i = 0; i < v.Length; i++) v[i] = turbines * curve.Power(windSpeed[i]) * (1 - losses);
        return new TimeSeries(windSpeed.Start, windSpeed.Step, v, "Wind forecast", "MW");
    }

    /// <summary>Persistence forecast: the last observed value repeated over the horizon (the standard reference forecast).</summary>
    public static TimeSeries Persistence(TimeSeries history, int horizon)
    {
        var v = new double[horizon];
        Array.Fill(v, history.Count == 0 ? 0 : history[history.Count - 1]);
        return new TimeSeries(history.End, history.Step, v, "Persistence", history.Unit);
    }
}
