using System.Reflection;
using System.Runtime.InteropServices;

namespace Medical.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct MdnStats
{
    public ulong Count;
    public double Min;
    public double Max;
    public double Mean;
    public double M2;
}

internal static partial class NativeMethods
{
    internal const string Lib = "medical_native";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        if (NativeLibrary.TryLoad(name, asm, path, out var h)) return h;
        string file = OperatingSystem.IsWindows() ? $"{Lib}.dll" : OperatingSystem.IsMacOS() ? $"lib{Lib}.dylib" : $"lib{Lib}.so";
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, file),
            Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
            Path.Combine(baseDir, "runtimes", $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "native", file),
        ];
        foreach (var c in candidates)
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out h)) return h;
        return IntPtr.Zero;
    }

    [LibraryImport(Lib, EntryPoint = "mdn_version")]
    internal static partial uint Version();

    [LibraryImport(Lib, EntryPoint = "mdn_stats_i16")]
    internal static unsafe partial int StatsI16(short* values, nuint len, double slope, double intercept, MdnStats* output);

    [LibraryImport(Lib, EntryPoint = "mdn_window_level_i16")]
    internal static unsafe partial int WindowLevelI16(short* src, byte* dst, nuint len, double slope, double intercept, double center, double width, int invert);

    [LibraryImport(Lib, EntryPoint = "mdn_window_level_u16")]
    internal static unsafe partial int WindowLevelU16(ushort* src, byte* dst, nuint len, double slope, double intercept, double center, double width, int invert);

    [LibraryImport(Lib, EntryPoint = "mdn_slab_i16")]
    internal static unsafe partial int SlabI16(short* vol, nuint nx, nuint ny, nuint nz, double* origin, double* u, double* v, double* n, uint steps, int mode, short fill, short* output, nuint outW, nuint outH);

    [LibraryImport(Lib, EntryPoint = "mdn_detect_r_peaks")]
    internal static unsafe partial int DetectRPeaks(float* signal, nuint len, double fs, double thresholdRatio, uint* output, nuint capacity, nuint* count);
}
