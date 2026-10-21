using RLNet;
using RLNet.Agents;
using RLNet.Environments;
using RLNet.Spaces;

namespace Blazor.AI.Analytics;

public readonly record struct EpisodeStat(int Episode, double Return, int Steps, double MovingAverage, double Epsilon);

/// <summary>A finished (or stopped) training session; agents are kept so they can be replayed.</summary>
public sealed class TrainingSession
{
    public required string Name { get; init; }

    public required string Environment { get; init; }

    public required string Algorithm { get; init; }

    public List<EpisodeStat> Episodes { get; } = [];

    public IAgent? Agent { get; set; }

    public object? EnvironmentInstance { get; set; }

    public TimeSpan Duration { get; set; }

    public double BestAverage => Episodes.Count == 0 ? 0 : Episodes.Max(e => e.MovingAverage);

    public double FinalAverage => Episodes.Count == 0 ? 0 : Episodes[^1].MovingAverage;
}

/// <summary>One frame of a simulated episode, with the environment's render state.</summary>
public sealed record SimulationFrame(int Step, int Action, double Reward, double Return, IReadOnlyDictionary<string, double> State, bool Done);

/// <summary>Training and simulation loops over RLNet environments and agents. Runs off the UI thread and reports progress.</summary>
public static class RlTrainer
{
    public static IReadOnlyList<EnvironmentEntry> Environments => Catalog.Environments.Where(e => e.Kind != EnvironmentKind.MultiAgent).ToArray();

    public static async Task<TrainingSession> TrainAsync(
        string environmentName,
        Algorithm algorithm,
        int episodes,
        IProgress<EpisodeStat>? progress = null,
        Func<object>? environmentFactory = null,
        int seed = 7,
        CancellationToken cancellationToken = default)
    {
        var environment = environmentFactory?.Invoke() ?? Catalog.Find(environmentName).Create();
        var session = new TrainingSession { Name = $"{algorithm} · {environmentName}", Environment = environmentName, Algorithm = algorithm.ToString(), EnvironmentInstance = environment };
        var started = DateTime.UtcNow;

        await Task.Run(async () =>
        {
            switch (environment)
            {
                case IDiscreteEnvironment discrete:
                    var agent = CreateDiscreteAgent(discrete, algorithm, seed);
                    session.Agent = agent;
                    await LoopAsync(session, episodes, progress, cancellationToken, ep =>
                    {
                        discrete.Reset(seed + ep);
                        var obs = discrete.Observation.ToArray();
                        double total = 0;
                        var steps = 0;
                        while (steps++ < 2_000)
                        {
                            var action = agent.SelectAction(obs);
                            var result = discrete.Step(action);
                            var next = discrete.Observation.ToArray();
                            agent.Observe(obs, action, result.Reward, next, result.Terminated, result.Truncated);
                            total += result.Reward;
                            obs = next;
                            if (result.Done) break;
                        }

                        agent.OnEpisodeEnd();
                        agent.SetProgress((ep + 1f) / episodes);
                        return (total, steps, agent.Metrics.Epsilon);
                    }).ConfigureAwait(false);
                    break;

                case IContinuousEnvironment continuous:
                    var cagent = Catalog.CreateAgent(algorithm is Algorithm.Td3 ? Algorithm.Td3 : Algorithm.Sac, continuous.ObservationSpace, (BoxSpace)continuous.ActionSpace, seed);
                    session.Agent = cagent;
                    var action = new float[((BoxSpace)continuous.ActionSpace).FlatSize];
                    await LoopAsync(session, episodes, progress, cancellationToken, ep =>
                    {
                        continuous.Reset(seed + ep);
                        var obs = continuous.Observation.ToArray();
                        double total = 0;
                        var steps = 0;
                        while (steps++ < 2_000)
                        {
                            cagent.SelectAction(obs, action);
                            var result = continuous.Step(action);
                            var next = continuous.Observation.ToArray();
                            cagent.Observe(obs, action, result.Reward, next, result.Terminated, result.Truncated);
                            total += result.Reward;
                            obs = next;
                            if (result.Done) break;
                        }

                        cagent.OnEpisodeEnd();
                        cagent.SetProgress((ep + 1f) / episodes);
                        return (total, steps, cagent.Metrics.Temperature);
                    }).ConfigureAwait(false);
                    break;

                default:
                    throw new NotSupportedException($"Environment '{environmentName}' is not supported here.");
            }
        }, cancellationToken).ConfigureAwait(false);

        session.Duration = DateTime.UtcNow - started;
        return session;
    }

    public static IDiscreteAgent CreateDiscreteAgent(IDiscreteEnvironment environment, Algorithm algorithm, int seed)
    {
        if (algorithm == Algorithm.QLearning)
        {
            StateKeyEncoder encoder = environment is DesignedGridWorld grid
                ? (ReadOnlySpan<float> o) => grid.KeyOf(o)
                : environment.ObservationSpace is BoxSpace box
                    ? StateDiscretizer.ForBox(box, Enumerable.Repeat(10, box.FlatSize).ToArray(), 3f)
                    : (ReadOnlySpan<float> o) => (long)o[0];
            return new QTableAgent(environment.ActionSpace, encoder, new QTableOptions { LearningRate = 0.2f, Gamma = 0.95f, Epsilon = Schedule.Linear(1f, 0.05f, 0.7f) }, seed);
        }

        return Catalog.CreateAgent(algorithm, environment.ObservationSpace, environment.ActionSpace, seed);
    }

