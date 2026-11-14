using System.Globalization;
using Electrical.Core.Network;
using Electrical.Markets;
using Electrical.Optimization;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

// ----------------------------------------------------------------------------------------------------------------------------
// v1.2 demos: AC optimal power flow, security-constrained unit commitment and electricity market clearing. All of them use the
// built-in solvers only (interior point, dual simplex, branch and bound) so the gallery carries no native solver dependency.
// ----------------------------------------------------------------------------------------------------------------------------

internal static class MarketFmt
{
    public static string Id(int bus) => bus.ToString(CultureInfo.InvariantCulture);
    public static string Money(double v) => v.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
    public static string Gen(Generator g, int k) => g.Name ?? $"G{k + 1}";
    public static int BranchIndex(PowerNetwork net, int a, int b) => net.Branches.FindIndex(x => (x.From == a && x.To == b) || (x.From == b && x.To == a));
    public static string Branch(PowerNetwork net, int l) => l < 0 ? "—" : $"{net.Branches[l].From}–{net.Branches[l].To}";
}

/// <summary>AC-OPF (primal-dual interior point) on case9 / case14 with a tightened line, compared with the DC-OPF; LMP decomposition.</summary>
public sealed class AcOpfDemo : IInteractiveScenario
{
    public string Id => "acopf";

