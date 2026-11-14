using System.Globalization;
using Electrical.Core.Data;
using Electrical.Core.Network;
using Electrical.Forecasting;
using Electrical.Optimization;
using Electrical.PowerFlow;
using Electrical.Renewable;
using Electrical.Simulation;
using Electrical.SmartGrid;
using Electrical.StateEstimation;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

// Declarative demos for Electrical.Renewable, SmartGrid, Optimization, StateEstimation, Forecasting and Simulation.

internal static class Hours
{
    public static readonly IReadOnlyList<string> Day = Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToList();
    public static IReadOnlyList<string> From(int start) => Enumerable.Range(0, 24).Select(h => ((h + start) % 24).ToString("00", CultureInfo.InvariantCulture)).ToList();
}

/// <summary>PV module I-V/P-V curves and daily PV output; wind turbine power curve with Weibull AEP.</summary>
public sealed class RenewableDemo : IInteractiveScenario
{
    public string Id => "renewable";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param> { new ChoiceParam("view", new("Resource", "Sumber"), [("pv", new("Solar PV", "Surya PV")), ("wind", new("Wind", "Angin"))], "pv") };
        if (v.Get("view", "pv") == "pv")
            list.Add(new RangeParam("temp", new("Cell temperature", "Suhu sel"), 0, 75, 5, 45, "0", " °C"));
        else
        {
            list.Add(new RangeParam("mean", new("Mean wind speed", "Kecepatan angin rata-rata"), 4, 11, 0.5, 7, "0.0", " m/s"));
            list.Add(new RangeParam("k", new("Weibull shape k", "Bentuk Weibull k"), 1.4, 3.0, 0.1, 2.0, "0.0"));
        }
        return list;
    }

    public InteractiveResult Run(ParamValues v, string lang) => v.Get("view", "pv") == "pv" ? Pv(v.D("temp"), lang) : Wind(v.D("mean"), v.D("k"), lang);

    private static InteractiveResult Pv(double temp, string lang)
    {
        var m = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT);
        double[] gs = [1000, 800, 600, 400, 200];
        string[] cs = [SvgTheme.Var("eln-series-2"), SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-series-6"), SvgTheme.Var("eln-series-3"), SvgTheme.Var("eln-series-8")];
        var iv = gs.Select((g, k) => (g, curve: m.IvCurve(g, temp, 80), color: cs[k])).ToList();
        string ivSvg = SvgCharts.Line(iv.Select(x => new ChartSeries($"{x.g:0} W/m²", x.curve.Select(p => p.Current).ToList(), x.color) { X = x.curve.Select(p => p.Voltage).ToList() }).ToList(),
            new ChartOptions { Width = 640, Height = 260, XLabel = Pick(lang, "voltage (V)", "tegangan (V)"), YLabel = Pick(lang, "current (A)", "arus (A)"), YMin = 0 });
        string pvSvg = SvgCharts.Line(iv.Select(x => new ChartSeries($"{x.g:0} W/m²", x.curve.Select(p => p.Power).ToList(), x.color) { X = x.curve.Select(p => p.Voltage).ToList() }).ToList(),
            new ChartOptions { Width = 640, Height = 260, XLabel = Pick(lang, "voltage (V)", "tegangan (V)"), YLabel = Pick(lang, "power (W)", "daya (W)"), YMin = 0 });

        // Clear-sky day in Jakarta (WIB), 1 MWp plant: solar position → Ineichen GHI → POA (10° north-facing) → PVWatts.
        var day = new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.FromHours(7));
        var acKw = new double[96];
        for (int i = 0; i < 96; i++)
        {
            var t = day.AddMinutes(15 * i);
            var sun = SolarPosition.Compute(t, -6.2, 106.8);
            if (sun.ZenithDeg >= 90) continue;
            var irr = Irradiance.IneichenClearSky(sun.ZenithDeg, t.DayOfYear);
            var poa = Irradiance.PlaneOfArray(irr, sun, 10, 0);
            double cell = CellTemperature.Noct(30, poa.Global);
            acKw[i] = PvWatts.InverterAc(PvWatts.DcPower(poa.Global, cell, 1000), 900);
        }
        string daySvg = SvgCharts.Line([new ChartSeries("AC kW", acKw, SvgTheme.Var("eln-amber")) { Area = true }],
            new ChartOptions { Width = 640, Height = 240, YLabel = "kW", ShowLegend = false, TimeStart = day, TimeStep = TimeSpan.FromMinutes(15) });

        var mpp = m.MaximumPowerPoint(1000, temp);
        var mppStc = m.MaximumPowerPoint(1000, 25);
        double energy = acKw.Sum() * 0.25 / 1000;
        var kpis = new List<Kpi>
        {
            new(new("MPP @ 1000 W/m²", "MPP @ 1000 W/m²"), F(mpp.Power, "0.0"), "W", Same($"{F(mpp.Voltage, "0.0")} V · {F(mpp.Current, "0.00")} A"), "ok"),
            new(new("Temperature loss", "Rugi suhu"), F(100 * (1 - mpp.Power / mppStc.Power), "0.0"), "%", Same($"vs 25 °C ({F(mppStc.Power, "0")} W)"), temp > 50 ? "warn" : null),
            new(Same("Voc"), F(m.OpenCircuitVoltage(1000, temp), "0.00"), "V", Same($"Rs {F(m.Rs, "0.000")} Ω · Rp {F(m.Rp, "0")} Ω")),
            new(new("1 MWp clear-sky day", "Hari cerah 1 MWp"), F(energy, "0.00"), "MWh", new("Jakarta, 21 March", "Jakarta, 21 Maret")),
            new(new("Peak AC", "Puncak AC"), F(acKw.Max(), "0"), "kW"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("I–V curves", "Kurva I–V"), ivSvg, Same($"Kyocera KC200GT · {F(temp, "0")} °C")),
            new ChartBlock(new("P–V curves", "Kurva P–V"), pvSvg, new("single-diode model", "model dioda tunggal")),
            new ChartBlock(new("Clear-sky PV output", "Keluaran PV langit cerah"), daySvg, new("SolarPosition · Ineichen · PVWatts", "SolarPosition · Ineichen · PVWatts"), Wide: true),
        ]);
    }

    private static InteractiveResult Wind(double mean, double k, string lang)
    {
        var t = WindTurbine.Generic2MW;
        var w = WeibullDistribution.FromMean(mean, k);
        var vs = Enumerable.Range(0, 61).Select(i => i * 0.5).ToList();
        string curve = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "power kW", "daya kW"), vs.Select(x => t.Power(x)).ToList(), SvgTheme.Var("eln-accent")) { X = vs },
        ], new ChartOptions { Width = 640, Height = 260, XLabel = Pick(lang, "wind speed (m/s)", "kecepatan angin (m/s)"), YLabel = "kW", ShowLegend = false });
        string pdf = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "probability %", "probabilitas %"), vs.Select(x => 100 * w.Pdf(x) * 0.5).ToList(), SvgTheme.Var("eln-series-3")) { X = vs, Area = true },
            new ChartSeries(Pick(lang, "energy share %", "porsi energi %"), Contribution(vs, w, t), SvgTheme.Var("eln-amber")) { X = vs },
        ], new ChartOptions { Width = 640, Height = 260, XLabel = Pick(lang, "wind speed (m/s)", "kecepatan angin (m/s)"), YLabel = "%" });

        var aep = WindEnergy.AnnualEnergyProduction(t, w, availability: 0.97, losses: 0.05);
        var kpis = new List<Kpi>
        {
            new(Same("AEP"), F(aep.AepMwh, "0"), "MWh/yr", new("97 % availability, 5 % losses", "ketersediaan 97 %, rugi 5 %"), "ok"),
            new(new("Capacity factor", "Faktor kapasitas"), F(100 * aep.CapacityFactor, "0.0"), "%", null, aep.CapacityFactor < 0.2 ? "warn" : "ok"),
            new(new("Full-load hours", "Jam beban penuh"), F(aep.FullLoadHours, "0"), "h"),
            new(new("Weibull scale c", "Skala Weibull c"), F(w.C, "0.00"), "m/s", Same($"k = {F(k, "0.0")}")),
            new(new("Power density", "Kerapatan daya"), F(w.PowerDensity(), "0"), "W/m²"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Power curve", "Kurva daya"), curve, Same("Generic 2 MW · 90 m rotor")),
            new ChartBlock(new("Wind regime", "Rezim angin"), pdf, new("Weibull pdf and where the energy comes from", "pdf Weibull dan asal energi")),
        ]);
    }

    private static List<double> Contribution(List<double> vs, WeibullDistribution w, WindTurbine t)
    {
        var e = vs.Select(x => w.Pdf(x) * t.Power(x)).ToList();
        double sum = e.Sum();
        return e.Select(x => sum > 0 ? 100 * x / sum : 0).ToList();
    }
}

