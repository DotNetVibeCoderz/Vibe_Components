using System.Globalization;
using System.Text;
using AquaCulture.AI.Agent;
using AquaCulture.Analytics;
using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Population;
using AquaCulture.Reporting;
using AquaCulture.StockAssessment;
using AquaCulture.Visualization;
using AquaCulture.WaterQuality;

namespace AquaCulture.Gallery.Shared;

// ───────────────────────────── Stock assessment ─────────────────────────────
public sealed record StockDemoResult(DynamicSchaeferFit Fit, EquilibriumFit Schaefer, EquilibriumFit Fox, YprResult Ypr, double PaulyM, double Z, double E,
    IReadOnlyList<CatchEffort> Data, string CpueSvg, string KobeSvg, string YprSvg);

public static class StockDemo
{
    /// <summary>Synthetic small-pelagic fishery (Sardinella lemuru-like) with rising effort.</summary>
    public static IReadOnlyList<CatchEffort> Fishery(double effortGrowth = 0.07, double r = 0.55, double k = 60_000, ulong seed = 5)
    {
        var rng = new DeterministicRandom(seed);
        double b = k * 0.95, q = 0.00011;
        var list = new List<CatchEffort>();
        for (int y = 0; y < 24; y++)
        {
            double effort = 600 * Math.Exp(effortGrowth * y) * (y > 17 ? 0.85 : 1);
            double c = Math.Min(b * 0.8, q * effort * b);
            list.Add(new CatchEffort(2002 + y, Math.Round(c * rng.LogNormal(1, 0.05)), Math.Round(effort)));
            b = Math.Max(100, b + r * b * (1 - b / k) - c);
        }
        return list;
    }

    public static StockDemoResult Run(double effortGrowth = 0.07, double tc = 0.4, double m = 0.9)
    {
        var data = Fishery(effortGrowth);
        var fit = SurplusProduction.FitDynamic(data, 0.95);
        var sch = SurplusProduction.Schaefer(data); var fox = SurplusProduction.Fox(data);
        var ypr = YieldPerRecruit.Analyze(new YprParameters(120, 1.1, -0.1, m, 0.3, tc), 4);
        double pauly = LengthBased.PaulyM(23, 1.1, 27.5);
        // synthetic length frequency for the catch curve (Z = 2.4)
        var lfq = new List<(double, double)>();
        for (double l = 6; l <= 22; l += 1)
        {
            double Age(double x) => -0.1 - Math.Log(1 - x / 23) / 1.1;
            double n = 1e5 * (Math.Exp(-2.4 * Age(l - 0.5)) - Math.Exp(-2.4 * Age(l + 0.5)));
            lfq.Add((l, l < 10 ? n * (l - 5) / 5 : n));
        }
        var cc = LengthBased.CatchCurve(lfq, 23, 1.1, -0.1);
        var (_, e) = LengthBased.Exploitation(cc.Z, pauly);
        var cpue = SvgCharts.Line(
            [
                new Series("CPUE observed", data.Select(d => ((double)d.Year, d.Cpue)).ToList(), "#5b7279"),
                new Series("CPUE model", data.Select((d, i) => ((double)d.Year, fit.PredictedCpue[i])).ToList(), "#6c5ca8"),
            ], new ChartOptions { Height = 230, YFormat = "0.00", XFormat = x => $"{x:0}" });
        // Kobe plot
        var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 340 300\" font-family=\"inherit\">");
        double X(double v) => 40 + Math.Clamp(v, 0, 2.5) / 2.5 * 280;
        double Y(double v) => 270 - Math.Clamp(v, 0, 2.5) / 2.5 * 250;
        sb.Append($"<rect x=\"{X(1)}\" y=\"{Y(2.5)}\" width=\"{X(2.5) - X(1)}\" height=\"{Y(1) - Y(2.5)}\" fill=\"#e9b44c\" opacity=\".55\"/>");
        sb.Append($"<rect x=\"{X(0)}\" y=\"{Y(2.5)}\" width=\"{X(1) - X(0)}\" height=\"{Y(1) - Y(2.5)}\" fill=\"#e0614a\" opacity=\".55\"/>");
        sb.Append($"<rect x=\"{X(1)}\" y=\"{Y(1)}\" width=\"{X(2.5) - X(1)}\" height=\"{Y(0) - Y(1)}\" fill=\"#4fb389\" opacity=\".55\"/>");
        sb.Append($"<rect x=\"{X(0)}\" y=\"{Y(1)}\" width=\"{X(1) - X(0)}\" height=\"{Y(0) - Y(1)}\" fill=\"#e9d38a\" opacity=\".55\"/>");
        var traj = data.Select((d, i) => (B: fit.Biomass[i] / fit.Bmsy, F: d.Catch / fit.Biomass[i] / fit.Fmsy)).ToList();
        sb.Append("<path d=\"").Append(string.Join(" ", traj.Select((p, i) => string.Create(CultureInfo.InvariantCulture, $"{(i == 0 ? "M" : "L")}{X(p.B):0.#},{Y(p.F):0.#}")))).Append("\" fill=\"none\" stroke=\"#0b2e36\" stroke-width=\"1.6\"/>");
        foreach (var (p, i) in traj.Select((p, i) => (p, i)))
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{X(p.B):0.#}\" cy=\"{Y(p.F):0.#}\" r=\"{(i == traj.Count - 1 ? 6 : 2.5)}\" fill=\"{(i == traj.Count - 1 ? "#0b2e36" : "#ffffff")}\" stroke=\"#0b2e36\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{X(traj[0].B) + 6:0}\" y=\"{Y(traj[0].F) - 6:0}\" font-size=\"10\" fill=\"#0b2e36\">{data[0].Year}</text><text x=\"{X(traj[^1].B) + 8:0}\" y=\"{Y(traj[^1].F) + 4:0}\" font-size=\"11\" font-weight=\"700\" fill=\"#0b2e36\">{data[^1].Year}</text>");
        sb.Append($"<line x1=\"40\" y1=\"270\" x2=\"320\" y2=\"270\" stroke=\"#5b7279\"/><line x1=\"40\" y1=\"20\" x2=\"40\" y2=\"270\" stroke=\"#5b7279\"/><text x=\"180\" y=\"292\" text-anchor=\"middle\" font-size=\"11\" fill=\"#5b7279\">B / Bmsy</text><text x=\"14\" y=\"145\" transform=\"rotate(-90 14 145)\" text-anchor=\"middle\" font-size=\"11\" fill=\"#5b7279\">F / Fmsy</text></svg>");
        var ys = SvgCharts.Line([new Series("Y/R g", ypr.Curve.Select(p => (p.F, p.YieldPerRecruit)).ToList(), "#6c5ca8", Area: true)],
            new ChartOptions { Width = 1100, Height = 260, YFormat = "0.#", XFormat = x => $"F {x:0.#}",
                References = [new Reference(ypr.YprAtFmax, $"Fmax {ypr.Fmax:0.00}", "#6c5ca8"), new Reference(ypr.YprAtF01, $"F0.1 {ypr.F01:0.00}", "#2f8f6a")] });
        return new StockDemoResult(fit, sch, fox, ypr, pauly, cc.Z, e, data, cpue, sb.ToString(), ys);
    }
}

