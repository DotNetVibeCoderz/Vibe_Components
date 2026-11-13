using System.Buffers;

namespace Medical.Native;

/// <summary>Summary statistics of rescaled pixel values (e.g. Hounsfield units).</summary>
public readonly record struct PixelStats(long Count, double Min, double Max, double Mean, double StdDev);

/// <summary>Which implementation executes the kernels.</summary>
public enum KernelBackend { Managed, Rust }

/// <summary>
/// High-performance medical kernels. Uses the Rust <c>medical_native</c> library when it can be loaded,
/// otherwise a managed implementation with identical semantics (same formulas, same f64 arithmetic).
/// </summary>
public static class MedicalKernels
{
    private static readonly Lazy<bool> _nativeAvailable = new(() =>
    {
        try { return NativeMethods.Version() >= 1_00_00; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>Set to <see cref="KernelBackend.Managed"/> to force the managed path (benchmarks, tests).</summary>
    public static KernelBackend PreferredBackend { get; set; } = KernelBackend.Rust;

    public static bool IsNativeAvailable => _nativeAvailable.Value;

    public static KernelBackend ActiveBackend => PreferredBackend == KernelBackend.Rust && IsNativeAvailable ? KernelBackend.Rust : KernelBackend.Managed;

    // ---------------------------------------------------------------- VOI window / level

    /// <summary>DICOM PS3.3 C.11.2.1.2 linear VOI function for a single (already rescaled) value.</summary>
    public static byte WindowValue(double value, double center, double width, bool invert)
    {
        double lo = center - 0.5 - (width - 1.0) / 2.0;
        double hi = center - 0.5 + (width - 1.0) / 2.0;
        double y = value <= lo ? 0.0 : value > hi ? 255.0 : ((value - (center - 0.5)) / (width - 1.0) + 0.5) * 255.0;
        byte b = (byte)(y + 0.5);
        return invert ? (byte)(255 - b) : b;
    }

    /// <summary>
    /// Maps signed stored pixels through Modality LUT (<paramref name="slope"/>, <paramref name="intercept"/>) and the
    /// linear VOI window to 8-bit grayscale. <paramref name="invert"/> is used for MONOCHROME1.
    /// </summary>
    public static unsafe void WindowLevel(ReadOnlySpan<short> src, Span<byte> dst, double slope, double intercept, double center, double width, bool invert = false)
    {
        CheckWindow(src.Length, dst.Length, width);
        if (ActiveBackend == KernelBackend.Rust)
        {
            fixed (short* ps = src) fixed (byte* pd = dst)
                ThrowIfError(NativeMethods.WindowLevelI16(ps, pd, (nuint)src.Length, slope, intercept, center, width, invert ? 1 : 0));
            return;
        }
        for (int i = 0; i < src.Length; i++) dst[i] = WindowValue(src[i] * slope + intercept, center, width, invert);
    }

    /// <inheritdoc cref="WindowLevel(ReadOnlySpan{short}, Span{byte}, double, double, double, double, bool)"/>
    public static unsafe void WindowLevel(ReadOnlySpan<ushort> src, Span<byte> dst, double slope, double intercept, double center, double width, bool invert = false)
    {
        CheckWindow(src.Length, dst.Length, width);
        if (ActiveBackend == KernelBackend.Rust)
        {
            fixed (ushort* ps = src) fixed (byte* pd = dst)
                ThrowIfError(NativeMethods.WindowLevelU16(ps, pd, (nuint)src.Length, slope, intercept, center, width, invert ? 1 : 0));
            return;
        }
        for (int i = 0; i < src.Length; i++) dst[i] = WindowValue(src[i] * slope + intercept, center, width, invert);
    }

    // ---------------------------------------------------------------- statistics

    /// <summary>Statistics of <c>raw * slope + intercept</c> (Welford, numerically stable).</summary>
    public static unsafe PixelStats Stats(ReadOnlySpan<short> values, double slope = 1, double intercept = 0)
    {
        MdnStats s;
        if (ActiveBackend == KernelBackend.Rust)
        {
            fixed (short* pv = values) ThrowIfError(NativeMethods.StatsI16(pv, (nuint)values.Length, slope, intercept, &s));
        }
        else
        {
            s = new MdnStats { Min = double.PositiveInfinity, Max = double.NegativeInfinity };
            foreach (var raw in values)
            {
                double v = raw * slope + intercept;
                s.Count++;
                if (v < s.Min) s.Min = v;
                if (v > s.Max) s.Max = v;
                double d = v - s.Mean;
                s.Mean += d / s.Count;
                s.M2 += d * (v - s.Mean);
            }
            if (s.Count == 0) { s.Min = s.Max = s.Mean = double.NaN; }
        }
        double sd = s.Count > 1 ? Math.Sqrt(s.M2 / (s.Count - 1)) : 0;
        return new PixelStats((long)s.Count, s.Min, s.Max, s.Mean, sd);
    }

    // ---------------------------------------------------------------- ECG

    /// <summary>
    /// Pan-Tompkins style QRS detection. Returns sample indices of R peaks.
    /// <paramref name="sampleRate"/> in Hz; <paramref name="thresholdRatio"/> in (0, 1) relative to the
    /// maximum of the integrated energy signal.
    /// </summary>
    public static unsafe int[] DetectRPeaks(ReadOnlySpan<float> signal, double sampleRate, double thresholdRatio = 0.3)
    {
        if (!(sampleRate > 0)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!(thresholdRatio > 0 && thresholdRatio < 1)) throw new ArgumentOutOfRangeException(nameof(thresholdRatio));
        if (ActiveBackend == KernelBackend.Rust)
        {
            // At most one beat per refractory period (200 ms).
            int capacity = Math.Max(1, (int)(signal.Length / Math.Max(1, Math.Round(0.2 * sampleRate))) + 2);
            var buffer = ArrayPool<uint>.Shared.Rent(capacity);
            try
            {
                nuint count;
                fixed (float* ps = signal) fixed (uint* po = buffer)
                    ThrowIfError(NativeMethods.DetectRPeaks(ps, (nuint)signal.Length, sampleRate, thresholdRatio, po, (nuint)buffer.Length, &count));
                var result = new int[(int)count];
                for (int i = 0; i < result.Length; i++) result[i] = (int)buffer[i];
                return result;
            }
            finally { ArrayPool<uint>.Shared.Return(buffer); }
        }
        return DetectRPeaksManaged(signal, sampleRate, thresholdRatio);
    }

    private static int[] DetectRPeaksManaged(ReadOnlySpan<float> x, double fs, double thresholdRatio)
    {
        int n = x.Length;
        if (n < 8) return [];
        int w = Math.Max(1, (int)Math.Round(0.150 * fs, MidpointRounding.AwayFromZero));
        int refractory = (int)Math.Round(0.200 * fs, MidpointRounding.AwayFromZero);
        var sq = ArrayPool<double>.Shared.Rent(n);
        var integ = ArrayPool<double>.Shared.Rent(n);
        try
        {
            double acc = 0, max = 0;
            for (int i = 0; i < n; i++)
            {
                double d = i >= 4 ? (2.0 * x[i] + (double)x[i - 1] - x[i - 3] - 2.0 * x[i - 4]) / 8.0 : 0.0;
                sq[i] = d * d;
                acc += sq[i];
                if (i >= w) acc -= sq[i - w];
                integ[i] = acc / w;
                if (integ[i] > max) max = integ[i];
            }
            if (max <= 0) return [];
            double thr = thresholdRatio * max;
            var peaks = new List<int>();
            int last = -1;
            for (int i = 0; i < n;)
            {
                if (integ[i] > thr)
                {
                    int start = i;
                    while (i < n && integ[i] > thr) i++;
                    int end = i - 1, from = Math.Max(0, start - w), best = from;
                    for (int k = from; k <= end; k++) if (x[k] > x[best]) best = k;
                    if (last < 0 || best >= last + refractory) { peaks.Add(best); last = best; }
                }
                else i++;
            }
            return [.. peaks];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sq);
            ArrayPool<double>.Shared.Return(integ);
        }
    }

    private static void CheckWindow(int src, int dst, double width)
    {
        if (dst < src) throw new ArgumentException("Destination is shorter than source.", nameof(dst));
        if (!(width >= 1)) throw new ArgumentOutOfRangeException(nameof(width), "Window width must be >= 1.");
    }

    private static void ThrowIfError(int code)
    {
        if (code == 0) return;
        throw code switch
        {
            -1 => new ArgumentNullException("buffer", "Native kernel received a null buffer."),
            -2 => new ArgumentException("Native kernel rejected an argument."),
            -3 => new InvalidOperationException("Native kernel output buffer too small."),
            _ => new InvalidOperationException($"Native kernel error {code}."),
        };
    }
}
