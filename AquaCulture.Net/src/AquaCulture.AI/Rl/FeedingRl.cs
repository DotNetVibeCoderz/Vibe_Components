using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Feeding;
using AquaCulture.Growth;
using AquaCulture.WaterQuality;
using RLNet.Agents;
using RLNet.Buffers;
using RLNet.Environments;
using RLNet.Spaces;
using RLNet.Training;

namespace AquaCulture.AI.Rl;

/// <summary>Settings of the daily feeding problem.</summary>
public sealed record FeedingScenario
{
    public SpeciesProfile Species { get; init; } = SpeciesCatalog.Vaname;
    public double VolumeM3 { get; init; } = 2600;
    public long StockedCount { get; init; } = 220_000;
    public double StartWeightG { get; init; } = 3;
    public double FeedPriceIdrPerKg { get; init; } = 16_000;
    public double FeedProteinFraction { get; init; } = 0.35;
    /// <summary>Share of the pond's TAN removed per day (nitrification, phytoplankton uptake, exchange).</summary>
    public double TanRemovalPerDay { get; init; } = 0.35;
    public double Ph { get; init; } = 8.0;
    public double SalinityPpt { get; init; } = 18;
}

public sealed record FeedingEvaluation(string Policy, double ProfitIdr, double HarvestKg, double FeedKg, double Fcr, double MeanNh3, double Survival, double WasteKg);

/// <summary>
/// Daily ration control as an RLNet continuous environment. Action: ration factor 0.4–1.3 of the feeding table. Dynamics: the AquaGrowth
/// simulator (TGC × temperature × DO × NH₃ × crowding × ration); feed above appetite is wasted, costs money, mineralises into TAN and
/// lowers the next dawn DO. Reward: value of biomass gained minus the cost of feed given (thousand IDR per day).
/// </summary>
public sealed class FeedingEnvironment : ContinuousEnvironmentBase
{
    public const int ObservationSize = 7;

    private readonly FeedingScenario _s;
    private readonly GrowthSimulator _sim;
    private readonly ulong _baseSeed;
    private int _episode;
    private DeterministicRandom _rng = new(1);
    private double _abw, _count, _tan, _temp, _doDawn, _last = 1, _wasteBod;
    private DateOnly _date;

    public FeedingEnvironment(FeedingScenario? scenario = null, int days = 60, ulong seed = 1)
        : base(BoxSpace.Unbounded(ObservationSize, null!), BoxSpace.Uniform(1, 0.4f, 1.3f, ["ration_factor"]), days)
    {
        _s = scenario ?? new FeedingScenario();
        _sim = new GrowthSimulator(_s.Species);
        _baseSeed = seed;
    }

    public override string Name => "AquaCulture/Feeding-v1";
    public double AbwG => _abw;
    public double Nh3 => WaterChemistry.UnionizedAmmonia(_tan, _temp, _s.Ph, _s.SalinityPpt);
    public double BiomassKg => _abw * _count / 1000;
    public double ProfitIdr { get; private set; }
    public double FeedKg { get; private set; }
    public double GainKg { get; private set; }
    public double SumNh3 { get; private set; }
    public double WasteKg { get; private set; }

    public void ResetEpisode(int episode) { _episode = episode; Reset(null); }

    protected override void OnReset()
    {
        _rng = new DeterministicRandom(_baseSeed * 7_919 + (ulong)_episode++);
        _abw = _s.StartWeightG * _rng.Uniform(0.8, 1.2);
        _count = _s.StockedCount * _rng.Uniform(0.85, 1.1);
        _tan = 0.2; _last = 1; _wasteBod = 0;
        _date = new DateOnly(2026, 1, 1).AddDays(_rng.Next(300));
        NextWeather();
        ProfitIdr = 0; FeedKg = 0; GainKg = 0; SumNh3 = 0; WasteKg = 0;
    }

    private void NextWeather()
    {
        _temp = 29 + 1.5 * Math.Sin(_date.DayOfYear / 58.0) + 0.6 * _rng.NextGaussian();
        // dawn DO: weather noise, minus a share of the night-time oxygen demand of yesterday's uneaten feed
        _doDawn = Math.Max(1.5, 5.0 + 0.6 * _rng.NextGaussian() - 0.25 * _wasteBod);
    }

    protected override void WriteObservation(Span<float> o)
    {
        o[0] = (float)Math.Log(_abw / _s.Species.MarketWeightG);
        o[1] = (float)((_temp - 29) / 2);
        o[2] = (float)(_doDawn / _s.Species.DoOptimal);
        o[3] = (float)(Nh3 / _s.Species.Nh3Max);
        o[4] = (float)(BiomassKg / _s.VolumeM3 / _s.Species.MaxBiomassKgM3);
        o[5] = (float)(ElapsedSteps / (double)MaxEpisodeSteps);
        o[6] = (float)_last;
    }

    /// <summary>The unadjusted feeding-table ration for today (kg).</summary>
    public double TableKg => FeedingCalculator.Plan(_s.Species, _abw, _count, _temp).DailyKg;

