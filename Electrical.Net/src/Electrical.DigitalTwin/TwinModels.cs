using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.StateEstimation;

namespace Electrical.DigitalTwin;

/// <summary>Configuration of a <see cref="GridTwin"/>. Voltages in pu, loadings in % of the branch rating.</summary>
public sealed record GridTwinOptions
{
    /// <summary>Largest-normalized-residual threshold used to remove bad measurements (3.0 by convention).</summary>
    public double BadDataThreshold { get; init; } = 3.0;
    /// <summary>Maximum number of measurements removed per cycle.</summary>
    public int MaxBadDataRemovals { get; init; } = 5;
    /// <summary>Lower voltage limit [pu] for undervoltage alarms (ANSI C84.1 Range A: 0.95).</summary>
    public double VoltageLowPu { get; init; } = 0.95;
    /// <summary>Upper voltage limit [pu] for overvoltage alarms (ANSI C84.1 Range A: 1.05).</summary>
    public double VoltageHighPu { get; init; } = 1.05;
    /// <summary>Branch loading [%] above which an overload alarm is raised.</summary>
    public double OverloadPercent { get; init; } = 100;
    /// <summary>Branch loading [%] above which a "heavily loaded" warning is raised.</summary>
    public double HighLoadingPercent { get; init; } = 90;
    /// <summary>Deviation of the estimated system load from the forecast [%] that raises a forecast-deviation alarm.</summary>
    public double ForecastDeviationPercent { get; init; } = 10;
    /// <summary>Number of estimated states kept in <see cref="GridTwin.History"/>.</summary>
    public int HistoryCapacity { get; init; } = 2000;
    /// <summary>Assign a rating to unrated branches (RateA = 0) from the base-case power flow so loading and ageing can be tracked.</summary>
    public bool AutoRateUnratedBranches { get; init; } = true;
    /// <summary>Rating = margin × base-case |S| for auto-rated branches (1.25 → base case at 80 %).</summary>
    public double AutoRatingMargin { get; init; } = 1.25;
    /// <summary>Ambient temperature [°C] used for thermal ageing.</summary>
    public double AmbientTemperatureC { get; init; } = 30;
    /// <summary>Transformer thermal parameters (IEEE C57.91).</summary>
    public TransformerThermalParameters Transformer { get; init; } = TransformerThermalParameters.Default;
    /// <summary>Standard deviations assigned to measurements mapped from SCADA tags.</summary>
    public TagMeasurementSigmas Sigmas { get; init; } = new();
    /// <summary>Options passed to the WLS estimator.</summary>
    public StateEstimationOptions? Estimation { get; init; }

    public static GridTwinOptions Default { get; } = new();
}

/// <summary>Kind of an explained twin alarm.</summary>
public enum TwinAlarmKind { Undervoltage, Overvoltage, Overload, HighLoading, ForecastDeviation, BadData, EstimatorFailure }

/// <summary>Alarm severity.</summary>
public enum TwinAlarmSeverity { Info = 0, Warning = 1, High = 2, Critical = 3 }

