using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using GDAL.Net.Native;

namespace GDAL.Net.Processing;

/// <summary>Result of <see cref="Kernels.Statistics"/>.</summary>
public readonly record struct ArrayStatistics(double Minimum, double Maximum, double Mean, double StdDev, long ValidCount);

/// <summary>
/// Hot-path array kernels. Uses the Rust library <c>gdalnet_kernels</c> (rayon data parallelism, LTO) when present
/// and falls back to managed SIMD (System.Numerics.Tensors / Vector&lt;T&gt;) otherwise — results are identical.
/// </summary>
public static unsafe partial class Kernels
{
    private const string Lib = NativeLibraryLoader.KernelsLibraryName;

    private static readonly Lazy<bool> s_native = new(ProbeNative);

    /// <summary>True when the Rust accelerated library is loaded.</summary>
    public static bool IsNativeAccelerated => s_native.Value && !ForceManaged;

    /// <summary>Forces the managed implementation (benchmarks / diagnostics).</summary>
    public static bool ForceManaged { get; set; }

    /// <summary>"Rust (rayon, N threads)" or "Managed SIMD (Vector256)".</summary>
    public static string Backend => IsNativeAccelerated
        ? $"Rust (rayon, {gnk_thread_count()} threads)"
        : $"Managed SIMD (Vector{Vector<byte>.Count * 8})";

