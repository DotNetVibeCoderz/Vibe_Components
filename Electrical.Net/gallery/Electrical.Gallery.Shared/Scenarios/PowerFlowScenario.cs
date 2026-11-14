using System.Diagnostics;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

/// <summary>One solver run in the method comparison.</summary>
public sealed record MethodRun(PowerFlowMethod Method, string Label, bool Applicable, bool Converged, int Iterations, double Milliseconds,
    double LossesMw, double MinVm, int MinVmBus, IReadOnlyList<double> History, L? Note);

public sealed record PowerFlowDemoResult(
    string Case, double LoadScale, PowerFlowMethod Method, PowerNetwork Network, PowerFlowResult Result,
    string SldSvg, string VoltageSvg, string ConvergenceSvg, IReadOnlyList<MethodRun> Methods, IReadOnlyList<Kpi> Kpis, bool Radial);

/// <summary>Power flow on a test case with a selected method, plus a comparison of every method on the same operating point.</summary>
public static class PowerFlowScenario
{
    public static readonly (PowerFlowMethod Method, string Short, L Name)[] MethodNames =
    [
        (PowerFlowMethod.NewtonRaphson, "NR", new("Newton–Raphson", "Newton–Raphson")),
        (PowerFlowMethod.FastDecoupled, "FDLF", new("Fast-decoupled (XB)", "Fast-decoupled (XB)")),
        (PowerFlowMethod.GaussSeidel, "GS", new("Gauss–Seidel", "Gauss–Seidel")),
        (PowerFlowMethod.DC, "DC", new("DC (linear)", "DC (linier)")),
        (PowerFlowMethod.BackwardForwardSweep, "BFS", new("Backward/forward sweep", "Backward/forward sweep")),
    ];

    public static string Short(PowerFlowMethod m) => MethodNames.First(x => x.Method == m).Short;