/// <summary>Feeder reconfiguration, EV valley filling, microgrid EMS and battery peak shaving.</summary>
public sealed class SmartGridDemo : IInteractiveScenario
{
    public string Id => "smartgrid";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param>
        {
            new ChoiceParam("view", new("Study", "Studi"),
                [("reconfig", new("Feeder reconfiguration", "Rekonfigurasi penyulang")), ("ev", new("EV charging", "Pengisian EV")),
                 ("microgrid", new("Microgrid EMS", "EMS mikrogrid")), ("peak", new("BESS peak shaving", "Peak shaving BESS"))], "reconfig"),
        };
        switch (v.Get("view", "reconfig"))
        {
            case "ev": list.Add(new RangeParam("evs", new("Electric vehicles", "Kendaraan listrik"), 20, 200, 10, 80, "0")); break;
            case "peak": list.Add(new RangeParam("mwh", new("Battery energy", "Energi baterai"), 2, 20, 1, 8, "0", " MWh")); break;
        }
        return list;
    }

    public InteractiveResult Run(ParamValues v, string lang) => v.Get("view", "reconfig") switch
    {
        "ev" => Ev(v.I("evs"), lang),
        "microgrid" => Microgrid(lang),
        "peak" => Peak(v.D("mwh"), lang),
        _ => Reconfig(lang),
    };

    private static InteractiveResult Reconfig(string lang)
    {
        var net = FeederReconfiguration.Case33WithTies();
        var before = PowerFlowSolver.Solve(net);
        var r = FeederReconfiguration.Optimize(net);
        var after = PowerFlowSolver.Solve(r.Network);
        var opt = new SldOptions { LayerGap = 80, BusGap = 30, ShowFlows = false, ShowLegend = false };
        string sldBefore = SldRenderer.Render(net, before, opt);
        string sldAfter = SldRenderer.Render(r.Network, after, opt);
        var ids = before.Buses.Select(b => b.BusId.ToString(CultureInfo.InvariantCulture)).ToList();
        string prof = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "base (ties open)", "dasar (tie terbuka)"), before.Buses.Select(b => b.Vm).ToList(), SvgTheme.Var("eln-amber")),
            new ChartSeries(Pick(lang, "optimized", "teroptimasi"), after.Buses.Select(b => b.Vm).ToList(), SvgTheme.Var("eln-accent")),
        ], new ChartOptions { Width = 640, Height = 250, XLabel = "bus", YLabel = "|V| (pu)", XLabels = ids, ReferenceLines = [(0.95, "0.95")] });
        var kpis = new List<Kpi>
        {
            new(new("Losses before", "Rugi sebelum"), F(r.LossesBeforeMw * 1000, "0.0"), "kW"),
            new(new("Losses after", "Rugi sesudah"), F(r.LossesAfterMw * 1000, "0.0"), "kW", Same($"−{F(r.LossReductionPercent, "0.0")} %"), "ok"),
            new(new("Min voltage", "Tegangan min"), F(r.MinVoltageAfter, "0.000"), "pu", Same($"{Pick(lang, "was", "semula")} {F(r.MinVoltageBefore, "0.000")}"), r.MinVoltageAfter < 0.95 ? "warn" : "ok"),
            new(new("Open switches", "Sakelar terbuka"), r.OpenBranches.Count.ToString(CultureInfo.InvariantCulture), null, Same(string.Join(", ", r.OpenBranches))),
            new(new("Power flows run", "Aliran daya dijalankan"), r.PowerFlows.ToString(CultureInfo.InvariantCulture)),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Before: radial base case", "Sebelum: kasus dasar radial"), sldBefore, Same($"{F(r.LossesBeforeMw * 1000, "0.0")} kW")),
            new ChartBlock(new("After: loss-minimal configuration", "Sesudah: konfigurasi rugi minimum"), sldAfter, Same($"{F(r.LossesAfterMw * 1000, "0.0")} kW")),
            new ChartBlock(new("Voltage profile", "Profil tegangan"), prof, Same("Baran–Wu 33-bus + 5 ties"), Wide: true),
        ]);
    }

    private static double[] BaseLoadFromNoon(double peakKw)
    {
        var x = new double[24];
        for (int s = 0; s < 24; s++) { int h = (s + 12) % 24; x[s] = peakKw * Electrical.Gallery.Shared.ScadaScenario.Shape(h); }
        return x;
    }

    private static InteractiveResult Ev(int count, string lang)
    {
        var fleet = EvCharging.SyntheticFleet(count);
        var baseLoad = BaseLoadFromNoon(800);
        var dumb = EvCharging.Uncontrolled(fleet, baseLoad);
        var smart = EvCharging.ValleyFilling(fleet, baseLoad);
        var hours = Hours.From(12);
        string svg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "base load", "beban dasar"), baseLoad, SvgTheme.Var("eln-muted")) { Dashed = true },
            new ChartSeries(Pick(lang, "uncontrolled", "tanpa kendali"), dumb.TotalLoadKw, SvgTheme.Var("eln-danger")),
            new ChartSeries(Pick(lang, "valley filling", "pengisian lembah"), smart.TotalLoadKw, SvgTheme.Var("eln-accent")) { Area = true },
        ], new ChartOptions { Width = 680, Height = 270, YLabel = "kW", XLabels = hours, XLabel = Pick(lang, "hour (from 12:00)", "jam (mulai 12:00)") });
        string evSvg = SvgCharts.Bar(hours, [
            new ChartSeries(Pick(lang, "uncontrolled", "tanpa kendali"), dumb.EvLoadKw, SvgTheme.Var("eln-danger")),
            new ChartSeries(Pick(lang, "valley filling", "pengisian lembah"), smart.EvLoadKw, SvgTheme.Var("eln-accent")),
        ], new ChartOptions { Width = 680, Height = 230, YLabel = Pick(lang, "EV kW", "kW EV") });
        var kpis = new List<Kpi>
        {
            new(new("Fleet", "Armada"), count.ToString(CultureInfo.InvariantCulture), "EV", Same($"{F(fleet.Sum(f => f.EnergyKwh), "0")} kWh")),
            new(new("Peak, uncontrolled", "Puncak tanpa kendali"), F(dumb.PeakKw, "0"), "kW", null, "alarm"),
            new(new("Peak, valley filling", "Puncak pengisian lembah"), F(smart.PeakKw, "0"), "kW", Same($"−{F(100 * (1 - smart.PeakKw / dumb.PeakKw), "0")} %"), "ok"),
            new(new("Load factor", "Faktor beban"), F(smart.LoadFactor, "0.00"), null, Same($"{Pick(lang, "was", "semula")} {F(dumb.LoadFactor, "0.00")}")),
            new(new("Unmet energy", "Energi tak terpenuhi"), F(smart.TotalUnmetKwh, "0.0"), "kWh", null, smart.TotalUnmetKwh > 0.1 ? "warn" : "ok"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Feeder load with EV charging", "Beban penyulang dengan pengisian EV"), svg, null, Wide: true),
            new ChartBlock(new("EV charging power", "Daya pengisian EV"), evSvg, null, Wide: true),
        ]);
    }

    private static InteractiveResult Microgrid(string lang)
    {
        var c = MicrogridConfig.Demo();
        var rule = MicrogridEms.RuleBased(c);
        var opt = MicrogridEms.Optimal(c with { KeepFinalSoc = false }); // same end-of-day freedom as the rule-based strategy
        string mix = SvgCharts.StackedArea(
        [
            new ChartSeries("PV", c.PvKw.Select((p, i) => p - opt.CurtailedKw[i]).ToList(), SvgTheme.Var("eln-amber")),
            new ChartSeries(Pick(lang, "battery discharge", "baterai keluar"), opt.BatteryKw.Select(b => Math.Max(0, b)).ToList(), SvgTheme.Var("eln-series-3")),
            new ChartSeries(Pick(lang, "grid import", "impor jaringan"), opt.GridKw.Select(g => Math.Max(0, g)).ToList(), SvgTheme.Var("eln-series-6")),
            new ChartSeries("diesel", opt.DieselKw, SvgTheme.Var("eln-series-8")),
        ], new ChartOptions { Width = 680, Height = 270, YLabel = "kW", XLabels = Hours.Day, XLabel = Pick(lang, "hour", "jam") });
        string soc = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "SoC optimal", "SoC optimal"), opt.Soc.Select(s => 100 * s).ToList(), SvgTheme.Var("eln-accent")),
            new ChartSeries(Pick(lang, "SoC rule-based", "SoC berbasis aturan"), rule.Soc.Select(s => 100 * s).ToList(), SvgTheme.Var("eln-muted")) { Dashed = true },
            new ChartSeries(Pick(lang, "price ×100 $/kWh", "harga ×100 $/kWh"), c.BuyPrice.Select(p => 100 * p).ToList(), SvgTheme.Var("eln-danger")) { StrokeWidth = 1.4 },
        ], new ChartOptions { Width = 640, Height = 240, YLabel = "%", XLabels = Hours.Day });
        var rows = new List<Cell[]>
        {
            new Cell[] { Pick(lang, "Rule-based", "Berbasis aturan"), Cell.Num(rule.TotalCost, "0.00"), Cell.Num(rule.ImportKwh(), "0"), Cell.Num(rule.ExportKwh(), "0"), Cell.Num(rule.DieselKwh(), "0") },
            new Cell[] { Pick(lang, "Optimal (DP)", "Optimal (DP)"), Cell.Num(opt.TotalCost, "0.00"), Cell.Num(opt.ImportKwh(), "0"), Cell.Num(opt.ExportKwh(), "0"), Cell.Num(opt.DieselKwh(), "0") },
        };
        var kpis = new List<Kpi>
        {
            new(new("Daily cost, optimal", "Biaya harian optimal"), F(opt.TotalCost, "0.00"), "$", null, "ok"),
            new(new("Daily cost, rule-based", "Biaya harian aturan"), F(rule.TotalCost, "0.00"), "$"),
            new(new("Optimal vs rule-based", "Optimal vs aturan"), F(100 * (opt.TotalCost / rule.TotalCost - 1), "+0.0;-0.0"), "%",
                Same($"SoC end {F(100 * opt.Soc[^1], "0")} % vs {F(100 * rule.Soc[^1], "0")} %"), opt.TotalCost <= rule.TotalCost ? "ok" : "warn"),
            new(new("PV used", "PV terpakai"), F(100 * (1 - opt.CurtailedKw.Sum() / Math.Max(1, c.PvKw.Sum())), "0.0"), "%"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Optimal dispatch", "Dispatch optimal"), mix, new("dynamic programming over battery SoC", "pemrograman dinamis atas SoC baterai"), Wide: true),
            new ChartBlock(new("Battery state of charge", "Status muatan baterai"), soc, new("vs time-of-use price", "vs harga waktu pemakaian")),
            new TableBlock(new("Strategy comparison", "Perbandingan strategi"), [new("Strategy", "Strategi"), Same("$"), new("Import kWh", "Impor kWh"), new("Export kWh", "Ekspor kWh"), Same("Diesel kWh")], rows),
        ]);
    }

    private static InteractiveResult Peak(double mwh, string lang)
    {
        var load = Hours.Day.Select((_, h) => 40 * ScadaScenario.Shape(h)).ToArray();
        var bess = BatteryStorage.FromCRate(mwh, 0.5, 0.9);
        var r = DemandResponse.OptimalPeakShaving(load, bess);
        string svg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "load", "beban"), r.LoadMw, SvgTheme.Var("eln-muted")) { Dashed = true },
            new ChartSeries(Pick(lang, "net grid demand", "permintaan bersih"), r.NetMw, SvgTheme.Var("eln-accent")) { Area = true },
        ], new ChartOptions { Width = 680, Height = 260, YLabel = "MW", XLabels = Hours.Day, ReferenceLines = [(r.ThresholdMw, Pick(lang, "threshold", "ambang"))] });
        string bat = SvgCharts.Bar(Hours.Day, [new ChartSeries(Pick(lang, "battery MW (+ discharge)", "baterai MW (+ keluar)"), r.BatteryMw, SvgTheme.Var("eln-series-3"))],
            new ChartOptions { Width = 680, Height = 220, YLabel = "MW", ShowLegend = false, ValueFormat = "0.0" });
        var kpis = new List<Kpi>
        {
            new(new("Original peak", "Puncak semula"), F(r.OriginalPeakMw, "0.0"), "MW"),
            new(new("Shaved peak", "Puncak terpangkas"), F(r.NewPeakMw, "0.0"), "MW", Same($"−{F(100 * (1 - r.NewPeakMw / r.OriginalPeakMw), "0.0")} %"), "ok"),
            new(Same("BESS"), F(mwh, "0"), "MWh", Same($"{F(mwh * 0.5, "0.0")} MW · 0.5C")),
            new(new("Min SoC", "SoC min"), F(100 * r.Soc.Min(), "0"), "%", null, r.Soc.Min() < 0.15 ? "warn" : "ok"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Peak shaving", "Pemangkasan puncak"), svg, new("lowest feasible threshold (bisection)", "ambang terendah yang layak (biseksi)"), Wide: true),
            new ChartBlock(new("Battery schedule", "Jadwal baterai"), bat, null, Wide: true),
        ]);
    }
}

