using System.Globalization;
using System.Numerics;
using System.Text;
using Electrical.Dynamics;
using Electrical.Emt;
using Electrical.Stability;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

// Declarative demos for Electrical.Dynamics (Kundur two-area system, inverter-based resources) and Electrical.Emt (v1.3).

internal static class Plot
{
    public static readonly string[] Colors =
        [SvgTheme.Var("eln-series-1"), SvgTheme.Var("eln-series-2"), SvgTheme.Var("eln-series-3"), SvgTheme.Var("eln-series-5"), SvgTheme.Var("eln-series-6"), SvgTheme.Var("eln-series-4")];

    /// <summary>Indices of at most <paramref name="max"/> evenly spaced samples (the last one included).</summary>
    public static int[] Pick(int count, int max = 600)
    {
        int stride = Math.Max(1, (int)Math.Ceiling(count / (double)max));
        var idx = new List<int>();
        for (int i = 0; i < count; i += stride) idx.Add(i);
        if (idx[^1] != count - 1) idx.Add(count - 1);
        return idx.ToArray();
    }

    public static ChartSeries Series(string name, double[] time, double[] values, int[] idx, string color, double scale = 1) =>
        new(name, idx.Select(i => values[i] * scale).ToList(), color) { X = idx.Select(i => time[i]).ToList() };

    /// <summary>
    /// Eigenvalues in the upper half of the complex plane (σ horizontal, ω vertical) with constant-damping rays; <paramref name="highlight"/>
    /// eigenvalues get a ring and a label. Points outside the window are left out (counted in the caption).
    /// </summary>
    public static string EigenPlane(IReadOnlyList<Complex> eig, IReadOnlyList<(Complex Lambda, string Label, string Color)> highlight,
        string lang, double sigmaMin = -3, double sigmaMax = 0.5, double omegaMax = 10, int width = 640, int height = 300)
    {
        string fg = SvgTheme.Var("eln-fg"), muted = SvgTheme.Var("eln-muted"), grid = SvgTheme.Var("eln-grid"), accent = SvgTheme.Var("eln-accent"), danger = SvgTheme.Var("eln-danger");
        double l = 54, t = 16, w = width - l - 18, h = height - t - 44;
        double X(double s) => l + (s - sigmaMin) / (sigmaMax - sigmaMin) * w;
        double Y(double o) => t + h - o / omegaMax * h;
        string N(double v) => SvgFormat.N(v);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" class=\"eln-chart\" font-family=\"{SvgTheme.FontStack}\" font-size=\"11\">");
        sb.Append($"<title>{SvgFormat.Esc(Lx.Pick(lang, "Eigenvalues", "Nilai eigen"))}</title>");
        for (double s = Math.Ceiling(sigmaMin); s <= sigmaMax + 1e-9; s += 1)
        {
            sb.Append($"<line x1=\"{N(X(s))}\" y1=\"{N(t)}\" x2=\"{N(X(s))}\" y2=\"{N(t + h)}\" stroke=\"{grid}\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{N(X(s))}\" y=\"{N(t + h + 16)}\" fill=\"{muted}\" text-anchor=\"middle\">{s.ToString("0", CultureInfo.InvariantCulture)}</text>");
        }
        for (double o = 0; o <= omegaMax + 1e-9; o += 2)
        {
            sb.Append($"<line x1=\"{N(l)}\" y1=\"{N(Y(o))}\" x2=\"{N(l + w)}\" y2=\"{N(Y(o))}\" stroke=\"{grid}\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{N(l - 6)}\" y=\"{N(Y(o) + 4)}\" fill=\"{muted}\" text-anchor=\"end\">{o.ToString("0", CultureInfo.InvariantCulture)}</text>");
        }
        // imaginary axis (σ = 0) and constant-damping rays σ = −ζ/√(1−ζ²)·ω
        sb.Append($"<line x1=\"{N(X(0))}\" y1=\"{N(t)}\" x2=\"{N(X(0))}\" y2=\"{N(t + h)}\" stroke=\"{danger}\" stroke-width=\"1.2\"/>");
        foreach (double z in new[] { 0.05, 0.10, 0.20 })
        {
            double k = z / Math.Sqrt(1 - z * z);
            double oEnd = Math.Min(omegaMax, -sigmaMin / k);
            sb.Append($"<line x1=\"{N(X(0))}\" y1=\"{N(Y(0))}\" x2=\"{N(X(-k * oEnd))}\" y2=\"{N(Y(oEnd))}\" stroke=\"{muted}\" stroke-width=\"1\" stroke-dasharray=\"5 4\"/>");
            sb.Append($"<text x=\"{N(X(-k * oEnd) + 4)}\" y=\"{N(Y(oEnd) + 12)}\" fill=\"{muted}\">ζ {(100 * z).ToString("0", CultureInfo.InvariantCulture)} %</text>");
        }
        int shown = 0;
        foreach (var e in eig)
        {
            if (e.Imaginary < -1e-9 || e.Real < sigmaMin || e.Real > sigmaMax || e.Imaginary > omegaMax) continue;
            shown++;
            sb.Append($"<circle cx=\"{N(X(e.Real))}\" cy=\"{N(Y(e.Imaginary))}\" r=\"3\" fill=\"{accent}\" fill-opacity=\"0.75\"/>");
        }
        foreach (var (lam, label, color) in highlight)
        {
            if (lam.Real < sigmaMin || lam.Real > sigmaMax || lam.Imaginary > omegaMax) continue;
            double cx = X(lam.Real), cy = Y(lam.Imaginary);
            sb.Append($"<circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"7\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"/>");
            if (label.Length == 0) continue;
            bool left = cx > l + w * 0.6;
            sb.Append($"<text x=\"{N(left ? cx - 11 : cx + 11)}\" y=\"{N(cy + 4)}\" fill=\"{fg}\" font-weight=\"600\" text-anchor=\"{(left ? "end" : "start")}\">{SvgFormat.Esc(label)}</text>");
        }
        sb.Append($"<line x1=\"{N(l)}\" y1=\"{N(t + h)}\" x2=\"{N(l + w)}\" y2=\"{N(t + h)}\" stroke=\"{muted}\" stroke-width=\"1\"/>");
        sb.Append($"<text x=\"{N(l + w / 2)}\" y=\"{N(height - 6)}\" fill=\"{muted}\" text-anchor=\"middle\" font-weight=\"600\">σ (1/s) · {SvgFormat.Esc(Lx.Pick(lang, $"{shown} of {eig.Count(x => x.Imaginary >= -1e-9)} eigenvalues in view", $"{shown} dari {eig.Count(x => x.Imaginary >= -1e-9)} nilai eigen terlihat"))}</text>");
        sb.Append($"<text x=\"{N(l + 6)}\" y=\"{N(t + 12)}\" fill=\"{muted}\" font-weight=\"600\">ω (rad/s)</text>");
        return sb.Append("</svg>").ToString();
    }
}