    /// <summary>The farmer's rule: the feeding table with its own DO / NH₃ / temperature corrections, as a factor of the plain table.</summary>
    public double AdjustedTableFactor
    {
        get
        {
            double plain = TableKg;
            return plain <= 0 ? 1 : FeedingCalculator.Plan(_s.Species, _abw, _count, _temp, _doDawn, Nh3).DailyKg / plain;
        }
    }

    protected override StepResult OnStep(ReadOnlySpan<float> action)
    {
        double ration = Math.Clamp(action[0], 0.4, 1.3);
        double given = TableKg * ration;
        // growth responds to the ration up to appetite (factor 1); anything above appetite is not eaten
        var env = new DailyEnvironment(_temp, _doDawn, Nh3, _s.SalinityPpt, Math.Min(1, ration));
        var traj = _sim.Simulate(new StockState(_s.Species, _date, _abw, _count, _s.VolumeM3, ElapsedSteps), 1, env);
        var d = traj.Final;
        double eaten = Math.Min(given, d.FeedKg), waste = Math.Max(0, given - eaten);
        double before = BiomassKg;
        _abw = d.AbwG; _count = d.Survivors;
        double gain = BiomassKg - before;
        double litres = _s.VolumeM3 * 1000;
        // TAN: excretion of eaten protein (0.092 kg TAN per kg protein) + mineralisation of uneaten feed (16 % N of its protein); first-order removal
        double tanIn = (eaten * _s.FeedProteinFraction * 0.092 + waste * _s.FeedProteinFraction * 0.16) * 1e6 / litres;
        _tan = Math.Max(0, _tan * (1 - _s.TanRemovalPerDay) + tanIn);
        _wasteBod = waste * 1.2 * 1e6 / litres;     // mg/L of oxygen demand from uneaten feed (≈ 1.2 kg O₂ per kg feed)
        _date = _date.AddDays(1); _last = ration;
        NextWeather();
        double value = gain * _s.Species.PricePerKg - given * _s.FeedPriceIdrPerKg;
        ProfitIdr += value; FeedKg += given; GainKg += gain; SumNh3 += Nh3; WasteKg += waste;
        return Advance((float)(value / 1000), false);
    }

    public static FeedingEvaluation Evaluate(string name, Func<FeedingEnvironment, double> policy, int episodes = 20, FeedingScenario? scenario = null, int days = 60, ulong seed = 1)
    {
        var env = new FeedingEnvironment(scenario, days, seed);
        double profit = 0, kg = 0, feed = 0, nh3 = 0, surv = 0, waste = 0, gain = 0;
        for (int e = 0; e < episodes; e++)
        {
            env.ResetEpisode(10_000 + e);
            double start = env._count;
            float[] a = [0];
            StepResult r;
            do { a[0] = (float)policy(env); r = env.Step(a); } while (!r.Done);
            profit += env.ProfitIdr; kg += env.BiomassKg; feed += env.FeedKg; nh3 += env.SumNh3 / days; surv += env._count / start; waste += env.WasteKg; gain += env.GainKg;
        }
        return new FeedingEvaluation(name, Math.Round(profit / episodes), Math.Round(kg / episodes, 1), Math.Round(feed / episodes, 1),
            Math.Round(feed / Math.Max(1e-9, gain), 3), Math.Round(nh3 / episodes, 4), Math.Round(surv / episodes, 3), Math.Round(waste / episodes, 1));
    }

    public static Func<FeedingEnvironment, double> Fixed(double ration) => _ => ration;

    /// <summary>Baseline: the feeding table with its standard DO / NH₃ corrections.</summary>
    public static double AdjustedTable(FeedingEnvironment e) => e.AdjustedTableFactor;
}

/// <summary>A trained continuous ration policy (RLNet SAC).</summary>
public sealed class FeedingRlPolicy
{
    private readonly SacAgent _agent;
    internal FeedingRlPolicy(SacAgent agent, TrainingReport report) { _agent = agent; Report = report; }
    public TrainingReport Report { get; }

    public double Decide(FeedingEnvironment env)
    {
        Span<float> a = stackalloc float[1];
        _agent.SelectAction(env.Observation, a, deterministic: true);
        return Math.Clamp(a[0], 0.4, 1.3);
    }

    public float[] ExportParameters() => _agent.ExportParameters();

    public static FeedingRlPolicy Train(int steps = 15_000, FeedingScenario? scenario = null, int seed = 3)
    {
        var env = new FeedingEnvironment(scenario, 60, (ulong)seed);
        var options = new SacOptions { HiddenSizes = [64, 64], LearningRate = 3e-3f, Gamma = 0.98f, BatchSize = 128, LearningStarts = 600, BufferCapacity = 100_000, AutoTuneTemperature = true };
        var agent = new SacAgent(env.ObservationSpace, env.ActionSpace, options, new UniformReplayBuffer(options.BufferCapacity, env.ObservationSpace.FlatSize, 1), seed, null!);
        var report = Trainer.Train(env, agent, new TrainingOptions { MaxSteps = steps, Seed = seed, WindowSize = 20 });
        return new FeedingRlPolicy(agent, report);
    }
}