// ───────────────────────────── Population ─────────────────────────────
public sealed record PopulationDemoResult(IReadOnlyList<PopulationState> NoFishing, IReadOnlyList<PopulationState> ConstantF, IReadOnlyList<PopulationState> HockeyStick,
    double Lambda, string BiomassSvg, string CatchSvg, string PredatorSvg);

public static class PopulationDemo
{
    public static PopulationDemoResult Run(double f = 0.6, bool predator = true, double recruitmentCv = 0.3, double migration = 0.02)
    {
        var p = new AgeStructuredParameters
        {
            WeightAtAge = [0.02, 0.15, 0.4, 0.75, 1.1, 1.4, 1.6], MaturityAtAge = [0, 0.1, 0.6, 1, 1, 1, 1], SelectivityAtAge = [0, 0.3, 0.8, 1, 1, 1, 1],
            M = 0.35, Recruitment = RecruitmentModel.FromSteepness(0.75, 2000, 1.6), RecruitmentCv = recruitmentCv, EmigrationRate = migration, Immigration = 40,
            SpawningSeason = [1.0], Predator = predator ? new PredatorParameters(0.0004, 0.002, 0.05, 0.2, 60) : null,
        };
        var model = new AgeStructuredModel(p);
        var init = model.Equilibrium(2000);
        var none = model.Run(init, 40, new HarvestPolicy.None(), 7);
        var cf = model.Run(init, 40, new HarvestPolicy.ConstantF(f), 7);
        double b0 = none.Skip(10).Average(x => x.SpawningBiomass);
        var hs = model.Run(init, 40, new HarvestPolicy.HockeyStick(f, 0.5 * b0, 0.2 * b0), 7);
        var surv = Enumerable.Repeat(Math.Exp(-p.M), 6).ToArray();
        var fec = p.WeightAtAge.Select((w, i) => p.MaturityAtAge[i] * w * 1.1).ToArray();
        var (lambda, _) = new LeslieMatrix(fec, surv).Dominant();
        var bSvg = SvgCharts.Line(
            [
                new Series("No fishing", none.Select(x => ((double)x.Step, x.SpawningBiomass)).ToList(), "#3e8e5e"),
                new Series($"F = {f}", cf.Select(x => ((double)x.Step, x.SpawningBiomass)).ToList(), "#e0614a"),
                new Series("Hockey-stick HCR", hs.Select(x => ((double)x.Step, x.SpawningBiomass)).ToList(), "#6c5ca8"),
            ], new ChartOptions { Height = 240, YFormat = "N0", XFormat = x => $"y{x:0}", YMin = 0 });
        var cSvg = SvgCharts.Line(
            [new Series($"F = {f}", cf.Select(x => ((double)x.Step, x.Catch)).ToList(), "#e0614a"), new Series("Hockey-stick", hs.Select(x => ((double)x.Step, x.Catch)).ToList(), "#6c5ca8")],
            new ChartOptions { Height = 200, YFormat = "N0", XFormat = x => $"y{x:0}", YMin = 0 });
        var pp = PredatorPrey.Simulate(40, 9, 1.0, 100, 0.1, 0.1, 0.5, 0.3, 120, 0.1);
        var pSvg = SvgCharts.Line([new Series("Prey (tilapia)", pp.Select(x => (x.T, x.Prey)).ToList(), "#12a4b6"), new Series("Predator (snakehead)", pp.Select(x => (x.T, x.Predator)).ToList(), "#e0614a")],
            new ChartOptions { Width = 1100, Height = 240, XFormat = x => $"t{x:0}" });
        return new PopulationDemoResult(none, cf, hs, lambda, bSvg, cSvg, pSvg);
    }
}

