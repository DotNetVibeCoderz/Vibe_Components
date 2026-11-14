using Electrical.Core.Data;

namespace Electrical.Forecasting;

/// <summary>Customer class used to shape synthetic daily load profiles.</summary>
public enum LoadProfileKind
{
    /// <summary>Morning and evening peaks (peak ≈ 19–20 h), higher on weekends.</summary>
    Residential,
    /// <summary>Office/retail hours plateau 9–17 h, low on weekends.</summary>
    Commercial,
    /// <summary>Flat, shift-based consumption with a mild daytime hump.</summary>
    Industrial,
}

/// <summary>
/// Deterministic synthetic time series (load, temperature, irradiance, wind speed) for demos, tests and studies.
/// All noise is generated from a seeded <see cref="Random"/>, so results are identical across processes.
/// Time of day is taken from the local clock of the <see cref="DateTimeOffset"/> (its offset); solar geometry uses UTC.
/// </summary>
public static class SyntheticProfiles
{
    // Hourly weekday shapes [pu of daily peak], value at hh:00.
    private static readonly double[] ResidentialShape =
        [0.45, 0.40, 0.38, 0.37, 0.38, 0.45, 0.60, 0.70, 0.62, 0.55, 0.52, 0.52, 0.53, 0.52, 0.52, 0.55, 0.65, 0.82, 0.95, 1.00, 0.96, 0.85, 0.70, 0.55];
    private static readonly double[] CommercialShape =
        [0.35, 0.33, 0.32, 0.32, 0.33, 0.38, 0.50, 0.70, 0.88, 0.96, 1.00, 1.00, 0.97, 0.98, 0.99, 0.97, 0.92, 0.80, 0.62, 0.50, 0.45, 0.42, 0.39, 0.37];
    private static readonly double[] IndustrialShape =
        [0.70, 0.68, 0.67, 0.67, 0.68, 0.72, 0.82, 0.92, 0.98, 1.00, 1.00, 0.99, 0.95, 0.98, 1.00, 0.99, 0.96, 0.90, 0.83, 0.78, 0.75, 0.73, 0.72, 0.71];

    /// <summary>Normalized load [pu of the weekday peak] at a local hour of day (0–24, interpolated) and day of week.</summary>
    public static double Shape(LoadProfileKind kind, double hourOfDay, DayOfWeek day)
    {
        var s = kind switch
        {
            LoadProfileKind.Residential => ResidentialShape,
            LoadProfileKind.Commercial => CommercialShape,
            _ => IndustrialShape,
        };
        double h = ((hourOfDay % 24) + 24) % 24;
        int i = (int)h;
        double f = h - i;
        double v = s[i] * (1 - f) + s[(i + 1) % 24] * f;
        bool weekend = day is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (!weekend) return v;
        return kind switch
        {
            LoadProfileKind.Residential => v * 1.05,
            LoadProfileKind.Commercial => 0.32 + (v - 0.32) * 0.35,
            _ => v * 0.75,
        };
    }

    /// <summary>
    /// Synthetic load [MW] of a customer class. Optional temperature sensitivity adds
    /// <c>coolingPerDegree·max(0, T − balanceTempC)</c> (fraction of peak per °C) when <paramref name="temperature"/> is supplied.
    /// </summary>
    /// <param name="peakMw">Weekday peak demand [MW] (before temperature effect).</param>
    /// <param name="noise">Relative standard deviation of multiplicative AR(1) noise (0 = smooth profile).</param>
    public static TimeSeries Load(LoadProfileKind kind, DateTimeOffset start, int days, TimeSpan step, double peakMw,
        double noise = 0, int seed = 1, TimeSeries? temperature = null, double coolingPerDegree = 0.02, double balanceTempC = 24)
    {
        int n = Samples(days, step);
        var v = new double[n];
        var rng = new Random(seed);
        double e = 0;
        for (int i = 0; i < n; i++)
        {
            var t = start + step * i;
            double x = Shape(kind, t.Hour + t.Minute / 60.0 + t.Second / 3600.0, t.DayOfWeek);
            if (temperature is not null && i < temperature.Count) x += coolingPerDegree * Math.Max(0, temperature[i] - balanceTempC);
            e = 0.7 * e + noise * Math.Sqrt(1 - 0.49) * Gaussian(rng);
            v[i] = peakMw * x * (1 + e);
        }
        return new TimeSeries(start, step, v, $"{kind} load", "MW");
    }

