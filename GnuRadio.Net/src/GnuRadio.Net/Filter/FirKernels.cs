using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GnuRadio.Kernels;

namespace GnuRadio.Filter;

/// <summary>Real taps prepared for the SIMD dot products: reversed, plus an interleaved copy for complex samples.</summary>
internal sealed class RealTaps
{
    public RealTaps(ReadOnlySpan<float> taps)
    {
        Original = taps.ToArray();
        Reversed = taps.ToArray();
        Array.Reverse(Reversed);
        Interleaved = Volk.Interleave(Reversed);
    }

    public float[] Original { get; }
    public float[] Reversed { get; }
    public float[] Interleaved { get; }
    public int Length => Reversed.Length;

    /// <summary>Σ window[j]·reversed[j]; window holds exactly <see cref="Length"/> samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Dot<T>(ReadOnlySpan<T> window) where T : unmanaged
    {
        if (typeof(T) == typeof(float))
        {
            var r = Volk.Dot(MemoryMarshal.Cast<T, float>(window), Reversed);
            return Unsafe.As<float, T>(ref r);
        }
        var c = Volk.DotComplexReal(MemoryMarshal.Cast<T, Complex32>(window), Interleaved);
        return Unsafe.As<Complex32, T>(ref c);
    }
}

/// <summary>
/// Stateful FIR filter with real taps over float or complex samples (GNU Radio's <c>fir_filter_fff/ccf</c> kernel).
/// Keeps <c>ntaps − 1</c> samples of history between calls so streams can be filtered in arbitrary chunks.
/// </summary>
public sealed class FirFilter<T> where T : unmanaged
{
    private RealTaps _taps;
    private T[] _work = [];
    private int _history;

