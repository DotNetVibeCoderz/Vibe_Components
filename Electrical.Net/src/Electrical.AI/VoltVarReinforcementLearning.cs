using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.StateEstimation;

namespace Electrical.AI;

/// <summary>A controllable reactive-power device: shunt capacitor bank (levels ≥ 0) or inverter (levels may be negative = absorbing).</summary>
/// <param name="StepMvar">Reactive power per level [Mvar] (capacitor: at 1 pu voltage).</param>
public sealed record VoltVarDevice(string Name, int BusId, double StepMvar, int MinLevel, int MaxLevel, int InitialLevel = 0, bool IsInverter = false)
{
    public int LevelCount => MaxLevel - MinLevel + 1;
}

/// <summary>Configuration of <see cref="VoltVarEnvironment"/>.</summary>
public sealed record VoltVarEnvironmentOptions
{
    /// <summary>Feeder (default: Baran &amp; Wu 33-bus, 12.66 kV).</summary>
    public Func<PowerNetwork> Network { get; init; } = TestCases.Case33BW;
    /// <summary>Default: 0.15 Mvar × 0–4 capacitor at bus 18, 0.3 Mvar × 0–4 at bus 30, ±0.2 Mvar (0.1 steps) inverter at bus 25.</summary>
    public IReadOnlyList<VoltVarDevice> Devices { get; init; } =
    [
        new("C18", 18, 0.15, 0, 4),
        new("C30", 30, 0.30, 0, 4),
        new("INV25", 25, 0.10, -2, 2, IsInverter: true),
    ];
    /// <summary>Steps (hours) per episode.</summary>
    public int EpisodeLength { get; init; } = 24;
    /// <summary>Daily load shape driving the feeder loads.</summary>
    public LoadProfileKind Profile { get; init; } = LoadProfileKind.Residential;
    /// <summary>Load multiplier at the profile peak (1 = Baran–Wu base case).</summary>
    public double PeakLoadScale { get; init; } = 1.0;
    /// <summary>Relative standard deviation of per-episode random load variation (0 = deterministic episodes).</summary>
    public double LoadNoise { get; init; }
    public double VoltageLowPu { get; init; } = 0.95;
    public double VoltageHighPu { get; init; } = 1.05;
    /// <summary>Reward weight per kW of losses.</summary>
    public double LossWeightPerKw { get; init; } = 0.01;
    /// <summary>Reward weight per pu of summed voltage-band violation.</summary>
    public double VoltagePenaltyPerPu { get; init; } = 20;
    /// <summary>Penalty per device operation (wear).</summary>
    public double SwitchingCost { get; init; } = 0.01;
}

/// <summary>One transition.</summary>
/// <param name="Observation">Next observation: bus voltages [pu], device levels (normalized −1..1) and time of day (0..1).</param>
/// <param name="Reward">−w_loss·P_loss[kW] − w_V·Σ violation[pu] − switching cost.</param>
public sealed record VoltVarStep(double[] Observation, double Reward, bool Done, double LossesKw, double MinVoltagePu, double MaxVoltagePu, int Violations, int Hour);

/// <summary>
/// Reinforcement-learning environment for Volt/VAR control on a distribution feeder (gym-style <see cref="Reset"/>/<see cref="Step"/>).
/// Each step is one hour: the agent moves one device by one level (or does nothing), the feeder is solved by AC power flow at that hour's load,
/// and the reward penalizes losses, voltage-band violations and switching. Discrete actions: 0 = no-op, 1 + 2d = raise device d, 2 + 2d = lower.
/// Power-flow results are cached per (hour, device levels) when episodes are deterministic, so tabular training is fast.
/// Dependency-free; observations/actions map directly onto Gravicode.RLNet agents (future integration).
/// </summary>
public sealed class VoltVarEnvironment
{
    private readonly PowerNetwork _net;
    private readonly (double P, double Q)[] _baseLoads;
    private readonly int[] _deviceGen;
    private readonly Dictionary<long, (double Loss, double Vmin, double Vmax, int Viol, double Pen, double[] V)> _cache = [];
    private readonly int[] _levels;
    private Random _rng;
    private double[] _noise;