// ───────────────────────────── Analytics ─────────────────────────────
public sealed record AnalyticsDemoResult(FarmSummary Summary, CycleKpis Selected, string CostSvg, string FcrSvg, string SrSvg, string PondId);

public static class AnalyticsDemo
{
    public static AnalyticsDemoResult Run(string pondId = "K-04", double price = 72_000)
    {
        var farm = DemoFarm.Farm;
        var summary = KpiCalculator.Summarize(farm, DemoFarm.Today, 17_500);
        var c = farm.ActiveCycle(pondId) ?? farm.Cycles[0];
        var water = Enumerable.Range(0, 20).Select(i => WaterQualityIndex.Assess(new WaterSample
        { TemperatureC = 29.5 + Math.Sin(i), DissolvedOxygen = 4.4 + 1.2 * Math.Cos(i * 0.7), Ph = 8 + 0.2 * Math.Sin(i * 0.4), Ammonia = 0.6 + 0.3 * Math.Sin(i * 0.3), SalinityPpt = 18 }, c.Species));
        var k = KpiCalculator.Calculate(c, farm.Pond(c.PondId), DemoFarm.Today, price, 17_500, water);
        var cost = SvgCharts.Bars(k.Costs.Items.Select(i => (i.Name.En, Math.Round(i.Amount / 1e6, 1))).ToList(), new ChartOptions { Height = 220, YFormat = "0.0", Title = "Rp million / juta" },
            ["#2f6fb0", "#12a4b6", "#d9a13b", "#6c5ca8", "#3e8e5e", "#8a6a4a"]);
        var fcr = SvgCharts.Bars(summary.Cycles.Select(x => (farm.Cycles.First(cc => cc.Id == x.CycleId).PondId, x.Fcr)).ToList(), new ChartOptions { Height = 200, YFormat = "0.00", YMin = 0, Title = "FCR" }, ["#12a4b6"]);
        var sr = SvgCharts.Bars(summary.Cycles.Select(x => (farm.Cycles.First(cc => cc.Id == x.CycleId).PondId, x.Sr)).ToList(), new ChartOptions { Height = 200, YFormat = "0", YMin = 0, YMax = 100, Title = "SR %" }, ["#3e8e5e"]);
        return new AnalyticsDemoResult(summary, k, cost, fcr, sr, c.PondId);
    }
}

// ───────────────────────────── Reporting ─────────────────────────────
public sealed record ExportFile(string Name, string Mime, byte[] Bytes);

public sealed record ReportingDemoResult(ComplianceResult Compliance, IReadOnlyList<ChecklistItem> Checklist, ReportDocument Report, IReadOnlyList<ExportFile> Files);

public static class ReportingDemo
{
    /// <summary>Default self-assessment answers for the demo farm.</summary>
    public static Dictionary<string, ComplianceAnswer> DefaultAnswers(ComplianceStandard s)
    {
        var items = ComplianceAudit.Checklist(s);
        var d = items.ToDictionary(i => i.Id, _ => ComplianceAnswer.Yes);
        if (items.Count > 3) d[items[3].Id] = ComplianceAnswer.Partial;
        if (items.Count > 8) d[items[8].Id] = ComplianceAnswer.No;
        if (items.Count > 10) d[items[10].Id] = ComplianceAnswer.Partial;
        return d;
    }

