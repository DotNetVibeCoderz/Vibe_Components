using System.Globalization;
using Electrical.Circuit;
using Electrical.Core.Network;
using Electrical.Harmonic;
using Electrical.PowerFlow;
using Electrical.Protection;
using Electrical.ShortCircuit;
using Electrical.Stability;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

// Declarative demos for Electrical.ShortCircuit, Protection, Stability, Harmonic and Circuit.

/// <summary>IEC 60909 short-circuit scan and a single fault in detail.</summary>
public sealed class ShortCircuitDemo : IInteractiveScenario
{
    public string Id => "shortcircuit";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("case", new("Test system", "Sistem uji"), [("case14", Same("IEEE 14-bus")), ("case9", Same("WSCC 9-bus"))], "case14"),
        new ChoiceParam("type", new("Fault", "Gangguan"),
            [("ThreePhase", new("3-phase", "3 fasa")), ("SingleLineToGround", new("1-phase–ground", "1 fasa–tanah")), ("LineToLine", new("phase–phase", "fasa–fasa")), ("DoubleLineToGround", new("2-phase–ground", "2 fasa–tanah"))],
            "SingleLineToGround"),
        new RangeParam("bus", new("Faulted bus", "Bus gangguan"), 1, v.Get("case", "case14") == "case9" ? 9 : 14, 1, 4, "0"),
    ];

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var net = GalleryCases.Load(v["case"]);
        int bus = Math.Clamp(v.I("bus"), 1, net.BusCount);
        var type = Enum.Parse<FaultType>(v["type"]);
        var scan = ShortCircuitAnalysis.ScanAllBuses(net);
        var f = ShortCircuitAnalysis.Calculate(net, bus, type);

        string scanSvg = SvgCharts.Bar(scan.Select(s => s.BusId.ToString(CultureInfo.InvariantCulture)).ToList(),
        [
            new ChartSeries("I\"k3", scan.Select(s => s.Ik3Ka).ToList(), SvgTheme.Var("eln-accent")),
            new ChartSeries("I\"k1", scan.Select(s => s.Ik1Ka).ToList(), SvgTheme.Var("eln-amber")),
        ], new ChartOptions { Width = 640, Height = 260, YLabel = "kA", XLabel = "bus", ValueFormat = "0.0" });

        var vs = f.Voltages;
        string vSvg = SvgCharts.Line(
        [
            new ChartSeries("Va", vs.Select(x => x.Va).ToList(), SvgTheme.Var("eln-series-2")),
            new ChartSeries("Vb", vs.Select(x => x.Vb).ToList(), SvgTheme.Var("eln-series-3")),
            new ChartSeries("Vc", vs.Select(x => x.Vc).ToList(), SvgTheme.Var("eln-series-6")),
        ], new ChartOptions { Width = 640, Height = 260, YLabel = "|V| (pu)", XLabel = "bus", YMin = 0, XLabels = vs.Select(x => x.BusId.ToString(CultureInfo.InvariantCulture)).ToList() });

        var branches = f.Branches.OrderByDescending(b => b.MaxKa).Take(8)
            .Select(b => new Cell[] { $"{b.From}–{b.To}", Cell.Num(b.IaKa, "0.000"), Cell.Num(b.IbKa, "0.000"), Cell.Num(b.IcKa, "0.000"), Cell.Num(b.ResidualKa, "0.000") }).ToList();
        var sources = f.Sources.Select(s => new Cell[] { s.Name, s.BusId.ToString(CultureInfo.InvariantCulture), Cell.Num(s.IaKa, "0.000"), Cell.Num(s.IbKa, "0.000"), Cell.Num(s.IcKa, "0.000") }).ToList();

        var kpis = new List<Kpi>
        {
            new(new("Initial I\"k", "I\"k awal"), F(f.IkKa, "0.00"), "kA", Same($"{TypeName(type, lang)} @ bus {bus} · {F(f.BaseKv, "0.#")} kV"), "alarm"),
            new(Same("Peak ip"), F(f.IpKa, "0.00"), "kA", Same($"κ = {F(f.Kappa, "0.000")} · R/X {F(f.RxRatio, "0.000")}")),
            new(new("Breaking Ib", "Pemutusan Ib"), F(f.IbKa, "0.00"), "kA", Same($"Idc {F(f.IdcKa, "0.00")} kA")),
            new(new("Thermal Ith", "Termal Ith"), F(f.IthKa, "0.00"), "kA", new("1 s", "1 dtk")),
            new(Same("S\"k"), F(f.SkMva, "0"), "MVA", Same($"c = {F(f.C, "0.00")}")),
            new(new("Lowest voltage", "Tegangan terendah"), F(vs.Min(x => x.Min), "0.000"), "pu", new("any phase, any bus", "fasa & bus mana pun"), "warn"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Fault level at every bus", "Level gangguan tiap bus"), scanSvg, new("IEC 60909, c max", "IEC 60909, c maks")),
            new ChartBlock(new("Phase voltages during the fault", "Tegangan fasa saat gangguan"), vSvg, Same($"{TypeName(type, lang)} @ bus {bus}")),
            new TableBlock(new("Branch fault currents", "Arus gangguan cabang"), [new("Branch", "Cabang"), Same("Ia kA"), Same("Ib kA"), Same("Ic kA"), Same("3I0 kA")], branches, new("top 8", "8 teratas")),
            new TableBlock(new("Source contributions", "Kontribusi sumber"), [new("Source", "Sumber"), Same("Bus"), Same("Ia kA"), Same("Ib kA"), Same("Ic kA")], sources),
        ]);
    }

    private static string TypeName(FaultType t, string lang) => t switch
    {
        FaultType.ThreePhase => Pick(lang, "3-phase", "3 fasa"),
        FaultType.SingleLineToGround => Pick(lang, "1-ph–ground", "1 fasa–tanah"),
        FaultType.LineToLine => Pick(lang, "ph–ph", "fasa–fasa"),
        _ => Pick(lang, "2-ph–ground", "2 fasa–tanah"),
    };
}

