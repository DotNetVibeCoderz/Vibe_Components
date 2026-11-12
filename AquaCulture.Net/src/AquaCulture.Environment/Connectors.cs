using System.Globalization;
using System.Text.Json;
using AquaCulture.Core;

namespace AquaCulture.Environment;

/// <summary>Location returned by BMKG for an administrative village code (adm4).</summary>
public sealed record BmkgLocation(string Adm4, string Village, string District, string Regency, string Province, double Latitude, double Longitude);

/// <summary>BMKG three-hourly forecast with the location it belongs to.</summary>
public sealed record BmkgForecast(BmkgLocation Location, IReadOnlyList<WeatherObservation> Hourly, IReadOnlyList<(DateTime Time, Text2 Description)> Conditions);

/// <summary>
/// Client for the BMKG public forecast API (Indonesian Meteorology, Climatology and Geophysics Agency):
/// <c>https://api.bmkg.go.id/publik/prakiraan-cuaca?adm4=…</c>. The <c>adm4</c> code is the Kemendagri village code (for example
/// <c>31.71.03.1001</c>). Records are three-hourly; wind is converted from km/h to m/s and shortwave radiation is estimated from
/// cloud cover (BMKG does not publish it). Respect BMKG's terms (attribution, at most 60 requests per minute).
/// </summary>
public sealed class BmkgClient(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; init; } = "https://api.bmkg.go.id/publik/prakiraan-cuaca";

    public async Task<BmkgForecast> GetForecastAsync(string adm4, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{BaseUrl}?adm4={Uri.EscapeDataString(adm4)}", ct).ConfigureAwait(false);
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode || json.Contains("\"error\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"BMKG has no forecast for adm4 '{adm4}' ({(int)resp.StatusCode}).");
        return Parse(json);
    }

    /// <summary>Parses a BMKG forecast document (exposed for tests and offline caches).</summary>
    public static BmkgForecast Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException($"BMKG: {err}");
        if (!root.TryGetProperty("lokasi", out var loc)) throw new InvalidOperationException("BMKG: response has no \"lokasi\".");
        string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        double D(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
        var location = new BmkgLocation(S(loc, "adm4"), S(loc, "desa"), S(loc, "kecamatan"), S(loc, "kotkab"), S(loc, "provinsi"), D(loc, "lat"), D(loc, "lon"));
        var list = new List<WeatherObservation>();
        var conditions = new List<(DateTime, Text2)>();
        foreach (var block in root.GetProperty("data").EnumerateArray())
            foreach (var day in block.GetProperty("cuaca").EnumerateArray())
                foreach (var r in day.EnumerateArray())
                {
                    // local_datetime is the farm's wall-clock time, which is what pond decisions use
                    var t = DateTime.ParseExact(S(r, "local_datetime"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    double cloud = D(r, "tcc");
                    double solar = SyntheticWeather.ClearSkySolar(location.Latitude, t.DayOfYear, t.Hour) * (1 - 0.75 * (double.IsNaN(cloud) ? 0.5 : cloud / 100));
                    list.Add(new WeatherObservation(t, D(r, "t"), D(r, "tp"), D(r, "hu"), D(r, "ws") / 3.6, Math.Round(solar, 1), double.NaN, cloud));
                    conditions.Add((t, new Text2(S(r, "weather_desc_en"), S(r, "weather_desc"))));
                }
        var order = list.Select((w, i) => (w, i)).OrderBy(x => x.w.Time).ToList();
        return new BmkgForecast(location, order.Select(x => x.w).ToList(), order.Select(x => conditions[x.i]).ToList());
    }

    /// <summary>Expands three-hourly records to hourly by linear interpolation (rain is spread evenly), for models that step hourly.</summary>
    public static IReadOnlyList<WeatherObservation> ToHourly(IReadOnlyList<WeatherObservation> threeHourly, double latitude)
    {
        var o = new List<WeatherObservation>();
        for (int i = 0; i < threeHourly.Count; i++)
        {
            var a = threeHourly[i];
            var b = i + 1 < threeHourly.Count ? threeHourly[i + 1] : a;
            int steps = Math.Max(1, (int)Math.Round((b.Time - a.Time).TotalHours));
            if (i + 1 == threeHourly.Count) steps = 1;
            for (int k = 0; k < steps; k++)
            {
                double f = (double)k / steps;
                double L(double x, double y) => double.IsNaN(y) ? x : x + f * (y - x);
                var t = a.Time.AddHours(k);
                o.Add(new WeatherObservation(t, L(a.AirTempC, b.AirTempC), a.RainfallMm / steps, L(a.HumidityPct, b.HumidityPct), L(a.WindMs, b.WindMs),
                    SyntheticWeather.ClearSkySolar(latitude, t.DayOfYear, t.Hour + 0.5) * (1 - 0.75 * (double.IsNaN(a.CloudPct) ? 0.5 : a.CloudPct / 100)), a.PressureHPa, a.CloudPct));
            }
        }
        return o;
    }
}

/// <summary>
/// NASA POWER daily point data (MERRA-2 / CERES, 1981–present, no key) for climate-risk analysis and growth calibration:
/// <c>https://power.larc.nasa.gov/api/temporal/daily/point</c>.
/// </summary>
public sealed class NasaPowerClient(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

    public const string Parameters = "T2M_MAX,T2M_MIN,T2M,PRECTOTCORR,WS2M,ALLSKY_SFC_SW_DWN,RH2M,PS";
    public string BaseUrl { get; init; } = "https://power.larc.nasa.gov/api/temporal/daily/point";

    public async Task<IReadOnlyList<DailyWeather>> GetDailyAsync(double latitude, double longitude, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}?parameters={Parameters}&community=AG&longitude={longitude:0.####}&latitude={latitude:0.####}&start={start:yyyyMMdd}&end={end:yyyyMMdd}&format=JSON");
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"NASA POWER request failed ({(int)resp.StatusCode}): {json[..Math.Min(300, json.Length)]}");
        return Parse(json);
    }

    /// <summary>Parses a POWER daily JSON document; fill values (−999) become NaN and wind at 2 m is used as the mean and max wind.</summary>
    public static IReadOnlyList<DailyWeather> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement.GetProperty("properties").GetProperty("parameter");
        double fill = doc.RootElement.TryGetProperty("header", out var h) && h.TryGetProperty("fill_value", out var fv) ? fv.GetDouble() : -999;
        double V(string name, string day) => p.TryGetProperty(name, out var a) && a.TryGetProperty(day, out var v) && v.GetDouble() is var d && d != fill ? d : double.NaN;
        var list = new List<DailyWeather>();
        foreach (var day in p.GetProperty("T2M").EnumerateObject())
        {
            var date = DateOnly.ParseExact(day.Name, "yyyyMMdd", CultureInfo.InvariantCulture);
            double ws = V("WS2M", day.Name), ps = V("PS", day.Name);
            list.Add(new DailyWeather(date, V("T2M_MIN", day.Name), V("T2M_MAX", day.Name), V("T2M", day.Name), V("PRECTOTCORR", day.Name),
                ws, ws * 1.8, V("ALLSKY_SFC_SW_DWN", day.Name), V("RH2M", day.Name), ps * 10));
        }
        return list.OrderBy(d => d.Date).ToList();
    }
}

