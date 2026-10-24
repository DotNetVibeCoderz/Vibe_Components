using System.Runtime.InteropServices;
using GnuRadio.Blocks;
using GnuRadio.Filter;

namespace GnuRadio.Analog;

/// <summary>
/// FM discriminator: gain · arg(x[n]·conj(x[n−1])) (GNU Radio's <c>analog.quadrature_demod_cf</c>).
/// For a deviation of Δf at sample rate fs, a gain of fs/(2π·Δf) maps full deviation to ±1.
/// </summary>
public sealed class QuadratureDemod(float gain) : SyncBlock<Complex32, float>("quadrature_demod_cf")
{
    private Complex32 _last = Complex32.One;

    public float Gain { get; set; } = gain;

    /// <summary>Gain that maps a deviation of <paramref name="maxDeviation"/> Hz to ±1.</summary>
    public static float GainFor(double sampleRate, double maxDeviation) => (float)(sampleRate / (2 * Math.PI * maxDeviation));

    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        var g = Gain;
        var prev = _last;
        for (var i = 0; i < input.Length; i++)
        {
            var x = input[i];
            // arg(x · conj(prev)) without forming the full product twice.
            var re = x.Real * prev.Real + x.Imaginary * prev.Imaginary;
            var im = x.Imaginary * prev.Real - x.Real * prev.Imaginary;
            output[i] = g * MathF.Atan2(im, re);
            prev = x;
        }
        _last = prev;
    }
}

/// <summary>Phase accumulator FM: y = e^(jφ), φ += sensitivity·x (GNU Radio's <c>analog.frequency_modulator_fc</c>).</summary>
public sealed class FrequencyModulator(float sensitivity) : SyncBlock<float, Complex32>("frequency_modulator_fc")
{
    private double _phase;

    /// <summary>Radians per sample per unit input; 2π·Δf/fs for a deviation of Δf at full scale.</summary>
    public float Sensitivity { get; set; } = sensitivity;

    public static float SensitivityFor(double sampleRate, double maxDeviation) => (float)(2 * Math.PI * maxDeviation / sampleRate);

    protected override void Work(ReadOnlySpan<float> input, Span<Complex32> output)
    {
        var k = Sensitivity;
        var phase = _phase;
        for (var i = 0; i < input.Length; i++)
        {
            phase += k * input[i];
            if (phase > Math.PI) phase -= 2 * Math.PI;
            else if (phase < -Math.PI) phase += 2 * Math.PI;
            output[i] = Complex32.FromPolar(1f, (float)phase);
        }
        _phase = phase;
    }
}

/// <summary>y = e^(j·sensitivity·x) (GNU Radio's <c>analog.phase_modulator_fc</c>).</summary>
public sealed class PhaseModulator(float sensitivity) : SyncBlock<float, Complex32>("phase_modulator_fc")
{
    public float Sensitivity { get; set; } = sensitivity;

    protected override void Work(ReadOnlySpan<float> input, Span<Complex32> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = Complex32.FromPolar(1f, Sensitivity * input[i]);
    }
}

/// <summary>Phase demodulator: gain · arg(x) (pair with <see cref="PhaseModulator"/>).</summary>
public sealed class PhaseDemod(float gain = 1f) : SyncBlock<Complex32, float>("phase_demod_cf")
{
    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = gain * input[i].Phase;
    }
}

/// <summary>
/// Double-sideband full-carrier AM at complex baseband: y = 1 + m·x (GNU Radio builds this from
/// <c>add_const</c> and <c>multiply_const</c>; packaged here as one block).
/// </summary>
public sealed class AmModulator(float modulationIndex = 0.8f) : SyncBlock<float, Complex32>("am_modulator_fc")
{
    public float ModulationIndex { get; set; } = modulationIndex;

    protected override void Work(ReadOnlySpan<float> input, Span<Complex32> output)
    {
        var m = ModulationIndex;
        for (var i = 0; i < input.Length; i++) output[i] = new Complex32(1f + m * input[i], 0f);
    }
}

