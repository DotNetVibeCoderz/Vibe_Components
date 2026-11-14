using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Stability;

/// <summary>Kind of switching event in a transient-stability scenario.</summary>
public enum StabilityEventKind { ApplyFault, ClearFault, TripBranch, CloseBranch, TripGenerator, ScaleLoad }

/// <summary>A switching event at <see cref="TimeS"/> [s].</summary>
/// <param name="TimeS">Event time [s].</param>
/// <param name="Kind">Event kind.</param>
/// <param name="BusId">Bus for faults / load scaling.</param>
/// <param name="Index">Branch index (<see cref="PowerNetwork.Branches"/>) or generator index (<see cref="PowerNetwork.Generators"/>).</param>
/// <param name="FaultImpedancePu">Fault impedance [pu] (zero = bolted).</param>
/// <param name="Value">Load scale factor for <see cref="StabilityEventKind.ScaleLoad"/>.</param>
public sealed record StabilityEvent(double TimeS, StabilityEventKind Kind, int BusId = 0, int Index = -1, Complex FaultImpedancePu = default, double Value = 1)
{
    public static StabilityEvent Fault(double t, int busId, Complex? impedancePu = null) => new(t, StabilityEventKind.ApplyFault, busId, FaultImpedancePu: impedancePu ?? Complex.Zero);
    public static StabilityEvent Clear(double t, int busId) => new(t, StabilityEventKind.ClearFault, busId);
    public static StabilityEvent TripBranch(double t, int branchIndex) => new(t, StabilityEventKind.TripBranch, Index: branchIndex);
    public static StabilityEvent CloseBranch(double t, int branchIndex) => new(t, StabilityEventKind.CloseBranch, Index: branchIndex);
    public static StabilityEvent TripGenerator(double t, int generatorIndex) => new(t, StabilityEventKind.TripGenerator, Index: generatorIndex);
    public static StabilityEvent ScaleLoad(double t, int busId, double factor) => new(t, StabilityEventKind.ScaleLoad, busId, Value: factor);
}

/// <summary>Transient-stability simulation options.</summary>
public sealed record TransientStabilityOptions
{
    /// <summary>Integration step [s] (RK4).</summary>
    public double TimeStepS { get; init; } = 0.002;
    /// <summary>Simulation end time [s].</summary>
    public double EndTimeS { get; init; } = 3.0;
    /// <summary>Instability threshold: maximum rotor-angle deviation from the centre of inertia [degrees].</summary>
    public double InstabilityAngleDeg { get; init; } = 180;
    /// <summary>Stop as soon as the threshold is exceeded.</summary>
    public bool StopWhenUnstable { get; init; } = true;
    /// <summary>Store every n-th step in the result.</summary>
    public int OutputInterval { get; init; } = 1;

    public static TransientStabilityOptions Default { get; } = new();
}

/// <summary>Rotor trajectories of a transient-stability run (angles in degrees, speeds in pu, powers in pu on the system base).</summary>
public sealed class TransientStabilityResult
{
    public required IReadOnlyList<ClassicalMachine> Machines { get; init; }
    public required double[] Time { get; init; }
    /// <summary>Absolute rotor angles [degrees] per machine × time.</summary>
    public required double[][] AngleDeg { get; init; }
    /// <summary>Rotor angles relative to the centre of inertia [degrees].</summary>
    public required double[][] AngleCoiDeg { get; init; }
    /// <summary>Rotor speed [pu].</summary>
    public required double[][] SpeedPu { get; init; }
    /// <summary>Electrical power [pu].</summary>
    public required double[][] ElectricalPowerPu { get; init; }
    public required bool Stable { get; init; }
    /// <summary>Time when the instability threshold was crossed [s] (NaN when stable).</summary>
    public required double InstabilityTimeS { get; init; }
    /// <summary>Largest rotor-angle deviation from the COI over the run [degrees].</summary>
    public required double MaxAngleDeviationDeg { get; init; }

    /// <summary>Angle difference δ_i − δ_j over time [degrees].</summary>
    public double[] AngleDifferenceDeg(int i, int j) => AngleDeg[i].Select((a, k) => a - AngleDeg[j][k]).ToArray();
}

