using System.Reflection;
using AquaCulture.AI.Learning;
using AquaCulture.Core;
using AquaCulture.AI.Rl;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Species;
using AquaCulture.Data;
using AquaCulture.Environment;
using AquaCulture.GIS;
using AquaCulture.IoT;
using AquaCulture.Visualization;
using Microsoft.Data.Sqlite;

namespace AquaCulture.Gallery.Shared;

// ───────────────────────────── data & connectors ─────────────────────────────

public sealed record ConnectorsDemoResult(
    BmkgForecast Bmkg, WeatherImpactReport BmkgImpact, string BmkgSvg,
    IReadOnlyList<DailyWeather> NasaDays, string NasaSvg,
    MarineRisk Marine, string MarineSvg, string MarineSpecies,
    IReadOnlyList<SensorReading> ChirpStack,
    int StoredReadings, int QueriedReadings, string StoreSvg, long ParquetBytes, long CsvBytes, string CsvHead);

/// <summary>
/// v1.1 connectors on recorded responses (BMKG Kemayoran, NASA POWER Indramayu January 2025, Open-Meteo Marine Sunda Strait), a ChirpStack
/// uplink, and a SQLite store holding a simulated day of two ponds exported to Parquet and CSV. The CLI (<c>aqua bmkg</c>, <c>history</c>,
/// <c>marine</c>, <c>export</c>) runs the same code against the live services.
/// </summary>
public static class ConnectorsDemo
{
    public static string Fixture(string name)
    {
        using var s = typeof(ConnectorsDemo).Assembly.GetManifestResourceStream($"AquaCulture.Gallery.Shared.Fixtures.{name}")
                      ?? throw new FileNotFoundException(name);
        return new StreamReader(s).ReadToEnd();
    }

