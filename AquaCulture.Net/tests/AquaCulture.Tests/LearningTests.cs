using AquaCulture.AI;
using AquaCulture.AI.Learning;
using AquaCulture.AI.Torch;
using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Growth;

namespace AquaCulture.Tests;

public class LearningTests
{
    private static readonly Lazy<IReadOnlyList<DoObservation>> History = new(() => DoHistory.Simulate(days: 45, seed: 5));

    [Fact]
    public void Simulated_history_has_a_realistic_diurnal_cycle_and_set_point_aeration()
    {
        var h = History.Value;
        Assert.Equal(45 * 24, h.Count);
        var dawn = h.Where(o => o.Time.Hour is >= 4 and <= 6).Average(o => o.Do);
        var afternoon = h.Where(o => o.Time.Hour is >= 14 and <= 16).Average(o => o.Do);
        Assert.True(afternoon - dawn > 1.5, $"afternoon {afternoon:0.00} vs dawn {dawn:0.00}");
        Assert.Contains(h, o => o.AeratorOn);
        Assert.Contains(h, o => !o.AeratorOn);
    }

    [Fact]
    public void Gbt_do_forecaster_beats_persistence_and_diurnal_baselines_and_round_trips()
    {
        var h = History.Value;
        var f = new GbtDoForecaster(horizon: 12, trees: 100).Train(h);
        Assert.Equal(12, f.Skill.Count);
        foreach (var s in f.Skill.Where(s => s.HorizonHours is 3 or 6))
            Assert.True(s.SkillScore > 0.1, $"h={s.HorizonHours}: rmse {s.Rmse} vs persistence {s.Persistence}, diurnal {s.Diurnal}");
        // 12 h ahead the next day's cloud cover dominates and is not in the inputs: the model only has to beat both baselines
        var s12 = f.Skill[^1];
        Assert.True(s12.SkillScore > 0, $"h=12: rmse {s12.Rmse} vs persistence {s12.Persistence}, diurnal {s12.Diurnal}");
        Assert.True(f.Skill[0].Rmse < f.Skill[^1].Rmse, "error should grow with the horizon");

        var curve = f.Forecast(h);
        Assert.Equal(12, curve.Point.Length);
        Assert.All(Enumerable.Range(0, 12), i => Assert.True(curve.Lower[i] <= curve.Point[i] && curve.Point[i] <= curve.Upper[i]));
        Assert.Equal(h[^1].Time.AddHours(1), curve.Times[0]);

        var dir = Path.Combine(Path.GetTempPath(), $"aqua-gbt-{Guid.NewGuid():N}");
        try
        {
            f.Save(dir);
            var g = GbtDoForecaster.Load(dir);
            Assert.Equal(curve.Point, g.Forecast(h).Point);
            Assert.Equal(f.Skill[5].Rmse, g.Skill[5].Rmse);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Lstm_do_forecaster_learns_the_oxygen_regime()
    {
        var h = History.Value;
        using var f = new LstmDoForecaster(horizon: 12, lookback: 24, hidden: 24, epochs: 25).Train(h);
        Assert.True(f.TrainingLoss[^1] < f.TrainingLoss[0] * 0.5, $"loss {f.TrainingLoss[0]:0.0000} → {f.TrainingLoss[^1]:0.0000}");
        var s6 = f.Skill.Single(s => s.HorizonHours == 6);
        Assert.True(s6.Rmse < s6.Persistence, $"h=6 rmse {s6.Rmse} vs persistence {s6.Persistence}");
        var curve = f.Forecast(h);
        Assert.Equal(12, curve.Point.Length);
        Assert.All(curve.Point, v => Assert.InRange(v, 0, 20));

        var path = Path.Combine(Path.GetTempPath(), $"aqua-lstm-{Guid.NewGuid():N}.bin");
        try
        {
            f.Save(path);
            using var g = new LstmDoForecaster(horizon: 12, lookback: 24, hidden: 24).Load(path);
            Assert.Equal(curve.Point, g.Forecast(h).Point);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A "real" farm whose stock grows 30 % faster than the catalogue TGC, logged weekly like a farmer would.</summary>
    private static (List<CultureCycle> Cycles, Func<string, DateOnly, DailyWater?> Water) FarmCycles(int cycles, ulong seed)
    {
        var s = SpeciesCatalog.Vaname;
        var sim = new GrowthSimulator(s) { Tgc = s.Tgc * 1.3 };
        var rng = new DeterministicRandom(seed);
        var water = new Dictionary<(string, DateOnly), DailyWater>();
        var list = new List<CultureCycle>();
        for (int c = 0; c < cycles; c++)
        {
            string pond = $"P-{c:00}";
            var start = new DateOnly(2025, 1, 1).AddDays(40 * c);
            double tBase = rng.Uniform(27, 31), doBase = rng.Uniform(3.5, 5.5);
            DailyEnvironment Env(int day, DateOnly date)
            {
                var w = new DailyWater(tBase + Math.Sin(day / 9.0), doBase + 0.4 * Math.Sin(day / 5.0), 0.02 + 0.01 * Math.Sin(day / 7.0), 18);
                water[(pond, date)] = w;
                return new DailyEnvironment(w.TemperatureC, w.DawnDo, w.Nh3, w.SalinityPpt);
            }
            var traj = sim.Simulate(new StockState(s, start, 0.5, 200_000, 2600), 100, Env);
            var cycle = new CultureCycle { Id = $"C{c}", PondId = pond, Species = s, StockingDate = start, StockedCount = 200_000, StockingWeightG = 0.5 };
            foreach (var d in traj.Days)
            {
                cycle.LogFeed(d.Date, d.FeedKg);
                if (d.Day % 7 == 0) cycle.LogSampling(d.Date, d.AbwG * Math.Exp(0.03 * rng.NextGaussian()));
            }
            list.Add(cycle);
        }
        return (list, (p, d) => water.TryGetValue((p, d), out var w) ? w : null);
    }

    [Fact]
    public void Farm_growth_learner_adapts_the_prior_to_local_growth()
    {
        var (cycles, water) = FarmCycles(6, 21);
        var train = FarmGrowthLearner.FromCycles(cycles.Take(5), water);
        var holdout = FarmGrowthLearner.FromCycles(cycles.Skip(5), water);
        Assert.True(train.Count >= 50, $"{train.Count} rows");
        Assert.True(holdout.Count >= 10);

        var prior = new GrowthPredictor(SpeciesCatalog.Vaname);
        var local = FarmGrowthLearner.Fit(SpeciesCatalog.Vaname, train);
        double before = FarmGrowthLearner.Rmse(prior, holdout), after = FarmGrowthLearner.Rmse(local, holdout);
        Assert.True(after < before * 0.8, $"holdout RMSE prior {before:0.000} → farm-adapted {after:0.000} %/day");
    }
}
