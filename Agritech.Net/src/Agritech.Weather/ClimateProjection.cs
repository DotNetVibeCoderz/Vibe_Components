using Agritech.Core.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.Weather;

/// <summary>IPCC AR6 Shared Socioeconomic Pathway scenarios.</summary>
public enum ClimateScenario { Ssp126, Ssp245, Ssp370, Ssp585 }

/// <summary>Climate change deltas relative to a baseline period.</summary>
public readonly record struct ClimateDelta(double TempChangeC, double RainChangePct, double Co2Ppm);

/// <summary>
/// Delta-change downscaling: perturbs observed baseline series with scenario deltas.
/// Defaults approximate AR6 global-mean warming relative to 1995–2014 and a ±tropical precipitation signal.
/// </summary>
public static class ClimateProjection
{
    /// <summary>Approximate deltas for a target year (linear interpolation from 2020 to 2100).</summary>
    public static ClimateDelta Delta(ClimateScenario scenario, int year)
    {
        (double warm2100, double rain2100, double co2_2100) = scenario switch
        {
            ClimateScenario.Ssp126 => (1.4, 2.0, 445.0),
            ClimateScenario.Ssp245 => (2.3, 3.5, 603.0),
            ClimateScenario.Ssp370 => (3.4, 4.5, 867.0),
            _ => (4.2, 6.0, 1135.0),
        };
        double f = Math.Clamp((year - 2020) / 80.0, 0, 1.25);
        return new ClimateDelta(Math.Round(warm2100 * f, 2), Math.Round(rain2100 * f, 2), Math.Round(415 + (co2_2100 - 415) * f, 0));
    }

    public static IReadOnlyList<DailyWeather> Apply(IEnumerable<DailyWeather> baseline, ClimateDelta delta, int yearShift = 0) =>
        baseline.Select(d => d with
        {
            Date = d.Date.AddYears(yearShift),
            TminC = d.TminC + delta.TempChangeC * 1.1, // nights warm faster than days
            TmaxC = d.TmaxC + delta.TempChangeC * 0.9,
            RainMm = Math.Max(0, d.RainMm * (1 + delta.RainChangePct / 100)),
            Et0Mm = null,
        }).ToList();
}

/// <summary>Agro-climatic indicators used for planning and risk assessment.</summary>
public static class AgroClimate
{
    /// <summary>Longest run of consecutive days with rain below threshold (mm).</summary>
    public static int LongestDrySpell(IEnumerable<DailyWeather> days, double thresholdMm = 1)
    {
        int best = 0, cur = 0;
        foreach (var d in days) { cur = d.RainMm < thresholdMm ? cur + 1 : 0; best = Math.Max(best, cur); }
        return best;
    }

    public static int HeatStressDays(IEnumerable<DailyWeather> days, double tmaxThreshold = 35) => days.Count(d => d.TmaxC >= tmaxThreshold);

    /// <summary>
    /// Rainy-season onset: first day when 5-day accumulated rain ≥ <paramref name="onsetMm"/> and no dry spell
    /// longer than <paramref name="maxDrySpell"/> days follows in the next 30 days (false-start protection).
    /// </summary>
    public static DateOnly? RainySeasonOnset(IReadOnlyList<DailyWeather> days, double onsetMm = 40, int maxDrySpell = 10)
    {
        for (int i = 4; i < days.Count; i++)
        {
            double sum = 0;
            for (int k = i - 4; k <= i; k++) sum += days[k].RainMm;
            if (sum < onsetMm) continue;
            int end = Math.Min(days.Count, i + 31);
            if (LongestDrySpell(days.Skip(i + 1).Take(end - i - 1)) <= maxDrySpell) return days[i].Date;
        }
        return null;
    }

    /// <summary>Simple water balance aridity index P/ET0.</summary>
    public static double AridityIndex(IReadOnlyList<DailyWeather> days, WeatherStation station)
    {
        double p = days.Sum(d => d.RainMm);
        double et = Evapotranspiration.Compute(days, station).Sum();
        return et <= 0 ? double.PositiveInfinity : p / et;
    }
}

public static class WeatherBuilderExtensions
{
    /// <summary>Registers Open-Meteo as the default <see cref="IWeatherProvider"/> (or the synthetic generator when offline).</summary>
    public static AgritechBuilder AddWeather(this AgritechBuilder builder, bool offline = false)
    {
        builder.TryAddModule("Weather");
        if (offline) builder.Services.TryAddSingleton<IWeatherProvider>(new SyntheticWeatherProvider());
        else builder.Services.TryAddSingleton<IWeatherProvider>(_ => new OpenMeteoWeatherProvider());
        return builder;
    }
}
