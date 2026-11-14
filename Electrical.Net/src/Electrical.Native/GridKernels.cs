using System.Numerics;
using System.Runtime.InteropServices;

namespace Electrical.Native;

/// <summary>
/// High-performance kernels backed by the Rust library <c>electrical_native</c> when it is available for the current
/// runtime, with managed fallbacks that produce identical results. Set <see cref="PreferNative"/> to false to force managed code.
/// </summary>
public static class GridKernels
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try { return NativeMethods.Version() >= 10000; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>Use native code when available (default true).</summary>
    public static bool PreferNative { get; set; } = true;

    /// <summary>True when the Rust library was found and loaded.</summary>
    public static bool IsNativeAvailable => Available.Value;

    private static bool UseNative => PreferNative && Available.Value;

    /// <summary>"native (Rust)" or "managed".</summary>
    public static string Backend => UseNative ? "native (Rust)" : "managed";

    /// <summary>In-place complex FFT of power-of-two length (unscaled inverse when <paramref name="inverse"/>).</summary>
    public static unsafe void Fft(Span<Complex> data, bool inverse = false)
    {
        int n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.", nameof(data));
        if (UseNative)
        {
            var d = MemoryMarshal.Cast<Complex, double>(data);
            fixed (double* p = d)
                if (NativeMethods.Fft(p, (nuint)n, inverse ? 1 : 0) == 0) return;
        }
        ManagedFft(data, inverse);
    }

    internal static void ManagedFft(Span<Complex> a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j |= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        double sign = inverse ? 1 : -1;
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = sign * 2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int s = 0; s < n; s += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[s + k];
                    var t = a[s + k + len / 2] * w;
                    a[s + k] = u + t;
                    a[s + k + len / 2] = u - t;
                    w *= wl;
                }
            }
        }
    }

    /// <summary>Peak amplitudes of harmonics 1..maxHarmonic (Goertzel). Exact for coherent sampling.</summary>
    public static unsafe double[] Harmonics(ReadOnlySpan<double> samples, double sampleRateHz, double fundamentalHz, int maxHarmonic)
    {
        if (samples.IsEmpty || maxHarmonic <= 0 || sampleRateHz <= 0 || fundamentalHz <= 0) throw new ArgumentException("Invalid harmonic analysis arguments.");
        var result = new double[maxHarmonic];
        if (UseNative)
        {
            fixed (double* x = samples)
            fixed (double* o = result)
                if (NativeMethods.Harmonics(x, (nuint)samples.Length, sampleRateHz, fundamentalHz, o, (nuint)maxHarmonic) == 0) return result;
        }
        double n = samples.Length;
        for (int idx = 0; idx < maxHarmonic; idx++)
        {
            double w = 2 * Math.PI * (idx + 1) * fundamentalHz / sampleRateHz;
            double c = Math.Cos(w), s = Math.Sin(w), coeff = 2 * c, s1 = 0, s2 = 0;
            foreach (var v in samples)
            {
                double s0 = v + coeff * s1 - s2;
                s2 = s1; s1 = s0;
            }
            double re = s1 - s2 * c, im = s2 * s;
            result[idx] = 2 * Math.Sqrt(re * re + im * im) / n;
        }
        return result;
    }

    /// <summary>Total harmonic distortion [%] from harmonic amplitudes (index 0 = fundamental).</summary>
    public static double Thd(ReadOnlySpan<double> amplitudes)
    {
        if (amplitudes.Length < 2 || amplitudes[0] == 0) return 0;
        double s = 0;
        for (int i = 1; i < amplitudes.Length; i++) s += amplitudes[i] * amplitudes[i];
        return 100 * Math.Sqrt(s) / amplitudes[0];
    }

    public static unsafe double Rms(ReadOnlySpan<double> x)
    {
        if (x.IsEmpty) return 0;
        if (UseNative) fixed (double* p = x) return NativeMethods.Rms(p, (nuint)x.Length);
        double acc = 0;
        foreach (var v in x) acc += v * v;
        return Math.Sqrt(acc / x.Length);
    }

    /// <summary>S = V ∘ conj(Y·V) for a CSR complex matrix (row pointers, column indices, values).</summary>
    public static unsafe void PowerInjections(ReadOnlySpan<int> rowPtr, ReadOnlySpan<int> colIdx, ReadOnlySpan<Complex> values,
        ReadOnlySpan<Complex> v, Span<Complex> s)
    {
        int n = v.Length;
        if (rowPtr.Length != n + 1 || s.Length != n) throw new ArgumentException("Dimension mismatch.");
        if (UseNative)
        {
            fixed (int* rp = rowPtr)
            fixed (int* ci = colIdx)
            fixed (Complex* y = values)
            fixed (Complex* vp = v)
            fixed (Complex* sp = s)
                if (NativeMethods.PowerInjections((nuint)n, rp, ci, (double*)y, (double*)vp, (double*)sp) == 0) return;
        }
        for (int i = 0; i < n; i++)
        {
            Complex acc = 0;
            for (int p = rowPtr[i]; p < rowPtr[i + 1]; p++) acc += values[p] * v[colIdx[p]];
            s[i] = v[i] * Complex.Conjugate(acc);
        }
    }

    /// <summary>Deterministic N(mean, std) samples (SplitMix64 + Box–Muller); identical native and managed.</summary>
    public static unsafe void NormalSamples(ulong seed, double mean, double std, Span<double> output)
    {
        if (UseNative)
        {
            fixed (double* o = output)
                if (NativeMethods.NormalSamples(seed, mean, std, o, (nuint)output.Length) == 0) return;
        }
        ulong st = seed;
        for (int i = 0; i < output.Length; i += 2)
        {
            double u1 = Unit(ref st), u2 = Unit(ref st);
            double r = Math.Sqrt(-2 * Math.Log(u1)), t = 2 * Math.PI * u2;
            output[i] = mean + std * r * Math.Cos(t);
            if (i + 1 < output.Length) output[i + 1] = mean + std * r * Math.Sin(t);
        }
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static double Unit(ref ulong state) => ((SplitMix64(ref state) >> 11) + 1.0) * (1.0 / 9007199254740992.0);
}
