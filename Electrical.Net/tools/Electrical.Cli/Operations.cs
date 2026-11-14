using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.Optimization;
using Electrical.PowerFlow;
using Electrical.Renewable;
using Electrical.Simulation;
using Electrical.SmartGrid;
using Electrical.StateEstimation;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchOperations(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "ed" or "dispatch" => Operations.Dispatch(cli),
            "opf" => Operations.Opf(cli),
            "uc" => Operations.UnitCommitmentCmd(),
            "se" => Operations.StateEstimationCmd(cli),
            "forecast" => Operations.ForecastCmd(cli),
            "reconfig" => Operations.Reconfig(),
            "vvo" => Operations.Vvo(),
            "caps" => Operations.Caps(),
            "hosting" => Operations.Hosting(cli),
            "qsts" => Operations.Qsts(cli),
            _ => null,
        };
        DispatchIntelligence(cli, ref result);
    }

    static partial void DispatchIntelligence(Args cli, ref Task<int>? result);
}

public static partial class Commands
{
    static partial void MoreHelpOperations(ref string text)
    {
        text += """

              ed [--losses]                           Economic dispatch (λ, unit outputs; B-coefficient losses with --losses)
              opf [--limit 5-6:60]                    DC optimal power flow with LMPs (optional line limit override in MW)
              uc                                      24 h unit commitment (Wood & Wollenberg 3-unit example)
              se [--bad 2-3]                          WLS state estimation with bad-data detection (gross error injected on a flow)
              forecast [--method hw|reg|knn|ens] [--days 14]   Day-ahead load forecast on a synthetic profile (MAPE)
              reconfig                                Feeder reconfiguration on the 33-bus feeder with 5 tie switches
              vvo                                     Volt/VAR optimization on the 33-bus feeder
              caps                                    Optimal capacitor placement on the 33-bus feeder
              hosting [--bus 18] [--max 5]            PV hosting capacity at a bus (33-bus feeder)
              qsts [--days 1] [--pv 1.5] [--control]  Quasi-static time-series simulation (15-min load + PV; OLTC + switched capacitor with --control)
            """;
        MoreHelpIntelligence(ref text);
    }

    static partial void MoreHelpIntelligence(ref string text);
}

internal static class Operations
{
    public static Task<int> Dispatch(Args cli)
    {
        var net = cli.Network(cli.Get("case") is null && cli.Get("file") is null ? "case9" : null);
        var r = cli.Flag("losses") ? EconomicDispatch.SolveWithLosses(net) : EconomicDispatch.Solve(net);
        Console.WriteLine($"Economic dispatch on {net.Name}: λ = {r.Lambda:0.000} $/MWh, cost {r.TotalCost:0.00} $/h, losses {r.LossesMw:0.###} MW");
        for (int i = 0; i < r.OutputMw.Length; i++) Console.WriteLine($"  Unit {i + 1}: {r.OutputMw[i],8:0.00} MW");
        return Commands.Ok();
    }

    public static Task<int> Opf(Args cli)
    {
        var net = cli.Network(cli.Get("case") is null && cli.Get("file") is null ? "case9" : null);
        var opts = new DcOpfOptions();
        if (cli.Get("limit") is { } lim)
        {
            var parts = lim.Split(':');
            var ends = parts[0].Split('-').Select(int.Parse).ToArray();
            int idx = net.Branches.FindIndex(b => (b.From == ends[0] && b.To == ends[1]) || (b.From == ends[1] && b.To == ends[0]));
            if (idx < 0) throw new ArgumentException($"Branch {parts[0]} not found.");
            net.Branches[idx].RateA = double.Parse(parts[1]);
        }
        var r = DcOpf.Solve(net, opts);
        Console.WriteLine($"DC-OPF on {net.Name}: {(r.Converged ? "optimal" : "NOT converged")}, cost {r.TotalCost:0.00} $/h, λ = {r.Lambda:0.000} $/MWh");
        for (int i = 0; i < r.DispatchMw.Length; i++) Console.WriteLine($"  Gen {i + 1}: {r.DispatchMw[i],8:0.00} MW");
        Console.WriteLine("  LMP [$/MWh]: " + string.Join("  ", net.Buses.Select((b, i) => $"{b.Id}:{r.Lmp[i]:0.00}")));
        foreach (var b in r.Binding) Console.WriteLine($"  Binding: {b.Description} (shadow price {b.ShadowPrice:0.000})");
        return Commands.Ok();
    }

