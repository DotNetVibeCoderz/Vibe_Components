using Electrical.Core.Data;
using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.PowerFlow;

namespace Electrical.Simulation;

/// <summary>Kind of a timed switching / outage event.</summary>
public enum SimulationEventKind
{
    /// <summary>Open branch <c>Index</c>.</summary>
    BranchOpen,
    /// <summary>Close branch <c>Index</c>.</summary>
    BranchClose,
    /// <summary>Trip generator <c>Index</c> (taken out of service).</summary>
    GeneratorTrip,
    /// <summary>Return generator <c>Index</c> to service.</summary>
    GeneratorReturn,
    /// <summary>Multiply every load by <c>Value</c> from now on (e.g. 0.9 = 10 % load shedding).</summary>
    LoadScale,
    /// <summary>Set the shunt susceptance of bus id <c>Index</c> to <c>Value</c> Mvar (capacitor switching).</summary>
    ShuntSet,
}

/// <summary>Event applied when simulation time (relative to the start) reaches <paramref name="At"/>.</summary>
public sealed record SimulationEvent(TimeSpan At, SimulationEventKind Kind, int Index, double Value = 0, string? Description = null);

/// <summary>A PV system for QSTS: injection = RatedMw × ProfilePu at the given power factor (absorbing Q when &lt; 1).</summary>
public sealed record PvSystem(int Bus, double RatedMw, TimeSeries ProfilePu, double PowerFactor = 1, string? Name = null);

/// <summary>Quasi-static time-series results; arrays are indexed by step and ready for plotting.</summary>
public sealed class QstsResult
{
    public required DateTimeOffset[] Times { get; init; }
    public required double StepHours { get; init; }
    public required bool[] Converged { get; init; }
    public required int[] Iterations { get; init; }
    /// <summary>Minimum / maximum bus voltage per step [pu].</summary>
    public required double[] VminPu { get; init; }
    public required double[] VmaxPu { get; init; }
    public required int[] VminBus { get; init; }
    public required double[] LossesMw { get; init; }
    /// <summary>Highest branch loading per step [% of RateA] (0 when no branch is rated).</summary>
    public required double[] MaxLoadingPercent { get; init; }
    public required double[] LoadMw { get; init; }
    public required double[] PvMw { get; init; }
    /// <summary>Slack (grid) active power per step [MW] (positive = import).</summary>
    public required double[] GridMw { get; init; }
    /// <summary>Bus voltage magnitudes [step, bus position] [pu].</summary>
    public required double[,] BusVoltages { get; init; }
    public required int[] BusIds { get; init; }
    /// <summary>Applied events with their actual step time.</summary>
    public required IReadOnlyList<(DateTimeOffset Time, string Description)> Timeline { get; init; }
    /// <summary>Names of the <see cref="QstsSimulation.Controllers"/> (column order of the controller arrays; empty without controllers).</summary>
    public IReadOnlyList<string> ControllerNames { get; init; } = [];
    /// <summary>Controller kinds ("OLTC", "Regulator", "Capacitor"), same order as <see cref="ControllerNames"/>.</summary>
    public IReadOnlyList<string> ControllerKinds { get; init; } = [];
    /// <summary>Controller state at the end of each step [step, controller]: tap position or capacitor stages in service.</summary>
    public double[,] ControllerStates { get; init; } = new double[0, 0];
    /// <summary>Cumulative controller operations at the end of each step [step, controller]: tap steps moved or capacitor switchings.</summary>
    public int[,] ControllerOperations { get; init; } = new int[0, 0];
    /// <summary>Control re-solves performed in each step (0 when no controller acted; empty without controllers).</summary>
    public int[] ControlIterations { get; init; } = [];