    /// <summary>Line whose rating the slider controls: case9 5–6 (rated 150 MVA), case14 1–2 (unrated in MATPOWER).</summary>
    private static (int From, int To, string Key, double Min, double Max, double Default) Line(string c) =>
        c == "case14" ? (1, 2, "lim14", 60, 200, 120) : (5, 6, "lim9", 20, 150, 42);

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        string c = v.Get("case", "case9");
        var (a, b, key, min, max, def) = Line(c);
        return
        [
            new ChoiceParam("case", new("Test system", "Sistem uji"), [("case9", Same("WSCC 9-bus")), ("case14", Same("IEEE 14-bus"))], "case9"),
            new RangeParam(key, new($"Line {a}–{b} rating", $"Rating saluran {a}–{b}"), min, max, 1, def, "0", " MVA"),
        ];
    }

    public InteractiveResult Run(ParamValues v, string lang)
    {
        string c = v.Get("case", "case9");
        var (a, b, key, _, max, _) = Line(c);
        double limit = v.Has(key) ? v.D(key) : Line(c).Default;
        var net = TestCases.ByName(c);
        int line = MarketFmt.BranchIndex(net, a, b);
        bool unlimited = limit >= max - 1e-9 && net.Branches[line].RateA <= 0;
        var over = unlimited ? null : new Dictionary<int, double> { [line] = limit };

        var ac = AcOpf.Solve(net, new AcOpfOptions { RatingOverrides = over });
        var dc = DcOpf.Solve(net, new DcOpfOptions { RatingOverrides = over });
        var free = AcOpf.Solve(net);
        var d = ac.Decompose();
        var pf = ac.ToPowerFlowResult();

        var buses = ac.BusIds.Select(MarketFmt.Id).ToList();
        var gens = net.Generators.Select((g, k) => $"{MarketFmt.Gen(g, k)} @{g.Bus}").ToList();
        string dispatch = SvgCharts.Bar(gens,
        [
            new ChartSeries(Pick(lang, "AC-OPF P", "AC-OPF P"), ac.PgMw, SvgTheme.Var("eln-accent")),
            new ChartSeries(Pick(lang, "DC-OPF P", "DC-OPF P"), dc.DispatchMw, SvgTheme.Var("eln-muted")),
            new ChartSeries(Pick(lang, "AC-OPF Q (Mvar)", "AC-OPF Q (Mvar)"), ac.QgMvar, SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 640, Height = 260, YLabel = "MW / Mvar", ValueFormat = "0" });

        // the energy component is the same at every bus; stack the bus-specific adders around it so they stay readable
        string lmp = SvgCharts.Bar(buses,
        [
            new ChartSeries(Pick(lang, "loss", "rugi"), d.Loss, SvgTheme.Var("eln-series-3")),
            new ChartSeries(Pick(lang, "congestion", "kongesti"), d.Congestion, SvgTheme.Var("eln-danger")),
            new ChartSeries(Pick(lang, "voltage", "tegangan"), d.Voltage, SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 1080, Height = 300, XLabel = "bus", YLabel = Pick(lang, "LMP − energy ($/MWh)", "LMP − energi ($/MWh)"), ValueFormat = "0.00" }, stacked: true);

        string lmpCompare = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "AC LMP", "LMP AC"), ac.Lmp, SvgTheme.Var("eln-accent")),
            new ChartSeries(Pick(lang, "DC LMP", "LMP DC"), dc.Lmp, SvgTheme.Var("eln-muted")) { Dashed = true },
            new ChartSeries(Pick(lang, "AC LMP, no limit", "LMP AC, tanpa batas"), free.Lmp, SvgTheme.Var("eln-series-6")) { StrokeWidth = 1.2 },
        ], new ChartOptions { Width = 640, Height = 260, XLabels = buses, XLabel = "bus", YLabel = "$/MWh" });

        var hist = ac.Solver.History;
        string conv = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "feasibility", "kelayakan"), hist.Select(h => Math.Log10(Math.Max(1e-12, h.Feasibility))).ToList(), SvgTheme.Var("eln-accent")),
            new ChartSeries(Pick(lang, "gradient", "gradien"), hist.Select(h => Math.Log10(Math.Max(1e-12, h.Gradient))).ToList(), SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 640, Height = 240, XLabel = Pick(lang, "iteration", "iterasi"), YLabel = "log10", ReferenceLines = [(-6, "tol 1e-6")] });

        string sld = SldRenderer.Render(net, pf);

        var decRows = Enumerable.Range(0, d.Lmp.Length).Select(i => new Cell[]
        {
            buses[i], Cell.Num(ac.Vm[i], "0.0000"), Cell.Num(d.Lmp[i], "0.000"), Cell.Num(d.Energy[i], "0.000"), Cell.Num(d.Loss[i], "0.000"),
            Cell.Num(d.Congestion[i], "0.000"), Cell.Num(d.Voltage[i], "0.000"), Cell.Num(d.LossFactors[i], "0.0000"), Cell.Num(dc.Lmp[i], "0.000"),
        }).ToList();

        string Kind(string k) => k switch
        {
            "Flow" => Pick(lang, "line MVA", "MVA saluran"), "Vmax" => "|V| max", "Vmin" => "|V| min", _ => k,
        };
        string Unit(string k) => k switch { "Flow" => "$/MVAh", "Vmax" or "Vmin" => "$/h·pu", "Qmax" or "Qmin" => "$/Mvarh", _ => "$/MWh" };
        var bindRows = ac.Binding.Select(x => new Cell[] { Kind(x.Kind), x.Description, new($"{F(x.ShadowPrice, "0.000")} {Unit(x.Kind)}", null, true) }).ToList();
        foreach (var x in dc.Binding)
            bindRows.Add([Same("DC " + x.Kind)[lang], x.Description, new($"{F(x.ShadowPrice, "0.000")} $/MWh", null, true)]);
        if (bindRows.Count == 0) bindRows.Add([Same("—")[lang], Pick(lang, "no binding constraint", "tidak ada kendala terikat"), ""]);

        var br = ac.Branches[line];
        double s = Math.Max(br.SFrom, br.STo);
        double spread = ac.Lmp.Max() - ac.Lmp.Min();
        bool congested = ac.Binding.Any(x => x.Kind == "Flow");
        var kpis = new List<Kpi>
        {
            new(new("AC-OPF cost", "Biaya AC-OPF"), F(ac.TotalCost, "0.00"), "$/h",
                Same($"{(ac.Converged ? Pick(lang, "optimal", "optimal") : ac.Status.ToString())} · {ac.Iterations} it · {F(ac.Elapsed.TotalMilliseconds, "0")} ms"), ac.Converged ? "ok" : "alarm"),
            new(new("DC-OPF cost", "Biaya DC-OPF"), F(dc.TotalCost, "0.00"), "$/h", new($"AC − DC = {F(ac.TotalCost - dc.TotalCost, "0.00")} $/h", $"AC − DC = {F(ac.TotalCost - dc.TotalCost, "0.00")} $/h")),
            new(new("Losses", "Rugi-rugi"), F(ac.LossesMw, "0.000"), "MW", new("DC model: lossless", "model DC: tanpa rugi")),
            new(new($"Line {a}–{b}", $"Saluran {a}–{b}"), F(s, "0.0"), "MVA", Same(unlimited ? Pick(lang, "unlimited", "tanpa batas") : $"{Pick(lang, "limit", "batas")} {F(limit, "0")} MVA"),
                congested ? "alarm" : "ok"),
            new(new("LMP spread", "Selisih LMP"), F(spread, "0.00"), "$/MWh", Same($"{F(ac.Lmp.Min(), "0.00")} – {F(ac.Lmp.Max(), "0.00")}"), congested ? "warn" : null),
        };
        string matpower = c == "case14" ? "8081.53 $/h, LMP bus 1 36.724 $/MWh" : "5296.69 $/h, Pg 89.80 / 134.32 / 94.19 MW";
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("LMP decomposition", "Dekomposisi LMP"), lmp,
                new($"LMP = energy {F(d.Energy[0], "0.00")} $/MWh (bus {d.ReferenceBusId}) + loss + congestion + voltage · max residual {F(d.MaxResidual, "0.0e0")}",
                    $"LMP = energi {F(d.Energy[0], "0.00")} $/MWh (bus {d.ReferenceBusId}) + rugi + kongesti + tegangan · residu maks {F(d.MaxResidual, "0.0e0")}"), Wide: true),
            new ChartBlock(new("Generator dispatch", "Dispatch pembangkit"), dispatch, new("AC-OPF vs DC-OPF", "AC-OPF vs DC-OPF")),
            new ChartBlock(new("Nodal prices", "Harga nodal"), lmpCompare, new("AC vs DC, and AC without the line limit", "AC vs DC, dan AC tanpa batas saluran")),
            new TableBlock(new("Prices per bus ($/MWh)", "Harga per bus ($/MWh)"),
                [Same("Bus"), Same("|V| pu"), Same("LMP"), new("Energy", "Energi"), new("Loss", "Rugi"), new("Congestion", "Kongesti"), new("Voltage", "Tegangan"), new("Loss factor", "Faktor rugi"), Same("DC LMP")],
                decRows, Same($"λQ {F(ac.LmpQ.Min(), "0.000")} – {F(ac.LmpQ.Max(), "0.000")} $/Mvarh"), Wide: true),
            new TableBlock(new("Binding constraints", "Kendala terikat"), [new("Constraint", "Kendala"), new("Where", "Lokasi"), new("Shadow price", "Harga bayangan")], bindRows),
            new ChartBlock(new("Interior-point convergence", "Konvergensi titik interior"), conv, new("primal-dual, exact Hessians", "primal-dual, Hessian eksak")),
            new ChartBlock(new("OPF operating point", "Titik operasi OPF"), sld, new("AcOpfResult.ToPowerFlowResult() on the automatic single-line diagram", "AcOpfResult.ToPowerFlowResult() pada diagram segaris otomatis"), Wide: true),
            new NoteBlock(new($"Polar AC-OPF (MATPOWER runopf formulation): full AC power balance, |V| limits, generator P/Q limits and |S| ≤ rating at both branch ends, solved by a primal-dual interior point with exact Hessians. Without the line limit it reproduces MATPOWER: {matpower}. The DC-OPF limit is applied to MW flow; the AC limit to MVA. Losses and binding voltage limits are what the DC model cannot price.",
                $"AC-OPF polar (formulasi runopf MATPOWER): keseimbangan daya AC penuh, batas |V|, batas P/Q pembangkit, dan |S| ≤ rating di kedua ujung cabang, diselesaikan dengan titik interior primal-dual dengan Hessian eksak. Tanpa batas saluran hasilnya sama dengan MATPOWER: {matpower}. Batas DC-OPF berlaku pada aliran MW; batas AC pada MVA. Rugi-rugi dan batas tegangan yang terikat tidak dapat diberi harga oleh model DC.")),
        ]);
    }
}

