using System.Globalization;
using Electrical.Simulation;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// QSTS volt/var control: the Baran–Wu 33-bus feeder behind a 10 MVA substation transformer, simulated without control and with an
/// OLTC (bus 1 → 1.02 pu) plus a 2 × 0.45 Mvar switched capacitor at bus 30 (<see cref="QstsSimulation.Case33ControlDemo"/>).
/// </summary>
public sealed class VoltageControlDemo : IInteractiveScenario
{
    public string Id => "voltagecontrol";

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new RangeParam("pv", new("PV at bus 18", "PV di bus 18"), 0, 3, 0.25, 1.5, "0.00", " MW"),
        new ChoiceParam("days", new("Horizon", "Horizon"), [("1", new("1 day", "1 hari")), ("2", new("2 days", "2 hari"))], "1"),
    ];

    /// <summary>Step-shaped series for a discrete state: each value is held until the next step (X in step units).</summary>
    private static ChartSeries Steps(string name, double[] s, string color)
    {
        var x = new List<double>(2 * s.Length);
        var y = new List<double>(2 * s.Length);
        for (int t = 0; t < s.Length; t++)
        {
            x.Add(t); y.Add(s[t]);
            x.Add(t + 1); y.Add(s[t]);
        }
        return new ChartSeries(name, y, color) { X = x };
    }

    public InteractiveResult Run(ParamValues v, string lang)
    {
        double pv = v.D("pv");
        int days = v.Get("days", "1") == "2" ? 2 : 1;
        var off = QstsSimulation.Case33ControlDemo(days, withControl: false, pvMw: pv).Run();
        var on = QstsSimulation.Case33ControlDemo(days, withControl: true, pvMw: pv).Run();
        var t0 = on.Times[0];
        var step = on.Times.Length > 1 ? on.Times[1] - on.Times[0] : TimeSpan.FromMinutes(15);
        string without = Pick(lang, "without control", "tanpa kendali"), with = Pick(lang, "with control", "dengan kendali");

        string band = SvgCharts.Line(
        [
            new ChartSeries($"Vmin {without}", off.VminPu, SvgTheme.Var("eln-danger")) { Dashed = true },
            new ChartSeries($"Vmax {without}", off.VmaxPu, SvgTheme.Var("eln-muted")) { Dashed = true },
            new ChartSeries($"Vmin {with}", on.VminPu, SvgTheme.Var("eln-accent")),
            new ChartSeries($"Vmax {with}", on.VmaxPu, SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 680, Height = 270, YLabel = "|V| (pu)", TimeStart = t0, TimeStep = step, ReferenceLines = [(0.95, "0.95"), (1.05, "1.05")] });

        int oltc = on.ControllerNames.ToList().IndexOf("OLTC T1"), cap = on.ControllerNames.ToList().IndexOf("Cap bus 30");
        var tap = on.ControllerState(oltc);
        var stages = on.ControllerState(cap);
        string tapSvg = SvgCharts.Line([Steps(Pick(lang, "tap position", "posisi tap"), tap, SvgTheme.Var("eln-series-3"))],
            new ChartOptions { Width = 640, Height = 230, YLabel = Pick(lang, "tap (steps of 0.625 %)", "tap (langkah 0,625 %)"), TimeStart = t0, TimeStep = step, ShowLegend = false,
                YMin = Math.Min(0, tap.Min() - 1), YMax = Math.Max(4, tap.Max() + 1) });
        string capSvg = SvgCharts.Line([Steps(Pick(lang, "stages in service", "tahap aktif"), stages, SvgTheme.Var("eln-series-6")) ],
            new ChartOptions { Width = 640, Height = 230, YLabel = Pick(lang, "stages (× 0.45 Mvar)", "tahap (× 0,45 Mvar)"), TimeStart = t0, TimeStep = step, ShowLegend = false, YMin = 0, YMax = 2 });

        // cumulative operations
        var tapOps = Enumerable.Range(0, on.Steps).Select(t => (double)on.ControllerOperations[t, oltc]).ToArray();
        var capOps = Enumerable.Range(0, on.Steps).Select(t => (double)on.ControllerOperations[t, cap]).ToArray();
        string opsSvg = SvgCharts.Line(
        [
            Steps(Pick(lang, "OLTC tap steps", "langkah tap OLTC"), tapOps, SvgTheme.Var("eln-series-3")),
            Steps(Pick(lang, "capacitor switchings", "pensaklaran kapasitor"), capOps, SvgTheme.Var("eln-series-6")),
        ], new ChartOptions { Width = 640, Height = 230, YLabel = Pick(lang, "cumulative operations", "operasi kumulatif"), TimeStart = t0, TimeStep = step, YMin = 0 });

        int vOff = off.VoltageViolationSteps(), vOn = on.VoltageViolationSteps();
        int nTap = on.TotalOperations(oltc), nCap = on.TotalOperations(cap);
        string steps = Pick(lang, "steps", "langkah");
        var kpis = new List<Kpi>
        {
            new(new("Violations without control", "Pelanggaran tanpa kendali"), vOff.ToString(CultureInfo.InvariantCulture), $"/ {off.Steps} {steps}", Same($"Vmin {F(off.VminPu.Min(), "0.0000")} pu"), vOff > 0 ? "alarm" : "ok"),
            new(new("Violations with control", "Pelanggaran dengan kendali"), vOn.ToString(CultureInfo.InvariantCulture), $"/ {on.Steps} {steps}", Same($"Vmin {F(on.VminPu.Min(), "0.0000")} pu"), vOn > 0 ? "warn" : "ok"),
            new(new("OLTC operations", "Operasi OLTC"), nTap.ToString(CultureInfo.InvariantCulture), Pick(lang, "tap steps", "langkah tap"), Same($"tap {F(tap.Min(), "0")}…{F(tap.Max(), "0")}")),
            new(new("Capacitor operations", "Operasi kapasitor"), nCap.ToString(CultureInfo.InvariantCulture), Pick(lang, "switchings", "pensaklaran"), new($"max {F(stages.Max(), "0")} of 2 stages", $"maks {F(stages.Max(), "0")} dari 2 tahap")),
            new(new("Losses", "Rugi-rugi"), F(on.EnergyLossesMwh * 1000, "0"), "kWh", new($"{F(off.EnergyLossesMwh * 1000, "0")} kWh without control", $"{F(off.EnergyLossesMwh * 1000, "0")} kWh tanpa kendali")),
        };

        var rows = new List<Cell[]>
        {
            new Cell[] { new L("Violation steps (0.95–1.05 pu)", "Langkah pelanggaran (0,95–1,05 pu)")[lang], new(vOff.ToString(CultureInfo.InvariantCulture), vOff > 0 ? "alarm" : "ok"), new(vOn.ToString(CultureInfo.InvariantCulture), vOn > 0 ? "warn" : "ok") },
            new Cell[] { new L("Lowest voltage (pu)", "Tegangan terendah (pu)")[lang], Cell.Num(off.VminPu.Min(), "0.0000"), Cell.Num(on.VminPu.Min(), "0.0000") },
            new Cell[] { new L("Highest voltage (pu)", "Tegangan tertinggi (pu)")[lang], Cell.Num(off.VmaxPu.Max(), "0.0000"), Cell.Num(on.VmaxPu.Max(), "0.0000") },
            new Cell[] { new L("Energy losses (kWh)", "Rugi energi (kWh)")[lang], Cell.Num(off.EnergyLossesMwh * 1000, "0"), Cell.Num(on.EnergyLossesMwh * 1000, "0") },
            new Cell[] { new L("Substation import (MWh)", "Impor gardu (MWh)")[lang], Cell.Num(off.EnergyImportMwh, "0.00"), Cell.Num(on.EnergyImportMwh, "0.00") },
            new Cell[] { new L("Control re-solves", "Penyelesaian ulang kendali")[lang], Cell.Num(0, "0"), Cell.Num(on.ControlIterations.Sum(), "0") },
        };

        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Feeder voltage band: without vs with control", "Pita tegangan penyulang: tanpa vs dengan kendali"), band,
                new("dashed = no control; OLTC regulates bus 1 to 1.02 pu, capacitor at bus 30", "putus-putus = tanpa kendali; OLTC mengatur bus 1 ke 1,02 pu, kapasitor di bus 30"), Wide: true),
            new ChartBlock(new("OLTC tap position", "Posisi tap OLTC"), tapSvg, new("±16 × 0.625 %, band 0.0125 pu, 1-step delay", "±16 × 0,625 %, pita 0,0125 pu, tunda 1 langkah")),
            new ChartBlock(new("Capacitor stages at bus 30", "Tahap kapasitor di bus 30"), capSvg, new("on below 0.97 pu, off above 1.03 pu", "masuk di bawah 0,97 pu, keluar di atas 1,03 pu")),
            new ChartBlock(new("Operations counter", "Penghitung operasi"), opsSvg, new("QstsResult.ControllerOperations", "QstsResult.ControllerOperations")),
            new TableBlock(new("Comparison", "Perbandingan"), [new("Quantity", "Besaran"), new("Without control", "Tanpa kendali"), new("With control", "Dengan kendali")], rows,
                new($"{on.Steps} × 15-min power flows each", $"masing-masing {on.Steps} × aliran daya 15 menit")),
            new NoteBlock(new("After every converged power flow each controller compares its measurement with its dead band; when one acts (tap move or capacitor stage) the step is re-solved with a warm start and the controllers are checked again. Delay timers count consecutive out-of-band steps, so short excursions do not wear the tap changer.",
                "Setelah setiap aliran daya konvergen, tiap pengendali membandingkan pengukurannya dengan pita matinya; bila ada yang bertindak (tap bergeser atau tahap kapasitor), langkah itu diselesaikan ulang dengan awal hangat dan pengendali diperiksa lagi. Pewaktu tunda menghitung langkah berurutan di luar pita, sehingga simpangan singkat tidak mengausi pengubah tap.")),
        ]);
    }
}