/// <summary>Kundur two-area system: fault simulation with detailed machines, exciters and PSS, plus small-signal modes.</summary>
public sealed class DynamicsDemo : IInteractiveScenario
{
    public string Id => "dynamics";

    private static readonly Dictionary<KundurExcitation, Lazy<DynamicSmallSignalResult>> Modes = Enum.GetValues<KundurExcitation>()
        .ToDictionary(e => e, e => new Lazy<DynamicSmallSignalResult>(() => DynamicSmallSignal.Analyze(DynamicTestCases.KundurTwoArea(e).Build())));

    public IReadOnlyList<Param> Params(ParamValues v) =>
    [
        new ChoiceParam("exc", new("Excitation", "Eksitasi"),
            [("const", new("Constant Efd", "Efd konstan")), ("st", new("ESST1A, no PSS", "ESST1A tanpa PSS")), ("pss", new("ESST1A + PSS", "ESST1A + PSS"))], "st"),
        new ChoiceParam("bus", new("Fault location", "Lokasi gangguan"),
            [("7", Same("Bus 7")), ("8", new("Bus 8 (tie midpoint)", "Bus 8 (tengah interkoneksi)")), ("9", Same("Bus 9"))], "8"),
        new ChoiceParam("trip", new("Clearing", "Pemutusan"),
            [("no", new("Fault only", "Gangguan saja")), ("yes", new("+ trip one tie circuit", "+ lepas satu sirkit interkoneksi"))], "no"),
        new RangeParam("tc", new("Fault duration", "Durasi gangguan"), 0.05, 0.25, 0.01, 0.10, "0.00", " s"),
    ];

    private static KundurExcitation Exc(string key) => key switch
    {
        "const" => KundurExcitation.ConstantField,
        "pss" => KundurExcitation.StaticExciterWithPss,
        _ => KundurExcitation.StaticExciter,
    };

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var exc = Exc(v["exc"]);
        int bus = int.Parse(v["bus"], CultureInfo.InvariantCulture);
        double tc = v.D("tc"), tf = 0.5;
        var c = DynamicTestCases.KundurTwoArea(exc);
        var sys = c.Build();
        var events = new List<DynamicEvent> { DynamicEvent.Fault(tf, bus, new Complex(0, 0.01)), DynamicEvent.Clear(tf + tc, bus) };
        string tripName = "";
        if (v["trip"] == "yes")
        {
            var (f, t) = bus == 9 ? (8, 9) : (7, 8);
            events.Add(DynamicEvent.TripBranch(tf + tc, TransientStability.BranchIndex(c.Network, f, t)));
            tripName = $" + {f}–{t}";
        }
        var r = DynamicSimulation.Run(sys, events, new SimulationOptions { EndTimeS = 10, StopWhenUnstable = true });
        var ss = Modes[exc].Value;
        var em = ss.ElectromechanicalModes();
        var inter = em.Count > 0 ? em[0] : null;

