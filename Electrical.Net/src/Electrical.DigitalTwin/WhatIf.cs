using System.Globalization;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.DigitalTwin;

/// <summary>A change applied to a cloned network in a what-if study.</summary>
public abstract record WhatIfAction
{
    public abstract void Apply(PowerNetwork net);
    public abstract string Describe(string language = "en");
}

/// <summary>Opens (switches out) branch <paramref name="BranchIndex"/>.</summary>
public sealed record OpenBranch(int BranchIndex) : WhatIfAction
{
    public override void Apply(PowerNetwork net) => net.Branches[BranchIndex].InService = false;
    public override string Describe(string language = "en") => language == "id" ? $"buka cabang #{BranchIndex}" : $"open branch #{BranchIndex}";
}

/// <summary>Closes (returns to service) branch <paramref name="BranchIndex"/>.</summary>
public sealed record CloseBranch(int BranchIndex) : WhatIfAction
{
    public override void Apply(PowerNetwork net) => net.Branches[BranchIndex].InService = true;
    public override string Describe(string language = "en") => language == "id" ? $"tutup cabang #{BranchIndex}" : $"close branch #{BranchIndex}";
}

/// <summary>Scales P and Q of all loads (or of the loads at <paramref name="BusId"/>) by <paramref name="Factor"/>.</summary>
public sealed record ScaleLoad(double Factor, int? BusId = null) : WhatIfAction
{
    public override void Apply(PowerNetwork net)
    {
        foreach (var l in net.Loads)
            if (BusId is null || l.Bus == BusId) { l.P *= Factor; l.Q *= Factor; }
    }

    public override string Describe(string language = "en") => language == "id"
        ? $"skala beban ×{Factor.ToString("0.###", CultureInfo.InvariantCulture)}{(BusId is null ? "" : $" di bus {BusId}")}"
        : $"scale load ×{Factor.ToString("0.###", CultureInfo.InvariantCulture)}{(BusId is null ? "" : $" at bus {BusId}")}";
}

/// <summary>Adds a PV plant injecting <paramref name="Mw"/> [MW] and <paramref name="Mvar"/> [Mvar] (PQ injection) at a bus.</summary>
public sealed record AddPv(int BusId, double Mw, double Mvar = 0) : WhatIfAction
{
    public override void Apply(PowerNetwork net) =>
        net.Generators.Add(new Generator { Bus = BusId, Name = $"PV@{BusId}", Kind = GeneratorKind.Solar, P = Mw, Q = Mvar, Pmax = Mw, Qmax = Mvar, Qmin = Mvar });
    public override string Describe(string language = "en") => language == "id"
        ? $"tambah PV {Mw.ToString("0.###", CultureInfo.InvariantCulture)} MW di bus {BusId}"
        : $"add {Mw.ToString("0.###", CultureInfo.InvariantCulture)} MW PV at bus {BusId}";
}

/// <summary>Adds a shunt capacitor of <paramref name="Mvar"/> [Mvar at 1 pu] at a bus.</summary>
public sealed record AddShuntCapacitor(int BusId, double Mvar) : WhatIfAction
{
    public override void Apply(PowerNetwork net) => net.GetBus(BusId).Bs += Mvar;
    public override string Describe(string language = "en") => language == "id"
        ? $"tambah kapasitor {Mvar.ToString("0.###", CultureInfo.InvariantCulture)} Mvar di bus {BusId}"
        : $"add {Mvar.ToString("0.###", CultureInfo.InvariantCulture)} Mvar capacitor at bus {BusId}";
}

/// <summary>Sets the active output [MW] of generator <paramref name="GeneratorIndex"/>.</summary>
public sealed record SetGeneration(int GeneratorIndex, double Mw) : WhatIfAction
{
    public override void Apply(PowerNetwork net) => net.Generators[GeneratorIndex].P = Mw;
    public override string Describe(string language = "en") => language == "id"
        ? $"atur pembangkit #{GeneratorIndex} = {Mw.ToString("0.###", CultureInfo.InvariantCulture)} MW"
        : $"set generator #{GeneratorIndex} to {Mw.ToString("0.###", CultureInfo.InvariantCulture)} MW";
}