    public FirFilter(ReadOnlySpan<float> taps)
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(Complex32))
            throw new NotSupportedException("FirFilter supports float and Complex32 samples.");
        if (taps.IsEmpty) throw new ArgumentException("At least one tap is required.", nameof(taps));
        _taps = new RealTaps(taps);
        _history = _taps.Length - 1;
        _work = new T[_history];
    }

    public ReadOnlySpan<float> Taps => _taps.Original;

    /// <summary>Replaces the taps; history is kept (resized if the length changed).</summary>
    public void SetTaps(ReadOnlySpan<float> taps)
    {
        var old = _history;
        _taps = new RealTaps(taps);
        _history = _taps.Length - 1;
        if (_history != old)
        {
            var w = new T[_history];
            var keep = Math.Min(old, _history);
            _work.AsSpan(old - keep, keep).CopyTo(w.AsSpan(_history - keep));
            _work = w;
        }
    }

    /// <summary>Filters <paramref name="input"/>, writing <c>input.Length / decimation</c> outputs.</summary>
    public void Filter(ReadOnlySpan<T> input, Span<T> output, int decimation = 1)
    {
        var outputs = input.Length / decimation;
        var used = outputs * decimation;
        var h = _history;
        var need = h + used;
        if (_work.Length < need)
        {
            var w = new T[Math.Max(need, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input[..used].CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, need);
        var taps = _taps;
        var n = taps.Length;

        if (Volk.UseNative && NativeKernels.IsAvailable && outputs >= 64)
        {
            if (typeof(T) == typeof(float))
                NativeKernels.FirFloat(MemoryMarshal.Cast<T, float>(work), taps.Reversed, decimation, MemoryMarshal.Cast<T, float>(output[..outputs]));
            else
                NativeKernels.FirComplexReal(MemoryMarshal.Cast<T, Complex32>(work), taps.Interleaved, decimation, MemoryMarshal.Cast<T, Complex32>(output[..outputs]));
        }
        else
        {
            for (var k = 0; k < outputs; k++)
                output[k] = taps.Dot<T>(work.Slice(k * decimation, n));
        }
        work.Slice(used, h).CopyTo(_work);
    }

    /// <summary>Filters one sample and returns one output (no decimation).</summary>
    public T FilterOne(T sample)
    {
        Span<T> output = stackalloc T[1];
        Filter([sample], output);
        return output[0];
    }

    public void Reset() => Array.Clear(_work);
}

/// <summary>FIR filter with complex taps over complex samples (GNU Radio's <c>fir_filter_ccc</c> kernel).</summary>
public sealed class ComplexFirFilter
{
    private Complex32[] _reversed;
    private Complex32[] _work;
    private int _history;

    public ComplexFirFilter(ReadOnlySpan<Complex32> taps)
    {
        if (taps.IsEmpty) throw new ArgumentException("At least one tap is required.", nameof(taps));
        _reversed = Reverse(taps);
        _history = taps.Length - 1;
        _work = new Complex32[_history];
    }

    public int TapCount => _reversed.Length;

    public void SetTaps(ReadOnlySpan<Complex32> taps)
    {
        var old = _history;
        _reversed = Reverse(taps);
        _history = taps.Length - 1;
        if (_history != old)
        {
            var w = new Complex32[_history];
            var keep = Math.Min(old, _history);
            _work.AsSpan(old - keep, keep).CopyTo(w.AsSpan(_history - keep));
            _work = w;
        }
    }

    private static Complex32[] Reverse(ReadOnlySpan<Complex32> taps)
    {
        var r = taps.ToArray();
        Array.Reverse(r);
        return r;
    }

    public void Filter(ReadOnlySpan<Complex32> input, Span<Complex32> output, int decimation = 1)
    {
        var outputs = input.Length / decimation;
        var used = outputs * decimation;
        var h = _history;
        var need = h + used;
        if (_work.Length < need)
        {
            var w = new Complex32[Math.Max(need, _work.Length * 2)];
            _work.AsSpan(0, h).CopyTo(w);
            _work = w;
        }
        input[..used].CopyTo(_work.AsSpan(h));
        var work = _work.AsSpan(0, need);
        var taps = _reversed;
        for (var k = 0; k < outputs; k++)
            output[k] = Volk.Dot(work.Slice(k * decimation, taps.Length), taps);
        work.Slice(used, h).CopyTo(_work);
    }

    public void Reset() => Array.Clear(_work);
}

/// <summary>
/// Polyphase rational resampler kernel: interpolate by L, filter, decimate by M, computing only the
/// outputs that survive decimation (GNU Radio's <c>rational_resampler</c> kernel).
/// </summary>
public sealed class PolyphaseResampler<T> where T : unmanaged
{
    private readonly RealTaps[] _branches;
    private readonly int _branchLength;
    private T[] _work;
    private int _phase;
    private int _pendingSkip;

    public PolyphaseResampler(int interpolation, int decimation, ReadOnlySpan<float> taps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interpolation, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(decimation, 1);
        Interpolation = interpolation;
        Decimation = decimation;
        _branchLength = (taps.Length + interpolation - 1) / interpolation;
        _branches = new RealTaps[interpolation];
        var branch = new float[_branchLength];
        for (var p = 0; p < interpolation; p++)
        {
            Array.Clear(branch);
            for (var k = 0; k < _branchLength; k++)
            {
                var idx = k * interpolation + p;
                if (idx < taps.Length) branch[k] = taps[idx];
            }
            _branches[p] = new RealTaps(branch);
        }
        _work = new T[_branchLength - 1];
    }

    public int Interpolation { get; }
    public int Decimation { get; }

    /// <summary>
    /// Resamples as much of <paramref name="input"/> as fits in <paramref name="output"/>.
    /// Returns (consumed inputs, produced outputs).
    /// </summary>
    public (int Consumed, int Produced) Process(ReadOnlySpan<T> input, Span<T> output)
    {
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

        // Samples the previous call stepped past (decimation can jump beyond the end of a chunk).
        var inPos = _pendingSkip;
        var produced = 0;
        var phase = _phase;
        while (produced < output.Length && inPos < input.Length)
        {
            output[produced++] = _branches[phase].Dot<T>(work.Slice(inPos, _branchLength));
            phase += Decimation;
            while (phase >= Interpolation)
            {
                phase -= Interpolation;
                inPos++;
            }
        }
        var consumed = Math.Min(inPos, input.Length);
        _pendingSkip = inPos - consumed;
        _phase = phase;
        work.Slice(consumed, h).CopyTo(_work);
        return (consumed, produced);
    }

    /// <summary>
    /// GNU Radio's default resampler prototype: Kaiser low-pass with β = 7 and 40 % fractional bandwidth.
    /// </summary>
    public static float[] DesignTaps(int interpolation, int decimation, double fractionalBandwidth = 0.4)
    {
        const double beta = 7.0;
        const double halfband = 0.5;
        var rate = (double)interpolation / decimation;
        double transition, mid;
        if (rate >= 1.0)
        {
            transition = halfband - fractionalBandwidth;
            mid = halfband - transition / 2.0;
        }
        else
        {
            transition = rate * (halfband - fractionalBandwidth);
            mid = rate * halfband - transition / 2.0;
        }
        return Firdes.LowPass(interpolation, interpolation, mid, transition, WindowType.Kaiser, beta);
    }
}