/// <summary>Overcurrent relay grading on a radial 11 kV feeder (TCC) and an IEEE 1584 arc-flash calculation.</summary>
public sealed class ProtectionDemo : IInteractiveScenario
{
    public string Id => "protection";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("curve", new("Curve", "Kurva"),
            [("IecStandardInverse", Same("IEC SI")), ("IecVeryInverse", Same("IEC VI")), ("IecExtremelyInverse", Same("IEC EI")), ("IeeeVeryInverse", Same("IEEE VI"))], "IecStandardInverse"),
        new RangeParam("cti", new("Grading margin", "Margin koordinasi"), 0.2, 0.5, 0.05, 0.3, "0.00", " s"),
        new RangeParam("ka", new("Bolted fault at LV board", "Gangguan di panel TR"), 5, 50, 1, 25, "0", " kA"),
    ];

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var curve = Enum.Parse<CurveType>(v["curve"]);
        double cti = v.D("cti");
        // Relays from the load end towards the source: pickup, max fault at the relay, fault at the next downstream relay.
        var r1 = new OvercurrentRelay("R1 feeder end", 200, curve);
        var r2 = new OvercurrentRelay("R2 mid feeder", 400, curve);
        var r3 = new OvercurrentRelay("R3 substation", 600, curve);
        var grading = Coordination.GradeTimeMultipliers(
        [
            new GradingStage(r1, 3000, 3000), new GradingStage(r2, 5000, 3000), new GradingStage(r3, 8000, 5000),
        ], cti);
        OvercurrentRelay[] relays = [r1, r2, r3];
        foreach (var relay in relays) relay.TimeMultiplier = grading.Rows.First(row => row.Relay == relay.Name).TimeMultiplier;
        var fuse = Fuse.FromI2t("F1 63 A fuse", 63, 40_000);

        var curves = new List<TccCurve> { TccCurve.FromFunction(fuse.Name, i => fuse.OperatingTime(i), 100, 10_000, color: SvgTheme.Var("eln-series-8")) with { Dashed = true } };
        string[] colors = [SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-series-2"), SvgTheme.Var("eln-series-3")];
        for (int i = 0; i < relays.Length; i++)
        {
            var r = relays[i];
            curves.Add(TccCurve.FromFunction($"{r.Name} · TMS {F(r.TimeMultiplier, "0.00")}", x => r.OperatingTime(x), r.PickupA * 1.05, 20_000, color: colors[i]));
        }
        string tcc = SvgCharts.Tcc(curves,
            [new TccMarker(3000, "If R1", SvgTheme.Var("eln-muted")), new TccMarker(5000, "If R2", SvgTheme.Var("eln-muted")), new TccMarker(8000, "If R3", SvgTheme.Var("eln-danger"))],
            new TccOptions { Width = 640, Height = 460, CurrentMin = 50, CurrentMax = 20_000, TimeMin = 0.01, TimeMax = 100 });

        var rows = grading.Rows.Select(r => new Cell[]
        {
            r.Relay, Cell.Num(r.PickupA, "0"), Cell.Num(r.TimeMultiplier, "0.00"), Cell.Num(r.MaxFaultCurrentA, "0"), Cell.Num(r.TimeAtMaxFaultS, "0.000"),
            r.MarginS is { } m ? new Cell(F(m, "0.000") + " s", m >= cti - 1e-6 ? "ok" : "alarm") : Cell.Chip("—", "na"),
        }).ToList();

        // Arc flash on the 0.48 kV board, cleared by an LV breaker modelled as an IEC very-inverse relay with a 0.05 s instantaneous element.
        var lvBreaker = new OvercurrentRelay("LV main", 2000, CurveType.IecVeryInverse, 0.2) { InstantaneousPickupA = 15_000, InstantaneousDelayS = 0.05 };
        var arc = ArcFlash.Calculate(new ArcFlashInput(0.48, v.D("ka"), 0, 610, ArcEquipment.Switchgear), lvBreaker, breakerOpeningS: 0.05);
        string tone = arc.PpeCategory switch { <= 1 and >= 0 => "ok", 2 => "warn", _ => "alarm" };

        var kpis = new List<Kpi>
        {
            new(new("Coordination", "Koordinasi"), grading.AllCoordinated ? Pick(lang, "SELECTIVE", "SELEKTIF") : Pick(lang, "OVERLAP", "TUMPANG TINDIH"), null,
                Same($"CTI {F(cti, "0.00")} s"), grading.AllCoordinated ? "ok" : "alarm"),
            new(new("R3 at 8 kA", "R3 pada 8 kA"), F(r3.OperatingTime(8000), "0.000"), "s", Same($"TMS {F(r3.TimeMultiplier, "0.00")}")),
            new(new("Arcing current", "Arus busur"), F(arc.ArcingCurrentKa, "0.0"), "kA", Same($"{arc.Model}")),
            new(new("Clearing time", "Waktu pemutusan"), F(arc.ArcDurationS * 1000, "0"), "ms"),
            new(new("Incident energy", "Energi insiden"), F(arc.IncidentEnergyCalCm2, "0.0"), "cal/cm²", Same($"@ 610 mm · AFB {F(arc.ArcFlashBoundaryMm, "0")} mm"), tone),
            new(new("PPE category", "Kategori APD"), arc.PpeCategory < 0 ? "N/A" : arc.PpeCategory.ToString(CultureInfo.InvariantCulture), null, Same(arc.PpeDescription), tone),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Time–current curves", "Kurva waktu–arus"), tcc, new("log–log, 11 kV radial feeder", "log–log, penyulang radial 11 kV")),
            new TableBlock(new("Relay grading", "Penyetelan relai"), [new("Relay", "Relai"), Same("Is A"), Same("TMS"), Same("If max A"), Same("t @ If s"), new("Margin", "Margin")], rows,
                new("time multipliers solved for the margin", "TMS dihitung untuk margin")),
            new NoteBlock(new($"Arc flash: IEEE 1584 switchgear at 0.48 kV, {F(v.D("ka"), "0")} kA bolted, 610 mm working distance; the duration is the LV breaker clearing time at the arcing current (and 85 % of it).",
                              $"Arc flash: IEEE 1584 switchgear 0,48 kV, gangguan {F(v.D("ka"), "0")} kA, jarak kerja 610 mm; durasi = waktu pemutusan pemutus TR pada arus busur (dan 85 %-nya).")),
        ]);
    }
}