    public int Steps => Times.Length;
    public double EnergyLoadMwh => LoadMw.Sum() * StepHours;
    public double EnergyPvMwh => PvMw.Sum() * StepHours;
    public double EnergyLossesMwh => LossesMw.Sum() * StepHours;
    public double EnergyImportMwh => GridMw.Where(g => g > 0).Sum() * StepHours;
    public double EnergyExportMwh => -GridMw.Where(g => g < 0).Sum() * StepHours;
    /// <summary>Number of steps with any voltage outside [vmin, vmax].</summary>
    public int VoltageViolationSteps(double vmin = 0.95, double vmax = 1.05) => VminPu.Zip(VmaxPu).Count(p => p.First < vmin || p.Second > vmax);

    /// <summary>State trajectory of controller <paramref name="index"/> (tap position or stages in service per step).</summary>
    public double[] ControllerState(int index)
    {
        var v = new double[Steps];
        for (int t = 0; t < Steps; t++) v[t] = ControllerStates[t, index];
        return v;
    }

    /// <summary>State trajectory of the controller named <paramref name="name"/>.</summary>
    public double[] ControllerState(string name) => ControllerState(ControllerIndex(name));

    /// <summary>Total operations of controller <paramref name="index"/> over the run (tap steps or switching operations).</summary>
    public int TotalOperations(int index) => Steps == 0 ? 0 : ControllerOperations[Steps - 1, index];

    /// <summary>Total operations of the controller named <paramref name="name"/>.</summary>
    public int TotalOperations(string name) => TotalOperations(ControllerIndex(name));

    private int ControllerIndex(string name)
    {
        for (int k = 0; k < ControllerNames.Count; k++) if (ControllerNames[k] == name) return k;
        throw new KeyNotFoundException($"No controller named '{name}'.");
    }

    /// <summary>Voltage profile of one bus over time [pu].</summary>
    public double[] VoltageOf(int busId)
    {
        int b = Array.IndexOf(BusIds, busId);
        var v = new double[Steps];
        for (int t = 0; t < Steps; t++) v[t] = BusVoltages[t, b];
        return v;
    }

    /// <summary>Named series for charting (time axis as hours from start in "hours").</summary>
    public IReadOnlyDictionary<string, double[]> ToChartSeries() => new Dictionary<string, double[]>
    {
        ["hours"] = Times.Select(t => (t - Times[0]).TotalHours).ToArray(),
        ["vmin_pu"] = VminPu, ["vmax_pu"] = VmaxPu, ["losses_mw"] = LossesMw, ["max_loading_pct"] = MaxLoadingPercent,
        ["load_mw"] = LoadMw, ["pv_mw"] = PvMw, ["grid_mw"] = GridMw,
    };
}

/// <summary>
/// Quasi-static time-series (QSTS) simulation: a sequence of AC power flows over load/PV profiles with warm starts (the previous solution
/// initializes the next step) and a timeline of switching events. Load multipliers [pu of the base-case load] are taken, in order of
/// precedence, from <see cref="LoadProfiles"/> (by load index), <see cref="CategoryProfiles"/> (by <see cref="Load.Category"/>) and
/// <see cref="DefaultLoadProfile"/>; loads without any profile keep their base value. All profiles must share the same step.
/// </summary>
public sealed class QstsSimulation(PowerNetwork baseNetwork)
{
    public PowerNetwork BaseNetwork { get; } = baseNetwork;
    public TimeSeries? DefaultLoadProfile { get; set; }
    public Dictionary<string, TimeSeries> CategoryProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, TimeSeries> LoadProfiles { get; } = [];
    public List<PvSystem> PvSystems { get; } = [];
    public List<SimulationEvent> Events { get; } = [];
    public PowerFlowOptions Options { get; set; } = new() { Tolerance = 1e-8 };
    /// <summary>
    /// Discrete controllers (OLTCs, regulators, capacitor banks) evaluated after each converged power flow, in list order.
    /// Their targets refer to the base network's branch indices / bus ids (PV systems are appended as generators only).
    /// </summary>
    public List<QstsController> Controllers { get; } = [];
    /// <summary>Maximum control re-solves per step (bounds hunting between controllers).</summary>
    public int MaxControlIterations { get; set; } = 10;

