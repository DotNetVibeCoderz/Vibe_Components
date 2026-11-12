using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AquaCulture.Analytics;
using AquaCulture.Core.Farming;
using AquaCulture.WaterQuality;

namespace AquaCulture.AI.Agent;

/// <summary>A farm in a portfolio: its records and the live context of each pond (water readings, stock).</summary>
public sealed record FarmSite(Farm Farm, IReadOnlyList<AquaContext> Ponds, DateOnly AsOf, double DefaultFeedPricePerKg = 16_000);

/// <summary>
/// AI copilot across farms: one <see cref="AquaAgent"/> whose ponds are every pond of every farm (named "farm/pond", so "tambak TMS kolam 3"
/// selects it), plus portfolio tools — overview, comparison by a KPI, and alerts across farms — grounded in <see cref="KpiCalculator"/>.
/// </summary>
public static class FarmCopilot
{
    public static AquaAgent Create(IReadOnlyList<FarmSite> sites, IChatModel? model = null, AgentMemory? memory = null)
    {
        if (sites.Count == 0) throw new ArgumentException("At least one farm is required.", nameof(sites));
        foreach (var s in sites) foreach (var c in s.Ponds) if (!c.PondName.Contains('/')) c.PondName = $"{s.Farm.Id}/{c.PondName}";
        string lang = "en";
        var tools = Tools(sites, () => lang);
        var agent = new AquaAgent(sites.SelectMany(s => s.Ponds), model, memory, tools)
        {
            PromptAddendum = $"You are also the copilot of a portfolio of {sites.Count} farms ({string.Join(", ", sites.Select(s => $"{s.Farm.Id} {s.Farm.Name}"))}). " +
                             "Use portfolio_overview, compare_farms and farm_alerts for questions about several farms.",
        };
        agent.LanguageChanged += l => lang = l;
        return agent;
    }

    public static IReadOnlyList<(FarmSite Site, FarmSummary Summary)> Summaries(IReadOnlyList<FarmSite> sites) =>
        sites.Select(s => (s, KpiCalculator.Summarize(s.Farm, s.AsOf, s.DefaultFeedPricePerKg))).ToList();

    /// <summary>A KPI of a farm: fcr, profit (per kg harvested), survival (%), productivity (t/ha of harvested cycles).</summary>
    public static double Metric(FarmSummary f, string metric) => metric switch
    {
        "fcr" => f.MeanFcr,
        "profit" => f.HarvestedKg > 0 ? f.Profit / f.HarvestedKg : double.NaN,
        "survival" => f.Cycles.Where(c => !double.IsNaN(c.Sr)).Select(c => c.Sr).DefaultIfEmpty(double.NaN).Average(),
        "productivity" => f.Cycles.Where(c => c.HarvestedKg > 0).Select(c => c.ProductivityTonHa).DefaultIfEmpty(double.NaN).Average(),
        _ => throw new ArgumentException($"Unknown metric '{metric}' (fcr, profit, survival, productivity)."),
    };

    /// <summary>Ponds across farms whose water needs attention, worst first.</summary>
    public static IReadOnlyList<(string Pond, double Wqi, double Do, string Issue)> Alerts(IReadOnlyList<FarmSite> sites, double wqiThreshold = 60) =>
        sites.SelectMany(s => s.Ponds.Select(c => (c, a: WaterQualityIndex.Assess(c.Water, c.Species))))
            .Where(x => x.a.Wqi < wqiThreshold || (x.c.Water.DissolvedOxygen ?? 99) < x.c.Species.DoCritical + 1)
            .OrderBy(x => x.a.Wqi)
            .Select(x => (x.c.PondName, Math.Round(x.a.Wqi, 1), x.c.Water.DissolvedOxygen ?? double.NaN, x.a.Issues.FirstOrDefault().En ?? ""))
            .ToList();

    private static IReadOnlyList<AgentTool> Tools(IReadOnlyList<FarmSite> sites, Func<string> lang)
    {
        string T(string en, string id) => lang() == "id" ? id : en;
        var inv = CultureInfo.InvariantCulture;
        const string metricSchema = """{"type":"object","properties":{"metric":{"type":"string","enum":["fcr","profit","survival","productivity"]}},"required":["metric"]}""";
        return
        [
            new("portfolio_overview", "Every farm of the portfolio: ponds, active cycles, standing biomass, harvest, revenue, profit, FCR and the worst pond.",
                """{"type":"object","properties":{}}""", _ =>
                {
                    var sb = new StringBuilder(T("Portfolio:\n", "Portofolio:\n"));
                    foreach (var (s, f) in Summaries(sites).OrderByDescending(x => x.Summary.Profit))
                    {
                        var worst = s.Ponds.Select(c => (c.PondName, Wqi: WaterQualityIndex.Assess(c.Water, c.Species).Wqi)).OrderBy(x => x.Wqi).FirstOrDefault();
                        sb.Append(inv, $"- {s.Farm.Id} {s.Farm.Name}: {f.Ponds} {T("ponds", "kolam")}, {f.ActiveCycles} {T("active cycles", "siklus aktif")}, " +
                                       $"{T("biomass", "biomassa")} {f.StandingBiomassKg:N0} kg, {T("harvested", "dipanen")} {f.HarvestedKg:N0} kg, " +
                                       $"{T("profit", "laba")} Rp {f.Profit:N0}, FCR {f.MeanFcr:0.00}");
                        if (worst.PondName is not null) sb.Append(inv, $", {T("worst water", "air terburuk")} {worst.PondName} (WQI {worst.Wqi:0})");
                        sb.Append('\n');
                    }
                    return sb.ToString().TrimEnd();
                }),
            new("compare_farms", "Rank the farms by one KPI: fcr (lower is better), profit per kg, survival %, productivity t/ha.", metricSchema, a =>
            {
                string metric = a["metric"]?.GetValue<string>() ?? "fcr";
                bool lowerBetter = metric == "fcr";
                var rows = Summaries(sites).Select(x => (x.Site.Farm.Id, V: Metric(x.Summary, metric))).Where(x => !double.IsNaN(x.V))
                    .OrderBy(x => lowerBetter ? x.V : -x.V).ToList();
                if (rows.Count == 0) return T("No data for that metric yet.", "Belum ada data untuk metrik itu.");
                var sb = new StringBuilder(T($"Ranking by {metric}:\n", $"Peringkat menurut {metric}:\n"));
                foreach (var (id, v) in rows) sb.Append(inv, $"- {id}: {v:0.###}\n");
                sb.Append(inv, $"{T("Gap best–worst", "Selisih terbaik–terburuk")}: {Math.Abs(rows[0].V - rows[^1].V):0.###}");
                return sb.ToString();
            }),
            new("farm_alerts", "Ponds across all farms whose water quality or dissolved oxygen needs attention now, worst first.", """{"type":"object","properties":{}}""", _ =>
            {
                var alerts = Alerts(sites);
                return alerts.Count == 0 ? T("No pond needs attention.", "Tidak ada kolam yang perlu perhatian.")
                    : T("Ponds needing attention:\n", "Kolam yang perlu perhatian:\n") + string.Join("\n", alerts.Select(x =>
                        string.Create(inv, $"- {x.Pond}: WQI {x.Wqi:0}, DO {x.Do:0.0} mg/L{(x.Issue.Length > 0 ? " — " + x.Issue : "")}")));
            }),
        ];
    }
}