/// <summary>24 h security-constrained unit commitment (MILP, built-in branch and bound) vs the priority-list dynamic program.</summary>
public sealed class ScucDemo : IInteractiveScenario
{
    public string Id => "scuc";

    /// <summary>Wood &amp; Wollenberg daily demand used by the unit-commitment validation tests [MW].</summary>
    internal static readonly double[] WwDemand =
        [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param>
        {
            new ChoiceParam("system", new("System", "Sistem"),
                [("ww", new("W&W 3 units, single bus", "W&W 3 unit, satu bus")), ("case9", new("WSCC 9-bus, 12 h", "WSCC 9 bus, 12 jam"))], "ww"),
            new RangeParam("reserve", new("Spinning reserve", "Cadangan berputar"), 0, 9, 1, 5, "0", " %"),
        };
        if (v.Get("system", "ww") == "case9")
            list.Add(new RangeParam("lim89", new("Line 8–9 rating", "Rating saluran 8–9"), 40, 150, 2.5, 57.5, "0.0", " MW"));
        return list;
    }

    /// <summary>case9 generators as commitment units: their quadratic costs plus start-up costs and minimum up/down times.</summary>
    private static List<UcUnit> Case9Units(PowerNetwork net)
    {
        double[] start = [900, 600, 450];
        int[] up = [6, 4, 2], down = [4, 3, 2], init = [24, 24, -8];
        return net.Generators.Select((g, k) => new UcUnit(MarketFmt.Gen(g, k), g.Cost, Math.Max(g.Pmin, 0.25 * g.Pmax), g.Pmax, start[k], up[k], down[k], init[k])).ToList();
    }

    /// <summary>First hour and length of the case9 horizon (12 daytime hours keep the network MILP small enough for an interactive demo).</summary>
    private const int Case9Start = 6, Case9Hours = 12;

    internal static double[] Case9Demand(PowerNetwork net)
    {
        double peak = net.TotalLoadMw;
        return [.. Enumerable.Range(Case9Start, Case9Hours).Select(h => Math.Round(peak * (0.6 + 0.4 * Math.Max(0, Math.Sin((h - 6) * Math.PI / 16))), 1))];
    }

    /// <summary>Exact quadratic cost of a commitment: hourly economic dispatch of the committed units plus start-up costs [$].</summary>
    private static double ExactCost(IReadOnlyList<UcUnit> units, bool[,] status, IReadOnlyList<double> demand)
    {
        double total = 0;
        for (int t = 0; t < demand.Count; t++)
        {
            var on = Enumerable.Range(0, units.Count).Where(i => status[i, t]).ToList();
            if (on.Count == 0) return double.NaN;
            total += EconomicDispatch.Solve(on.Select(i => new DispatchUnit(units[i].Name, units[i].Cost, units[i].PminMw, units[i].PmaxMw)).ToList(), demand[t]).TotalCost;
            foreach (int i in on)
                if (!(t == 0 ? units[i].InitialHours > 0 : status[i, t - 1])) total += units[i].StartupCost;
        }
        return total;
    }

