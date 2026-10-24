using System.Diagnostics;

namespace GnuRadio.Hardware;

/// <summary>Kinds of transmitters in a <see cref="SimulatedSdrDevice"/> scene.</summary>
public enum EmitterKind
{
    /// <summary>Unmodulated carrier.</summary>
    Carrier,

    /// <summary>Wide-band FM broadcast with a stereo-like multi-tone program.</summary>
    BroadcastFm,

    /// <summary>Narrow-band FM voice-like channel.</summary>
    NarrowFm,

    /// <summary>AM with a slowly varying audio envelope.</summary>
    Am,

    /// <summary>QPSK data carrier with a raised-cosine-like spectrum.</summary>
    Qpsk,

    /// <summary>A carrier that keys on and off (telemetry bursts).</summary>
    Burst,
}

/// <summary>A transmitter in the simulated RF environment.</summary>
public sealed record Emitter(double Frequency, double PowerDb, EmitterKind Kind = EmitterKind.Carrier, double Bandwidth = 10e3, string Name = "");

/// <summary>
/// A virtual SDR that synthesises an RF scene around its tuned frequency: any <see cref="Emitter"/> inside the
/// sampled bandwidth appears at the right offset, over a noise floor. Paced to real time, so applications,
/// demos and tests behave as with real hardware, and retuning moves through the band.
/// </summary>
public sealed class SimulatedSdrDevice : ISdrDevice
{
    private readonly FastRandom _rng;
    private readonly Lock _lock = new();
    private List<Emitter> _emitters;
    private readonly Dictionary<Emitter, EmitterState> _states = new();
    private long _startTicks;
    private long _produced;
    private double _rate = 2.4e6;
    private double _frequency = 100e6;

    private sealed class EmitterState
    {
        public double CarrierPhase;
        public double ModPhase;
        public double ModPhase2;
        public Complex32 Symbol = Complex32.One;
        public Complex32 Smoothed;
        public int SymbolCounter;
        public bool On = true;
        public long Toggle;
    }

    public SimulatedSdrDevice(IEnumerable<Emitter>? emitters = null, double noiseFloorDb = -90, long seed = 0)
    {
        _emitters = (emitters ?? DefaultScene()).ToList();
        NoiseFloorDb = noiseFloorDb;
        _rng = new FastRandom(seed);
    }

    /// <summary>An FM broadcast band (88–108 MHz) with stations plus a few utility signals.</summary>
    public static IEnumerable<Emitter> DefaultScene() =>
    [
        new(88.9e6, -42, EmitterKind.BroadcastFm, 180e3, "88.9 FM"),
        new(90.5e6, -55, EmitterKind.BroadcastFm, 180e3, "90.5 FM"),
        new(93.4e6, -38, EmitterKind.BroadcastFm, 180e3, "93.4 FM"),
        new(97.1e6, -48, EmitterKind.BroadcastFm, 180e3, "97.1 FM"),
        new(99.9e6, -35, EmitterKind.BroadcastFm, 180e3, "99.9 FM"),
        new(100.6e6, -62, EmitterKind.NarrowFm, 12e3, "Utility NBFM"),
        new(101.2e6, -58, EmitterKind.Qpsk, 200e3, "Data link"),
        new(102.7e6, -44, EmitterKind.BroadcastFm, 180e3, "102.7 FM"),
        new(104.3e6, -70, EmitterKind.Burst, 25e3, "Telemetry"),
        new(105.8e6, -40, EmitterKind.BroadcastFm, 180e3, "105.8 FM"),
        new(107.5e6, -52, EmitterKind.BroadcastFm, 180e3, "107.5 FM"),
        new(100.05e6, -75, EmitterKind.Carrier, 0, "Spur"),
    ];

    public string Driver => "simulated";
    public string Label => "Simulated SDR";
    public bool CanTransmit => false;
    public double NoiseFloorDb { get; set; }

    /// <summary>When true (default) reads are paced to the sample rate like real hardware.</summary>
    public bool RealTime { get; set; } = true;

    public IReadOnlyList<Emitter> Emitters
    {
        get
        {
            lock (_lock) return [.. _emitters];
        }
        set
        {
            lock (_lock) _emitters = [.. value];
        }
    }

    public double SampleRate
    {
        get => _rate;
        set
        {
            _rate = value;
            ResetClock();
        }
    }

    public double CenterFrequency
    {
        get => _frequency;
        set => _frequency = value;
    }

    public double Bandwidth { get; set; }
    public double Gain { get; set; }
    public bool AutomaticGain { get; set; }

    public void Start(SdrDirection direction = SdrDirection.Receive)
    {
        if (direction == SdrDirection.Transmit) throw new NotSupportedException("The simulated SDR only receives.");
        ResetClock();
    }

