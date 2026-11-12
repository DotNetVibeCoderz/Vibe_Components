using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;
using AquaCulture.Native;
using AquaCulture.WaterQuality;
using Microsoft.Extensions.DependencyInjection;

namespace AquaCulture.Tests;

public class CoreTests
{
    [Fact]
    public void Catalog_has_the_ten_required_species()
    {
        string[] ids = ["nila", "lele", "gurame", "patin", "koi", "bandeng", "kerapu", "kakap", "vaname", "windu"];
        foreach (var id in ids) Assert.True(SpeciesCatalog.TryGet(id, out _), id);
        Assert.Equal("vaname", SpeciesCatalog.Get("Udang vaname").Id);
        Assert.Equal("nila", SpeciesCatalog.Get("Oreochromis niloticus").Id);
    }

    [Fact]
    public void Species_profiles_are_consistent()
    {
        foreach (var s in SpeciesCatalog.All)
        {
            Assert.True(s.Temperature.Min < s.Temperature.OptLow && s.Temperature.OptHigh < s.Temperature.Max, s.Id);
            Assert.True(s.DoCritical < s.DoOptimal, s.Id);
            Assert.True(s.MarketWeightG > s.StockingWeightG, s.Id);
            Assert.InRange(s.TypicalSurvival, 0.5, 1);
            // feeding rate decreases with size
            Assert.True(s.FeedingRatePercent(s.StockingWeightG) > s.FeedingRatePercent(s.MarketWeightG), s.Id);
            // length–weight round trip
            Assert.Equal(100, s.WeightFromLength(s.LengthFromWeight(100)), 6);
        }
    }

    [Fact]
    public void Deterministic_random_is_stable_across_processes()
    {
        var a = new DeterministicRandom("aquaculture");
        var b = new DeterministicRandom("aquaculture");
        for (int i = 0; i < 50; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());
        // pinned value: guards against accidental algorithm changes (managed and Rust kernels must agree)
        Assert.Equal(1546998764402558742UL, new DeterministicRandom(42).NextUInt64());
    }