/// <summary>DC-OPF with LMPs and congestion; unit commitment over 24 h.</summary>
public sealed class OptimizationDemo : IInteractiveScenario
{
    public string Id => "optimization";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param> { new ChoiceParam("view", new("Study", "Studi"), [("opf", Same("DC-OPF & LMP")), ("uc", new("Unit commitment", "Unit commitment"))], "opf") };
        if (v.Get("view", "opf") == "opf") list.Add(new RangeParam("limit", new("Line 4–5 rating", "Rating saluran 4–5"), 10, 150, 5, 25, "0", " MW"));
        else list.Add(new RangeParam("peak", new("Peak demand", "Beban puncak"), 600, 1150, 25, 1000, "0", " MW"));
        return list;
    }

    public InteractiveResult Run(ParamValues v, string lang) => v.Get("view", "opf") == "opf" ? Opf(v.D("limit"), lang) : Uc(v.D("peak"), lang);

    private static InteractiveResult Opf(double limit, string lang)
    {
        var net = TestCases.Case9();
        int k = net.Branches.FindIndex(b => b.From == 4 && b.To == 5);
        var r = DcOpf.Solve(net, new DcOpfOptions { RatingOverrides = new Dictionary<int, double> { [k] = limit } });
        var free = DcOpf.Solve(net);
        string lmp = SvgCharts.Bar(r.BusIds.Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList(),
        [
            new ChartSeries(Pick(lang, "unconstrained", "tanpa batas"), free.Lmp, SvgTheme.Var("eln-muted")),
            new ChartSeries(Pick(lang, "with limit", "dengan batas"), r.Lmp, SvgTheme.Var("eln-accent")),
        ], new ChartOptions { Width = 640, Height = 260, XLabel = "bus", YLabel = "LMP ($/MWh)", ValueFormat = "0.0" });
        var flows = net.Branches.Select((b, i) => new Cell[]
        {
            $"{b.From}–{b.To}", Cell.Num(r.FlowsMw[i], "0.0"), Cell.Num(i == k ? limit : b.RateA, "0"),
            Math.Abs(r.FlowsMw[i]) >= (i == k ? limit : b.RateA) - 1e-3 ? Cell.Chip(Pick(lang, "binding", "terikat"), "alarm") : Cell.Chip("ok", "ok"),
        }).ToList();
        var gens = net.Generators.Select((g, i) => new Cell[] { g.Name ?? $"G{i + 1}", g.Bus.ToString(CultureInfo.InvariantCulture), Cell.Num(r.DispatchMw[i], "0.0"), Cell.Num(free.DispatchMw[i], "0.0") }).ToList();
        double spread = r.Lmp.Max() - r.Lmp.Min();
        var kpis = new List<Kpi>
        {
            new(new("Total cost", "Biaya total"), F(r.TotalCost, "0"), "$/h", Same($"+{F(r.TotalCost - free.TotalCost, "0")} $/h"), r.TotalCost > free.TotalCost + 1 ? "warn" : "ok"),
            new(new("System λ", "λ sistem"), F(r.Lambda, "0.00"), "$/MWh"),
            new(new("LMP spread", "Selisih LMP"), F(spread, "0.00"), "$/MWh", null, spread > 0.5 ? "alarm" : "ok"),
            new(new("Congestion rent", "Sewa kongesti"), F(r.CongestionRent, "0"), "$/h"),
            new(new("Binding constraints", "Kendala terikat"), r.Binding.Count.ToString(CultureInfo.InvariantCulture), null, r.Binding.Count > 0 ? Same(r.Binding[0].Description) : null),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Locational marginal prices", "Harga marginal lokasional"), lmp, Same("WSCC 9-bus · DC-OPF")),
            new TableBlock(new("Line flows", "Aliran saluran"), [new("Line", "Saluran"), Same("MW"), new("Limit MW", "Batas MW"), Same("")], flows),
            new TableBlock(new("Dispatch", "Dispatch"), [new("Unit", "Unit"), Same("Bus"), new("MW (limit)", "MW (batas)"), new("MW (free)", "MW (bebas)")], gens),
        ]);
    }

    private static InteractiveResult Uc(double peak, string lang)
    {
        var units = UnitCommitment.WoodWollenbergThreeUnits();
        var demand = Hours.Day.Select((_, h) => Math.Round(peak * (0.45 + 0.55 * (ScadaScenario.Shape(h) - 0.56) / 0.44), 1)).ToArray();
        var r = UnitCommitment.Solve(units, demand);
        string[] colors = [SvgTheme.Var("eln-series-6"), SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-amber")];
        string mix = SvgCharts.StackedArea(units.Select((u, i) => new ChartSeries(u.Name, Enumerable.Range(0, r.Hours).Select(t => r.DispatchMw[i, t]).ToList(), colors[i])).ToList(),
            new ChartOptions { Width = 680, Height = 270, YLabel = "MW", XLabels = Hours.Day, XLabel = Pick(lang, "hour", "jam") });
        string lam = SvgCharts.Line([new ChartSeries("λ $/MWh", r.Lambda, SvgTheme.Var("eln-danger"))], new ChartOptions { Width = 640, Height = 220, YLabel = "$/MWh", XLabels = Hours.Day, ShowLegend = false });
        var rows = units.Select((u, i) => new Cell[]
        {
            u.Name, Cell.Num(u.PminMw, "0"), Cell.Num(u.PmaxMw, "0"), Cell.Num(Enumerable.Range(0, r.Hours).Count(t => r.Status[i, t]), "0"), Cell.Num(r.StartsOf(i), "0"),
            Cell.Num(Enumerable.Range(0, r.Hours).Sum(t => r.DispatchMw[i, t]), "0"),
        }).ToList();
        var kpis = new List<Kpi>
        {
            new(new("Total cost", "Biaya total"), F(r.TotalCost, "0"), "$", Same($"{Pick(lang, "start-ups", "start-up")} {F(r.StartupCost, "0")} $")),
            new(new("Energy served", "Energi tersuplai"), F(demand.Sum(), "0"), "MWh"),
            new(new("Peak λ", "λ puncak"), F(r.Lambda.Max(), "0.00"), "$/MWh"),
            new(new("Feasible", "Layak"), r.Feasible ? Pick(lang, "YES", "YA") : Pick(lang, "NO", "TIDAK"), null, new("10 % spinning reserve", "cadangan berputar 10 %"), r.Feasible ? "ok" : "alarm"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Hourly dispatch", "Dispatch per jam"), mix, new("priority list + economic dispatch", "daftar prioritas + dispatch ekonomis"), Wide: true),
            new ChartBlock(new("System marginal price", "Harga marginal sistem"), lam),
            new TableBlock(new("Units", "Unit"), [new("Unit", "Unit"), Same("Pmin"), Same("Pmax"), new("Hours on", "Jam hidup"), new("Starts", "Start"), Same("MWh")], rows),
        ]);
    }
}

