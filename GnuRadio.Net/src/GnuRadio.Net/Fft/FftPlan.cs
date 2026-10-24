using System.Collections.Concurrent;
using System.Numerics;
using GnuRadio.Kernels;

namespace GnuRadio.Fft;

/// <summary>
/// Precomputed complex FFT of a fixed size (GNU Radio's <c>fft::fft_complex</c>). Powers of two use an
/// iterative radix-2 transform with cached twiddles and bit-reversal tables; other sizes use Bluestein's
/// algorithm on top of a power-of-two plan. Transforms are unnormalised in both directions, like FFTW.
/// A plan holds scratch memory, so use one plan per thread (<see cref="Get"/> caches per thread).
/// </summary>
public sealed class FftPlan
{
    [ThreadStatic] private static Dictionary<(int, bool), FftPlan>? t_cache;

    private readonly Complex32[] _twiddles = [];
    private readonly int[] _bitReverse = [];
    private readonly int _log2;

    // Bluestein state for non-power-of-two sizes.
    private readonly FftPlan? _inner;
    private readonly Complex32[] _chirp = [];
    private readonly Complex32[] _chirpSpectrum = [];
    private readonly Complex32[] _scratch = [];

    public FftPlan(int size, bool inverse = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        Size = size;
        Inverse = inverse;
        if (BitOperations.IsPow2(size))
        {
            _log2 = BitOperations.Log2((uint)size);
            _twiddles = new Complex32[size / 2];
            var sign = inverse ? 1.0 : -1.0;
            for (var k = 0; k < size / 2; k++)
            {
                var a = sign * 2 * Math.PI * k / size;
                _twiddles[k] = new Complex32((float)Math.Cos(a), (float)Math.Sin(a));
            }
            _bitReverse = new int[size];
            for (var i = 0; i < size; i++)
                _bitReverse[i] = (int)(ReverseBits((uint)i) >> (32 - _log2));
            if (size == 1) _bitReverse[0] = 0;
        }
        else
        {
            var m = (int)BitOperations.RoundUpToPowerOf2((uint)(2 * size - 1));
            _inner = new FftPlan(m, false);
            var innerInverse = new FftPlan(m, true);
            _chirp = new Complex32[size];
            var sign = inverse ? 1.0 : -1.0;
            for (var n = 0; n < size; n++)
            {
                // Reduce n² mod 2N in integers to keep the phase accurate for large n.
                var nn = (long)n * n % (2L * size);
                var a = sign * Math.PI * nn / size;
                _chirp[n] = new Complex32((float)Math.Cos(a), (float)Math.Sin(a));
            }
            _chirpSpectrum = new Complex32[m];
            _chirpSpectrum[0] = _chirp[0].Conjugate();
            for (var n = 1; n < size; n++)
                _chirpSpectrum[n] = _chirpSpectrum[m - n] = _chirp[n].Conjugate();
            _inner.Execute(_chirpSpectrum);
            _scratch = new Complex32[m];
            _innerInverse = innerInverse;
        }
    }

    private readonly FftPlan? _innerInverse;

    public int Size { get; }
    public bool Inverse { get; }

    /// <summary>A cached plan for the current thread.</summary>
    public static FftPlan Get(int size, bool inverse = false)
    {
        t_cache ??= [];
        if (!t_cache.TryGetValue((size, inverse), out var plan))
            t_cache[(size, inverse)] = plan = new FftPlan(size, inverse);
        return plan;
    }

    /// <summary>In-place transform of exactly <see cref="Size"/> samples.</summary>
    public void Execute(Span<Complex32> data)
    {
        if (data.Length != Size) throw new ArgumentException($"Expected {Size} samples, got {data.Length}.", nameof(data));
        if (_inner is not null)
        {
            Bluestein(data);
            return;
        }
        if (Size == 1) return;
        if (Volk.UseNative && NativeKernels.IsAvailable && Size >= 64)
        {
            NativeKernels.Fft(data, Inverse);
            return;
        }
        Radix2(data);
    }

    /// <summary>Out-of-place transform; <paramref name="input"/> is not modified.</summary>
    public void Execute(ReadOnlySpan<Complex32> input, Span<Complex32> output)
    {
        input.CopyTo(output);
        Execute(output[..Size]);
    }