    public VoltVarEnvironment(VoltVarEnvironmentOptions? options = null, int seed = 0)
    {
        Options = options ?? new VoltVarEnvironmentOptions();
        _net = Options.Network();
        _baseLoads = _net.Loads.Select(l => (l.P, l.Q)).ToArray();
        _deviceGen = new int[Options.Devices.Count];
        for (int d = 0; d < Options.Devices.Count; d++)
        {
            var dev = Options.Devices[d];
            _net.GetBus(dev.BusId); // validate
            if (dev.IsInverter)
            {
                _net.Generators.Add(new Generator { Bus = dev.BusId, Name = dev.Name, Kind = GeneratorKind.Solar, P = 0, Pmax = 0 });
                _deviceGen[d] = _net.Generators.Count - 1;
            }
            else _deviceGen[d] = -1;
        }
        _levels = Options.Devices.Select(d => d.InitialLevel).ToArray();
        _rng = new Random(seed);
        _noise = new double[_baseLoads.Length];
    }

    public VoltVarEnvironmentOptions Options { get; }
    public int Hour { get; private set; }
    /// <summary>Lowest / highest bus voltage of the last solved hour [pu].</summary>
    public double LastMinVoltagePu { get; private set; } = 1;
    public double LastMaxVoltagePu { get; private set; } = 1;
    public IReadOnlyList<int> Levels => _levels;
    public int ActionCount => 1 + 2 * Options.Devices.Count;
    public int ObservationSize => _net.BusCount + Options.Devices.Count + 1;
    /// <summary>Number of discrete states (hour × device-level combinations) for tabular methods.</summary>
    public int StateCount => Options.EpisodeLength * Options.Devices.Aggregate(1, (a, d) => a * d.LevelCount);

    /// <summary>Discrete state index (hour, levels) of the current state.</summary>
    public int StateIndex
    {
        get
        {
            int code = 0;
            for (int d = 0; d < _levels.Length; d++) code = code * Options.Devices[d].LevelCount + (_levels[d] - Options.Devices[d].MinLevel);
            return Math.Min(Hour, Options.EpisodeLength - 1) * Options.Devices.Aggregate(1, (a, x) => a * x.LevelCount) + code;
        }
    }

    public string DescribeAction(int action) => action == 0 ? "no-op"
        : $"{(action % 2 == 1 ? "raise" : "lower")} {Options.Devices[(action - 1) / 2].Name}";

    /// <summary>Starts a new episode at hour 0 with initial device levels; returns the first observation.</summary>
    public double[] Reset(int? seed = null)
    {
        if (seed is { } s) _rng = new Random(s);
        Hour = 0;
        for (int d = 0; d < _levels.Length; d++) _levels[d] = Options.Devices[d].InitialLevel;
        for (int i = 0; i < _noise.Length; i++) _noise[i] = Options.LoadNoise > 0 ? Options.LoadNoise * MeasurementGenerator.Gaussian(_rng) : 0;
        var e = Evaluate(0);
        LastMinVoltagePu = e.Vmin; LastMaxVoltagePu = e.Vmax;
        return Observe(e.V, 0);
    }

    /// <summary>Applies an action at the current hour, solves the feeder and advances one hour.</summary>
    public VoltVarStep Step(int action)
    {
        if (action < 0 || action >= ActionCount) throw new ArgumentOutOfRangeException(nameof(action));
        if (Hour >= Options.EpisodeLength) throw new InvalidOperationException("Episode finished; call Reset().");
        bool moved = false;
        if (action > 0)
        {
            int d = (action - 1) / 2;
            var dev = Options.Devices[d];
            int nl = Math.Clamp(_levels[d] + (action % 2 == 1 ? 1 : -1), dev.MinLevel, dev.MaxLevel);
            moved = nl != _levels[d];
            _levels[d] = nl;
        }
        var e = Evaluate(Hour);
        LastMinVoltagePu = e.Vmin; LastMaxVoltagePu = e.Vmax;
        double reward = -Options.LossWeightPerKw * e.Loss - Options.VoltagePenaltyPerPu * e.Pen - (moved ? Options.SwitchingCost : 0);
        int h = Hour;
        Hour++;
        bool done = Hour >= Options.EpisodeLength;
        return new VoltVarStep(Observe(e.V, Hour), reward, done, e.Loss, e.Vmin, e.Vmax, e.Viol, h);
    }

