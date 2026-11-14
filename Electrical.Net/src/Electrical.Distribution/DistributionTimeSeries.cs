using System.Numerics;

namespace Electrical.Distribution;

/// <summary>One time step of a <see cref="DistributionTimeSeries"/> run. Powers in kW/kvar, voltages in pu.</summary>
public sealed record DistributionTimeStep(
    int Index, double TimeSeconds, double LoadMultiplier, bool Converged, double MinVoltagePu, double MaxVoltagePu, double SourceKw, double SourceKvar,
    double LossesKw, double MaxVoltageUnbalance, IReadOnlyDictionary<string, int[]> RegulatorTaps, IReadOnlyDictionary<string, bool> CapacitorStates);

/// <summary>Result of a quasi-static time-series run.</summary>
public sealed class DistributionTimeSeriesResult
{
    internal DistributionTimeSeriesResult(IReadOnlyList<DistributionTimeStep> steps, double stepSeconds, IReadOnlyDictionary<string, int> operations)
    {
        Steps = steps;
        StepSeconds = stepSeconds;
        ControllerOperations = operations;
    }

    public IReadOnlyList<DistributionTimeStep> Steps { get; }
    /// <summary>Step length [s].</summary>
    public double StepSeconds { get; }
    /// <summary>Operations (tap steps / switchings) per controller during the run.</summary>
    public IReadOnlyDictionary<string, int> ControllerOperations { get; }
    /// <summary>Total tap steps of regulator and OLTC controllers.</summary>
    public int TapOperations { get; internal init; }
    /// <summary>Total capacitor switching operations.</summary>
    public int CapacitorSwitchings { get; internal init; }
    /// <summary>Energy delivered by the source [kWh].</summary>
    public double EnergyKwh => Steps.Sum(s => s.SourceKw) * StepSeconds / 3600;
    /// <summary>Energy losses [kWh].</summary>
    public double LossesKwh => Steps.Sum(s => s.LossesKw) * StepSeconds / 3600;
    public double MinVoltagePu => Steps.Min(s => s.MinVoltagePu);
    public double MaxVoltagePu => Steps.Max(s => s.MaxVoltagePu);
    public bool AllConverged => Steps.All(s => s.Converged);
}

/// <summary>
/// Quasi-static time series (QSTS) for the three-phase model: each step scales all loads by a multiplier, solves the unbalanced power flow
/// and lets the network controllers act with their time delays (state such as tap positions carries over between steps).
/// </summary>
public static class DistributionTimeSeries
{
    /// <summary>Runs <paramref name="loadMultipliers"/> (one value per step of <paramref name="stepSeconds"/>) starting at
    /// <paramref name="startSeconds"/> (time of day matters for time-controlled capacitors).</summary>
    public static DistributionTimeSeriesResult Run(DistributionNetwork network, IReadOnlyList<double> loadMultipliers, double stepSeconds = 3600,
        DistributionPowerFlowOptions? options = null, double startSeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepSeconds);
        options ??= DistributionPowerFlowOptions.Default;
        var start = network.Controllers.ToDictionary(c => c.Name, c => c.Operations);
        var steps = new List<DistributionTimeStep>(loadMultipliers.Count);
        for (int k = 0; k < loadMultipliers.Count; k++)
        {
            double t = startSeconds + k * stepSeconds;
            var o = options with { LoadMultiplier = loadMultipliers[k], ControlMode = ControlMode.Off };
            var r = UnbalancedPowerFlow.SolveOnce(network, o);
            if (network.Controllers.Count > 0) r = UnbalancedPowerFlow.RunControls(network, o, r, new ControlTiming(t, stepSeconds, false));
            Complex s = r.TotalSourceKva;
            steps.Add(new DistributionTimeStep(k, t, loadMultipliers[k], r.Converged, r.MinVoltage.Vpu, r.MaxVoltage.Vpu, s.Real, s.Imaginary,
                r.LossesKw, r.MaxVoltageUnbalance, r.RegulatorTaps,
                network.Capacitors.ToDictionary(c => c.Name, c => c.IsOn, StringComparer.OrdinalIgnoreCase)));
        }
        var ops = network.Controllers.ToDictionary(c => c.Name, c => c.Operations - start[c.Name]);
        return new DistributionTimeSeriesResult(steps, stepSeconds, ops)
        {
            TapOperations = network.Controllers.Where(c => c is VoltageRegulatorController or OltcController).Sum(c => ops[c.Name]),
            CapacitorSwitchings = network.Controllers.OfType<CapacitorController>().Sum(c => ops[c.Name]),
        };
    }

    /// <summary>Deterministic 24-point residential-like daily load shape (pu of peak), useful for demos and tests.</summary>
    public static double[] DailyLoadShape() =>
        [0.55, 0.50, 0.47, 0.46, 0.47, 0.52, 0.62, 0.72, 0.78, 0.80, 0.82, 0.84, 0.85, 0.84, 0.84, 0.86, 0.90, 0.97, 1.00, 0.98, 0.92, 0.83, 0.72, 0.62];
}