/// <summary>Hourly sea state at a marine site (floating net cages, coastal intakes).</summary>
public sealed record MarineObservation(DateTime Time, double WaveHeightM, double WavePeriodS, double SeaSurfaceTempC, double CurrentMs);

/// <summary>Sea-state risk for marine cages.</summary>
public sealed record MarineRisk(double MaxWaveM, double MaxCurrentMs, double MeanSstC, IReadOnlyList<WeatherImpactItem> Impacts);

/// <summary>Open-Meteo Marine forecast (waves, sea-surface temperature, currents; no key) and a cage-oriented risk summary.</summary>
public sealed class MarineForecastClient(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; init; } = "https://marine-api.open-meteo.com/v1/marine";

    public async Task<IReadOnlyList<MarineObservation>> GetHourlyAsync(double latitude, double longitude, int days = 3, CancellationToken ct = default)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}?latitude={latitude:0.####}&longitude={longitude:0.####}&hourly=wave_height,wave_period,sea_surface_temperature,ocean_current_velocity&timezone=auto&forecast_days={Math.Clamp(days, 1, 16)}");
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Parses an Open-Meteo Marine response; current velocity is converted from km/h to m/s.</summary>
    public static IReadOnlyList<MarineObservation> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var h = doc.RootElement.GetProperty("hourly");
        var time = h.GetProperty("time");
        double V(string name, int i) => h.TryGetProperty(name, out var a) && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : double.NaN;
        var list = new List<MarineObservation>(time.GetArrayLength());
        for (int i = 0; i < time.GetArrayLength(); i++)
            list.Add(new MarineObservation(DateTime.Parse(time[i].GetString()!, CultureInfo.InvariantCulture), V("wave_height", i), V("wave_period", i),
                V("sea_surface_temperature", i), V("ocean_current_velocity", i) / 3.6));
        return list;
    }

    /// <summary>
    /// Cage risk: significant wave height above 1.5 m stresses moorings and nets (above 2.5 m is severe for most HDPE cages),
    /// currents above 0.5 m/s deform nets and cut cage volume, and SST outside the species' optimum signals heat or cold stress.
    /// </summary>
    public static MarineRisk Assess(IReadOnlyList<MarineObservation> sea, Core.Species.SpeciesProfile species)
    {
        double wave = sea.Where(x => double.IsFinite(x.WaveHeightM)).Select(x => x.WaveHeightM).DefaultIfEmpty(0).Max();
        double cur = sea.Where(x => double.IsFinite(x.CurrentMs)).Select(x => x.CurrentMs).DefaultIfEmpty(0).Max();
        double sst = sea.Where(x => double.IsFinite(x.SeaSurfaceTempC)).Select(x => x.SeaSurfaceTempC).DefaultIfEmpty(double.NaN).Average();
        var items = new List<WeatherImpactItem>();
        if (wave > 1.5)
            items.Add(new("waves", wave > 2.5 ? ImpactSeverity.Severe : ImpactSeverity.High,
                new($"Waves up to {wave:0.0} m", $"Gelombang hingga {wave:0.0} m"),
                new("Check moorings, frames and nets; stop feeding during peaks; plan a transfer if waves keep rising",
                    "Periksa tambat, rangka dan jaring; hentikan pakan saat puncak; rencanakan pemindahan bila gelombang terus naik")));
        if (cur > 0.5)
            items.Add(new("current", cur > 1 ? ImpactSeverity.High : ImpactSeverity.Moderate,
                new($"Currents up to {cur:0.00} m/s deform the nets", $"Arus hingga {cur:0.00} m/s mengubah bentuk jaring"),
                new("Add net weights and reduce stocking density per cage", "Tambah pemberat jaring dan kurangi kepadatan per kurungan")));
        if (double.IsFinite(sst) && !species.Temperature.IsOptimal(sst))
            items.Add(new("sst", species.Temperature.IsTolerable(sst) ? ImpactSeverity.Moderate : ImpactSeverity.High,
                new($"Sea-surface temperature {sst:0.0} °C is outside the optimum for {species.Name.En}", $"Suhu permukaan laut {sst:0.0} °C di luar optimum {species.Name.Id}"),
                new("Feed in the cooler hours and watch appetite", "Beri pakan pada jam sejuk dan amati nafsu makan")));
        return new MarineRisk(Math.Round(wave, 2), Math.Round(cur, 2), Math.Round(sst, 2), items);
    }
}
