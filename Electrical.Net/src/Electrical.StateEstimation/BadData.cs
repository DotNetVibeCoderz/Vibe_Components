using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.StateEstimation;

/// <summary>Numerical observability analysis result.</summary>
/// <param name="IsObservable">True when the gain matrix has full rank.</param>
/// <param name="Rank">Numerical rank of G = HᵀH.</param>
/// <param name="StateCount">Number of state variables (2N − 1).</param>
/// <param name="UnobservableStates">Names of states hit by zero pivots, e.g. "θ@bus 7".</param>
public sealed record ObservabilityResult(bool IsObservable, int Rank, int StateCount, IReadOnlyList<string> UnobservableStates);

/// <summary>
/// Numerical observability check (Monticelli &amp; Wu 1985): factorize the gain matrix G = HᵀH built at flat start with unit weights by
/// symmetric Gaussian elimination (Cholesky without square roots, diagonal pivoting); pivots below a relative threshold are zero pivots,
/// each marking an unobservable state direction.
/// </summary>
public static class Observability
{
    public static ObservabilityResult Check(PowerNetwork net, IReadOnlyList<Measurement> measurements, double pivotTolerance = 1e-9)
    {
        var model = new MeasurementModel(net);
        int ns = model.StateCount;
        var h = new double[measurements.Count];
        var rows = model.Evaluate(measurements, Enumerable.Repeat(1.0, model.N).ToArray(), new double[model.N], h);
        var g = new double[ns, ns];
        foreach (var row in rows)
            foreach (var (a, va) in row)
                foreach (var (b, vb) in row)
                    g[a, b] += va * vb;
        double maxDiag = 0;
        for (int k = 0; k < ns; k++) maxDiag = Math.Max(maxDiag, g[k, k]);
        double tol = Math.Max(maxDiag, 1e-300) * pivotTolerance;
        var bad = new List<string>();
        int rank = 0;
        for (int k = 0; k < ns; k++)
        {
            double piv = g[k, k];
            if (piv <= tol)
            {
                bad.Add(model.StateName(k));
                for (int j = k; j < ns; j++) { g[k, j] = 0; g[j, k] = 0; }
                continue;
            }
            rank++;
            for (int i = k + 1; i < ns; i++)
            {
                if (g[i, k] == 0) continue;
                double f = g[i, k] / piv;
                for (int j = k + 1; j < ns; j++) g[i, j] -= f * g[k, j];
            }
        }
        return new ObservabilityResult(bad.Count == 0, rank, ns, bad);
    }
}

/// <summary>Outcome of the bad-data identification loop.</summary>
/// <param name="Final">Estimate after the last removal.</param>
/// <param name="Removed">Measurements removed, in order, with the normalized residual that flagged them.</param>
/// <param name="InitialChiSquarePassed">Whether the first estimate passed the χ² test.</param>
public sealed record BadDataResult(StateEstimationResult Final, IReadOnlyList<(Measurement Measurement, double NormalizedResidual)> Removed,
    bool InitialChiSquarePassed, double InitialObjective);

/// <summary>
/// Bad-data processing: χ² detection on J(x̂) followed by the largest normalized residual (LNR) test
/// r_N,i = |r_i|/√Ω_ii with Ω = R − H·G⁻¹·Hᵀ; the worst measurement above the threshold (3.0 by convention) is removed and the
/// estimate repeated until none exceeds it.
/// </summary>
public static class BadDataDetector
{
    public static BadDataResult IdentifyAndRemove(PowerNetwork net, IReadOnlyList<Measurement> measurements, double threshold = 3.0, int maxRemovals = 10,
        StateEstimationOptions? options = null)
    {
        options = (options ?? StateEstimationOptions.Default) with { ComputeNormalizedResiduals = true };
        var current = measurements.ToList();
        var removed = new List<(Measurement, double)>();
        var res = WlsEstimator.Estimate(net, current, options);
        bool initialPass = !res.BadDataSuspected;
        double j0 = res.ObjectiveJ;
        for (int k = 0; k < maxRemovals; k++)
        {
            int worst = res.LargestNormalizedResidualIndex;
            if (worst < 0 || res.NormalizedResiduals[worst] <= threshold) break;
            removed.Add((current[worst], res.NormalizedResiduals[worst]));
            current.RemoveAt(worst);
            res = WlsEstimator.Estimate(net, current, options);
            if (!res.Observable) break;
        }
        return new BadDataResult(res, removed, initialPass, j0);
    }
}

/// <summary>DI facade for state estimation.</summary>
public interface IStateEstimationService
{
    StateEstimationResult Estimate(PowerNetwork net, IReadOnlyList<Measurement> measurements);
    BadDataResult EstimateWithBadDataRemoval(PowerNetwork net, IReadOnlyList<Measurement> measurements);
    ObservabilityResult CheckObservability(PowerNetwork net, IReadOnlyList<Measurement> measurements);
}

internal sealed class StateEstimationService : IStateEstimationService
{
    public StateEstimationResult Estimate(PowerNetwork net, IReadOnlyList<Measurement> measurements) => WlsEstimator.Estimate(net, measurements);
    public BadDataResult EstimateWithBadDataRemoval(PowerNetwork net, IReadOnlyList<Measurement> measurements) =>
        BadDataDetector.IdentifyAndRemove(net, measurements);
    public ObservabilityResult CheckObservability(PowerNetwork net, IReadOnlyList<Measurement> measurements) => Observability.Check(net, measurements);
}

public static class StateEstimationBuilderExtensions
{
    /// <summary>Registers <see cref="IStateEstimationService"/> (and the PowerFlow module).</summary>
    public static ElectricalBuilder AddStateEstimation(this ElectricalBuilder b)
    {
        b.AddPowerFlow();
        if (b.TryAddModule("StateEstimation")) b.Services.TryAddSingleton<IStateEstimationService, StateEstimationService>();
        return b;
    }
}
