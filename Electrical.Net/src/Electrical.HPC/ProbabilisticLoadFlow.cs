using System.Diagnostics;
using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Native;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.HPC;

/// <summary>Runs many independent power flows in parallel (scenario sweeps, what-if batches).</summary>
public static class BatchPowerFlow
{
    /// <summary>Solves every scenario in parallel; results keep the input order.</summary>
    public static PowerFlowResult[] Run(IReadOnlyList<PowerNetwork> scenarios, PowerFlowOptions? options = null, int maxDegreeOfParallelism = -1)
    {
        var results = new PowerFlowResult[scenarios.Count];
        Parallel.For(0, scenarios.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            i => results[i] = PowerFlowSolver.Solve(scenarios[i], options));
        return results;
    }

    /// <summary>Load-scaling sweep: solves the network with all loads multiplied by each factor.</summary>
    public static (double Factor, PowerFlowResult Result)[] LoadSweep(PowerNetwork net, IEnumerable<double> factors, PowerFlowOptions? options = null)
    {
        var fs = factors.ToArray();
        var scen = fs.Select(f =>
        {
            var c = net.Clone();
            foreach (var l in c.Loads) { l.P *= f; l.Q *= f; }
            return c;
        }).ToArray();
        var r = Run(scen, options);
        return fs.Select((f, i) => (f, r[i])).ToArray();
    }
}

public sealed record ProbabilisticLoadFlowOptions
{
    public int Samples { get; init; } = 1000;
    /// <summary>Standard deviation of each load as a fraction of its mean (independent Gaussian).</summary>
    public double LoadStdFraction { get; init; } = 0.1;
    /// <summary>Optional common (correlated) component as a fraction of load, applied to all loads per sample.</summary>
    public double SystemStdFraction { get; init; } = 0.05;
    public ulong Seed { get; init; } = 2026;
    public PowerFlowOptions PowerFlow { get; init; } = PowerFlowOptions.Default with { FlatStart = false };
    public int MaxDegreeOfParallelism { get; init; } = -1;
}

public sealed record BusVoltageStatistics(int BusId, double Mean, double StdDev, double P05, double P95, double ProbabilityUnderVoltage, double ProbabilityOverVoltage);
public sealed record BranchLoadingStatistics(int Index, int From, int To, double MeanLoadingPercent, double P95LoadingPercent, double ProbabilityOverload);

public sealed class ProbabilisticLoadFlowResult
{
    public required int Samples { get; init; }
    public required int Converged { get; init; }
    public required IReadOnlyList<BusVoltageStatistics> Buses { get; init; }
    public required IReadOnlyList<BranchLoadingStatistics> Branches { get; init; }
    /// <summary>Losses [MW] of each converged sample.</summary>
    public required double[] LossesMw { get; init; }
    /// <summary>Minimum system voltage [pu] of each converged sample.</summary>
    public required double[] MinVoltage { get; init; }
    public required string Backend { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double MeanLossesMw => Stats.Mean(LossesMw);
    public double ProbabilityAnyUnderVoltage(double vmin = 0.95) => MinVoltage.Length == 0 ? 0 : MinVoltage.Count(v => v < vmin) / (double)MinVoltage.Length;
}

/// <summary>
/// Monte-Carlo probabilistic load flow: loads are sampled from Gaussian distributions (independent + common component),
/// each sample is solved with AC Newton–Raphson in parallel, and voltage / loading statistics are aggregated.
/// Sampling uses the deterministic SplitMix64 + Box–Muller generator from <see cref="GridKernels"/> (native when available).
/// </summary>
public static class ProbabilisticLoadFlow
{
    public static ProbabilisticLoadFlowResult Run(PowerNetwork net, ProbabilisticLoadFlowOptions? options = null)
    {
        options ??= new();
        var sw = Stopwatch.StartNew();
        int s = options.Samples, nl = net.Loads.Count, nb = net.BusCount, nbr = net.Branches.Count;
        var indep = new double[s * nl];
        var common = new double[s];
        GridKernels.NormalSamples(options.Seed, 0, options.LoadStdFraction, indep);
        GridKernels.NormalSamples(options.Seed ^ 0xA5A5A5A5UL, 0, options.SystemStdFraction, common);

        var baseRes = PowerFlowSolver.Solve(net, options.PowerFlow with { FlatStart = true });
        var warm = net.Clone();
        baseRes.ApplyTo(warm);

        var vm = new double[s][];
        var load = new double[s][];
        var losses = new double[s];
        var ok = new bool[s];
        Parallel.For(0, s, new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism }, k =>
        {
            var c = warm.Clone();
            for (int j = 0; j < nl; j++)
            {
                double f = Math.Max(0, 1 + common[k] + indep[k * nl + j]);
                c.Loads[j].P = net.Loads[j].P * f;
                c.Loads[j].Q = net.Loads[j].Q * f;
            }
            try
            {
                var r = PowerFlowSolver.Solve(c, options.PowerFlow);
                if (!r.Converged) return;
                vm[k] = r.Buses.Select(b => b.Vm).ToArray();
                load[k] = r.Branches.Select(b => b.LoadingPercent).ToArray();
                losses[k] = r.LossesMw;
                ok[k] = true;
            }
            catch (InvalidOperationException) { }
        });