    public static ReportingDemoResult Run(ComplianceStandard standard, IReadOnlyDictionary<string, ComplianceAnswer>? answers = null, string lang = "en")
    {
        answers ??= DefaultAnswers(standard);
        var result = ComplianceAudit.Evaluate(standard, answers);
        var a = AnalyticsDemo.Run("K-01");
        var g = GrowthDemo.Run();
        var chart = PngEncoder.Raster(g.Trajectory.Days.Select(d => (float)d.AbwG).ToArray(), g.Trajectory.Days.Count, 1, ColorRamp.Score.WithRange(0, 25), 4);
        var doc = Reports.Cycle(a.Selected, "K-01", lang, chart, Enumerable.Range(0, 8).Select(i => (DemoFarm.Now.AddHours(-3 * i), 4.6 + 0.3 * Math.Sin(i), 29.8 + 0.4 * Math.Cos(i), 8.1, 0.7)));
        var comp = Reports.Compliance(result, DemoFarm.Name, lang);
        doc.Blocks.AddRange(comp.Blocks);
        doc.Subtitle = $"{DemoFarm.Name} · {DemoFarm.Place} · {doc.Subtitle}";
        string stem = $"aquaculture-{a.PondId.ToLowerInvariant()}";
        var files = new List<ExportFile>
        {
            new($"{stem}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ReportWriters.Xlsx(doc)),
            new($"{stem}.pdf", "application/pdf", ReportWriters.Pdf(doc)),
            new($"{stem}.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ReportWriters.Docx(doc)),
            new($"{stem}-costs.csv", "text/csv", Encoding.UTF8.GetBytes(ReportWriters.Csv(doc))),
            new($"{stem}.json", "application/json", Encoding.UTF8.GetBytes(ReportWriters.Json(doc))),
            new($"{stem}.html", "text/html", Encoding.UTF8.GetBytes(ReportWriters.Html(doc))),
            new($"{stem}.md", "text/markdown", Encoding.UTF8.GetBytes(ReportWriters.Markdown(doc))),
        };
        return new ReportingDemoResult(result, ComplianceAudit.Checklist(standard), doc, files);
    }
}

// ───────────────────────────── Agent ─────────────────────────────
public static class AgentDemo
{
    public static readonly (string En, string Id)[] Suggestions =
    [
        ("How much should I feed pond K-01 today?", "Berapa pakan kolam K-01 hari ini?"),
        ("Is dissolved oxygen safe tonight? When should the aerators start?", "Apakah oksigen aman malam ini? Kapan kincir harus dinyalakan?"),
        ("Check the water quality and tell me what to do.", "Periksa kualitas air dan apa yang harus saya lakukan?"),
        ("When can I harvest at 20 g and what is the margin?", "Kapan bisa panen di 20 g dan berapa untungnya?"),
        ("What is the disease risk this week?", "Apa risiko penyakit minggu ini?"),
        ("Will the weather affect my pond in the next 3 days?", "Apakah cuaca 3 hari ke depan berpengaruh ke kolam?"),
    ];

    public static AquaContext Context()
    {
        var farm = DemoFarm.Farm; var pond = farm.Ponds[0]; var c = farm.ActiveCycle(pond.Id)!;
        var recent = new TimeSeries("DO", "mg/L");
        for (int i = 0; i < 6; i++) recent.Add(DemoFarm.Now.AddMinutes(-30 * (5 - i)), 5.6 - 0.28 * i);
        return new AquaContext
        {
            Species = c.Species, PondName = $"{pond.Name} ({pond.Id})", AreaM2 = pond.AreaM2, DepthM = pond.DepthM, System = pond.System, AerationKw = pond.AerationKw,
            StockingDate = c.StockingDate, Today = DemoFarm.Today, Population = c.EstimatedPopulation, AbwG = c.LatestAbwG, FeedPricePerKg = 17_500, SalePricePerKg = 72_000,
            Water = new WaterSample { TemperatureC = 30.2, DissolvedOxygen = 4.2, Ph = 8.3, SalinityPpt = 18, Ammonia = 1.1, Nitrite = 0.5, Alkalinity = 110, TurbidityNtu = 45, OrpMv = 190 },
            RecentDo = recent, Weather = DemoFarm.Weather(3),
        };
    }

    /// <summary>The agent with the configured LLM (environment) or the offline model.</summary>
    public static AquaAgent Create(IChatModel? model = null) => new(Context(), model ?? (LlmOptions.FromEnvironment() is { } o ? o.CreateModel() : new RuleBasedChatModel()));
}
