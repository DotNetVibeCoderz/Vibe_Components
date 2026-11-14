using System.Reflection;
using System.Runtime.InteropServices;

namespace Electrical.Native;

internal static partial class NativeMethods
{
    internal const string Lib = "electrical_native";

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
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
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

    [LibraryImport(Lib, EntryPoint = "eln_version")]
    internal static partial uint Version();

    [LibraryImport(Lib, EntryPoint = "eln_fft")]
    internal static unsafe partial int Fft(double* data, nuint n, int inverse);

    [LibraryImport(Lib, EntryPoint = "eln_harmonics")]
    internal static unsafe partial int Harmonics(double* x, nuint len, double fs, double f0, double* output, nuint maxH);

    [LibraryImport(Lib, EntryPoint = "eln_rms")]
    internal static unsafe partial double Rms(double* x, nuint len);

    [LibraryImport(Lib, EntryPoint = "eln_power_injections")]
    internal static unsafe partial int PowerInjections(nuint n, int* rowPtr, int* colIdx, double* vals, double* v, double* s);

    [LibraryImport(Lib, EntryPoint = "eln_normal_samples")]
    internal static unsafe partial int NormalSamples(ulong seed, double mean, double std, double* output, nuint len);
}