        var idx = Plot.Pick(r.Time.Length, 700);
        string time = Pick(lang, "time (s)", "waktu (dtk)");
        ChartOptions Opt(string y, int hgt = 240) => new() { Width = 640, Height = hgt, XLabel = time, YLabel = y };
        List<ChartSeries> Per(double[][] data, double scale = 1) =>
            Enumerable.Range(0, r.UnitNames.Count).Select(k => Plot.Series(r.UnitNames[k], r.Time, data[k], idx, Plot.Colors[k], scale)).ToList();

        string angles = SvgCharts.Line(Per(r.AngleCoiDeg), Opt(Pick(lang, "rotor angle vs COI (°)", "sudut rotor thd COI (°)"), 280));
        string speed = SvgCharts.Line(Per(r.SpeedPu), Opt(Pick(lang, "speed (pu)", "kecepatan (pu)")));
        string vt = SvgCharts.Line(Per(r.TerminalVoltagePu), Opt(Pick(lang, "terminal voltage (pu)", "tegangan terminal (pu)")));
        string efd = SvgCharts.Line(Per(r.FieldVoltagePu), Opt(Pick(lang, "field voltage Efd (pu)", "tegangan medan Efd (pu)")));

        string Kind(ModeInfo m)
        {
            var s = m.SpeedShape;
            if (s.TryGetValue("G1", out var a) && s.TryGetValue("G3", out var b) && Math.Cos(a.Phase - b.Phase) < -0.5 && a.Magnitude > 0.2 && b.Magnitude > 0.2)
                return Pick(lang, "inter-area", "antar-area");
            double a1 = s.Where(p => p.Key is "G1" or "G2").Sum(p => p.Value.Magnitude), a2 = s.Where(p => p.Key is "G3" or "G4").Sum(p => p.Value.Magnitude);
            return a1 >= a2 ? Pick(lang, "local, area 1", "lokal, area 1") : Pick(lang, "local, area 2", "lokal, area 2");
        }
        string Tone(double z) => z < 0 ? "alarm" : z < 0.05 ? "warn" : "ok";
        var rows = em.Select(m => new Cell[]
        {
            Kind(m), Cell.Num(m.FrequencyHz, "0.000"), new(F(100 * m.DampingRatio, "0.0") + " %", Tone(m.DampingRatio)),
            Cell.Num(m.Eigenvalue.Real, "0.000"), Cell.Num(m.Eigenvalue.Imaginary, "0.00"),
            string.Join(", ", m.Dominant.Take(3).Select(d => $"{d.State} {d.Participation:0.00}")),
        }).ToList();

        string locals = Pick(lang, "local ", "lokal ") + string.Join(" / ", em.Skip(1).Select(m => F(m.FrequencyHz, "0.00"))) + " Hz";
        var hl = em.Select((m, i) => (m.Eigenvalue, i == 0 ? Pick(lang, $"inter-area {F(m.FrequencyHz, "0.00")} Hz", $"antar-area {F(m.FrequencyHz, "0.00")} Hz") : i == em.Count - 1 ? locals : "",
            i == 0 ? SvgTheme.Var("eln-danger") : SvgTheme.Var("eln-amber"))).ToList();
        string plane = Plot.EigenPlane(ss.Eigenvalues, hl, lang);

