using System.Numerics;
using System.Runtime.InteropServices;

namespace Agritech.Native;

/// <summary>NaN-aware summary statistics for a raster band.</summary>
public readonly record struct RasterStats(long Count, double Min, double Max, double Mean, double StdDev);

/// <summary>Which implementation executes the kernels.</summary>
public enum KernelBackend { Managed, Rust }

/// <summary>
/// High-performance raster kernels. Uses the Rust <c>agritech_native</c> library when it can be loaded,
/// otherwise a managed <see cref="Vector{T}"/> SIMD implementation with identical semantics.
/// Division by zero yields <see cref="float.NaN"/> (no-data).
/// </summary>
public static class AgriKernels
{
    private static readonly Lazy<bool> _nativeAvailable = new(() =>
    {
        try { return NativeMethods.Version() >= 1_00_00; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>Set to <see cref="KernelBackend.Managed"/> to force the managed path (e.g. for benchmarking).</summary>
    public static KernelBackend PreferredBackend { get; set; } = KernelBackend.Rust;

    public static bool IsNativeAvailable => _nativeAvailable.Value;

    public static KernelBackend ActiveBackend => PreferredBackend == KernelBackend.Rust && IsNativeAvailable ? KernelBackend.Rust : KernelBackend.Managed;

    /// <summary>(a - b) / (a + b) — NDVI(nir, red), NDWI(green, nir), NDRE(nir, rededge), GNDVI(nir, green), NBR(nir, swir2)…</summary>
    public static unsafe void NormalizedDifference(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        Check(a.Length, b.Length, output.Length);
        if (ActiveBackend == KernelBackend.Rust)
        {
            fixed (float* pa = a) fixed (float* pb = b) fixed (float* po = output)
                ThrowIfError(NativeMethods.NormalizedDifference(pa, pb, po, (nuint)a.Length));
            return;
        }
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            var vo = MemoryMarshal.Cast<float, Vector<float>>(output);
            var nan = new Vector<float>(float.NaN);
            for (int k = 0; k < va.Length; k++)
            {
                var sum = va[k] + vb[k];
                var res = (va[k] - vb[k]) / sum;
                vo[k] = Vector.ConditionalSelect(Vector.Equals(sum, Vector<float>.Zero), nan, res);
            }
            i = va.Length * Vector<float>.Count;
        }
        for (; i < a.Length; i++)
        {
            float s = a[i] + b[i];
            output[i] = s == 0 ? float.NaN : (a[i] - b[i]) / s;
        }
    }

    /// <summary>Soil-Adjusted Vegetation Index: (1+L)(NIR-Red)/(NIR+Red+L).</summary>
    public static unsafe void Savi(ReadOnlySpan<float> nir, ReadOnlySpan<float> red, Span<float> output, float l = 0.5f)
    {
        Check(nir.Length, red.Length, output.Length);
        if (ActiveBackend == KernelBackend.Rust)
        {
            fixed (float* pn = nir) fixed (float* pr = red) fixed (float* po = output)
                ThrowIfError(NativeMethods.Savi(pn, pr, po, (nuint)nir.Length, l));
            return;
        }
        for (int i = 0; i < nir.Length; i++)
        {
            float d = nir[i] + red[i] + l;
            output[i] = d == 0 ? float.NaN : (1 + l) * (nir[i] - red[i]) / d;
        }
    }

    /// <summary>Enhanced Vegetation Index: G(NIR-Red)/(NIR + C1·Red − C2·Blue + L).</summary>
    public static unsafe void Evi(ReadOnlySpan<float> nir, ReadOnlySpan<float> red, ReadOnlySpan<float> blue, Span<float> output,
        float g = 2.5f, float c1 = 6f, float c2 = 7.5f, float l = 1f)
    {
        Check(nir.Length, red.Length, output.Length);
        Check(nir.Length, blue.Length, output.Length);
        if (ActiveBackend == KernelBackend.Rust)
        {
            fixed (float* pn = nir) fixed (float* pr = red) fixed (float* pb = blue) fixed (float* po = output)
                ThrowIfError(NativeMethods.Evi(pn, pr, pb, po, (nuint)nir.Length, g, c1, c2, l));
            return;
        }
        for (int i = 0; i < nir.Length; i++)
        {
            float d = nir[i] + c1 * red[i] - c2 * blue[i] + l;
            output[i] = d == 0 ? float.NaN : g * (nir[i] - red[i]) / d;
        }
    }

    /// <summary>Single-pass NaN-aware statistics.</summary>
    public static unsafe RasterStats Statistics(ReadOnlySpan<float> values)
    {
        if (ActiveBackend == KernelBackend.Rust)
        {
            AgtStats s;
            fixed (float* pv = values) ThrowIfError(NativeMethods.Stats(pv, (nuint)values.Length, &s));
            return s.Count == 0 ? new RasterStats(0, double.NaN, double.NaN, double.NaN, double.NaN)
                : new RasterStats((long)s.Count, s.Min, s.Max, s.Mean, s.Count > 1 ? Math.Sqrt(s.M2 / (s.Count - 1)) : 0);
        }
        long n = 0; double mean = 0, m2 = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var f in values)
        {
            if (float.IsNaN(f)) continue;
            double x = f; n++;
            double d = x - mean; mean += d / n; m2 += d * (x - mean);
            if (x < min) min = x; if (x > max) max = x;
        }
        return n == 0 ? new RasterStats(0, double.NaN, double.NaN, double.NaN, double.NaN)
            : new RasterStats(n, min, max, mean, n > 1 ? Math.Sqrt(m2 / (n - 1)) : 0);
    }

    /// <summary>Batch FAO-56 Penman–Monteith ET0 (mm/day). Returns false if the native backend is unavailable.</summary>
    public static unsafe bool TryEt0Batch(ReadOnlySpan<NativeDailyWeather> days, double latitudeDeg, double elevationM, Span<double> output)
    {
        if (ActiveBackend != KernelBackend.Rust) return false;
        Check(days.Length, output.Length, output.Length);
        fixed (NativeDailyWeather* pd = days) fixed (double* po = output)
            ThrowIfError(NativeMethods.Et0Batch(pd, (nuint)days.Length, latitudeDeg, elevationM, po));
        return true;
    }

    private static void Check(int a, int b, int o)
    {
        if (a != b || a != o) throw new ArgumentException($"Band lengths must match (got {a}, {b}, {o}).");
    }

    private static void ThrowIfError(int code)
    {
        if (code != 0) throw new InvalidOperationException($"agritech_native returned error code {code}.");
    }
}