/// <summary>Envelope AM detector with carrier (DC) removal (GNU Radio's <c>analog.am_demod_cf</c> core).</summary>
public sealed class AmDemod(float dcAlpha = 0.001f) : SyncBlock<Complex32, float>("am_demod_cf")
{
    private float _dc;

    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        var dc = _dc;
        for (var i = 0; i < input.Length; i++)
        {
            var env = input[i].Magnitude;
            dc += dcAlpha * (env - dc);
            output[i] = env - dc;
        }
        _dc = dc;
    }
}

/// <summary>SSB sideband.</summary>
public enum Sideband
{
    Upper,
    Lower,
}

/// <summary>Single-sideband modulator by the phasing method: x ± j·H{x} (Hilbert).</summary>
public sealed class SsbModulator : SyncBlock<float, Complex32>
{
    private readonly FirFilter<float> _hilbert;
    private readonly float[] _delay;
    private int _delayPos;
    private float[] _q = [];

    public SsbModulator(Sideband sideband, int hilbertTaps = 129) : base("ssb_modulator_fc")
    {
        Sideband = sideband;
        _hilbert = new FirFilter<float>(Firdes.Hilbert(hilbertTaps, WindowType.Hamming));
        _delay = new float[(hilbertTaps - 1) / 2];
    }

    public Sideband Sideband { get; set; }

    protected override void Work(ReadOnlySpan<float> input, Span<Complex32> output)
    {
        if (_q.Length < input.Length) _q = new float[input.Length];
        var q = _q.AsSpan(0, input.Length);
        _hilbert.Filter(input, q);
        var sign = Sideband == Sideband.Upper ? 1f : -1f;
        for (var i = 0; i < input.Length; i++)
        {
            var re = _delay[_delayPos];
            _delay[_delayPos] = input[i];
            _delayPos = (_delayPos + 1) % _delay.Length;
            output[i] = new Complex32(re, sign * q[i]);
        }
    }
}

/// <summary>
/// SSB demodulator for complex baseband: keeps one sideband with a complex band-pass, then takes the
/// real part (product detection).
/// </summary>
public sealed class SsbDemod : HierBlock
{
    public SsbDemod(double sampleRate, Sideband sideband, double lowCut = 300, double highCut = 3000) : base("ssb_demod_cf")
    {
        DeclareInput<Complex32>();
        DeclareOutput<float>();
        var taps = sideband == Sideband.Upper
            ? Firdes.ComplexBandPass(1, sampleRate, lowCut, highCut, 200)
            : Firdes.ComplexBandPass(1, sampleRate, -highCut, -lowCut, 200);
        var filter = new FirFilterCcc(1, taps);
        var real = new ComplexToReal();
        Connect(filter, real);
        ConnectInput(0, filter);
        ConnectOutput(0, real);
    }
}

/// <summary>
/// Automatic gain control: y = g·x, g += rate·(reference − |y|) (GNU Radio's <c>analog.agc_cc/ff</c>).
/// </summary>
public sealed class Agc<T> : SyncBlock<T, T> where T : unmanaged
{
    private float _gain;

    public Agc(float rate = 1e-4f, float reference = 1f, float gain = 1f, float maxGain = 65536f) : base("agc")
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("Agc supports float and Complex32.");
        Rate = rate;
        Reference = reference;
        _gain = gain;
        MaxGain = maxGain;
    }

    public float Rate { get; set; }
    public float Reference { get; set; }
    public float MaxGain { get; set; }
    public float Gain => _gain;

    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        var g = _gain;
        if (typeof(T) == typeof(Complex32))
        {
            var ci = MemoryMarshal.Cast<T, Complex32>(input);
            var co = MemoryMarshal.Cast<T, Complex32>(output);
            for (var i = 0; i < ci.Length; i++)
            {
                var y = ci[i] * g;
                co[i] = y;
                g += Rate * (Reference - y.Magnitude);
                g = Math.Clamp(g, 0f, MaxGain);
            }
        }
        else
        {
            var fi = MemoryMarshal.Cast<T, float>(input);
            var fo = MemoryMarshal.Cast<T, float>(output);
            for (var i = 0; i < fi.Length; i++)
            {
                var y = fi[i] * g;
                fo[i] = y;
                g += Rate * (Reference - MathF.Abs(y));
                g = Math.Clamp(g, 0f, MaxGain);
            }
        }
        _gain = g;
    }
}

