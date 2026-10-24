using System.Runtime.InteropServices;
using GnuRadio.Kernels;

namespace GnuRadio.Filter;

/// <summary>Decimating FIR, float samples, float taps (GNU Radio's <c>filter.fir_filter_fff</c>).</summary>
public sealed class FirFilterFff : DecimatorBlock<float, float>
{
    private readonly FirFilter<float> _filter;
    private volatile float[]? _pendingTaps;

    public FirFilterFff(int decimation, ReadOnlySpan<float> taps) : base("fir_filter_fff", decimation)
    {
        _filter = new FirFilter<float>(taps);
    }

    public ReadOnlySpan<float> Taps => _filter.Taps;

    /// <summary>Replaces the taps; takes effect on the next work call (thread-safe).</summary>
    public void SetTaps(ReadOnlySpan<float> taps) => _pendingTaps = taps.ToArray();

    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        if (Interlocked.Exchange(ref _pendingTaps, null) is { } t) _filter.SetTaps(t);
        _filter.Filter(input, output, Decimation);
    }
}

/// <summary>Decimating FIR, complex samples, real taps (GNU Radio's <c>filter.fir_filter_ccf</c>).</summary>
public sealed class FirFilterCcf : DecimatorBlock<Complex32, Complex32>
{
    private readonly FirFilter<Complex32> _filter;
    private volatile float[]? _pendingTaps;

    public FirFilterCcf(int decimation, ReadOnlySpan<float> taps) : base("fir_filter_ccf", decimation)
    {
        _filter = new FirFilter<Complex32>(taps);
    }

    public ReadOnlySpan<float> Taps => _filter.Taps;

    public void SetTaps(ReadOnlySpan<float> taps) => _pendingTaps = taps.ToArray();

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (Interlocked.Exchange(ref _pendingTaps, null) is { } t) _filter.SetTaps(t);
        _filter.Filter(input, output, Decimation);
    }
}

/// <summary>Decimating FIR, complex samples, complex taps (GNU Radio's <c>filter.fir_filter_ccc</c>).</summary>
public sealed class FirFilterCcc : DecimatorBlock<Complex32, Complex32>
{
    private readonly ComplexFirFilter _filter;
    private volatile Complex32[]? _pendingTaps;

    public FirFilterCcc(int decimation, ReadOnlySpan<Complex32> taps) : base("fir_filter_ccc", decimation)
    {
        _filter = new ComplexFirFilter(taps);
    }

    public void SetTaps(ReadOnlySpan<Complex32> taps) => _pendingTaps = taps.ToArray();

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (Interlocked.Exchange(ref _pendingTaps, null) is { } t) _filter.SetTaps(t);
        _filter.Filter(input, output, Decimation);
    }
}

/// <summary>
/// Convenience low-pass FIR designed from cutoff and transition width (GRC's "Low Pass Filter" block).
/// </summary>
public static class LowPassFilter
{
    public static FirFilterCcf Complex(int decimation, double sampleRate, double cutoff, double transitionWidth,
        double gain = 1, WindowType window = WindowType.Hamming, double beta = 6.76) =>
        new(decimation, Firdes.LowPass(gain, sampleRate, cutoff, transitionWidth, window, beta));

    public static FirFilterFff Real(int decimation, double sampleRate, double cutoff, double transitionWidth,
        double gain = 1, WindowType window = WindowType.Hamming, double beta = 6.76) =>
        new(decimation, Firdes.LowPass(gain, sampleRate, cutoff, transitionWidth, window, beta));
}

/// <summary>
/// Frequency-translating decimating FIR (GNU Radio's <c>filter.freq_xlating_fir_filter_ccf</c>): shifts
/// <see cref="CenterFrequency"/> to 0 Hz, low-pass filters, and decimates in one step. The classic first block
/// of a receiver that picks one channel out of a wide capture. The centre frequency can be retuned live.
/// </summary>
public sealed class FreqXlatingFirFilter : DecimatorBlock<Complex32, Complex32>
{
    private readonly float[] _prototype;
    private readonly ComplexFirFilter _filter;
    private Complex32 _phase = Complex32.One;
    private Complex32 _increment;
    private volatile bool _retune;
    private double _center;

