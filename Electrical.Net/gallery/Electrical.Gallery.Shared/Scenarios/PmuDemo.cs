using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Electrical.Core.Network;
using Electrical.Pmu;
using Electrical.Pmu.C37118;
using Electrical.PowerFlow;
using Electrical.Visualization;
using static Electrical.Gallery.Shared.Fmt;
using static Electrical.Gallery.Shared.Lx;

namespace Electrical.Gallery.Shared;

/// <summary>
/// Synchrophasors: a PMU simulator on the IEEE 14-bus power flow publishes an IEEE C37.118.2 data frame (hex dump + decoded fields),
/// the C37.118.1 Annex C estimator (P / M class) is tested at an off-nominal frequency (TVE over time and over Δf), and the decoded
/// frame feeds the linear PMU state estimator for the chosen PMU placement.
/// </summary>
public sealed class PmuDemo : IInteractiveScenario
{
    public string Id => "pmu";

    private static readonly Lazy<PowerFlowResult> Case14 = new(() => PowerFlowSolver.Solve(TestCases.Case14()));
    private static readonly Dictionary<PmuClass, (double[] Df, double[] Tve1, double[] Tve3)> SweepCache = [];
    private static readonly Lock Gate = new();

    private static readonly (string Key, int[] Buses, L Text)[] Placements =
    [
        ("min", [2, 6, 7, 9], Same("2 · 6 · 7 · 9")),
        ("five", [2, 6, 7, 9, 13], Same("2 · 6 · 7 · 9 · 13")),
        ("weak", [2, 6, 9], new("2 · 6 · 9 (unobservable)", "2 · 6 · 9 (tak teramati)")),
        ("all", Enumerable.Range(1, 14).ToArray(), new("every bus", "semua bus")),
    ];

    public IReadOnlyList<Param> Params(ParamValues current) =>
    [
        new ChoiceParam("format", new("Data format", "Format data"),
            [("fpolar", Same("float polar")), ("frect", Same("float rect.")), ("ipolar", Same("int16 polar")), ("irect", Same("int16 rect."))], "fpolar"),
        new RangeParam("df", new("Off-nominal frequency Δf", "Deviasi frekuensi Δf"), -5, 5, 0.25, 1.5, "0.00", " Hz"),
        new ChoiceParam("class", new("Estimator class", "Kelas estimator"), [("P", Same("P (protection)")), ("M", Same("M (measurement)"))], "P"),
        new ChoiceParam("pmus", new("PMU buses", "Bus PMU"), Placements.Select(p => (p.Key, p.Text)).ToList(), "min"),
        new RangeParam("noise", new("Measurement noise", "Derau pengukuran"), 0, 5, 0.5, 1, "0.0", "×"),
    ];

    private static C37DataFormat FormatOf(string key) => key switch
    {
        "frect" => C37DataFormat.FloatRectangular,
        "ipolar" => C37DataFormat.PhasorPolar,
        "irect" => C37DataFormat.Int16Rectangular,
        _ => C37DataFormat.FloatPolar,
    };