/// <summary>
/// An alarm with a human-readable explanation, e.g. "Bus 14 voltage 0.93 pu below 0.95 because line 9-14 carries 120% load".
/// <paramref name="Message"/> is English, <paramref name="MessageId"/> Bahasa Indonesia.
/// </summary>
/// <param name="Element">Element key, e.g. "bus 14" or "branch 9-14".</param>
/// <param name="Value">Measured/estimated value (pu, %, MW depending on the kind).</param>
/// <param name="Limit">Limit that was violated (same unit as <paramref name="Value"/>).</param>
/// <param name="Causes">Short machine-friendly cause tags, e.g. "overload:9-14", "outage:7-9", "load-above-forecast".</param>
public sealed record ExplainedAlarm(TwinAlarmKind Kind, string Element, double Value, double Limit, TwinAlarmSeverity Severity,
    string Message, string MessageId, IReadOnlyList<string> Causes)
{
    /// <summary>Stable identity used to detect newly raised alarms.</summary>
    public string Key => $"{Kind}:{Element}";

    public string Localized(string language) => language == "id" ? MessageId : Message;

    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>Key performance indicators of an operating point.</summary>
/// <param name="TotalLoadMw">Total load [MW].</param>
/// <param name="LossesMw">Active losses [MW].</param>
/// <param name="MinVoltagePu">Lowest bus voltage [pu] and its bus.</param>
/// <param name="MaxLoadingPercent">Highest branch loading [% of RateA].</param>
/// <param name="Overloads">Number of branches above the overload threshold.</param>
/// <param name="VoltageViolations">Number of buses outside [low, high].</param>
public sealed record GridKpis(bool Converged, double TotalLoadMw, double LossesMw, double MinVoltagePu, int MinVoltageBus, double MaxVoltagePu,
    int MaxVoltageBus, double MaxLoadingPercent, string? MostLoadedBranch, int Overloads, int VoltageViolations)
{
    public static GridKpis Failed { get; } = new(false, 0, 0, 0, 0, 0, 0, 0, null, 0, 0);

    /// <summary>KPIs of solved bus/branch results with the given limits.</summary>
    public static GridKpis From(IReadOnlyList<BusResult> buses, IReadOnlyList<BranchResult> branches, bool converged = true,
        double vLow = 0.95, double vHigh = 1.05, double overloadPercent = 100)
    {
        if (buses.Count == 0) return Failed;
        var minB = buses.MinBy(b => b.Vm)!;
        var maxB = buses.MaxBy(b => b.Vm)!;
        var most = branches.Where(b => b.RateA > 0).MaxBy(b => b.LoadingPercent);
        return new GridKpis(converged, buses.Sum(b => b.PLoad), branches.Sum(b => b.PLoss), minB.Vm, minB.BusId, maxB.Vm, maxB.BusId,
            most?.LoadingPercent ?? 0, most is null ? null : $"{most.From}-{most.To}",
            branches.Count(b => b.LoadingPercent > overloadPercent), buses.Count(b => b.Vm < vLow - 1e-9 || b.Vm > vHigh + 1e-9));
    }

    public static GridKpis From(PowerFlowResult pf, double vLow = 0.95, double vHigh = 1.05, double overloadPercent = 100) =>
        From(pf.Buses, pf.Branches, pf.Converged, vLow, vHigh, overloadPercent);

    public override string ToString() =>
        $"load {TotalLoadMw:0.###} MW, losses {LossesMw:0.###} MW, Vmin {MinVoltagePu:0.0000} pu @ bus {MinVoltageBus}, " +
        $"Vmax {MaxVoltagePu:0.0000} pu @ bus {MaxVoltageBus}, max loading {MaxLoadingPercent:0.#}%{(MostLoadedBranch is null ? "" : $" ({MostLoadedBranch})")}, " +
        $"{Overloads} overload(s), {VoltageViolations} voltage violation(s)";
}

/// <summary>
/// One estimated state of the twin (one SCADA cycle). Voltages in pu / degrees aligned with <see cref="BusIds"/>; bus and branch
/// results are computed from the estimated voltages (powers in MW / Mvar).
/// </summary>
public sealed class TwinState
{
    public required int Step { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>True when the estimator converged on an observable measurement set.</summary>
    public required bool Converged { get; init; }
    public required bool Observable { get; init; }
    public required int Iterations { get; init; }
    /// <summary>WLS objective J(x̂) after bad-data removal.</summary>
    public required double ObjectiveJ { get; init; }
    public required int[] BusIds { get; init; }
    public required double[] Vm { get; init; }
    public required double[] VaDeg { get; init; }
    public required IReadOnlyList<BusResult> Buses { get; init; }
    public required IReadOnlyList<BranchResult> Branches { get; init; }
    /// <summary>Estimated total system load [MW] (model load × <see cref="LoadScale"/>).</summary>
    public required double EstimatedLoadMw { get; init; }
    /// <summary>Estimated load level relative to the model loads (1 = model value).</summary>
    public required double LoadScale { get; init; }
    /// <summary>Forecast system load [MW] at this timestamp (null when no forecast covers it).</summary>
    public double? ForecastLoadMw { get; init; }
    /// <summary>100·(estimated − forecast)/forecast [%].</summary>
    public double? ForecastDeviationPercent { get; init; }
    public required int MeasurementCount { get; init; }
    /// <summary>Measurements removed by the largest-normalized-residual test, with their normalized residual.</summary>
    public required IReadOnlyList<(Measurement Measurement, double NormalizedResidual)> RemovedMeasurements { get; init; }
    public required IReadOnlyList<ExplainedAlarm> Alarms { get; init; }
    public required GridKpis Kpis { get; init; }

    public double VmAt(int busId) => Vm[Array.IndexOf(BusIds, busId)];

    public override string ToString() =>
        $"#{Step} {Timestamp:yyyy-MM-dd HH:mm} {(Converged ? "OK" : "FAILED")} J={ObjectiveJ:0.##} load {EstimatedLoadMw:0.##} MW, " +
        $"{RemovedMeasurements.Count} bad meas., {Alarms.Count} alarm(s); {Kpis}";
}

/// <summary>Published after every twin update.</summary>
public sealed record TwinStateUpdated(TwinState State, DateTimeOffset Timestamp) : IGridEvent;

/// <summary>Published when an explained alarm is newly raised (not active in the previous cycle).</summary>
public sealed record TwinAlarmRaised(ExplainedAlarm Alarm, DateTimeOffset Timestamp) : IGridEvent;

/// <summary>Published when an alarm active in the previous cycle has cleared.</summary>
public sealed record TwinAlarmCleared(ExplainedAlarm Alarm, DateTimeOffset Timestamp) : IGridEvent;

/// <summary>Published for every measurement rejected by bad-data processing.</summary>
public sealed record TwinBadMeasurement(Measurement Measurement, double NormalizedResidual, DateTimeOffset Timestamp) : IGridEvent;

/// <summary>Helpers for branch ratings.</summary>
public static class BranchRatings
{
    /// <summary>
    /// Assigns RateA = <paramref name="margin"/> × max(|S_from|, |S_to|) of a base-case power flow (at least <paramref name="minimumMva"/>)
    /// to every branch without a rating. Returns the number of branches rated.
    /// </summary>
    public static int AssignMissing(PowerNetwork net, double margin = 1.25, double minimumMva = 1e-3)
    {
        if (!net.Branches.Any(b => b.RateA <= 0)) return 0;
        var pf = PowerFlowSolver.Solve(net);
        int n = 0;
        foreach (var r in pf.Branches)
        {
            var br = net.Branches[r.Index];
            if (br.RateA > 0) continue;
            double s = Math.Max(r.SFrom, r.STo);
            double rate = Math.Max(minimumMva, margin * s);
            // round to 2 significant digits for readability
            double mag = Math.Pow(10, Math.Floor(Math.Log10(rate)) - 1);
            br.RateA = Math.Ceiling(rate / mag) * mag;
            n++;
        }
        return n;
    }

    /// <summary>"line 9-14" / "transformer 4-7" (or the branch name when set).</summary>
    public static string Label(Branch br, string language = "en") =>
        br.IsTransformer ? (language == "id" ? $"trafo {br.From}-{br.To}" : $"transformer {br.From}-{br.To}")
                         : (language == "id" ? $"saluran {br.From}-{br.To}" : $"line {br.From}-{br.To}");
}