    public FreqXlatingFirFilter(int decimation, ReadOnlySpan<float> taps, double centerFrequency, double sampleRate)
        : base("freq_xlating_fir_filter_ccf", decimation)
    {
        _prototype = taps.ToArray();
        SampleRate = sampleRate;
        _center = centerFrequency;
        _filter = new ComplexFirFilter(BuildTaps());
        _increment = Complex32.FromPolar(1f, (float)(-2 * Math.PI * _center / SampleRate * Decimation));
        RegisterMessageInput("freq", m => CenterFrequency = m.ToDouble());
    }

    public double SampleRate { get; }

    public double CenterFrequency
    {
        get => Volatile.Read(ref _center);
        set
        {
            Volatile.Write(ref _center, value);
            _retune = true;
        }
    }

    private Complex32[] BuildTaps()
    {
        var taps = new Complex32[_prototype.Length];
        var w = 2 * Math.PI * _center / SampleRate;
        for (var i = 0; i < taps.Length; i++)
            taps[i] = Complex32.FromPolar(_prototype[i], (float)(w * i));
        return taps;
    }

    protected override void Work(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        if (_retune)
        {
            _retune = false;
            _filter.SetTaps(BuildTaps());
            _increment = Complex32.FromPolar(1f, (float)(-2 * Math.PI * _center / SampleRate * Decimation));
        }
        _filter.Filter(input, output, Decimation);
        Volk.Rotate(output, _increment, ref _phase, output);
    }
}

/// <summary>Interpolating FIR, polyphase (GNU Radio's <c>filter.interp_fir_filter_xxf</c>).</summary>
public sealed class InterpFirFilter<T> : InterpolatorBlock<T, T> where T : unmanaged
{
    private readonly PolyphaseResampler<T> _resampler;

    public InterpFirFilter(int interpolation, ReadOnlySpan<float> taps) : base("interp_fir_filter", interpolation)
    {
        _resampler = new PolyphaseResampler<T>(interpolation, 1, taps);
    }

    protected override void Work(ReadOnlySpan<T> input, Span<T> output) => _resampler.Process(input, output);
}

/// <summary>
/// Rational resampler L/M (GNU Radio's <c>filter.rational_resampler_xxx</c>). Taps default to GNU Radio's
/// Kaiser design with 40 % fractional bandwidth.
/// </summary>
public sealed class RationalResampler<T> : Block where T : unmanaged
{
    private readonly PolyphaseResampler<T> _resampler;

    public RationalResampler(int interpolation, int decimation, float[]? taps = null, double fractionalBandwidth = 0.4)
        : base("rational_resampler")
    {
        var g = Gcd(interpolation, decimation);
        interpolation /= g;
        decimation /= g;
        Interpolation = interpolation;
        Decimation = decimation;
        RelativeRate = (double)interpolation / decimation;
        _resampler = new PolyphaseResampler<T>(interpolation, decimation,
            taps ?? PolyphaseResampler<T>.DesignTaps(interpolation, decimation, fractionalBandwidth));
        AddInput<T>();
        AddOutput<T>();
    }

    public int Interpolation { get; }
    public int Decimation { get; }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var (consumed, produced) = _resampler.Process(context.Input<T>(0), context.Output<T>(0));
        context.Consume(0, consumed);
        context.Produce(0, produced);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Arbitrary-ratio resampler using a 32-branch polyphase filter bank with linear interpolation between
/// branches (GNU Radio's <c>filter.pfb_arb_resampler_xxf</c>). <see cref="Rate"/> is output/input and may be irrational.
/// </summary>
public sealed class ArbitraryResampler<T> : Block where T : unmanaged
{
    private const int Branches = 32;
    private readonly RealTaps[] _bank;
    private readonly int _branchLength;
    private T[] _work;
    private double _accumulator;
    private int _pendingSkip;

    public ArbitraryResampler(double rate, float[]? taps = null) : base("pfb_arb_resampler")
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("ArbitraryResampler supports float and Complex32.");
        Rate = rate;
        RelativeRate = rate;
        taps ??= DesignTaps(rate);
        _branchLength = (taps.Length + Branches - 1) / Branches;
        _bank = new RealTaps[Branches];
        var branch = new float[_branchLength];
        for (var p = 0; p < Branches; p++)
        {
            Array.Clear(branch);
            for (var k = 0; k < _branchLength; k++)
            {
                var idx = k * Branches + p;
                if (idx < taps.Length) branch[k] = taps[idx];
            }
            _bank[p] = new RealTaps(branch);
        }
        _work = new T[_branchLength];
        AddInput<T>();
        AddOutput<T>();
    }

