using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace GnuRadio.Kernels;

/// <summary>
/// Vectorised math kernels, the counterpart of GNU Radio's VOLK library. Each kernel has a portable
/// SIMD implementation (<see cref="Vector128"/>/<see cref="Vector256"/>, so SSE/AVX on x64 and NEON on
/// ARM64). When the optional Rust library <c>gnuradio_native</c> is present and
/// <see cref="UseNative"/> is true, FIR filtering and FFTs are routed to it: those are the kernels where it
/// measured faster (see docs/en/performance.md); element-wise kernels stay managed because they are not.
/// </summary>
public static class Volk
{
    /// <summary>True when the native Rust kernels were found and loaded.</summary>
    public static bool NativeAvailable => NativeKernels.IsAvailable;

    /// <summary>Routes kernels to the native library when available. Defaults to false; benchmark before enabling.</summary>
    public static bool UseNative { get; set; }

    /// <summary>Describes the SIMD path the managed kernels take on this machine.</summary>
    public static string Machine =>
        Vector256.IsHardwareAccelerated ? "avx2/256-bit" :
        Vector128.IsHardwareAccelerated ? "sse/neon 128-bit" : "scalar";

    /// <summary>dst[i] = a[i] · b[i] (complex). <c>volk_32fc_x2_multiply_32fc</c>.</summary>
    public static void Multiply(ReadOnlySpan<Complex32> a, ReadOnlySpan<Complex32> b, Span<Complex32> dst)
    {
        var n = Math.Min(a.Length, Math.Min(b.Length, dst.Length));
        var fa = MemoryMarshal.Cast<Complex32, float>(a[..n]);
        var fb = MemoryMarshal.Cast<Complex32, float>(b[..n]);
        var fd = MemoryMarshal.Cast<Complex32, float>(dst[..n]);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var sign = Vector256.Create(-1f, 1f, -1f, 1f, -1f, 1f, -1f, 1f);
            for (; i + 8 <= fa.Length; i += 8)
            {
                var va = Vector256.Create(fa.Slice(i, 8));
                var vb = Vector256.Create(fb.Slice(i, 8));
                var bRe = Vector256.Shuffle(vb, Vector256.Create(0, 0, 2, 2, 4, 4, 6, 6));
                var bIm = Vector256.Shuffle(vb, Vector256.Create(1, 1, 3, 3, 5, 5, 7, 7));
                var aSwap = Vector256.Shuffle(va, Vector256.Create(1, 0, 3, 2, 5, 4, 7, 6));
                (va * bRe + aSwap * bIm * sign).CopyTo(fd.Slice(i, 8));
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var sign = Vector128.Create(-1f, 1f, -1f, 1f);
            for (; i + 4 <= fa.Length; i += 4)
            {
                var va = Vector128.Create(fa.Slice(i, 4));
                var vb = Vector128.Create(fb.Slice(i, 4));
                var bRe = Vector128.Shuffle(vb, Vector128.Create(0, 0, 2, 2));
                var bIm = Vector128.Shuffle(vb, Vector128.Create(1, 1, 3, 3));
                var aSwap = Vector128.Shuffle(va, Vector128.Create(1, 0, 3, 2));
                (va * bRe + aSwap * bIm * sign).CopyTo(fd.Slice(i, 4));
            }
        }
        for (var k = i / 2; k < n; k++) dst[k] = a[k] * b[k];
    }

    /// <summary>dst[i] = a[i] · conj(b[i]). <c>volk_32fc_x2_multiply_conjugate_32fc</c>.</summary>
    public static void MultiplyConjugate(ReadOnlySpan<Complex32> a, ReadOnlySpan<Complex32> b, Span<Complex32> dst)
    {
        var n = Math.Min(a.Length, Math.Min(b.Length, dst.Length));
        for (var k = 0; k < n; k++)
        {
            var x = a[k];
            var y = b[k];
            dst[k] = new Complex32(x.Real * y.Real + x.Imaginary * y.Imaginary, x.Imaginary * y.Real - x.Real * y.Imaginary);
        }
    }