    [Fact]
    public void Stats_and_regression()
    {
        double[] x = [1, 2, 3, 4, 5], y = [2.1, 3.9, 6.2, 7.8, 10.1];
        var fit = Stats.LinearRegression(x, y);
        Assert.Equal(1.99, fit.Slope, 2);
        Assert.True(fit.RSquared > 0.99);
        Assert.Equal(15, Stats.Sum(x));
        Assert.Equal(3, Stats.Median(x));
        var s = Stats.Summarize([1, double.NaN, 3]);
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Nelder_mead_finds_rosenbrock_minimum()
    {
        var r = Numerics.NelderMead(p => Math.Pow(1 - p[0], 2) + 100 * Math.Pow(p[1] - p[0] * p[0], 2), [-1.2, 1], maxIter: 5000, tol: 1e-14);
        Assert.Equal(1, r.Parameters[0], 2);
        Assert.Equal(1, r.Parameters[1], 2);
    }

    [Fact]
    public void Geo_area_and_containment()
    {
        var c = new GeoCoordinate(-6.3, 107.3);
        var rect = GeoPolygon.Rectangle(c, 50, 40);
        Assert.Equal(2000, rect.AreaM2, 0);
        Assert.True(rect.Contains(c));
        Assert.False(rect.Contains(GeoMath.Destination(c, 100, 90)));
        Assert.Equal(1000, GeoMath.Haversine(c, GeoMath.Destination(c, 1000, 45)), 0);
    }

    [Fact]
    public void Culture_cycle_bookkeeping()
    {
        var cyc = new CultureCycle { Id = "c1", PondId = "p1", Species = SpeciesCatalog.Nila, StockingDate = new(2026, 1, 1), StockedCount = 10_000, StockingWeightG = 5 };
        cyc.LogMortality(new(2026, 1, 10), 500).LogSampling(new(2026, 2, 1), 60).LogFeed(new(2026, 1, 15), 100);
        Assert.Equal(9_500, cyc.EstimatedPopulation);
        Assert.Equal(570, cyc.EstimatedBiomassKg, 6);
        Assert.Equal(31, cyc.DayOfCulture(new(2026, 2, 1)));
    }

    [Fact]
    public void Dependency_injection_registers_modules_once()
    {
        var services = new ServiceCollection();
        services.AddAquaCulture(b => b.AddWaterQuality().AddWaterQuality());
        var sp = services.BuildServiceProvider();
        Assert.Contains("WaterQuality", sp.GetRequiredService<AquaModules>().Names);
        Assert.NotNull(sp.GetRequiredService<IAquaEventBus>());
        var factory = sp.GetRequiredService<Func<SpeciesProfile, AlertEngine>>();
        Assert.NotEmpty(factory(SpeciesCatalog.Vaname).Rules);
    }

    [Fact]
    public void Event_bus_isolates_failing_handlers()
    {
        var bus = new InMemoryEventBus();
        int got = 0;
        using var a = bus.Subscribe<WaterAlert>(_ => throw new InvalidOperationException());
        using var b = bus.Subscribe<WaterAlert>(_ => got++);
        bus.Publish(new WaterAlert(DateTime.UtcNow, "p", WaterParameter.Ph, AlertSeverity.Info, 1, 1, new("a", "b")));
        Assert.Equal(1, got);
    }
}

public class NativeKernelTests
{
    [Fact]
    public void Native_and_managed_agree()
    {
        double[] t = [20, 25, 29, 32], ph = [7, 8, 8.5, 9], sal = [0, 0, 15, 30];
        var nat = new double[4]; var man = new double[4];
        AquaKernels.Nh3FractionBatch(t, ph, sal, nat);
        AquaKernels.ForceManaged = true;
        try
        {
            AquaKernels.Nh3FractionBatch(t, ph, sal, man);
            var mc = AquaKernels.TgcMonteCarlo(5, 1.6, Enumerable.Repeat(29.0, 60).ToArray(), 8, 1.0, 99);
            AquaKernels.ForceManaged = false;
            var nc = AquaKernels.TgcMonteCarlo(5, 1.6, Enumerable.Repeat(29.0, 60).ToArray(), 8, 1.0, 99);
            for (int i = 0; i < mc.Length; i++) Assert.Equal(mc[i], nc[i], 9);
        }
        finally { AquaKernels.ForceManaged = false; }
        for (int i = 0; i < 4; i++) Assert.Equal(man[i], nat[i], 12);
    }

    [Fact]
    public void Native_library_loads_on_this_platform()
    {
        Assert.Equal(KernelBackend.Native, AquaKernels.Backend);
        Assert.Equal(10000u, AquaKernels.NativeVersion);
    }

    [Fact]
    public void Advection_diffusion_conserves_mass_and_matches_managed()
    {
        int nx = 20, ny = 12, n = nx * ny;
        var c = new float[n]; c[6 * nx + 10] = 100;
        var u = Enumerable.Repeat(0.05f, n).ToArray(); var v = new float[n];
        var wet = Enumerable.Repeat((byte)1, n).ToArray(); var src = new float[n];
        var a = new float[n]; var b = new float[n];
        AquaKernels.AdvectDiffuseStep(c, a, u, v, wet, src, nx, ny, 1, 1, 0.1f, 0);
        AquaKernels.ForceManaged = true;
        try { AquaKernels.AdvectDiffuseStep(c, b, u, v, wet, src, nx, ny, 1, 1, 0.1f, 0); }
        finally { AquaKernels.ForceManaged = false; }
        for (int i = 0; i < n; i++) Assert.Equal(b[i], a[i], 5);
        Assert.Equal(100, a.Sum(), 3);
    }
}

public class WaterQualityTests
{
    [Fact]
    public void Do_saturation_matches_reference_tables()
    {
        // APHA: 8.26 mg/L at 25 °C freshwater, 7.81 at 28 °C; seawater (35 ppt) at 25 °C ≈ 6.7–6.8
        Assert.Equal(8.26, WaterChemistry.OxygenSaturation(25), 1);
        Assert.Equal(7.81, WaterChemistry.OxygenSaturation(28), 1);
        Assert.InRange(WaterChemistry.OxygenSaturation(25, 35), 6.65, 6.85);
        Assert.True(WaterChemistry.OxygenSaturation(28, 0, 1000) < WaterChemistry.OxygenSaturation(28));
    }