    public double Rate { get; }

    public static float[] DesignTaps(double rate)
    {
        const double percent = 0.80;
        var halfband = rate < 1 ? 0.5 * rate : 0.5;
        var bw = percent * halfband;
        var tb = percent / 2.0 * halfband;
        return Firdes.LowPass(Branches, Branches, bw, tb, WindowType.BlackmanHarris);
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var input = context.Input<T>(0);
        var output = context.Output<T>(0);
        var h = _branchLength - 1;
        var need = h + input.Length;
        if (_work.Length < need)
        {
            var w = new T[Math.Max(need, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input.CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, need);
        var step = Branches / Rate;
        var acc = _accumulator;
        var inPos = _pendingSkip;
        var produced = 0;
        var isComplex = typeof(T) == typeof(Complex32);
        while (produced < output.Length && inPos + 1 < input.Length)
        {
            var j = (int)acc;
            var frac = (float)(acc - j);
            var a = _bank[j].Dot<T>(work.Slice(inPos, _branchLength));
            var b = j + 1 < Branches
                ? _bank[j + 1].Dot<T>(work.Slice(inPos, _branchLength))
                : _bank[0].Dot<T>(work.Slice(inPos + 1, _branchLength));
            if (isComplex)
            {
                var ca = MemoryMarshal.Cast<T, Complex32>(MemoryMarshal.CreateSpan(ref a, 1))[0];
                var cb = MemoryMarshal.Cast<T, Complex32>(MemoryMarshal.CreateSpan(ref b, 1))[0];
                MemoryMarshal.Cast<T, Complex32>(output)[produced] = ca * (1 - frac) + cb * frac;
            }
            else
            {
                var fa = MemoryMarshal.Cast<T, float>(MemoryMarshal.CreateSpan(ref a, 1))[0];
                var fb = MemoryMarshal.Cast<T, float>(MemoryMarshal.CreateSpan(ref b, 1))[0];
                MemoryMarshal.Cast<T, float>(output)[produced] = fa * (1 - frac) + fb * frac;
            }
            produced++;
            acc += step;
            while (acc >= Branches)
            {
                acc -= Branches;
                inPos++;
            }
        }
        var consumed = Math.Min(inPos, input.Length);
        _pendingSkip = inPos - consumed;
        _accumulator = acc;
        work.Slice(consumed, h).CopyTo(_work);
        context.Consume(0, consumed);
        context.Produce(0, produced);
        return WorkResult.Ok;
    }
}

/// <summary>Real signal → analytic complex signal via a Hilbert FIR (GNU Radio's <c>filter.hilbert_fc</c>).</summary>
public sealed class HilbertFilter : SyncBlock<float, Complex32>
{
    private readonly FirFilter<float> _hilbert;
    private readonly float[] _delay;
    private int _delayPos;
    private float[] _scratch = [];

    public HilbertFilter(int ntaps = 65, WindowType window = WindowType.Hamming) : base("hilbert_fc")
    {
        _hilbert = new FirFilter<float>(Firdes.Hilbert(ntaps, window));
        // The real branch is delayed by the filter's group delay so I and Q line up.
        _delay = new float[(ntaps - 1) / 2];
    }

    protected override void Work(ReadOnlySpan<float> input, Span<Complex32> output)
    {
        if (_scratch.Length < input.Length) _scratch = new float[input.Length];
        var q = _scratch.AsSpan(0, input.Length);
        _hilbert.Filter(input, q);
        for (var i = 0; i < input.Length; i++)
        {
            float re;
            if (_delay.Length == 0)
            {
                re = input[i];
            }
            else
            {
                re = _delay[_delayPos];
                _delay[_delayPos] = input[i];
                _delayPos = (_delayPos + 1) % _delay.Length;
            }
            output[i] = new Complex32(re, q[i]);
        }
    }
}