    /// <summary>Byte ranges of a C37.118.2 data frame for the configuration (offsets into the frame).</summary>
    private static List<(int Start, int Length, string Field, string Value)> Fields(byte[] f, C37ConfigFrame cfg, C37DataFrame d)
    {
        var list = new List<(int, int, string, string)>
        {
            (0, 2, "SYNC", $"0x{f[0]:X2}{f[1]:X2} · data frame, version {f[1] & 0x0F}"),
            (2, 2, "FRAMESIZE", $"{BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(2))} bytes"),
            (4, 2, "IDCODE", BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4)).ToString(CultureInfo.InvariantCulture)),
            (6, 4, "SOC", d.Time.ToDateTime(cfg.TimeBase).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)),
            (10, 4, "FRACSEC", $"{d.Time.FractionOfSecond} / {cfg.TimeBase} · TQ 0x{d.Time.TimeQuality:X2}"),
        };
        int o = 14;
        for (int k = 0; k < cfg.Pmus.Count; k++)
        {
            var p = cfg.Pmus[k];
            var b = d.Blocks[k];
            string tag = $"[{p.StationName}]";
            list.Add((o, 2, $"{tag} STAT", $"0x{b.RawStatus:X4}"));
            o += 2;
            int ph = (p.Format & C37DataFormat.PhasorFloat) != 0 ? 8 : 4;
            for (int i = 0; i < p.Phasors.Count; i++)
            {
                var x = b.Phasors[i];
                list.Add((o, ph, $"{tag} {p.Phasors[i].Name}", $"{x.Magnitude.ToString("0.###", CultureInfo.InvariantCulture)} ∠ {(x.Phase * 180 / Math.PI).ToString("0.000", CultureInfo.InvariantCulture)}°"));
                o += ph;
            }
            int fr = (p.Format & C37DataFormat.FrequencyFloat) != 0 ? 4 : 2;
            list.Add((o, fr, $"{tag} FREQ", $"{b.FrequencyHz.ToString("0.0000", CultureInfo.InvariantCulture)} Hz"));
            o += fr;
            list.Add((o, fr, $"{tag} DFREQ", $"{b.RocofHzPerS.ToString("0.000", CultureInfo.InvariantCulture)} Hz/s"));
            o += fr;
        }
        ushort crc = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(f.Length - 2));
        ushort calc = CrcCcitt.Compute(f.AsSpan(0, f.Length - 2));
        list.Add((f.Length - 2, 2, "CHK", $"0x{crc:X4} (CRC-CCITT {(crc == calc ? "ok" : "MISMATCH")})"));
        return list;
    }

    private static string HexListing(byte[] f, int maxRows = 18)
    {
        var sb = new StringBuilder();
        var rows = HexDump.Rows(f);
        foreach (var r in rows.Take(maxRows))
            sb.Append(CultureInfo.InvariantCulture, $"{r.Offset:X4}  {HexDump.Spaced(r.Bytes),-47}  {string.Concat(r.Bytes.Select(HexDump.Ascii))}\n");
        if (rows.Count > maxRows) sb.Append(CultureInfo.InvariantCulture, $"…  ({f.Length - maxRows * 16} more bytes)\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Max TVE (single-phase, positive sequence) and FE for a steady off-nominal signal.</summary>
    private static (double Tve1, double Tve3, double Fe, IReadOnlyList<PhasorEstimate> Series, double[] TveSeries) Steady(PhasorEstimator est, double df)
    {
        double f0 = est.Options.NominalFrequencyHz, fs = est.Options.SampleRateHz;
        int n = (int)(fs * 0.6);
        var x = Complex.FromPolarCoordinates(1.0, 0.3);
        var a = PhasorEstimator.Sinusoid(x, f0 + df, fs, n);
        var b = PhasorEstimator.Sinusoid(x * Complex.FromPolarCoordinates(1, -2 * Math.PI / 3), f0 + df, fs, n);
        var c = PhasorEstimator.Sinusoid(x * Complex.FromPolarCoordinates(1, 2 * Math.PI / 3), f0 + df, fs, n);
        var one = est.Estimate(a, 0);
        var three = est.EstimatePositiveSequence(a, b, c, 0);
        Complex Ref(double t) => x * Complex.FromPolarCoordinates(1, 2 * Math.PI * df * t);
        var tve = one.Select(e => PhasorEstimator.Tve(e.Phasor, Ref(e.TimeS))).ToArray();
        double t3 = three.Count == 0 ? double.NaN : three.Max(e => PhasorEstimator.Tve(e.Phasor, Ref(e.TimeS)));
        double fe = three.Count == 0 ? double.NaN : three.Max(e => Math.Abs(e.FrequencyHz - (f0 + df)));
        return (tve.Length == 0 ? double.NaN : tve.Max(), t3, fe, one, tve);
    }

    private static PhasorEstimator Estimator(PmuClass cls) =>
        new(new PhasorEstimatorOptions { NominalFrequencyHz = 50, SampleRateHz = 1000, ReportingRate = 50, Class = cls });

    private static (double[] Df, double[] Tve1, double[] Tve3) Sweep(PmuClass cls)
    {
        lock (Gate)
        {
            if (SweepCache.TryGetValue(cls, out var s)) return s;
            var est = Estimator(cls);
            var df = Enumerable.Range(0, 21).Select(i => -5 + 0.5 * i).ToArray();
            var r = df.Select(d => Steady(est, d)).ToArray();
            s = (df, r.Select(x => x.Tve1 * 100).ToArray(), r.Select(x => x.Tve3 * 100).ToArray());
            SweepCache[cls] = s;
            return s;
        }
    }

    public InteractiveResult Run(ParamValues v, string lang)
    {
        var pf = Case14.Value;
        var net = pf.Network;
        double df = v.D("df"), noise = v.D("noise");
        var placement = Placements.First(p => p.Key == v.Get("pmus", "min"));
        var fmt = FormatOf(v.Get("format", "fpolar"));
        var cls = v.Get("class", "P") == "M" ? PmuClass.M : PmuClass.P;

        // ---- C37.118.2 stream -----------------------------------------------------------------------------------------------
        var sim = new PmuSimulator(pf, placement.Buses, new PmuSimulatorOptions
        {
            Format = fmt, FrequencyOffsetHz = df, VoltageNoise = 0.001 * noise, CurrentNoise = 0.002 * noise, AngleNoiseDeg = 0.05 * noise,
            FrequencyNoiseHz = 0.001 * noise,
        });
        var t = sim.Options.ReferenceTime.AddMilliseconds(20);
        byte[] frame = C37Codec.Encode(sim.CreateFrame(t), sim.Configuration);
        byte[] cfgFrame = C37Codec.Encode(sim.Configuration);
        var decoded = C37Codec.DecodeData(frame, sim.Configuration);
        var fields = Fields(frame, sim.Configuration, decoded);
        var fieldRows = fields.Take(5 + 2 + sim.Configuration.Pmus[0].Phasors.Count + 2)
            .Concat(fields.Count > 9 + sim.Configuration.Pmus[0].Phasors.Count ? [fields[^1]] : [])
            .Select(x => new Cell[] { new($"{x.Start:X4}–{x.Start + x.Length - 1:X4}", null, true), new(x.Length.ToString(CultureInfo.InvariantCulture), null, true), x.Field, x.Value })
            .ToList();

        // ---- phasor estimation ----------------------------------------------------------------------------------------------
        var est = Estimator(cls);
        var steady = Steady(est, df);
        double limitHz = cls == PmuClass.P ? 2 : 5;
        bool inRange = Math.Abs(df) <= limitHz + 1e-9;
        var sweepP = Sweep(PmuClass.P);
        var sweepM = Sweep(PmuClass.M);
        string sweepSvg = SvgCharts.Line(
        [
            new ChartSeries(Pick(lang, "P class, single phase", "kelas P, satu fasa"), sweepP.Tve1, SvgTheme.Var("eln-accent")) { X = sweepP.Df },
            new ChartSeries(Pick(lang, "M class, single phase", "kelas M, satu fasa"), sweepM.Tve1, SvgTheme.Var("eln-amber")) { X = sweepM.Df },
            new ChartSeries(Pick(lang, "P class, positive sequence", "kelas P, urutan positif"), sweepP.Tve3, SvgTheme.Var("eln-ok")) { X = sweepP.Df, Dashed = true },
        ], new ChartOptions
        {
            Width = 640, Height = 260, XLabel = "Δf [Hz]", YLabel = "max TVE [%]", YMin = 0, YMax = 2, ValueFormat = "0.##",
            ReferenceLines = [(1.0, Pick(lang, "C37.118.1 limit 1 %", "batas C37.118.1 1 %"))],
        });
        var times = steady.Series.Select(e => e.TimeS * 1000).ToArray();
        string tveSvg = SvgCharts.Line(
        [
            new ChartSeries("TVE", steady.TveSeries.Select(x => x * 100).ToArray(), SvgTheme.Var("eln-accent")) { X = times, Area = true },
        ], new ChartOptions
        {
            Width = 640, Height = 260, XLabel = Pick(lang, "time [ms]", "waktu [ms]"), YLabel = "TVE [%]", YMin = 0, ShowLegend = false, ValueFormat = "0.###",
            ReferenceLines = steady.Tve1 > 0.5 ? [(1.0, "1 %")] : null,
        });

        // ---- linear state estimation ----------------------------------------------------------------------------------------
        var z = LinearStateEstimator.FromFrame(net, decoded, sim.Bindings, 0.001 * Math.Max(noise, 0.2), 0.002 * Math.Max(noise, 0.2));
        bool observable = LinearStateEstimator.IsObservable(net, z);
        var kpis = new List<Kpi>
        {
            new(new("Data frame", "Frame data"), frame.Length.ToString(CultureInfo.InvariantCulture), "B",
                Same($"{sim.Configuration.Pmus.Count} PMU · {sim.Configuration.Pmus.Sum(p => p.Phasors.Count)} phasors · CFG-2 {cfgFrame.Length} B")),
            new(new("Max TVE", "TVE maks"), F(steady.Tve1 * 100, "0.000"), "%",
                new($"{cls} class, single phase at Δf {F(df, "0.00")} Hz", $"kelas {cls}, satu fasa pada Δf {F(df, "0.00")} Hz"),
                steady.Tve1 < 0.01 ? "ok" : inRange ? "alarm" : "warn"),
            new(new("Frequency error", "Galat frekuensi"), F(steady.Fe * 1000, "0.00"), "mHz", new("positive sequence, limit 5 mHz", "urutan positif, batas 5 mHz"), steady.Fe < 0.005 ? "ok" : "warn"),
        };
        var blocks = new List<Block>
        {
            new ListingBlock(new("C37.118.2 data frame", "Frame data C37.118.2"), HexListing(frame), "text",
                Same($"C37Codec.Encode · {fmt}")),
            new TableBlock(new("Decoded fields (first PMU block)", "Field terdekode (blok PMU pertama)"),
                [new("Bytes", "Byte"), Same("n"), Same("Field"), new("Value", "Nilai")], fieldRows,
                new("C37Codec.DecodeData with the CFG-2 frame", "C37Codec.DecodeData dengan frame CFG-2")),
            new ChartBlock(new("TVE vs. time at the chosen Δf", "TVE terhadap waktu pada Δf terpilih"), tveSvg,
                new($"{cls} class, f₀ 50 Hz, 1 kHz sampling, Fs 50 /s, filter N = {est.FilterOrder}", $"kelas {cls}, f₀ 50 Hz, sampling 1 kHz, Fs 50 /s, filter N = {est.FilterOrder}")),
            new ChartBlock(new("Steady-state TVE over the frequency range", "TVE kondisi tunak sepanjang rentang frekuensi"), sweepSvg,
                new("P class must hold ±2 Hz, M class ±5 Hz", "kelas P wajib ±2 Hz, kelas M ±5 Hz")),
        };

        if (observable)
        {
            var se = LinearStateEstimator.Estimate(net, z);
            var vRef = se.Referenced(1, pf.Bus(1).VaDeg);
            var rows = new List<Cell[]>();
            double maxDv = 0, maxDa = 0;
            for (int i = 0; i < net.BusCount; i++)
            {
                var b = pf.Buses[i];
                double vm = vRef[i].Magnitude, va = vRef[i].Phase * 180 / Math.PI;
                double dv = Math.Abs(vm - b.Vm), da = Math.Abs(va - b.VaDeg);
                maxDv = Math.Max(maxDv, dv); maxDa = Math.Max(maxDa, da);
                bool hasPmu = placement.Buses.Contains(b.BusId);
                rows.Add([b.BusId.ToString(CultureInfo.InvariantCulture), hasPmu ? Cell.Chip("PMU", "ok") : Cell.Chip(Pick(lang, "via I", "lewat I"), "na"),
                    Cell.Num(b.Vm, "0.0000"), Cell.Num(vm, "0.0000"), Cell.Num(dv * 1000, "0.00"), Cell.Num(b.VaDeg, "0.000"), Cell.Num(va, "0.000"), Cell.Num(da, "0.000")]);
            }
            kpis.Add(new(new("Linear SE max |ΔVm|", "SE linear |ΔVm| maks"), F(maxDv, "0.0000"), "pu", Same($"|Δθ| {F(maxDa, "0.000")}° · J {F(se.Objective, "0.0")} / dof {se.DegreesOfFreedom}"),
                maxDv < 0.005 ? "ok" : "warn"));
            blocks.Add(new TableBlock(new("Linear PMU state estimate vs. power flow", "Estimasi keadaan PMU linear vs. aliran daya"),
                [Same("Bus"), Same(""), new("Vm true", "Vm benar"), new("Vm est.", "Vm est."), Same("ΔVm ×10⁻³"), new("θ true °", "θ benar °"), Same("θ est. °"), Same("Δθ °")], rows,
                new($"{z.Count} phasors from {placement.Buses.Length} PMUs, x̂ = (HᴴWH)⁻¹HᴴWz in one step", $"{z.Count} fasor dari {placement.Buses.Length} PMU, x̂ = (HᴴWH)⁻¹HᴴWz dalam satu langkah"), Wide: true));
        }
        else
        {
            kpis.Add(new(new("Linear SE", "SE linear"), Pick(lang, "UNOBSERVABLE", "TAK TERAMATI"), null,
                new("add a PMU or a branch current", "tambah PMU atau arus cabang"), "alarm"));
            var known = new HashSet<int>(placement.Buses);
            foreach (var m in z.Where(m => m.Channel.Kind == Electrical.Pmu.C37118.PhasorKind.Current))
            {
                var br = net.Branches[m.Channel.BranchIndex];
                known.Add(br.From); known.Add(br.To);
            }
            var missing = net.Buses.Select(b => b.Id).Where(id => !known.Contains(id)).ToList();
            blocks.Add(new NoteBlock(new($"PMUs at {string.Join(", ", placement.Buses)} do not observe bus {string.Join(", ", missing)}: no voltage phasor and no measured current reaches it, so H has a null space and LinearStateEstimator.Estimate refuses to solve.",
                $"PMU di {string.Join(", ", placement.Buses)} tidak mengamati bus {string.Join(", ", missing)}: tidak ada fasor tegangan maupun arus terukur yang menjangkaunya, sehingga H memiliki ruang nol dan LinearStateEstimator.Estimate menolak menyelesaikan.")));
        }

        blocks.Add(new NoteBlock(new("The simulator publishes the power-flow voltages [V line-to-neutral] and incident branch currents [A] of every PMU bus, rotating at 2π·Δf. The estimator demodulates at f₀ with the Annex C filter (P: 2-cycle triangle, M: Hamming-windowed sinc) and compensates the filter gain at the estimated Δf; single-phase TVE shows the 2f₀ ripple, the positive-sequence estimate cancels it. With integer formats the 16-bit quantisation (PHUNIT) adds a visible error to the state estimate.",
            "Simulator menerbitkan tegangan aliran daya [V fasa-netral] dan arus cabang [A] setiap bus PMU, berputar pada 2π·Δf. Estimator mendemodulasi pada f₀ dengan filter Annex C (P: segitiga 2 siklus, M: sinc berjendela Hamming) dan mengompensasi penguatan filter pada Δf terestimasi; TVE satu fasa menunjukkan riak 2f₀, estimasi urutan positif menghilangkannya. Dengan format integer, kuantisasi 16 bit (PHUNIT) menambah galat yang terlihat pada estimasi keadaan.")));
        return new InteractiveResult(kpis, blocks);
    }
}
