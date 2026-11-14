using System.Diagnostics;
using System.Globalization;
using RLNet.Agents;
using RLNet.Environments;
using RLNet.Persistence;
using RLNet.Spaces;
using RLNet.Training;

namespace Electrical.AI.Learning;

/// <summary>
/// <see cref="VoltVarEnvironment"/> exposed as a Gravicode.RLNet discrete environment. Observation (float):
/// per-bus voltage deviation (V − 1)/0.05 pu, device levels (−1..1), and the hour as fraction, sin and cos.
/// Actions are those of the wrapped environment (0 = no-op, 1 + 2d raise device d, 2 + 2d lower). The episode end (hour 24) is a
/// true terminal state of the finite-horizon task (the hour is part of the observation). Rewards are scaled by
/// <see cref="RewardScale"/> for learning; unscaled values are reported by <see cref="LastStep"/>.
/// </summary>
public sealed class VoltVarRlEnvironment : DiscreteEnvironmentBase
{
    private double[] _obs = [];
    private int? _pendingSeed;

    public VoltVarRlEnvironment(VoltVarEnvironmentOptions? options = null, int seed = 0, float rewardScale = 0.1f)
        : this(new VoltVarEnvironment(options, seed), rewardScale) { }

    public VoltVarRlEnvironment(VoltVarEnvironment inner, float rewardScale = 0.1f)
        : base(BoxSpace.Unbounded(inner.ObservationSize + 2), new DiscreteSpace(inner.ActionCount), inner.Options.EpisodeLength)
    {
        Inner = inner;
        RewardScale = rewardScale;
    }

    /// <summary>The wrapped Electrical.AI environment (power flow, reward, caches).</summary>
    public VoltVarEnvironment Inner { get; }
    public float RewardScale { get; }
    /// <summary>Last unscaled transition.</summary>
    public VoltVarStep? LastStep { get; private set; }
    public override string Name => "VoltVar";

    protected override void OnReset()
    {
        // Stochastic episodes draw their load noise from RLNet's seeded generator so Reset(seed) is reproducible.
        int? seed = _pendingSeed ?? (Inner.Options.LoadNoise > 0 ? Random.NextInt(int.MaxValue) : null);
        _pendingSeed = null;
        _obs = Inner.Reset(seed);
        LastStep = null;
    }

    /// <summary>
    /// Starts an episode whose load noise is drawn exactly as <c>VoltVarEnvironment.Reset(seed)</c> would, so greedy evaluations match
    /// <see cref="VoltVarPolicies.Evaluate"/> with the same seed.
    /// </summary>
    public void ResetEpisode(int? seed)
    {
        _pendingSeed = seed;
        Reset(seed);
    }

    protected override StepResult OnStep(int action)
    {
        var st = Inner.Step(action);
        LastStep = st;
        _obs = st.Observation;
        return Advance((float)(st.Reward * RewardScale), st.Done);
    }

    protected override void WriteObservation(Span<float> destination)
    {
        int nb = Inner.ObservationSize - Inner.Options.Devices.Count - 1;
        for (int i = 0; i < nb; i++) destination[i] = (float)((_obs[i] - 1) / 0.05);
        for (int i = nb; i < _obs.Length - 1; i++) destination[i] = (float)_obs[i];
        double h = _obs[^1];
        destination[_obs.Length - 1] = (float)h;
        destination[_obs.Length] = (float)Math.Sin(2 * Math.PI * h);
        destination[_obs.Length + 1] = (float)Math.Cos(2 * Math.PI * h);
    }
}

/// <summary>Deep-RL algorithm used by <see cref="VoltVarAgentTrainer"/>.</summary>
public enum VoltVarRlAlgorithm { Dqn, Ppo, A2C }