    /// <summary>Runs one greedy episode with a trained agent and records frames for replay.</summary>
    public static IReadOnlyList<SimulationFrame> Simulate(TrainingSession session, int seed = 123, int maxSteps = 500)
    {
        var frames = new List<SimulationFrame>();
        if (session.EnvironmentInstance is IDiscreteEnvironment env && session.Agent is IDiscreteAgent agent)
        {
            env.Reset(seed);
            frames.Add(new SimulationFrame(0, -1, 0, 0, RenderState(env), false));
            double total = 0;
            for (var step = 1; step <= maxSteps; step++)
            {
                var action = agent.SelectAction(env.Observation, deterministic: true);
                var result = env.Step(action);
                total += result.Reward;
                frames.Add(new SimulationFrame(step, action, result.Reward, total, RenderState(env), result.Done));
                if (result.Done) break;
            }
        }
        else if (session.EnvironmentInstance is IContinuousEnvironment cenv && session.Agent is IContinuousAgent cagent)
        {
            cenv.Reset(seed);
            var action = new float[((BoxSpace)cenv.ActionSpace).FlatSize];
            double total = 0;
            frames.Add(new SimulationFrame(0, -1, 0, 0, RenderState(cenv), false));
            for (var step = 1; step <= maxSteps; step++)
            {
                cagent.SelectAction(cenv.Observation, action, deterministic: true);
                var result = cenv.Step(action);
                total += result.Reward;
                frames.Add(new SimulationFrame(step, 0, result.Reward, total, RenderState(cenv), result.Done));
                if (result.Done) break;
            }
        }

        return frames;
    }

    /// <summary>Environment-specific values used by the simulation renderer.</summary>
    public static Dictionary<string, double> RenderState(object environment)
    {
        var state = new Dictionary<string, double>();
        switch (environment)
        {
            case RLNet.Environments.Classic.CartPoleEnvironment cart:
                state["x"] = cart.CartPosition;
                state["angle"] = cart.PoleAngle;
                break;
            case RLNet.Environments.Classic.MountainCarEnvironment car:
                state["x"] = car.Position;
                state["v"] = car.Velocity;
                break;
            case RLNet.Environments.Classic.LunarLanderEnvironment lander:
                state["x"] = lander.X;
                state["y"] = lander.Y;
                state["angle"] = lander.Angle;
                state["main"] = lander.MainEngineFiring ? 1 : 0;
                state["landed"] = lander.Landed ? 1 : 0;
                break;
            case RLNet.Environments.Classic.GridWorldEnvironment grid:
                state["x"] = grid.AgentX;
                state["y"] = grid.AgentY;
                state["w"] = grid.Width;
                state["h"] = grid.Height;
                break;
            case DesignedGridWorld designed:
                state["x"] = designed.AgentX;
                state["y"] = designed.AgentY;
                state["w"] = designed.Width;
                state["h"] = designed.Height;
                break;
            case RLNet.Environments.Control.PendulumEnvironment pendulum:
                state["angle"] = pendulum.Angle;
                state["torque"] = pendulum.LastTorque;
                break;
            case RLNet.Environments.Domain.SupplyChainEnvironment supply:
                state["inventory"] = supply.Inventory;
                state["demand"] = supply.LastDemand;
                state["profit"] = supply.CumulativeProfit;
                break;
            case RLNet.Environments.Domain.TradingEnvironment trading:
                state["price"] = trading.CurrentPrice;
                state["cash"] = trading.Cash;
                state["shares"] = trading.Shares;
                break;
            case IEnvironment generic:
                var obs = generic.Observation;
                for (var i = 0; i < Math.Min(obs.Length, 6); i++)
                {
                    state[$"o{i}"] = obs[i];
                }

                break;
        }

        return state;
    }

    private static async Task LoopAsync(TrainingSession session, int episodes, IProgress<EpisodeStat>? progress, CancellationToken cancellationToken, Func<int, (double Return, int Steps, double Epsilon)> episode)
    {
        var window = new Queue<double>();
        double sum = 0;
        for (var ep = 0; ep < episodes; ep++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var (ret, steps, epsilon) = episode(ep);
            window.Enqueue(ret);
            sum += ret;
            if (window.Count > 20)
            {
                sum -= window.Dequeue();
            }

            var stat = new EpisodeStat(ep + 1, ret, steps, sum / window.Count, epsilon);
            session.Episodes.Add(stat);
            progress?.Report(stat);
            if (ep % 10 == 0)
            {
                // Yield so single-threaded hosts (WebAssembly) keep the UI responsive.
                await Task.Yield();
            }
        }
    }
}