/// <summary>Transient stability (rotor angles + CCT) on WSCC 9-bus, PV curve and small-signal modes.</summary>
public sealed class StabilityDemo : IInteractiveScenario
{
    public string Id => "stability";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("bus", new("Fault bus", "Bus gangguan"), [("7", Same("Bus 7")), ("9", Same("Bus 9")), ("5", Same("Bus 5")), ("4", Same("Bus 4"))], "7"),
        new RangeParam("tc", new("Clearing time", "Waktu pemutusan"), 0.04, 0.5, 0.01, 0.12, "0.00", " s"),
    ];

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var net = TestCases.Case9();
        int bus = int.Parse(v["bus"], CultureInfo.InvariantCulture);
        double tc = v.D("tc");
        var model = ClassicalModel.FromPowerFlow(net);
        var sim = TransientStability.SimulateFault(model, bus, tc, options: new TransientStabilityOptions { EndTimeS = 2.5, StopWhenUnstable = true });
        double cct = TransientStability.CriticalClearingTime(model, bus, maxS: 0.6, toleranceS: 0.002);

        int stride = Math.Max(1, sim.Time.Length / 400);
        var xs = sim.Time.Where((_, i) => i % stride == 0).ToList();
        string[] colors = [SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-series-2"), SvgTheme.Var("eln-series-3")];
        var series = sim.Machines.Select((m, k) => new ChartSeries(m.Name, sim.AngleCoiDeg[k].Where((_, i) => i % stride == 0).ToList(), colors[k % 3]) { X = xs }).ToList();
        string angles = SvgCharts.Line(series, new ChartOptions { Width = 640, Height = 280, XLabel = Pick(lang, "time (s)", "waktu (dtk)"), YLabel = Pick(lang, "rotor angle vs COI (°)", "sudut rotor thd COI (°)") });

        var pv = VoltageStability.PvCurve(TestCases.Case9());
        int[] loadBuses = [5, 7, 9];
        string[] lc = [SvgTheme.Var("eln-series-6"), SvgTheme.Var("eln-series-2"), SvgTheme.Var("eln-series-5")];
        string pvSvg = SvgCharts.Line(loadBuses.Select((b, i) => new ChartSeries($"Bus {b}", pv.Voltage(b).ToList(), lc[i]) { X = pv.TotalLoadMw }).ToList(),
            new ChartOptions { Width = 640, Height = 280, XLabel = Pick(lang, "total load (MW)", "beban total (MW)"), YLabel = "|V| (pu)", ReferenceLines = [(0.9, "0.9")] });

        var ss = SmallSignal.Analyze(net);
        var modes = ss.OscillatoryModes.OrderBy(m => m.FrequencyHz).Select(m => new Cell[]
        {
            Cell.Num(m.Eigenvalue.Real, "0.000"), Cell.Num(m.Eigenvalue.Imaginary, "0.000"), Cell.Num(m.FrequencyHz, "0.000"),
            new(F(100 * m.DampingRatio, "0.00") + " %", m.DampingRatio < 0 ? "alarm" : m.DampingRatio < 0.05 ? "warn" : "ok"),
        }).ToList();

        var kpis = new List<Kpi>
        {
            new(new("Transient stability", "Stabilitas transien"), sim.Stable ? Pick(lang, "STABLE", "STABIL") : Pick(lang, "LOSS OF SYNC", "LEPAS SINKRON"), null,
                Same($"bus {bus} · tc {F(tc * 1000, "0")} ms"), sim.Stable ? "ok" : "alarm"),
            new(new("Critical clearing time", "Waktu pemutusan kritis"), F(cct * 1000, "0"), "ms", new("bisection on the full simulation", "biseksi simulasi penuh"), tc < cct ? "ok" : "warn"),
            new(new("Max angle swing", "Ayunan sudut maks"), F(sim.MaxAngleDeviationDeg, "0"), "°"),
            new(new("Loadability", "Batas pembebanan"), F(pv.LambdaMax, "0.00"), "λ", Same($"+{F(pv.MarginMw, "0")} MW · bus {pv.CriticalBusId}")),
            new(new("Min damping", "Redaman min"), F(Math.Round(100 * ss.MinimumDampingRatio, 1) + 0.0, "0.0"), "%", Same($"{ss.OscillatoryModes.Count()} modes"), ss.IsStable() ? "ok" : "alarm"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Rotor angles after the fault", "Sudut rotor setelah gangguan"), angles, Same($"3-phase @ bus {bus}, cleared {F(tc, "0.00")} s"), Wide: true),
            new ChartBlock(new("PV (nose) curves", "Kurva PV (hidung)"), pvSvg, new("continuation power flow", "aliran daya kontinuasi")),
            new TableBlock(new("Electromechanical modes", "Mode elektromekanis"), [Same("σ"), Same("ω rad/s"), Same("f Hz"), new("Damping", "Redaman")], modes, new("classical model eigenvalues", "nilai eigen model klasik")),
        ]);
    }
}