    public InteractiveResult Run(ParamValues v, string lang)
    {
        bool grid = v.Get("system", "ww") == "case9";
        double reserve = v.D("reserve") / 100;
        PowerNetwork? net = grid ? TestCases.Case9() : null;
        var uc = grid ? Case9Units(net!) : UnitCommitment.WoodWollenbergThreeUnits();
        var demand = grid ? Case9Demand(net!) : WwDemand;
        var units = uc.Select((u, k) => ScucUnit.FromUcUnit(u, grid ? net!.Generators[k].Bus : 0)).ToList();
        int line = grid ? MarketFmt.BranchIndex(net!, 8, 9) : -1;
        double limit = grid && v.Has("lim89") ? v.D("lim89") : 57.5;
        var opt = new ScucOptions { ReserveFraction = reserve, CostSegments = 4, RatingOverrides = grid ? new Dictionary<int, double> { [line] = limit } : null };

        var r = grid
            ? SecurityConstrainedUnitCommitment.Solve(net, units, SecurityConstrainedUnitCommitment.DistributeDemand(net!, demand), opt)
            : SecurityConstrainedUnitCommitment.Solve(units, demand, opt);
        var dp = UnitCommitment.Solve(uc, demand, reserveFraction: reserve);
        // copper-plate SCUC (network ignored) isolates the cost of the line limit
        var plate = grid ? SecurityConstrainedUnitCommitment.Solve(net, units, SecurityConstrainedUnitCommitment.DistributeDemand(net!, demand), opt with { EnforceNetwork = false }) : r;
        bool ok = r.HasSolution;
        int G = units.Count, T = demand.Length;
        double exact = ok ? ExactCost(uc, r.Commitment, demand) : double.NaN;
        string[] colors = [SvgTheme.Var("eln-series-6"), SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-amber")];
        string hour = Pick(lang, "hour", "jam");
        var hours = grid ? Hours.Day.Skip(Case9Start).Take(Case9Hours).ToList() : Hours.Day;

        var heat = new double[G, T];
        for (int g = 0; g < G; g++) for (int t = 0; t < T; t++) heat[g, t] = ok && r.Commitment[g, t] ? r.DispatchMw[g, t] : double.NaN;
        string map = SvgCharts.Heatmap(heat, units.Select(u => u.Name).ToList(), hours,
            new HeatmapOptions { CellWidth = 27, CellHeight = 30, ShowValues = true, ValueFormat = "0", Unit = "MW", Min = 0, Max = units.Max(u => u.PmaxMw) });

        string mix = SvgCharts.StackedArea(units.Select((u, g) => new ChartSeries(u.Name, Enumerable.Range(0, T).Select(t => ok ? r.DispatchMw[g, t] : 0).ToList(), colors[g % 3])).ToList(),
            new ChartOptions { Width = 1080, Height = 280, YLabel = "MW", XLabels = hours, XLabel = hour });

        var lmpSeries = new List<ChartSeries>();
        if (grid)
        {
            for (int i = 0; i < r.BusIds.Length; i++)
                lmpSeries.Add(new ChartSeries($"bus {r.BusIds[i]}", Enumerable.Range(0, T).Select(t => ok ? r.Lmp[i, t] : double.NaN).ToList(), SvgTheme.Series(i)) { StrokeWidth = 1.6 });
        }
        else
        {
            lmpSeries.Add(new ChartSeries(Pick(lang, "SCUC λ (MILP duals)", "λ SCUC (dual MILP)"), ok ? r.SystemLambda : [], SvgTheme.Var("eln-danger")));
            lmpSeries.Add(new ChartSeries(Pick(lang, "priority-list DP λ", "λ DP daftar prioritas"), dp.Lambda, SvgTheme.Var("eln-muted")) { Dashed = true });
        }
        string lmp = SvgCharts.Line(lmpSeries, new ChartOptions { Width = 640, Height = 260, YLabel = "$/MWh", XLabels = hours, XLabel = hour });

        string res = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "scheduled reserve", "cadangan terjadwal"), ok ? r.SpinningReserveMw : [], SvgTheme.Var("eln-accent")) { Area = true },
            new ChartSeries(Pick(lang, "requirement", "kebutuhan"), demand.Select(d => reserve * d).ToList(), SvgTheme.Var("eln-danger")) { Dashed = true },
        ], new ChartOptions { Width = 640, Height = 240, YLabel = "MW", XLabels = hours, XLabel = hour, YMin = 0 });

        var unitRows = units.Select((u, g) => new Cell[]
        {
            u.Name, grid ? Same(MarketFmt.Id(u.Bus))[lang] : "—", Cell.Num(u.PminMw, "0"), Cell.Num(u.PmaxMw, "0"), Cell.Num(u.StartupCost, "0"),
            Same($"{u.MinUpHours}/{u.MinDownHours}")[lang],
            Cell.Num(ok ? Enumerable.Range(0, T).Count(t => r.Commitment[g, t]) : double.NaN, "0"),
            Cell.Num(ok ? Enumerable.Range(0, T).Count(t => r.Startup[g, t]) : double.NaN, "0"),
            Cell.Num(ok ? Enumerable.Range(0, T).Sum(t => r.DispatchMw[g, t]) : double.NaN, "0"),
        }).ToList();

        var ms = r.MilpSolution;
        double plateExact = plate.HasSolution ? ExactCost(uc, plate.Commitment, demand) : double.NaN;
        List<Cell[]> costRows =
        [
            [grid ? Pick(lang, "SCUC with line limit (4-segment cost)", "SCUC dengan batas saluran (biaya 4 segmen)") : Pick(lang, "SCUC MILP objective (4-segment cost)", "Objektif MILP SCUC (biaya 4 segmen)"),
                new(ok ? MarketFmt.Money(r.Objective) : "—", null, true),
                Same($"{ms.Nodes} {Pick(lang, "nodes", "simpul")} · gap {F(100 * r.MipGap, "0.000")} % · {F(ms.SolveTime.TotalMilliseconds, "0")} ms")[lang]],
            .. (grid
                ? (List<Cell[]>)[[Pick(lang, "SCUC copper plate (4-segment cost)", "SCUC tanpa jaringan (biaya 4 segmen)"), new(plate.HasSolution ? MarketFmt.Money(plate.Objective) : "—", null, true),
                    Pick(lang, "same units, network ignored", "unit sama, jaringan diabaikan")]]
                : []),
            [grid ? Pick(lang, "Copper-plate SCUC commitment at exact quadratic cost", "Komitmen SCUC tanpa jaringan pada biaya kuadratik eksak") : Pick(lang, "SCUC commitment at exact quadratic cost", "Komitmen SCUC pada biaya kuadratik eksak"),
                new(double.IsFinite(plateExact) ? MarketFmt.Money(plateExact) : "—", null, true), Pick(lang, "hourly economic dispatch + start-ups", "dispatch ekonomis per jam + start-up")],
            [Pick(lang, "Priority-list DP (Wood & Wollenberg)", "DP daftar prioritas (Wood & Wollenberg)"), new(dp.Feasible ? MarketFmt.Money(dp.TotalCost) : "—", null, true),
                grid ? Pick(lang, "no network", "tanpa jaringan") : Same($"{Pick(lang, "priority", "prioritas")} {string.Join(", ", dp.PriorityOrder.Select(i => units[i].Name))}")[lang]],
            [Pick(lang, "Start-up cost (SCUC)", "Biaya start-up (SCUC)"), new(ok ? MarketFmt.Money(r.StartupCost) : "—", null, true), ""],
        ];

        var blocks = new List<Block>
        {
            new ChartBlock(new("Commitment and dispatch (units × hours)", "Komitmen dan dispatch (unit × jam)"), map,
                new("cell = MW while committed, grey = off", "sel = MW saat hidup, abu-abu = mati"), Wide: true),
            new ChartBlock(new("Hourly dispatch", "Dispatch per jam"), mix, Same(grid ? Pick(lang, "SCUC with DC network limits (PTDF, lazy rows)", "SCUC dengan batas jaringan DC (PTDF, baris malas)") : Pick(lang, "SCUC, single bus", "SCUC, satu bus")), Wide: true),
            new ChartBlock(grid ? new("Hourly LMP per bus", "LMP per jam per bus") : new("Hourly system price", "Harga sistem per jam"), lmp,
                new("duals of the fixed-commitment LP", "dual LP dengan komitmen tetap")),
            new ChartBlock(new("Spinning reserve", "Cadangan berputar"), res, Same($"Σ(Pmax·u − P) ≥ {F(100 * reserve, "0")} % · D")),
            new TableBlock(new("Cost comparison ($)", "Perbandingan biaya ($)"), [new("Method", "Metode"), Same("$"), new("Notes", "Catatan")], costRows, Wide: true),
            new TableBlock(new("Units", "Unit"), [Same("Unit"), Same("Bus"), Same("Pmin"), Same("Pmax"), new("Start $", "Start $"), new("Up/down h", "Naik/turun j"), new("Hours on", "Jam hidup"), new("Starts", "Start"), Same("MWh")], unitRows, Wide: true),
        };
        if (grid)
        {
            var bind = r.Binding.Where(x => x.Kind != "Reserve").GroupBy(x => (x.Branch, x.Contingency)).Select(gp => new Cell[]
            {
                MarketFmt.Branch(net!, gp.Key.Branch), Same(string.Join(", ", gp.Select(x => (x.Hour + Case9Start).ToString("00", CultureInfo.InvariantCulture))))[lang],
                Cell.Num(gp.First().LimitMw, "0.0"), Cell.Num(gp.Max(x => x.ShadowPrice), "0.00"),
            }).ToList();
            if (bind.Count == 0) bind.Add(["—", Pick(lang, "no binding line", "tidak ada saluran terikat"), "", ""]);
            blocks.Add(new TableBlock(new("Binding network constraints", "Kendala jaringan terikat"), [new("Line", "Saluran"), new("Hours", "Jam"), new("Limit MW", "Batas MW"), new("max μ $/MWh", "μ maks $/MWh")], bind));
        }
        blocks.Add(new NoteBlock(new("Three-binary MILP (commitment, start-up, shut-down) with piecewise-linear costs, minimum up/down times, ramps, spinning reserve and, on a network, DC branch limits through PTDFs (N-1 through LODFs is available with ScucOptions.Contingencies). Solved here with the built-in branch and bound; HiGHS and OR-Tools adapters give the same optima. Prices are the duals of the LP with the commitment fixed. On the W&W data the MILP finds a cheaper schedule than the priority-list DP (183 665 $ vs 184 368 $ at 5 % reserve).",
            "MILP tiga biner (komitmen, start-up, shut-down) dengan biaya linear sepotong-sepotong, waktu hidup/mati minimum, ramp, cadangan berputar dan, pada jaringan, batas cabang DC melalui PTDF (N-1 melalui LODF tersedia lewat ScucOptions.Contingencies). Di sini diselesaikan dengan branch and bound bawaan; adapter HiGHS dan OR-Tools memberi optimum yang sama. Harga adalah dual LP dengan komitmen tetap. Pada data W&W, MILP menemukan jadwal yang lebih murah daripada DP daftar prioritas (183 665 $ vs 184 368 $ pada cadangan 5 %).")));

        var kpis = new List<Kpi>
        {
            grid
                ? new(new("SCUC cost", "Biaya SCUC"), ok ? MarketFmt.Money(r.TotalCost) : "—", "$", Same($"{r.Status} · {F(ms.SolveTime.TotalMilliseconds, "0")} ms"), ok ? "ok" : "alarm")
                : new(new("SCUC cost (exact)", "Biaya SCUC (eksak)"), double.IsFinite(exact) ? MarketFmt.Money(exact) : "—", "$",
                    Same($"MILP {MarketFmt.Money(r.Objective)} $ · {r.Status}"), ok ? "ok" : "alarm"),
            grid
                ? new(new("Line limit cost", "Biaya batas saluran"), ok && plate.HasSolution ? F(r.Objective - plate.Objective, "+0;−0") : "—", "$",
                    new($"vs copper-plate SCUC {MarketFmt.Money(plate.Objective)} $", $"thd SCUC tanpa jaringan {MarketFmt.Money(plate.Objective)} $"), ok && r.Objective > plate.Objective + 1 ? "warn" : null)
                : new(new("Priority-list DP", "DP daftar prioritas"), dp.Feasible ? MarketFmt.Money(dp.TotalCost) : "—", "$",
                    ok && dp.Feasible && double.IsFinite(exact) ? Same($"MILP {F(exact - dp.TotalCost, "+0;−0")} $ ({F(100 * (exact - dp.TotalCost) / dp.TotalCost, "+0.00;−0.00")} %)") : null),
            new(new("Start-ups", "Start-up"), ok ? Enumerable.Range(0, G).Sum(g => Enumerable.Range(0, T).Count(t => r.Startup[g, t])).ToString(CultureInfo.InvariantCulture) : "—", null,
                Same($"{MarketFmt.Money(ok ? r.StartupCost : 0)} $")),
            new(new("Peak price", "Harga puncak"), ok ? F(grid ? r.Lmp.Cast<double>().Max() : r.SystemLambda.Max(), "0.00") : "—", "$/MWh",
                grid && ok ? Same($"{Pick(lang, "max spread", "selisih maks")} {F(Enumerable.Range(0, T).Max(t => Enumerable.Range(0, r.BusIds.Length).Max(i => r.Lmp[i, t]) - Enumerable.Range(0, r.BusIds.Length).Min(i => r.Lmp[i, t])), "0.00")}") : null),
            new(new("Energy served", "Energi tersuplai"), F(demand.Sum(), "0"), "MWh", Same($"{Pick(lang, "peak", "puncak")} {F(demand.Max(), "0")} MW")),
        };
        return new InteractiveResult(kpis, blocks);
    }
}