        var kpis = new List<Kpi>
        {
            new(new("Inter-area mode", "Mode antar-area"), inter is null ? "—" : F(100 * inter.DampingRatio, "0.0"), "% ζ",
                Same(inter is null ? "" : $"{F(inter.FrequencyHz, "0.000")} Hz"), inter is null ? null : Tone(inter.DampingRatio)),
            new(new("Small-signal", "Sinyal kecil"), ss.IsStable() ? Pick(lang, "STABLE", "STABIL") : Pick(lang, "UNSTABLE", "TAK STABIL"), null,
                Same($"{ss.States.Count} states · {em.Count} EM modes"), ss.IsStable() ? "ok" : "alarm"),
            new(new("Fault response", "Respons gangguan"), r.Stable ? Pick(lang, "STABLE", "STABIL") : Pick(lang, "LOSS OF SYNC", "LEPAS SINKRON"), null,
                Same($"bus {bus} · {F(tc * 1000, "0")} ms{tripName}"), r.Stable ? "ok" : "alarm"),
            new(new("Max angle deviation", "Deviasi sudut maks"), F(r.MaxAngleDeviationDeg, "0"), "°"),
            new(new("Simulation", "Simulasi"), F(r.Elapsed.TotalMilliseconds, "0"), "ms", Same($"{r.Steps} steps · implicit trapezoidal")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Rotor angles relative to the centre of inertia", "Sudut rotor relatif terhadap pusat inersia"), angles,
                Same($"Kundur 2-area · GENROU · 3φ fault j0.01 pu @ bus {bus}, {F(tf, "0.0")}–{F(tf + tc, "0.00")} s{tripName}"), Wide: true),
            new ChartBlock(new("Rotor speed", "Kecepatan rotor"), speed),
            new ChartBlock(new("Terminal voltage", "Tegangan terminal"), vt),
            new ChartBlock(new("Field voltage", "Tegangan medan"), efd, exc == KundurExcitation.ConstantField ? new("manual excitation: Efd held constant", "eksitasi manual: Efd tetap") : new("ESST1A KA = 200, ceiling ±9 pu", "ESST1A KA = 200, batas ±9 pu")),
            new ChartBlock(new("Eigenvalues (complex plane)", "Nilai eigen (bidang kompleks)"), plane, new("dashed rays: constant damping ratio", "garis putus: rasio redaman konstan")),
            new TableBlock(new("Electromechanical modes", "Mode elektromekanis"),
                [new("Mode", "Mode"), Same("f (Hz)"), new("Damping ζ", "Redaman ζ"), Same("σ (1/s)"), Same("ω (rad/s)"), new("Dominant states (participation)", "Keadaan dominan (partisipasi)")],
                rows, new("linearised detailed model; Kundur: 0.545 Hz ζ ≈ 3 % with manual excitation", "model rinci terlinearisasi; Kundur: 0,545 Hz ζ ≈ 3 % dengan eksitasi manual"), Wide: true),
        ]);
    }
}