    public static Task<int> UnitCommitmentCmd()
    {
        var units = UnitCommitment.WoodWollenbergThreeUnits();
        double[] demand = [.. Enumerable.Range(0, 24).Select(h => Math.Round(600 + 450 * Math.Max(0, Math.Sin((h - 6) * Math.PI / 16)), 0))];
        var r = UnitCommitment.Solve(units, demand);
        Console.WriteLine($"Unit commitment (24 h): {(r.Feasible ? "feasible" : "INFEASIBLE")}, production {r.ProductionCost:0} $, startup {r.StartupCost:0} $, total {r.TotalCost:0} $");
        Console.WriteLine($"  Priority order: {string.Join(", ", r.PriorityOrder.Select(i => i + 1))}");
        Console.WriteLine("  Hour  Demand  " + string.Join("  ", Enumerable.Range(1, units.Count).Select(i => $"U{i}[MW]")) + "   λ");
        for (int h = 0; h < demand.Length; h++)
            Console.WriteLine($"  {h,4} {demand[h],7:0}  " + string.Join("  ", Enumerable.Range(0, units.Count).Select(u => r.Status[u, h] ? $"{r.DispatchMw[u, h],6:0}" : "   off")) + $"  {r.Lambda[h],6:0.00}");
        return Commands.Ok();
    }

    public static Task<int> StateEstimationCmd(Args cli)
    {
        var net = cli.Network();
        var pf = PowerFlowSolver.Solve(net);
        var z = MeasurementGenerator.FromPowerFlow(pf);
        if (cli.Get("bad") is { } bad)
        {
            var ends = bad.Split('-').Select(int.Parse).ToArray();
            int br = net.Branches.FindIndex(b => b.From == ends[0] && b.To == ends[1]);
            int k = z.FindIndex(m => m.Type == MeasurementType.ActiveFlow && m.Location == br && m.End == BranchEnd.From);
            if (k >= 0) z[k] = z[k] with { Value = z[k].Value + 25 * z[k].Sigma };
        }
        var r = BadDataDetector.IdentifyAndRemove(net, z);
        var est = r.Final;
        Console.WriteLine($"WLS state estimation on {net.Name}: {z.Count} measurements, converged {est.Converged} in {est.Iterations} it");
        Console.WriteLine($"  Initial J = {r.InitialObjective:0.0} (χ² passed: {r.InitialChiSquarePassed}); final J = {est.ObjectiveJ:0.0} ≤ {est.ChiSquareThreshold:0.0}");
        foreach (var (m, rn) in r.Removed) Console.WriteLine($"  Removed bad measurement: {m.Type} @ {m.Location} (rN = {rn:0.0})");
        double maxDv = pf.Buses.Max(b => Math.Abs(b.Vm - est.VmAt(b.BusId)));
        double maxDa = pf.Buses.Max(b => Math.Abs(b.VaDeg - est.VaDegAt(b.BusId)));
        Console.WriteLine($"  Max |ΔV| vs true state {maxDv:0.0000} pu, max |Δθ| {maxDa:0.000}°");
        return Commands.Ok();
    }