    private static bool ProbeNative()
    {
        try
        {
            return gnk_version() == 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [LibraryImport(Lib)] private static partial uint gnk_version();
    [LibraryImport(Lib)] private static partial uint gnk_thread_count();
    [LibraryImport(Lib)] private static partial int gnk_stats_f32(float* data, nuint len, float nodata, int hasNoData, NativeStats* output);
    [LibraryImport(Lib)] private static partial int gnk_histogram_f32(float* data, nuint len, double min, double max, nuint buckets, float nodata, int hasNoData, ulong* output);
    [LibraryImport(Lib)] private static partial int gnk_normalized_difference_f32(float* a, float* b, float* output, nuint len, float nodataIn, int hasNoData, float nodataOut);
    [LibraryImport(Lib)] private static partial int gnk_colorize_f32(float* data, nuint len, float min, float max, byte* lut, float nodata, int hasNoData, byte* rgba);
    [LibraryImport(Lib)] private static partial int gnk_composite_rgb_f32(float* r, float* g, float* b, nuint len, float* mins, float* maxs, float nodata, int hasNoData, byte* rgba);
    [LibraryImport(Lib)] private static partial int gnk_blend_shade(byte* rgba, byte* shade, nuint len, float strength);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStats
    {
        public double Min, Max, Mean, StdDev;
        public ulong ValidCount;
    }

    private static bool IsValid(float v, float nodata, bool hasNoData) => !float.IsNaN(v) && !(hasNoData && v == nodata);

    // ------------------------------------------------------------------ statistics

    /// <summary>Min/max/mean/std-dev ignoring NaN and the optional nodata value.</summary>
    public static ArrayStatistics Statistics(ReadOnlySpan<float> data, float? noData = null)
    {
        var nd = noData ?? 0;
        var has = noData.HasValue;
        if (IsNativeAccelerated)
        {
            NativeStats s;
            fixed (float* p = data)
            {
                gnk_stats_f32(p, (nuint)data.Length, nd, has ? 1 : 0, &s);
            }
            return new ArrayStatistics(s.Min, s.Max, s.Mean, s.StdDev, (long)s.ValidCount);
        }

        if (!has && data.Length > 0 && !HasNaN(data))
        {
            // Fully vectorized path (no masking needed). Sum in 64k slices to limit float accumulation error.
            var min = TensorPrimitives.Min(data);
            var max = TensorPrimitives.Max(data);
            double sum = 0;
            for (var i = 0; i < data.Length; i += 65536)
            {
                sum += TensorPrimitives.Sum(data.Slice(i, Math.Min(65536, data.Length - i)));
            }
            var mean = sum / data.Length;
            double m2 = 0;
            foreach (var v in data)
            {
                var d = v - mean;
                m2 += d * d;
            }
            return new ArrayStatistics(min, max, mean, Math.Sqrt(m2 / data.Length), data.Length);
        }

        double mn = double.PositiveInfinity, mx = double.NegativeInfinity, total = 0;
        long n = 0;
        foreach (var v in data)
        {
            if (!IsValid(v, nd, has)) continue;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
            total += v;
            n++;
        }
        if (n == 0) return new ArrayStatistics(double.NaN, double.NaN, double.NaN, double.NaN, 0);
        var mu = total / n;
        double acc = 0;
        foreach (var v in data)
        {
            if (!IsValid(v, nd, has)) continue;
            var d = v - mu;
            acc += d * d;
        }
        return new ArrayStatistics(mn, mx, mu, Math.Sqrt(acc / n), n);
    }

    private static bool HasNaN(ReadOnlySpan<float> data)
    {
        foreach (var v in data)
        {
            if (float.IsNaN(v)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ histogram

    /// <summary>Histogram with equal buckets over [min, max].</summary>
    public static ulong[] Histogram(ReadOnlySpan<float> data, double min, double max, int buckets = 256, float? noData = null)
    {
        var result = new ulong[buckets];
        if (max <= min || buckets <= 0) return result;
        var nd = noData ?? 0;
        var has = noData.HasValue;
        if (IsNativeAccelerated)
        {
            fixed (float* p = data)
            fixed (ulong* h = result)
            {
                gnk_histogram_f32(p, (nuint)data.Length, min, max, (nuint)buckets, nd, has ? 1 : 0, h);
            }
            return result;
        }
        var scale = buckets / (max - min);
        foreach (var v in data)
        {
            if (!IsValid(v, nd, has) || v < min || v > max) continue;
            var i = (int)((v - min) * scale);
            if (i >= buckets) i = buckets - 1;
            result[i]++;
        }
        return result;
    }

    // ------------------------------------------------------------------ spectral

    /// <summary>(a - b) / (a + b) for every element. Invalid inputs → <paramref name="noDataOut"/>.</summary>
    public static void NormalizedDifference(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output, float? noDataIn = null, float noDataOut = float.NaN)
    {
        if (a.Length != b.Length || output.Length < a.Length) throw new ArgumentException("Spans must have matching lengths.");
        var nd = noDataIn ?? 0;
        var has = noDataIn.HasValue;
        if (IsNativeAccelerated)
        {
            fixed (float* pa = a)
            fixed (float* pb = b)
            fixed (float* po = output)
            {
                gnk_normalized_difference_f32(pa, pb, po, (nuint)a.Length, nd, has ? 1 : 0, noDataOut);
            }
            return;
        }

        // Vectorized: out = (a-b)/(a+b), then patch invalid lanes.
        var len = a.Length;
        var rented = System.Buffers.ArrayPool<float>.Shared.Rent(len);
        try
        {
            var sum = rented.AsSpan(0, len);
            TensorPrimitives.Subtract(a, b, output[..len]);
            TensorPrimitives.Add(a, b, sum);
            TensorPrimitives.Divide(output[..len], sum, output[..len]);
            for (var i = 0; i < len; i++)
            {
                if (!IsValid(a[i], nd, has) || !IsValid(b[i], nd, has) || sum[i] == 0) output[i] = noDataOut;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(rented);
        }
    }

    // ------------------------------------------------------------------ color mapping

    /// <summary>Maps floats to RGBA with a 256-entry LUT (1024 bytes) and a linear stretch. Invalid pixels → transparent.</summary>
    public static void Colorize(ReadOnlySpan<float> data, float min, float max, ReadOnlySpan<byte> lut, Span<byte> rgba, float? noData = null)
    {
        if (lut.Length < 1024) throw new ArgumentException("LUT must hold 256 RGBA entries.", nameof(lut));
        if (rgba.Length < data.Length * 4) throw new ArgumentException("Output buffer is too small.", nameof(rgba));
        var nd = noData ?? 0;
        var has = noData.HasValue;
        if (IsNativeAccelerated)
        {
            fixed (float* p = data)
            fixed (byte* l = lut)
            fixed (byte* o = rgba)
            {
                gnk_colorize_f32(p, (nuint)data.Length, min, max, l, nd, has ? 1 : 0, o);
            }
            return;
        }
        var range = max > min ? max - min : 1f;
        var k = 255f / range;
        var lut32 = MemoryMarshal.Cast<byte, uint>(lut);
        var out32 = MemoryMarshal.Cast<byte, uint>(rgba);
        for (var i = 0; i < data.Length; i++)
        {
            var v = data[i];
            if (!IsValid(v, nd, has))
            {
                out32[i] = 0;
                continue;
            }
            var t = (int)Math.Clamp((v - min) * k, 0f, 255f);
            out32[i] = lut32[t];
        }
    }

    /// <summary>Linear stretch of three bands into an RGBA composite.</summary>
    public static void CompositeRgb(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> mins, ReadOnlySpan<float> maxs,
        Span<byte> rgba, float? noData = null)
    {
        var len = r.Length;
        if (g.Length != len || b.Length != len || rgba.Length < len * 4) throw new ArgumentException("Spans must have matching lengths.");
        var nd = noData ?? 0;
        var has = noData.HasValue;
        if (IsNativeAccelerated)
        {
            fixed (float* pr = r)
            fixed (float* pg = g)
            fixed (float* pb = b)
            fixed (float* pmin = mins)
            fixed (float* pmax = maxs)
            fixed (byte* o = rgba)
            {
                gnk_composite_rgb_f32(pr, pg, pb, (nuint)len, pmin, pmax, nd, has ? 1 : 0, o);
            }
            return;
        }
        Span<float> k = stackalloc float[3];
        for (var c = 0; c < 3; c++) k[c] = 255f / (maxs[c] > mins[c] ? maxs[c] - mins[c] : 1f);
        for (var i = 0; i < len; i++)
        {
            float vr = r[i], vg = g[i], vb = b[i];
            var o = i * 4;
            if (!IsValid(vr, nd, has) || !IsValid(vg, nd, has) || !IsValid(vb, nd, has))
            {
                rgba[o] = rgba[o + 1] = rgba[o + 2] = rgba[o + 3] = 0;
                continue;
            }
            rgba[o] = (byte)Math.Clamp((vr - mins[0]) * k[0], 0f, 255f);
            rgba[o + 1] = (byte)Math.Clamp((vg - mins[1]) * k[1], 0f, 255f);
            rgba[o + 2] = (byte)Math.Clamp((vb - mins[2]) * k[2], 0f, 255f);
            rgba[o + 3] = 255;
        }
    }

    /// <summary>Multiplies RGBA pixels by a hillshade intensity (0..255) with the given strength (0..1).</summary>
    public static void BlendShade(Span<byte> rgba, ReadOnlySpan<byte> shade, float strength = 0.6f)
    {
        var len = shade.Length;
        if (rgba.Length < len * 4) throw new ArgumentException("Output buffer is too small.", nameof(rgba));
        if (IsNativeAccelerated)
        {
            fixed (byte* p = rgba)
            fixed (byte* s = shade)
            {
                gnk_blend_shade(p, s, (nuint)len, strength);
            }
            return;
        }
        var st = Math.Clamp(strength, 0f, 1f);
        for (var i = 0; i < len; i++)
        {
            var f = 1f - st + st * (shade[i] / 255f);
            var o = i * 4;
            rgba[o] = (byte)(rgba[o] * f);
            rgba[o + 1] = (byte)(rgba[o + 1] * f);
            rgba[o + 2] = (byte)(rgba[o + 2] * f);
        }
    }
}