    private IEnumerable<TimeSeries> AllProfiles()
    {
        if (DefaultLoadProfile is not null) yield return DefaultLoadProfile;
        foreach (var p in CategoryProfiles.Values) yield return p;
        foreach (var p in LoadProfiles.Values) yield return p;
        foreach (var p in PvSystems) yield return p.ProfilePu;
    }

    /// <summary>Runs the simulation over <paramref name="steps"/> steps (default: the shortest profile).</summary>
    public QstsResult Run(int? steps = null)
    {
        var profiles = AllProfiles().ToList();
        if (profiles.Count == 0) throw new InvalidOperationException("No profiles assigned.");
        var start = profiles[0].Start;
        var step = profiles[0].Step;
        if (profiles.Any(p => p.Step != step)) throw new InvalidOperationException("All profiles must have the same step.");
        int n = steps ?? profiles.Min(p => p.Count);
        var work = BaseNetwork.Clone();
        var baseP = work.Loads.Select(l => l.P).ToArray();
        var baseQ = work.Loads.Select(l => l.Q).ToArray();
        var loadProfile = new TimeSeries?[work.Loads.Count];
        for (int k = 0; k < work.Loads.Count; k++)
            loadProfile[k] = LoadProfiles.TryGetValue(k, out var lp) ? lp
                : work.Loads[k].Category is { } c && CategoryProfiles.TryGetValue(c, out var cp) ? cp : DefaultLoadProfile;
        var pvGens = PvSystems.Select(pv =>
        {
            var g = new Generator { Bus = pv.Bus, Kind = GeneratorKind.Solar, Name = pv.Name ?? $"PV@{pv.Bus}", Pmax = pv.RatedMw };
            work.Generators.Add(g);
            return g;
        }).ToList();
        int nb = work.BusCount;
        var r = new QstsArrays(n, nb);
        var pending = Events.OrderBy(e => e.At).ToList();
        int nextEvent = 0;
        double loadScale = 1;
        var timeline = new List<(DateTimeOffset, string)>();
        var opts = Options with { FlatStart = true };
        int slack = work.SlackBus is { } sb ? sb.Id : work.Buses[0].Id;
        int nc = Controllers.Count;
        foreach (var c in Controllers) c.Initialize(work);
        var cState = new double[n, nc];
        var cOps = new int[n, nc];
        var cIter = new int[nc > 0 ? n : 0];
        for (int t = 0; t < n; t++)
        {
            var time = start + step * t;
            while (nextEvent < pending.Count && pending[nextEvent].At <= step * t)
            {
                var e = pending[nextEvent++];
                loadScale = Apply(work, e, loadScale);
                timeline.Add((time, e.Description ?? $"{e.Kind} {e.Index}{(e.Value != 0 ? $" ({e.Value})" : "")}"));
                opts = opts with { FlatStart = true };
            }
            for (int k = 0; k < work.Loads.Count; k++)
            {
                double m = (loadProfile[k] is { } p ? p[t] : 1) * loadScale;
                work.Loads[k].P = baseP[k] * m;
                work.Loads[k].Q = baseQ[k] * m;
            }
            double pvTotal = 0;
            for (int k = 0; k < PvSystems.Count; k++)
            {
                var pv = PvSystems[k];
                double p = Math.Max(0, pv.RatedMw * pv.ProfilePu[t]);
                pvGens[k].P = p;
                pvGens[k].Q = -p * Math.Tan(Math.Acos(Math.Clamp(pv.PowerFactor, 0.05, 1)));
                pvTotal += p;
            }
            r.Times[t] = time;
            r.Pv[t] = pvTotal;
            r.Load[t] = work.Loads.Where(l => l.InService).Sum(l => l.P);
            PowerFlowResult? res = null;
            try { res = PowerFlowSolver.Solve(work, opts); } catch (InvalidOperationException) { }
            for (int it = 0; nc > 0 && it < MaxControlIterations && res is { Converged: true }; it++)
            {
                bool acted = false;
                foreach (var c in Controllers) acted |= c.Update(work, res, t);
                if (!acted) break;
                cIter[t]++;
                res.ApplyTo(work);
                try { res = PowerFlowSolver.Solve(work, opts with { FlatStart = false }); } catch (InvalidOperationException) { res = null; }
            }
            for (int k = 0; k < nc; k++) { cState[t, k] = Controllers[k].State; cOps[t, k] = Controllers[k].Operations; }
            if (res is null || !res.Converged)
            {
                r.Converged[t] = false;
                r.Vmin[t] = r.Vmax[t] = r.Losses[t] = r.Loading[t] = r.Grid[t] = double.NaN;
                for (int b = 0; b < nb; b++) r.V[t, b] = double.NaN;
                opts = opts with { FlatStart = true };
                continue;
            }
            r.Converged[t] = true;
            r.Iterations[t] = res.Iterations;
            var live = res.Buses.Where(b => b.Vm > 0).ToList();
            var minB = live.MinBy(b => b.Vm)!;
            r.Vmin[t] = minB.Vm; r.VminBus[t] = minB.BusId; r.Vmax[t] = live.Max(b => b.Vm);
            r.Losses[t] = res.LossesMw;
            r.Loading[t] = res.Branches.Count == 0 ? 0 : res.Branches.Max(b => b.LoadingPercent);
            r.Grid[t] = res.Generators.Where(g => g.Bus == slack).Sum(g => g.P);
            for (int b = 0; b < nb; b++) r.V[t, b] = res.Buses[b].Vm;
            res.ApplyTo(work);
            for (int k = 0; k < PvSystems.Count; k++) pvGens[k].P = 0; // restored next step from the profile
            opts = opts with { FlatStart = false };
        }
        return new QstsResult
        {
            Times = r.Times, StepHours = step.TotalHours, Converged = r.Converged, Iterations = r.Iterations, VminPu = r.Vmin, VmaxPu = r.Vmax,
            VminBus = r.VminBus, LossesMw = r.Losses, MaxLoadingPercent = r.Loading, LoadMw = r.Load, PvMw = r.Pv, GridMw = r.Grid, BusVoltages = r.V,
            BusIds = work.Buses.Select(b => b.Id).ToArray(), Timeline = timeline,
            ControllerNames = Controllers.Select(c => c.DisplayName).ToArray(), ControllerKinds = Controllers.Select(c => c.Kind).ToArray(),
            ControllerStates = cState, ControllerOperations = cOps, ControlIterations = cIter,
        };
    }