    [Fact]
    public void Unionized_ammonia_fraction_follows_emerson()
    {
        Assert.Equal(0.0538, WaterChemistry.UnionizedAmmoniaFraction(25, 8), 3);
        Assert.Equal(0.0057, WaterChemistry.UnionizedAmmoniaFraction(25, 7), 3);
        Assert.True(WaterChemistry.UnionizedAmmoniaFraction(28, 8.2, 30) < WaterChemistry.UnionizedAmmoniaFraction(28, 8.2, 0));
    }

    [Fact]
    public void Good_water_scores_high_and_bad_water_is_critical()
    {
        var good = new WaterSample { TemperatureC = 29, DissolvedOxygen = 6, Ph = 7.8, SalinityPpt = 20, Ammonia = 0.2, Nitrite = 0.1, Alkalinity = 140 };
        var a = WaterQualityIndex.Assess(good, SpeciesCatalog.Vaname);
        Assert.True(a.Wqi >= 90, $"{a.Wqi}");
        Assert.Equal(WaterQualityClass.Excellent, a.Class);
        Assert.Empty(a.Issues);

        var bad = good with { DissolvedOxygen = 1.5, Ph = 8.9, Ammonia = 3.5 };
        var b = WaterQualityIndex.Assess(bad, SpeciesCatalog.Vaname);
        Assert.True(b.Wqi <= 30);
        Assert.Equal(WaterQualityClass.Critical, b.Class);
        Assert.Contains(b.Issues, i => i.En.Contains("NH₃"));
    }

    [Fact]
    public void Ccme_index_is_100_when_all_objectives_met()
    {
        var s = new WaterSample { TemperatureC = 29, DissolvedOxygen = 6, Ph = 7.5 };
        Assert.Equal(100, WaterQualityIndex.Ccme([s, s], SpeciesCatalog.Nila));
        Assert.True(WaterQualityIndex.Ccme([s with { DissolvedOxygen = 2 }, s], SpeciesCatalog.Nila) < 100);
    }

    [Fact]
    public void Alert_engine_uses_hysteresis_and_trend()
    {
        var engine = AlertEngine.ForSpecies(SpeciesCatalog.Vaname);
        var t0 = new DateTime(2026, 3, 1, 22, 0, 0);
        WaterSample S(double dox, double h) => new() { PondId = "P1", Timestamp = t0.AddHours(h), DissolvedOxygen = dox };
        Assert.Contains(engine.Evaluate(S(4.8, 0)), a => a.RuleId == "do-low" && !a.Cleared);
        Assert.DoesNotContain(engine.Evaluate(S(5.1, 0.5)), a => a.Cleared);          // inside hysteresis band
        Assert.Contains(engine.Evaluate(S(5.4, 1)), a => a.RuleId == "do-low" && a.Cleared);
        var trend = engine.Evaluate(S(4.4, 2)).Concat(engine.Evaluate(S(3.5, 3))).ToList();
        Assert.Contains(trend, a => a.RuleId == "do-trend");
    }

    [Fact]
    public void Recommendations_are_quantified()
    {
        var sample = new WaterSample { TemperatureC = 30, DissolvedOxygen = 2.5, Ph = 8.6, Ammonia = 2.5, SalinityPpt = 20, Alkalinity = 70 };
        var a = WaterQualityIndex.Assess(sample, SpeciesCatalog.Vaname);
        var recs = RecommendationEngine.Recommend(a, new PondContext(2600, 2000, 4000, 4, CultureSystem.LinedPond));
        Assert.Equal(1, recs[0].Priority);
        Assert.Contains(recs, r => r.Category == RecommendationCategory.WaterExchange && r.Quantity > 0);
        Assert.Contains(recs, r => r.Category == RecommendationCategory.Biological && r.Unit == "kg");
        Assert.Contains(recs, r => r.Category == RecommendationCategory.Aeration && r.Unit == "kW");
    }

    [Fact]
    public void Chemistry_doses()
    {
        Assert.Equal(1.68, WaterChemistry.SodiumBicarbonateKg(10, 100), 6);
        Assert.Equal(0.5, WaterChemistry.ExchangeFraction(2, 1), 6);
        Assert.True(WaterChemistry.SaltForNitriteKg(2, 0, 1000) > 0);
        Assert.Equal(19.9, WaterChemistry.FreeCo2(7, 100, 25), 0);
    }
}
