using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.Renewable;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Simulation;

/// <summary>Two-state generating unit with exponentially distributed up and down times.</summary>
/// <param name="CapacityMw">Available capacity when up [MW].</param>
/// <param name="MttfHours">Mean time to failure [h].</param>
/// <param name="MttrHours">Mean time to repair [h].</param>
public sealed record GeneratingUnit(string Name, double CapacityMw, double MttfHours, double MttrHours)
{
    /// <summary>Forced outage rate MTTR / (MTTF + MTTR).</summary>
    public double ForcedOutageRate => MttrHours / (MttfHours + MttrHours);
}

/// <summary>Generation adequacy indices.</summary>
/// <param name="LoleHoursPerYear">Loss of load expectation [h/yr].</param>
/// <param name="EensMwhPerYear">Expected energy not supplied [MWh/yr].</param>
/// <param name="LolfPerYear">Loss of load frequency [occurrences/yr] (NaN for the analytical method).</param>
/// <param name="Years">Simulated years (0 = analytical).</param>
/// <param name="CoefficientOfVariation">σ(EENS estimate)/EENS — the usual MC stopping criterion (0 for analytical).</param>
/// <param name="RunningLole">Running mean of LOLE after each simulated year (convergence plot).</param>
public sealed record AdequacyIndices(double LoleHoursPerYear, double EensMwhPerYear, double LolfPerYear, int Years, double CoefficientOfVariation,
    double[] RunningLole);

/// <summary>
/// Generation adequacy assessment: sequential (chronological) Monte-Carlo simulation of two-state units against an hourly load curve
/// (Billinton &amp; Li, "Reliability Assessment of Electric Power Systems Using Monte Carlo Methods"), and the analytical capacity outage
/// probability table (COPT) for validation.
/// </summary>
public static class AdequacyAssessment
{
    /// <param name="hourlyLoadMw">Hourly load for one year (any length; repeated each simulated year).</param>
    /// <param name="maxYears">Upper bound on simulated years.</param>
    /// <param name="targetCov">Stop when the coefficient of variation of EENS falls below this (0 = run all years).</param>
    public static AdequacyIndices SequentialMonteCarlo(IReadOnlyList<GeneratingUnit> units, IReadOnlyList<double> hourlyLoadMw, int maxYears = 500,
        int seed = 1234, double targetCov = 0, int minYears = 20)
    {
        var rng = new Random(seed);
        int nu = units.Count, H = hourlyLoadMw.Count;
        var up = new bool[nu]; var next = new double[nu];
        double Exp(double mean) => -mean * Math.Log(1 - rng.NextDouble());
        for (int i = 0; i < nu; i++)
        {
            up[i] = rng.NextDouble() >= units[i].ForcedOutageRate;           // start in steady-state distribution
            next[i] = Exp(up[i] ? units[i].MttfHours : units[i].MttrHours);
        }
        double clock = 0, sumLole = 0, sumEens = 0, sumEens2 = 0, sumLolf = 0;
        var running = new List<double>();
        int y = 0;
        for (; y < maxYears; y++)
        {
            double lole = 0, eens = 0, lolf = 0;
            bool prevShort = false;
            for (int h = 0; h < H; h++, clock++)
            {
                double cap = 0;
                for (int i = 0; i < nu; i++)
                {
                    while (next[i] <= clock)
                    {
                        up[i] = !up[i];
                        next[i] += Exp(up[i] ? units[i].MttfHours : units[i].MttrHours);
                    }
                    if (up[i]) cap += units[i].CapacityMw;
                }
                double deficit = hourlyLoadMw[h] - cap;
                bool shortH = deficit > 1e-9;
                if (shortH) { lole++; eens += deficit; if (!prevShort) lolf++; }
                prevShort = shortH;
            }
            sumLole += lole; sumEens += eens; sumEens2 += eens * eens; sumLolf += lolf;
            running.Add(sumLole / (y + 1));
            int n = y + 1;
            if (targetCov > 0 && n >= minYears && sumEens > 0)
            {
                double mean = sumEens / n, var = Math.Max(0, sumEens2 / n - mean * mean);
                if (Math.Sqrt(var / n) / mean < targetCov) { y++; break; }
            }
        }
        int years = Math.Max(1, y);
        double m = sumEens / years, v = Math.Max(0, sumEens2 / years - m * m);
        return new AdequacyIndices(sumLole / years, m, sumLolf / years, years, m > 0 ? Math.Sqrt(v / years) / m : 0, [.. running]);
    }

    /// <summary>Exact LOLE/EENS from the capacity outage probability table (recursive unit addition).</summary>
    public static AdequacyIndices Analytical(IReadOnlyList<GeneratingUnit> units, IReadOnlyList<double> hourlyLoadMw)
    {
        var table = CapacityOutageTable(units);
        double total = units.Sum(u => u.CapacityMw);
        double lole = 0, eens = 0;
        foreach (double load in hourlyLoadMw)
            foreach (var (outage, p) in table)
            {
                double deficit = load - (total - outage);
                if (deficit > 1e-9) { lole += p; eens += p * deficit; }
            }
        return new AdequacyIndices(lole, eens, double.NaN, 0, 0, []);
    }