    private static double Apply(PowerNetwork net, SimulationEvent e, double loadScale)
    {
        switch (e.Kind)
        {
            case SimulationEventKind.BranchOpen: net.Branches[e.Index].InService = false; break;
            case SimulationEventKind.BranchClose: net.Branches[e.Index].InService = true; break;
            case SimulationEventKind.GeneratorTrip: net.Generators[e.Index].InService = false; break;
            case SimulationEventKind.GeneratorReturn: net.Generators[e.Index].InService = true; break;
            case SimulationEventKind.LoadScale: return e.Value;
            case SimulationEventKind.ShuntSet: net.GetBus(e.Index).Bs = e.Value; break;
        }
        return loadScale;
    }

    /// <summary>
    /// Demonstration QSTS on the Baran–Wu 33-bus feeder: loads on odd buses are residential and on even buses commercial
    /// (synthetic shapes normalized to their peak), and a PV plant of <paramref name="pvMw"/> at bus <paramref name="pvBus"/> follows a
    /// synthetic irradiance profile (Jakarta, 35 % mean cloud cover). Step 15 min, starting Monday 2024-01-01 00:00 WIB.
    /// </summary>
    public static QstsSimulation Case33Demo(int days = 1, double pvMw = 1.5, int pvBus = 18)
    {
        var net = TestCases.Case33BW();
        foreach (var l in net.Loads) l.Category = l.Bus % 2 == 1 ? "residential" : "commercial";
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(7));
        var step = TimeSpan.FromMinutes(15);
        var sim = new QstsSimulation(net);
        sim.CategoryProfiles["residential"] = SyntheticProfiles.Load(LoadProfileKind.Residential, start, days, step, 1.0, noise: 0.02, seed: 1);
        sim.CategoryProfiles["commercial"] = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, days, step, 1.0, noise: 0.02, seed: 2);
        var ghi = SyntheticProfiles.Irradiance(start, days, step, -6.2, 106.8, 0.35);
        sim.PvSystems.Add(new PvSystem(pvBus, pvMw, ghi.Scale(1 / 1000.0), Name: "PV plant"));
        return sim;
    }

    /// <summary>
    /// Volt/var control demonstration on the Baran-Wu 33-bus feeder (same profiles and PV as <see cref="Case33Demo"/>) fed through a
    /// 10 MVA substation transformer: a new slack bus 0 (grid, 1.0 pu) connects to bus 1 over branch 0-1 (R = 0.005, X = 0.06 pu on
    /// 10 MVA, branch index 32). With <paramref name="withControl"/> the transformer gets an OLTC regulating bus 1 to 1.02 pu
    /// (band 0.0125 pu, 16 x 0.625 % each way, 1-step delay) and bus 30 a 2 x 0.45 Mvar capacitor bank in voltage mode (on below 0.97 pu,
    /// off above 1.03 pu at bus 30, 1-step delay). Without control the tap stays at 1 and the bank is absent.
    /// </summary>
    public static QstsSimulation Case33ControlDemo(int days = 1, bool withControl = true, double pvMw = 1.5)
    {
        var sim = Case33Demo(days, pvMw);
        var net = sim.BaseNetwork;
        net.Buses.Insert(0, new Bus { Id = 0, Name = "Grid 150 kV", BaseKv = 150, Type = BusType.Slack });
        net.InvalidateIndex();
        net.GetBus(1).Type = BusType.PQ;
        net.GetBus(1).Name = "Substation 12.66 kV";
        foreach (var g in net.Generators.Where(g => g.Bus == 1)) g.Bus = 0;
        net.Branches.Add(new Branch { From = 0, To = 1, Kind = BranchKind.Transformer, R = 0.005, X = 0.06, Tap = 1, RateA = 10, Name = "Substation transformer" });
        if (withControl)
        {
            sim.Controllers.Add(new OltcController(net.Branches.Count - 1, 1)
            {
                Name = "OLTC T1", TargetPu = 1.02, BandwidthPu = 0.0125, TapStepPu = 0.00625, MinTap = -16, MaxTap = 16, DelaySteps = 1,
            });
            sim.Controllers.Add(new CapacitorController(30, 0.45, 2)
            {
                Name = "Cap bus 30", Mode = CapacitorControlMode.Voltage, OnThreshold = 0.97, OffThreshold = 1.03, DelaySteps = 1,
            });
        }
        return sim;
    }

    private sealed class QstsArrays(int n, int nb)
    {
        public readonly DateTimeOffset[] Times = new DateTimeOffset[n];
        public readonly bool[] Converged = new bool[n];
        public readonly int[] Iterations = new int[n];
        public readonly double[] Vmin = new double[n], Vmax = new double[n], Losses = new double[n], Loading = new double[n];
        public readonly double[] Load = new double[n], Pv = new double[n], Grid = new double[n];
        public readonly int[] VminBus = new int[n];
        public readonly double[,] V = new double[n, nb];
    }
}