/// <summary>WLS state estimation on IEEE 14-bus with a gross error and largest-normalized-residual bad-data removal.</summary>
public sealed class StateEstimationDemo : IInteractiveScenario
{
    public string Id => "stateestimation";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new RangeParam("error", new("Gross error on P 1–2", "Galat kasar pada P 1–2"), 0, 40, 1, 20, "0", " σ"),
        new ChoiceParam("plan", new("Metering", "Pengukuran"), [("full", new("Full", "Lengkap")), ("sparse", new("Injections + V only", "Injeksi + V saja"))], "full"),
    ];

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var net = TestCases.Case14();
        var pf = PowerFlowSolver.Solve(net);
        var plan = v["plan"] == "full" ? MeasurementPlan.Full : MeasurementPlan.Full with { Flows = false, PmuBuses = [1, 6, 9] };
        var meas = MeasurementGenerator.FromPowerFlow(pf, plan, seed: 7);
        double err = v.D("error");
        int idx = meas.FindIndex(m => m.Type == MeasurementType.ActiveFlow && m.Location == 0 && m.End == BranchEnd.From);
        if (idx < 0) idx = meas.FindIndex(m => m.Type == MeasurementType.ActiveInjection);
        var bad = meas[idx];
        meas[idx] = bad with { Value = bad.Value + err * bad.Sigma };

        var first = WlsEstimator.Estimate(net, meas);
        var cleaned = BadDataDetector.IdentifyAndRemove(net, meas);
        var final = cleaned.Final;

        var top = first.NormalizedResiduals.Select((r, i) => (r: Math.Abs(r), i)).OrderByDescending(x => x.r).Take(14).ToList();
        string resSvg = SvgCharts.Bar(top.Select(x => Label(first.Measurements[x.i])).ToList(),
            [new ChartSeries("|rN|", top.Select(x => x.r).ToList(), SvgTheme.Var("eln-amber"))],
            new ChartOptions { Width = 640, Height = 260, YLabel = Pick(lang, "normalized residual", "residu ternormalisasi"), ShowLegend = false, ValueFormat = "0.0", ReferenceLines = [(3, "3.0")] });

        var ids = pf.Buses.Select(b => b.BusId.ToString(CultureInfo.InvariantCulture)).ToList();
        string vSvg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "true", "sebenarnya"), pf.Buses.Select(b => b.Vm).ToList(), SvgTheme.Var("eln-muted")) { StrokeWidth = 4 },
            new ChartSeries(Pick(lang, "with bad data", "dengan data buruk"), first.Vm, SvgTheme.Var("eln-danger")) { Dashed = true },
            new ChartSeries(Pick(lang, "after LNR removal", "setelah penghapusan LNR"), final.Vm, SvgTheme.Var("eln-accent")),
        ], new ChartOptions { Width = 640, Height = 260, XLabel = "bus", YLabel = "|V| (pu)", XLabels = ids });

        var removed = cleaned.Removed.Select(x => new Cell[] { Label(x.Measurement), Cell.Num(x.Measurement.Value, "0.00"), Cell.Num(x.NormalizedResidual, "0.0") }).ToList();
        double maxErr = pf.Buses.Select((b, i) => Math.Abs(final.Vm[i] - b.Vm)).Max();
        var kpis = new List<Kpi>
        {
            new(new("Measurements", "Pengukuran"), meas.Count.ToString(CultureInfo.InvariantCulture), null, Same($"{first.DegreesOfFreedom} dof")),
            new(Same("J(x̂)"), F(first.ObjectiveJ, "0.0"), null, Same($"χ² {Pick(lang, "limit", "batas")} {F(first.ChiSquareThreshold, "0.0")}"), first.BadDataSuspected ? "alarm" : "ok"),
            new(new("Bad data", "Data buruk"), first.BadDataSuspected ? Pick(lang, "DETECTED", "TERDETEKSI") : Pick(lang, "NONE", "TIDAK ADA"), null,
                Same($"{cleaned.Removed.Count} {Pick(lang, "removed", "dihapus")}"), first.BadDataSuspected ? "warn" : "ok"),
            new(new("Final J(x̂)", "J(x̂) akhir"), F(final.ObjectiveJ, "0.0"), null, null, final.BadDataSuspected ? "alarm" : "ok"),
            new(new("Max |V| error", "Galat |V| maks"), F(maxErr * 1000, "0.00"), "mpu", Same($"{final.Iterations} {Pick(lang, "iterations", "iterasi")}")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Largest normalized residuals", "Residu ternormalisasi terbesar"), resSvg, new("before removal", "sebelum penghapusan")),
            new ChartBlock(new("Estimated voltages", "Tegangan terestimasi"), vSvg),
            new TableBlock(new("Removed measurements", "Pengukuran yang dihapus"), [new("Measurement", "Pengukuran"), new("Value", "Nilai"), Same("rN")], removed),
            new NoteBlock(new($"A gross error of {F(err, "0")}σ was added to the active-power flow measured at the bus-1 end of line 1–2.",
                              $"Galat kasar {F(err, "0")}σ ditambahkan ke pengukuran aliran daya aktif di ujung bus 1 saluran 1–2.")),
        ]);
    }

    private static string Label(Measurement m) => m.Type switch
    {
        MeasurementType.VoltageMagnitude => $"V{m.Location}",
        MeasurementType.ActiveInjection => $"P{m.Location}",
        MeasurementType.ReactiveInjection => $"Q{m.Location}",
        MeasurementType.ActiveFlow => $"Pf{m.Location}{(m.End == BranchEnd.To ? "'" : "")}",
        MeasurementType.ReactiveFlow => $"Qf{m.Location}{(m.End == BranchEnd.To ? "'" : "")}",
        _ => $"θ{m.Location}",
    };
}