    /// <summary>Load multiplier at an hour (profile shape × peak scale).</summary>
    public double LoadScaleAt(int hour) => Options.PeakLoadScale * SyntheticProfiles.Shape(Options.Profile, hour % 24, DayOfWeek.Monday);

    private double[] Observe(double[] v, int hour)
    {
        var o = new double[ObservationSize];
        Array.Copy(v, o, v.Length);
        for (int d = 0; d < _levels.Length; d++)
        {
            var dev = Options.Devices[d];
            o[v.Length + d] = dev.LevelCount <= 1 ? 0 : 2.0 * (_levels[d] - dev.MinLevel) / (dev.LevelCount - 1) - 1;
        }
        o[^1] = (double)hour / Options.EpisodeLength;
        return o;
    }

    private (double Loss, double Vmin, double Vmax, int Viol, double Pen, double[] V) Evaluate(int hour)
    {
        long key = -1;
        if (Options.LoadNoise == 0)
        {
            key = hour;
            for (int d = 0; d < _levels.Length; d++) key = key * 64 + (_levels[d] - Options.Devices[d].MinLevel);
            if (_cache.TryGetValue(key, out var c)) return c;
        }
        double k = LoadScaleAt(hour);
        for (int i = 0; i < _baseLoads.Length; i++)
        {
            double f = k * (1 + _noise[i]);
            _net.Loads[i].P = _baseLoads[i].P * f;
            _net.Loads[i].Q = _baseLoads[i].Q * f;
        }
        var baseBs = new Dictionary<int, double>();
        for (int d = 0; d < _levels.Length; d++)
        {
            var dev = Options.Devices[d];
            double q = _levels[d] * dev.StepMvar;
            if (dev.IsInverter) { var g = _net.Generators[_deviceGen[d]]; g.Q = q; g.Qmax = q; g.Qmin = q; }
            else { var b = _net.GetBus(dev.BusId); baseBs.TryAdd(dev.BusId, b.Bs); b.Bs += q; }
        }
        var pf = PowerFlowSolver.Solve(_net);
        foreach (var (bus, bs) in baseBs) _net.GetBus(bus).Bs = bs;
        (double, double, double, int, double, double[]) r;
        if (!pf.Converged) r = (1e4, 0, 0, _net.BusCount, 10, new double[_net.BusCount]);
        else
        {
            var v = pf.Buses.Select(b => b.Vm).ToArray();
            double pen = 0; int viol = 0;
            foreach (var x in v)
            {
                double e = Math.Max(0, Options.VoltageLowPu - x) + Math.Max(0, x - Options.VoltageHighPu);
                if (e > 0) { pen += e; viol++; }
            }
            r = (pf.LossesMw * 1000, v.Min(), v.Max(), viol, pen, v);
        }
        if (key >= 0) _cache[key] = r;
        return r;
    }
}

/// <summary>Summary of one evaluated episode.</summary>
public sealed record VoltVarEpisode(double TotalReward, double EnergyLossesKwh, int ViolationBusHours, double MinVoltagePu, IReadOnlyList<int> Actions);

/// <summary>Training curve of <see cref="TabularQLearning.Train"/>.</summary>
public sealed record QLearningTrainingResult(double[] EpisodeRewards, int Episodes);

/// <summary>
/// Tabular Q-learning (Watkins 1989) with ε-greedy exploration decaying linearly from <see cref="EpsilonStart"/> to <see cref="EpsilonEnd"/>,
/// deterministic seed. Baseline for <see cref="VoltVarEnvironment"/>; deep RL can be plugged in through Gravicode.RLNet.
/// </summary>
public sealed class TabularQLearning
{
    private readonly double[,] _q;
    private readonly Random _rng;

