using GnuRadio.Analog;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Digital;
using GnuRadio.Fec;
using GnuRadio.Filter;
using GnuRadio.Sinks;
using GnuRadio.Synchronization;

namespace GnuRadio.Gallery.Demos;

internal static class DemoData
{
    public static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new FastRandom(seed).NextBytes(data);
        return data;
    }
}

/// <summary>PSK/QAM link: modulator, impairments, and a synchronising receiver.</summary>
public sealed class LinearModemDemo : Demo
{
    private const int Sps = 4;
    private const double SampleRate = 100e3;
    private string _name = "qpsk";
    private double _ebn0 = 18;
    private double _offsetHz = 150;
    private ChannelModel? _channel;
    private EvmProbe? _evm;
    private ConstellationReceiver? _rx;

    public LinearModemDemo()
    {
        AddParameter(new ChoiceParameter("const", new("Constellation", "Konstelasi"),
            [("bpsk", Text.Same("BPSK")), ("qpsk", Text.Same("QPSK")), ("8psk", Text.Same("8PSK")), ("16qam", Text.Same("16QAM")), ("64qam", Text.Same("64QAM"))],
            _name, v =>
            {
                _name = v;
                Restart();
            }));
        AddParameter(new NumberParameter("ebn0", new("Eb/N0", "Eb/N0"), 0, 35, 0.5, _ebn0, v =>
        {
            _ebn0 = v;
            if (_channel is not null) _channel.Noise.NoiseVoltage = Noise();
        }) { Unit = "dB" });
        AddParameter(new NumberParameter("offset", new("Carrier offset", "Ofset pembawa"), -800, 800, 10, _offsetHz, v =>
        {
            _offsetHz = v;
            if (_channel is not null) _channel.Rotator.PhaseIncrement = 2 * Math.PI * _offsetHz / SampleRate;
        }) { Unit = "Hz" });
    }

    public override string Id => "psk-qam";
    public override DemoCategory Category => DemoCategory.Digital;
    public override Text Title => new("PSK & QAM modem", "Modem PSK & QAM");

    public override Text Summary => new(
        "Root-raised-cosine shaped BPSK…64QAM over AWGN with a carrier offset. The receiver runs AGC, matched filter, Gardner timing recovery and a Costas or decision-directed carrier loop.",
        "BPSK…64QAM berbentuk root-raised-cosine melalui AWGN dengan ofset pembawa. Penerima menjalankan AGC, filter sepadan, pemulihan waktu Gardner, dan loop pembawa Costas atau decision-directed.");