        var good = Enumerable.Range(0, s).Where(k => ok[k]).ToArray();
        var buses = new BusVoltageStatistics[nb];
        var col = new double[good.Length];
        for (int i = 0; i < nb; i++)
        {
            for (int g = 0; g < good.Length; g++) col[g] = vm[good[g]][i];
            var bus = net.Buses[i];
            buses[i] = new BusVoltageStatistics(bus.Id, Stats.Mean(col), Stats.StdDev(col), Stats.Percentile(col, 0.05), Stats.Percentile(col, 0.95),
                Fraction(col, v => v < bus.Vmin), Fraction(col, v => v > bus.Vmax));
        }
        var branches = new BranchLoadingStatistics[nbr];
        for (int l = 0; l < nbr; l++)
        {
            for (int g = 0; g < good.Length; g++) col[g] = load[good[g]][l];
            var br = net.Branches[l];
            branches[l] = new BranchLoadingStatistics(l, br.From, br.To, Stats.Mean(col), Stats.Percentile(col, 0.95), Fraction(col, v => v > 100));
        }
        return new ProbabilisticLoadFlowResult
        {
            Samples = s, Converged = good.Length, Buses = buses, Branches = branches,
            LossesMw = good.Select(k => losses[k]).ToArray(), MinVoltage = good.Select(k => vm[k].Min()).ToArray(),
            Backend = GridKernels.Backend, Elapsed = sw.Elapsed,
        };
    }

    private static double Fraction(double[] x, Func<double, bool> pred) => x.Length == 0 ? 0 : x.Count(pred) / (double)x.Length;
}

public sealed record BenchmarkResult(string Case, int Buses, int Branches, PowerFlowMethod Method, int Iterations, double Milliseconds, bool Converged);

/// <summary>Scaling benchmark of the power-flow solvers on synthetic grids.</summary>
public static class PowerFlowBenchmark
{
    public static IReadOnlyList<BenchmarkResult> Run(IEnumerable<int> busCounts, PowerFlowMethod method = PowerFlowMethod.NewtonRaphson, int repeats = 3)
    {
        var list = new List<BenchmarkResult>();
        foreach (int n in busCounts)
        {
            var net = TestCases.SyntheticGrid(n);
            PowerFlowSolver.Solve(net, new PowerFlowOptions { Method = method }); // warm-up / JIT
            double best = double.MaxValue;
            PowerFlowResult? last = null;
            for (int r = 0; r < repeats; r++)
            {
                var sw = Stopwatch.StartNew();
                last = PowerFlowSolver.Solve(net, new PowerFlowOptions { Method = method });
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            list.Add(new BenchmarkResult(net.Name, net.BusCount, net.Branches.Count, method, last!.Iterations, best, last.Converged));
        }
        return list;
    }
}

public static class HpcBuilderExtensions
{
    public static ElectricalBuilder AddHpc(this ElectricalBuilder b)
    {
        b.TryAddModule("HPC");
        return b;
    }
}
