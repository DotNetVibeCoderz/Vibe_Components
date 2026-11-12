using AquaCulture.AI.Learning;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.Growth;
using AquaCulture.Hydrodynamic;
using AquaCulture.IoT;
using AquaCulture.Traceability;
using AquaCulture.WaterQuality;
using BenchmarkDotNet.Attributes;

namespace AquaCulture.Benchmarks;

/// <summary>The models a farm server or edge gateway runs on every reading, every hour or every day.</summary>
[MemoryDiagnoser]
public class ModelBenchmarks
{
    private IReadOnlyList<WeatherObservation> _weather = [];
    private OxygenBudgetParameters _pond = null!;
    private WaterSample _sample = null!;
    private SensorReading[] _readings = [];
    private GbtDoForecaster _gbt = null!;
    private IReadOnlyList<DoObservation> _recent = [];
    private IReadOnlyList<EpcisEvent> _events = [];

    [GlobalSetup]
    public void Setup()
    {
        _weather = SyntheticWeather.Generate(-6.3, new DateTime(2026, 7, 1), 2, 3);
        _pond = new OxygenBudgetParameters
        {
            VolumeM3 = 2600, DepthM = 1.3, BiomassKg = 3000, MeanWeightG = 12, Group = SpeciesGroup.Shrimp, ChlorophyllUgL = 120, SalinityPpt = 20, AeratorKw = 6,
        };
        _sample = new WaterSample { TemperatureC = 29, DissolvedOxygen = 4.2, Ph = 8.1, SalinityPpt = 20, Ammonia = 0.6, Nitrite = 0.3, Alkalinity = 130 };

        var rng = new Core.Data.DeterministicRandom(11);
        var t0 = new DateTime(2026, 7, 1);
        _readings = Enumerable.Range(0, EdgeReadings)
            .Select(i => new SensorReading("probe-" + i % 4, "P-0" + i % 4, SensorKind.DissolvedOxygen, 5 + rng.NextGaussian() * 0.3 + (i % 97 == 0 ? 8 : 0), t0.AddMinutes(i)))
            .ToArray();

        var history = DoHistory.Simulate(days: 20, seed: 5);
        _gbt = new GbtDoForecaster(horizon: 12, trees: 60).Train(history);
        _recent = history.Skip(history.Count - 48).ToList();

        var c = new CultureCycle { Id = "C1", PondId = "P-03", Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 6, 1), StockedCount = 150_000, StockingWeightG = 0.01 };
        for (int d = 0; d < 90; d++) c.LogFeed(new DateOnly(2026, 6, 1).AddDays(d), 20 + d * 10, d < 30 ? "STARTER-A" : "GROWER-B");
        c.LogHarvest(new DateOnly(2026, 8, 30), 1500, 70_000, 75_000);
        var farm = new Farm { Id = "TMS", Name = "Tambak" };
        farm.Cycles.Add(c);
        var setup = new TraceabilitySetup(Gs1.WithCheckDigit("899999900001"), Gs1.WithCheckDigit("899999900002"), Gs1.WithCheckDigit("899999910001"),
            Gs1.WithCheckDigit("899999910002"), "8999999") { DefaultFeedGtin = Gs1.WithCheckDigit("899999920002") };
        _events = FarmEpcis.CycleEvents(farm, c, setup, "PL10-2605-A");
    }

    private const int EdgeReadings = 1000;

    /// <summary>48 hours of the hourly pond oxygen budget with set-point aeration.</summary>
    [Benchmark]
    public double OxygenBudget48h() => PondOxygenBudget.Simulate(_pond, _weather, 6, PondOxygenBudget.SetPoint(4.5)).MinDo;

    /// <summary>A 120-day TGC growth run (daily steps with environmental modifiers and mortality).</summary>
    [Benchmark]
    public double Growth120d() =>
        new GrowthSimulator(SpeciesCatalog.Vaname).Simulate(new StockState(SpeciesCatalog.Vaname, new DateOnly(2026, 1, 1), 0.01, 150_000, 2600), 120,
            new DailyEnvironment(29, 5.5, 0.02, 20)).Final.AbwG;

    /// <summary>Full water-quality assessment of one sample (NH₃, DO saturation, WQI, issues).</summary>
    [Benchmark]
    public double WaterQualityAssess() => WaterQualityIndex.Assess(_sample, SpeciesCatalog.Vaname).Wqi;

    /// <summary>1 000 readings through the edge filter (Hampel outliers, stuck sensors, smoothing): the gateway hot path.</summary>
    [Benchmark(OperationsPerInvoke = EdgeReadings)]
    public int EdgeAnalytics1k()
    {
        var edge = new EdgeAnalytics();
        int outliers = 0;
        foreach (var r in _readings) if (edge.Process(r).IsOutlier) outliers++;
        return outliers;
    }

    /// <summary>A 12-hour learned DO forecast (one FastTree per horizon).</summary>
    [Benchmark]
    public double GbtDoForecast12h() => _gbt.Forecast(_recent).Minimum.Do;

    /// <summary>EPCIS 2.0 JSON-LD document for one culture cycle (90 feed logs).</summary>
    [Benchmark]
    public int EpcisDocument() => EpcisJson.ToDocument(_events, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)).Length;
}
