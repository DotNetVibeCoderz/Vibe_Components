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
                if (scanGdalSonames && OperatingSystem.IsWindows() && File.Exists(full)) ThrowIfDependencyShadowed(dir);
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

    /// <summary>
    /// Windows resolves a DLL import by module name first: if a module with the same file name is already loaded, the loader reuses it
    /// whatever its directory, and neither search flags nor activation contexts override that. A managed assembly with the same file
    /// name as one of GDAL's native dependencies (Parquet.Net's <c>Parquet.dll</c> versus GDAL's <c>parquet.dll</c>) therefore breaks
    /// GDAL with a bare <see cref="BadImageFormatException"/> when it is loaded first. This turns that into an explanation.
    /// </summary>
    private static void ThrowIfDependencyShadowed(string nativeDir)
    {
        var shadowed = FindShadowingManagedModules(nativeDir);
        if (shadowed.Count == 0) return;
        var (name, from) = shadowed[0];
        throw new GdalException(
            $"GDAL cannot be loaded: the managed assembly '{Path.GetFileName(from)}' ({from}) is already loaded and has the same file name as " +
            $"GDAL's native dependency '{name}'. Windows reuses an already-loaded module by name, so GDAL would bind to the managed file. " +
            "Call GDAL.Net.Gdal.Initialize() at application start-up, before the library that brings the managed assembly (for example " +
            "Parquet.Net) is first used; after that both work side by side. Alternatively run GDAL in a separate process.");
    }

    /// <summary>
    /// Managed assemblies currently loaded as Windows modules whose file name equals a native library in <paramref name="nativeDir"/>
    /// (and that are not that library). Empty on other platforms.
    /// </summary>
    internal static IReadOnlyList<(string NativeName, string LoadedFrom)> FindShadowingManagedModules(string nativeDir)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(nativeDir)) return [];
        var result = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(nativeDir, "*.dll"))
        {
            var name = Path.GetFileName(file);
            nint module = GetModuleHandleW(name);
            if (module == 0) continue;
            var buffer = new char[32768];
            int len = GetModuleFileNameW(module, buffer, buffer.Length);
            if (len <= 0) continue;
            var loaded = new string(buffer, 0, len);
            if (string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) continue;
            if (IsManagedImage(loaded)) result.Add((name, loaded));
        }
        return result;
    }

    private static bool IsManagedImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.PEHeaders.CorHeader is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileNameW(nint module, [Out] char[] fileName, int size);

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
