using System.Diagnostics;
using Electrical.PowerFlow;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

public sealed record ContingencyDemoResult(
    string Case, double LoadScale, IReadOnlyList<ContingencyResult> Ranking, string ChartSvg, string WorstSldSvg, string? WorstOutage,
    bool RatingsAssumed, double Milliseconds, IReadOnlyList<Kpi> Kpis);

/// <summary>N-1 branch contingency screening and ranking.</summary>
public static class ContingencyScenario
{
    public static readonly string[] Cases = ["case9", "case14", "synthetic"];

    public static ContingencyDemoResult Run(string caseKey = "case9", double loadScale = 1.0, string lang = "en")
    {
        var net = GalleryCases.Scaled(GalleryCases.Load(caseKey), loadScale);
        // Ratings (where the case has none) come from the unscaled base case so that higher load scales show real overloads.
        bool assumed = GalleryCases.AssignRatings(net, PowerFlowSolver.Solve(GalleryCases.Load(caseKey)));
        var baseCase = PowerFlowSolver.Solve(net);

        var sw = Stopwatch.StartNew();
        var ranking = ContingencyAnalysis.RunN1(net);
        sw.Stop();

        var top = ranking.Where(r => !r.Islanding && r.Converged).Take(12).ToList();
        string chart = SvgCharts.Bar(top.Select(r => r.Outage).ToList(),
            [new ChartSeries(lang == "id" ? "Pembebanan maks (%)" : "Max loading (%)", top.Select(r => r.MaxLoadingPercent).ToList(), SvgTheme.Var("eln-amber"))],
            new ChartOptions
            {
                Width = 640, Height = 260, YLabel = lang == "id" ? "pembebanan maks cabang (%)" : "max branch loading (%)", ShowLegend = false, ValueFormat = "0",
                XLabel = lang == "id" ? "cabang yang lepas" : "outaged branch", ReferenceLines = [(100, "100 %")], YMin = 0,
            });

        var worst = ranking.FirstOrDefault(r => !r.Islanding && r.Converged);
        string sld = "";
        if (worst is not null)
        {
            var c = net.Clone();
            c.Branches[worst.BranchIndex].InService = false;
            var r = PowerFlowSolver.Solve(c);
            sld = SldRenderer.Render(c, r, new SldOptions { LayerGap = c.BusCount > 20 ? 96 : 120, BusGap = c.BusCount > 20 ? 34 : 50, ShowFlows = c.BusCount <= 20 });
        }

        int islanding = ranking.Count(r => r.Islanding);
        int insecure = ranking.Count(r => r.Overloads > 0 || r.VoltageViolations > 0 || !r.Converged && !r.Islanding);
        var kpis = new List<Kpi>
        {
            new(new("Outages studied", "Gangguan dihitung"), ranking.Count.ToString(), null, new($"{F(sw.Elapsed.TotalMilliseconds, "0")} ms in parallel", $"{F(sw.Elapsed.TotalMilliseconds, "0")} md paralel")),
            new(new("Insecure", "Tidak aman"), insecure.ToString(), null, new("overload or V violation", "beban lebih / pelanggaran V"), insecure > 0 ? "alarm" : "ok"),
            new(new("Islanding", "Pemisahan pulau"), islanding.ToString(), null, new("radial branches", "cabang radial"), islanding > 0 ? "warn" : "ok"),
            new(new("Worst outage", "Gangguan terburuk"), worst?.Outage ?? "—", null,
                worst is null ? null : new($"max {F(worst.MaxLoadingPercent, "0.0")} % on {worst.MostLoadedBranch}", $"maks {F(worst.MaxLoadingPercent, "0.0")} % di {worst.MostLoadedBranch}"),
                worst is { Overloads: > 0 } ? "alarm" : "warn"),
            new(new("Base-case loading", "Pembebanan dasar"), F(baseCase.MostLoadedBranch?.LoadingPercent ?? 0, "0.0"), "%",
                new($"branch {baseCase.MostLoadedBranch?.From}–{baseCase.MostLoadedBranch?.To}", $"cabang {baseCase.MostLoadedBranch?.From}–{baseCase.MostLoadedBranch?.To}")),
        };
        return new ContingencyDemoResult(caseKey, loadScale, ranking, chart, sld, worst?.Outage, assumed, sw.Elapsed.TotalMilliseconds, kpis);
    }
}