    /// <summary>dst[i] = |x[i]|². <c>volk_32fc_magnitude_squared_32f</c>.</summary>
    public static void MagnitudeSquared(ReadOnlySpan<Complex32> x, Span<float> dst)
    {
        var n = Math.Min(x.Length, dst.Length);
        var f = MemoryMarshal.Cast<Complex32, float>(x[..n]);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= n; i += 8)
            {
                var lo = Vector256.Create(f.Slice(2 * i, 8));
                var hi = Vector256.Create(f.Slice(2 * i + 8, 8));
                lo *= lo;
                hi *= hi;
                // Sum adjacent pairs: (re² + im²) for 8 samples.
                var even = Vector256.Shuffle(lo, Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6)).GetLower();
                var odd = Vector256.Shuffle(lo, Vector256.Create(1, 3, 5, 7, 1, 3, 5, 7)).GetLower();
                var even2 = Vector256.Shuffle(hi, Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6)).GetLower();
                var odd2 = Vector256.Shuffle(hi, Vector256.Create(1, 3, 5, 7, 1, 3, 5, 7)).GetLower();
                Vector256.Create(even + odd, even2 + odd2).CopyTo(dst.Slice(i, 8));
            }
        }
        for (; i < n; i++) dst[i] = x[i].MagnitudeSquared;
    }

    /// <summary>dst[i] = |x[i]|. <c>volk_32fc_magnitude_32f</c>.</summary>
    public static void Magnitude(ReadOnlySpan<Complex32> x, Span<float> dst)
    {
        var n = Math.Min(x.Length, dst.Length);
        MagnitudeSquared(x[..n], dst);
        Sqrt(dst[..n], dst);
    }

    /// <summary>dst[i] = √x[i].</summary>
    public static void Sqrt(ReadOnlySpan<float> x, Span<float> dst)
    {
        var n = Math.Min(x.Length, dst.Length);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; i + 8 <= n; i += 8)
                Vector256.Sqrt(Vector256.Create(x.Slice(i, 8))).CopyTo(dst.Slice(i, 8));
        for (; i < n; i++) dst[i] = MathF.Sqrt(x[i]);
    }

    /// <summary>dst[i] = atan2(im, re). <c>volk_32fc_s32f_atan2_32f</c>.</summary>
    public static void Phase(ReadOnlySpan<Complex32> x, Span<float> dst)
    {
        var n = Math.Min(x.Length, dst.Length);
        for (var i = 0; i < n; i++) dst[i] = MathF.Atan2(x[i].Imaginary, x[i].Real);
    }

    /// <summary>Σ a[i]·b[i]. <c>volk_32f_x2_dot_prod_32f</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        var sum = 0f;
        if (Vector256.IsHardwareAccelerated && n >= 16)
        {
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            ref var ra = ref MemoryMarshal.GetReference(a);
            ref var rb = ref MemoryMarshal.GetReference(b);
            for (; i + 16 <= n; i += 16)
            {
                acc0 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref rb, (nuint)i), acc0);
                acc1 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref ra, (nuint)i + 8), Vector256.LoadUnsafe(ref rb, (nuint)i + 8), acc1);
            }
            sum = Vector256.Sum(acc0 + acc1);
        }
        else if (Vector128.IsHardwareAccelerated && n >= 8)
        {
            var acc = Vector128<float>.Zero;
            ref var ra = ref MemoryMarshal.GetReference(a);
            ref var rb = ref MemoryMarshal.GetReference(b);
            for (; i + 4 <= n; i += 4)
                acc = Vector128.FusedMultiplyAdd(Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref rb, (nuint)i), acc);
            sum = Vector128.Sum(acc);
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// Σ x[i]·t[i] with complex samples and real taps, where <paramref name="interleavedTaps"/> holds every tap
    /// twice (t0,t0,t1,t1,…) so the product is a plain float dot product with separate even/odd sums.
    /// <c>volk_32fc_32f_dot_prod_32fc</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 DotComplexReal(ReadOnlySpan<Complex32> x, ReadOnlySpan<float> interleavedTaps)
    {
        var f = MemoryMarshal.Cast<Complex32, float>(x);
        var n = Math.Min(f.Length, interleavedTaps.Length);
        var i = 0;
        float re = 0, im = 0;
        ref var rx = ref MemoryMarshal.GetReference(f);
        ref var rt = ref MemoryMarshal.GetReference(interleavedTaps);
        if (Vector256.IsHardwareAccelerated && n >= 16)
        {
            var acc0 = Vector256<float>.Zero;
            var acc1 = Vector256<float>.Zero;
            for (; i + 16 <= n; i += 16)
            {
                acc0 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref rx, (nuint)i), Vector256.LoadUnsafe(ref rt, (nuint)i), acc0);
                acc1 = Vector256.FusedMultiplyAdd(Vector256.LoadUnsafe(ref rx, (nuint)i + 8), Vector256.LoadUnsafe(ref rt, (nuint)i + 8), acc1);
            }
            var acc = acc0 + acc1;
            re = acc[0] + acc[2] + acc[4] + acc[6];
            im = acc[1] + acc[3] + acc[5] + acc[7];
        }
        else if (Vector128.IsHardwareAccelerated && n >= 8)
        {
            var acc = Vector128<float>.Zero;
            for (; i + 4 <= n; i += 4)
                acc = Vector128.FusedMultiplyAdd(Vector128.LoadUnsafe(ref rx, (nuint)i), Vector128.LoadUnsafe(ref rt, (nuint)i), acc);
            re = acc[0] + acc[2];
            im = acc[1] + acc[3];
        }
        for (; i + 1 < n; i += 2)
        {
            re += f[i] * interleavedTaps[i];
            im += f[i + 1] * interleavedTaps[i + 1];
        }
        return new Complex32(re, im);
    }

    /// <summary>Σ x[i]·t[i] with complex samples and complex taps. <c>volk_32fc_x2_dot_prod_32fc</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Complex32 Dot(ReadOnlySpan<Complex32> x, ReadOnlySpan<Complex32> taps)
    {
        var n = Math.Min(x.Length, taps.Length);
        var fx = MemoryMarshal.Cast<Complex32, float>(x[..n]);
        var ft = MemoryMarshal.Cast<Complex32, float>(taps[..n]);
        var i = 0;
        float re = 0, im = 0;
        if (Vector256.IsHardwareAccelerated && fx.Length >= 8)
        {
            // Accumulate x·tRe and swap(x)·tIm separately, combine signs once at the end.
            var accA = Vector256<float>.Zero;
            var accB = Vector256<float>.Zero;
            ref var rx = ref MemoryMarshal.GetReference(fx);
            ref var rt = ref MemoryMarshal.GetReference(ft);
            for (; i + 8 <= fx.Length; i += 8)
            {
                var vx = Vector256.LoadUnsafe(ref rx, (nuint)i);
                var vt = Vector256.LoadUnsafe(ref rt, (nuint)i);
                var tRe = Vector256.Shuffle(vt, Vector256.Create(0, 0, 2, 2, 4, 4, 6, 6));
                var tIm = Vector256.Shuffle(vt, Vector256.Create(1, 1, 3, 3, 5, 5, 7, 7));
                var xSwap = Vector256.Shuffle(vx, Vector256.Create(1, 0, 3, 2, 5, 4, 7, 6));
                accA = Vector256.FusedMultiplyAdd(vx, tRe, accA);    // [xr tr, xi tr]
                accB = Vector256.FusedMultiplyAdd(xSwap, tIm, accB); // [xi ti, xr ti]
            }
            re = accA[0] + accA[2] + accA[4] + accA[6] - (accB[0] + accB[2] + accB[4] + accB[6]);
            im = accA[1] + accA[3] + accA[5] + accA[7] + accB[1] + accB[3] + accB[5] + accB[7];
        }
        for (var k = i / 2; k < n; k++)
        {
            var p = x[k] * taps[k];
            re += p.Real;
            im += p.Imaginary;
        }
        return new Complex32(re, im);
    }

    /// <summary>dst[i] = x[i] · scale.</summary>
    public static void Scale(ReadOnlySpan<float> x, float scale, Span<float> dst)
    {
        var n = Math.Min(x.Length, dst.Length);
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var s = Vector256.Create(scale);
            for (; i + 8 <= n; i += 8)
                (Vector256.Create(x.Slice(i, 8)) * s).CopyTo(dst.Slice(i, 8));
        }
        for (; i < n; i++) dst[i] = x[i] * scale;
    }

    /// <summary>dst[i] = x[i] · scale (complex samples, real scale).</summary>
    public static void Scale(ReadOnlySpan<Complex32> x, float scale, Span<Complex32> dst) =>
        Scale(MemoryMarshal.Cast<Complex32, float>(x), scale, MemoryMarshal.Cast<Complex32, float>(dst));

    /// <summary>dst[i] = a[i] + b[i] (element-wise on floats).</summary>
    public static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        var n = Math.Min(a.Length, Math.Min(b.Length, dst.Length));
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; i + 8 <= n; i += 8)
                (Vector256.Create(a.Slice(i, 8)) + Vector256.Create(b.Slice(i, 8))).CopyTo(dst.Slice(i, 8));
        for (; i < n; i++) dst[i] = a[i] + b[i];
    }

    /// <summary>dst[i] = a[i] · b[i] (element-wise on floats).</summary>
    public static void Multiply(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> dst)
    {
        var n = Math.Min(a.Length, Math.Min(b.Length, dst.Length));
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; i + 8 <= n; i += 8)
                (Vector256.Create(a.Slice(i, 8)) * Vector256.Create(b.Slice(i, 8))).CopyTo(dst.Slice(i, 8));
        for (; i < n; i++) dst[i] = a[i] * b[i];
    }

    /// <summary>
    /// Mixes <paramref name="x"/> with a rotating phasor: dst[i] = x[i]·phase·inc^i, updating
    /// <paramref name="phase"/> in place. The phasor is renormalised every block so long runs stay unit
    /// magnitude. <c>volk_32fc_s32fc_x2_rotator_32fc</c>.
    /// </summary>
    public static void Rotate(ReadOnlySpan<Complex32> x, Complex32 increment, ref Complex32 phase, Span<Complex32> dst)
    {
        var n = Math.Min(x.Length, dst.Length);
        var p = phase;
        const int renormalize = 512;
        for (var i = 0; i < n; i++)
        {
            dst[i] = x[i] * p;
            p *= increment;
            if ((i & (renormalize - 1)) == renormalize - 1)
                p /= p.Magnitude;
        }
        phase = p / p.Magnitude;
    }

    /// <summary>10·log10(x + ε), for power spectra in dB.</summary>
    public static void PowerToDecibels(ReadOnlySpan<float> power, Span<float> dst, float offsetDb = 0f)
    {
        var n = Math.Min(power.Length, dst.Length);
        for (var i = 0; i < n; i++) dst[i] = 10f * MathF.Log10(power[i] + 1e-20f) + offsetDb;
    }

    /// <summary>Converts interleaved I/Q float pairs to complex samples (a reinterpretation, no math).</summary>
    public static ReadOnlySpan<Complex32> AsComplex(ReadOnlySpan<float> interleaved) =>
        MemoryMarshal.Cast<float, Complex32>(interleaved);

    /// <summary>Maximum element and its index.</summary>
    public static (int Index, float Value) ArgMax(ReadOnlySpan<float> x)
    {
        if (x.IsEmpty) return (-1, float.NaN);
        var best = 0;
        var value = x[0];
        for (var i = 1; i < x.Length; i++)
            if (x[i] > value) { value = x[i]; best = i; }
        return (best, value);
    }

    /// <summary>Arithmetic mean.</summary>
    public static float Mean(ReadOnlySpan<float> x)
    {
        if (x.IsEmpty) return 0;
        var sum = 0.0;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var acc = Vector256<float>.Zero;
            for (; i + 8 <= x.Length; i += 8) acc += Vector256.Create(x.Slice(i, 8));
            sum = Vector256.Sum(acc);
        }
        for (; i < x.Length; i++) sum += x[i];
        return (float)(sum / x.Length);
    }

    internal static float[] Interleave(ReadOnlySpan<float> taps)
    {
        var result = new float[taps.Length * 2];
        for (var i = 0; i < taps.Length; i++)
        {
            result[2 * i] = taps[i];
            result[2 * i + 1] = taps[i];
        }
        return result;
    }

    // Vector<T> is referenced so trimming keeps the generic SIMD path consistent on all runtimes.
    internal static int PreferredVectorWidth => Vector<float>.Count;
}
