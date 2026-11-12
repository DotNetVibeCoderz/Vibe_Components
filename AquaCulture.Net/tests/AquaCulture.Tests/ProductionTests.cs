using AquaCulture.Analytics;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Disease;
using AquaCulture.Feeding;
using AquaCulture.Growth;

namespace AquaCulture.Tests;

public class GrowthTests
{
    [Fact]
    public void Tgc_round_trip()
    {
        double w = GrowthModels.Tgc(5, 1.6, 29 * 100);
        Assert.Equal(1.6, GrowthModels.EstimateTgc(5, w, 29 * 100), 9);
        Assert.Equal(100, GrowthModels.DaysToWeight(5, w, 1.6, 29), 6);
        Assert.True(GrowthModels.Sgr(5, w, 100) > 0);
    }

    [Fact]
    public void Von_bertalanffy_inverse()
    {
        double l = GrowthModels.VbLength(45, 0.6, -0.15, 1.2);
        Assert.Equal(1.2, GrowthModels.VbAgeAtLength(45, 0.6, -0.15, l), 9);
    }

    [Fact]
    public void Optimal_conditions_reach_market_size_near_typical_cycle()
    {
        foreach (var s in new[] { SpeciesCatalog.Nila, SpeciesCatalog.Vaname, SpeciesCatalog.Lele })
        {
            var sim = new GrowthSimulator(s);
            double t = (s.Temperature.OptLow + s.Temperature.OptHigh) / 2;
            var traj = sim.SimulateToWeight(new StockState(s, new DateOnly(2026, 1, 1), s.StockingWeightG, 10_000, 10_000), s.MarketWeightG,
                (_, _) => new DailyEnvironment(Math.Min(t, 29), s.DoOptimal + 1, 0), 400);
            int days = traj.Final.Day;
            Assert.InRange(days, s.TypicalCycleDays * 0.6, s.TypicalCycleDays * 1.5);
            Assert.InRange(traj.SurvivalRate, s.TypicalSurvival - 0.12, s.TypicalSurvival + 0.08);
            Assert.InRange(traj.Final.Fcr, 0.4, 3.0);
        }
    }

    [Fact]
    public void Stress_slows_growth_and_raises_mortality()
    {
        var s = SpeciesCatalog.Vaname;
        var sim = new GrowthSimulator(s);
        var start = new StockState(s, new DateOnly(2026, 1, 1), 5, 100_000, 2500);
        var good = sim.Simulate(start, 30, new DailyEnvironment(29, 6, 0.02, 20));
        var bad = sim.Simulate(start, 30, new DailyEnvironment(29, 2.5, 0.35, 20));
        Assert.True(bad.Final.AbwG < good.Final.AbwG);
        Assert.True(bad.SurvivalRate < good.SurvivalRate);
    }

    [Fact]
    public void Harvest_optimum_and_biomass_ci()
    {
        var s = SpeciesCatalog.Vaname;
        var traj = new GrowthSimulator(s).Simulate(new StockState(s, new DateOnly(2026, 1, 1), 1, 200_000, 2600), 150, new DailyEnvironment(29, 5.5, 0.02, 20));
        var best = HarvestPredictor.Optimal(traj, 2000, new HarvestEconomics(17_000, 150_000));
        Assert.True(best.Margin > 0);
        Assert.True(best.DayOfCulture > 30);
        var est = HarvestPredictor.EstimateBiomass([10, 11, 12, 9, 10, 11, 10, 12, 9, 10], 100_000);
        Assert.True(est.BiomassLowKg < est.BiomassKg && est.BiomassKg < est.BiomassHighKg);
    }
}

public class FeedingTests
{
    [Fact]
    public void Fcr_definitions()
    {
        Assert.Equal(1.5, Fcr.Calculate(150, 100));
        Assert.True(double.IsNaN(Fcr.Calculate(10, 0)));
        Assert.Equal(1.25, Fcr.Biological(150, 120, 20, 20), 6);
    }

    [Fact]
    public void Plan_cuts_ration_when_oxygen_is_low()
    {
        var s = SpeciesCatalog.Nila;
        var normal = FeedingCalculator.Plan(s, 100, 10_000, 29, 6);
        var lowDo = FeedingCalculator.Plan(s, 100, 10_000, 29, 3);
        Assert.True(lowDo.DailyKg < normal.DailyKg);
        Assert.NotEmpty(lowDo.Adjustments);
        Assert.Equal(normal.Meals.Count, FeedingCalculator.MealsPerDay(s, 100));
        Assert.Equal(normal.DailyKg, normal.Meals.Sum(m => m.Kg), 2);
        Assert.Equal(26, normal.DailyKg, 0); // 1000 kg × 2.6 %
    }

    [Fact]
    public void Shrimp_get_a_night_meal_and_best_feed_matches_size()
    {
        var plan = FeedingCalculator.Plan(SpeciesCatalog.Vaname, 12, 150_000, 29);
        Assert.Contains(plan.Meals, m => m.Time.Hour == 22);
        Assert.Equal("shrimp-grower", plan.Feed!.Id);
    }

    [Fact]
    public void Pearson_square_blend()
    {
        var (fh, fl, price) = FeedOptimizer.Blend(FeedCatalog.Get("fish-starter"), FeedCatalog.Get("fish-finisher"), 34);
        Assert.Equal(0.5, fh, 6); Assert.Equal(0.5, fl, 6);
        Assert.Equal((16_500 + 10_500) / 2.0, price, 6);
    }