/// <summary>Outcome of a what-if study: KPIs before/after, the scenario power flow and its explained alarms.</summary>
public sealed record WhatIfResult(string Description, string DescriptionId, GridKpis Baseline, GridKpis Scenario, PowerFlowResult? ScenarioFlow,
    IReadOnlyList<ExplainedAlarm> Alarms, string Summary, string SummaryId)
{
    public double DeltaLossesMw => Scenario.LossesMw - Baseline.LossesMw;
    public double DeltaMinVoltagePu => Scenario.MinVoltagePu - Baseline.MinVoltagePu;
    public double DeltaMaxLoadingPercent => Scenario.MaxLoadingPercent - Baseline.MaxLoadingPercent;
}

/// <summary>Clone-and-solve what-if simulation.</summary>
public static class WhatIfSimulator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Solves <paramref name="baseNetwork"/> and a clone with <paramref name="actions"/> applied (Newton–Raphson) and compares KPIs.
    /// The base network is not modified.
    /// </summary>
    public static WhatIfResult Run(PowerNetwork baseNetwork, IEnumerable<WhatIfAction> actions, GridTwinOptions? options = null, AlarmContext? context = null)
    {
        var o = options ?? GridTwinOptions.Default;
        var acts = actions.ToList();
        var basePf = SafeSolve(baseNetwork);
        var baseKpi = basePf is null ? GridKpis.Failed : GridKpis.From(basePf, o.VoltageLowPu, o.VoltageHighPu, o.OverloadPercent);
        var scen = baseNetwork.Clone();
        foreach (var a in acts) a.Apply(scen);
        var pf = SafeSolve(scen);
        var kpi = pf is null ? GridKpis.Failed : GridKpis.From(pf, o.VoltageLowPu, o.VoltageHighPu, o.OverloadPercent);
        var open = scen.Branches.Select((b, i) => (b, i)).Where(x => !x.b.InService && baseNetwork.Branches[x.i].InService).Select(x => x.i).ToList();
        var ctx = (context ?? new AlarmContext()) with { OpenBranches = open };
        IReadOnlyList<ExplainedAlarm> alarms = pf is null
            ? [new ExplainedAlarm(TwinAlarmKind.EstimatorFailure, "system", 0, 0, TwinAlarmSeverity.Critical,
                "Scenario power flow did not converge (voltage collapse or islanding).", "Aliran daya skenario tidak konvergen (runtuh tegangan atau pulau).", ["non-convergence"])]
            : AlarmExplainer.Explain(pf, o, ctx);
        string desc = acts.Count == 0 ? "base case" : string.Join(", ", acts.Select(a => a.Describe()));
        string descId = acts.Count == 0 ? "kasus dasar" : string.Join(", ", acts.Select(a => a.Describe("id")));
        string summary, summaryId;
        if (pf is null)
        {
            summary = $"What-if [{desc}]: the scenario does not converge.";
            summaryId = $"Simulasi [{descId}]: skenario tidak konvergen.";
        }
        else
        {
            summary = $"What-if [{desc}]: losses {F(baseKpi.LossesMw)} → {F(kpi.LossesMw)} MW, Vmin {F(baseKpi.MinVoltagePu, 4)} → {F(kpi.MinVoltagePu, 4)} pu (bus {kpi.MinVoltageBus}), " +
                      $"max loading {F(baseKpi.MaxLoadingPercent, 1)} → {F(kpi.MaxLoadingPercent, 1)}%, violations {baseKpi.Overloads + baseKpi.VoltageViolations} → {kpi.Overloads + kpi.VoltageViolations}.";
            summaryId = $"Simulasi [{descId}]: rugi-rugi {F(baseKpi.LossesMw)} → {F(kpi.LossesMw)} MW, Vmin {F(baseKpi.MinVoltagePu, 4)} → {F(kpi.MinVoltagePu, 4)} pu (bus {kpi.MinVoltageBus}), " +
                        $"pembebanan maks {F(baseKpi.MaxLoadingPercent, 1)} → {F(kpi.MaxLoadingPercent, 1)}%, pelanggaran {baseKpi.Overloads + baseKpi.VoltageViolations} → {kpi.Overloads + kpi.VoltageViolations}.";
        }
        return new WhatIfResult(desc, descId, baseKpi, kpi, pf, alarms, summary, summaryId);
    }

    internal static PowerFlowResult? SafeSolve(PowerNetwork net)
    {
        try
        {
            if (Topology.Islands(net).Count > 1) return null;
            var r = PowerFlowSolver.Solve(net);
            return r.Converged ? r : null;
        }
        catch (Exception e) when (e is InvalidOperationException or ArithmeticException or ArgumentException)
        {
            return null;
        }
    }

    private static string F(double v, int d = 3) => v.ToString("F" + d, Inv);
}