/// <summary>Classical-model transient stability (swing equations, RK4, network reduced per switching configuration).</summary>
public static class TransientStability
{
    /// <summary>
    /// Simulates the swing equations dδ/dt = ω_s(ω − 1), dω/dt = (P_m − P_e − D(ω − 1)) / (2H) with the given switching events.
    /// </summary>
    public static TransientStabilityResult Simulate(ClassicalModel model, IEnumerable<StabilityEvent> events, TransientStabilityOptions? options = null)
    {
        var opt = options ?? TransientStabilityOptions.Default;
        var ms = model.Machines;
        int m = ms.Count;
        double ws = model.OmegaS;
        var ev = events.OrderBy(e => e.TimeS).ToList();
        var config = new ClassicalModel.Configuration();
        var yred = model.ReducedAdmittance(config);
        var e = ms.Select(x => x.E).ToArray();
        var pm = ms.Select(x => x.Pm).ToArray();
        var state = new double[2 * m];
        for (int i = 0; i < m; i++) { state[i] = ms[i].Delta0Rad; state[m + i] = 1.0; }
        var online = Enumerable.Repeat(true, m).ToArray();
        double hTot() => Enumerable.Range(0, m).Where(i => online[i] && !ms[i].Infinite).Sum(i => ms[i].H);

        var time = new List<double>(); var ang = new List<double[]>(); var coi = new List<double[]>(); var spd = new List<double[]>(); var pel = new List<double[]>();
        var pe = new double[m];
        double maxDev = 0, tUnstable = double.NaN;

        void Deriv(ReadOnlySpan<double> x, Span<double> dx)
        {
            ModelPe(yred, e, x[..m], pe, online);
            for (int i = 0; i < m; i++)
            {
                if (!online[i] || ms[i].Infinite) { dx[i] = 0; dx[m + i] = 0; continue; }
                double dw = x[m + i] - 1;
                dx[i] = ws * dw;
                dx[m + i] = (pm[i] - pe[i] - ms[i].D * dw) / (2 * ms[i].H);
            }
        }

        double Coi(ReadOnlySpan<double> x)
        {
            var inf = Enumerable.Range(0, m).Where(i => online[i] && ms[i].Infinite).ToList();
            if (inf.Count > 0) return x[inf[0]];
            double ht = hTot();
            double s = 0;
            for (int i = 0; i < m; i++) if (online[i] && !ms[i].Infinite) s += ms[i].H * x[i];
            return ht > 0 ? s / ht : 0;
        }

        void Record(double t, ReadOnlySpan<double> x)
        {
            ModelPe(yred, e, x[..m], pe, online);
            double c = Coi(x);
            var a = new double[m]; var ac = new double[m]; var w = new double[m]; var p = new double[m];
            for (int i = 0; i < m; i++)
            {
                a[i] = x[i] * Scalar.Rad2Deg; ac[i] = (x[i] - c) * Scalar.Rad2Deg; w[i] = x[m + i]; p[i] = online[i] ? pe[i] : 0;
            }
            time.Add(t); ang.Add(a); coi.Add(ac); spd.Add(w); pel.Add(p);
        }

        double Deviation(ReadOnlySpan<double> x)
        {
            double c = Coi(x), d = 0;
            for (int i = 0; i < m; i++) if (online[i]) d = Math.Max(d, Math.Abs(x[i] - c));
            return d * Scalar.Rad2Deg;
        }

        int evIdx = 0;
        void ApplyEventsAt(double t)
        {
            bool changed = false;
            while (evIdx < ev.Count && ev[evIdx].TimeS <= t + 1e-12)
            {
                var x = ev[evIdx++];
                switch (x.Kind)
                {
                    case StabilityEventKind.ApplyFault: config.Faults[x.BusId] = x.FaultImpedancePu; break;
                    case StabilityEventKind.ClearFault: config.Faults.Remove(x.BusId); break;
                    case StabilityEventKind.TripBranch: config.TrippedBranches.Add(x.Index); config.ClosedBranches.Remove(x.Index); break;
                    case StabilityEventKind.CloseBranch: config.ClosedBranches.Add(x.Index); config.TrippedBranches.Remove(x.Index); break;
                    case StabilityEventKind.TripGenerator:
                        {
                            int k = ms.ToList().FindIndex(q => q.GeneratorIndex == x.Index);
                            if (k >= 0) { config.TrippedMachines.Add(k); online[k] = false; }
                            break;
                        }
                    case StabilityEventKind.ScaleLoad: config.LoadScale[x.BusId] = x.Value; break;
                }
                changed = true;
            }
            if (changed) yred = model.ReducedAdmittance(config);
        }

        double tNow = 0;
        ApplyEventsAt(0);
        Record(0, state);
        var k1 = new double[2 * m]; var k2 = new double[2 * m]; var k3 = new double[2 * m]; var k4 = new double[2 * m]; var tmp = new double[2 * m];
        int step = 0;
        bool stable = true;
        while (tNow < opt.EndTimeS - 1e-12)
        {
            double h = Math.Min(opt.TimeStepS, opt.EndTimeS - tNow);
            if (evIdx < ev.Count && ev[evIdx].TimeS < tNow + h - 1e-12) h = ev[evIdx].TimeS - tNow; // land exactly on the event
            if (h > 1e-12)
            {
                Deriv(state, k1);
                for (int i = 0; i < tmp.Length; i++) tmp[i] = state[i] + 0.5 * h * k1[i];
                Deriv(tmp, k2);
                for (int i = 0; i < tmp.Length; i++) tmp[i] = state[i] + 0.5 * h * k2[i];
                Deriv(tmp, k3);
                for (int i = 0; i < tmp.Length; i++) tmp[i] = state[i] + h * k3[i];
                Deriv(tmp, k4);
                for (int i = 0; i < tmp.Length; i++) state[i] += h / 6 * (k1[i] + 2 * k2[i] + 2 * k3[i] + k4[i]);
                tNow += h;
            }
            ApplyEventsAt(tNow);
            step++;
            double dev = Deviation(state);
            maxDev = Math.Max(maxDev, dev);
            if (step % Math.Max(1, opt.OutputInterval) == 0 || tNow >= opt.EndTimeS - 1e-12) Record(tNow, state);
            if (dev > opt.InstabilityAngleDeg && stable)
            {
                stable = false; tUnstable = tNow;
                if (opt.StopWhenUnstable) { if (time[^1] != tNow) Record(tNow, state); break; }
            }
        }

        double[][] T(List<double[]> rows) => Enumerable.Range(0, m).Select(i => rows.Select(r => r[i]).ToArray()).ToArray();
        return new TransientStabilityResult
        {
            Machines = ms, Time = time.ToArray(), AngleDeg = T(ang), AngleCoiDeg = T(coi), SpeedPu = T(spd), ElectricalPowerPu = T(pel),
            Stable = stable, InstabilityTimeS = tUnstable, MaxAngleDeviationDeg = maxDev,
        };
    }