/// <summary>Inverter-based resources: REGC/REEC fault ride-through and grid-forming droop vs virtual synchronous machine.</summary>
public sealed class IbrDemo : IInteractiveScenario
{
    public string Id => "ibr";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param>
        {
            new ChoiceParam("view", new("Study", "Studi"), [("frt", new("Fault ride-through (REGC/REEC)", "Fault ride-through (REGC/REEC)")), ("gfm", new("Grid-forming: droop vs VSM", "Grid-forming: droop vs VSM"))], "frt"),
        };
        if (v.Get("view", "frt") == "frt")
        {
            list.Add(new RangeParam("xf", new("Fault reactance", "Reaktansi gangguan"), 0.02, 0.2, 0.01, 0.08, "0.00", " pu"));
            list.Add(new RangeParam("dur", new("Fault duration", "Durasi gangguan"), 0.05, 0.3, 0.01, 0.15, "0.00", " s"));
        }
        else
            list.Add(new RangeParam("dp", new("Load step at bus 3", "Langkah beban di bus 3"), 5, 40, 1, 10, "0", " MW"));
        return list;
    }

    public InteractiveResult Run(ParamValues v, string lang) => v.Get("view", "frt") == "frt" ? Frt(v.D("xf"), v.D("dur"), lang) : Gfm(v.D("dp"), lang);

    private static InteractiveResult Frt(double xf, double dur, string lang)
    {
        var sys = DynamicTestCases.InverterBehindLine().Build();
        double t0 = 0.1;
        var r = DynamicSimulation.Run(sys, [DynamicEvent.Fault(t0, 2, new Complex(0, xf)), DynamicEvent.Clear(t0 + dur, 2)], new SimulationOptions { EndTimeS = 1.5 });
        int u = r.UnitIndex("PV1");
        var idx = Plot.Pick(r.Time.Length, 700);
        string time = Pick(lang, "time (s)", "waktu (dtk)");
        string vi = SvgCharts.Line(
        [
            Plot.Series(Pick(lang, "V terminal", "V terminal"), r.Time, r.TerminalVoltagePu[u], idx, Plot.Colors[0]),
            Plot.Series("Ip", r.Time, r.ActiveCurrentPu[u], idx, Plot.Colors[1]),
            Plot.Series("Iq", r.Time, r.ReactiveCurrentPu[u], idx, Plot.Colors[2]),
        ], new ChartOptions { Width = 640, Height = 280, XLabel = time, YLabel = "pu" });
        string pq = SvgCharts.Line(
        [
            Plot.Series("P (MW)", r.Time, r.ActivePowerMw[u], idx, Plot.Colors[1]),
            Plot.Series("Q (Mvar)", r.Time, r.ReactivePowerMvar[u], idx, Plot.Colors[2]),
        ], new ChartOptions { Width = 640, Height = 240, XLabel = time, YLabel = "MW / Mvar" });
        double tMid = t0 + dur / 2;
        double vDip = r.At(r.TerminalVoltagePu[u], tMid), iq = r.At(r.ReactiveCurrentPu[u], tMid), ip = r.At(r.ActiveCurrentPu[u], tMid);
        double p0 = r.ActivePowerMw[u][0];
        int k90 = Enumerable.Range(0, r.Time.Length).FirstOrDefault(k => r.Time[k] > t0 + dur && r.ActivePowerMw[u][k] >= 0.9 * p0);
        double rec = k90 > 0 ? r.Time[k90] - (t0 + dur) : double.NaN;
        var rows = new[] { 0.0, t0 + 0.01, tMid, t0 + dur + 0.05, t0 + dur + 0.2, 1.5 }.Select(t => new Cell[]
        {
            Cell.Num(t, "0.000"), Cell.Num(r.At(r.TerminalVoltagePu[u], t), "0.000"), Cell.Num(r.At(r.ActiveCurrentPu[u], t), "0.000"),
            Cell.Num(r.At(r.ReactiveCurrentPu[u], t), "0.000"), Cell.Num(r.At(r.ActivePowerMw[u], t), "0.0"), Cell.Num(r.At(r.ReactivePowerMvar[u], t), "0.0"),
        }).ToList();
        var kpis = new List<Kpi>
        {
            new(new("Voltage during dip", "Tegangan saat dip"), F(vDip, "0.00"), "pu", new("REEC dip threshold 0.9 pu", "ambang dip REEC 0,9 pu"), vDip < 0.9 ? "warn" : "ok"),
            new(new("Reactive current", "Arus reaktif"), F(iq, "0.00"), "pu", new("Iq = KQV·(1 − V − 0.05), Q priority", "Iq = KQV·(1 − V − 0,05), prioritas Q")),
            new(new("Active current", "Arus aktif"), F(ip, "0.00"), "pu", new("limited by IMAX and LVACM", "dibatasi IMAX dan LVACM")),
            new(new("Recovery to 90 % P", "Pemulihan ke 90 % P"), double.IsFinite(rec) ? F(rec * 1000, "0") : "—", "ms", new("RRPWR ramp 2 pu/s", "ramp RRPWR 2 pu/dtk")),
            new(new("Simulation", "Simulasi"), F(r.Elapsed.TotalMilliseconds, "0"), "ms", Same($"{r.Steps} steps")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Terminal voltage and converter currents", "Tegangan terminal dan arus konverter"), vi,
                Same($"80 MW REGC_A + REEC_A · 3φ fault j{F(xf, "0.00")} pu at bus 2, {F(t0, "0.0")}–{F(t0 + dur, "0.00")} s"), Wide: true),
            new ChartBlock(new("Active and reactive power", "Daya aktif dan reaktif"), pq),
            new TableBlock(new("Samples", "Sampel"), [new("t (s)", "t (dtk)"), Same("V (pu)"), Same("Ip (pu)"), Same("Iq (pu)"), Same("P (MW)"), Same("Q (Mvar)")], rows),
        ]);
    }

    private static InteractiveResult Gfm(double dp, string lang)
    {
        double t0 = 1;
        DynamicSimulationResult Sim(GridFormingMode m) =>
            DynamicSimulation.Run(DynamicTestCases.GridFormingPair(m).Build(), [DynamicEvent.LoadStep(t0, 3, dp)], new SimulationOptions { EndTimeS = 6 });
        var droop = Sim(GridFormingMode.Droop);
        var vsm = Sim(GridFormingMode.VirtualSynchronousMachine);
        var idx = Plot.Pick(droop.Time.Length, 700);
        string time = Pick(lang, "time (s)", "waktu (dtk)");
        double fn = 50;
        string freq = SvgCharts.Line(
        [
            Plot.Series("GFM1 droop", droop.Time, droop.SpeedPu[0], idx, Plot.Colors[0], fn),
            Plot.Series("GFM2 droop", droop.Time, droop.SpeedPu[1], idx, Plot.Colors[4], fn),
            Plot.Series("GFM1 VSM", vsm.Time, vsm.SpeedPu[0], idx, Plot.Colors[1], fn),
            Plot.Series("GFM2 VSM", vsm.Time, vsm.SpeedPu[1], idx, Plot.Colors[3], fn),
        ], new ChartOptions { Width = 640, Height = 280, XLabel = time, YLabel = Pick(lang, "frequency (Hz)", "frekuensi (Hz)") });
        ChartSeries Dp(string name, DynamicSimulationResult r, int u, string color) =>
            Plot.Series(name, r.Time, r.ActivePowerMw[u].Select(p => p - r.ActivePowerMw[u][0]).ToArray(), idx, color);
        string pw = SvgCharts.Line([Dp("GFM1 droop", droop, 0, Plot.Colors[0]), Dp("GFM2 droop", droop, 1, Plot.Colors[4]), Dp("GFM1 VSM", vsm, 0, Plot.Colors[1]), Dp("GFM2 VSM", vsm, 1, Plot.Colors[3])],
            new ChartOptions { Width = 640, Height = 280, XLabel = time, YLabel = "ΔP (MW)" });
        Cell[] Row(string name, DynamicSimulationResult r)
        {
            double d1 = r.ActivePowerMw[0][^1] - r.ActivePowerMw[0][0], d2 = r.ActivePowerMw[1][^1] - r.ActivePowerMw[1][0];
            double nadir = r.SpeedPu[0].Min() * fn;
            double rocof = 0;
            for (int k = 1; k < r.Time.Length; k++)
                if (r.Time[k] > t0 && r.Time[k] < t0 + 0.5) rocof = Math.Max(rocof, Math.Abs(r.SpeedPu[0][k] - r.SpeedPu[0][k - 1]) * fn / (r.Time[k] - r.Time[k - 1]));
            return [name, Cell.Num(d1, "0.00"), Cell.Num(d2, "0.00"), $"1 : {F(d2 / d1, "0.00")}", Cell.Num(r.SpeedPu[0][^1] * fn, "0.000"), Cell.Num(nadir, "0.000"), Cell.Num(rocof, "0.00")];
        }
        double s1 = droop.ActivePowerMw[0][^1] - droop.ActivePowerMw[0][0], s2 = droop.ActivePowerMw[1][^1] - droop.ActivePowerMw[1][0];
        var kpis = new List<Kpi>
        {
            new(new("Load sharing", "Pembagian beban"), $"1 : {F(s2 / s1, "0.00")}", null, new("MP 0.05 vs 0.025 pu → 1 : 2", "MP 0,05 vs 0,025 pu → 1 : 2"), Math.Abs(s2 / s1 - 2) < 0.05 ? "ok" : "warn"),
            new(new("Steady-state frequency", "Frekuensi tunak"), F(droop.SpeedPu[0][^1] * fn, "0.000"), "Hz", new("on the droop line", "pada garis droop")),
            new(new("Nadir, droop", "Titik terendah, droop"), F(droop.SpeedPu[0].Min() * fn, "0.000"), "Hz"),
            new(new("Nadir, VSM", "Titik terendah, VSM"), F(vsm.SpeedPu[0].Min() * fn, "0.000"), "Hz", new("virtual inertia H = 1 / 2 s", "inersia virtual H = 1 / 2 dtk")),
            new(new("Simulation", "Simulasi"), F((droop.Elapsed + vsm.Elapsed).TotalMilliseconds, "0"), "ms", Same($"2 × {droop.Steps} steps")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Inverter frequency after the load step", "Frekuensi inverter setelah langkah beban"), freq,
                Same($"+{F(dp, "0")} MW at bus 3, t = {F(t0, "0")} s · 2 × 100 MVA grid-forming inverters"), Wide: true),
            new ChartBlock(new("Change in active power", "Perubahan daya aktif"), pw),
            new TableBlock(new("Droop vs virtual synchronous machine", "Droop vs mesin sinkron virtual"),
                [new("Control", "Kendali"), Same("ΔP1 (MW)"), Same("ΔP2 (MW)"), new("Sharing", "Pembagian"), new("f end (Hz)", "f akhir (Hz)"), new("Nadir (Hz)", "Terendah (Hz)"), new("RoCoF (Hz/s)", "RoCoF (Hz/dtk)")],
                [Row("Droop", droop), Row("VSM", vsm)]),
        ]);
    }
}