/// <summary>Day-ahead load forecast (Holt-Winters, regression, similar day, ensemble) against a synthetic actual.</summary>
public sealed class ForecastingDemo : IInteractiveScenario
{
    public string Id => "forecasting";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("method", new("Method", "Metode"),
            [("HoltWinters", Same("Holt-Winters")), ("Regression", new("Regression", "Regresi")), ("SimilarDay", new("Similar day", "Hari serupa")), ("Ensemble", new("Ensemble", "Ensembel"))], "Ensemble"),
        new ChoiceParam("kind", new("Customer mix", "Jenis pelanggan"),
            [("Residential", new("Residential", "Rumah tangga")), ("Commercial", new("Commercial", "Komersial")), ("Industrial", new("Industrial", "Industri"))], "Residential"),
        new RangeParam("horizon", new("Horizon", "Horizon"), 24, 72, 24, 48, "0", " h"),
    ];

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(7));
        int horizon = v.I("horizon"), histDays = 28;
        var step = TimeSpan.FromHours(1);
        int total = histDays * 24 + horizon;
        int days = (total + 23) / 24;
        var temp = SyntheticProfiles.Temperature(start, days, step, noiseC: 0.6, seed: 5);
        var load = SyntheticProfiles.Load(Enum.Parse<LoadProfileKind>(v["kind"]), start, days, step, 120, noise: 0.03, seed: 11, temperature: temp);
        var history = new TimeSeries(start, step, load.Values[..(histDays * 24)], "load", "MW");
        var actual = load.Values[(histDays * 24)..total];
        var method = Enum.Parse<LoadForecastMethod>(v["method"]);
        var fc = LoadForecast.Forecast(history, horizon, method, method is LoadForecastMethod.Regression or LoadForecastMethod.Ensemble ? temp : null);
        var pred = fc.Values.Take(horizon).ToArray();
        var err = ForecastMetrics.Evaluate(actual, pred);

        int show = 72;
        var lastHist = history.Values[^show..];
        var actualSeries = lastHist.Concat(actual).ToList();
        var predSeries = Enumerable.Repeat(double.NaN, show).Concat(pred).ToList();
        string svg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "actual", "aktual"), actualSeries, SvgTheme.Var("eln-muted")) { StrokeWidth = 2.6 },
            new ChartSeries(Pick(lang, "forecast", "ramalan"), predSeries, SvgTheme.Var("eln-accent")) { StrokeWidth = 2.2 },
        ], new ChartOptions { Width = 720, Height = 280, YLabel = "MW", TimeStart = history.TimeAt(history.Count - show), TimeStep = step });

        var errs = actual.Zip(pred, (a, p) => p - a).ToList();
        string errSvg = SvgCharts.Bar(Enumerable.Range(1, horizon).Select(h => h.ToString(CultureInfo.InvariantCulture)).ToList(),
            [new ChartSeries(Pick(lang, "error MW", "galat MW"), errs, SvgTheme.Var("eln-amber"))],
            new ChartOptions { Width = 640, Height = 220, XLabel = Pick(lang, "hours ahead", "jam ke depan"), YLabel = "MW", ShowLegend = false, ValueFormat = "0.0" });

        var kpis = new List<Kpi>
        {
            new(Same("MAPE"), F(err.Mape, "0.00"), "%", Same($"{horizon} h"), err.Mape < 3 ? "ok" : err.Mape < 6 ? "warn" : "alarm"),
            new(Same("RMSE"), F(err.Rmse, "0.00"), "MW"),
            new(Same("MAE"), F(err.Mae, "0.00"), "MW"),
            new(new("Bias", "Bias"), F(err.Bias, "0.00"), "MW"),
            new(new("Forecast peak", "Puncak ramalan"), F(pred.Max(), "0.0"), "MW", Same($"{Pick(lang, "actual", "aktual")} {F(actual.Max(), "0.0")}")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Forecast vs actual", "Ramalan vs aktual"), svg, Same($"{histDays} {Pick(lang, "days of history", "hari riwayat")} · {method}"), Wide: true),
            new ChartBlock(new("Error by lead time", "Galat per jam ke depan"), errSvg, null, Wide: true),
        ]);
    }
}