/// <summary>What a <see cref="Pll"/> block outputs.</summary>
public enum PllOutput
{
    /// <summary>The input mixed down by the tracked carrier (GNU Radio's <c>pll_carriertracking_cc</c>).</summary>
    CarrierTracking,

    /// <summary>The locked reference carrier itself (GNU Radio's <c>pll_refout_cc</c>).</summary>
    Reference,
}

/// <summary>
/// Phase-locked loop on a complex carrier. <see cref="LoopBandwidth"/> is in rad/sample (≈2π/100 to 2π/200 is typical);
/// <see cref="Frequency"/> exposes the tracked frequency in rad/sample.
/// </summary>
public sealed class Pll : SyncBlock<Complex32, Complex32>
{
    private readonly ControlLoop _loop;

    public Pll(double loopBandwidth, double maxFrequency, double minFrequency, PllOutput output = PllOutput.CarrierTracking)
        : base(output == PllOutput.Reference ? "pll_refout_cc" : "pll_carriertracking_cc")
    {
        _loop = new ControlLoop(loopBandwidth, maxFrequency, minFrequency);
        Output = output;
    }

    public PllOutput Output { get; }
    public double Frequency => _loop.Frequency;
    public double Phase => _loop.Phase;

    public double LoopBandwidth
    {
        get => _loop.LoopBandwidth;
        set => _loop.LoopBandwidth = value;
    }

    /// <summary>Smoothed |phase error|; near zero when locked.</summary>
    public float LockError { get; private set; } = 1f;

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        var lockError = LockError;
        for (var i = 0; i < input.Length; i++)
        {
            var nco = Complex32.FromPolar(1f, (float)-_loop.Phase);
            var mixed = input[i] * nco;
            output[i] = Output == PllOutput.Reference ? nco.Conjugate() : mixed;
            var error = mixed.Phase;
            _loop.Advance(error);
            lockError = 0.999f * lockError + 0.001f * MathF.Abs(error);
        }
        LockError = lockError;
    }
}

/// <summary>PLL frequency detector: outputs the tracked frequency in rad/sample (GNU Radio's <c>analog.pll_freqdet_cf</c>).</summary>
public sealed class PllFreqDet(double loopBandwidth, double maxFrequency, double minFrequency) : SyncBlock<Complex32, float>("pll_freqdet_cf")
{
    private readonly ControlLoop _loop = new(loopBandwidth, maxFrequency, minFrequency);

    protected override void Work(ReadOnlySpan<Complex32> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var mixed = input[i] * Complex32.FromPolar(1f, (float)-_loop.Phase);
            _loop.Advance(mixed.Phase);
            output[i] = (float)_loop.Frequency;
        }
    }
}

/// <summary>Emphasis networks for FM broadcast audio.</summary>
public static class Emphasis
{
    /// <summary>De-emphasis IIR taps (feed-forward, feedback) for time constant <paramref name="tau"/> (75 µs US, 50 µs EU).</summary>
    public static (double[] Feedforward, double[] Feedback) Deemphasis(double sampleRate, double tau = 75e-6)
    {
        var wc = 1.0 / tau;
        var wca = 2.0 * sampleRate * Math.Tan(wc / (2.0 * sampleRate));
        var k = -wca / (2.0 * sampleRate);
        var p1 = (1.0 + k) / (1.0 - k);
        var b0 = -k / (1.0 - k);
        return ([b0, b0], [1.0, -p1]);
    }

