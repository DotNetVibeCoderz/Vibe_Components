using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>
/// One-time native bootstrap: driver registration, GDAL_DATA / PROJ database discovery,
/// GIS-friendly axis order and the managed error handler. Runs automatically on the first native call.
/// </summary>
internal static unsafe class GdalRuntime
{
    private static int s_bootstrapped;

    public static string? GdalDataPath { get; private set; }
    public static string? ProjDataPath { get; private set; }

    public static event Action<GdalLogMessage>? Log;

    internal static void Bootstrap()
    {
        if (Interlocked.Exchange(ref s_bootstrapped, 1) == 1) return;

        // Silence GDAL's default stderr printer and forward messages to managed listeners.
        GdalNative.CPLSetErrorHandler((nint)(delegate* unmanaged[Cdecl]<int, int, byte*, void>)&ErrorHandler);

        GdalDataPath = ResolveGdalData();
        if (GdalDataPath is not null)
        {
            GdalNative.CPLSetConfigOption("GDAL_DATA", GdalDataPath);

            // The bundled libcurl has no system CA store on Windows: point it at the shipped Mozilla bundle
            // unless the user configured one (needed for https:// with /vsicurl/, /vsis3/, /vsiaz/, /vsigs/).
            var caBundle = Path.Combine(GdalDataPath, "cacert.pem");
            if (File.Exists(caBundle)
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CURL_CA_BUNDLE"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSL_CERT_FILE"))
                && GdalNative.CPLGetConfigOption("CURL_CA_BUNDLE", null) == 0)
            {
                GdalNative.CPLSetConfigOption("CURL_CA_BUNDLE", caBundle);
            }
        }

        ProjDataPath = ResolveProjData();
        if (ProjDataPath is not null)
        {
            using var paths = new NativeStringList([ProjDataPath]);
            GdalNative.OSRSetPROJSearchPaths(paths.Pointer);
        }

        // Longitude/easting first everywhere unless the user opts back into authority order.
        GdalNative.CPLSetConfigOption("OSR_DEFAULT_AXIS_MAPPING_STRATEGY", "TRADITIONAL_GIS_ORDER");
        GdalNative.GDALAllRegister();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ErrorHandler(int errClass, int errNo, byte* message)
    {
        try
        {
            var handler = Log;
            if (handler is null) return;
            var text = message == null ? string.Empty : Marshal.PtrToStringUTF8((nint)message) ?? string.Empty;
            handler(new GdalLogMessage((GdalLogLevel)errClass, errNo, text));
        }
        catch
        {
            // Never propagate managed exceptions into GDAL.
        }
    }

    private static string? ResolveGdalData()
    {
        var env = Environment.GetEnvironmentVariable("GDAL_DATA");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        foreach (var dir in CandidateRoots())
        {
            foreach (var sub in new[] { "gdal-data", Path.Combine("runtimes", "any", "native", "gdal-data"), Path.Combine("share", "gdal") })
            {
                var candidate = Path.Combine(dir, sub);
                if (File.Exists(Path.Combine(candidate, "gdalvrt.xsd")) || File.Exists(Path.Combine(candidate, "header.dxf")))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static string? ResolveProjData()
    {
        foreach (var name in new[] { "PROJ_DATA", "PROJ_LIB" })
        {
            var env = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "proj.db"))) return env;
        }

        var gdalDir = NativeLibraryLoader.LoadedGdalPath is { } p ? Path.GetDirectoryName(p) : null;
        var roots = new List<string>();
        if (gdalDir is not null) roots.Add(gdalDir);
        roots.AddRange(NativeLibraryLoader.SearchDirectories());
        roots.AddRange(CandidateRoots());

        foreach (var dir in roots)
        {
            foreach (var sub in new[] { "maxrev.gdal.core.libshared", "proj", Path.Combine("share", "proj"), "." })
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, sub));
                if (File.Exists(Path.Combine(candidate, "proj.db"))) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return AppContext.BaseDirectory;
#pragma warning disable IL3000 // Empty in single-file apps; AppContext.BaseDirectory is probed first.
        var asm = typeof(GdalRuntime).Assembly.Location;
#pragma warning restore IL3000
        if (!string.IsNullOrEmpty(asm) && Path.GetDirectoryName(asm) is { } d) yield return d;
    }
}
