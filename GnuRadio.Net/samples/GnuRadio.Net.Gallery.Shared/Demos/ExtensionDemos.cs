using System.Text;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Extensions;
using GnuRadio.Sinks;

namespace GnuRadio.Gallery.Demos;

/// <summary>LoRa-style chirp spread spectrum.</summary>
public sealed class LoRaDemo : Demo
{
    private int _sf = 7;
    private float _noiseVoltage = 0.25f;
    private AwgnChannel? _noise;
    private LoRaDemodulator? _demod;
    private SymbolFrameProbe? _probe;

    public LoRaDemo()
    {
        AddParameter(new ChoiceParameter("sf", new("Spreading factor", "Faktor penyebaran"),
            [("7", Text.Same("SF7")), ("8", Text.Same("SF8")), ("9", Text.Same("SF9")), ("10", Text.Same("SF10"))], "7", v =>
            {
                _sf = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                Restart();
            }));
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 3, 0.05, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }) { Help = new("CSS decodes well below the noise floor; raise the SF to go further.", "CSS terdekode jauh di bawah derau dasar; naikkan SF untuk lebih jauh.") });
    }

    public override string Id => "lora";
    public override DemoCategory Category => DemoCategory.Extensions;
    public override Text Title => new("LoRa chirps (gr-lora)", "Chirp LoRa (gr-lora)");

    public override Text Summary => new(
        "Chirp spread spectrum: each symbol is a cyclic shift of an up-chirp across 125 kHz. Dechirping and an FFT turn it into a single peak, so symbols are recovered even when the signal is under the noise.",
        "Chirp spread spectrum: setiap simbol adalah geseran siklik dari up-chirp selebar 125 kHz. Dechirp dan FFT mengubahnya menjadi satu puncak, sehingga simbol pulih walau sinyal di bawah derau.");

    public override Text? Notes => new(
        "This is the CSS physical layer and frame sync only (preamble, two down-chirps, length symbol) — no whitening, Hamming or interleaving, so it is not LoRaWAN-compatible.",
        "Ini hanya lapisan fisik CSS dan sinkronisasi frame (preamble, dua down-chirp, simbol panjang) — tanpa whitening, Hamming atau interleaving, jadi tidak kompatibel dengan LoRaWAN.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Channels;
        using GnuRadio.Extensions;

        var p = new LoRaParameters(SpreadingFactor: 8, Bandwidth: 125e3, Oversampling: 2);
        var symbols = new byte[] { 12, 200, 7, 99, 3, 255, 64, 128 };
        var rx = new VectorSink<byte>();

        var fg = new Flowgraph("lora");
        fg.Connect(new VectorSource<byte>(symbols, repeat: true), new LoRaModulator(p, symbolsPerFrame: 8),
            new Throttle<Complex32>(p.SampleRate), new AwgnChannel(1.0f), new LoRaDemodulator(p), rx);
        fg.Start();
        """;

    protected override Flowgraph Build()
    {
        var p = new LoRaParameters(_sf, 125e3, 2);
        var payload = Enumerable.Range(0, 16).Select(i => (byte)((i * 37 + 11) % p.ChipsPerSymbol)).ToArray();
        var mod = new LoRaModulator(p, 16);
        _noise = new AwgnChannel(_noiseVoltage, 7);
        _demod = new LoRaDemodulator(p);
        _probe = new SymbolFrameProbe(payload);
        // A chirp lasts only 256–2048 samples, so the waterfall needs short FFTs and every row to show the sweep.
        var waterfall = new WaterfallSink(32, p.SampleRate, 0, 200) { UpdateRate = p.SampleRate / 32 };
        var fg = new Flowgraph("lora");
        fg.Connect(new VectorSource<byte>(payload, repeat: true), mod, new Throttle<Complex32>(p.SampleRate), _noise);
        fg.Connect(_noise, 0, waterfall, 0);
        fg.Connect(_noise, _demod, _probe);

        var bins = p.ChipsPerSymbol;
        AddView(new WaterfallPanel(new("Chirps on the waterfall", "Chirp pada waterfall"), waterfall, -40, 15, Height: 300));
        AddView(new VectorPanel(new("Dechirped symbol (FFT bins)", "Simbol setelah dechirp (bin FFT)"), Data: () =>
        {
            var m = _demod.LastPeakBins;
            var max = m.Length == 0 ? 1 : m.Max();
            return [.. m.Select(v => 10f * MathF.Log10(v / max + 1e-6f))];
        }, Min: -40, Max: 3, XStart: 0, XEnd: bins, YUnit: " dB", Height: 220));
        AddView(new TextPanel(new("Last decoded symbols", "Simbol terakhir terdekode"), () => _probe.Recent, PanelSize.Half, 220));
        AddReadout(new("Frames detected", "Frame terdeteksi"), () => _demod.FramesDetected.ToString());
        AddReadout(new("Frames error-free", "Frame tanpa galat"), () => _probe.Frames == 0 ? "—" : $"{_probe.FramesCorrect}/{_probe.Frames}");
        AddReadout(new("Bit rate", "Laju bit"), () => $"{p.SpreadingFactor * 125e3 / p.ChipsPerSymbol / 1000:0.00} kbit/s");
        AddReadout(new("Processing gain", "Penguatan proses"), () => $"{10 * Math.Log10(p.ChipsPerSymbol):0.0} dB");
        return fg;
    }
}

/// <summary>FMCW radar range profile with CFAR detection.</summary>
public sealed class RadarDemo : Demo
{
    private readonly FmcwParameters _p = new();
    private double _range1 = 35;
    private double _range2 = 120;
    private double _speed2 = 8;
    private FmcwRadarSource? _source;
    private FmcwRangeProcessor? _processor;
    private VectorFrameSink? _profile;

    public RadarDemo()
    {
        AddParameter(new NumberParameter("r1", new("Target A range", "Jarak target A"), 5, 300, 1, _range1, v =>
        {
            _range1 = v;
            UpdateTargets();
        }) { Unit = "m" });
        AddParameter(new NumberParameter("r2", new("Target B range", "Jarak target B"), 5, 300, 1, _range2, v =>
        {
            _range2 = v;
            UpdateTargets();
        }) { Unit = "m" });
        AddParameter(new NumberParameter("v2", new("Target B speed", "Kecepatan target B"), -30, 30, 1, _speed2, v =>
        {
            _speed2 = v;
            UpdateTargets();
        }) { Unit = "m/s" });
    }

    public override string Id => "fmcw-radar";
    public override DemoCategory Category => DemoCategory.Extensions;
    public override Text Title => new("FMCW radar (gr-radar)", "Radar FMCW (gr-radar)");

    public override Text Summary => new(
        "A 24 GHz FMCW radar sweeping 150 MHz per millisecond: each target produces a beat tone proportional to its range. A range FFT per chirp and CA-CFAR detection pick the targets out.",
        "Radar FMCW 24 GHz menyapu 150 MHz per milidetik: setiap target menghasilkan nada beat sebanding dengan jaraknya. FFT jarak per chirp dan deteksi CA-CFAR menemukan targetnya.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Extensions;
        using GnuRadio.Sinks;

        var p = new FmcwParameters(CarrierHz: 24e9, SweepBandwidth: 150e6, ChirpDuration: 1e-3, SampleRate: 512e3);
        var radar = new FmcwRadarSource(p, [new RadarTarget(35, 0), new RadarTarget(120, 8)]);
        var ranges = new FmcwRangeProcessor(p);
        var profile = new VectorFrameSink(ranges.FftSize / 2);
        profile.FrameUpdated += () => { /* copy with TryCopy, then: */ };

        var fg = new Flowgraph("fmcw");
        fg.Connect(radar, new Throttle<Complex32>(p.SampleRate), ranges, profile);
        fg.Start();
        // var hits = Cfar.Detect(profileDb).Select(ranges.BinRange);   // metres
        """;

    private void UpdateTargets()
    {
        if (_source is not null) _source.Targets = [new RadarTarget(_range1, 0), new RadarTarget(_range2, _speed2, 0.6)];
    }

    protected override Flowgraph Build()
    {
        _source = new FmcwRadarSource(_p, [new RadarTarget(_range1, 0), new RadarTarget(_range2, _speed2, 0.6)], 0.02f, 3);
        _processor = new FmcwRangeProcessor(_p);
        _profile = new VectorFrameSink(_processor.FftSize / 2) { UpdateRate = 20 };
        var fg = new Flowgraph("fmcw");
        fg.Connect(_source, new Throttle<Complex32>(_p.SampleRate), _processor, _profile);

        var maxRange = _processor.BinRange(_processor.FftSize / 2);
        IReadOnlyList<(double, string)> Hits()
        {
            // Own buffer per call: the UI and the readout strip may ask from different threads.
            var buffer = new float[_processor.FftSize / 2];
            long version = -1;
            _profile.TryCopy(ref version, buffer);
            return [.. Cfar.Detect(buffer, 8, 2, 12).Select(i => (_processor.BinRange(i), $"{_processor.BinRange(i):0.0} m"))];
        }
        AddView(new VectorPanel(new("Range profile", "Profil jarak"), _profile, Min: -90, Max: 0, XStart: 0, XEnd: maxRange,
            XUnit: " m", YUnit: " dB", Markers: Hits, Size: PanelSize.Full, Height: 300));
        AddReadout(new("Detections", "Deteksi"), () => string.Join(" · ", Hits().Select(h => h.Item2)) is { Length: > 0 } s ? s : "—");
        AddReadout(new("Range resolution", "Resolusi jarak"), () => $"{_p.RangeResolution:0.00} m");
        AddReadout(new("Max range", "Jarak maks"), () => $"{_p.MaxRange:0} m");
        AddReadout(new("Beat of target A", "Beat target A"), () => Axis.FormatFrequency(_p.BeatFrequency(_range1), 1));
        return fg;
    }
}

