using System.Reflection;
using System.Runtime.InteropServices;
using Highs;

namespace Electrical.Optimization.Highs;

/// <summary>
/// Resolves the native library "highs" of the <c>Highs.Native</c> wrapper to the copy this package ships as
/// <c>runtimes/&lt;rid&gt;/native/highs-native.{dll,so,dylib}</c> (renamed so that it does not collide with the different highs.dll bundled by
/// Google OR-Tools). Falls back to the default probing (a plain "highs" library on the search path).
/// </summary>
internal static class HighsNativeLoader
{
    private static int _installed;

    public static void EnsureInstalled()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        try { NativeLibrary.SetDllImportResolver(typeof(HighsLpSolver).Assembly, Resolve); }
        catch (InvalidOperationException) { /* a resolver was already set by the host */ }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (!string.Equals(name, "highs", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        string ext = OperatingSystem.IsWindows() ? "dll" : OperatingSystem.IsMacOS() ? "dylib" : "so";
        string file = $"highs-native.{ext}";
        string rid = RuntimeInformation.RuntimeIdentifier;
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var dirs = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(HighsNativeLoader).Assembly.Location) ?? "" }
            .Where(d => d.Length > 0).Distinct();
        foreach (var dir in dirs)
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, "runtimes", $"{os}-{arch}", "native", file), Path.Combine(dir, "runtimes", rid, "native", file),
                         Path.Combine(dir, file),
                     })
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;
    }
}