    public TabularQLearning(int stateCount, int actionCount, int seed = 1)
    {
        _q = new double[stateCount, actionCount];
        _rng = new Random(seed);
        ActionCount = actionCount;
    }

    public int ActionCount { get; }
    public double Alpha { get; init; } = 0.3;
    public double Gamma { get; init; } = 0.95;
    public double EpsilonStart { get; init; } = 1.0;
    public double EpsilonEnd { get; init; } = 0.02;
    public double[,] Q => _q;

    /// <summary>Greedy action (ties → lowest index, i.e. prefer no-op).</summary>
    public int Greedy(int state)
    {
        int best = 0;
        for (int a = 1; a < ActionCount; a++) if (_q[state, a] > _q[state, best] + 1e-12) best = a;
        return best;
    }

    public int Act(int state, double epsilon) => _rng.NextDouble() < epsilon ? _rng.Next(ActionCount) : Greedy(state);

    public void Update(int s, int a, double r, int s2, bool done)
    {
        double target = r;
        if (!done) { double m = double.NegativeInfinity; for (int b = 0; b < ActionCount; b++) m = Math.Max(m, _q[s2, b]); target += Gamma * m; }
        _q[s, a] += Alpha * (target - _q[s, a]);
    }

    public QLearningTrainingResult Train(VoltVarEnvironment env, int episodes = 400)
    {
        var rewards = new double[episodes];
        for (int ep = 0; ep < episodes; ep++)
        {
            double eps = EpsilonStart + (EpsilonEnd - EpsilonStart) * ep / Math.Max(1, episodes - 1);
            env.Reset();
            int s = env.StateIndex;
            double total = 0;
            while (true)
            {
                int a = Act(s, eps);
                var st = env.Step(a);
                int s2 = env.StateIndex;
                Update(s, a, st.Reward, s2, st.Done);
                total += st.Reward;
                s = s2;
                if (st.Done) break;
            }
            rewards[ep] = total;
        }
        return new QLearningTrainingResult(rewards, episodes);
    }

    /// <summary>Greedy policy over the learned table.</summary>
    public Func<VoltVarEnvironment, int> Policy => env => Greedy(env.StateIndex);
}

/// <summary>Reference policies and episode evaluation.</summary>
public static class VoltVarPolicies
{
    /// <summary>Never operates any device.</summary>
    public static Func<VoltVarEnvironment, int> DoNothing { get; } = _ => 0;

    /// <summary>
    /// Simple local rule: if the lowest voltage of the last solve is below the band raise the first device that can still be raised; if the
    /// highest voltage is above the band lower one; otherwise do nothing.
    /// </summary>
    public static Func<VoltVarEnvironment, int> VoltageRule { get; } = env =>
    {
        var devs = env.Options.Devices;
        if (env.LastMinVoltagePu < env.Options.VoltageLowPu)
            for (int d = 0; d < devs.Count; d++) if (env.Levels[d] < devs[d].MaxLevel) return 1 + 2 * d;
        if (env.LastMaxVoltagePu > env.Options.VoltageHighPu)
            for (int d = 0; d < devs.Count; d++) if (env.Levels[d] > devs[d].MinLevel) return 2 + 2 * d;
        return 0;
    };

    /// <summary>Runs one episode with a policy.</summary>
    public static VoltVarEpisode Evaluate(VoltVarEnvironment env, Func<VoltVarEnvironment, int> policy, int? seed = null)
    {
        env.Reset(seed);
        double total = 0, loss = 0, vmin = double.PositiveInfinity;
        int viol = 0;
        var actions = new List<int>();
        while (true)
        {
            int a = policy(env);
            actions.Add(a);
            var st = env.Step(a);
            total += st.Reward; loss += st.LossesKw; viol += st.Violations; vmin = Math.Min(vmin, st.MinVoltagePu);
            if (st.Done) break;
        }
        return new VoltVarEpisode(total, loss, viol, vmin, actions);
    }
}