/// <summary>AX.25 telemetry beacon over GFSK with HDLC framing (gr-satellites).</summary>
public sealed class SatelliteTelemetryDemo : Demo
{
    private const int Sps = 8;
    private const double Baud = 9600;
    private float _noiseVoltage = 0.25f;
    private AwgnChannel? _noise;
    private HdlcDeframer? _deframer;
    private HdlcFramer? _framer;
    private Timer? _beacon;
    private int _sequence;
    private readonly List<string> _log = [];
    private readonly Lock _lock = new();

    public SatelliteTelemetryDemo()
    {
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 1.2, 0.02, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }));
        AddParameter(new ActionParameter("send", new("Send a beacon now", "Kirim beacon sekarang"), SendBeacon));
    }

    public override string Id => "satellite-ax25";
    public override DemoCategory Category => DemoCategory.Extensions;
    public override Text Title => new("CubeSat AX.25 telemetry (gr-satellites)", "Telemetri AX.25 CubeSat (gr-satellites)");

    public override Text Summary => new(
        "A 9600-baud GFSK beacon like many amateur satellites: AX.25 UI frames, HDLC bit stuffing with a CRC-16/X.25 check, NRZI line coding. The receiver demodulates, deframes and parses the packets.",
        "Beacon GFSK 9600 baud seperti banyak satelit amatir: frame UI AX.25, bit stuffing HDLC dengan pemeriksaan CRC-16/X.25, pengodean NRZI. Penerima mendemodulasi, membongkar frame dan mengurai paket.");

    public override string Code => """
        using System.Text;
        using GnuRadio;
        using GnuRadio.Digital;
        using GnuRadio.Extensions;

        var framer = new HdlcFramer();
        var deframer = new HdlcDeframer();
        deframer.FrameReceived += frame =>
        {
            var (to, from, info) = Ax25.Parse(frame);
            Console.WriteLine($"{from}>{to}: {Encoding.ASCII.GetString(info)}");
        };

        var fg = new Flowgraph("ax25_9600");
        fg.Connect(framer, new NrziEncoder(), new FskModulator(8, 0.5, 0.5), channel,
            new FskDemodulator(8, 0.5), new NrziDecoder(), deframer);
        fg.Start();
        framer.Enqueue(Ax25.UiFrame("CQ", "YB0SAT", Encoding.ASCII.GetBytes("T+0042 BAT=7.9V TEMP=21C")));
        """;

    private void SendBeacon()
    {
        if (_framer is null) return;
        var seq = Interlocked.Increment(ref _sequence);
        var payload = $"T+{seq:0000} BAT={7.6 + 0.3 * Math.Sin(seq / 3.0):0.00}V TEMP={18 + seq % 7}C MODE=NOMINAL";
        _framer.Enqueue(Ax25.UiFrame("CQ", "YB0SAT", Encoding.ASCII.GetBytes(payload), sourceSsid: 1));
    }

    protected override Flowgraph Build()
    {
        var rate = Baud * Sps;
        _framer = new HdlcFramer();
        var mod = new FskModulator(Sps, 0.5, 0.5);
        _noise = new AwgnChannel(_noiseVoltage, 3);
        var spectrum = new FrequencySink(1024, rate) { Averaging = 0.2f, UpdateRate = 20 };
        var demod = new FskDemodulator(Sps, 0.5);
        _deframer = new HdlcDeframer();
        _deframer.FrameReceived += frame =>
        {
            try
            {
                var (to, from, info) = Ax25.Parse(frame);
                lock (_lock)
                {
                    _log.Add($"{DateTime.Now:HH:mm:ss}  {from}>{to}: {Encoding.ASCII.GetString(info)}");
                    if (_log.Count > 8) _log.RemoveAt(0);
                }
            }
            catch (ArgumentException)
            {
            }
        };
        var disc = new Analog.QuadratureDemod((float)(Sps / (Math.PI * 0.5)));
        var scope = new TimeSink<float>(Sps * 40, rate) { UpdateRate = 15 };

        // The framer emits idle flags as fast as buffers allow; small buffers keep a beacon from queuing
        // behind seconds of flags (latency = buffer size / bit rate).
        var fg = new Flowgraph("ax25") { BufferBytes = 2048 };
        fg.Connect(_framer, new NrziEncoder(), mod, new Throttle<Complex32>(rate), _noise);
        fg.Connect(_noise, 0, spectrum, 0);
        fg.Connect(_noise, demod, new NrziDecoder(), _deframer);
        fg.Connect(_noise, disc, scope);

        AddView(new SpectrumPanel(new("GFSK 9600 Bd spectrum", "Spektrum GFSK 9600 Bd"), spectrum, -80, 0, Size: PanelSize.Half, Height: 220));
        AddView(new ScopePanel(new("Discriminator · 40 bits", "Diskriminator · 40 bit"), scope, -2, 2, Height: 220));
        AddView(new TextPanel(new("Received packets", "Paket diterima"), () =>
        {
            lock (_lock) return _log.Count == 0 ? "Waiting for the next beacon…" : string.Join('\n', _log);
        }, PanelSize.Full, 190));
        AddReadout(new("Frames OK", "Frame OK"), () => _deframer.FramesReceived.ToString());
        AddReadout(new("FCS errors", "Galat FCS"), () => _deframer.FcsErrors.ToString());
        AddReadout(new("Beacons sent", "Beacon terkirim"), () => _sequence.ToString());
        return fg;
    }

    protected override void OnStarted() => _beacon = new Timer(_ => SendBeacon(), null, 500, 1500);

    protected override void OnStopping()
    {
        _beacon?.Dispose();
        _beacon = null;
    }
}

