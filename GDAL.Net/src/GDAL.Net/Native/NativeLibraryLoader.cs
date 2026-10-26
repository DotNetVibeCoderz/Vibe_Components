using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GDAL.Net.Native;

/// <summary>
/// Locates the native GDAL library (shipped by the MaxRev.Gdal.*Runtime packages) and the optional
/// Rust kernel library on every supported platform, regardless of whether the app was built
/// RID-specific, portable, self-contained or single-file.
/// </summary>
internal static class NativeLibraryLoader
{
    internal const string GdalLibraryName = "gdal";
    internal const string KernelsLibraryName = "gdalnet_kernels";

    /// <summary>Environment variable that may point to an explicit gdal shared library.</summary>
    public const string GdalLibraryPathVariable = "GDALNET_LIBRARY_PATH";

    private static string? s_gdalPath;

    /// <summary>Full path of the GDAL library that was loaded (null until first use or when resolved by the OS).</summary>
    public static string? LoadedGdalPath => s_gdalPath;

#pragma warning disable CA2255 // Module initializers are intended for app code; here we only register a resolver.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already registered by the host; respect it.
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return libraryName switch
        {
            GdalLibraryName => LoadGdal(assembly, searchPath),
            KernelsLibraryName => LoadFromCandidates(KernelCandidates(), assembly, searchPath, scanGdalSonames: false, out _),
            _ => 0,
        };
    }

    private static nint LoadGdal(Assembly assembly, DllImportSearchPath? searchPath)
    {
        var explicitPath = Environment.GetEnvironmentVariable(GdalLibraryPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && NativeLibrary.TryLoad(explicitPath, out var h))
        {
            s_gdalPath = explicitPath;
            return h;
        }

        var handle = LoadFromCandidates(GdalCandidates(), assembly, searchPath, scanGdalSonames: true, out var path);
        s_gdalPath = path;
        return handle;
    }

    private static nint LoadFromCandidates(IEnumerable<string> fileNames, Assembly assembly, DllImportSearchPath? searchPath, bool scanGdalSonames, out string? loadedPath)
    {
        var names = fileNames.ToArray();
        foreach (var dir in SearchDirectories())
        {
            foreach (var name in names)
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var handle))
                {
                    loadedPath = full;
                    return handle;
                }
            }

            // Versioned sonames we do not know in advance (libgdal.so.40, libgdal.41.dylib, ...).
            if (scanGdalSonames && !OperatingSystem.IsWindows() && Directory.Exists(dir))
            {
                {
                    foreach (var candidate in Directory.EnumerateFiles(dir, "libgdal*"))
                    {
                        var file = Path.GetFileName(candidate);
                        if (file.Contains("wrap", StringComparison.Ordinal)) continue;
                        if (NativeLibrary.TryLoad(candidate, out var handle))
                        {
                            loadedPath = candidate;
                            return handle;
                        }
                    }
                }
            }
        }

        // Fall back to the platform loader (system-wide GDAL installation, LD_LIBRARY_PATH, PATH, ...).
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
            {
                loadedPath = null;
                return handle;
            }
        }

        loadedPath = null;
        return 0;
    }

    private static IEnumerable<string> GdalCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "gdal.dll";
            yield return "gdal313.dll";
            yield return "libgdal.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "libgdal.39.dylib";
            yield return "libgdal.dylib";
        }
        else
        {
            yield return "libgdal.so.39";
            yield return "libgdal.so";
        }
    }

    private static IEnumerable<string> KernelCandidates()
    {
        if (OperatingSystem.IsWindows()) yield return "gdalnet_kernels.dll";
        else if (OperatingSystem.IsMacOS()) yield return "libgdalnet_kernels.dylib";
        else yield return "libgdalnet_kernels.so";
    }

    /// <summary>Directories probed for native binaries, most specific first.</summary>
    internal static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDir in BaseDirectories())
        {
            foreach (var rid in RuntimeIdentifiers())
            {
                var dir = Path.Combine(baseDir, "runtimes", rid, "native");
                if (seen.Add(dir)) yield return dir;
            }
            if (seen.Add(baseDir)) yield return baseDir;
        }
    }

    private static IEnumerable<string> BaseDirectories()
    {
        yield return AppContext.BaseDirectory;
#pragma warning disable IL3000 // Empty in single-file apps; AppContext.BaseDirectory is probed first.
        var asmLocation = typeof(NativeLibraryLoader).Assembly.Location;
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(asmLocation))
        {
            var dir = Path.GetDirectoryName(asmLocation);
            if (!string.IsNullOrEmpty(dir)) yield return dir;
        }
    }

    internal static IEnumerable<string> RuntimeIdentifiers()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        yield return RuntimeInformation.RuntimeIdentifier;
        yield return $"{os}-{arch}";
        if (os == "linux") yield return $"linux-musl-{arch}";
    }
}