/// <summary>Training budget and hyper-parameters of <see cref="VoltVarAgentTrainer.Train"/>.</summary>
public sealed record VoltVarRlOptions
{
    public VoltVarRlAlgorithm Algorithm { get; init; } = VoltVarRlAlgorithm.Dqn;
    /// <summary>Environment steps (24 per episode). The default (≈ 250 episodes) trains in a few seconds; use <see cref="Long"/> for demos.</summary>
    public long Steps { get; init; } = 6_000;
    public int[] HiddenSizes { get; init; } = [64, 64];
    public float LearningRate { get; init; } = 1e-3f;
    public float Gamma { get; init; } = 0.97f;
    public int Seed { get; init; } = 1;
    public float RewardScale { get; init; } = 0.1f;

    /// <summary>A longer budget (≈ 2 000 episodes) for demos and the CLI.</summary>
    public static VoltVarRlOptions Long(VoltVarRlAlgorithm algorithm = VoltVarRlAlgorithm.Dqn) => new() { Algorithm = algorithm, Steps = 48_000 };
}

/// <summary>A trained RLNet agent bound to a Volt/VAR environment.</summary>
public sealed class VoltVarRlAgent
{
    internal VoltVarRlAgent(IDiscreteAgent agent, VoltVarRlEnvironment env, VoltVarRlOptions options, TrainingReport? report)
    {
        Agent = agent; Environment = env; Options = options; Report = report;
    }

    /// <summary>The RLNet agent (DQN, PPO or A2C).</summary>
    public IDiscreteAgent Agent { get; }
    public VoltVarRlEnvironment Environment { get; }
    public VoltVarRlOptions Options { get; }
    /// <summary>RLNet training report (null for a loaded agent).</summary>
    public TrainingReport? Report { get; }

    /// <summary>Greedy action for an RLNet observation.</summary>
    public int Act(ReadOnlySpan<float> observation) => Agent.SelectAction(observation, deterministic: true);

    /// <summary>Runs one greedy episode and returns the unscaled summary (same accounting as <see cref="VoltVarPolicies.Evaluate"/>).</summary>
    public VoltVarEpisode Evaluate(int? seed = null)
    {
        var env = Environment;
        env.ResetEpisode(seed);
        var obs = new float[env.ObservationSpace.FlatSize];
        double total = 0, loss = 0, vmin = double.PositiveInfinity;
        int viol = 0;
        var actions = new List<int>();
        while (true)
        {
            env.Observation.CopyTo(obs);
            int a = Act(obs);
            actions.Add(a);
            var r = env.Step(a);
            var st = env.LastStep!;
            total += st.Reward; loss += st.LossesKw; viol += st.Violations; vmin = Math.Min(vmin, st.MinVoltagePu);
            if (r.Done) break;
        }
        return new VoltVarEpisode(total, loss, viol, vmin, actions);
    }

    /// <summary>Saves the network weights with RLNet's checkpoint format.</summary>
    public void Save(string path)
    {
        if (Agent is not ICheckpointable c) throw new NotSupportedException($"{Agent.Name} cannot be checkpointed.");
        Checkpoint.Save(path, c);
    }

    /// <summary>Recreates an agent with the same options and loads its weights from <paramref name="path"/>.</summary>
    public static VoltVarRlAgent Load(string path, VoltVarRlOptions options, VoltVarEnvironmentOptions? environment = null)
    {
        var env = new VoltVarRlEnvironment(environment, options.Seed, options.RewardScale);
        var agent = VoltVarAgentTrainer.CreateAgent(env, options);
        Checkpoint.Load(path, (ICheckpointable)agent);
        return new VoltVarRlAgent(agent, env, options, null);
    }
}

/// <summary>Average episode reward of every compared policy.</summary>
public sealed record VoltVarComparison(double DoNothing, double VoltageRule, double TabularQLearning, double DeepRl, string DeepRlName,
    VoltVarEpisode DeepRlEpisode, double DeepRlTrainingSeconds)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"do-nothing {DoNothing:0.00}, voltage rule {VoltageRule:0.00}, tabular Q {TabularQLearning:0.00}, {DeepRlName} {DeepRl:0.00} (trained in {DeepRlTrainingSeconds:0.0} s)");
}

