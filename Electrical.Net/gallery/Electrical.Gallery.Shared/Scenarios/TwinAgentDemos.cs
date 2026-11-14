using System.Globalization;
using Electrical.AI;
using Electrical.Core.Network;
using Electrical.DigitalTwin;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>Live digital twin fed by a simulated RTU (state estimation every step, explained alarms, asset health) plus a what-if comparison.</summary>
public sealed class DigitalTwinDemo : IInteractiveScenario
{
    public string Id => "digitaltwin";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("case", new("Grid", "Jaringan"), [("case33bw", Same("Baran–Wu 33-bus")), ("case14", Same("IEEE 14-bus"))], "case33bw"),
        new RangeParam("peak", new("Peak load", "Beban puncak"), 0.8, 1.5, 0.05, 1.2, "0.00", "×"),
        new ChoiceParam("bad", new("Bad telemetry at 10:00", "Telemetri buruk pukul 10:00"), [("on", new("Inject", "Suntikkan")), ("off", new("None", "Tidak ada"))], "on"),
        new ChoiceParam("whatif", new("What-if", "Bagaimana-jika"),
            [("open", new("Open busiest line", "Buka saluran tersibuk")), ("load", new("+20 % load", "+20 % beban")), ("pv", new("Add 30 % PV", "Tambah PV 30 %"))], "open"),
    ];

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var net = TestCases.ByName(v["case"]);
        string badTag = v["case"] == "case14" ? "BUS14.VM" : "BUS18.VM";
        var options = new TwinSimulatorOptions
        {
            Steps = 96, StepSize = TimeSpan.FromMinutes(15), PeakLoadScale = v.D("peak"),
            BadData = v["bad"] == "on" ? [new TwinBadDataInjection(40, badTag, 0.82)] : [],
        };
        var r = new TwinSimulator(net, options).Run();
        var start = options.Start;

        var est = r.States.Select(s => s.Converged ? s.Kpis.MinVoltagePu : double.NaN).ToList();
        var truth = r.Truth.TakeLast(r.States.Count).Select(t => t.Converged ? t.MinVoltageBus.Vm : double.NaN).ToList();
        string vSvg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "true Vmin", "Vmin sebenarnya"), truth, SvgTheme.Var("eln-muted")) { StrokeWidth = 4 },
            new ChartSeries(Pick(lang, "estimated Vmin", "Vmin estimasi"), est, SvgTheme.Var("eln-accent")),
        ], new ChartOptions { Width = 680, Height = 250, YLabel = "|V| (pu)", TimeStart = start, TimeStep = options.StepSize, ReferenceLines = [(0.95, "0.95")] });
        string loadSvg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "estimated load", "beban estimasi"), r.States.Select(s => s.EstimatedLoadMw).ToList(), SvgTheme.Var("eln-amber")) { Area = true },
        ], new ChartOptions { Width = 680, Height = 200, YLabel = "MW", ShowLegend = false, TimeStart = start, TimeStep = options.StepSize });

        var alarms = r.RaisedAlarms.Take(10).Select(a => new Cell[]
        {
            Cell.Chip(a.Severity.ToString(), a.Severity >= TwinAlarmSeverity.High ? "alarm" : a.Severity == TwinAlarmSeverity.Warning ? "warn" : "ok"),
            a.Element, a.Localized(lang),
        }).ToList();
        var health = r.AssetHealth.OrderByDescending(h => h.LoadingPercent).Take(6).Select(h => new Cell[]
        {
            h.Name, Cell.Num(h.LoadingPercent, "0.0"), Cell.Num(h.HotSpotC, "0.0"), Cell.Num(h.AgingFactor, "0.000"),
            new(F(h.HealthIndex, "0.00"), h.HealthIndex < 0.6 ? "alarm" : h.HealthIndex < 0.85 ? "warn" : "ok"),
        }).ToList();

        // What-if on the base network.
        var basePf = Electrical.PowerFlow.PowerFlowSolver.Solve(net);
        WhatIfAction action = v["whatif"] switch
        {
            "load" => new ScaleLoad(1.2),
            "pv" => new AddPv(net.Loads.MaxBy(l => l.P)!.Bus, Math.Round(0.3 * net.TotalLoadMw, 2)),
            // Busiest line whose outage keeps the grid connected; a radial feeder has none, so a +20 % load step is used instead.
            _ => basePf.Branches.Where(b => Topology.Islands(WithOut(net, b.Index)).Count == 1).MaxBy(b => b.SFrom) is { } busiest
                ? new OpenBranch(busiest.Index) : new ScaleLoad(1.2),
        };
        var w = WhatIfSimulator.Run(net, [action]);
        var wRows = new List<Cell[]>
        {
            new Cell[] { Pick(lang, "Losses MW", "Rugi MW"), Cell.Num(w.Baseline.LossesMw, "0.000"), Cell.Num(w.Scenario.LossesMw, "0.000"), Cell.Num(w.DeltaLossesMw, "+0.000;-0.000") },
            new Cell[] { "Vmin pu", Cell.Num(w.Baseline.MinVoltagePu, "0.0000"), Cell.Num(w.Scenario.MinVoltagePu, "0.0000"), Cell.Num(w.DeltaMinVoltagePu, "+0.0000;-0.0000") },
            new Cell[] { Pick(lang, "Max loading %", "Pembebanan maks %"), Cell.Num(w.Baseline.MaxLoadingPercent, "0.0"), Cell.Num(w.Scenario.MaxLoadingPercent, "0.0"), Cell.Num(w.DeltaMaxLoadingPercent, "+0.0;-0.0") },
            new Cell[] { Pick(lang, "Violations", "Pelanggaran"), Cell.Num(w.Baseline.VoltageViolations + w.Baseline.Overloads, "0"), Cell.Num(w.Scenario.VoltageViolations + w.Scenario.Overloads, "0"), "" },
        };

        int removed = r.States.Sum(s => s.RemovedMeasurements.Count);
        var kpis = new List<Kpi>
        {
            new(new("Twin steps", "Langkah twin"), r.States.Count.ToString(CultureInfo.InvariantCulture), null, Same($"{r.States.Count(s => s.Converged)} {Pick(lang, "estimated", "terestimasi")} · 15 min")),
            new(new("Mean |V| error", "Galat |V| rata-rata"), F(r.MeanVoltageErrorPu * 1000, "0.00"), "mpu", Same($"max {F(r.MaxVoltageErrorPu * 1000, "0.0")} mpu"), r.MaxVoltageErrorPu < 0.01 ? "ok" : "warn"),
            new(new("Bad data removed", "Data buruk dihapus"), removed.ToString(CultureInfo.InvariantCulture), null, new("LNR test inside the twin", "uji LNR di dalam twin"), removed > 0 ? "warn" : "ok"),
            new(new("Alarms raised", "Alarm muncul"), r.RaisedAlarms.Count.ToString(CultureInfo.InvariantCulture), null, null, r.RaisedAlarms.Any(a => a.Severity >= TwinAlarmSeverity.High) ? "alarm" : r.RaisedAlarms.Count > 0 ? "warn" : "ok"),
            new(new("What-if", "Bagaimana-jika"), lang == "id" ? w.DescriptionId : w.Description, null, Same($"ΔVmin {F(w.DeltaMinVoltagePu, "+0.000;-0.000")} pu")),
        };
        var blocks = new List<Block>
        {
            new ChartBlock(new("Estimated vs true minimum voltage", "Tegangan minimum estimasi vs sebenarnya"), vSvg, new("WLS on simulated RTU scans", "WLS dari pindaian RTU simulasi"), Wide: true),
            new TableBlock(new("Explained alarms", "Alarm beserta penjelasan"), [new("Severity", "Tingkat"), new("Element", "Elemen"), new("Explanation", "Penjelasan")], alarms),
            new TableBlock(new("Asset health", "Kesehatan aset"), [new("Asset", "Aset"), new("Loading %", "Beban %"), new("Hot spot °C", "Titik panas °C"), new("Aging", "Penuaan"), new("Health", "Kesehatan")], health,
                new("IEC 60076-7 thermal model", "model termal IEC 60076-7")),
            new TableBlock(new("What-if comparison", "Perbandingan bagaimana-jika"), [new("Quantity", "Besaran"), new("Now", "Kini"), new("What-if", "Bagaimana-jika"), Same("Δ")], wRows,
                Same(lang == "id" ? w.DescriptionId : w.Description)),
            new ChartBlock(new("Estimated load", "Beban terestimasi"), loadSvg),
            new NoteBlock(new(w.Summary, w.SummaryId)),
        };
        return new InteractiveResult(kpis, blocks);
    }

    private static PowerNetwork WithOut(PowerNetwork net, int k)
    {
        var c = net.Clone();
        c.Branches[k].InService = false;
        return c;
    }
}