/// <summary>Modulation recognition from higher-order statistics.</summary>
public sealed class ClassifierDemo : Demo
{
    private const double SymbolRate = 20e3;
    private string _actual = "16qam";
    private float _noiseVoltage = 0.1f;
    private AwgnChannel? _noise;
    private ClassifierProbe? _probe;
    private static readonly Lazy<ModulationClassifier> s_classifier = new(() => ModulationClassifier.CreateDefault());

    public ClassifierDemo()
    {
        AddParameter(new ChoiceParameter("actual", new("Transmitted", "Dikirim"),
            [("bpsk", Text.Same("BPSK")), ("qpsk", Text.Same("QPSK")), ("8psk", Text.Same("8PSK")), ("16qam", Text.Same("16QAM")), ("64qam", Text.Same("64QAM"))],
            _actual, v =>
            {
                _actual = v;
                Restart();
            }));
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 0.6, 0.01, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }) { Format = v => $"{-20 * Math.Log10(Math.Max(v, 1e-3)):0} dB SNR" });
    }

    public override string Id => "ml-classifier";
    public override DemoCategory Category => DemoCategory.Extensions;
    public override Text Title => new("Machine learning for RF", "Machine learning untuk RF");

    public override Text Summary => new(
        "Identifies the modulation of unknown symbols from cumulant features (|C40|, |C42|, |C63|, amplitude variance) with a nearest-centroid model trained on synthetic data. Phase-rotation invariant.",
        "Mengenali modulasi simbol tak dikenal dari fitur kumulan (|C40|, |C42|, |C63|, varians amplitudo) dengan model nearest-centroid yang dilatih dengan data sintetis. Tahan rotasi fase.");

    public override Text? Notes => new(
        "Retrain on your own captures with ModulationClassifier.Train(label, blocks). For deep models, export blocks of IQ samples and use them with a dedicated ML library.",
        "Latih ulang dengan tangkapan Anda sendiri lewat ModulationClassifier.Train(label, blocks). Untuk model deep learning, ekspor blok sampel IQ dan gunakan pustaka ML khusus.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Extensions;

        var classifier = ModulationClassifier.CreateDefault();          // BPSK…64QAM centroids
        Complex32[] symbols = CaptureSymbols(4096);                      // symbol-rate samples
        var (label, scores) = classifier.Classify(symbols);
        Console.WriteLine($"{label}  ({scores[label]:P0})");

        // Add your own class from labelled examples:
        classifier.Train("32APSK", myApskBlocks);
        """;

    protected override Flowgraph Build()
    {
        var c = Constellation.FromName(_actual);
        var symbols = DemoData.Random(16_384, 8).Select(b => (byte)(b % c.Arity)).ToArray();
        _noise = new AwgnChannel(_noiseVoltage, 4);
        _probe = new ClassifierProbe(s_classifier.Value, 2048);
        var constellation = new ConstellationSink(1500);
        var fg = new Flowgraph("classifier");
        fg.Connect(new VectorSource<byte>(symbols, repeat: true), new ChunksToSymbols(c), new Throttle<Complex32>(SymbolRate),
            new Rotator(0), new MultiplyConst<Complex32>(Complex32.FromPolar(1, 0.4f)), _noise);
        fg.Connect(_noise, 0, constellation, 0);
        fg.Connect(_noise, 0, _probe, 0);

        AddView(new ConstellationPanel(new("Unknown signal", "Sinyal tak dikenal"), constellation, null, 1.6f, Size: PanelSize.Half, Height: 300));
        AddView(new TextPanel(new("Classifier scores", "Skor pengklasifikasi"), () =>
        {
            var (label, scores) = _probe.Latest;
            var sb = new StringBuilder();
            foreach (var (name, score) in scores.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"{name,-6} {new string('█', (int)Math.Round(score * 30)),-30} {score,6:P1}{(name == label ? "  ◀" : "")}");
            return sb.Length == 0 ? "Collecting 2048 symbols…" : sb.ToString();
        }, PanelSize.Half, 300));
        AddReadout(new("Prediction", "Prediksi"), () => _probe.Latest.Label);
        AddReadout(new("Truth", "Sebenarnya"), () => c.Name);
        AddReadout(new("Correct", "Benar"), () => _probe.Latest.Label == "—" ? "—" : _probe.Latest.Label == c.Name ? "yes / ya" : "no / tidak");
        return fg;
    }
}
