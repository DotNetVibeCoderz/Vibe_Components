using System.Reflection;
using System.Runtime.InteropServices;

namespace AquaCulture.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct AqStats
{
    public ulong Count;
    public double Min;
    public double Max;
    public double Mean;
    public double M2;
}

internal static partial class NativeMethods
{
    internal const string Lib = "aqua_native";

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

    [LibraryImport(Lib, EntryPoint = "aq_version")]
    internal static partial uint Version();

    [LibraryImport(Lib, EntryPoint = "aq_nh3_fraction_batch")]
    internal static unsafe partial int Nh3FractionBatch(double* temp, double* ph, double* sal, double* output, nuint len);

    [LibraryImport(Lib, EntryPoint = "aq_do_saturation_batch")]
    internal static unsafe partial int DoSaturationBatch(double* temp, double* sal, double pressureKPa, double* output, nuint len);

    [LibraryImport(Lib, EntryPoint = "aq_stats")]
    internal static unsafe partial int Stats(float* values, nuint len, AqStats* output);

    [LibraryImport(Lib, EntryPoint = "aq_tgc_monte_carlo")]
    internal static unsafe partial int TgcMonteCarlo(double w0, double tgc, double* temps, nuint ndays, nuint nsims, double tempSd, ulong seed, double* output);

    [LibraryImport(Lib, EntryPoint = "aq_advect_diffuse_step")]
    internal static unsafe partial int AdvectDiffuseStep(float* c, float* output, float* u, float* v, byte* wet, float* source,
        nuint nx, nuint ny, float dx, float dt, float diff, float decay);
}
