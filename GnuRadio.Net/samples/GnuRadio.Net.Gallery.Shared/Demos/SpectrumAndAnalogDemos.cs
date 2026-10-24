using GnuRadio.Analog;
using GnuRadio.Blocks;
using GnuRadio.Channels;
using GnuRadio.Extensions;
using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Hardware;
using GnuRadio.Sinks;

namespace GnuRadio.Gallery.Demos;

/// <summary>Sweeps the simulated FM band with a spectrum analyser, waterfall and automatic signal detection.</summary>
public sealed class BandScannerDemo : Demo
{
    private SimulatedSdrDevice? _device;
    private FrequencySink? _spectrum;
    private WaterfallSink? _waterfall;
    private SignalDetector? _detector;
    private double _center = 99.5e6;
    private double _rate = 2.4e6;
    private float _averaging = 0.35f;
    private bool _maxHold;

    public BandScannerDemo()
    {
        AddParameter(new NumberParameter("freq", new("Tuned frequency", "Frekuensi tala"), 88e6, 108e6, 100e3, _center, Retune)
        {
            IsFrequency = true,
            Help = new("Scroll a digit to tune, or click the spectrum.", "Gulir digit untuk menala, atau klik spektrum."),
        });
        AddParameter(new ChoiceParameter("span", new("Span", "Lebar sapuan"),
            [("1.2", Text.Same("1.2 MHz")), ("2.4", Text.Same("2.4 MHz"))], "2.4", v =>
            {
                _rate = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) * 1e6;
                Restart();
            }));
        AddParameter(new NumberParameter("avg", new("Averaging", "Perataan"), 0.05, 1, 0.05, _averaging, v =>
        {
            _averaging = (float)v;
            if (_spectrum is not null) _spectrum.Averaging = _averaging;
        }) { Format = v => v >= 1 ? "off" : $"α {v:0.00}" });
        AddParameter(new ChoiceParameter("hold", new("Max hold", "Tahan puncak"),
            [("off", new("Off", "Mati")), ("on", new("On", "Nyala"))], "off", v =>
            {
                _maxHold = v == "on";
                if (_spectrum is null) return;
                _spectrum.MaxHold = _maxHold;
                _spectrum.ResetMaxHold();
                Restart();
            }));
    }

    public override string Id => "band-scanner";
    public override DemoCategory Category => DemoCategory.Spectrum;
    public override Text Title => new("FM band scanner", "Pemindai pita FM");

    public override Text Summary => new(
        "A simulated SDR in the 88–108 MHz broadcast band, analysed live: spectrum with max-hold, waterfall, and automatic detection of occupied channels.",
        "SDR simulasi di pita siaran 88–108 MHz, dianalisis langsung: spektrum dengan tahan-puncak, waterfall, dan deteksi otomatis kanal yang terisi.");

    public override Text? Notes => new(
        "The same flowgraph runs against real hardware: replace SimulatedSdrDevice with Sdr.RtlSdr(), Sdr.HackRf() or new RtlTcpDevice(host). Signal detection follows gr-inspector: estimate the noise floor, threshold, merge adjacent bins.",
        "Flowgraph yang sama berjalan dengan perangkat nyata: ganti SimulatedSdrDevice dengan Sdr.RtlSdr(), Sdr.HackRf() atau new RtlTcpDevice(host). Deteksi sinyal mengikuti gr-inspector: estimasi derau dasar, ambang batas, gabungkan bin yang berdekatan.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Extensions;
        using GnuRadio.Fft;
        using GnuRadio.Hardware;
        using GnuRadio.Sinks;

        var sdr = new SimulatedSdrDevice { CenterFrequency = 99.5e6, SampleRate = 2.4e6 };
        // Real radio instead: var sdr = Sdr.RtlSdr(); or new RtlTcpDevice("192.168.1.20");

        var source = new SdrSource(sdr);
        var spectrum = new FrequencySink(2048, sdr.SampleRate, sdr.CenterFrequency) { Averaging = 0.35f };
        var waterfall = new WaterfallSink(1024, sdr.SampleRate, sdr.CenterFrequency, history: 220);
        var fft = new LogPowerFft(sdr.SampleRate, 2048, frameRate: 5, averageAlpha: 0.3f);
        var detector = new SignalDetector(2048, sdr.SampleRate, sdr.CenterFrequency, thresholdDb: 12);
        detector.SignalsDetected += signals =>
            Console.WriteLine(string.Join(", ", signals.Select(s => $"{s.CenterFrequency / 1e6:F2} MHz ({s.SnrDb:F0} dB)")));

        var fg = new Flowgraph("band_scanner");
        fg.Connect(source, 0, spectrum, 0);
        fg.Connect(source, 0, waterfall, 0);
        fg.Connect(source, fft, detector);
        fg.Start();
        // UI: <SpectrumView Sink="spectrum" /> and <WaterfallView Sink="waterfall" />
        """;

    private void Retune(double f)
    {
        _center = Math.Round(f / 1e5) * 1e5;
        if (_device is null) return;
        _device.CenterFrequency = _center;
        _spectrum!.CenterFrequency = _center;
        _spectrum.ResetMaxHold();
        _waterfall!.CenterFrequency = _center;
        _detector!.CenterFrequency = _center;
    }

    protected override Flowgraph Build()
    {
        _device = new SimulatedSdrDevice(seed: 7) { CenterFrequency = _center, SampleRate = _rate };
        var source = new SdrSource(_device);
        _spectrum = new FrequencySink(2048, _rate, _center) { Averaging = _averaging, MaxHold = _maxHold, UpdateRate = 25 };
        _waterfall = new WaterfallSink(1024, _rate, _center, 220) { UpdateRate = 25 };
        var fft = new LogPowerFft(_rate, 2048, 5, 0.3f);
        _detector = new SignalDetector(2048, _rate, _center, 12, 3);
        var fg = new Flowgraph("band_scanner");
        fg.Connect(source, 0, _spectrum, 0);
        fg.Connect(source, 0, _waterfall, 0);
        fg.Connect(source, fft, _detector);

        // Clicks go through the parameter so every UI showing the frequency follows.
        var tune = (double f) => Parameters.OfType<NumberParameter>().First(p => p.Key == "freq").Set(f);
        IReadOnlyList<(double, string)> Markers() =>
            [.. _detector.Latest.Select(s => (s.CenterFrequency, $"{s.CenterFrequency / 1e6:0.00}"))];
        AddView(new SpectrumPanel(new("Spectrum", "Spektrum"), _spectrum, -105, -25, Markers, tune, _maxHold, Height: 260));
        AddView(new WaterfallPanel(new("Waterfall", "Waterfall"), _waterfall, -100, -35, Markers, tune, Height: 260));

        AddReadout(new("Signals", "Sinyal"), () => _detector.Latest.Count.ToString());
        AddReadout(new("Strongest", "Terkuat"), () => _detector.Latest.MaxBy(s => s.PeakDb) is { } s
            ? $"{s.CenterFrequency / 1e6:0.000} MHz · {s.SnrDb:0} dB SNR" : "—");
        AddReadout(new("Throughput", "Laju data"), () => $"{source.Statistics.ProducedItems / 1e6:0.0} MS");
        return fg;
    }
}

/// <summary>Picks one station out of the band and demodulates wide-band FM.</summary>
public sealed class FmReceiverDemo : Demo
{
    private const double Center = 99.5e6;
    private const double Rate = 2.4e6;
    private FreqXlatingFirFilter? _xlate;
    private SimulatedSdrDevice? _device;
    private double _tuned = 99.9e6;
    private bool _play;

    public FmReceiverDemo()
    {
        AddParameter(new NumberParameter("tune", new("Station", "Stasiun"), Center - 1.1e6, Center + 1.1e6, 100e3, _tuned, f =>
        {
            _tuned = f;
            if (_xlate is not null) _xlate.CenterFrequency = _tuned - Center;
        }) { IsFrequency = true });
        if (OperatingSystem.IsWindows())
            AddParameter(new ChoiceParameter("audio", new("Speaker", "Speaker"),
                [("off", new("Muted", "Senyap")), ("on", new("Play", "Putar"))], "off", v =>
                {
                    _play = v == "on";
                    Restart();
                }) { Help = new("Plays on this computer's sound card.", "Diputar di kartu suara komputer ini.") });
    }

    public override string Id => "fm-receiver";
    public override DemoCategory Category => DemoCategory.Analog;
    public override Text Title => new("Broadcast FM receiver", "Penerima FM siaran");

    public override Text Summary => new(
        "Frequency-translating filter selects one 200 kHz channel from a 2.4 MS/s capture, then a WBFM receiver demodulates, de-emphasises and decimates to 48 kHz audio.",
        "Filter translasi frekuensi memilih satu kanal 200 kHz dari tangkapan 2,4 MS/s, lalu penerima WBFM mendemodulasi, de-emphasis dan menurunkan laju ke audio 48 kHz.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Analog;
        using GnuRadio.Blocks;
        using GnuRadio.Filter;
        using GnuRadio.Hardware;
        using GnuRadio.Sinks;

        const double rate = 2.4e6, center = 99.5e6;
        var sdr = new SimulatedSdrDevice { CenterFrequency = center, SampleRate = rate };

        // 2.4 MS/s → 240 kS/s around the station, then FM demod → 48 kHz audio.
        var channel = new FreqXlatingFirFilter(10, Firdes.LowPass(1, rate, 100e3, 50e3), 99.9e6 - center, rate);
        var fm = new WbfmReceive(quadratureRate: 240e3, audioDecimation: 5);
        var scope = new TimeSink<float>(1024, 48e3, "Audio");

        var fg = new Flowgraph("wbfm");
        fg.Connect(new SdrSource(sdr), channel, fm, scope);
        fg.Connect(fm, 0, new AudioSink(48_000), 0);   // Windows sound card
        fg.Start();

        channel.CenterFrequency = 99.1e6 - center;      // retune live
        """;

    protected override Flowgraph Build()
    {
        _device = new SimulatedSdrDevice(seed: 3) { CenterFrequency = Center, SampleRate = Rate };
        var source = new SdrSource(_device);
        var rf = new FrequencySink(2048, Rate, Center) { Averaging = 0.4f, UpdateRate = 20 };
        _xlate = new FreqXlatingFirFilter(10, Firdes.LowPass(1, Rate, 100e3, 50e3), _tuned - Center, Rate);
        var channel = new FrequencySink(1024, 240e3, 0) { Averaging = 0.4f, UpdateRate = 20 };
        var fm = new WbfmReceive(240e3, 5);
        var audioScope = new TimeSink<float>(960, 48e3, "Audio") { UpdateRate = 20 };
        var audioToComplex = new FloatToComplexReal();
        var audioSpectrum = new FrequencySink(1024, 48e3) { Averaging = 0.3f, UpdateRate = 20 };
        var rms = new Rms<float>(0.001f);
        var level = new NumberSink("Audio", 0.05f);

        var fg = new Flowgraph("wbfm");
        fg.Connect(source, 0, rf, 0);
        fg.Connect(source, _xlate);
        fg.Connect(_xlate, 0, channel, 0);
        fg.Connect(_xlate, fm);
        fg.Connect(fm, 0, audioScope, 0);
        fg.Connect(fm, audioToComplex, audioSpectrum);
        fg.Connect(fm, rms, level);
        if (_play && OperatingSystem.IsWindows()) fg.Connect(fm, 0, new AudioSink(48_000) { Volume = 0.8f }, 0);

        AddView(new SpectrumPanel(new("RF · 2.4 MHz", "RF · 2,4 MHz"), rf, -105, -25,
            () => [(_tuned, "▼ " + Axis.FormatFrequency(_tuned, 1))], f => Tune(f), Height: 220));
        AddView(new SpectrumPanel(new("Selected channel · 240 kHz", "Kanal terpilih · 240 kHz"), channel, -100, -20, Size: PanelSize.Half, Height: 200));
        AddView(new ScopePanel(new("Demodulated audio", "Audio hasil demodulasi"), audioScope, -1.5f, 1.5f, Height: 200));
        AddView(new SpectrumPanel(new("Audio spectrum", "Spektrum audio"), audioSpectrum, -90, 0, Size: PanelSize.Half, Height: 200));

        AddReadout(new("Station", "Stasiun"), () => NearestStation() ?? "—");
        AddReadout(new("Offset", "Ofset"), () => Axis.FormatFrequency(_tuned - Center, 1));
        AddReadout(new("Audio RMS", "RMS audio"), () => $"{20 * Math.Log10(Math.Max(level.Value, 1e-6)):0.0} dBFS");
        return fg;
    }

    private void Tune(double f)
    {
        _tuned = Math.Clamp(Math.Round(f / 1e5) * 1e5, Center - 1.1e6, Center + 1.1e6);
        if (_xlate is not null) _xlate.CenterFrequency = _tuned - Center;
        if (Parameters.OfType<NumberParameter>().FirstOrDefault(p => p.Key == "tune") is { } p && p.Value != _tuned) p.Set(_tuned);
    }

    private string? NearestStation() => _device?.Emitters
        .Where(e => Math.Abs(e.Frequency - _tuned) < 60e3)
        .Select(e => e.Name)
        .FirstOrDefault();
}