    public static PowerFlowDemoResult Run(string caseKey = "case14", double loadScale = 1.0, PowerFlowMethod method = PowerFlowMethod.NewtonRaphson, string lang = "en")
    {
        var net = GalleryCases.Scaled(GalleryCases.Load(caseKey), loadScale);
        bool radial = Topology.IsRadial(net);
        var runs = MethodNames.Select(m => RunOne(net, m.Method, radial)).ToList();

        var chosen = runs.First(r => r.Method == method);
        if (!chosen.Applicable) method = PowerFlowMethod.NewtonRaphson;
        var result = PowerFlowSolver.Solve(net, Options(method));

        string sld = SldRenderer.Render(net, result, new SldOptions
        {
            LayerGap = net.BusCount > 20 ? 96 : 130, ShowLabels = true, ShowFlows = net.BusCount <= 20, BusGap = net.BusCount > 20 ? 34 : 56,
        });

        var vm = result.Buses.Select(b => b.Vm).ToList();
        double vLo = Math.Min(0.9, Math.Floor(vm.Min() * 50) / 50), vHi = Math.Max(1.1, Math.Ceiling(vm.Max() * 50) / 50);
        string vsvg = SvgCharts.Line(
            [new ChartSeries("|V|", vm, SvgTheme.Var("eln-accent")) { Area = true, StrokeWidth = 2.4 }],
            new ChartOptions
            {
                Width = 640, Height = 240, YLabel = "|V| (pu)", XLabel = "bus", ShowLegend = false, YMin = vLo, YMax = vHi,
                XLabels = result.Buses.Select(b => b.BusId.ToString()).ToList(),
                ReferenceLines = [(0.95, "0.95"), (1.05, "1.05")],
            });

        var conv = runs.Where(r => r.Applicable && r.History.Count > 0).Select((r, i) => new ChartSeries(Short(r.Method),
            r.History.Take(40).Select(h => Math.Log10(Math.Max(h, 1e-16))).ToList(), SvgTheme.Series(MethodIndex(r.Method)))
        { StrokeWidth = 2.2 }).ToList();
        string csvg = SvgCharts.Line(conv, new ChartOptions
        {
            Width = 640, Height = 260, XLabel = lang == "id" ? "iterasi" : "iteration", YLabel = "log₁₀ mismatch (pu)",
            ReferenceLines = [(-8, lang == "id" ? "toleransi" : "tolerance")],
        });

        var most = result.MostLoadedBranch;
        var viol = result.VoltageViolations().Count();
        var kpis = new List<Kpi>
        {
            new(new("Status", "Status"), result.Converged ? "CONVERGED" : "DIVERGED", null,
                new($"{Short(method)} · {result.Iterations} it · {F(result.Elapsed.TotalMilliseconds, "0.00")} ms", $"{Short(method)} · {result.Iterations} iterasi · {F(result.Elapsed.TotalMilliseconds, "0.00")} md"),
                result.Converged ? "ok" : "alarm"),
            new(new("Load", "Beban"), F(result.TotalLoadMw, "0.0"), "MW", new($"{F(result.TotalLoadMvar, "0.0")} Mvar", $"{F(result.TotalLoadMvar, "0.0")} Mvar")),
            new(new("Losses", "Rugi-rugi"), F(result.LossesMw, result.LossesMw < 1 ? "0.000" : "0.00"), "MW",
                new($"{F(100 * result.LossesMw / Math.Max(result.TotalLoadMw, 1e-9), "0.00")} % of load", $"{F(100 * result.LossesMw / Math.Max(result.TotalLoadMw, 1e-9), "0.00")} % dari beban")),
            new(new("Min voltage", "Tegangan min"), F(result.MinVoltageBus.Vm, "0.0000"), "pu", new($"bus {result.MinVoltageBus.BusId}", $"bus {result.MinVoltageBus.BusId}"),
                result.MinVoltageBus.Vm < 0.95 ? "alarm" : result.MinVoltageBus.Vm < 0.97 ? "warn" : "ok"),
            new(new("Most loaded", "Beban tertinggi"), most is null ? "—" : F(most.LoadingPercent, "0.0"), most is null ? null : "%",
                most is null ? new("no ratings", "tanpa rating") : new($"branch {most.From}–{most.To}", $"cabang {most.From}–{most.To}"),
                most is null ? null : most.LoadingPercent > 100 ? "alarm" : most.LoadingPercent > 80 ? "warn" : "ok"),
            new(new("Violations", "Pelanggaran"), viol.ToString(), null, new("buses outside V band", "bus di luar batas V"), viol > 0 ? "alarm" : "ok"),
        };
        return new PowerFlowDemoResult(caseKey, loadScale, method, net, result, sld, vsvg, csvg, runs, kpis, radial);
    }

    private static int MethodIndex(PowerFlowMethod m) => m switch
    {
        PowerFlowMethod.NewtonRaphson => 0, PowerFlowMethod.FastDecoupled => 1, PowerFlowMethod.GaussSeidel => 2, PowerFlowMethod.DC => 4, _ => 3,
    };

    private static PowerFlowOptions Options(PowerFlowMethod m) => new()
    {
        Method = m, MaxIterations = m == PowerFlowMethod.GaussSeidel ? 400 : 40, Tolerance = 1e-8,
    };

    private static MethodRun RunOne(PowerNetwork net, PowerFlowMethod m, bool radial)
    {
        string label = Short(m);
        if (m == PowerFlowMethod.BackwardForwardSweep && !radial)
            return new MethodRun(m, label, false, false, 0, 0, double.NaN, double.NaN, 0, [], new("radial networks only", "hanya jaringan radial"));
        try
        {
            var sw = Stopwatch.StartNew();
            var r = PowerFlowSolver.Solve(net, Options(m));
            sw.Stop();
            L? note = m == PowerFlowMethod.DC ? new("lossless, |V| = 1 pu", "tanpa rugi, |V| = 1 pu") : null;
            return new MethodRun(m, label, true, r.Converged, r.Iterations, sw.Elapsed.TotalMilliseconds, r.LossesMw, r.MinVoltageBus.Vm, r.MinVoltageBus.BusId, r.History, note);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or ArithmeticException)
        {
            return new MethodRun(m, label, false, false, 0, 0, double.NaN, double.NaN, 0, [], new(e.Message, e.Message));
        }
    }
}
