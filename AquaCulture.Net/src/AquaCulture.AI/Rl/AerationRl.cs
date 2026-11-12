using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Environment;
using AquaCulture.Hydrodynamic;
using RLNet.Agents;
using RLNet.Buffers;
using RLNet.Environments;
using RLNet.Spaces;
using RLNet.Training;

namespace AquaCulture.AI.Rl;

/// <summary>Economic and biological settings of the aeration problem.</summary>
public sealed record AerationScenario
{
    public SpeciesProfile Species { get; init; } = SpeciesCatalog.Vaname;
    public double VolumeM3 { get; init; } = 2600;
    public double DepthM { get; init; } = 1.3;
    public double AeratorKw { get; init; } = 4.4;
    /// <summary>Electricity tariff (IDR/kWh; PLN business tariff ≈ 1,445).</summary>
    public double TariffIdrPerKwh { get; init; } = 1445;
    /// <summary>Cost assigned to each mg/L·h below the species' critical DO (stress, mortality risk), IDR.</summary>
    public double CriticalPenaltyIdr { get; init; } = 40_000;
    /// <summary>Cost of each mg/L·h below the optimum (lost growth and appetite), IDR.</summary>
    public double SuboptimalPenaltyIdr { get; init; } = 2_500;
    public double MinBiomassKg { get; init; } = 2000;
    public double MaxBiomassKg { get; init; } = 4500;
    public double Latitude { get; init; } = -6.3;
    public SpeciesGroup Group => Species.Group;
}

/// <summary>Outcome of running an aeration policy over evaluation episodes (per-episode means).</summary>
public sealed record AerationEvaluation(string Policy, double CostIdr, double Kwh, double HoursBelowCritical, double HoursBelowOptimum, double MinDo, double Reward);

/// <summary>
/// Hourly aeration control as an RLNet continuous environment. Observation: DO, water temperature, time of day, current and 24-h solar
/// radiation, biomass and the previous action. Action: aerator power fraction 0–1. The dynamics are <see cref="PondOxygenBudget.StepHour"/>
/// on synthetic tropical weather (a new cloud pattern every episode); the reward is minus (electricity + DO-stress cost) in thousand IDR.
/// </summary>
public sealed class AerationEnvironment : ContinuousEnvironmentBase
{
    public const int ObservationSize = 9;

    private readonly AerationScenario _s;
    private readonly ulong _baseSeed;
    private int _episode;
    private IReadOnlyList<WeatherObservation> _weather = [];
    private double[] _waterTemp = [];
    private OxygenBudgetParameters _p = null!;
    private double _do, _lastAction;
    private int _t;

    public AerationEnvironment(AerationScenario? scenario = null, int hours = 72, ulong seed = 1)
        : base(BoxSpace.Unbounded(ObservationSize, null!), BoxSpace.Uniform(1, 0, 1, ["aerator_fraction"]), hours)
    {
        _s = scenario ?? new AerationScenario();
        _baseSeed = seed;
    }

    public override string Name => "AquaCulture/Aeration-v1";
    public AerationScenario Scenario => _s;
    public double Do => _do;
    public DateTime Time => _weather.Count == 0 ? default : _weather[Math.Min(_t, _weather.Count - 1)].Time;
    public double BiomassKg => _p.BiomassKg;

    // per-episode statistics
    public double Kwh { get; private set; }
    public double CostIdr { get; private set; }
    public int HoursBelowCritical { get; private set; }
    public int HoursBelowOptimum { get; private set; }
    public double MinDo { get; private set; }

    /// <summary>Starts episode number <paramref name="episode"/> (weather and biomass are a deterministic function of it).</summary>
    public void ResetEpisode(int episode) { _episode = episode; Reset(null); }

    protected override void OnReset()
    {
        var rng = new DeterministicRandom(_baseSeed * 1_000_003 + (ulong)_episode++);
        int days = MaxEpisodeSteps / 24 + 2;
        var start = new DateTime(2026, 1, 1).AddDays(rng.Next(300)).AddHours(18);     // episodes start at 18:00, before the critical night
        _weather = SyntheticWeather.Generate(_s.Latitude, start.Date, days, (ulong)rng.Next(1_000_000) + 1UL).Where(w => w.Time >= start).ToList();
        _waterTemp = WeatherImpactPredictor.WaterTemperature(_weather, _s.DepthM, double.NaN);
        _p = new OxygenBudgetParameters
        {
            VolumeM3 = _s.VolumeM3, DepthM = _s.DepthM, BiomassKg = rng.Uniform(_s.MinBiomassKg, _s.MaxBiomassKg), MeanWeightG = rng.Uniform(8, 18),
            Group = _s.Group, ChlorophyllUgL = rng.Uniform(70, 150), AeratorKw = _s.AeratorKw,
        };
        _do = rng.Uniform(5, 7);
        _t = 0; _lastAction = 0;
        Kwh = 0; CostIdr = 0; HoursBelowCritical = 0; HoursBelowOptimum = 0; MinDo = _do;
    }

    private double PeakSolar(int t)
    {
        var day = _weather[t].Time.Date;
        return _weather.Where(w => w.Time.Date == day).Select(w => w.SolarWm2).DefaultIfEmpty(900).Max();
    }