    public static ConnectorsDemoResult Run(string marineSpecies = "kerapu", double pondDepthM = 1.3)
    {
        var bmkg = BmkgClient.Parse(Fixture("bmkg-kemayoran.json"));
        var hourly = BmkgClient.ToHourly(bmkg.Hourly, bmkg.Location.Latitude);
        var impact = WeatherImpactPredictor.Predict(hourly, new PondExposure(pondDepthM, 0));
        var bmkgSvg = SvgCharts.Line(
        [
            new Series("Air °C", hourly.Select((h, i) => ((double)i, h.AirTempC)).ToList(), "#d9a13b"),
            new Series("Cloud %/4", hourly.Select((h, i) => ((double)i, double.IsNaN(h.CloudPct) ? 0 : h.CloudPct / 4)).ToList(), "#7d8b93", Dashed: true),
            new Series("Solar W/m²/40", hourly.Select((h, i) => ((double)i, h.SolarWm2 / 40)).ToList(), "#e9b44c", Area: true),
        ], new ChartOptions { Height = 220, XFormat = h => $"+{h:0}h" });

        var nasa = NasaPowerClient.Parse(Fixture("nasa-power-indramayu-2025-01.json"));
        var nasaSvg = SvgCharts.Line(
        [
            new Series("Tmax", nasa.Select((d, i) => ((double)i + 1, d.TmaxC)).ToList(), "#e0614a"),
            new Series("Tmin", nasa.Select((d, i) => ((double)i + 1, d.TminC)).ToList(), "#2f6fb0"),
            new Series("Rain mm/3", nasa.Select((d, i) => ((double)i + 1, d.RainMm / 3)).ToList(), "#12a4b6", Area: true),
        ], new ChartOptions { Height = 220, XFormat = d => $"{d:0} Jan" });

        var sea = MarineForecastClient.Parse(Fixture("open-meteo-marine.json"));
        var marine = MarineForecastClient.Assess(sea, SpeciesCatalog.Get(marineSpecies));
        var marineSvg = SvgCharts.Line(
        [
            new Series("Wave m", sea.Select((s, i) => ((double)i, s.WaveHeightM)).ToList(), "#2f6fb0", Area: true),
            new Series("Current m/s", sea.Select((s, i) => ((double)i, s.CurrentMs)).ToList(), "#12a4b6"),
        ], new ChartOptions { Height = 200, XFormat = h => $"+{h:0}h", References = [new Reference(1.5, "1.5 m")] });

        var payload = Convert.ToBase64String(LoRaPayload.EncodePond(4.4, 29.8, 7.9, 17, 0.42, 3.62));
        var chirp = ChirpStack.ParseUplink($$$"""{"time":"2026-11-12T11:00:00Z","deviceInfo":{"devEui":"a84041000181c061","deviceName":"probe-P03","tags":{"pond":"P-03"}},"data":"{{{payload}}}"}""");

        // SQLite store with a simulated day of two ponds, then Parquet / CSV export
        var path = Path.Combine(Path.GetTempPath(), $"aqua-gallery-{Guid.NewGuid():N}.db");
        int stored, queried; long parquet, csvBytes; string csvHead, storeSvg;
        try
        {
            using (var store = AquaStore.OpenSqlite(path))
            {
                var sim = new SimulatedSensorTransport([new SimulatedPond("P-01", "probe-P01", MeanDo: 5.8), new SimulatedPond("P-02", "probe-P02", MeanDo: 4.6, DoAmplitude: 2.6)]) { DropoutProbability = 0 };
                var t0 = DemoFarm.Now.Date.AddDays(-1);
                var rows = Enumerable.Range(0, 144).SelectMany(i => sim.Emit(DateTime.SpecifyKind(t0.AddMinutes(10 * i), DateTimeKind.Utc))).ToList();
                stored = store.AppendReadingsAsync(rows).GetAwaiter().GetResult();
                var p1 = store.SeriesAsync("P-01", SensorKind.DissolvedOxygen, DateTime.MinValue, DateTime.MaxValue).GetAwaiter().GetResult();
                var p2 = store.SeriesAsync("P-02", SensorKind.DissolvedOxygen, DateTime.MinValue, DateTime.MaxValue).GetAwaiter().GetResult();
                var all = store.QueryReadingsAsync("P-02").GetAwaiter().GetResult();
                queried = all.Count;
                storeSvg = SvgCharts.Line(
                [
                    new Series("P-01 DO", p1.Points.Select((p, i) => (i / 6.0, p.Value)).ToList(), "#12a4b6"),
                    new Series("P-02 DO", p2.Points.Select((p, i) => (i / 6.0, p.Value)).ToList(), "#e0614a"),
                ], new ChartOptions { Height = 210, XFormat = h => $"{h:00}:00", References = [new Reference(3, "3 mg/L")] });
                using var ms = new MemoryStream();
                TimeSeriesExport.WriteParquetAsync(all, ms).GetAwaiter().GetResult();
                parquet = ms.Length;
                var csv = TimeSeriesExport.WriteCsv(all);
                csvBytes = System.Text.Encoding.UTF8.GetByteCount(csv);
                csvHead = string.Join("\n", csv.Split("\r\n").Take(4));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
        return new ConnectorsDemoResult(bmkg, impact, bmkgSvg, nasa, nasaSvg, marine, marineSvg, marineSpecies, chirp, stored, queried, storeSvg, parquet, csvBytes, csvHead);
    }
}

// ───────────────────────────── chlorophyll map ─────────────────────────────

public sealed record CageZoneStats(string Zone, double MeanChl, double MaxChl, string Trophic);

public sealed record ChlorophyllDemoResult(byte[] ChlPng, byte[] NdwiPng, string ZonesSvg, int Width, int Height, WaterStats Stats, Text2 Trophic,
    IReadOnlyList<CageZoneStats> Zones, double WaterAreaHa, string LegendSvg);

/// <summary>
/// Sentinel-2 water colour on a synthetic reservoir with floating-net-cage (KJA) blocks: bands are generated from a chlorophyll field
/// (feed nutrients raise chlorophyll around the cages), then <see cref="WaterColour"/> recovers NDWI, the water mask, NDCI and chlorophyll-a.
/// Real scenes go through <c>GdalRaster.ReadAllBands</c> the same way.
/// </summary>
public static class ChlorophyllDemo
{
    public static readonly GeoBounds Reservoir = new(-6.560, 107.330, -6.500, 107.410);   // around Jatiluhur, West Java

    public static ChlorophyllDemoResult Run(double cageLoad = 1.0, double background = 12, ulong seed = 4)
    {
        const int w = 96, h = 72;
        var rng = new Core.Data.DeterministicRandom(seed);
        (double cx, double cy, double r)[] cages = [(0.30, 0.42, 0.06), (0.58, 0.55, 0.05), (0.72, 0.30, 0.045)];
        var green = new float[w * h]; var red = new float[w * h]; var redEdge = new float[w * h]; var nir = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)w, v = y / (double)h;
                // irregular reservoir shoreline
                double shore = 0.36 + 0.06 * Math.Sin(7 * Math.Atan2(v - 0.5, u - 0.5)) + 0.03 * Math.Sin(13 * u);
                bool water = Math.Sqrt((u - 0.5) * (u - 0.5) / 1.4 + (v - 0.5) * (v - 0.5)) < shore;
                int i = y * w + x;
                double noise = 0.004 * rng.NextGaussian();
                if (!water)
                {
                    green[i] = (float)(0.07 + noise); red[i] = (float)(0.06 + noise); redEdge[i] = (float)(0.14 + noise); nir[i] = (float)(0.28 + 2 * noise);
                    continue;
                }
                double chl = background;
                foreach (var (cx, cy, r) in cages)
                {
                    double d = Math.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy));
                    chl += cageLoad * 70 * Math.Exp(-d * d / (2 * r * r * 2.2));
                }
                chl *= Math.Exp(0.08 * rng.NextGaussian());
                // invert Mishra & Mishra: 194.325 n² + 86.115 n + 14.039 = chl
                double disc = 86.115 * 86.115 - 4 * 194.325 * (14.039 - chl);
                double n = disc <= 0 ? -0.08 : (-86.115 + Math.Sqrt(disc)) / (2 * 194.325);
                double r665 = 0.028 + 0.004 * rng.NextGaussian();
                red[i] = (float)r665; redEdge[i] = (float)(r665 * (1 + n) / (1 - n));
                green[i] = (float)(0.05 + 0.0004 * chl + noise); nir[i] = (float)(0.012 + 0.0002 * chl + noise / 2);
            }
        GeoRaster G(float[] d) => new(Reservoir, w, h, d);
        var ndwi = WaterColour.Ndwi(G(green), G(nir));
        var mask = WaterColour.WaterMask(ndwi, 0.1);
        var chlR = WaterColour.Chlorophyll(WaterColour.Ndci(G(redEdge), G(red)), mask);
        var stats = WaterColour.Summarize(chlR, mask);
        var ramp = new ColorRamp("Chlorophyll", 0, 80, (0, "#0b4f6c"), (0.2, "#1fa3a8"), (0.45, "#7cc6a4"), (0.7, "#c9d98a"), (0.85, "#e9b44c"), (1, "#8a6a1f"));
        var chlPng = PngEncoder.Raster(chlR.Data, w, h, ramp, 6);
        var ndwiPng = PngEncoder.Raster(ndwi.Data, w, h, new ColorRamp("NDWI", -0.6, 0.8, (0, "#8a6a4a"), (0.45, "#d9c38f"), (0.55, "#9fd3d0"), (1, "#0b4f6c")), 6);

        var zones = new List<CageZoneStats>();
        var svg = new System.Text.StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w * 6} {h * 6}\" font-family=\"inherit\">");
        foreach (var (k, (cx, cy, r)) in cages.Select((c, k) => (k, c)))
        {
            var vals = new List<double>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double u = x / (double)w, v = y / (double)h;
                    if (Math.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) < r * 1.6 && !float.IsNaN(chlR.Data[y * w + x])) vals.Add(chlR.Data[y * w + x]);
                }
            string name = $"KJA-{k + 1}";
            double mean = vals.Count == 0 ? double.NaN : vals.Average();
            zones.Add(new CageZoneStats(name, Math.Round(mean, 1), Math.Round(vals.DefaultIfEmpty(double.NaN).Max(), 1), WaterColour.TrophicState(mean).En));
            double px = cx * w * 6, py = cy * h * 6, rr = r * 1.6 * w * 6;
            svg.Append(System.FormattableString.Invariant($"<circle cx=\"{px:0}\" cy=\"{py:0}\" r=\"{rr:0}\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2\" stroke-dasharray=\"6 4\"/>"));
            svg.Append(System.FormattableString.Invariant($"<rect x=\"{px - 9:0}\" y=\"{py - 6:0}\" width=\"18\" height=\"12\" fill=\"#0b2e36\" stroke=\"#fff\"/>"));
            double ly = Math.Max(16, py - rr - 6);
            svg.Append(System.FormattableString.Invariant($"<rect x=\"{px - 26:0}\" y=\"{ly - 13:0}\" width=\"52\" height=\"18\" rx=\"4\" fill=\"#0b2e36\" opacity=\"0.85\"/>"));
            svg.Append(System.FormattableString.Invariant($"<text x=\"{px:0}\" y=\"{ly:0}\" text-anchor=\"middle\" font-size=\"13\" fill=\"#fff\" font-weight=\"bold\">{name}</text>"));
        }
        svg.Append("</svg>");
        var legend = SvgCharts.Bars(zones.Select(z => (z.Zone, z.MeanChl)).Prepend(("Reservoir", Math.Round(stats.Median, 1))).ToList(),
            new ChartOptions { Height = 190, Title = "Chl-a µg/L", YFormat = "0" }, ["#1fa3a8", "#e9b44c", "#e9b44c", "#e9b44c"]);
        return new ChlorophyllDemoResult(chlPng, ndwiPng, svg.ToString(), w * 6, h * 6, stats, WaterColour.TrophicState(stats.Median), zones, Math.Round(stats.AreaHa), legend);
    }
}