    private static void ModelPe(DenseMatrix<Complex> yred, double[] e, ReadOnlySpan<double> delta, double[] pe, bool[] online)
    {
        ClassicalModel.ElectricalPower(yred, e, delta, pe);
        for (int i = 0; i < pe.Length; i++) if (!online[i]) pe[i] = 0;
    }

    /// <summary>Builds the classical model from <paramref name="network"/> and simulates the events.</summary>
    public static TransientStabilityResult Simulate(PowerNetwork network, IEnumerable<StabilityEvent> events, TransientStabilityOptions? options = null,
        PowerFlowResult? powerFlow = null) => Simulate(ClassicalModel.FromPowerFlow(network, powerFlow), events, options);

    /// <summary>Three-phase fault at <paramref name="faultBusId"/> applied at t = 0 and cleared at <paramref name="clearingTimeS"/>, optionally tripping a branch.</summary>
    public static TransientStabilityResult SimulateFault(ClassicalModel model, int faultBusId, double clearingTimeS, int? tripBranchIndex = null,
        TransientStabilityOptions? options = null) => Simulate(model, FaultEvents(faultBusId, clearingTimeS, tripBranchIndex), options);

    private static List<StabilityEvent> FaultEvents(int bus, double tc, int? branch)
    {
        var list = new List<StabilityEvent> { StabilityEvent.Fault(0, bus), StabilityEvent.Clear(tc, bus) };
        if (branch is int b) list.Add(StabilityEvent.TripBranch(tc, b));
        return list;
    }

    /// <summary>
    /// Critical clearing time [s] of a three-phase fault at <paramref name="faultBusId"/> (cleared by removing the fault and, optionally,
    /// tripping <paramref name="tripBranchIndex"/>) found by bisection to within <paramref name="toleranceS"/>.
    /// Returns <paramref name="maxS"/> when stable for the longest clearing time and 0 when unstable for the shortest.
    /// </summary>
    public static double CriticalClearingTime(ClassicalModel model, int faultBusId, int? tripBranchIndex = null, double minS = 0.0, double maxS = 1.0,
        double toleranceS = 0.001, TransientStabilityOptions? options = null)
    {
        bool Stable(double tc) => SimulateFault(model, faultBusId, tc, tripBranchIndex, options).Stable;
        if (Stable(maxS)) return maxS;
        if (!Stable(minS)) return 0;
        double lo = minS, hi = maxS;
        while (hi - lo > toleranceS)
        {
            double mid = 0.5 * (lo + hi);
            if (Stable(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>Finds the branch index between two buses (either direction).</summary>
    public static int BranchIndex(PowerNetwork network, int fromBus, int toBus)
    {
        int k = network.Branches.FindIndex(b => (b.From == fromBus && b.To == toBus) || (b.From == toBus && b.To == fromBus));
        return k >= 0 ? k : throw new ArgumentException($"No branch between {fromBus} and {toBus}.");
    }
}