    /// <summary>Pre-emphasis taps: a zero at 1/τ and a pole near Nyquist, unity gain at DC.</summary>
    public static (double[] Feedforward, double[] Feedback) Preemphasis(double sampleRate, double tau = 75e-6)
    {
        var tauHigh = 1.0 / (2 * Math.PI * 0.925 * sampleRate / 2);
        var k = 2 * sampleRate;
        var a0 = 1 + tauHigh * k;
        return ([(1 + tau * k) / a0, (1 - tau * k) / a0], [1.0, (1 - tauHigh * k) / a0]);
    }

    public static IirFilter DeemphasisFilter(double sampleRate, double tau = 75e-6)
    {
        var (b, a) = Deemphasis(sampleRate, tau);
        return new IirFilter(b, a);
    }

    public static IirFilter PreemphasisFilter(double sampleRate, double tau = 75e-6)
    {
        var (b, a) = Preemphasis(sampleRate, tau);
        return new IirFilter(b, a);
    }
}

/// <summary>
/// Wide-band FM broadcast receiver: discriminator, audio low-pass with decimation, de-emphasis
/// (GNU Radio's <c>analog.wfm_rcv</c>). Input at the quadrature rate, output at quadrature rate / decimation.
/// </summary>
public sealed class WbfmReceive : HierBlock
{
    public WbfmReceive(double quadratureRate, int audioDecimation, double deviation = 75e3, double tau = 75e-6) : base("wfm_rcv")
    {
        DeclareInput<Complex32>();
        DeclareOutput<float>();
        var demod = new QuadratureDemod(QuadratureDemod.GainFor(quadratureRate, deviation));
        var audio = new FirFilterFff(audioDecimation, Firdes.LowPass(1, quadratureRate, 15e3, 4e3));
        var deemph = Emphasis.DeemphasisFilter(quadratureRate / audioDecimation, tau);
        Connect(demod, audio, deemph);
        ConnectInput(0, demod);
        ConnectOutput(0, deemph);
    }
}

/// <summary>Narrow-band FM receiver (GNU Radio's <c>analog.nbfm_rx</c>).</summary>
public sealed class NbfmReceive : HierBlock
{
    public NbfmReceive(double quadratureRate, int audioDecimation, double deviation = 5e3, double tau = 75e-6) : base("nbfm_rx")
    {
        DeclareInput<Complex32>();
        DeclareOutput<float>();
        var demod = new QuadratureDemod(QuadratureDemod.GainFor(quadratureRate, deviation));
        var deemph = Emphasis.DeemphasisFilter(quadratureRate, tau);
        var audio = new FirFilterFff(audioDecimation, Firdes.LowPass(1, quadratureRate, 3.5e3, 1e3));
        Connect(demod, deemph, audio);
        ConnectInput(0, demod);
        ConnectOutput(0, audio);
    }
}

/// <summary>
/// FM transmitter: pre-emphasis, interpolation from audio to quadrature rate, frequency modulation
/// (GNU Radio's <c>analog.wfm_tx</c>/<c>nbfm_tx</c>). The quadrature rate must be an integer multiple of the audio rate.
/// </summary>
public sealed class FmTransmit : HierBlock
{
    public FmTransmit(double audioRate, double quadratureRate, double deviation = 75e3, double tau = 75e-6) : base("fm_tx")
    {
        var interp = (int)Math.Round(quadratureRate / audioRate);
        if (Math.Abs(interp * audioRate - quadratureRate) > 1e-6)
            throw new ArgumentException("Quadrature rate must be an integer multiple of the audio rate.");
        DeclareInput<float>();
        DeclareOutput<Complex32>();
        var preemph = Emphasis.PreemphasisFilter(audioRate, tau);
        var taps = Firdes.LowPass(interp, quadratureRate, Math.Min(15e3, audioRate * 0.45), audioRate * 0.1);
        var up = new InterpFirFilter<float>(interp, taps);
        var mod = new FrequencyModulator(FrequencyModulator.SensitivityFor(quadratureRate, deviation));
        Connect(preemph, up, mod);
        ConnectInput(0, preemph);
        ConnectOutput(0, mod);
    }
}
