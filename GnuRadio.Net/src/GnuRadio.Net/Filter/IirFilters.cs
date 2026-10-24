using System.Runtime.InteropServices;

namespace GnuRadio.Filter;

/// <summary>Second-order IIR section in transposed direct form II, double-precision state.</summary>
public sealed class Biquad
{
    private double _z1, _z2;

    public Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        B0 = b0;
        B1 = b1;
        B2 = b2;
        A1 = a1;
        A2 = a2;
    }

    public double B0 { get; }
    public double B1 { get; }
    public double B2 { get; }
    public double A1 { get; }
    public double A2 { get; }

    public double Process(double x)
    {
        var y = B0 * x + _z1;
        _z1 = B1 * x - A1 * y + _z2;
        _z2 = B2 * x - A2 * y;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0;

    /// <summary>Copy with the same coefficients and cleared state, for filtering a second channel.</summary>
    public Biquad Clone() => new(B0, B1, B2, A1, A2);

    /// <summary>|H(e^jω)| at normalised frequency <paramref name="f"/> (cycles/sample).</summary>
    public double Magnitude(double f)
    {
        var w = 2 * Math.PI * f;
        var (s1, c1) = Math.SinCos(-w);
        var (s2, c2) = Math.SinCos(-2 * w);
        var nr = B0 + B1 * c1 + B2 * c2;
        var ni = B1 * s1 + B2 * s2;
        var dr = 1 + A1 * c1 + A2 * c2;
        var di = A1 * s1 + A2 * s2;
        return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
    }
}

/// <summary>IIR filter design (GNU Radio's <c>filter.iir_design</c> / <c>optfir</c> counterparts).</summary>
public static class IirDesign
{
    /// <summary>Butterworth low-pass of the given order as cascaded biquads (bilinear transform, pre-warped).</summary>
    public static Biquad[] ButterworthLowPass(int order, double cutoff, double sampleRate) =>
        Butterworth(order, cutoff, sampleRate, highPass: false);

    /// <summary>Butterworth high-pass of the given order as cascaded biquads.</summary>
    public static Biquad[] ButterworthHighPass(int order, double cutoff, double sampleRate) =>
        Butterworth(order, cutoff, sampleRate, highPass: true);

    private static Biquad[] Butterworth(int order, double cutoff, double sampleRate, bool highPass)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, 1);
        if (cutoff <= 0 || cutoff >= sampleRate / 2) throw new ArgumentOutOfRangeException(nameof(cutoff));
        var w0 = 2 * Math.PI * cutoff / sampleRate;
        var cos = Math.Cos(w0);
        var sections = new List<Biquad>();
        for (var k = 1; k <= order / 2; k++)
        {
            var q = 1.0 / (2 * Math.Sin((2 * k - 1) * Math.PI / (2 * order)));
            var alpha = Math.Sin(w0) / (2 * q);
            var a0 = 1 + alpha;
            sections.Add(highPass
                ? new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0)
                : new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0));
        }
        if ((order & 1) == 1)
        {
            var kk = Math.Tan(w0 / 2);
            var a1 = (kk - 1) / (kk + 1);
            sections.Add(highPass
                ? new Biquad(1 / (1 + kk), -1 / (1 + kk), 0, a1, 0)
                : new Biquad(kk / (1 + kk), kk / (1 + kk), 0, a1, 0));
        }
        return [.. sections];
    }

    /// <summary>Peaking/notch section: removes a narrow band around <paramref name="frequency"/> (RBJ notch).</summary>
    public static Biquad Notch(double frequency, double sampleRate, double q = 30)
    {
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var alpha = Math.Sin(w0) / (2 * q);
        var cos = Math.Cos(w0);
        var a0 = 1 + alpha;
        return new Biquad(1 / a0, -2 * cos / a0, 1 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }
}

