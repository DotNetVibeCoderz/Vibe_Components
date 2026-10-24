using System.Globalization;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Fec;
using GnuRadio.Filter;
using GnuRadio.Sinks;

namespace GnuRadio.Gallery.Demos;

/// <summary>Rayleigh and Rician flat fading on QPSK.</summary>
public sealed class FadingDemo : Demo
{
    private const double SymbolRate = 10e3;
    private bool _rician;
    private double _dopplerHz = 20;
    private double _k = 6;
    private float _noiseVoltage = 0.05f;
    private FadingModel? _signalFading;
    private FadingModel? _envelopeFading;
    private AwgnChannel? _noise;
    private FadeProbe? _fades;

    public FadingDemo()
    {
        AddParameter(new ChoiceParameter("model", new("Model", "Model"),
            [("rayleigh", new("Rayleigh (no line of sight)", "Rayleigh (tanpa LOS)")), ("rician", new("Rician (line of sight)", "Rician (dengan LOS)"))], "rayleigh", v =>
            {
                _rician = v == "rician";
                Restart();
            }));
        AddParameter(new NumberParameter("doppler", new("Max Doppler", "Doppler maks"), 1, 200, 1, _dopplerHz, v =>
        {
            _dopplerHz = v;
            if (_signalFading is null) return;
            _signalFading.FdTs = _dopplerHz / SymbolRate;
            _envelopeFading!.FdTs = _dopplerHz / SymbolRate;
        }) { Unit = "Hz" });
        AddParameter(new NumberParameter("k", new("Rician K", "K Rician"), 0, 20, 0.5, _k, v =>
        {
            _k = v;
            if (_rician) Restart();
        }));
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 0.4, 0.01, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }));
    }

    public override string Id => "fading";
    public override DemoCategory Category => DemoCategory.Channels;
    public override Text Title => new("Fading channels", "Kanal fading");

    public override Text Summary => new(
        "Sum-of-sinusoids fading (Zheng & Xiao), as in GNU Radio's channels.fading_model. Rayleigh fades dip 20–30 dB; a line-of-sight component (Rician K) keeps the envelope up.",
        "Fading sum-of-sinusoids (Zheng & Xiao), seperti channels.fading_model GNU Radio. Fade Rayleigh turun 20–30 dB; komponen line-of-sight (K Rician) menjaga selubung tetap tinggi.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Channels;
        using GnuRadio.Digital;

        var qpsk = Constellation.Qpsk();
        var fading = new FadingModel(sinusoids: 8, fdTs: 20 / 10e3, los: true, kFactor: 6);
        var constellation = new GnuRadio.Sinks.ConstellationSink(1024);

        var fg = new Flowgraph("fading");
        fg.Connect(new VectorSource<byte>(randomSymbols, repeat: true), new ChunksToSymbols(qpsk),
            new Throttle<Complex32>(10e3), fading, new AwgnChannel(0.05f), constellation);
        fg.Start();
        // Frequency-selective: new SelectiveFadingModel(delays: [0, 1.5, 3.2], magnitudes: [1, 0.5, 0.25])
        """;

    protected override Flowgraph Build()
    {
        var c = Constellation.Qpsk();
        var symbols = DemoData.Random(8192, 5).Select(b => (byte)(b & 3)).ToArray();
        var fdTs = _dopplerHz / SymbolRate;
        _signalFading = new FadingModel(8, fdTs, _rician, _k, 11);
        _envelopeFading = new FadingModel(8, fdTs, _rician, _k, 11);
        _noise = new AwgnChannel(_noiseVoltage, 2);
        var constellation = new ConstellationSink(600);
        var power = new ComplexToMagSquared();
        var db = new NLog10(10);
        var scope = new TimeSink<float>(10_000, SymbolRate) { UpdateRate = 8 };
        _fades = new FadeProbe(-10);
        var spectrum = new FrequencySink(2048, SymbolRate) { Averaging = 0.05f, UpdateRate = 10 };

        var fg = new Flowgraph("fading");
        fg.Connect(new VectorSource<byte>(symbols, repeat: true), new ChunksToSymbols(c), new Throttle<Complex32>(SymbolRate), _signalFading, _noise, constellation);
        var envelopeSource = new ConstantSource<Complex32>(Complex32.One);
        var envelopeThrottle = new Throttle<Complex32>(SymbolRate);
        fg.Connect(envelopeSource, envelopeThrottle, _envelopeFading, power, db, scope);
        fg.Connect(db, 0, _fades, 0);
        fg.Connect(_envelopeFading, 0, spectrum, 0);

        AddView(new ScopePanel(new("Channel power (dB) · 1 s", "Daya kanal (dB) · 1 dtk"), scope, -35, 10, Size: PanelSize.Full, Height: 220));
        AddView(new ConstellationPanel(new("Faded QPSK", "QPSK terkena fading"), constellation, c.Points, 2.5f, Size: PanelSize.Half));
        AddView(new SpectrumPanel(new("Doppler spectrum of the gain", "Spektrum Doppler dari gain"), spectrum, -70, 10, Size: PanelSize.Half, Height: 280));
        AddReadout(new("Below −10 dB", "Di bawah −10 dB"), () => $"{_fades.DeepFadeFraction * 100:0.0} % of the time");
        AddReadout(new("Coherence time", "Waktu koherensi"), () => $"≈ {0.423 / _dopplerHz * 1000:0} ms");
        AddReadout(new("Model", "Model"), () => _rician ? $"Rician K = {_k:0.0}" : "Rayleigh");
        return fg;
    }
}

/// <summary>Monte-Carlo BER curves for the FEC codes, against uncoded BPSK theory.</summary>
public sealed class BerLabDemo : Demo
{
    private readonly Lock _lock = new();
    private readonly List<ChartLine> _lines = [];
    private int _revision;
    private CancellationTokenSource? _cts;
    private long _bitsPerPoint = 60_000;
    private string _progress = "—";
    private readonly double[] _ebn0 = Enumerable.Range(0, 15).Select(i => i * 0.5).ToArray();

    public BerLabDemo()
    {
        AddParameter(new ChoiceParameter("bits", new("Bits per point", "Bit per titik"),
            [("20000", Text.Same("20 k")), ("60000", Text.Same("60 k")), ("200000", Text.Same("200 k"))], "60000", v =>
            {
                _bitsPerPoint = long.Parse(v, CultureInfo.InvariantCulture);
                Restart();
            }));
        AddParameter(new ActionParameter("run", new("Run again", "Jalankan lagi"), Restart));
    }

    public override string Id => "ber-lab";
    public override DemoCategory Category => DemoCategory.ErrorCorrection;
    public override Text Title => new("FEC BER laboratory", "Laboratorium BER FEC");

    public override Text Summary => new(
        "Measures bit error rate against Eb/N0 for convolutional (K=7, Viterbi), Reed–Solomon (255,223), LDPC (min-sum) and Polar (SC) codes over BPSK/AWGN, the way GNU Radio's bercurve_generator does.",
        "Mengukur laju galat bit terhadap Eb/N0 untuk kode konvolusi (K=7, Viterbi), Reed–Solomon (255,223), LDPC (min-sum) dan Polar (SC) pada BPSK/AWGN, seperti bercurve_generator GNU Radio.");

    public override Text? Notes => new(
        "Curves stop where no errors were seen in the bits simulated. Eb/N0 accounts for the code rate, so a curve left of the dashed theory line is real coding gain.",
        "Kurva berhenti ketika tidak ada galat pada bit yang disimulasikan. Eb/N0 sudah memperhitungkan laju kode, jadi kurva di kiri garis teori putus-putus adalah penguatan pengodean yang nyata.");

    public override string Code => """
        using GnuRadio.Fec;

        var conv = new ConvolutionalCode(1024);
        var ldpc = LdpcCode.Gallager(1008, 3, 6);
        var polar = new PolarCode(1024, 512, designSnrDb: 1);
        var rs = new ReedSolomonFec(ReedSolomon.Rs255_223());
        var codes = new (IFecEncoder, IFecDecoder)[] { (conv, conv), (ldpc, ldpc), (polar, polar), (rs, rs) };

        foreach (var (enc, dec) in codes)
            for (var ebn0 = 0.0; ebn0 <= 7; ebn0 += 0.5)
            {
                var r = BerMath.Simulate(enc, dec, ebn0, maxBits: 200_000);
                Console.WriteLine($"{enc.Name,-28} {ebn0,4:0.0} dB  BER {r.Ber:0.0E0}");
            }
        """;

    protected override Flowgraph Build()
    {
        lock (_lock)
        {
            _lines.Clear();
            _lines.Add(new ChartLine("Uncoded BPSK (theory)", _ebn0, _ebn0.Select(BerMath.BpskBer).ToArray(), Dashed: true));
            _revision++;
        }
        AddView(new ChartPanel(new("Bit error rate vs Eb/N0", "Laju galat bit vs Eb/N0"), Lines, () => Volatile.Read(ref _revision),
            0, 7, 1e-6, 0.5, LogY: true, XUnit: " dB", Meta: () => _progress, Height: 380));
        AddReadout(new("Progress", "Kemajuan"), () => _progress);
        AddReadout(new("Bits per point", "Bit per titik"), () => _bitsPerPoint.ToString("N0", CultureInfo.InvariantCulture));
        return new Flowgraph("ber_lab");
    }

    private IReadOnlyList<ChartLine> Lines()
    {
        lock (_lock) return [.. _lines];
    }

    protected override void OnStarted()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => Simulate(token), token);
    }

    protected override void OnStopping() => _cts?.Cancel();

    private void Simulate(CancellationToken token)
    {
        var ldpc = LdpcCode.Gallager(1008, 3, 6, seed: 3);
        var codes = new (string Name, IFecEncoder Encoder, IFecDecoder Decoder)[]
        {
            ("Convolutional K=7 r=1/2", new ConvolutionalCode(1024), new ConvolutionalCode(1024)),
            ("Reed–Solomon (255,223)", new ReedSolomonFec(ReedSolomon.Rs255_223()), new ReedSolomonFec(ReedSolomon.Rs255_223())),
            ($"LDPC ({ldpc.N},{ldpc.K}) min-sum", ldpc, ldpc),
            ("Polar (1024,512) SC", new PolarCode(1024, 512, 1), new PolarCode(1024, 512, 1)),
        };
        var total = codes.Length * _ebn0.Length;
        var done = 0;
        foreach (var (name, encoder, decoder) in codes)
        {
            var x = new List<double>();
            var y = new List<double>();
            foreach (var e in _ebn0)
            {
                if (token.IsCancellationRequested) return;
                var r = BerMath.Simulate(encoder, decoder, e, _bitsPerPoint, 400, seed: (int)(e * 10) + 1);
                done++;
                _progress = $"{done * 100 / total} % · {name}";
                if (r.Errors == 0) break;
                x.Add(e);
                y.Add(r.Ber);
                lock (_lock)
                {
                    _lines.RemoveAll(l => l.Name == name);
                    _lines.Add(new ChartLine(name, [.. x], [.. y], Points: true));
                    _revision++;
                }
            }
            done = (Array.IndexOf(codes.Select(c => c.Name).ToArray(), name) + 1) * _ebn0.Length;
        }
        _progress = "100 % · done";
    }
}

/// <summary>Interactive FIR design with its response and filtered noise.</summary>
public sealed class FilterDesignerDemo : Demo
{
    private const double Rate = 48e3;
    private string _type = "low";
    private double _cutoff = 6000;
    private double _transition = 1500;
    private WindowType _window = WindowType.Hamming;
    private FirFilterCcf? _filter;
    private float[] _taps = [];
    private int _revision;

    public FilterDesignerDemo()
    {
        AddParameter(new ChoiceParameter("type", new("Response", "Respons"),
            [("low", new("Low-pass", "Lolos-rendah")), ("high", new("High-pass", "Lolos-tinggi")), ("band", new("Band-pass", "Lolos-pita"))], _type, v =>
            {
                _type = v;
                Redesign();
            }));
        AddParameter(new NumberParameter("cutoff", new("Cutoff", "Frekuensi potong"), 500, 20_000, 100, _cutoff, v =>
        {
            _cutoff = v;
            Redesign();
        }) { Format = v => Axis.FormatFrequency(v, 1) });
        AddParameter(new NumberParameter("transition", new("Transition width", "Lebar transisi"), 200, 5000, 50, _transition, v =>
        {
            _transition = v;
            Redesign();
        }) { Format = v => Axis.FormatFrequency(v, 1) });
        AddParameter(new ChoiceParameter("window", new("Window", "Jendela"),
            [("Hamming", Text.Same("Hamming")), ("Hann", Text.Same("Hann")), ("Blackman", Text.Same("Blackman")), ("BlackmanHarris", Text.Same("Blackman-Harris")), ("Rectangular", new("Rectangular", "Persegi"))],
            "Hamming", v =>
            {
                _window = Enum.Parse<WindowType>(v);
                Redesign();
            }));
    }

    public override string Id => "fir-designer";
    public override DemoCategory Category => DemoCategory.Filters;
    public override Text Title => new("FIR filter designer", "Perancang filter FIR");

    public override Text Summary => new(
        "Windowed-sinc design with GNU Radio's firdes rules: the window sets the stop-band attenuation, the transition width sets the number of taps. The taps update live on white noise.",
        "Desain windowed-sinc dengan aturan firdes GNU Radio: jendela menentukan atenuasi pita-henti, lebar transisi menentukan jumlah tap. Tap diperbarui langsung pada derau putih.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Filter;

        var taps = Firdes.LowPass(gain: 1, sampleRate: 48e3, cutoff: 6e3, transitionWidth: 1.5e3, WindowType.Hamming);
        Console.WriteLine($"{taps.Length} taps");
        var response = Firdes.MagnitudeResponseDb(taps, points: 512);   // 0 … fs/2

        var filter = new FirFilterCcf(decimation: 1, taps);
        fg.Connect(new NoiseSource<Complex32>(NoiseType.Gaussian, 1), new Throttle<Complex32>(48e3), filter, spectrum);
        filter.SetTaps(Firdes.HighPass(1, 48e3, 6e3, 1.5e3));          // retune while running
        """;

    private float[] Design() => _type switch
    {
        "high" => Firdes.HighPass(1, Rate, _cutoff, _transition, _window),
        "band" => Firdes.BandPass(1, Rate, Math.Max(100, _cutoff - 2000), Math.Min(Rate / 2 - 100, _cutoff + 2000), _transition, _window),
        _ => Firdes.LowPass(1, Rate, _cutoff, _transition, _window),
    };

    private void Redesign()
    {
        _taps = Design();
        _filter?.SetTaps(_taps);
        Interlocked.Increment(ref _revision);
    }

    protected override Flowgraph Build()
    {
        _taps = Design();
        _filter = new FirFilterCcf(1, _taps);
        var spectrum = new FrequencySink(2048, Rate) { Averaging = 0.08f, UpdateRate = 20 };
        var fg = new Flowgraph("fir_designer");
        fg.Connect(new NoiseSource<Complex32>(NoiseType.Gaussian, 1, 3), new Throttle<Complex32>(Rate), _filter, spectrum);

        var freqs = Enumerable.Range(0, 512).Select(i => i * Rate / 2 / 511 / 1000).ToArray();
        AddView(new ChartPanel(new("Magnitude response", "Respons magnitudo"),
            () => [new ChartLine($"{_taps.Length} taps", freqs, [.. Firdes.MagnitudeResponseDb(_taps, 512).Select(v => (double)v)])],
            () => Volatile.Read(ref _revision), 0, Rate / 2000, -120, 5, XUnit: " kHz", YUnit: " dB", Height: 260));
        AddView(new SpectrumPanel(new("Filtered white noise", "Derau putih tersaring"), spectrum, -80, 0, Height: 220));
        AddReadout(new("Taps", "Tap"), () => _taps.Length.ToString());
        AddReadout(new("Group delay", "Tunda grup"), () => $"{(_taps.Length - 1) / 2} samples · {(_taps.Length - 1) / 2 / Rate * 1000:0.00} ms");
        AddReadout(new("Window attenuation", "Atenuasi jendela"), () => $"≈ {Window.MaxAttenuation(_window):0} dB");
        return fg;
    }
}