/// <summary>QSTS day on the 33-bus feeder with PV, plus generation adequacy (LOLE/EENS).</summary>
public sealed class SimulationDemo : IInteractiveScenario
{
    public string Id => "simulation";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param> { new ChoiceParam("view", new("Study", "Studi"), [("qsts", new("QSTS day with PV", "QSTS sehari dengan PV")), ("adequacy", new("Generation adequacy", "Kecukupan pembangkitan"))], "qsts") };
        if (v.Get("view", "qsts") == "qsts") list.Add(new RangeParam("pv", new("PV at bus 18", "PV di bus 18"), 0, 4, 0.25, 2.5, "0.00", " MW"));
        else list.Add(new RangeParam("peak", new("Annual peak", "Puncak tahunan"), 150, 230, 5, 195, "0", " MW"));
        return list;
    }

    public InteractiveResult Run(ParamValues v, string lang) => v.Get("view", "qsts") == "qsts" ? Qsts(v.D("pv"), lang) : Adequacy(v.D("peak"), lang);

    private static InteractiveResult Qsts(double pvMw, string lang)
    {
        var r = QstsSimulation.Case33Demo(1, pvMw, 18).Run();
        var t0 = r.Times[0];
        var step = r.Times.Length > 1 ? r.Times[1] - r.Times[0] : TimeSpan.FromMinutes(15);
        string band = SvgCharts.Line(
        [
            new ChartSeries("Vmax", r.VmaxPu, SvgTheme.Var("eln-amber")),
            new ChartSeries("Vmin", r.VminPu, SvgTheme.Var("eln-accent")) { Area = false },
        ], new ChartOptions { Width = 680, Height = 260, YLabel = "|V| (pu)", TimeStart = t0, TimeStep = step, ReferenceLines = [(0.95, "0.95"), (1.05, "1.05")] });
        string pw = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "load", "beban"), r.LoadMw, SvgTheme.Var("eln-muted")),
            new ChartSeries("PV", r.PvMw, SvgTheme.Var("eln-amber")) { Area = true },
            new ChartSeries(Pick(lang, "substation import", "impor gardu"), r.GridMw, SvgTheme.Var("eln-series-6")),
        ], new ChartOptions { Width = 680, Height = 260, YLabel = "MW", TimeStart = t0, TimeStep = step });
        int viol = r.VoltageViolationSteps();
        var kpis = new List<Kpi>
        {
            new(new("Steps", "Langkah"), r.Steps.ToString(CultureInfo.InvariantCulture), null, new($"{r.Converged.Count(c => c)} converged", $"{r.Converged.Count(c => c)} konvergen")),
            new(new("Energy served", "Energi tersuplai"), F(r.EnergyLoadMwh, "0.00"), "MWh", Same($"PV {F(r.EnergyPvMwh, "0.00")} MWh")),
            new(new("Losses", "Rugi-rugi"), F(r.EnergyLossesMwh * 1000, "0"), "kWh"),
            new(new("Reverse flow", "Aliran balik"), F(Math.Max(0, r.EnergyExportMwh), "0.00"), "MWh", null, r.EnergyExportMwh > 0.005 ? "warn" : null),
            new(new("Voltage violations", "Pelanggaran tegangan"), viol.ToString(CultureInfo.InvariantCulture), Pick(lang, "steps", "langkah"), Same($"Vmin {F(r.VminPu.Min(), "0.000")} · Vmax {F(r.VmaxPu.Max(), "0.000")}"), viol > 0 ? "alarm" : "ok"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Feeder voltage band", "Pita tegangan penyulang"), band, new("Baran–Wu 33-bus, 15-min power flows", "Baran–Wu 33 bus, aliran daya 15 menit"), Wide: true),
            new ChartBlock(new("Power balance", "Neraca daya"), pw, null, Wide: true),
        ]);
    }

    private static InteractiveResult Adequacy(double peak, string lang)
    {
        var units = new List<GeneratingUnit>();
        for (int i = 0; i < 6; i++) units.Add(new GeneratingUnit($"Coal {i + 1}", 25, 1460, 40));
        for (int i = 0; i < 4; i++) units.Add(new GeneratingUnit($"Gas {i + 1}", 15, 1100, 25));
        units.Add(new GeneratingUnit("Hydro", 30, 2900, 60));
        var load = AdequacyAssessment.SyntheticAnnualLoad(peak);
        var ana = AdequacyAssessment.Analytical(units, load);
        var mc = AdequacyAssessment.SequentialMonteCarlo(units, load, maxYears: 150, minYears: 50);
        string conv = SvgCharts.Line([new ChartSeries(Pick(lang, "running LOLE", "LOLE berjalan"), mc.RunningLole, SvgTheme.Var("eln-accent"))],
            new ChartOptions { Width = 640, Height = 240, XLabel = Pick(lang, "simulated years", "tahun disimulasikan"), YLabel = "h/yr", ShowLegend = false, ReferenceLines = [(ana.LoleHoursPerYear, Pick(lang, "analytical", "analitis"))] });
        var cot = AdequacyAssessment.CapacityOutageTable(units).Where(x => x.Probability > 1e-6).Take(12).ToList();
        string cotSvg = SvgCharts.Bar(cot.Select(x => F(x.OutageMw, "0")).ToList(), [new ChartSeries("P", cot.Select(x => 100 * x.Probability).ToList(), SvgTheme.Var("eln-series-3"))],
            new ChartOptions { Width = 640, Height = 240, XLabel = Pick(lang, "capacity out (MW)", "kapasitas keluar (MW)"), YLabel = "%", ShowLegend = false, ValueFormat = "0.0" });
        double cap = units.Sum(u => u.CapacityMw);
        var kpis = new List<Kpi>
        {
            new(new("Installed", "Terpasang"), F(cap, "0"), "MW", Same($"{Pick(lang, "reserve", "cadangan")} {F(100 * (cap / peak - 1), "0")} %")),
            new(Same("LOLE"), F(ana.LoleHoursPerYear, "0.00"), "h/yr", new("analytical", "analitis"), ana.LoleHoursPerYear > 2.4 ? "alarm" : "ok"),
            new(Same("LOLE (MC)"), F(mc.LoleHoursPerYear, "0.00"), "h/yr", Same($"{mc.Years} {Pick(lang, "years", "tahun")} · CoV {F(mc.CoefficientOfVariation, "0.00")}")),
            new(Same("EENS"), F(mc.EensMwhPerYear, "0.0"), "MWh/yr"),
            new(Same("LOLF"), F(mc.LolfPerYear, "0.00"), "/yr"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Monte-Carlo convergence", "Konvergensi Monte-Carlo"), conv, new("sequential simulation", "simulasi sekuensial")),
            new ChartBlock(new("Capacity outage probability", "Probabilitas kapasitas keluar"), cotSvg, new("COPT", "COPT")),
        ]);
    }
}
