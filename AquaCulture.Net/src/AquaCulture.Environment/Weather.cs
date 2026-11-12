using System.Globalization;
using System.Text.Json;
using AquaCulture.Core.Data;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Environment;

/// <summary>Hourly (or daily) weather observation.</summary>
public sealed record WeatherObservation(DateTime Time, double AirTempC, double RainfallMm, double HumidityPct, double WindMs, double SolarWm2,
    double PressureHPa = 1010, double CloudPct = double.NaN);

/// <summary>Daily aggregate.</summary>
public sealed record DailyWeather(DateOnly Date, double TminC, double TmaxC, double TmeanC, double RainMm, double MeanWindMs, double MaxWindMs,
    double SolarMjM2, double HumidityPct, double PressureHPa)
{
    public static IReadOnlyList<DailyWeather> FromHourly(IEnumerable<WeatherObservation> hourly) =>
        hourly.GroupBy(h => DateOnly.FromDateTime(h.Time)).OrderBy(g => g.Key).Select(g => new DailyWeather(g.Key,
            g.Min(x => x.AirTempC), g.Max(x => x.AirTempC), g.Average(x => x.AirTempC), g.Sum(x => x.RainfallMm),
            g.Average(x => x.WindMs), g.Max(x => x.WindMs), g.Sum(x => x.SolarWm2) * 3600 / 1e6 * (24.0 / g.Count()),
            g.Average(x => x.HumidityPct), g.Average(x => x.PressureHPa))).ToList();
}

/// <summary>
/// Deterministic tropical weather generator (hourly): diurnal temperature, monsoon rain seasonality, convective afternoon showers,
/// clear-sky solar geometry with cloud attenuation and gusty wind. Useful for simulations, demos and tests.
/// </summary>
public static class SyntheticWeather
{
    public static IReadOnlyList<WeatherObservation> Generate(double latitude, DateTime start, int days, ulong seed = 42, double meanTempC = 27.5,
        double wetSeasonStrength = 1)
    {
        var rng = new DeterministicRandom(seed);
        var list = new List<WeatherObservation>(days * 24);
        double cloudiness = 0.4, windBase = 2.2;
        for (int d = 0; d < days; d++)
        {
            var date = start.Date.AddDays(d);
            int doy = date.DayOfYear;
            // Indonesian west-monsoon wet season peaks around January (doy ~15)
            double wet = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (doy - 15) / 365.0) * wetSeasonStrength;
            cloudiness = Math.Clamp(0.7 * cloudiness + 0.3 * (0.25 + 0.5 * wet) + 0.15 * rng.NextGaussian(), 0.05, 0.98);
            windBase = Math.Clamp(0.8 * windBase + 0.2 * 2.5 + 0.4 * rng.NextGaussian(), 0.3, 9);
            bool rainyDay = rng.NextDouble() < 0.25 + 0.5 * wet;
            double dayRain = rainyDay ? rng.LogNormal(8 + 22 * wet, 1.1) : 0;
            double dayMean = meanTempC + 0.8 * Math.Cos(2 * Math.PI * (doy - 120) / 365.0) - 1.2 * cloudiness + 0.4 * rng.NextGaussian();
            double amp = 3.8 * (1 - 0.5 * cloudiness);
            int rainStart = 13 + rng.Next(6);
            for (int h = 0; h < 24; h++)
            {
                var t = date.AddHours(h);
                double temp = dayMean + amp * Math.Cos(2 * Math.PI * (h - 14) / 24.0) + 0.2 * rng.NextGaussian();
                double rain = 0;
                if (dayRain > 0 && h >= rainStart && h < rainStart + 3) rain = dayRain * (h == rainStart ? 0.5 : 0.25);
                if (rain > 0) temp -= 1.5;
                double solar = ClearSkySolar(latitude, doy, h + 0.5) * (1 - 0.75 * cloudiness) * (rain > 0 ? 0.3 : 1);
                double wind = Math.Max(0, windBase * (0.7 + 0.6 * Math.Max(0, Math.Sin(Math.PI * (h - 8) / 12.0))) + 0.5 * rng.NextGaussian() + (rain > 0 ? 2.5 : 0));
                double rh = Math.Clamp(95 - 3.2 * (temp - (dayMean - amp)) + (rain > 0 ? 8 : 0) + 2 * rng.NextGaussian(), 40, 100);
                double p = 1010 + 2 * Math.Cos(4 * Math.PI * (h - 10) / 24.0) + 0.5 * rng.NextGaussian();
                list.Add(new WeatherObservation(t, Math.Round(temp, 2), Math.Round(rain, 2), Math.Round(rh, 1), Math.Round(wind, 2), Math.Round(solar, 1),
                    Math.Round(p, 1), Math.Round(cloudiness * 100)));
            }
        }
        return list;
    }

    /// <summary>Clear-sky global horizontal irradiance (W/m²) from solar geometry (simple Haurwitz model).</summary>
    public static double ClearSkySolar(double latitudeDeg, int dayOfYear, double solarHour)
    {
        double decl = 23.45 * Math.Sin(2 * Math.PI * (284 + dayOfYear) / 365.0) * Math.PI / 180;
        double lat = latitudeDeg * Math.PI / 180, ha = (solarHour - 12) * 15 * Math.PI / 180;
        double cosZ = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(ha);
        return cosZ <= 0 ? 0 : 1098 * cosZ * Math.Exp(-0.057 / cosZ);
    }
}

/// <summary>Minimal Open-Meteo forecast client (no API key). https://open-meteo.com</summary>
public sealed class OpenMeteoClient(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; init; } = "https://api.open-meteo.com/v1/forecast";

    /// <summary>Hourly forecast for 1–16 days.</summary>
    public async Task<IReadOnlyList<WeatherObservation>> GetHourlyForecastAsync(double latitude, double longitude, int days = 3, CancellationToken ct = default)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}?latitude={latitude:0.####}&longitude={longitude:0.####}&hourly=temperature_2m,relative_humidity_2m,precipitation,wind_speed_10m,shortwave_radiation,surface_pressure,cloud_cover&wind_speed_unit=ms&timezone=auto&forecast_days={Math.Clamp(days, 1, 16)}");
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    /// <summary>Parses an Open-Meteo JSON response (exposed for tests and offline caches).</summary>
    public static IReadOnlyList<WeatherObservation> Parse(JsonElement root)
    {
        var h = root.GetProperty("hourly");
        var time = h.GetProperty("time");
        double V(string name, int i) => h.TryGetProperty(name, out var a) && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : double.NaN;
        var list = new List<WeatherObservation>(time.GetArrayLength());
        for (int i = 0; i < time.GetArrayLength(); i++)
            list.Add(new WeatherObservation(DateTime.Parse(time[i].GetString()!, CultureInfo.InvariantCulture), V("temperature_2m", i), V("precipitation", i),
                V("relative_humidity_2m", i), V("wind_speed_10m", i), V("shortwave_radiation", i), V("surface_pressure", i), V("cloud_cover", i)));
        return list;
    }
}

public static class EnvironmentBuilderExtensions
{
    public static AquaBuilder AddEnvironment(this AquaBuilder b) => b.TryAddModule("Environment", s =>
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(s, new OpenMeteoClient()));
}
