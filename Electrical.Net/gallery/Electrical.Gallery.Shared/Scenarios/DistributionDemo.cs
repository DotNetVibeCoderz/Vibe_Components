using System.Globalization;
using System.Numerics;
using Electrical.Distribution;
using Electrical.Visualization;
using static Electrical.Distribution.ConductorRole;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// Unbalanced three-phase power flow on the IEEE 13-node test feeder (built in code or read from its OpenDSS script), compared with the
/// published voltages; regulator taps, unbalance factors, and a Carson/Kron line-impedance calculator for configurations 601–607.
/// </summary>
public sealed class DistributionDemo : IInteractiveScenario
{
    public string Id => "distribution";

    private static readonly string[] BusOrder = ["650", "rg60", "632", "633", "634", "645", "646", "670", "671", "680", "684", "611", "652", "692", "675"];

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("model", new("Feeder model", "Model penyulang"), [("code", new("Built in code", "Dibangun di kode")), ("dss", new("OpenDSS script", "Skrip OpenDSS"))], "code"),
        new ChoiceParam("reg", new("Regulators", "Regulator"), [("fixed", new("Published taps", "Tap terbitan")), ("auto", new("Automatic (LDC)", "Otomatis (LDC)"))], "fixed"),
        new RangeParam("load", new("Load multiplier", "Pengali beban"), 0.5, 1.3, 0.05, 1.0, "0.00", "×"),
        new ChoiceParam("config", new("Line configuration", "Konfigurasi saluran"),
            [("601", Same("601")), ("602", Same("602")), ("603", Same("603")), ("604", Same("604")), ("605", Same("605")), ("606", Same("606")), ("607", Same("607"))], "601"),
    ];

    /// <summary>Conductor geometry of the IEEE 13-node configurations (IEEE data sheet: spacings 500/505/510 overhead, 515/520 underground).</summary>
    public static (LineImpedance Z, L Description) Geometry(int id)
    {
        var raven = ConductorLibrary.Acsr1_0_6_1;
        return id switch
        {
            601 => (LineConstants.Overhead(OverheadConfiguration.Spacing500(ConductorLibrary.Acsr556_5_26_7, ConductorLibrary.Acsr4_0_6_1, PhaseB, PhaseA, PhaseC)),
                new("Spacing 500, phasing B A C N, 556,500 26/7 ACSR phases, 4/0 6/1 ACSR neutral", "Jarak 500, urutan B A C N, fasa ACSR 556.500 26/7, netral ACSR 4/0 6/1")),
            602 => (LineConstants.Overhead(OverheadConfiguration.Spacing500(ConductorLibrary.Acsr4_0_6_1, ConductorLibrary.Acsr4_0_6_1, PhaseC, PhaseA, PhaseB)),
                new("Spacing 500, phasing C A B N, 4/0 6/1 ACSR phases and neutral", "Jarak 500, urutan C A B N, fasa dan netral ACSR 4/0 6/1")),
            603 => (LineConstants.Overhead(new OverheadConfiguration("ID-505", [new(PhaseC, 0, 28, raven), new(PhaseB, 7, 28, raven), new(Neutral, 4, 24, raven)])),
                new("Spacing 505, phasing C B N, 1/0 ACSR", "Jarak 505, urutan C B N, ACSR 1/0")),
            604 => (LineConstants.Overhead(new OverheadConfiguration("ID-505", [new(PhaseA, 0, 28, raven), new(PhaseC, 7, 28, raven), new(Neutral, 4, 24, raven)])),
                new("Spacing 505, phasing A C N, 1/0 ACSR", "Jarak 505, urutan A C N, ACSR 1/0")),
            605 => (LineConstants.Overhead(new OverheadConfiguration("ID-510", [new(PhaseC, 0.5, 29, raven), new(Neutral, 0, 24, raven)])),
                new("Spacing 510, phasing C N, 1/0 ACSR", "Jarak 510, urutan C N, ACSR 1/0")),
            606 => (LineConstants.ConcentricNeutral(new ConcentricNeutralCable(ConductorLibrary.Aa250, ConductorLibrary.Cu14, 13, 1.29),
                    [new(PhaseA, 0, 0), new(PhaseB, 0.5, 0), new(PhaseC, 1.0, 0)]),
                new("Spacing 515, three 250 kcmil AA concentric-neutral cables (13 × #14 Cu), 6 in apart", "Jarak 515, tiga kabel netral konsentris AA 250 kcmil (13 × #14 Cu), berjarak 6 in")),
            _ => (LineConstants.TapeShielded(new TapeShieldedCable(ConductorLibrary.Aa1_0, 0.88, 5), [new(PhaseA, 0, 0)],
                    new ConductorPosition(Neutral, 1.0 / 12, 0, ConductorLibrary.Cu1_0)),
                new("Spacing 520, 1/0 AA tape-shielded cable (5 mil) with a 1/0 Cu neutral 1 in away", "Jarak 520, kabel berpelindung pita AA 1/0 (5 mil) dengan netral Cu 1/0 berjarak 1 in")),
        };
    }

    /// <summary>Phase of each row/column of the data-sheet matrix (<see cref="DistributionTestCases.Ieee13Config"/>).</summary>
    private static int[] SheetOrder(int id) => id switch { 603 => [2, 1], 604 => [0, 2], 605 => [2], 607 => [0], _ => [0, 1, 2] };

    private static string Z(Complex c) => $"{F(c.Real, "0.0000")} {(c.Imaginary < 0 ? "−" : "+")} j{F(Math.Abs(c.Imaginary), "0.0000")}";

    public InteractiveResult Run(ParamValues v, string lang)
    {
        bool dss = v.Get("model", "code") == "dss", auto = v.Get("reg", "fixed") == "auto";
        double mult = v.D("load");
        var net = dss ? OpenDssReader.Parse(DistributionTestCases.Ieee13NodeDss) : DistributionTestCases.Ieee13Node(automaticRegulator: auto);
        if (dss && !auto)
        {
            // the script carries regcontrols; freeze them at the published taps for the "fixed" case
            net.Controllers.Clear();
            int[] taps = [10, 8, 11];
            foreach (var reg in net.Regulators)
                foreach (var p in reg.Phases.Indices()) reg.SetTap(p, taps[p]);
        }
        var r = UnbalancedPowerFlow.Solve(net, new DistributionPowerFlowOptions { ControlMode = auto ? ControlMode.Static : ControlMode.Off, LoadMultiplier = mult });
        var pub = DistributionTestCases.Ieee13NodePublished;

        DistributionBusResult? Find(string name) => r.Buses.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        var buses = BusOrder.Where(b => Find(b) is not null).ToList();
        string[] colors = [SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-series-2"), SvgTheme.Var("eln-series-3")];
        var series = new List<ChartSeries>();
        for (int p = 0; p < 3; p++)
            series.Add(new ChartSeries(Pick(lang, $"phase {"ABC"[p]}", $"fasa {"ABC"[p]}"), buses.Select(b => Find(b)!.VmPu[p]).ToList(), colors[p]));
        for (int p = 0; p < 3; p++)
            series.Add(new ChartSeries($"IEEE {"ABC"[p]}", buses.Select(b => pub.TryGetValue(b, out var x) ? x[p].Magnitude : double.NaN).ToList(), colors[p]) { Dashed = true, StrokeWidth = 1.2 });
        string profile = SvgCharts.Line(series, new ChartOptions { Width = 680, Height = 280, YLabel = "|V| (pu)", XLabels = buses, ReferenceLines = [(0.95, "0.95"), (1.05, "1.05")] });

        // ---- comparison table ---------------------------------------------------------------------------------------------
        double worst = 0; string worstBus = "";
        var rows = new List<Cell[]>();
        foreach (var name in buses)
        {
            var b = Find(name)!;
            double err = double.NaN;
            if (pub.TryGetValue(name, out var pv))
            {
                err = b.Phases.Indices().Where(p => !double.IsNaN(pv[p].Magnitude)).Select(p => Math.Abs(b.VmPu[p] - pv[p].Magnitude) / pv[p].Magnitude).DefaultIfEmpty(0).Max();
                if (err > worst) { worst = err; worstBus = name; }
            }
            Cell Ph(int p) => b.Phases.Has(p)
                ? new Cell($"{F(b.VmPu[p], "0.0000")}" + (pv is not null && !double.IsNaN(pv[p].Magnitude) ? $" ({F(pv[p].Magnitude, "0.0000")})" : ""), null, true)
                : new Cell("—", null, true);
            rows.Add([name, Ph(0), Ph(1), Ph(2),
                double.IsNaN(err) ? new Cell("—", null, true) : Cell.Chip(F(100 * err, "0.000") + " %", Math.Abs(mult - 1) > 1e-9 ? "na" : err < 0.005 ? "ok" : "warn"),
                double.IsNaN(b.VoltageUnbalanceFactor) ? new Cell("—", null, true) : Cell.Num(100 * b.VoltageUnbalanceFactor, "0.00")]);
        }

        // ---- unbalance chart --------------------------------------------------------------------------------------------
        var three = buses.Where(b => !double.IsNaN(Find(b)!.VoltageUnbalanceFactor)).ToList();
        string unb = SvgCharts.Bar(three,
        [
            new ChartSeries(Pick(lang, "VUF |V2|/|V1|", "VUF |V2|/|V1|"), three.Select(b => 100 * Find(b)!.VoltageUnbalanceFactor).ToList(), SvgTheme.Var("eln-accent")),
            new ChartSeries("NEMA", three.Select(b => 100 * Find(b)!.NemaUnbalance).ToList(), SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 640, Height = 240, YLabel = "%", ValueFormat = "0.0" });

        // ---- regulators -------------------------------------------------------------------------------------------------
        var regRows = r.RegulatorTaps.Select(kv => new Cell[]
        {
            kv.Key, new(string.Join(" / ", kv.Value), null, true),
            new(string.Join(" / ", kv.Value.Select(t => F(1 + 0.00625 * t, "0.0000"))), null, true),
        }).ToList();
        regRows.Add([Pick(lang, "published (IEEE)", "terbitan (IEEE)"), new("10 / 8 / 11", null, true), new("1.0625 / 1.0500 / 1.0687", null, true)]);

        // ---- Carson calculator ------------------------------------------------------------------------------------------
        int cfg = int.Parse(v.Get("config", "601"), CultureInfo.InvariantCulture);
        var (zImp, desc) = Geometry(cfg);
        var full = zImp.ZFull3x3OhmPerMile();
        var sheet = DistributionTestCases.Ieee13Config(cfg).ZOhmPerMile;
        var order = SheetOrder(cfg);
        var sheetFull = new Complex?[3, 3];
        for (int i = 0; i < order.Length; i++) for (int j = 0; j < order.Length; j++) sheetFull[order[i], order[j]] = sheet[i, j];
        double dev = 0;
        var has = zImp.PhaseOrder;
        var zRows = new List<Cell[]>();
        for (int i = 0; i < 3; i++)
        {
            var row = new Cell[4];
            row[0] = "abc"[i].ToString();
            for (int j = 0; j < 3; j++)
            {
                if (!has.Contains(i) || !has.Contains(j)) { row[j + 1] = new Cell("—", null, true); continue; }
                if (sheetFull[i, j] is Complex s) dev = Math.Max(dev, (s - full[i, j]).Magnitude);
                row[j + 1] = new Cell(Z(full[i, j]), null, true);
            }
            zRows.Add(row);
        }
        var sRows = new List<Cell[]>();
        for (int i = 0; i < 3; i++)
            sRows.Add(["abc"[i].ToString(), .. Enumerable.Range(0, 3).Select(j => sheetFull[i, j] is Complex s ? new Cell(Z(s), null, true) : new Cell("—", null, true))]);
        string seqNote = zImp.PhaseCount == 3
            ? $"z1 = {Z(zImp.Sequence.Positive)}, z0 = {Z(zImp.Sequence.Zero)} Ω/mile"
            : Pick(lang, $"{zImp.PhaseCount}-phase lateral: no sequence values", $"lateral {zImp.PhaseCount} fasa: tanpa nilai urutan");

        // ---- OpenDSS listing --------------------------------------------------------------------------------------------
        var dssLines = DistributionTestCases.Ieee13NodeDss.Replace("\r\n", "\n").Split('\n');
        string dssText = string.Join('\n', dssLines.Take(24)) + $"\n…  ({dssLines.Length - 24} more lines)";

        var min = r.MinVoltage;
        var kpis = new List<Kpi>
        {
            new(new("Power flow", "Aliran daya"), r.Converged ? "OK" : "FAIL", null, Same($"{r.Method} · {r.Iterations} it"), r.Converged ? "ok" : "alarm"),
            new(new("Source", "Sumber"), F(r.TotalSourceKva.Real, "0"), "kW", Same($"{F(r.TotalSourceKva.Imaginary, "0")} kvar")),
            new(new("Losses", "Rugi-rugi"), F(r.LossesKw, "0.0"), "kW"),
            new(new("Worst error vs IEEE", "Galat terbesar thd IEEE"), F(100 * worst, "0.000"), "%", Same($"{Pick(lang, "bus", "bus")} {worstBus}"),
                Math.Abs(mult - 1) > 1e-9 ? null : worst < 0.005 ? "ok" : "warn"),
            new(new("Max unbalance", "Ketidakseimbangan maks"), F(100 * r.MaxVoltageUnbalance, "0.00"), "%", Same($"Vmin {F(min.Vpu, "0.0000")} @ {min.Bus}.{"ABC"[Math.Max(0, min.Phase)]}"),
                r.MaxVoltageUnbalance > 0.02 ? "warn" : "ok"),
        };

        var blocks = new List<Block>
        {
            new ChartBlock(new("Per-phase voltage profile", "Profil tegangan per fasa"), profile,
                new("solid = computed, dashed = IEEE published", "garis = hasil hitung, putus-putus = terbitan IEEE"), Wide: true),
            new TableBlock(new("Bus voltages, pu (IEEE in brackets)", "Tegangan bus, pu (IEEE dalam kurung)"),
                [Same("Bus"), new("Phase A", "Fasa A"), new("Phase B", "Fasa B"), new("Phase C", "Fasa C"), new("Max error", "Galat maks"), Same("VUF %")], rows,
                Math.Abs(mult - 1) > 1e-9 ? new("load multiplier ≠ 1: published values are for nominal load", "pengali beban ≠ 1: nilai terbitan untuk beban nominal") : null, Wide: true),
            new ChartBlock(new("Voltage unbalance", "Ketidakseimbangan tegangan"), unb, new("IEC VUF and NEMA MG-1 line-voltage unbalance", "VUF IEC dan ketidakseimbangan tegangan saluran NEMA MG-1")),
            new TableBlock(new("Regulator taps (A / B / C)", "Tap regulator (A / B / C)"), [new("Regulator", "Regulator"), Same("Tap"), new("Ratio", "Rasio")], regRows,
                auto ? new("LDC: 122 V, 2 V band, PT 20, CT 700 A, R' 3 V, X' 9 V", "LDC: 122 V, pita 2 V, PT 20, CT 700 A, R' 3 V, X' 9 V") : new("fixed at the published positions", "tetap pada posisi terbitan")),
            new TableBlock(new($"Config {cfg}: Carson + Kron z_abc (Ω/mile)", $"Konfigurasi {cfg}: Carson + Kron z_abc (Ω/mile)"), [Same(""), Same("a"), Same("b"), Same("c")], zRows,
                Same($"{desc[lang]} · {seqNote}"), Wide: true),
            new TableBlock(new($"Config {cfg}: IEEE data sheet (Ω/mile)", $"Konfigurasi {cfg}: lembar data IEEE (Ω/mile)"), [Same(""), Same("a"), Same("b"), Same("c")], sRows,
                new($"max |Δz| vs computed {F(dev, "0.0000")} Ω/mile", $"maks |Δz| thd hasil hitung {F(dev, "0.0000")} Ω/mile"), Wide: true),
            new ListingBlock(new("OpenDSS script (IEEE 13-node)", "Skrip OpenDSS (IEEE 13 node)"), dssText, "text",
                new($"OpenDssReader.Parse → {net.Buses.Count} buses, {net.Lines.Count} lines, {net.Regulators.Count} regulators", $"OpenDssReader.Parse → {net.Buses.Count} bus, {net.Lines.Count} saluran, {net.Regulators.Count} regulator"), Wide: true),
        };
        if (net.Warnings.Count > 0)
            blocks.Add(new NoteBlock(Same(string.Join(" · ", net.Warnings))));
        blocks.Add(new NoteBlock(new("Phase-frame ladder (backward/forward sweep) power flow with 3×3 line matrices from the modified Carson equations and Kron reduction, wye/delta ZIP loads, step regulators and three-phase transformers. Configurations 601–607 are recomputed from conductor data and pole/cable geometry and land on the IEEE data sheet within 0.0001 Ω/mile.",
            "Aliran daya ladder kerangka fasa (sapuan maju/mundur) dengan matriks saluran 3×3 dari persamaan Carson termodifikasi dan reduksi Kron, beban ZIP bintang/delta, regulator bertingkat, dan trafo tiga fasa. Konfigurasi 601–607 dihitung ulang dari data konduktor dan geometri tiang/kabel dan sesuai dengan lembar data IEEE dalam 0,0001 Ω/mil.")));
        return new InteractiveResult(kpis, blocks);
    }
}