/// <summary>Grid-operator agent (Electrical.AI): offline rule-based model by default, an LLM when ELECTRICAL_LLM_* variables are set.</summary>
public static class AgentScenario
{
    public static readonly L[] SuggestedPrompts =
    [
        new("Run a power flow on case14 at 120% load", "Jalankan aliran daya case14 pada beban 120%"),
        new("What is the short-circuit current at bus 4?", "Berapa arus hubung singkat di bus 4?"),
        new("Explain current alarms", "Jelaskan alarm saat ini"),
        new("Which N-1 outage is the most severe on case9?", "Gangguan N-1 mana yang paling parah di case9?"),
        new("Forecast tomorrow's load", "Ramalkan beban besok"),
        new("Give me a grid overview", "Berikan ringkasan jaringan"),
    ];

    /// <summary>Creates an agent on IEEE 14-bus (the model is chosen by <see cref="GridAgent.CreateDefaultModel"/>).</summary>
    public static GridAgent CreateAgent() => new(new GridAgentContext { CaseName = "case14" });

    /// <summary>True when an LLM endpoint is configured through ELECTRICAL_LLM_* environment variables.</summary>
    public static bool LlmConfigured => LlmOptions.FromEnvironment() is not null;

    public static async Task<AgentAnswer> AskAsync(GridAgent agent, string question, CancellationToken ct = default)
    {
        try { return await agent.AskAsync(question, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new AgentAnswer($"The model call failed ({e.Message}). Falling back to the offline model.\n\n" +
                (await new GridAgent(agent.Context, new RuleBasedChatModel()).AskAsync(question, ct).ConfigureAwait(false)).Text, [], "fallback", "en");
        }
    }
}
