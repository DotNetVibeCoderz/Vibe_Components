using Electrical.Core.Network;
using Electrical.ShortCircuit;

namespace Electrical.Protection;

/// <summary>Result of a primary/backup coordination check at one fault current.</summary>
public sealed record CoordinationCheck(
    string Downstream, string Upstream, double DownstreamCurrentA, double UpstreamCurrentA, double DownstreamTimeS, double UpstreamTimeS,
    double RequiredMarginS)
{
    /// <summary>Actual grading margin t_up − t_down [s].</summary>
    public double MarginS => UpstreamTimeS - DownstreamTimeS;
    /// <summary>True when the margin meets the coordination time interval (CTI).</summary>
    public bool Coordinated => double.IsFinite(DownstreamTimeS) && MarginS >= RequiredMarginS - 1e-9;
}

/// <summary>One relay stage of a radial feeder for time grading.</summary>
/// <param name="Relay">The relay (its <see cref="OvercurrentRelay.TimeMultiplier"/> is set by grading).</param>
/// <param name="MaxFaultCurrentA">Maximum fault current through the relay for a fault just downstream of it [A].</param>
/// <param name="CoordinationCurrentA">Current through this relay for the coordinating fault at the next downstream relay [A]; ignored for the most downstream stage.</param>
public sealed record GradingStage(OvercurrentRelay Relay, double MaxFaultCurrentA, double CoordinationCurrentA);

/// <summary>Per-relay outcome of time grading.</summary>
public sealed record GradingRow(
    string Relay, double PickupA, double TimeMultiplier, double MaxFaultCurrentA, double TimeAtMaxFaultS, double? CoordinationCurrentA,
    double? DownstreamTimeS, double? UpstreamTimeAtCoordinationS, double? MarginS);

/// <summary>Result of a radial time-grading study (rows ordered from the source towards the load).</summary>
public sealed record GradingResult(IReadOnlyList<GradingRow> Rows, double CtiS)
{
    public bool AllCoordinated => Rows.All(r => r.MarginS is null || r.MarginS >= CtiS - 1e-6);
}

/// <summary>Overcurrent coordination: CTI checks and automatic TMS grading of radial feeders.</summary>
public static class Coordination
{
    /// <summary>Checks that <paramref name="upstream"/> backs up <paramref name="downstream"/> with at least <paramref name="ctiS"/>.</summary>
    /// <param name="downstreamCurrentA">Fault current through the downstream device [A].</param>
    /// <param name="upstreamCurrentA">Current through the upstream device [A] (defaults to the downstream current — radial, same voltage).</param>
    /// <param name="ctiS">Coordination time interval [s] (typical 0.2–0.4 s for relays).</param>
    public static CoordinationCheck Check(IProtectiveDevice downstream, IProtectiveDevice upstream, double downstreamCurrentA,
        double? upstreamCurrentA = null, double ctiS = 0.3)
    {
        double iu = upstreamCurrentA ?? downstreamCurrentA;
        return new CoordinationCheck(downstream.Name, upstream.Name, downstreamCurrentA, iu,
            downstream.OperatingTime(downstreamCurrentA), upstream.OperatingTime(iu), ctiS);
    }