/// <summary>
/// General IIR, y = Σ b·x − Σ a·y with a[0] = 1 (MATLAB/SciPy convention), float samples,
/// double-precision state (GNU Radio's <c>filter.iir_filter_ffd</c> with <c>oldstyle=False</c>).
/// </summary>
public sealed class IirFilter : SyncBlock<float, float>
{
    private readonly double[] _b;
    private readonly double[] _a;
    private readonly double[] _state;

    public IirFilter(ReadOnlySpan<double> feedforward, ReadOnlySpan<double> feedback) : base("iir_filter_ffd")
    {
        if (feedback.IsEmpty || feedback[0] == 0) throw new ArgumentException("feedback[0] must be non-zero.", nameof(feedback));
        var n = Math.Max(feedforward.Length, feedback.Length);
        _b = new double[n];
        _a = new double[n];
        for (var i = 0; i < feedforward.Length; i++) _b[i] = feedforward[i] / feedback[0];
        for (var i = 0; i < feedback.Length; i++) _a[i] = feedback[i] / feedback[0];
        _state = new double[n];
    }

    public double Process(double x)
    {
        var n = _b.Length;
        var y = _b[0] * x + _state[0];
        for (var i = 1; i < n; i++)
            _state[i - 1] = _b[i] * x - _a[i] * y + (i < n - 1 ? _state[i] : 0);
        return y;
    }

    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = (float)Process(input[i]);
    }
}

/// <summary>Cascade of biquads over float samples, for Butterworth and notch designs from <see cref="IirDesign"/>.</summary>
public sealed class BiquadFilter(params Biquad[] sections) : SyncBlock<float, float>("biquad_filter_ff")
{
    protected override void Work(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            double y = input[i];
            foreach (var s in sections) y = s.Process(y);
            output[i] = (float)y;
        }
    }
}

/// <summary>y[n] = α·x[n] + (1−α)·y[n−1] (GNU Radio's <c>filter.single_pole_iir_filter_ff/cc</c>).</summary>
public sealed class SinglePoleIir<T> : SyncBlock<T, T> where T : unmanaged
{
    private float _re, _im;

    public SinglePoleIir(float alpha, int vectorLength = 1) : base("single_pole_iir_filter", vectorLength, vectorLength)
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("SinglePoleIir supports float and Complex32.");
        if (vectorLength != 1) _vector = new Complex32[vectorLength];
        Alpha = alpha;
    }

    private readonly Complex32[]? _vector;

    public float Alpha { get; set; }

    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        var a = Alpha;
        var b = 1 - a;
        if (_vector is { } v)
        {
            // Vector mode: each element of the vector is smoothed independently (spectrum averaging).
            var len = v.Length;
            if (typeof(T) == typeof(float))
            {
                var fi = MemoryMarshal.Cast<T, float>(input);
                var fo = MemoryMarshal.Cast<T, float>(output);
                for (var i = 0; i < fi.Length; i++)
                {
                    var k = i % len;
                    var y = a * fi[i] + b * v[k].Real;
                    v[k] = new Complex32(y, 0);
                    fo[i] = y;
                }
            }
            else
            {
                var ci = MemoryMarshal.Cast<T, Complex32>(input);
                var co = MemoryMarshal.Cast<T, Complex32>(output);
                for (var i = 0; i < ci.Length; i++)
                {
                    var k = i % len;
                    v[k] = ci[i] * a + v[k] * b;
                    co[i] = v[k];
                }
            }
            return;
        }
        if (typeof(T) == typeof(float))
        {
            var fi = MemoryMarshal.Cast<T, float>(input);
            var fo = MemoryMarshal.Cast<T, float>(output);
            var y = _re;
            for (var i = 0; i < fi.Length; i++) fo[i] = y = a * fi[i] + b * y;
            _re = y;
        }
        else
        {
            var ci = MemoryMarshal.Cast<T, Complex32>(input);
            var co = MemoryMarshal.Cast<T, Complex32>(output);
            float yr = _re, yi = _im;
            for (var i = 0; i < ci.Length; i++)
            {
                yr = a * ci[i].Real + b * yr;
                yi = a * ci[i].Imaginary + b * yi;
                co[i] = new Complex32(yr, yi);
            }
            _re = yr;
            _im = yi;
        }
    }
}