/// <summary>Harmonic source spectra, driving-point frequency scan with capacitor resonance and a harmonic load flow.</summary>
public sealed class HarmonicDemo : IInteractiveScenario
{
    public string Id => "harmonic";
    private static readonly IReadOnlyList<HarmonicSourceSpectrum> Spectra = TypicalSpectra.All();

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("src", new("Nonlinear load", "Beban nonlinier"), Spectra.Select((s, i) => (i.ToString(CultureInfo.InvariantCulture), Same(Short(s.Name)))).ToList(), "2"),
        new RangeParam("cap", new("Capacitor at bus 9", "Kapasitor di bus 9"), 0, 60, 2, 19, "0", " Mvar"),
    ];

    private static string Short(string name) => name.Split('(')[0].Trim();

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var spec = Spectra[int.Parse(v["src"], CultureInfo.InvariantCulture)];
        var net = TestCases.Case14();
        net.GetBus(9).Bs = v.D("cap");

        var comps = spec.Components.Where(c => c.Order >= 2 && c.Order <= 25).ToList();
        string specSvg = SvgCharts.Bar(comps.Select(c => c.Order.ToString(CultureInfo.InvariantCulture)).ToList(),
            [new ChartSeries("%", comps.Select(c => c.MagnitudePercent).ToList(), SvgTheme.Var("eln-amber"))],
            new ChartOptions { Width = 640, Height = 240, XLabel = Pick(lang, "harmonic order", "orde harmonik"), YLabel = Pick(lang, "% of fundamental", "% dari fundamental"), ShowLegend = false, ValueFormat = "0.#" });

        var scan = FrequencyScan.DrivingPoint(net, 9, 1, 25, 0.05);
        var res = FrequencyScan.FindResonances(scan);
        string scanSvg = SvgCharts.Line([new ChartSeries("|Z|", scan.Select(p => p.ImpedanceOhm).ToList(), SvgTheme.Var("eln-accent")) { X = scan.Select(p => p.Order).ToList(), Area = true }],
            new ChartOptions { Width = 640, Height = 240, XLabel = Pick(lang, "harmonic order", "orde harmonik"), YLabel = "|Z| (Ω)", ShowLegend = false });

        var hlf = HarmonicLoadFlow.Run(net, [new HarmonicInjection(9, spec), new HarmonicInjection(14, spec)]);
        string thdSvg = SvgCharts.Bar(net.Buses.Select(b => b.Id.ToString(CultureInfo.InvariantCulture)).ToList(),
            [new ChartSeries("THDv %", net.Buses.Select(b => hlf.ThdPercent(b.Id)).ToList(), SvgTheme.Var("eln-series-3"))],
            new ChartOptions { Width = 640, Height = 240, XLabel = "bus", YLabel = "THDv (%)", ShowLegend = false, ValueFormat = "0.0", ReferenceLines = [(5, "IEEE 519")] });

        var checks = hlf.CheckIeee519();
        var rows = checks.Select(c => new Cell[]
        {
            c.BusId.ToString(CultureInfo.InvariantCulture), Cell.Num(c.Result.TotalMeasured, "0.00"), Cell.Num(c.Result.TotalLimit, "0.0"),
            c.Result.Compliant ? Cell.Chip(Pick(lang, "pass", "lulus"), "ok") : Cell.Chip($"{c.Result.Violations.Count()} {Pick(lang, "viol.", "langgar")}", "alarm"),
        }).ToList();

        var worst = hlf.WorstBus;
        var mainRes = res.Where(r => r.Parallel).OrderByDescending(r => r.ImpedanceOhm).FirstOrDefault();
        int failing = checks.Count(c => !c.Result.Compliant);
        var kpis = new List<Kpi>
        {
            new(new("Source THDi", "THDi sumber"), F(spec.ThdPercent, "0.0"), "%", Same(Short(spec.Name))),
            new(new("Parallel resonance", "Resonansi paralel"), mainRes is null ? "—" : F(mainRes.Order, "0.00"), "h",
                mainRes is null ? null : Same($"{F(mainRes.FrequencyHz, "0")} Hz · {F(mainRes.ImpedanceOhm, "0")} Ω"),
                mainRes is not null && comps.Any(c => Math.Abs(c.Order - mainRes.Order) < 0.6) ? "alarm" : "ok"),
            new(new("Worst bus THDv", "THDv bus terburuk"), F(worst.ThdPercent, "0.00"), "%", Same($"bus {worst.BusId}"), worst.ThdPercent > 5 ? "alarm" : worst.ThdPercent > 3 ? "warn" : "ok"),
            new(Same("IEEE 519"), failing == 0 ? Pick(lang, "COMPLIANT", "MEMENUHI") : $"{failing} {Pick(lang, "BUSES FAIL", "BUS GAGAL")}", null, null, failing == 0 ? "ok" : "alarm"),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Injected current spectrum", "Spektrum arus injeksi"), specSvg, Same(spec.Name)),
            new ChartBlock(new("Frequency scan at bus 9", "Pindai frekuensi di bus 9"), scanSvg, new("driving-point impedance", "impedansi titik penggerak")),
            new ChartBlock(new("Voltage distortion per bus", "Distorsi tegangan per bus"), thdSvg, new("sources at buses 9 and 14", "sumber di bus 9 dan 14")),
            new TableBlock(new("IEEE 519 voltage check", "Pemeriksaan tegangan IEEE 519"), [Same("Bus"), Same("THDv %"), new("Limit %", "Batas %"), Same("")], rows),
        ]);
    }
}

