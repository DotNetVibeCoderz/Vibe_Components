using System.Globalization;
using System.Text.Json;
using Agritech.Core.Data;
using Agritech.Core.Geo;

namespace Agritech.Weather;

/// <summary>Source of historical and forecast daily weather.</summary>
public interface IWeatherProvider
{
    string Name { get; }
    Task<IReadOnlyList<DailyWeather>> GetForecastAsync(GeoCoordinate location, int days = 7, CancellationToken ct = default);
    Task<IReadOnlyList<DailyWeather>> GetHistoricalAsync(GeoCoordinate location, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>
/// Open-Meteo (https://open-meteo.com) provider — free, no API key. Forecast up to 16 days and ERA5 archive history.
/// </summary>
public sealed class OpenMeteoWeatherProvider(HttpClient http) : IWeatherProvider
{
    private const string Daily = "temperature_2m_max,temperature_2m_min,precipitation_sum,relative_humidity_2m_mean,wind_speed_10m_max,shortwave_radiation_sum,et0_fao_evapotranspiration";

    public OpenMeteoWeatherProvider() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) { }

    public string Name => "Open-Meteo";

    public Task<IReadOnlyList<DailyWeather>> GetForecastAsync(GeoCoordinate location, int days = 7, CancellationToken ct = default) =>
        FetchAsync($"https://api.open-meteo.com/v1/forecast?{Coords(location)}&daily={Daily}&forecast_days={Math.Clamp(days, 1, 16)}&timezone=auto&wind_speed_unit=ms", ct);

    public Task<IReadOnlyList<DailyWeather>> GetHistoricalAsync(GeoCoordinate location, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        FetchAsync($"https://archive-api.open-meteo.com/v1/archive?{Coords(location)}&daily={Daily}&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}&timezone=auto&wind_speed_unit=ms", ct);

    private static string Coords(GeoCoordinate c) =>
        string.Create(CultureInfo.InvariantCulture, $"latitude={c.Latitude:F4}&longitude={c.Longitude:F4}");

    private async Task<IReadOnlyList<DailyWeather>> FetchAsync(string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    /// <summary>Parses an Open-Meteo "daily" JSON payload.</summary>
    public static IReadOnlyList<DailyWeather> Parse(JsonElement root)
    {
        var d = root.GetProperty("daily");
        var time = d.GetProperty("time");
        int n = time.GetArrayLength();
        var res = new List<DailyWeather>(n);
        for (int i = 0; i < n; i++)
        {
            double? Get(string name) => d.TryGetProperty(name, out var arr) && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : null;
            var tmax = Get("temperature_2m_max"); var tmin = Get("temperature_2m_min");
            if (tmax is null || tmin is null) continue;
            var wind10 = Get("wind_speed_10m_max");
            res.Add(new DailyWeather(
                DateOnly.ParseExact(time[i].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                tmin.Value, tmax.Value, Get("precipitation_sum") ?? 0,
                Get("relative_humidity_2m_mean"),
                // daily max wind at 10 m → approx. mean wind at 2 m (log profile × 0.6 max→mean factor)
                wind10 is { } w ? Core.Units.WindAt2m(w, 10) * 0.6 : null,
                Get("shortwave_radiation_sum"),
                Get("et0_fao_evapotranspiration")));
        }
        return res;
    }
}

/// <summary>
/// Stochastic weather generator (WGEN-style): first-order Markov chain wet/dry occurrence, gamma rainfall amounts and
/// sinusoidal seasonal temperature with noise. Deterministic for a seed — ideal for demos, tests and scenario runs.
/// </summary>
public sealed class SyntheticWeatherProvider(int seed = 42, double meanTempC = 27, double tempAmplitudeC = 1.5, double annualRainMm = 2200,
    double pWetAfterWet = 0.7, double pWetAfterDry = 0.3) : IWeatherProvider
{
    public string Name => "Synthetic (WGEN)";

    public Task<IReadOnlyList<DailyWeather>> GetForecastAsync(GeoCoordinate location, int days = 7, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return GetHistoricalAsync(location, today, today.AddDays(days - 1), ct);
    }

    public Task<IReadOnlyList<DailyWeather>> GetHistoricalAsync(GeoCoordinate location, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult(Generate(location, from, to));

    public IReadOnlyList<DailyWeather> Generate(GeoCoordinate location, DateOnly from, DateOnly to)
    {
        // Stable across processes (HashCode.Combine is randomised per process, so it must not be used for seeding).
        var rng = new Random(unchecked(seed * 1_000_003 ^ from.DayNumber * 7_919 ^ (int)(location.Latitude * 1000) * 31 ^ (int)(location.Longitude * 1000) * 131));
        double pWet = pWetAfterDry / (1 - pWetAfterWet + pWetAfterDry);
        double meanWetDay = annualRainMm / (365 * pWet);
        const double shape = 0.75;
        bool wet = false;
        var list = new List<DailyWeather>();
        // Southern hemisphere tropics (e.g. Java): wet season Dec–Feb peaks.
        double phase = location.Latitude < 0 ? 15 : 196;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            double seasonal = Math.Cos(2 * Math.PI * (d.DayOfYear - phase) / 365.0);
            double pw = wet ? pWetAfterWet : pWetAfterDry;
            pw = Math.Clamp(pw * (1 + 0.35 * seasonal), 0.02, 0.98);
            wet = rng.NextDouble() < pw;
            double rain = wet ? rng.NextGamma(shape, meanWetDay * (1 + 0.4 * seasonal) / shape) : 0;
            double tmean = meanTempC - tempAmplitudeC * seasonal * (location.Latitude < 0 ? -1 : 1) + rng.NextGaussian(0, 0.8) - (wet ? 0.6 : 0);
            double range = (wet ? 6.5 : 9.5) + rng.NextGaussian(0, 1);
            double rh = Math.Clamp((wet ? 88 : 76) + rng.NextGaussian(0, 4), 35, 100);
            double wind = Math.Max(0.3, 1.8 + rng.NextGaussian(0, 0.5));
            double ra = Evapotranspiration.ExtraterrestrialRadiation(location.Latitude, d.DayOfYear);
            double rs = Math.Clamp(ra * (wet ? 0.38 : 0.55) + rng.NextGaussian(0, 1.5), ra * 0.2, ra * 0.75);
            list.Add(new DailyWeather(d, Math.Round(tmean - range / 2, 1), Math.Round(tmean + range / 2, 1), Math.Round(rain, 1),
                Math.Round(rh, 0), Math.Round(wind, 2), Math.Round(rs, 2)));
        }
        return list;
    }
}