    private void ResetClock()
    {
        _startTicks = Stopwatch.GetTimestamp();
        _produced = 0;
    }

    public void Stop()
    {
    }

    public int Read(Span<Complex32> buffer, TimeSpan timeout)
    {
        var n = buffer.Length;
        if (RealTime)
        {
            var due = Stopwatch.GetElapsedTime(_startTicks).TotalSeconds * _rate - _produced;
            if (due < 1)
            {
                Thread.Sleep((int)Math.Min(timeout.TotalMilliseconds, 5));
                due = Stopwatch.GetElapsedTime(_startTicks).TotalSeconds * _rate - _produced;
            }
            n = (int)Math.Clamp(due, 0, Math.Min(buffer.Length, _rate / 20));
            if (n == 0) return 0;
        }
        var gainLinear = (float)Math.Pow(10, Gain / 20);
        var noise = (float)Math.Pow(10, NoiseFloorDb / 20) * gainLinear;
        for (var i = 0; i < n; i++) buffer[i] = _rng.NextComplexGaussian() * noise;

        List<Emitter> emitters;
        lock (_lock) emitters = _emitters;
        foreach (var e in emitters)
        {
            var offset = e.Frequency - _frequency;
            if (Math.Abs(offset) > _rate / 2 + e.Bandwidth) continue;
            if (!_states.TryGetValue(e, out var st)) _states[e] = st = new EmitterState { CarrierPhase = _rng.NextDouble() * 6.28 };
            var amplitude = (float)Math.Pow(10, e.PowerDb / 20) * gainLinear;
            var inc = 2 * Math.PI * offset / _rate;
            for (var i = 0; i < n; i++)
            {
                Complex32 v;
                switch (e.Kind)
                {
                    case EmitterKind.BroadcastFm:
                    {
                        // Program: two tones and a slow sweep → deviation up to 75 kHz.
                        st.ModPhase += 2 * Math.PI * 1000 / _rate;
                        st.ModPhase2 += 2 * Math.PI * 0.3 / _rate;
                        var audio = 0.5 * Math.Sin(st.ModPhase) + 0.3 * Math.Sin(st.ModPhase * 3.7) + 0.2 * Math.Sin(st.ModPhase2);
                        st.CarrierPhase += inc + 2 * Math.PI * 75e3 * audio / _rate;
                        v = Complex32.FromPolar(amplitude, (float)st.CarrierPhase);
                        break;
                    }
                    case EmitterKind.NarrowFm:
                    {
                        st.ModPhase += 2 * Math.PI * 700 / _rate;
                        st.CarrierPhase += inc + 2 * Math.PI * 3e3 * Math.Sin(st.ModPhase) / _rate;
                        v = Complex32.FromPolar(amplitude, (float)st.CarrierPhase);
                        break;
                    }
                    case EmitterKind.Am:
                    {
                        st.ModPhase += 2 * Math.PI * 400 / _rate;
                        st.CarrierPhase += inc;
                        v = Complex32.FromPolar(amplitude * (float)(1 + 0.6 * Math.Sin(st.ModPhase)), (float)st.CarrierPhase);
                        break;
                    }
                    case EmitterKind.Qpsk:
                    {
                        var samplesPerSymbol = Math.Max(1, (int)(_rate / Math.Max(e.Bandwidth / 1.35, 1)));
                        if (++st.SymbolCounter >= samplesPerSymbol)
                        {
                            st.SymbolCounter = 0;
                            st.Symbol = new Complex32(_rng.NextBit() == 0 ? -0.7071f : 0.7071f, _rng.NextBit() == 0 ? -0.7071f : 0.7071f);
                        }
                        st.Smoothed = st.Smoothed * 0.6f + st.Symbol * 0.4f;
                        st.CarrierPhase += inc;
                        v = st.Smoothed * Complex32.FromPolar(amplitude, (float)st.CarrierPhase);
                        break;
                    }
                    case EmitterKind.Burst:
                    {
                        if (--st.Toggle <= 0)
                        {
                            st.On = !st.On;
                            st.Toggle = (long)(_rate * (st.On ? 0.15 : 0.6));
                        }
                        st.CarrierPhase += inc;
                        v = st.On ? Complex32.FromPolar(amplitude, (float)st.CarrierPhase) : default;
                        break;
                    }
                    default:
                        st.CarrierPhase += inc;
                        v = Complex32.FromPolar(amplitude, (float)st.CarrierPhase);
                        break;
                }
                if (st.CarrierPhase > Math.PI * 64) st.CarrierPhase %= 2 * Math.PI;
                buffer[i] += v;
            }
        }
        _produced += n;
        return n;
    }

    public int Write(ReadOnlySpan<Complex32> buffer, TimeSpan timeout) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}
