using System.Reflection;
using System.Runtime.InteropServices;

namespace GnuRadio.Kernels;

/// <summary>
/// P/Invoke surface of the optional Rust library <c>gnuradio_native</c> (see <c>native/gnuradio-native</c>).
/// Every entry point takes raw pointers and lengths; the managed side owns all memory.
/// </summary>
internal static unsafe partial class NativeKernels
{
    private const string Library = "gnuradio_native";
    private static readonly Lazy<bool> s_available = new(Probe);

    static NativeKernels()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeKernels).Assembly, Resolve);
    }

    public static bool IsAvailable => s_available.Value;

    /// <summary>ABI version reported by the library; the managed side requires a matching major version.</summary>
    public const uint ExpectedAbiVersion = 1;

    private static bool Probe()
    {
        try
        {
            return gr_native_abi_version() == ExpectedAbiVersion;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle)) return handle;

        var file = OperatingSystem.IsWindows() ? "gnuradio_native.dll"
                 : OperatingSystem.IsMacOS() ? "libgnuradio_native.dylib"
                 : "libgnuradio_native.so";
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, file),
                     Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
                     Path.Combine(baseDir, "runtimes", PortableRid(), "native", file),
                 })
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle)) return handle;
        }
        return IntPtr.Zero;
    }

    private static string PortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    [LibraryImport(Library)]
    private static partial uint gr_native_abi_version();

    [LibraryImport(Library)]
    private static partial void gr_complex_multiply(float* a, float* b, float* dst, nuint n);

    [LibraryImport(Library)]
    private static partial void gr_magnitude_squared(float* x, float* dst, nuint n);

    [LibraryImport(Library)]
    private static partial void gr_rotate(float* x, float incRe, float incIm, float* phase, float* dst, nuint n);

    [LibraryImport(Library)]
    private static partial void gr_fir_fff(float* input, nuint outputs, float* reversedTaps, nuint taps, nuint decimation, float* output);

    [LibraryImport(Library)]
    private static partial void gr_fir_ccf(float* input, nuint outputs, float* reversedTaps, nuint taps, nuint decimation, float* output);

    [LibraryImport(Library)]
    private static partial int gr_fft_c2c(float* data, nuint n, int inverse);

    public static void ComplexMultiply(ReadOnlySpan<Complex32> a, ReadOnlySpan<Complex32> b, Span<Complex32> dst, int n)
    {
        fixed (Complex32* pa = a, pb = b, pd = dst)
            gr_complex_multiply((float*)pa, (float*)pb, (float*)pd, (nuint)n);
    }

    public static void MagnitudeSquared(ReadOnlySpan<Complex32> x, Span<float> dst, int n)
    {
        fixed (Complex32* px = x)
        fixed (float* pd = dst)
            gr_magnitude_squared((float*)px, pd, (nuint)n);
    }

    public static void Rotate(ReadOnlySpan<Complex32> x, Complex32 increment, ref Complex32 phase, Span<Complex32> dst, int n)
    {
        fixed (Complex32* px = x, pd = dst, pp = &phase)
            gr_rotate((float*)px, increment.Real, increment.Imaginary, (float*)pp, (float*)pd, (nuint)n);
    }

    /// <summary>Real FIR: output[k] = Σ input[k·decim + j]·reversedTaps[j].</summary>
    public static void FirFloat(ReadOnlySpan<float> input, ReadOnlySpan<float> reversedTaps, int decimation, Span<float> output)
    {
        fixed (float* pi = input, pt = reversedTaps, po = output)
            gr_fir_fff(pi, (nuint)output.Length, pt, (nuint)reversedTaps.Length, (nuint)decimation, po);
    }

    /// <summary>Complex samples, real taps FIR; <paramref name="interleavedTaps"/> is reversed and duplicated (t, t).</summary>
    public static void FirComplexReal(ReadOnlySpan<Complex32> input, ReadOnlySpan<float> interleavedTaps, int decimation, Span<Complex32> output)
    {
        fixed (Complex32* pi = input, po = output)
        fixed (float* pt = interleavedTaps)
            gr_fir_ccf((float*)pi, (nuint)output.Length, pt, (nuint)(interleavedTaps.Length / 2), (nuint)decimation, (float*)po);
    }

    /// <summary>In-place complex FFT (rustfft). Unnormalised in both directions.</summary>
    public static void Fft(Span<Complex32> data, bool inverse)
    {
        fixed (Complex32* p = data)
            if (gr_fft_c2c((float*)p, (nuint)data.Length, inverse ? 1 : 0) != 0)
                throw new InvalidOperationException("Native FFT failed.");
    }
}
