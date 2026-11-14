using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.PowerFlow;

public sealed record ContingencyResult(
    string Outage, int BranchIndex, bool Converged, bool Islanding, double MaxLoadingPercent, string? MostLoadedBranch,
    double MinVoltage, int MinVoltageBus, int Overloads, int VoltageViolations)
{
    /// <summary>Severity index combining overloads and voltage violations (higher is worse).</summary>
    public double Severity => !Converged || Islanding ? double.PositiveInfinity
        : Math.Max(0, MaxLoadingPercent - 100) + 1000 * Math.Max(0, 0.95 - MinVoltage) + 10 * (Overloads + VoltageViolations);
}

/// <summary>N-1 branch contingency screening (AC Newton–Raphson per outage, parallelized).</summary>
public static class ContingencyAnalysis
{
    public static IReadOnlyList<ContingencyResult> RunN1(PowerNetwork net, PowerFlowOptions? options = null, int maxDegreeOfParallelism = -1)
    {
        options ??= PowerFlowOptions.Default with { FlatStart = false };
        var baseCase = PowerFlowSolver.Solve(net, options);
        var warm = net.Clone();
        baseCase.ApplyTo(warm);
        var results = new ContingencyResult[net.Branches.Count];
        Parallel.For(0, net.Branches.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, k =>
        {
            var c = warm.Clone();
            var br = c.Branches[k];
            string name = br.Name ?? $"{br.From}-{br.To}";
            if (!br.InService) { results[k] = new(name, k, true, false, 0, null, 1, 0, 0, 0); return; }
            br.InService = false;
            if (Topology.Islands(c).Count > 1) { results[k] = new(name, k, false, true, 0, null, 0, 0, 0, 0); return; }
            try
            {
                var r = PowerFlowSolver.Solve(c, options);
                var most = r.MostLoadedBranch;
                results[k] = new(name, k, r.Converged, false, most?.LoadingPercent ?? 0, most is null ? null : $"{most.From}-{most.To}",
                    r.MinVoltageBus.Vm, r.MinVoltageBus.BusId, r.Overloads().Count(), r.VoltageViolations().Count());
            }
            catch (Exception)
            {
                results[k] = new(name, k, false, false, 0, null, 0, 0, 0, 0);
            }
        });
        return results.OrderByDescending(r => r.Severity).ToList();
    }
}

public static class PowerFlowBuilderExtensions
{
    /// <summary>Registers <see cref="IPowerFlowService"/>.</summary>
    public static ElectricalBuilder AddPowerFlow(this ElectricalBuilder b)
    {
        if (b.TryAddModule("PowerFlow")) b.Services.TryAddSingleton<IPowerFlowService, PowerFlowService>();
        return b;
    }
}

public interface IPowerFlowService
{
    PowerFlowResult Solve(PowerNetwork network, PowerFlowOptions? options = null);
    IReadOnlyList<ContingencyResult> ContingencyN1(PowerNetwork network);
}

internal sealed class PowerFlowService : IPowerFlowService
{
    public PowerFlowResult Solve(PowerNetwork network, PowerFlowOptions? options = null) => PowerFlowSolver.Solve(network, options);
    public IReadOnlyList<ContingencyResult> ContingencyN1(PowerNetwork network) => ContingencyAnalysis.RunN1(network);
}