/// <summary>
/// Moving sum over <see cref="Length"/> samples times <see cref="Scale"/> (GNU Radio's <c>blocks.moving_average_ff/cc</c>).
/// Uses a running sum, recomputed periodically so float rounding cannot accumulate.
/// </summary>
public sealed class MovingAverage<T> : SyncBlock<T, T> where T : unmanaged
{
    private readonly Complex32[] _ring;
    private int _pos;
    private double _sumRe, _sumIm;
    private long _count;

    public MovingAverage(int length, float scale) : base("moving_average")
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("MovingAverage supports float and Complex32.");
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        Length = length;
        Scale = scale;
        _ring = new Complex32[length];
    }

    public int Length { get; }
    public float Scale { get; set; }

    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        var isComplex = typeof(T) == typeof(Complex32);
        var ci = isComplex ? MemoryMarshal.Cast<T, Complex32>(input) : default;
        var fi = isComplex ? default : MemoryMarshal.Cast<T, float>(input);
        var co = isComplex ? MemoryMarshal.Cast<T, Complex32>(output) : default;
        var fo = isComplex ? default : MemoryMarshal.Cast<T, float>(output);
        var scale = Scale;
        for (var i = 0; i < input.Length; i++)
        {
            var x = isComplex ? ci[i] : new Complex32(fi[i], 0);
            var old = _ring[_pos];
            _ring[_pos] = x;
            _pos = _pos + 1 == Length ? 0 : _pos + 1;
            _sumRe += x.Real - old.Real;
            _sumIm += x.Imaginary - old.Imaginary;
            if (++_count % (64L * Length) == 0)
            {
                _sumRe = _sumIm = 0;
                foreach (var r in _ring)
                {
                    _sumRe += r.Real;
                    _sumIm += r.Imaginary;
                }
            }
            if (isComplex) co[i] = new Complex32((float)(_sumRe * scale), (float)(_sumIm * scale));
            else fo[i] = (float)(_sumRe * scale);
        }
    }
}

/// <summary>
/// Removes the DC component with a one-pole high-pass y[n] = x[n] − x[n−1] + R·y[n−1], R = 1 − 1/<c>length</c>
/// (same purpose as GNU Radio's <c>filter.dc_blocker_ff/cc</c>).
/// </summary>
public sealed class DcBlocker<T> : SyncBlock<T, T> where T : unmanaged
{
    private readonly float _r;
    private Complex32 _x1, _y1;

    public DcBlocker(int length = 32) : base("dc_blocker")
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("DcBlocker supports float and Complex32.");
        _r = 1f - 1f / length;
    }

    protected override void Work(ReadOnlySpan<T> input, Span<T> output)
    {
        if (typeof(T) == typeof(float))
        {
            var fi = MemoryMarshal.Cast<T, float>(input);
            var fo = MemoryMarshal.Cast<T, float>(output);
            float x1 = _x1.Real, y1 = _y1.Real;
            for (var i = 0; i < fi.Length; i++)
            {
                var y = fi[i] - x1 + _r * y1;
                x1 = fi[i];
                fo[i] = y1 = y;
            }
            _x1 = new Complex32(x1, 0);
            _y1 = new Complex32(y1, 0);
        }
        else
        {
            var ci = MemoryMarshal.Cast<T, Complex32>(input);
            var co = MemoryMarshal.Cast<T, Complex32>(output);
            for (var i = 0; i < ci.Length; i++)
            {
                var y = ci[i] - _x1 + _y1 * _r;
                _x1 = ci[i];
                co[i] = _y1 = y;
            }
        }
    }
}