    private void Radix2(Span<Complex32> data)
    {
        var n = Size;
        for (var i = 0; i < n; i++)
        {
            var j = _bitReverse[i];
            if (j > i) (data[i], data[j]) = (data[j], data[i]);
        }

        // First two stages unrolled: twiddles are ±1 and ±j.
        for (var i = 0; i + 1 < n; i += 2)
        {
            var a = data[i];
            var b = data[i + 1];
            data[i] = a + b;
            data[i + 1] = a - b;
        }
        var jSign = Inverse ? 1f : -1f;
        if (n >= 4)
        {
            for (var i = 0; i < n; i += 4)
            {
                var a0 = data[i];
                var a1 = data[i + 1];
                var a2 = data[i + 2];
                var a3 = data[i + 3];
                // a3 · (∓j)
                var t3 = new Complex32(-jSign * a3.Imaginary, jSign * a3.Real);
                data[i] = a0 + a2;
                data[i + 2] = a0 - a2;
                data[i + 1] = a1 + t3;
                data[i + 3] = a1 - t3;
            }
        }

        for (var len = 8; len <= n; len <<= 1)
        {
            var half = len >> 1;
            var step = n / len;
            for (var start = 0; start < n; start += len)
            {
                for (var k = 0; k < half; k++)
                {
                    var w = _twiddles[k * step];
                    var u = data[start + k];
                    var v = data[start + k + half] * w;
                    data[start + k] = u + v;
                    data[start + k + half] = u - v;
                }
            }
        }
    }

    private void Bluestein(Span<Complex32> data)
    {
        var m = _scratch.Length;
        var a = _scratch.AsSpan();
        a.Clear();
        for (var n = 0; n < Size; n++) a[n] = data[n] * _chirp[n];
        _inner!.Execute(a);
        Volk.Multiply(a, _chirpSpectrum, a);
        _innerInverse!.Execute(a);
        var scale = 1f / m;
        for (var k = 0; k < Size; k++) data[k] = a[k] * _chirp[k] * scale;
    }

    private static uint ReverseBits(uint v)
    {
        v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
        v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
        v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
        v = ((v >> 8) & 0x00FF00FF) | ((v & 0x00FF00FF) << 8);
        return (v >> 16) | (v << 16);
    }
}

/// <summary>One-call FFT helpers.</summary>
public static class FftMath
{
    /// <summary>Forward FFT of <paramref name="input"/> into a new array.</summary>
    public static Complex32[] Forward(ReadOnlySpan<Complex32> input)
    {
        var result = input.ToArray();
        FftPlan.Get(result.Length).Execute(result);
        return result;
    }

    /// <summary>Inverse FFT scaled by 1/N so <c>Inverse(Forward(x)) == x</c>.</summary>
    public static Complex32[] Inverse(ReadOnlySpan<Complex32> input)
    {
        var result = input.ToArray();
        FftPlan.Get(result.Length, inverse: true).Execute(result);
        Volk.Scale(result, 1f / result.Length, result);
        return result;
    }

    /// <summary>Swaps the halves so 0 Hz is in the middle (numpy's <c>fftshift</c>).</summary>
    public static void Shift<T>(Span<T> data)
    {
        var half = data.Length / 2;
        if (data.Length % 2 == 0)
        {
            for (var i = 0; i < half; i++) (data[i], data[i + half]) = (data[i + half], data[i]);
        }
        else
        {
            var tmp = data.ToArray();
            for (var i = 0; i < data.Length; i++) data[i] = tmp[(i + half + 1) % data.Length];
        }
    }

    /// <summary>Frequency in Hz of each bin after <see cref="Shift{T}"/>, from −fs/2 upward.</summary>
    public static double[] ShiftedFrequencies(int size, double sampleRate, double centerFrequency = 0)
    {
        var f = new double[size];
        for (var i = 0; i < size; i++) f[i] = centerFrequency + (i - size / 2) * sampleRate / size;
        return f;
    }

    internal static readonly ConcurrentDictionary<(WindowTypeKey, int), float[]> WindowCache = new();

    internal readonly record struct WindowTypeKey(Filter.WindowType Type, double Beta);

    internal static float[] CachedWindow(Filter.WindowType type, int size, double beta = 6.76) =>
        WindowCache.GetOrAdd((new WindowTypeKey(type, beta), size), k => Filter.Window.Build(k.Item1.Type, k.Item2, k.Item1.Beta));
}
