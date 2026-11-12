using AquaCulture.Hatchery;
using AquaCulture.Traceability;

namespace AquaCulture.Tests;

public class HatcheryTests
{
    private static Dictionary<string, PcrResult> AllNegative() => BroodstockUnit.RequiredPathogens.ToDictionary(p => p, _ => PcrResult.Negative);

    [Fact]
    public void Broodstock_clearance_spawning_rate_and_culling()
    {
        var unit = new BroodstockUnit();
        var day0 = new DateOnly(2026, 5, 1);
        for (int i = 1; i <= 10; i++) unit.Animals.Add(new Broodstock($"F{i:00}", Sex.Female, "SPF-Hawaii", 45, day0) { Pcr = AllNegative() });
        for (int i = 1; i <= 5; i++) unit.Animals.Add(new Broodstock($"M{i:00}", Sex.Male, "SPF-Hawaii", 40, day0) { Pcr = AllNegative() });
        unit.Animals.Add(new Broodstock("F99", Sex.Female, "local", 50, day0) { Spf = false });
        var ehp = AllNegative(); ehp["EHP"] = PcrResult.Positive;
        unit.Animals.Add(new Broodstock("F98", Sex.Female, "SPF-Hawaii", 48, day0) { Pcr = ehp });
        Assert.Equal(15, unit.Cleared.Count());

        for (int d = 0; d < 10; d++)
            foreach (var f in new[] { "F01", "F02" })
                if ((d + (f == "F02" ? 1 : 0)) % 3 == 0)
                    unit.Spawnings.Add(new Spawning(day0.AddDays(10 + d), f, "M01", 200_000, f == "F01" ? 120_000 : 40_000));
        Assert.Equal(0.6, unit.Spawnings.First(s => s.FemaleId == "F01").HatchingRate, 6);
        var perf = unit.FemalePerformance(day0.AddDays(20));
        Assert.Equal("F01", perf[0].FemaleId);
        Assert.InRange(unit.SpawningRate(day0.AddDays(10), day0.AddDays(19)), 0.05, 0.08);   // 7 spawns / (12 females × 10 nights)
        var cull = unit.CullCandidates(day0.AddDays(30));
        Assert.Contains("F02", cull);                                   // weak spawner
        Assert.Contains("F05", cull);                                   // never spawned in 30 days
        Assert.DoesNotContain("F01", cull);
    }

    [Fact]
    public void Larval_stages_follow_temperature_and_feeding_changes_by_stage()
    {
        var s29 = LarvalRearing.Schedule(29);
        int pl1 = s29.First(x => x.Stage == LarvalStage.PL).Day;
        Assert.InRange(pl1, 8, 10);
        Assert.Equal(LarvalStage.N, s29[0].Stage);
        Assert.Contains(s29, x => x.Stage == LarvalStage.Z2);
        Assert.Equal(10, s29[^1].PlDay);
        Assert.True(LarvalRearing.Schedule(32).First(x => x.Stage == LarvalStage.PL).Day < LarvalRearing.Schedule(26).First(x => x.Stage == LarvalStage.PL).Day);

        Assert.Equal(0, LarvalRearing.Feeding(LarvalStage.N).AlgaeCellsPerMl);
        Assert.True(LarvalRearing.Feeding(LarvalStage.Z2).AlgaeCellsPerMl > LarvalRearing.Feeding(LarvalStage.M2).AlgaeCellsPerMl);
        Assert.Equal(0, LarvalRearing.Feeding(LarvalStage.Z3).ArtemiaPerLarva);
        Assert.True(LarvalRearing.Feeding(LarvalStage.PL, 5).ArtemiaPerLarva > LarvalRearing.Feeding(LarvalStage.M3).ArtemiaPerLarva);
        Assert.InRange(LarvalRearing.ExpectedSurvivalToPl1(), 0.6, 0.75);
    }

    [Fact]
    public void Tank_batch_feed_quality_and_seed_lot_event()
    {
        var unit = new BroodstockUnit();
        unit.Animals.Add(new Broodstock("F01", Sex.Female, "SPF-Hawaii", 45, new DateOnly(2026, 5, 1)) { Pcr = AllNegative() });
        unit.Animals.Add(new Broodstock("M01", Sex.Male, "SPF-Florida", 40, new DateOnly(2026, 5, 1)) { Pcr = AllNegative() });
        var batch = new LarvalBatch { Id = "B1", TankId = "LRT-04", Stocked = new DateOnly(2026, 6, 1), NaupliiStocked = 2_000_000, TankVolumeM3 = 10, TemperatureC = 29 };
        batch.Sources.Add(new Spawning(new DateOnly(2026, 5, 30), "F01", "M01", 250_000, 180_000));
        Assert.Equal(200, batch.DensityPerLitre);
        Assert.Equal(LarvalStage.N, batch.StageOn(new DateOnly(2026, 6, 1)).Stage);
        var z = batch.DailyFeed(new DateOnly(2026, 6, 3));
        Assert.True(z.AlgaeLitres > 0 && z.ArtemiaMillions == 0);
        batch.Counts.Add((new DateOnly(2026, 6, 8), 1_500_000));
        var m = batch.DailyFeed(new DateOnly(2026, 6, 7));
        Assert.True(m.ArtemiaMillions > 0);

        var good = new PlSample { PlDay = 10, StressTestSurvival = 0.92, LengthCv = 0.10, MuscleGutRatio = 4.5, GutFullness = 0.9, NecrosisShare = 0.01, FoulingShare = 0.02, Pcr = AllNegative() };
        var q = PlQualityAssessment.Evaluate(good);
        Assert.True(q.Pass);
        Assert.True(q.Score >= 85);
        var sick = PlQualityAssessment.Evaluate(good with { Pcr = new Dictionary<string, PcrResult>(AllNegative()) { ["WSSV"] = PcrResult.Positive } });
        Assert.False(sick.Pass);
        Assert.Contains(sick.Issues, i => i.Id.Contains("WSSV"));
        Assert.False(PlQualityAssessment.Evaluate(good with { StressTestSurvival = 0.6 }).Pass);
        Assert.Contains(PlQualityAssessment.Evaluate(good with { Pcr = new Dictionary<string, PcrResult>() }).Issues, i => i.En.Contains("not tested"));

        var harvest = new DateOnly(2026, 6, 19);
        Assert.Throws<InvalidOperationException>(() => SeedLots.Commissioning(batch, unit, good, Gs1.WithCheckDigit("899999910001"), Gs1.WithCheckDigit("899999900002"), harvest));
        batch.HarvestedPl = 1_300_000; batch.HarvestDate = harvest;
        Assert.Equal(0.65, batch.Survival, 6);
        var e = SeedLots.Commissioning(batch, unit, good, Gs1.WithCheckDigit("899999910001"), Gs1.WithCheckDigit("899999900002"), harvest);
        Assert.Equal(1_300_000, e.Quantities.Single().Quantity);
        Assert.EndsWith("/10/LRT-04-260619", e.Quantities.Single().EpcClass);
        Assert.Equal("SPF-Hawaii;SPF-Florida", e.Ilmd["aqua:broodstockOrigin"]);
        Assert.Equal("Negative", e.Ilmd["aqua:pcrWSSV"]);
        Assert.Contains("Disp-active", e.Disposition);
        Assert.Contains("aqua:qualityScore", EpcisJson.ToDocument([e]));
    }
}