    public override Text? Notes => new(
        "Left: samples taken naively every 4th sample after the matched filter, so timing and frequency errors smear them. Right: after synchronisation. Carrier recovery has a rotational ambiguity; real links resolve it with differential coding or a known preamble.",
        "Kiri: sampel diambil setiap sampel ke-4 setelah filter sepadan, sehingga galat waktu dan frekuensi membuatnya tersebar. Kanan: setelah sinkronisasi. Pemulihan pembawa memiliki ambiguitas rotasi; tautan nyata mengatasinya dengan pengodean diferensial atau preamble yang diketahui.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Channels;
        using GnuRadio.Digital;
        using GnuRadio.Fec;
        using GnuRadio.Sinks;

        var qam = Constellation.Qam(16);
        var bytes = new byte[8192];
        new FastRandom(1).NextBytes(bytes);

        var tx = new ConstellationModulator(qam, samplesPerSymbol: 4, excessBandwidth: 0.35);
        var noise = BerMath.NoiseVoltage(ebN0Db: 18, qam.BitsPerSymbol, samplesPerSymbol: 4) / 2f;
        var channel = new ChannelModel(noise, frequencyOffset: 0.0015);
        var rx = new ConstellationReceiver(qam, samplesPerSymbol: 4);
        var constellation = new ConstellationSink(2048);

        var fg = new Flowgraph("qam_link");
        fg.Connect(new VectorSource<byte>(bytes, repeat: true), tx, new Throttle<Complex32>(100e3), channel, rx, constellation);
        fg.Connect(rx, new ConstellationDecoder(qam), new NullSink<byte>());
        fg.Start();
        """;

    private float Noise() => BerMath.NoiseVoltage(_ebn0, Constellation.FromName(_name).BitsPerSymbol, Sps) / MathF.Sqrt(Sps);

    protected override Flowgraph Build()
    {
        var c = Constellation.FromName(_name);
        var tx = new ConstellationModulator(c, Sps);
        var throttle = new Throttle<Complex32>(SampleRate);
        _channel = new ChannelModel(Noise(), _offsetHz / SampleRate, 1.0, seed: 9);
        _rx = new ConstellationReceiver(c, Sps);
        var after = new ConstellationSink(1500, "after");
        _evm = new EvmProbe(c);
        var matched = new FirFilterCcf(1, Firdes.RootRaisedCosine(1, Sps, 1, 0.35, 11 * Sps));
        var agc = new Agc<Complex32>(1e-3f, 0.8f);
        var naive = new KeepOneInN<Complex32>(Sps);
        var before = new ConstellationSink(1500, "before");
        var spectrum = new FrequencySink(1024, SampleRate) { Averaging = 0.2f, UpdateRate = 20 };

        var fg = new Flowgraph("linear_modem");
        fg.Connect(new VectorSource<byte>(DemoData.Random(16_384, 4), repeat: true), tx, throttle, _channel);
        fg.Connect(_channel, 0, spectrum, 0);
        fg.Connect(_channel, _rx);
        fg.Connect(_rx, 0, after, 0);
        fg.Connect(_rx, 0, _evm, 0);
        fg.Connect(_channel, agc, matched, naive, before);

        var reference = c.Points.ToArray();
        var scale = c.Name.Contains("QAM") ? 1.4f : 1.5f;
        AddView(new SpectrumPanel(new("Received spectrum", "Spektrum diterima"), spectrum, -70, 10, Size: PanelSize.Third, Height: 280));
        AddView(new ConstellationPanel(new("Before synchronisation", "Sebelum sinkronisasi"), before, reference, scale));
        AddView(new ConstellationPanel(new("After synchronisation", "Setelah sinkronisasi"), after, reference, scale,
            () => $"EVM {_evm.Evm * 100:0.0} %"));
        AddReadout(new("EVM", "EVM"), () => $"{_evm.Evm * 100:0.0} % ({_evm.MerDb:0.0} dB MER)");
        AddReadout(new("Carrier estimate", "Estimasi pembawa"), () =>
        {
            var f = _rx.CostasLoop?.Frequency ?? _rx.DecisionDirected!.Frequency;
            return $"{f / (2 * Math.PI) * SampleRate / Sps:0} Hz";
        });
        AddReadout(new("Timing", "Waktu"), () => $"{_rx.SymbolSync.SamplesPerSymbol:0.0000} samples/symbol");
        AddReadout(new("Theory BER", "BER teori"), () =>
        {
            var m = c.Arity;
            var ber = c.Name.Contains("QAM") ? BerMath.QamBer(m, _ebn0) : BerMath.PskBer(m, _ebn0);
            return ber < 1e-12 ? "< 1E-12" : ber.ToString("0.0E0", System.Globalization.CultureInfo.InvariantCulture);
        });
        return fg;
    }
}

/// <summary>FSK, GFSK and GMSK with an eye-style scope of the discriminator.</summary>
public sealed class FskDemo : Demo
{
    private const int Sps = 8;
    private const double SymbolRate = 4800;
    private double _h = 1.0;
    private double _bt;
    private float _noiseVoltage = 0.1f;
    private AwgnChannel? _noise;

    public FskDemo()
    {
        AddParameter(new ChoiceParameter("bt", new("Pulse shaping", "Pembentukan pulsa"),
            [("0", new("FSK (none)", "FSK (tanpa)")), ("0.5", Text.Same("GFSK BT 0.5")), ("0.3", Text.Same("GMSK BT 0.3"))], "0", v =>
            {
                _bt = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                if (_bt == 0.3) _h = 0.5;
                Restart();
            }));
        AddParameter(new NumberParameter("h", new("Modulation index h", "Indeks modulasi h"), 0.3, 2, 0.05, _h, v =>
        {
            _h = v;
            Restart();
        }));
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 0.6, 0.01, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }));
    }

    public override string Id => "fsk";
    public override DemoCategory Category => DemoCategory.Digital;
    public override Text Title => new("FSK, GFSK & GMSK", "FSK, GFSK & GMSK");

    public override Text Summary => new(
        "Binary frequency-shift keying with optional Gaussian pulse shaping. Smaller BT narrows the spectrum at the price of inter-symbol interference visible on the discriminator trace.",
        "Frequency-shift keying biner dengan pembentukan pulsa Gaussian opsional. BT lebih kecil mempersempit spektrum dengan harga interferensi antar-simbol yang terlihat pada jejak diskriminator.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Channels;
        using GnuRadio.Digital;

        var bits = Bits.Unpack(randomBytes);
        var gmsk = new FskModulator(samplesPerSymbol: 8, modulationIndex: 0.5, bt: 0.3);
        var demod = new FskDemodulator(samplesPerSymbol: 8, modulationIndex: 0.5);
        var received = new VectorSink<byte>();

        var fg = new Flowgraph("gmsk");
        fg.Connect(new VectorSource<byte>(bits), gmsk, new AwgnChannel(0.1f), demod, received);
        fg.Run();
        """;

    protected override Flowgraph Build()
    {
        var rate = SymbolRate * Sps;
        var bits = Bits.Unpack(DemoData.Random(4096, 2));
        var mod = new FskModulator(Sps, _h, _bt);
        var throttle = new Throttle<Complex32>(rate);
        _noise = new AwgnChannel(_noiseVoltage, 4);
        var spectrum = new FrequencySink(1024, rate) { Averaging = 0.15f, UpdateRate = 20 };
        var disc = new QuadratureDemod((float)(Sps / (Math.PI * _h)));
        var scope = new TimeSink<float>(Sps * 24, rate) { UpdateRate = 15 };
        var demod = new FskDemodulator(Sps, _h);
        var bitsOut = new NullSink<byte>();

        var fg = new Flowgraph("fsk");
        fg.Connect(new VectorSource<byte>(bits, repeat: true), mod, throttle, _noise);
        fg.Connect(_noise, 0, spectrum, 0);
        fg.Connect(_noise, disc, scope);
        fg.Connect(_noise, demod, bitsOut);

        AddView(new SpectrumPanel(new("Spectrum", "Spektrum"), spectrum, -80, 0, Size: PanelSize.Half, Height: 240));
        AddView(new ScopePanel(new("Discriminator output · 24 symbols", "Keluaran diskriminator · 24 simbol"), scope, -2f, 2f, Height: 240));
        AddReadout(new("Deviation", "Deviasi"), () => $"± {_h * SymbolRate / 2:0} Hz");
        AddReadout(new("Carson bandwidth", "Lebar pita Carson"), () => $"{2 * (_h * SymbolRate / 2 + SymbolRate) / 1000:0.0} kHz");
        AddReadout(new("Clock recovery", "Pemulihan clock"), () => $"ω = {demod.ClockRecovery.Omega:0.000}");
        return fg;
    }
}

/// <summary>OFDM link with Schmidl &amp; Cox sync, channel estimation and pilot tracking.</summary>
public sealed class OfdmDemo : Demo
{
    private const double SampleRate = 1e6;
    private string _constellation = "qpsk";
    private string _multipath = "mild";
    private float _noiseVoltage = 0.03f;
    private double _cfo = 0.2;
    private ChannelModel? _channel;
    private OfdmDemodulator? _rx;
    private FrameBerProbe? _ber;
    private OfdmParameters _p = new();