    /// <summary>Capacity outage probability table: (capacity out [MW], exact probability), built by recursive convolution.</summary>
    public static IReadOnlyList<(double OutageMw, double Probability)> CapacityOutageTable(IReadOnlyList<GeneratingUnit> units)
    {
        var t = new Dictionary<double, double> { [0] = 1 };
        foreach (var u in units)
        {
            var n = new Dictionary<double, double>();
            foreach (var (c, p) in t)
            {
                n[c] = n.GetValueOrDefault(c) + p * (1 - u.ForcedOutageRate);
                double co = Math.Round(c + u.CapacityMw, 9);
                n[co] = n.GetValueOrDefault(co) + p * u.ForcedOutageRate;
            }
            t = n;
        }
        return t.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Hourly annual load curve [MW] from a daily shape, weekly factors and a seasonal cosine (deterministic), peak = <paramref name="peakMw"/>.</summary>
    public static double[] SyntheticAnnualLoad(double peakMw, double seasonalSwing = 0.1)
    {
        var v = new double[8760];
        for (int h = 0; h < 8760; h++)
        {
            int hod = h % 24, dow = h / 24 % 7;
            double daily = 0.7 + 0.3 * Math.Exp(-Math.Pow((hod - 19) / 4.0, 2)) + 0.1 * Math.Exp(-Math.Pow((hod - 11) / 3.0, 2));
            double week = dow >= 5 ? 0.85 : 1.0;
            double season = 1 - seasonalSwing * (1 + Math.Cos(2 * Math.PI * (h / 8760.0 - 0.55))) / 2;
            v[h] = daily * week * season;
        }
        double mx = v.Max();
        for (int h = 0; h < 8760; h++) v[h] *= peakMw / mx;
        return v;
    }
}

/// <summary>Named study scenarios derived from a base network by modification delegates; each build is an independent deep copy.</summary>
public sealed class ScenarioManager(PowerNetwork baseNetwork)
{
    private readonly List<(string Name, string? Description, Action<PowerNetwork> Modify)> _scenarios = [("Base", "Unmodified base case", _ => { })];

    public PowerNetwork BaseNetwork { get; } = baseNetwork;
    public IReadOnlyList<string> Names => _scenarios.Select(s => s.Name).ToList();

    public ScenarioManager Add(string name, Action<PowerNetwork> modify, string? description = null)
    {
        if (_scenarios.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"Scenario '{name}' exists.");
        _scenarios.Add((name, description, modify));
        return this;
    }

    public string? DescriptionOf(string name) => _scenarios.First(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Description;

    /// <summary>Clone of the base network with the scenario's modifications applied.</summary>
    public PowerNetwork Build(string name)
    {
        var s = _scenarios.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (s.Modify is null) throw new KeyNotFoundException($"Unknown scenario '{name}'.");
        var net = BaseNetwork.Clone();
        net.Name = $"{BaseNetwork.Name}:{s.Name}";
        s.Modify(net);
        return net;
    }

    /// <summary>Runs a study on every scenario (in parallel, results in registration order).</summary>
    public IReadOnlyList<(string Name, T Result)> RunAll<T>(Func<PowerNetwork, T> study) =>
        _scenarios.AsParallel().AsOrdered().Select(s => (s.Name, study(Build(s.Name)))).ToList();

    /// <summary>AC power flow summary per scenario.</summary>
    public IReadOnlyList<ScenarioSummary> RunPowerFlows(PowerFlowOptions? options = null) =>
        RunAll(n =>
        {
            try
            {
                var r = PowerFlowSolver.Solve(n, options);
                return new ScenarioSummary(n.Name, r.Converged, r.LossesMw, r.MinVoltageBus.Vm, r.MaxVoltageBus.Vm, r.MostLoadedBranch?.LoadingPercent ?? 0, r.TotalLoadMw);
            }
            catch (InvalidOperationException) { return new ScenarioSummary(n.Name, false, double.NaN, double.NaN, double.NaN, double.NaN, n.TotalLoadMw); }
        }).Select(t => t.Result with { Scenario = t.Name }).ToList();

    // common modifications
    public static Action<PowerNetwork> ScaleLoads(double factor) => n => { foreach (var l in n.Loads) { l.P *= factor; l.Q *= factor; } };
    public static Action<PowerNetwork> AddPv(int bus, double mw) => n => n.Generators.Add(new Generator { Bus = bus, P = mw, Pmax = mw, Kind = GeneratorKind.Solar, Name = $"PV@{bus}" });
    public static Action<PowerNetwork> OpenBranch(int from, int to) => n => { foreach (var b in n.Branches.Where(b => (b.From == from && b.To == to) || (b.From == to && b.To == from))) b.InService = false; };
    public static Action<PowerNetwork> AddCapacitor(int bus, double mvar) => n => n.GetBus(bus).Bs += mvar;
}

/// <summary>Power-flow summary of a scenario (MW, pu, %).</summary>
public sealed record ScenarioSummary(string Scenario, bool Converged, double LossesMw, double VminPu, double VmaxPu, double MaxLoadingPercent, double LoadMw);

/// <summary>DI facade for simulations.</summary>
public interface ISimulationService
{
    QstsResult RunQsts(QstsSimulation simulation);
    AdequacyIndices Adequacy(IReadOnlyList<GeneratingUnit> units, IReadOnlyList<double> hourlyLoadMw, int years = 200, int seed = 1234);
}

internal sealed class SimulationService : ISimulationService
{
    public QstsResult RunQsts(QstsSimulation simulation) => simulation.Run();
    public AdequacyIndices Adequacy(IReadOnlyList<GeneratingUnit> units, IReadOnlyList<double> hourlyLoadMw, int years = 200, int seed = 1234) =>
        AdequacyAssessment.SequentialMonteCarlo(units, hourlyLoadMw, years, seed);
}

public static class SimulationBuilderExtensions
{
    /// <summary>Registers <see cref="ISimulationService"/> (and the Renewable, Forecasting and PowerFlow modules).</summary>
    public static ElectricalBuilder AddSimulation(this ElectricalBuilder b)
    {
        b.AddRenewable().AddForecasting();
        if (b.TryAddModule("Simulation")) b.Services.TryAddSingleton<ISimulationService, SimulationService>();
        return b;
    }
}