    [Fact]
    public void Auto_feeder_learns_from_leftovers_and_skips_on_critical_do()
    {
        var af = new AutoFeedingAdvisor(SpeciesCatalog.Vaname);
        var meal = new Meal(new TimeOnly(10, 0), 10);
        var c1 = af.Next(meal, new FeedingObservation(40));
        Assert.True(c1.Grams < 10_000);
        Assert.True(af.Next(meal, null, doNow: 2).Skip);
    }
}

public class DiseaseTests
{
    [Fact]
    public void White_spots_point_to_wssv()
    {
        var r = DiagnosisEngine.Diagnose(SpeciesCatalog.Vaname, ["white_spots_shell", "red_body", "sudden_mass_mortality"]);
        Assert.Equal("wssv", r.Top!.Disease.Id);
        Assert.True(r.Top.Probability > 0.7);
    }

    [Fact]
    public void Pale_hepatopancreas_early_in_cycle_points_to_ahpnd()
    {
        var r = DiagnosisEngine.Diagnose(SpeciesCatalog.Vaname, ["pale_hepatopancreas", "empty_gut"], new PondConditions { DayOfCulture = 20 });
        Assert.Equal("ahpnd", r.Top!.Disease.Id);
    }

    [Fact]
    public void Fish_signs_and_species_filters()
    {
        var r = DiagnosisEngine.Diagnose(SpeciesCatalog.Nila, ["exophthalmia", "erratic_swimming"], new PondConditions { TemperatureC = 32 });
        Assert.Equal("strep", r.Top!.Disease.Id);
        Assert.DoesNotContain(DiseaseKnowledgeBase.For(SpeciesCatalog.Lele), d => d.Id is "tilv" or "khv" or "wssv");
        Assert.Contains(DiseaseKnowledgeBase.For(SpeciesCatalog.Koi), d => d.Id == "khv");
    }

    [Fact]
    public void Vague_signs_keep_probability_low()
    {
        var r = DiagnosisEngine.Diagnose(SpeciesCatalog.Nila, ["lethargy"]);
        Assert.True(r.ProbabilityNone > 0.2);
    }

    [Fact]
    public void Environmental_risk_rises_with_stress()
    {
        var s = SpeciesCatalog.Vaname;
        var calm = DiagnosisEngine.Risk(DiseaseKnowledgeBase.Get("wssv"), new PondConditions { TemperatureC = 29 }, s).Risk;
        var stress = DiagnosisEngine.Risk(DiseaseKnowledgeBase.Get("wssv"), new PondConditions { TemperatureC = 26, TemperatureSwing24h = 5, SalinityDrop = 6 }, s).Risk;
        Assert.True(stress > calm + 0.3);
    }
}

public class AnalyticsTests
{
    [Fact]
    public void Cycle_kpis_and_costs()
    {
        var pond = new Pond { Id = "P1", AreaM2 = 1000, DepthM = 1.2 };
        var c = new CultureCycle { Id = "C1", PondId = "P1", Species = SpeciesCatalog.Nila, StockingDate = new(2026, 1, 1), StockedCount = 5000, StockingWeightG = 5, SeedPricePerUnit = 300 };
        c.LogSampling(new(2026, 5, 1), 400).LogMortality(new(2026, 2, 1), 750);
        for (int d = 0; d < 120; d++) c.LogFeed(new DateOnly(2026, 1, 1).AddDays(d), 18, "fish-grower", 12_000);
        c.LogExpense(new(2026, 3, 1), "Listrik / energy", 1_500_000).LogExpense(new(2026, 3, 1), "Probiotik", 400_000).LogExpense(new(2026, 3, 1), "Upah tenaga kerja", 3_000_000);
        c.LogHarvest(new(2026, 5, 2), 1700, 4250, 30_000);
        var k = KpiCalculator.Calculate(c, pond, new(2026, 5, 2));
        Assert.Equal(85, k.Sr, 1);
        Assert.Equal(1.29, k.Fcr, 2); // 2160 kg / (1700 − 25)
        Assert.Equal(1_500_000, k.Costs.Seed);
        Assert.Equal(25_920_000, k.Costs.Feed);
        Assert.Equal(1_500_000, k.Costs.Energy);
        Assert.Equal(400_000, k.Costs.Chemicals);
        Assert.Equal(3_000_000, k.Costs.Labor);
        Assert.Equal(51_000_000, k.Revenue);
        Assert.True(k.Profit > 0);
        Assert.Equal(1.7, k.ProductivityKgM2, 2);
        Assert.Contains(k.Benchmarks, b => b.Kpi == "FCR" && b.Rating is BenchmarkRating.Excellent or BenchmarkRating.OnTarget);
    }

    [Fact]
    public void Expense_classifier_is_bilingual()
    {
        Assert.Equal(0, KpiCalculator.Classify("Benur vaname"));
        Assert.Equal(1, KpiCalculator.Classify("Pakan"));
        Assert.Equal(2, KpiCalculator.Classify("Solar genset"));
        Assert.Equal(4, KpiCalculator.Classify("Kapur dolomit"));
        Assert.Equal(5, KpiCalculator.Classify("Transport"));
    }
}