    public OfdmDemo()
    {
        AddParameter(new ChoiceParameter("const", new("Subcarrier mapping", "Pemetaan subcarrier"),
            [("qpsk", Text.Same("QPSK")), ("16qam", Text.Same("16QAM")), ("64qam", Text.Same("64QAM"))], _constellation, v =>
            {
                _constellation = v;
                Restart();
            }));
        AddParameter(new ChoiceParameter("multipath", new("Multipath", "Multipath"),
            [("none", new("None", "Tidak ada")), ("mild", new("Mild", "Ringan")), ("strong", new("Strong", "Kuat"))], _multipath, v =>
            {
                _multipath = v;
                Restart();
            }));
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 0.2, 0.002, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_channel is not null) _channel.Noise.NoiseVoltage = _noiseVoltage;
        }));
        AddParameter(new NumberParameter("cfo", new("Carrier offset", "Ofset pembawa"), -0.45, 0.45, 0.01, _cfo, v =>
        {
            _cfo = v;
            if (_channel is not null) _channel.Rotator.PhaseIncrement = 2 * Math.PI * _cfo / _p.FftSize;
        }) { Unit = "subcarriers" });
    }

    public override string Id => "ofdm";
    public override DemoCategory Category => DemoCategory.Digital;
    public override Text Title => new("OFDM link", "Tautan OFDM");

    public override Text Summary => new(
        "802.11a-style OFDM: 64-point FFT, 16-sample cyclic prefix, 48 data and 4 pilot carriers. The receiver finds frames with Schmidl & Cox, removes the carrier offset, equalises per carrier and tracks phase with pilots.",
        "OFDM gaya 802.11a: FFT 64 titik, cyclic prefix 16 sampel, 48 carrier data dan 4 pilot. Penerima menemukan frame dengan Schmidl & Cox, menghapus ofset pembawa, menyamakan per carrier, dan melacak fase dengan pilot.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Blocks;
        using GnuRadio.Channels;
        using GnuRadio.Digital;

        var p = new OfdmParameters { Constellation = Constellation.Qam(16), SymbolsPerFrame = 10 };
        var tx = new OfdmModulator(p);
        var rx = new OfdmDemodulator(p);
        var multipath = new[] { new Complex32(1, 0), new Complex32(0.3f, 0.2f), new Complex32(0, -0.1f) };
        var bytes = new VectorSink<byte>();

        var fg = new Flowgraph("ofdm");
        fg.Connect(new VectorSource<byte>(payload, repeat: true), tx,
            new ChannelModel(noiseVoltage: 0.03f, frequencyOffset: 0.2 / p.FftSize, taps: multipath), rx, bytes);
        fg.Connect(rx, 1, new GnuRadio.Sinks.ConstellationSink(2048), 0);   // equalised carriers
        fg.Start();
        """;

    protected override Flowgraph Build()
    {
        _p = new OfdmParameters { Constellation = Constellation.FromName(_constellation) };
        var payload = DemoData.Random(_p.BytesPerFrame, 12);
        var taps = _multipath switch
        {
            "none" => new[] { Complex32.One },
            "strong" => new[] { new Complex32(0.8f, 0), new Complex32(0, 0.5f), new Complex32(0.3f, -0.2f), new Complex32(-0.15f, 0.1f) },
            _ => new[] { new Complex32(1, 0), new Complex32(0.3f, 0.2f), new Complex32(0, -0.1f) },
        };
        var tx = new OfdmModulator(_p);
        var throttle = new Throttle<Complex32>(SampleRate);
        _channel = new ChannelModel(_noiseVoltage, _cfo / _p.FftSize, 1.0, taps, seed: 3);
        _rx = new OfdmDemodulator(_p);
        _ber = new FrameBerProbe(payload);
        var constellation = new ConstellationSink(_p.DataCarriers.Length * 20, "equalised");
        var spectrum = new FrequencySink(512, SampleRate) { Averaging = 0.1f, UpdateRate = 20 };

        var fg = new Flowgraph("ofdm");
        fg.Connect(new VectorSource<byte>(payload, repeat: true), tx, throttle, _channel);
        fg.Connect(_channel, 0, spectrum, 0);
        fg.Connect(_channel, _rx, _ber);
        fg.Connect(_rx, 1, constellation, 0);

        AddView(new SpectrumPanel(new("Received spectrum: 52 used carriers", "Spektrum diterima: 52 carrier terpakai"), spectrum, -70, 0, Size: PanelSize.Half, Height: 280));
        AddView(new ConstellationPanel(new("Equalised data carriers", "Carrier data setelah ekualisasi"), constellation, _p.Constellation.Points, 1.4f,
            () => $"{_p.Constellation.Name}", PanelSize.Half));
        AddReadout(new("Frames decoded", "Frame terdekode"), () => _rx.FramesDecoded.ToString());
        AddReadout(new("Bit error rate", "Laju galat bit"), () => _ber.Frames == 0 ? "—" : _ber.Ber == 0 ? "0 (no errors)" : _ber.Ber.ToString("0.0E0", System.Globalization.CultureInfo.InvariantCulture));
        AddReadout(new("Estimated CFO", "Estimasi CFO"), () => $"{_rx.LastFrequencyOffset:0.000} subcarriers");
        AddReadout(new("Throughput", "Throughput"), () => $"{_p.BitsPerFrame * SampleRate / _p.FrameLength / 1000:0} kbit/s");
        return fg;
    }
}

/// <summary>Costas loop acquisition: watch the frequency estimate converge.</summary>
public sealed class CarrierSyncDemo : Demo
{
    private const int Sps = 4;
    private const double SampleRate = 40e3;
    private double _offsetHz = 300;
    private double _loopBandwidth = 2 * Math.PI / 100;
    private ChannelModel? _channel;
    private CostasLoop? _costas;

    public CarrierSyncDemo()
    {
        AddParameter(new NumberParameter("offset", new("Carrier offset", "Ofset pembawa"), -1500, 1500, 25, _offsetHz, v =>
        {
            _offsetHz = v;
            if (_channel is not null) _channel.Rotator.PhaseIncrement = 2 * Math.PI * _offsetHz / SampleRate;
        }) { Unit = "Hz" });
        AddParameter(new NumberParameter("bw", new("Loop bandwidth", "Lebar pita loop"), 0.005, 0.2, 0.005, _loopBandwidth, v =>
        {
            _loopBandwidth = v;
            if (_costas is not null) _costas.LoopBandwidth = _loopBandwidth;
        }) { Format = v => $"{v:0.000} rad/sym" });
        AddParameter(new ActionParameter("reacquire", new("Re-acquire", "Akuisisi ulang"), Restart));
    }

    public override string Id => "carrier-sync";
    public override DemoCategory Category => DemoCategory.Synchronization;
    public override Text Title => new("Carrier & timing recovery", "Pemulihan pembawa & waktu");

    public override Text Summary => new(
        "QPSK with a carrier offset. Gardner timing recovery feeds a 4th-order Costas loop; the trace shows the loop's frequency estimate pulling in. A wider loop locks faster but leaves more jitter.",
        "QPSK dengan ofset pembawa. Pemulihan waktu Gardner memberi masukan loop Costas orde-4; jejak menampilkan estimasi frekuensi loop yang mengunci. Loop lebih lebar mengunci lebih cepat tetapi menyisakan jitter lebih banyak.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Analog;
        using GnuRadio.Filter;
        using GnuRadio.Synchronization;

        var agc = new Agc<Complex32>(1e-3f);
        var matched = new FirFilterCcf(1, Firdes.RootRaisedCosine(1, 4, 1, 0.35, 44));
        var timing = new SymbolSync(samplesPerSymbol: 4, loopBandwidth: 0.045);
        var costas = new CostasLoop(loopBandwidth: 2 * Math.PI / 100, order: 4);
        var frequency = new GnuRadio.Sinks.TimeSink<float>(2000, 10e3, "Costas frequency");

        fg.Connect(received, agc, matched, timing, costas);
        fg.Connect(costas, 1, frequency, 0);   // optional output 1: rad/symbol
        """;

    protected override Flowgraph Build()
    {
        var c = Constellation.Qpsk();
        var symbolRate = SampleRate / Sps;
        var tx = new ConstellationModulator(c, Sps);
        var throttle = new Throttle<Complex32>(SampleRate);
        _channel = new ChannelModel(0.08f, _offsetHz / SampleRate, 1.0, seed: 2);
        var agc = new Agc<Complex32>(1e-3f, 1f);
        var matched = new FirFilterCcf(1, Firdes.RootRaisedCosine(1, Sps, 1, 0.35, 11 * Sps));
        var timing = new SymbolSync(Sps);
        var symbolAgc = new Agc<Complex32>(5e-3f, 1f);
        _costas = new CostasLoop(_loopBandwidth, 4);
        var toHz = new MultiplyConst<float>((float)(symbolRate / (2 * Math.PI)));
        var trace = new TimeSink<float>(4000, symbolRate) { UpdateRate = 10 };
        var after = new ConstellationSink(800);
        var before = new ConstellationSink(800);
        var naive = new KeepOneInN<Complex32>(Sps);

        var fg = new Flowgraph("costas");
        fg.Connect(new VectorSource<byte>(DemoData.Random(8192, 3), repeat: true), tx, throttle, _channel, agc, matched, timing, symbolAgc, _costas, after);
        fg.Connect(_costas, 1, toHz, 0);
        fg.Connect(toHz, trace);
        fg.Connect(matched, naive, before);

        AddView(new ScopePanel(new("Loop frequency estimate (Hz) · 0.4 s", "Estimasi frekuensi loop (Hz) · 0,4 dtk"), trace, -1600, 1600, Size: PanelSize.Full, Height: 220));
        AddView(new ConstellationPanel(new("Without carrier recovery", "Tanpa pemulihan pembawa"), before, c.Points, 1.6f, Size: PanelSize.Half));
        AddView(new ConstellationPanel(new("Costas loop output", "Keluaran loop Costas"), after, c.Points, 1.6f, Size: PanelSize.Half));
        AddReadout(new("Actual offset", "Ofset sebenarnya"), () => $"{_offsetHz:0} Hz");
        AddReadout(new("Estimated", "Terestimasi"), () => $"{_costas.Frequency * symbolRate / (2 * Math.PI):0.0} Hz");
        AddReadout(new("Timing estimate", "Estimasi waktu"), () => $"{timing.SamplesPerSymbol:0.0000} samples/symbol");
        return fg;
    }
}