    protected override void WriteObservation(Span<float> o)
    {
        int t = Math.Min(_t, _weather.Count - 1);
        var w = _weather[t];
        double hour = w.Time.Hour;
        double solar24 = 0; for (int k = Math.Max(0, t - 23); k <= t; k++) solar24 += _weather[k].SolarWm2 / 1000;
        o[0] = (float)(_do / 10);
        o[1] = (float)((_waterTemp[t] - 29) / 3);
        o[2] = (float)Math.Sin(2 * Math.PI * hour / 24);
        o[3] = (float)Math.Cos(2 * Math.PI * hour / 24);
        o[4] = (float)(w.SolarWm2 / 1000);
        o[5] = (float)(solar24 / 6);
        o[6] = (float)(_p.BiomassKg / _s.MaxBiomassKg);
        o[7] = (float)_lastAction;
        o[8] = (float)((_do - _s.Species.DoCritical) / 5);
    }

    protected override StepResult OnStep(ReadOnlySpan<float> action)
    {
        double f = Math.Clamp(action[0], 0, 1);
        var (next, kwh, _) = PondOxygenBudget.StepHour(_p, _weather[_t], _waterTemp[_t], _do, f, PeakSolar(_t));
        _do = next; _lastAction = f; _t++;
        double crit = Math.Max(0, _s.Species.DoCritical - _do), sub = Math.Max(0, _s.Species.DoOptimal - _do);
        double cost = kwh * _s.TariffIdrPerKwh, penalty = crit * _s.CriticalPenaltyIdr + sub * _s.SuboptimalPenaltyIdr;
        Kwh += kwh; CostIdr += cost + penalty; MinDo = Math.Min(MinDo, _do);
        if (crit > 0) HoursBelowCritical++;
        if (sub > 0) HoursBelowOptimum++;
        return Advance((float)(-(cost + penalty) / 1000), false);
    }

    /// <summary>Evaluates a policy (observation → aerator fraction) on episodes <c>10 000 … 10 000 + n</c> (never used for training).</summary>
    public static AerationEvaluation Evaluate(string name, Func<AerationEnvironment, double> policy, int episodes = 20, AerationScenario? scenario = null, int hours = 72, ulong seed = 1)
    {
        var env = new AerationEnvironment(scenario, hours, seed);
        double cost = 0, kwh = 0, crit = 0, sub = 0, min = 0, reward = 0;
        for (int e = 0; e < episodes; e++)
        {
            env.ResetEpisode(10_000 + e);
            float[] a = [0];
            StepResult r;
            do { a[0] = (float)policy(env); r = env.Step(a); reward += r.Reward; } while (!r.Done);
            cost += env.CostIdr; kwh += env.Kwh; crit += env.HoursBelowCritical; sub += env.HoursBelowOptimum; min += env.MinDo;
        }
        return new AerationEvaluation(name, Math.Round(cost / episodes), Math.Round(kwh / episodes, 1), Math.Round(crit / episodes, 2), Math.Round(sub / episodes, 2),
            Math.Round(min / episodes, 2), Math.Round(reward / episodes, 1));
    }

    // ── baselines farmers use ──
    public static double AlwaysOn(AerationEnvironment _) => 1;
    public static Func<AerationEnvironment, double> SetPoint(double doSetPoint) => e => e.Do < doSetPoint ? 1 : 0;
    /// <summary>Timer from 22:00 to 06:00.</summary>
    public static double NightTimer(AerationEnvironment e) => e.Time.Hour is >= 22 or < 6 ? 1 : 0;
}

/// <summary>A trained continuous-action aeration policy (RLNet SAC actor).</summary>
public sealed class AerationPolicy
{
    private readonly SacAgent _agent;

    internal AerationPolicy(SacAgent agent, TrainingReport report) { _agent = agent; Report = report; }

    public TrainingReport Report { get; }

    /// <summary>Aerator power fraction (0–1) for the environment's current observation.</summary>
    public double Decide(AerationEnvironment env)
    {
        Span<float> a = stackalloc float[1];
        _agent.SelectAction(env.Observation, a, deterministic: true);
        return Math.Clamp(a[0], 0, 1);
    }

    public float[] ExportParameters() => _agent.ExportParameters();

    /// <summary>
    /// Trains a SAC agent (RLNet) for <paramref name="steps"/> hours of simulated operation. A few tens of thousands of steps (a few minutes
    /// of CPU) are enough to beat set-point control; re-train when tariffs or penalties change.
    /// </summary>
    public static AerationPolicy Train(int steps = 40_000, AerationScenario? scenario = null, int seed = 3, int[]? hidden = null)
    {
        var env = new AerationEnvironment(scenario, 72, (ulong)seed);
        var options = new SacOptions
        {
            HiddenSizes = hidden ?? [64, 64], LearningRate = 3e-3f, Gamma = 0.97f, BatchSize = 128, LearningStarts = 1_000, BufferCapacity = 100_000,
            AutoTuneTemperature = true,
        };
        var agent = new SacAgent(env.ObservationSpace, env.ActionSpace, options, new UniformReplayBuffer(options.BufferCapacity, env.ObservationSpace.FlatSize, 1), seed, null!);
        var report = Trainer.Train(env, agent, new TrainingOptions { MaxSteps = steps, Seed = seed, WindowSize = 20 });
        return new AerationPolicy(agent, report);
    }
}