// ───────────────────────────── learning: DO forecast & RL ─────────────────────────────

public sealed record LearningDemoResult(string ForecastSvg, string SkillSvg, IReadOnlyList<DoSkill> Skill, DoForecastCurve Curve,
    IReadOnlyList<AerationEvaluation> Aeration, double TrainSeconds);

/// <summary>
/// v1.2 learning on a pond's own data: a gradient-boosted DO forecaster trained on a simulated 30-day history (its skill against
/// persistence and same-hour-yesterday is shown honestly), and the aeration environment's farmer baselines. <see cref="TrainSac"/> adds a
/// reinforcement-learning policy on demand (about 30 s).
/// </summary>
public static class LearningDemo
{
    public static LearningDemoResult Run(int days = 30, ulong seed = 5, int horizon = 12, double biomassEndKg = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var history = DoHistory.Simulate(days, seed, biomassEndKg: biomassEndKg);
        var gbt = new GbtDoForecaster(horizon, trees: 80).Train(history);
        double secs = sw.Elapsed.TotalSeconds;
        var curve = gbt.Forecast(history);
        var past = history.Skip(history.Count - 36).ToList();
        int n0 = past.Count;
        var svg = SvgCharts.Line(
        [
            new Series("Observed DO", past.Select((o, i) => ((double)i - n0 + 1, o.Do)).ToList(), "#12a4b6"),
            new Series("Forecast", curve.Point.Select((v, i) => ((double)i + 1, v)).ToList(), "#e0614a"),
            new Series("90 % low", curve.Lower.Select((v, i) => ((double)i + 1, v)).ToList(), "#e9b44c", Dashed: true),
            new Series("90 % high", curve.Upper.Select((v, i) => ((double)i + 1, v)).ToList(), "#e9b44c", Dashed: true),
        ], new ChartOptions { Height = 240, XFormat = h => h <= 0 ? $"{h:0}h" : $"+{h:0}h", References = [new Reference(SpeciesCatalog.Vaname.DoCritical, "critical")] });
        var skillSvg = SvgCharts.Line(
        [
            new Series("GBT", gbt.Skill.Select(s => ((double)s.HorizonHours, s.Rmse)).ToList(), "#12a4b6"),
            new Series("Persistence", gbt.Skill.Select(s => ((double)s.HorizonHours, s.Persistence)).ToList(), "#7d8b93", Dashed: true),
            new Series("Same hour yesterday", gbt.Skill.Select(s => ((double)s.HorizonHours, s.Diurnal)).ToList(), "#e9b44c", Dashed: true),
        ], new ChartOptions { Height = 200, XFormat = h => $"+{h:0}h", YLabel = "RMSE mg/L" });
        var aeration = new List<AerationEvaluation>
        {
            AerationEnvironment.Evaluate("Always on", AerationEnvironment.AlwaysOn, 6),
            AerationEnvironment.Evaluate("Night timer 22–06", AerationEnvironment.NightTimer, 6),
            AerationEnvironment.Evaluate("Set point 4 mg/L", AerationEnvironment.SetPoint(4), 6),
            AerationEnvironment.Evaluate("Set point 5 mg/L", AerationEnvironment.SetPoint(5), 6),
        };
        return new LearningDemoResult(svg, skillSvg, gbt.Skill, curve, aeration, Math.Round(secs, 1));
    }

    /// <summary>Trains a SAC aeration policy (RLNet) and evaluates it on the same episodes as the baselines.</summary>
    public static AerationEvaluation TrainSac(int steps = 8_000) =>
        AerationEnvironment.Evaluate("SAC (RLNet)", AerationPolicy.Train(steps).Decide, 6);
}