/// <summary>Trains Gravicode.RLNet agents (DQN / PPO / A2C) on the Volt/VAR task, deterministically for a fixed seed.</summary>
public static class VoltVarAgentTrainer
{
    /// <summary>Builds an untrained RLNet agent for <paramref name="env"/>.</summary>
    public static IDiscreteAgent CreateAgent(VoltVarRlEnvironment env, VoltVarRlOptions o)
    {
        var obs = env.ObservationSpace;
        var act = env.ActionSpace;
        return o.Algorithm switch
        {
            VoltVarRlAlgorithm.Ppo => new PpoAgent(obs, act, new PpoOptions
            {
                HiddenSizes = o.HiddenSizes, LearningRate = o.LearningRate * 0.5f, Gamma = o.Gamma, RolloutLength = 240, MinibatchSize = 60,
                Epochs = 6, EntropyCoefficient = 0.01f,
            }, seed: o.Seed),
            VoltVarRlAlgorithm.A2C => new A2CAgent(obs, act, new A2COptions
            {
                HiddenSizes = o.HiddenSizes, LearningRate = o.LearningRate, Gamma = o.Gamma, RolloutLength = 24,
            }, seed: o.Seed),
            _ => new DqnAgent(obs, act, new DqnOptions
            {
                HiddenSizes = o.HiddenSizes, LearningRate = o.LearningRate, Gamma = o.Gamma, BatchSize = 32, LearningStarts = 240,
                TargetUpdateInterval = 200, BufferCapacity = 20_000, Epsilon = Schedule.Linear(1f, 0.02f, 0.6f), PrioritizedReplay = false,
            }, seed: o.Seed),
        };
    }

    /// <summary>Trains an agent on a fresh environment built from <paramref name="environment"/>.</summary>
    public static VoltVarRlAgent Train(VoltVarEnvironmentOptions? environment = null, VoltVarRlOptions? options = null)
    {
        var o = options ?? new VoltVarRlOptions();
        var env = new VoltVarRlEnvironment(environment, o.Seed, o.RewardScale);
        var agent = CreateAgent(env, o);
        var report = Trainer.Train(env, agent, new TrainingOptions { MaxSteps = o.Steps, MaxEpisodes = int.MaxValue, Seed = o.Seed });
        return new VoltVarRlAgent(agent, env, o, report);
    }

    /// <summary>
    /// Compares <see cref="VoltVarPolicies.DoNothing"/>, <see cref="VoltVarPolicies.VoltageRule"/>, <see cref="TabularQLearning"/>
    /// (<paramref name="tabularEpisodes"/> episodes) and a deep-RL agent on the same environment, averaging greedy episode rewards over
    /// <paramref name="evaluationEpisodes"/> seeded episodes (all identical when the environment has no load noise).
    /// </summary>
    public static VoltVarComparison Compare(VoltVarEnvironmentOptions? environment = null, VoltVarRlOptions? options = null, int tabularEpisodes = 400,
        int evaluationEpisodes = 1)
    {
        var o = options ?? new VoltVarRlOptions();
        var sw = Stopwatch.StartNew();
        var rl = Train(environment, o);
        double secs = sw.Elapsed.TotalSeconds;
        var env = new VoltVarEnvironment(environment, o.Seed);
        var q = new TabularQLearning(env.StateCount, env.ActionCount, o.Seed);
        q.Train(env, tabularEpisodes);
        double Avg(Func<int, double> f) => Enumerable.Range(0, evaluationEpisodes).Average(i => f(1000 + i));
        double none = Avg(s => VoltVarPolicies.Evaluate(env, VoltVarPolicies.DoNothing, s).TotalReward);
        double rule = Avg(s => VoltVarPolicies.Evaluate(env, VoltVarPolicies.VoltageRule, s).TotalReward);
        double tab = Avg(s => VoltVarPolicies.Evaluate(env, q.Policy, s).TotalReward);
        var last = rl.Evaluate(1000);
        double deep = Avg(s => rl.Evaluate(s).TotalReward);
        return new VoltVarComparison(none, rule, tab, deep, rl.Agent.Name, last, secs);
    }
}