/// <summary>Electromagnetic transients: line energization overvoltage and capacitor-bank inrush.</summary>
public sealed class EmtDemo : IInteractiveScenario
{
    public string Id => "emt";

    public IReadOnlyList<Param> Params(ParamValues v)
    {
        var list = new List<Param>
        {
            new ChoiceParam("view", new("Study", "Studi"), [("line", new("Line energization", "Energisasi saluran")), ("cap", new("Capacitor bank inrush", "Arus inrush bank kapasitor"))], "line"),
        };
        if (v.Get("view", "line") == "line")
        {
            list.Add(new RangeParam("tc", new("Breaker closing time", "Waktu penutupan pemutus"), 0, 20, 0.5, 5, "0.0", " ms"));
            list.Add(new RangeParam("km", new("Line length", "Panjang saluran"), 25, 400, 25, 150, "0", " km"));
        }
        else
        {
            list.Add(new RangeParam("ctc", new("Breaker closing time", "Waktu penutupan pemutus"), 10, 30, 0.5, 20, "0.0", " ms"));
            list.Add(new RangeParam("mvar", new("Bank size", "Ukuran bank"), 1, 20, 1, 5, "0", " Mvar"));
            list.Add(new RangeParam("ssc", new("Short-circuit level", "Level hubung singkat"), 100, 1000, 50, 250, "0", " MVA"));
        }
        return list;
    }