    public static Task<int> ForecastCmd(Args cli)
    {
        int days = cli.Int("days", 14);
        var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        var all = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, days + 1, TimeSpan.FromHours(1), 100, noise: 0.02, seed: 7);
        var history = new Core.Data.TimeSeries(all.Start, all.Step, all.Values[..(days * 24)]);
        var actual = all.Values[(days * 24)..];
        var method = cli.Get("method", "hw") switch
        {
            "reg" => LoadForecastMethod.Regression,
            "knn" => LoadForecastMethod.SimilarDay,
            "ens" => LoadForecastMethod.Ensemble,
            _ => LoadForecastMethod.HoltWinters,
        };
        var fc = LoadForecast.Forecast(history, 24, method);
        var err = ForecastMetrics.Evaluate(actual, fc.Values);
        Console.WriteLine($"Day-ahead load forecast ({method}, {days} days history): MAPE {err.Mape:0.00} %, RMSE {err.Rmse:0.00} MW, bias {err.Bias:0.00} MW");
        for (int h = 0; h < 24; h += 3) Console.WriteLine($"  {h:00}:00  actual {actual[h],7:0.0} MW  forecast {fc.Values[h],7:0.0} MW");
        return Commands.Ok();
    }

    public static Task<int> Reconfig()
    {
        var net = FeederReconfiguration.Case33WithTies();
        var r = FeederReconfiguration.Optimize(net);
        Console.WriteLine($"Feeder reconfiguration (33-bus, 5 ties): open {string.Join(", ", r.OpenBranches)}");
        Console.WriteLine($"  Losses {r.LossesBeforeMw * 1000:0.0} → {r.LossesAfterMw * 1000:0.0} kW ({r.LossReductionPercent:0.0} %), Vmin {r.MinVoltageBefore:0.0000} → {r.MinVoltageAfter:0.0000} pu, {r.PowerFlows} power flows");
        return Commands.Ok();
    }

    public static Task<int> Vvo()
    {
        var r = VoltVarOptimization.Optimize(TestCases.Case33BW(), VoltVarControls.Case33Default());
        Console.WriteLine($"Volt/VAR optimization (33-bus): losses {r.LossesBeforeMw * 1000:0.0} → {r.LossesAfterMw * 1000:0.0} kW, V [{r.VminBefore:0.000}, {r.VmaxBefore:0.000}] → [{r.VminAfter:0.000}, {r.VmaxAfter:0.000}] pu, feasible {r.Feasible}");
        Console.WriteLine($"  Substation set-point {r.SubstationVoltagePu:0.000} pu; capacitors: {string.Join(", ", r.CapacitorMvar.Select(c => $"bus {c.Key} {c.Value * 1000:0} kvar"))}; inverters: {string.Join(", ", r.InverterMvar.Select(c => $"bus {c.Key} {c.Value * 1000:0} kvar"))}");
        return Commands.Ok();
    }

    public static Task<int> Caps()
    {
        var r = CapacitorPlacement.Optimize(TestCases.Case33BW());
        Console.WriteLine($"Capacitor placement (33-bus): {string.Join(", ", r.Capacitors.Select(c => $"bus {c.BusId} {c.Mvar * 1000:0} kvar"))}");
        Console.WriteLine($"  Losses {r.LossesBeforeMw * 1000:0.0} → {r.LossesAfterMw * 1000:0.0} kW ({r.LossReductionPercent:0.0} %), Vmin {r.MinVoltageBefore:0.0000} → {r.MinVoltageAfter:0.0000} pu");
        return Commands.Ok();
    }

    public static Task<int> Hosting(Args cli)
    {
        var net = cli.Network("case33bw");
        int bus = cli.Int("bus", 18);
        var r = HostingCapacity.Analyze(net, bus, cli.Num("step", 0.1), cli.Num("max", 5));
        Console.WriteLine($"PV hosting capacity at bus {bus} of {net.Name}: {r.HostingCapacityMw:0.00} MW (limited by {r.LimitingFactor})");
        return Commands.Ok();
    }

    public static Task<int> Qsts(Args cli)
    {
        var sim = cli.Flag("control")
            ? QstsSimulation.Case33ControlDemo(cli.Int("days", 1), withControl: true, pvMw: cli.Num("pv", 1.5))
            : QstsSimulation.Case33Demo(cli.Int("days", 1), cli.Num("pv", 1.5));
        var r = sim.Run();
        Console.WriteLine($"QSTS (33-bus, {r.Times.Length} steps of {r.StepHours * 60:0} min): converged {r.Converged.Count(c => c)}/{r.Converged.Length}");
        Console.WriteLine($"  Energy: load {r.EnergyLoadMwh:0.00} MWh, PV {r.EnergyPvMwh:0.00} MWh, losses {r.EnergyLossesMwh:0.000} MWh, import {r.EnergyImportMwh:0.00} MWh, export {Math.Abs(r.EnergyExportMwh):0.00} MWh");
        Console.WriteLine($"  Voltage band: min {r.VminPu.Min():0.0000} pu, max {r.VmaxPu.Max():0.0000} pu, steps outside 0.95–1.05 pu: {Enumerable.Range(0, r.Times.Length).Count(k => r.VminPu[k] < 0.95 || r.VmaxPu[k] > 1.05)}");
        if (r.ControllerNames is { Count: > 0 } names)
            Console.WriteLine("  Controllers: " + string.Join(", ", names.Select(n => $"{n} ({r.TotalOperations(n)} operations)")));
        for (int k = 0; k < r.Times.Length; k += Math.Max(1, r.Times.Length / 8))
            Console.WriteLine($"  {r.Times[k]:HH:mm}  load {r.LoadMw[k],6:0.000} MW  PV {r.PvMw[k],6:0.000} MW  Vmin {r.VminPu[k]:0.0000}  losses {r.LossesMw[k] * 1000,6:0.0} kW");
        return Commands.Ok();
    }
}