/// <summary>Bid-based market clearing with stepwise offers / bids, nodal prices, settlement and an FTR.</summary>
public sealed class MarketDemo : IInteractiveScenario
{
    public string Id => "market";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        bool two = v.Get("system", "twobus") == "twobus";
        return
        [
            new ChoiceParam("system", new("Market", "Pasar"), [("twobus", new("Two-bus hand check", "Cek manual dua bus")), ("case9", new("WSCC 9-bus", "WSCC 9 bus"))], "twobus"),
            two ? new RangeParam("lim2", new("Line 1–2 limit", "Batas saluran 1–2"), 25, 200, 5, 100, "0", " MW")
                : new RangeParam("lim9", new("Line 8–9 limit", "Batas saluran 8–9"), 30, 150, 5, 60, "0", " MW"),
            new RangeParam(two ? "ftr2" : "ftr9", new("FTR quantity", "Kuantitas FTR"), 0, 150, 5, two ? 100 : 50, "0", " MW"),
        ];
    }

    private static (PowerNetwork Net, List<SupplyOffer> Offers, List<DemandBid> Bids, int Line, int FtrSource, int FtrSink) Build(bool two, double limit)
    {
        if (two)
        {
            var net = new PowerNetwork { Name = "two-bus", BaseMva = 100 };
            net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack });
            net.Buses.Add(new Bus { Id = 2 });
            net.Branches.Add(new Branch { From = 1, To = 2, X = 0.1, RateA = limit });
            return (net, [new("G1", 1, [new OfferBlock(200, 20)]), new("G2", 2, [new OfferBlock(200, 50)])], [new("L2", 2) { FixedMw = 150 }], 0, 1, 2);
        }
        var n9 = TestCases.Case9();
        // three price blocks per generator around its marginal cost; loads 90 % fixed + 10 % price responsive at 60 $/MWh (as `eln market`)
        var offers = n9.Generators.Select((g, k) =>
        {
            double third = g.Pmax / 3;
            return new SupplyOffer(MarketFmt.Gen(g, k), g.Bus,
            [
                new OfferBlock(third, Math.Round(g.Cost.Marginal(third / 2), 2)),
                new OfferBlock(third, Math.Round(g.Cost.Marginal(1.5 * third), 2)),
                new OfferBlock(third, Math.Round(g.Cost.Marginal(2.5 * third), 2)),
            ]);
        }).ToList();
        var bids = n9.Loads.Select(l => new DemandBid($"L{l.Bus}", l.Bus) { FixedMw = 0.9 * l.P, Blocks = [new OfferBlock(0.1 * l.P, 60)] }).ToList();
        int line = MarketFmt.BranchIndex(n9, 8, 9);
        n9.Branches[line].RateA = limit;
        return (n9, offers, bids, line, 1, 5);
    }

    /// <summary>Step curve through (cumulative MW, price) blocks as X/Y points.</summary>
    private static (List<double> X, List<double> Y) Steps(IEnumerable<OfferBlock> blocks, double startQ = 0, double? startPrice = null)
    {
        var x = new List<double>(); var y = new List<double>();
        double q = startQ;
        if (startPrice is double p0) { x.Add(0); y.Add(p0); x.Add(q); y.Add(p0); }
        foreach (var b in blocks)
        {
            x.Add(q); y.Add(b.Price);
            q += b.QuantityMw;
            x.Add(q); y.Add(b.Price);
        }
        return (x, y);
    }

    public InteractiveResult Run(ParamValues v, string lang)
    {
        bool two = v.Get("system", "twobus") == "twobus";
        double limit = two ? (v.Has("lim2") ? v.D("lim2") : 100) : (v.Has("lim9") ? v.D("lim9") : 60);
        var (net, offers, bids, line, src, sink) = Build(two, limit);
        var r = MarketClearing.ClearSinglePeriod(net, offers, bids);
        var copper = MarketClearing.ClearSinglePeriod(null, offers.Select(o => o with { Bus = 0 }).ToList(), bids.Select(b => b with { Bus = 0 }).ToList());
        var s = r.Settlement;

        // ---- aggregated supply / demand curves ------------------------------------------------------------------------
        var supply = offers.SelectMany(o => o.Blocks).OrderBy(b => b.Price).ToList();
        double top = Math.Ceiling(supply.Max(b => b.Price) * 1.25 / 20) * 20;
        double fixedMw = bids.Sum(b => b.FixedMw);
        var elastic = bids.SelectMany(b => b.Blocks).OrderByDescending(b => b.Price).ToList();
        var (sx, sy) = Steps(supply);
        var (dx, dy) = Steps(elastic, fixedMw, top);
        dx.Add(dx[^1]); dy.Add(0);
        if (elastic.Count == 0) { dx = [0, fixedMw, fixedMw]; dy = [top, top, 0]; }
        double qCleared = r.BidClearedMw.Sum();
        string curves = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "supply offers", "penawaran pasokan"), sy, SvgTheme.Var("eln-accent")) { X = sx },
            new ChartSeries(Pick(lang, "demand bids", "penawaran permintaan"), dy, SvgTheme.Var("eln-amber")) { X = dx },
            new ChartSeries(Pick(lang, "cleared quantity", "kuantitas terkliring"), [0, top], SvgTheme.Var("eln-muted")) { X = [qCleared, qCleared], Dashed = true, StrokeWidth = 1.2 },
        ], new ChartOptions
        {
            Width = 1080, Height = 320, XLabel = "MW", YLabel = "$/MWh", YMin = 0, YMax = top,
            ReferenceLines = [(copper.SystemLambda, $"{Pick(lang, "copper plate", "tanpa jaringan")} {F(copper.SystemLambda, "0.00")}")],
        });

        // ---- nodal prices ---------------------------------------------------------------------------------------------
        var buses = r.BusIds.Select(MarketFmt.Id).ToList();
        string lmp = SvgCharts.Bar(buses,
        [
            new ChartSeries(Pick(lang, "energy", "energi"), r.Components.Energy, SvgTheme.Var("eln-series-1")),
            new ChartSeries(Pick(lang, "congestion", "kongesti"), r.Components.Congestion, SvgTheme.Var("eln-danger")),
        ], new ChartOptions { Width = 640, Height = 260, XLabel = "bus", YLabel = "LMP $/MWh", ValueFormat = "0.00" }, stacked: true);

        // ---- settlement -----------------------------------------------------------------------------------------------
        var partRows = new List<Cell[]>();
        for (int g = 0; g < offers.Count; g++)
            partRows.Add([offers[g].Name, Pick(lang, "generator", "pembangkit"), MarketFmt.Id(offers[g].Bus), Cell.Num(r.OfferDispatchMw[g], "0.0"), Cell.Num(r.LmpAt(offers[g].Bus), "0.00"),
                Cell.Num(s.GeneratorRevenue[g], "0"), Cell.Num(s.GeneratorRevenue[g] - s.GeneratorCost[g], "0")]);
        for (int b = 0; b < bids.Count; b++)
            partRows.Add([bids[b].Name, Pick(lang, "load", "beban"), MarketFmt.Id(bids[b].Bus), Cell.Num(r.BidClearedMw[b], "0.0"), Cell.Num(r.LmpAt(bids[b].Bus), "0.00"),
                Cell.Num(-s.LoadPayment[b], "0"), new("—", null, true)]);

        List<Cell[]> totals =
        [
            [Pick(lang, "Load payments", "Pembayaran beban"), Cell.Num(s.TotalLoadPayment, "0")],
            [Pick(lang, "Generator revenues", "Pendapatan pembangkit"), Cell.Num(s.TotalGeneratorRevenue, "0")],
            [Pick(lang, "Congestion rent (payments − revenues)", "Sewa kongesti (pembayaran − pendapatan)"), Cell.Num(s.CongestionRent, "0")],
            [Pick(lang, "Σ shadow price × flow", "Σ harga bayangan × aliran"), Cell.Num(s.CongestionRentFromShadowPrices, "0")],
            [Pick(lang, "Producer surplus", "Surplus produsen"), Cell.Num(s.ProducerSurplus, "0")],
            [Pick(lang, "Consumer surplus, price-responsive bids", "Surplus konsumen, penawaran beli responsif"), Cell.Num(s.ElasticConsumerSurplus, "0")],
            [Pick(lang, "Consumer surplus (fixed load at VOLL)", "Surplus konsumen (beban tetap pada VOLL)"), Cell.Num(s.ConsumerSurplus, "0")],
            [Pick(lang, "Social welfare", "Kesejahteraan sosial"), Cell.Num(s.SocialWelfare, "0")],
        ];

        var bindRows = r.Binding.Select(c => new Cell[]
        {
            MarketFmt.Branch(net, c.Branch), Cell.Num(c.FlowMw, "0.0"), Cell.Num(c.LimitMw, "0.0"), Cell.Num(c.ShadowPrice, "0.00"), Cell.Num(c.ShadowPrice * c.FlowMw, "0"),
        }).ToList();
        if (bindRows.Count == 0) bindRows.Add(["—", Pick(lang, "uncongested", "tanpa kongesti"), "", "", ""]);

        // ---- FTR ------------------------------------------------------------------------------------------------------
        string fk = two ? "ftr2" : "ftr9";
        double ftrMw = v.Has(fk) ? v.D(fk) : (two ? 100 : 50);
        if (r.Lmp.Max() - r.Lmp.Min() > 1e-6)
        {
            // hedge the congested path: from the cheapest to the most expensive bus
            src = r.BusIds[Array.IndexOf(r.Lmp, r.Lmp.Min())];
            sink = r.BusIds[Array.IndexOf(r.Lmp, r.Lmp.Max())];
        }
        var ftr = new FtrObligation($"FTR {src}→{sink}", src, sink, ftrMw);
        var fs = FinancialTransmissionRights.Settle([ftr], r);
        bool feasible = FinancialTransmissionRights.IsSimultaneouslyFeasible(net, [ftr]);
        double ftrFlow = FinancialTransmissionRights.Flows(net, [ftr])[line];
        List<Cell[]> ftrRows =
        [
            [ftr.Name, Cell.Num(ftrMw, "0"), Cell.Num(r.LmpAt(sink) - r.LmpAt(src), "0.00"), Cell.Num(fs.TotalPayout, "0"), Cell.Num(fs.Surplus, "0"),
                feasible ? Cell.Chip(Pick(lang, "feasible", "layak"), "ok") : Cell.Chip(Pick(lang, "infeasible", "tidak layak"), "alarm"),
                fs.RevenueAdequate ? Cell.Chip(Pick(lang, "adequate", "cukup"), "ok") : Cell.Chip(Pick(lang, "shortfall", "kurang"), "warn")],
        ];

        double spread = r.Lmp.Max() - r.Lmp.Min();
        var kpis = new List<Kpi>
        {
            new(new("System λ", "λ sistem"), F(r.SystemLambda, "0.00"), "$/MWh", Same($"{Pick(lang, "copper plate", "tanpa jaringan")} {F(copper.SystemLambda, "0.00")}")),
            new(new("LMP range", "Rentang LMP"), $"{F(r.Lmp.Min(), "0.00")}–{F(r.Lmp.Max(), "0.00")}", "$/MWh", null, spread > 0.01 ? "warn" : "ok"),
            new(new("Cleared", "Terkliring"), F(qCleared, "0.0"), "MW", Same($"{Pick(lang, "fixed", "tetap")} {F(fixedMw, "0.0")} MW")),
            new(new("Congestion rent", "Sewa kongesti"), MarketFmt.Money(s.CongestionRent), "$", Same($"Σ μ·f = {MarketFmt.Money(s.CongestionRentFromShadowPrices)} $"), s.CongestionRent > 0.5 ? "warn" : null),
            new(new("FTR payout", "Pembayaran FTR"), MarketFmt.Money(fs.TotalPayout), "$", Same($"{ftr.Name} · {F(ftrMw, "0")} MW"), fs.RevenueAdequate ? "ok" : "alarm"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Supply offers and demand bids", "Penawaran pasokan dan permintaan"), curves,
                new("stepwise aggregate curves; fixed demand is price-taking (drawn at the top of the axis)", "kurva agregat bertingkat; permintaan tetap tidak responsif harga (digambar di puncak sumbu)"), Wide: true),
            new ChartBlock(new("Nodal prices", "Harga nodal"), lmp, new("energy + congestion (DC)", "energi + kongesti (DC)")),
            new TableBlock(new("Binding lines", "Saluran terikat"), [new("Line", "Saluran"), Same("MW"), new("Limit", "Batas"), Same("μ $/MWh"), Same("μ·f $")], bindRows),
            new TableBlock(new("Settlement per participant ($/h)", "Penyelesaian per peserta ($/jam)"),
                [new("Participant", "Peserta"), new("Role", "Peran"), Same("Bus"), Same("MW"), Same("LMP"), new("Revenue (−payment)", "Pendapatan (−bayar)"), new("Surplus", "Surplus")], partRows, Wide: true),
            new TableBlock(new("Market totals ($/h)", "Total pasar ($/jam)"), [new("Item", "Item"), Same("$")], totals),
            new TableBlock(new("Financial transmission right", "Hak transmisi finansial"),
                [Same("FTR"), Same("MW"), Same("ΔLMP"), new("Payout $", "Bayar $"), new("Rent left $", "Sisa sewa $"), new("SFT", "SFT"), new("Revenue", "Pendapatan")], ftrRows,
                new($"FTR flow {F(ftrFlow, "0.0")} / {F(limit, "0")} MW", $"aliran FTR {F(ftrFlow, "0.0")} / {F(limit, "0")} MW")),
            new NoteBlock(two
                ? new("Hand check: G1 offers 200 MW at 20 $/MWh at bus 1, G2 200 MW at 50 $/MWh at bus 2, 150 MW of fixed load at bus 2. With a 100 MW line G1 runs 100 MW, G2 50 MW, LMPs are 20 and 50 $/MWh, the load pays 7 500 $, generators receive 4 500 $ and the 3 000 $ congestion rent pays exactly a 100 MW FTR from bus 1 to bus 2.",
                    "Cek manual: G1 menawarkan 200 MW pada 20 $/MWh di bus 1, G2 200 MW pada 50 $/MWh di bus 2, beban tetap 150 MW di bus 2. Dengan saluran 100 MW, G1 beroperasi 100 MW, G2 50 MW, LMP 20 dan 50 $/MWh, beban membayar 7 500 $, pembangkit menerima 4 500 $, dan sewa kongesti 3 000 $ tepat membayar FTR 100 MW dari bus 1 ke bus 2.")
                : new("Uniform-price, bid-based clearing on the DC network: maximise bids − offers subject to power balance and line limits. LMPs are the LP duals, split into energy (reference bus) and congestion. An FTR set that is simultaneously feasible on the same network is always funded by the congestion rent (revenue adequacy).",
                    "Kliring harga seragam berbasis penawaran pada jaringan DC: maksimalkan penawaran beli − penawaran jual dengan kendala keseimbangan daya dan batas saluran. LMP adalah dual LP, dipisah menjadi energi (bus referensi) dan kongesti. Himpunan FTR yang layak secara simultan pada jaringan yang sama selalu didanai oleh sewa kongesti (kecukupan pendapatan).")),
        ]);
    }
}