/// <summary>AM and single-sideband modulation side by side.</summary>
public sealed class AmSsbDemo : Demo
{
    private const double Rate = 48e3;
    private const double Carrier = 9e3;
    private string _mode = "am";
    private AwgnChannel? _noise;
    private AmModulator? _am;
    private float _index = 0.8f;
    private float _noiseVoltage = 0.01f;

    public AmSsbDemo()
    {
        AddParameter(new ChoiceParameter("mode", new("Mode", "Mode"),
            [("am", Text.Same("AM")), ("usb", Text.Same("USB")), ("lsb", Text.Same("LSB"))], _mode, v =>
            {
                _mode = v;
                Restart();
            }));
        AddParameter(new NumberParameter("index", new("AM index", "Indeks AM"), 0.1, 1.2, 0.05, _index, v =>
        {
            _index = (float)v;
            if (_am is not null) _am.ModulationIndex = _index;
        }) { Format = v => $"{v * 100:0} %" });
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 0.3, 0.005, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }));
    }

    public override string Id => "am-ssb";
    public override DemoCategory Category => DemoCategory.Analog;
    public override Text Title => new("AM & SSB", "AM & SSB");

    public override Text Summary => new(
        "A two-tone voice-band signal modulated as full-carrier AM or as single sideband (phasing method with a Hilbert transformer), shown 9 kHz above the carrier, then demodulated.",
        "Sinyal pita suara dua nada dimodulasi sebagai AM pembawa penuh atau sideband tunggal (metode phasing dengan transformator Hilbert), ditampilkan 9 kHz di atas pembawa, lalu didemodulasi.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Analog;
        using GnuRadio.Blocks;
        using GnuRadio.Sinks;

        var voice = new Add<float>(2);
        var fg = new Flowgraph("ssb");
        fg.Connect(new SignalSource<float>(48e3, Waveform.Sine, 700, 0.5f), 0, voice, 0);
        fg.Connect(new SignalSource<float>(48e3, Waveform.Sine, 1900, 0.3f), 0, voice, 1);

        var usb = new SsbModulator(Sideband.Upper);           // x + j·H{x}
        var spectrum = new FrequencySink(2048, 48e3);
        var demod = new SsbDemod(48e3, Sideband.Upper);       // complex band-pass + real part
        var scope = new TimeSink<float>(960, 48e3);

        fg.Connect(voice, new Throttle<float>(48e3), usb);
        fg.Connect(usb, 0, spectrum, 0);
        fg.Connect(usb, demod, scope);
        fg.Start();
        // For AM: new AmModulator(0.8f) and new AmDemod()
        """;

    protected override Flowgraph Build()
    {
        var fg = new Flowgraph("am_ssb");
        // Syllable-like envelope on the tones so the audio is not static.
        var envelope = new SignalSource<float>(Rate, Waveform.Sine, 3, 0.15f, 0.85f);
        var tones = new Add<float>(2);
        fg.Connect(new SignalSource<float>(Rate, Waveform.Sine, 700, 0.45f), 0, tones, 0);
        fg.Connect(new SignalSource<float>(Rate, Waveform.Sine, 1900, 0.25f), 0, tones, 1);
        var shaped = new Multiply<float>(2);
        fg.Connect(tones, 0, shaped, 0);
        fg.Connect(envelope, 0, shaped, 1);
        var throttle = new Throttle<float>(Rate);
        fg.Connect(shaped, throttle);

        Block modulator = _mode switch
        {
            "usb" => new SsbModulator(Sideband.Upper),
            "lsb" => new SsbModulator(Sideband.Lower),
            _ => _am = new AmModulator(_index),
        };
        var up = new Rotator(2 * Math.PI * Carrier / Rate);
        _noise = new AwgnChannel(_noiseVoltage, 1);
        var spectrum = new FrequencySink(2048, Rate) { Averaging = 0.25f, UpdateRate = 20 };
        var down = new Rotator(-2 * Math.PI * Carrier / Rate);
        Block demod = _mode switch
        {
            "usb" => new SsbDemod(Rate, Sideband.Upper),
            "lsb" => new SsbDemod(Rate, Sideband.Lower),
            _ => new AmDemod(0.0005f),
        };
        var scope = new TimeSink<float>(1920, Rate, "Audio", inputs: 2) { UpdateRate = 20 };
        fg.Connect(throttle, modulator, up, _noise);
        fg.Connect(_noise, 0, spectrum, 0);
        fg.Connect(_noise, down, demod);
        fg.Connect(demod, 0, scope, 0);
        fg.Connect(throttle, 0, scope, 1);

        AddView(new SpectrumPanel(new("Transmitted spectrum (carrier at +9 kHz)", "Spektrum terkirim (pembawa di +9 kHz)"), spectrum, -90, 0,
            () => [(Carrier, _mode == "am" ? "carrier" : "suppressed carrier")], Height: 240));
        AddView(new ScopePanel(new("Recovered audio vs original", "Audio pulih vs asli"), scope, -1.4f, 1.4f, Size: PanelSize.Full, Height: 220));
        AddReadout(new("Mode", "Mode"), () => _mode.ToUpperInvariant());
        AddReadout(new("Occupied bandwidth", "Lebar pita terpakai"), () => _mode == "am" ? "≈ 3.8 kHz (2 × 1.9 kHz)" : "≈ 1.9 kHz");
        return fg;
    }
}

/// <summary>Morse code: keyed tone, noise, envelope detection and adaptive decoding.</summary>
public sealed class MorseDemo : Demo
{
    private const double Rate = 8000;
    private string _message = "CQ CQ DE YB0GRC K";
    private double _wpm = 18;
    private float _noiseVoltage = 0.15f;
    private AwgnChannel? _noise;
    private MorseDecoder? _decoder;

    public MorseDemo()
    {
        AddParameter(new ChoiceParameter("text", new("Message", "Pesan"),
            [("CQ CQ DE YB0GRC K", Text.Same("CQ … YB0GRC")), ("GNURADIO NET 73", Text.Same("GNURADIO NET 73")), ("SOS SOS SOS", Text.Same("SOS"))],
            _message, v =>
            {
                _message = v;
                Restart();
            }));
        AddParameter(new NumberParameter("wpm", new("Speed", "Kecepatan"), 8, 35, 1, _wpm, v =>
        {
            _wpm = v;
            Restart();
        }) { Unit = "WPM" });
        AddParameter(new NumberParameter("noise", new("Noise", "Derau"), 0, 0.8, 0.01, _noiseVoltage, v =>
        {
            _noiseVoltage = (float)v;
            if (_noise is not null) _noise.NoiseVoltage = _noiseVoltage;
        }));
    }

    public override string Id => "morse";
    public override DemoCategory Category => DemoCategory.Analog;
    public override Text Title => new("CW / Morse", "CW / Morse");

    public override Text Summary => new(
        "Text is keyed into a 700 Hz tone with click-free raised-cosine edges, buried in noise, narrow-filtered, envelope-detected and decoded with an adaptive threshold.",
        "Teks di-key menjadi nada 700 Hz dengan tepi raised-cosine tanpa klik, ditimbun derau, difilter sempit, dideteksi selubungnya dan didekode dengan ambang adaptif.");

    public override string Code => """
        using GnuRadio;
        using GnuRadio.Analog;
        using GnuRadio.Blocks;
        using GnuRadio.Channels;
        using GnuRadio.Filter;

        var keyer = new MorseKeyer("CQ CQ DE YB0GRC K", 8000, wordsPerMinute: 18);
        var tone = new SignalSource<Complex32>(8000, Waveform.Cosine, 700);
        var keyed = new Multiply<Complex32>(2);
        var toComplex = new FloatToComplexReal();
        var narrow = new FreqXlatingFirFilter(4, Firdes.LowPass(1, 8000, 80, 60), 700, 8000);
        var decoder = new MorseDecoder(2000, wordsPerMinute: 18);
        decoder.TextDecoded += Console.Write;

        var fg = new Flowgraph("cw");
        fg.Connect(keyer, toComplex);
        fg.Connect(toComplex, 0, keyed, 0);
        fg.Connect(tone, 0, keyed, 1);
        fg.Connect(keyed, new Throttle<Complex32>(8000), new AwgnChannel(0.15f), narrow, new ComplexToMag(), decoder);
        fg.Start();
        """;

    protected override Flowgraph Build()
    {
        var keyer = new MorseKeyer(_message + "   ", Rate, _wpm);
        var tone = new SignalSource<Complex32>(Rate, Waveform.Cosine, 700, 0.7f);
        var keyed = new Multiply<Complex32>(2);
        var toComplex = new FloatToComplexReal();
        _noise = new AwgnChannel(_noiseVoltage, 5);
        var spectrum = new FrequencySink(1024, Rate) { Averaging = 0.4f, UpdateRate = 20 };
        var waterfall = new WaterfallSink(512, Rate, 0, 200) { UpdateRate = 20 };
        var narrow = new FreqXlatingFirFilter(4, Firdes.LowPass(1, Rate, 80, 60), 700, Rate);
        var mag = new ComplexToMag();
        _decoder = new MorseDecoder(Rate / 4, _wpm);
        var scope = new TimeSink<float>(4000, Rate / 4) { UpdateRate = 10 };

        var fg = new Flowgraph("cw");
        fg.Connect(keyer, toComplex);
        fg.Connect(toComplex, 0, keyed, 0);
        fg.Connect(tone, 0, keyed, 1);
        var throttle = new Throttle<Complex32>(Rate);
        fg.Connect(keyed, throttle, _noise);
        fg.Connect(_noise, 0, spectrum, 0);
        fg.Connect(_noise, 0, waterfall, 0);
        fg.Connect(_noise, narrow, mag, _decoder);
        fg.Connect(mag, 0, scope, 0);

        AddView(new WaterfallPanel(new("Waterfall", "Waterfall"), waterfall, -80, -10, Size: PanelSize.Half, Height: 220));
        AddView(new SpectrumPanel(new("Spectrum", "Spektrum"), spectrum, -80, 0, () => [(700, "700 Hz")], Size: PanelSize.Half, Height: 220));
        AddView(new ScopePanel(new("Envelope after 80 Hz filter (2 s)", "Selubung setelah filter 80 Hz (2 dtk)"), scope, -0.1f, 0.9f, Size: PanelSize.Full, Height: 180));
        AddView(new TextPanel(new("Decoded", "Hasil dekode"), () => Tail(_decoder.Text, 400), PanelSize.Full, 90));
        AddReadout(new("Dot length", "Panjang titik"), () => $"{Morse.DotSeconds(_wpm) * 1000:0} ms");
        AddReadout(new("Characters", "Karakter"), () => _decoder.Text.Length.ToString());
        return fg;
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
}
