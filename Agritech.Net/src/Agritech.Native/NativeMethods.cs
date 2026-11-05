using System.Reflection;
using System.Runtime.InteropServices;

namespace Agritech.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct AgtStats
{
    public ulong Count;
    public double Min;
    public double Max;
    public double Mean;
    public double M2;
}

/// <summary>Daily weather record shared with the native ET0 kernel (C layout).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeDailyWeather
{
    public double Tmin;
    public double Tmax;
    public double RhMean;
    public double U2;
    public double Rs;
    public int DayOfYear;
}

internal static partial class NativeMethods
{
    internal const string Lib = "agritech_native";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        if (NativeLibrary.TryLoad(name, asm, path, out var h)) return h;
        string file = OperatingSystem.IsWindows() ? $"{Lib}.dll" : OperatingSystem.IsMacOS() ? $"lib{Lib}.dylib" : $"lib{Lib}.so";
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, file),
            Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", file),
            Path.Combine(baseDir, "runtimes", $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "native", file),
        ];
        foreach (var c in candidates)
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out h)) return h;
        return IntPtr.Zero;
    }

    [LibraryImport(Lib, EntryPoint = "agt_version")]
    internal static partial uint Version();

    [LibraryImport(Lib, EntryPoint = "agt_normalized_difference")]
    internal static unsafe partial int NormalizedDifference(float* a, float* b, float* output, nuint len);

    [LibraryImport(Lib, EntryPoint = "agt_savi")]
    internal static unsafe partial int Savi(float* nir, float* red, float* output, nuint len, float l);

    [LibraryImport(Lib, EntryPoint = "agt_evi")]
    internal static unsafe partial int Evi(float* nir, float* red, float* blue, float* output, nuint len, float g, float c1, float c2, float l);

    [LibraryImport(Lib, EntryPoint = "agt_stats")]
    internal static unsafe partial int Stats(float* values, nuint len, AgtStats* output);

    [LibraryImport(Lib, EntryPoint = "agt_et0_pm_batch")]
    internal static unsafe partial int Et0Batch(NativeDailyWeather* days, nuint len, double latDeg, double elev, double* output);
}