/// <summary>Series RLC circuit from a SPICE netlist: step response (transient) and Bode plot (AC sweep).</summary>
public sealed class CircuitDemo : IInteractiveScenario
{
    public string Id => "circuit";

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new RangeParam("r", new("Resistance R", "Resistansi R"), 1, 200, 1, 20, "0", " Ω"),
        new RangeParam("l", new("Inductance L", "Induktansi L"), 1, 50, 1, 10, "0", " mH"),
        new RangeParam("c", new("Capacitance C", "Kapasitansi C"), 1, 100, 1, 10, "0", " µF"),
    ];

    public static string NetlistText(double r, double lmH, double cuF) => string.Create(CultureInfo.InvariantCulture, $"""
        * Series RLC driven by a 10 V step
        V1 in 0 PULSE(0 10 0.5m 1u 1u 1 2) AC 1
        R1 in a {r}
        L1 a b {lmH}m
        C1 b 0 {cuF}u
        .tran 20u 20m
        .ac dec 40 10 100k
        .end
        """);

    public InteractiveResult Run(ParamValues v, string lang)
    {
        double r = v.D("r"), l = v.D("l") * 1e-3, c = v.D("c") * 1e-6;
        string text = NetlistText(v.D("r"), v.D("l"), v.D("c"));
        var circuit = Netlist.Parse(text);

        var tr = circuit.Transient(0.02, 2e-5);
        var t = tr.Time.Select(x => x * 1000).ToList();
        var vc = tr.Voltage("b");
        var i = tr.Current("L1").Select(x => x * 1000).ToList();
        string tranSvg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "v(C) V", "v(C) V"), vc, SvgTheme.Var("eln-accent")) { X = t },
            new ChartSeries("v(in) V", tr.Voltage("in"), SvgTheme.Var("eln-muted")) { X = t, Dashed = true, StrokeWidth = 1.4 },
        ], new ChartOptions { Width = 640, Height = 260, XLabel = Pick(lang, "time (ms)", "waktu (md)"), YLabel = "V" });
        string curSvg = SvgCharts.Line([new ChartSeries("i(L) mA", i, SvgTheme.Var("eln-amber")) { X = t }],
            new ChartOptions { Width = 640, Height = 200, XLabel = Pick(lang, "time (ms)", "waktu (md)"), YLabel = "mA", ShowLegend = false });

        var ac = circuit.AcSweep(10, 100_000, 30);
        var bode = ac.Bode("b", "in");
        var logF = bode.Select(p => Math.Log10(p.FrequencyHz)).ToList();
        string bodeSvg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "gain dB", "penguatan dB"), bode.Select(p => p.MagnitudeDb).ToList(), SvgTheme.Var("eln-accent")) { X = logF },
            new ChartSeries(Pick(lang, "phase /10 °", "fasa /10 °"), bode.Select(p => p.PhaseDeg / 10).ToList(), SvgTheme.Var("eln-series-3")) { X = logF, Dashed = true },
        ], new ChartOptions { Width = 640, Height = 260, XLabel = "log₁₀ f (Hz)", YLabel = "dB" });

        double w0 = 1 / Math.Sqrt(l * c), zeta = r / 2 * Math.Sqrt(c / l), f0 = w0 / (2 * Math.PI);
        double finalV = 10, peak = vc.Max();
        double overshoot = Math.Max(0, (peak - finalV) / finalV * 100);
        var kpis = new List<Kpi>
        {
            new(new("Resonance f₀", "Resonansi f₀"), F(f0, "0"), "Hz"),
            new(new("Damping ζ", "Redaman ζ"), F(zeta, "0.000"), null, zeta < 1 ? new("underdamped", "kurang teredam") : zeta > 1 ? new("overdamped", "lewat teredam") : new("critical", "kritis"), zeta < 0.3 ? "warn" : "ok"),
            new(new("Quality factor Q", "Faktor kualitas Q"), F(1 / (2 * zeta), "0.00")),
            new(new("Overshoot", "Lonjakan"), F(overshoot, "0.0"), "%", Same($"v(C) peak {F(peak, "0.00")} V"), overshoot > 30 ? "warn" : null),
            new(new("Time steps", "Langkah waktu"), tr.Time.Count.ToString(CultureInfo.InvariantCulture), null, new("trapezoidal MNA", "MNA trapesium")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Step response", "Respons tangga"), tranSvg, new("transient analysis", "analisis transien")),
            new ChartBlock(new("Bode plot v(C)/v(in)", "Plot Bode v(C)/v(in)"), bodeSvg, new("AC sweep 10 Hz – 100 kHz", "sapuan AC 10 Hz – 100 kHz")),
            new ChartBlock(new("Inductor current", "Arus induktor"), curSvg),
            new ListingBlock(new("Netlist", "Netlist"), text, "text", new("parsed by Netlist.Parse", "diurai oleh Netlist.Parse")),
        ]);
    }
}