    /// <summary>Ambient temperature [°C]: daily cosine (minimum ≈ 05 h, maximum ≈ 15 h local) plus AR(1) noise [°C].</summary>
    public static TimeSeries Temperature(DateTimeOffset start, int days, TimeSpan step, double meanC = 27, double dailyAmplitudeC = 4,
        double noiseC = 0, int seed = 2)
    {
        int n = Samples(days, step);
        var v = new double[n];
        var rng = new Random(seed);
        double e = 0, dayOffset = 0;
        for (int i = 0; i < n; i++)
        {
            var t = start + step * i;
            if (t.Hour == 0 && t.Minute == 0) dayOffset = noiseC * Gaussian(rng);
            double h = t.Hour + t.Minute / 60.0;
            e = 0.9 * e + noiseC * 0.3 * Gaussian(rng);
            v[i] = meanC + dailyAmplitudeC * Math.Cos(2 * Math.PI * (h - 15) / 24) + dayOffset + e;
        }
        return new TimeSeries(start, step, v, "Temperature", "°C");
    }

    /// <summary>Clear-sky global horizontal irradiance [W/m²] from the Haurwitz model at the given site.</summary>
    public static TimeSeries ClearSkyIrradiance(DateTimeOffset start, int days, TimeSpan step, double latitudeDeg, double longitudeDeg)
    {
        int n = Samples(days, step);
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = SolarForecast.ClearSkyGhi(start + step * i + step / 2, latitudeDeg, longitudeDeg);
        return new TimeSeries(start, step, v, "Clear-sky GHI", "W/m²");
    }

    /// <summary>Daily cloud-cover fraction (0–1), constant within each day, drawn deterministically around <paramref name="meanCloudCover"/>.</summary>
    public static TimeSeries CloudCover(DateTimeOffset start, int days, TimeSpan step, double meanCloudCover = 0.35, int seed = 3)
    {
        int n = Samples(days, step);
        var v = new double[n];
        var rng = new Random(seed);
        double c = meanCloudCover;
        for (int i = 0; i < n; i++)
        {
            var t = start + step * i;
            if (i == 0 || (t.Hour == 0 && t.Minute == 0)) c = Math.Clamp(meanCloudCover + 0.3 * (rng.NextDouble() - 0.5) * 2, 0, 1);
            v[i] = c;
        }
        return new TimeSeries(start, step, v, "Cloud cover", "fraction");
    }

    /// <summary>Global horizontal irradiance [W/m²]: clear-sky (Haurwitz) × Kasten–Czeplak cloud factor of <see cref="CloudCover"/>.</summary>
    public static TimeSeries Irradiance(DateTimeOffset start, int days, TimeSpan step, double latitudeDeg, double longitudeDeg,
        double meanCloudCover = 0.35, int seed = 3)
    {
        var clear = ClearSkyIrradiance(start, days, step, latitudeDeg, longitudeDeg);
        var cc = CloudCover(start, days, step, meanCloudCover, seed);
        var v = new double[clear.Count];
        for (int i = 0; i < v.Length; i++) v[i] = clear[i] * SolarForecast.CloudFactor(cc[i]);
        return new TimeSeries(start, step, v, "GHI", "W/m²");
    }

    /// <summary>Hub-height wind speed [m/s]: AR(1) process with a mild diurnal cycle, clipped at 0.</summary>
    public static TimeSeries WindSpeed(DateTimeOffset start, int days, TimeSpan step, double meanMs = 7, int seed = 4)
    {
        int n = Samples(days, step);
        var v = new double[n];
        var rng = new Random(seed);
        double e = 0;
        double phi = Math.Pow(0.97, step.TotalMinutes / 60.0);
        for (int i = 0; i < n; i++)
        {
            var t = start + step * i;
            double h = t.Hour + t.Minute / 60.0;
            e = phi * e + Math.Sqrt(1 - phi * phi) * 0.35 * meanMs * Gaussian(rng);
            v[i] = Math.Max(0, meanMs * (1 + 0.15 * Math.Cos(2 * Math.PI * (h - 3) / 24)) + e);
        }
        return new TimeSeries(start, step, v, "Wind speed", "m/s");
    }

    internal static int Samples(int days, TimeSpan step) => (int)Math.Round(TimeSpan.FromDays(days) / step);

    /// <summary>Standard normal deviate via Box–Muller.</summary>
    internal static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