    /// <summary>
    /// Grades the time multipliers of relays on a radial feeder. <paramref name="stagesFromLoad"/> is ordered from the most downstream relay
    /// (index 0, set to <paramref name="minimumTms"/>) towards the source. Each upstream TMS is the smallest value (rounded up to
    /// <paramref name="tmsStep"/>) giving t_up ≥ t_down + CTI at the coordinating fault. The relays are modified in place.
    /// </summary>
    public static GradingResult GradeTimeMultipliers(IReadOnlyList<GradingStage> stagesFromLoad, double ctiS = 0.3, double minimumTms = 0.05,
        double tmsStep = 0.01)
    {
        var rows = new GradingRow[stagesFromLoad.Count];
        for (int k = 0; k < stagesFromLoad.Count; k++)
        {
            var st = stagesFromLoad[k];
            var relay = st.Relay;
            if (k == 0)
            {
                relay.TimeMultiplier = minimumTms;
                rows[k] = new GradingRow(relay.Name, relay.PickupA, relay.TimeMultiplier, st.MaxFaultCurrentA, relay.OperatingTime(st.MaxFaultCurrentA),
                    null, null, null, null);
                continue;
            }
            var down = stagesFromLoad[k - 1];
            double tDown = down.Relay.OperatingTime(down.MaxFaultCurrentA);
            double target = tDown + ctiS - relay.BreakerTimeS;
            double m = Math.Min(st.CoordinationCurrentA / relay.PickupA, relay.MaxMultiple);
            double tms = InverseTimeCurves.RequiredTimeMultiplier(relay.Curve, m, target);
            if (!double.IsFinite(tms))
                throw new InvalidOperationException($"{relay.Name}: coordination current {st.CoordinationCurrentA:0.#} A is below pickup {relay.PickupA:0.#} A.");
            tms = Math.Max(minimumTms, Math.Ceiling(tms / tmsStep - 1e-9) * tmsStep);
            relay.TimeMultiplier = tms;
            double tUp = relay.OperatingTime(st.CoordinationCurrentA);
            rows[k] = new GradingRow(relay.Name, relay.PickupA, tms, st.MaxFaultCurrentA, relay.OperatingTime(st.MaxFaultCurrentA),
                st.CoordinationCurrentA, tDown, tUp, tUp - tDown);
        }
        return new GradingResult(rows.Reverse().ToList(), ctiS);
    }

    /// <summary>
    /// Grades relays along a radial path using IEC 60909 three-phase maximum fault currents from <see cref="ShortCircuitStudy"/>.
    /// Relay k sits at bus <c>busPathFromSource[k]</c> on the branch towards <c>busPathFromSource[k+1]</c>
    /// (so <c>relaysFromSource.Count == busPathFromSource.Count - 1</c>). Currents are converted to amperes at each relay's voltage,
    /// so transformers on the path are handled.
    /// </summary>
    public static GradingResult GradeRadialFeeder(PowerNetwork network, IReadOnlyList<int> busPathFromSource, IReadOnlyList<OvercurrentRelay> relaysFromSource,
        ShortCircuitOptions? shortCircuitOptions = null, double ctiS = 0.3, double minimumTms = 0.05, double tmsStep = 0.01)
    {
        if (relaysFromSource.Count != busPathFromSource.Count - 1)
            throw new ArgumentException("Need one relay per branch along the path (relays = buses − 1).", nameof(relaysFromSource));
        var study = new ShortCircuitStudy(network, shortCircuitOptions);
        int n = relaysFromSource.Count;
        var stages = new GradingStage[n];
        for (int k = 0; k < n; k++)
        {
            int bus = busPathFromSource[k];
            var atRelay = study.Calculate(bus, FaultType.ThreePhase);
            double iMax = atRelay.IkKa * 1000;
            double iCoord = 0;
            if (k < n - 1)
            {
                int next = busPathFromSource[k + 1];
                var f = study.Calculate(next, FaultType.ThreePhase);
                iCoord = BranchCurrentAt(network, f, bus, next) * 1000;
            }
            stages[n - 1 - k] = new GradingStage(relaysFromSource[k], iMax, iCoord);
        }
        return GradeTimeMultipliers(stages, ctiS, minimumTms, tmsStep);
    }

    /// <summary>Fault current [kA] in the branch between <paramref name="atBus"/> and <paramref name="otherBus"/>, referred to <paramref name="atBus"/>'s voltage.</summary>
    public static double BranchCurrentAt(PowerNetwork network, FaultResult fault, int atBus, int otherBus)
    {
        var bc = fault.Branches.FirstOrDefault(b => (b.From == atBus && b.To == otherBus) || (b.From == otherBus && b.To == atBus))
                 ?? throw new ArgumentException($"No in-service branch between buses {atBus} and {otherBus}.");
        double i = bc.MaxKa;
        if (bc.From != atBus) i *= network.GetBus(bc.From).BaseKv / network.GetBus(atBus).BaseKv;
        return i;
    }
}