    public InteractiveResult Run(ParamValues v, string lang) =>
        v.Get("view", "line") == "line" ? Line(v.D("tc") / 1000, v.D("km"), lang) : Cap(v.D("ctc") / 1000, v.D("mvar"), v.D("ssc"), lang);

    private static readonly string[] Ph = ["a", "b", "c"];

    private static InteractiveResult Line(double tc, double km, string lang)
    {
        const double kv = 230, f = 50, dt = 20e-6;
        double vpk = kv * 1000 * Math.Sqrt(2.0 / 3.0), xs = kv * 1000 / (Math.Sqrt(3) * 10_000);
        var net = new EmtNetwork();
        net.AddThreePhaseSource("Grid", "S", kv * 1000, f, l: xs / (2 * Math.PI * f));
        net.AddThreePhaseSwitch("CB", "S", "A", closeTime: tc);
        net.AddThreePhaseLine("Line", "A", "B", 0.03, 0.93e-3, 12.3e-9, 0.30, 3.0e-3, 7.9e-9, km);
        double stop = tc + 0.04;
        var r = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = dt, StopTime = stop });
        double tau = km * Math.Sqrt(0.93e-3 * 12.3e-9);
        var idx = Plot.Pick(r.Time.Length, 900);
        string time = Pick(lang, "time (ms)", "waktu (md)");
        var ms = r.Time.Select(t => t * 1000).ToArray();
        string vb = SvgCharts.Line(Ph.Select((p, k) => Plot.Series($"B.{p}", ms, r.Voltage($"B.{p}").Values, idx, Plot.Colors[k], 1 / vpk)).ToList(),
            new ChartOptions { Width = 640, Height = 300, XLabel = time, YLabel = Pick(lang, "open-end voltage (pu)", "tegangan ujung terbuka (pu)"), ReferenceLines = [(2, "2 pu"), (-2, "−2 pu")] });
        string va = SvgCharts.Line(Ph.Select((p, k) => Plot.Series($"A.{p}", ms, r.Voltage($"A.{p}").Values, idx, Plot.Colors[k], 1 / vpk)).ToList(),
            new ChartOptions { Width = 640, Height = 240, XLabel = time, YLabel = Pick(lang, "sending-end voltage (pu)", "tegangan ujung kirim (pu)") });
        var rows = Ph.Select(p =>
        {
            var ch = r.Voltage($"B.{p}");
            double pk = ch.Peak() / vpk;
            double vClose = Math.Cos(2 * Math.PI * f * tc - Ph.ToList().IndexOf(p) * 2 * Math.PI / 3);
            return new Cell[] { p, Cell.Num(vClose, "0.00"), new(F(pk, "0.00"), pk > 2.5 ? "alarm" : pk > 2.0 ? "warn" : "ok"), Cell.Num(ch.TimeOfPeak() * 1000, "0.00") };
        }).ToList();
        double max = Ph.Max(p => r.Voltage($"B.{p}").Peak()) / vpk;
        var kpis = new List<Kpi>
        {
            new(new("Max overvoltage", "Tegangan lebih maks"), F(max, "0.00"), "pu", new("open receiving end", "ujung terima terbuka"), max > 2.5 ? "alarm" : max > 2.0 ? "warn" : "ok"),
            new(new("Travel time", "Waktu tempuh"), F(tau * 1000, "0.000"), "ms", Same($"{F(km, "0")} km · positive sequence")),
            new(new("Kernel backend", "Backend kernel"), r.Backend, null, new("bit-identical managed fallback", "cadangan managed identik bit")),
            new(new("Run time", "Waktu jalan"), F(r.Elapsed.TotalMilliseconds, "0"), "ms", Same($"{r.Steps} steps · Δt {F(dt * 1e6, "0")} µs")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Receiving-end voltages (open line)", "Tegangan ujung terima (saluran terbuka)"), vb,
                Same($"230 kV · 10 kA source · transposed Bergeron line {F(km, "0")} km · breaker closes at {F(tc * 1000, "0.0")} ms"), Wide: true),
            new ChartBlock(new("Sending-end voltages", "Tegangan ujung kirim"), va),
            new TableBlock(new("Per phase", "Per fasa"), [new("Phase", "Fasa"), new("v at closing (pu)", "v saat menutup (pu)"), new("Peak (pu)", "Puncak (pu)"), new("t peak (ms)", "t puncak (md)")], rows,
                new("closing near a voltage peak gives the highest surge", "menutup dekat puncak tegangan memberi surja tertinggi")),
        ]);
    }

    private static InteractiveResult Cap(double tc, double mvar, double ssc, string lang)
    {
        const double kv = 11, f = 50, dt = 10e-6;
        double vpk = kv * 1000 * Math.Sqrt(2.0 / 3.0), q = mvar * 1e6;
        double ls = kv * 1000 * kv * 1000 / (ssc * 1e6) / (2 * Math.PI * f);
        double c = q / (kv * 1000 * kv * 1000 * 2 * Math.PI * f);
        var net = new EmtNetwork();
        net.AddThreePhaseSource("Grid", "S", kv * 1000, f, r: 2 * Math.PI * f * ls / 15, l: ls);   // source X/R = 15
        net.AddThreePhaseSwitch("CB", "S", "C", closeTime: tc);
        net.AddThreePhaseCapacitorBank("Bank", "C", c);
        var r = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = dt, StopTime = tc + 0.03 });
        double fRing = 1 / (2 * Math.PI * Math.Sqrt(ls * c));
        double iRated = q / (Math.Sqrt(3) * kv * 1000) * Math.Sqrt(2);
        double surge = vpk * Math.Sqrt(c / ls);
        int i0 = Array.FindIndex(r.Time, t => t >= tc - 0.003);
        var all = Plot.Pick(r.Time.Length - i0, 900).Select(i => i + i0).ToArray();
        string time = Pick(lang, "time (ms)", "waktu (md)");
        var ms = r.Time.Select(t => t * 1000).ToArray();
        string ic = SvgCharts.Line(Ph.Select((p, k) => Plot.Series($"i{p}", ms, r.Current($"CB.{p}").Values, all, Plot.Colors[k], 1 / 1000.0)).ToList(),
            new ChartOptions { Width = 640, Height = 280, XLabel = time, YLabel = Pick(lang, "breaker current (kA)", "arus pemutus (kA)") });
        string vc = SvgCharts.Line(Ph.Select((p, k) => Plot.Series($"v{p}", ms, r.Voltage($"C.{p}").Values, all, Plot.Colors[k], 1 / vpk)).ToList(),
            new ChartOptions { Width = 640, Height = 280, XLabel = time, YLabel = Pick(lang, "bus voltage (pu)", "tegangan bus (pu)") });
        double w0 = tc, w1 = tc + 0.01;   // first half cycle after closing
        double iPeak = Ph.Max(p => r.Current($"CB.{p}").Peak(w0, w1));
        double vMax = Ph.Max(p => r.Voltage($"C.{p}").Peak(w0, w1)) / vpk;
        var rows = Ph.Select(p =>
        {
            var ch = r.Current($"CB.{p}");
            return new Cell[] { p, Cell.Num(ch.Peak(w0, w1), "0"), Cell.Num(ch.Peak(w0, w1) / iRated, "0.0"), Cell.Num(r.Voltage($"C.{p}").Peak(w0, w1) / vpk, "0.00"), Cell.Num(ch.TimeOfPeak(w0, w1) * 1000, "0.00") };
        }).ToList();
        var kpis = new List<Kpi>
        {
            new(new("Inrush peak", "Puncak inrush"), F(iPeak, "0"), "A", Same($"{F(iPeak / iRated, "0.0")} × rated peak · V√(C/L) = {F(surge, "0")} A"), iPeak / iRated > 20 ? "alarm" : iPeak / iRated > 5 ? "warn" : "ok"),
            new(new("Ring frequency", "Frekuensi osilasi"), F(fRing, "0"), "Hz", Same("1 / (2π √(L·C))")),
            new(new("Bus overvoltage", "Tegangan lebih bus"), F(vMax, "0.00"), "pu", null, vMax > 1.8 ? "warn" : "ok"),
            new(new("Kernel backend", "Backend kernel"), r.Backend, null, Same($"{r.Steps} steps · {F(r.Elapsed.TotalMilliseconds, "0")} ms")),
        };
        return new InteractiveResult(kpis,
        [
            new ChartBlock(new("Inrush current", "Arus inrush"), ic, Same($"{F(mvar, "0")} Mvar wye-grounded bank · ring ≈ {F(fRing, "0")} Hz")),
            new ChartBlock(new("Bus voltage", "Tegangan bus"), vc, Same($"11 kV · S\"k {F(ssc, "0")} MVA · close at {F(tc * 1000, "0.0")} ms")),
            new TableBlock(new("Per phase", "Per fasa"), [new("Phase", "Fasa"), new("Peak (A)", "Puncak (A)"), new("× rated peak", "× puncak nominal"), new("V peak (pu)", "V puncak (pu)"), new("t peak (ms)", "t puncak (md)")], rows,
                new("first half cycle after closing; closing at the phase-a voltage peak (20 ms) maximises the inrush of phase a", "setengah siklus pertama setelah menutup; menutup pada puncak tegangan fasa a (20 md) memaksimalkan inrush fasa a"), Wide: true),
        ]);
    }
}
