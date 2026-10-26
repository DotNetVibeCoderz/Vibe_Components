using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>
/// Entry point of GDAL.Net: opening/creating datasets, drivers, configuration and runtime information.
/// <para>Created by Gravicode Studios, led by Kang Fadhil.</para>
/// </summary>
public static class Gdal
{
    /// <summary>Forces native initialization (optional: it happens automatically on first use).</summary>
    public static void Initialize() => _ = GdalNative.GDALGetDriverCount();

    /// <summary>GDAL release name, e.g. "3.13.3".</summary>
    public static string Version => Interop.PtrToString(GdalNative.GDALVersionInfo("RELEASE_NAME")) ?? "unknown";

    /// <summary>GDAL version as a numeric value, e.g. 3130300.</summary>
    public static int VersionNumber => int.TryParse(Interop.PtrToString(GdalNative.GDALVersionInfo("VERSION_NUM")), out var v) ? v : 0;

    /// <summary>Build information string (compiled features, dependencies).</summary>
    public static string BuildInfo => Interop.PtrToString(GdalNative.GDALVersionInfo("BUILD_INFO")) ?? string.Empty;

    /// <summary>Version of the PROJ library GDAL is linked against.</summary>
    public static unsafe Version ProjVersion
    {
        get
        {
            int major, minor, patch;
            GdalNative.OSRGetPROJVersion(&major, &minor, &patch);
            return new Version(major, minor, patch);
        }
    }

    /// <summary>Directory used as GDAL_DATA (null if none was found).</summary>
    public static string? DataPath { get { Initialize(); return GdalRuntime.GdalDataPath; } }

    /// <summary>Directory containing proj.db (null if none was found).</summary>
    public static string? ProjDataPath { get { Initialize(); return GdalRuntime.ProjDataPath; } }

    /// <summary>Path of the native GDAL library that was loaded.</summary>
    public static string? NativeLibraryPath { get { Initialize(); return NativeLibraryLoader.LoadedGdalPath; } }

    /// <summary>Raised for every debug/warning/error message emitted by GDAL (any thread).</summary>
    public static event Action<GdalLogMessage>? Log
    {
        add => GdalRuntime.Log += value;
        remove => GdalRuntime.Log -= value;
    }

    // ------------------------------------------------------------------ configuration

    /// <summary>Sets a global GDAL configuration option (e.g. GDAL_CACHEMAX, GDAL_NUM_THREADS, AWS_REGION).</summary>
    public static void SetConfigOption(string key, string? value) => GdalNative.CPLSetConfigOption(key, value);

    /// <summary>Sets a configuration option only for the calling thread.</summary>
    public static void SetThreadLocalConfigOption(string key, string? value) => GdalNative.CPLSetThreadLocalConfigOption(key, value);

    /// <summary>Gets a configuration option value.</summary>
    public static string? GetConfigOption(string key, string? defaultValue = null)
        => Interop.PtrToString(GdalNative.CPLGetConfigOption(key, defaultValue));

    /// <summary>Sets an option for a path prefix only (e.g. credentials for "/vsis3/my-bucket").</summary>
    public static void SetPathSpecificOption(string pathPrefix, string key, string? value)
        => GdalNative.VSISetPathSpecificOption(pathPrefix, key, value);

    /// <summary>Clears path specific options for a prefix (or all when null).</summary>
    public static void ClearPathSpecificOptions(string? pathPrefix = null) => GdalNative.VSIClearPathSpecificOptions(pathPrefix);

    /// <summary>Raster block cache size, in bytes.</summary>
    public static long CacheMax
    {
        get => GdalNative.GDALGetCacheMax64();
        set => GdalNative.GDALSetCacheMax64(value);
    }

    /// <summary>Bytes currently used by the raster block cache.</summary>
    public static long CacheUsed => GdalNative.GDALGetCacheUsed64();

    /// <summary>Temporarily sets configuration options; the previous values are restored on dispose.</summary>
    public static IDisposable ConfigScope(IReadOnlyDictionary<string, string?> options)
    {
        var previous = options.Keys.ToDictionary(k => k, k => GetConfigOption(k));
        foreach (var kv in options) SetConfigOption(kv.Key, kv.Value);
        return new ActionDisposable(() =>
        {
            foreach (var kv in previous) SetConfigOption(kv.Key, kv.Value);
        });
    }

    // ------------------------------------------------------------------ drivers

    /// <summary>All registered drivers.</summary>
    public static IReadOnlyList<Driver> Drivers
    {
        get
        {
            var count = GdalNative.GDALGetDriverCount();
            var list = new List<Driver>(count);
            for (var i = 0; i < count; i++)
            {
                list.Add(new Driver(GdalNative.GDALGetDriver(i)));
            }
            return list;
        }
    }

    /// <summary>Gets a driver by short name (e.g. "GTiff", "COG", "GPKG", "GeoJSON", "MEM").</summary>
    public static Driver GetDriver(string shortName)
    {
        var h = GdalNative.GDALGetDriverByName(shortName);
        return h == 0 ? throw new GdalException($"GDAL driver '{shortName}' is not available in this build.") : new Driver(h);
    }

    /// <summary>Tries to get a driver by short name.</summary>
    public static Driver? TryGetDriver(string shortName)
    {
        var h = GdalNative.GDALGetDriverByName(shortName);
        return h == 0 ? null : new Driver(h);
    }

    /// <summary>Identifies the driver able to open a file, or null.</summary>
    public static Driver? IdentifyDriver(string path)
    {
        var h = GdalNative.GDALIdentifyDriver(path, 0);
        return h == 0 ? null : new Driver(h);
    }

    // ------------------------------------------------------------------ open

    /// <summary>
    /// Opens a raster and/or vector dataset. Paths may use any GDAL virtual file system
    /// (/vsimem/, /vsizip/, /vsicurl/, /vsis3/, /vsiaz/, /vsigs/, ...).
    /// </summary>
    public static Dataset Open(string path, OpenFlags flags = OpenFlags.All, IEnumerable<string>? allowedDrivers = null, IDictionary<string, string>? openOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var drivers = new NativeStringList(allowedDrivers);
        using var options = NativeStringList.FromOptions(openOptions);
        var h = GdalNative.GDALOpenEx(path, (uint)(flags | OpenFlags.VerboseError), drivers.Pointer, options.Pointer, 0);
        if (h == 0)
        {
            throw GdalNative.LastError($"Unable to open '{path}'");
        }
        return new Dataset(h);
    }

    /// <summary>Opens a raster dataset (read-only unless <paramref name="update"/>).</summary>
    public static Dataset OpenRaster(string path, bool update = false)
        => Open(path, OpenFlags.Raster | (update ? OpenFlags.Update : OpenFlags.ReadOnly));

    /// <summary>Opens a vector dataset (read-only unless <paramref name="update"/>).</summary>
    public static Dataset OpenVector(string path, bool update = false)
        => Open(path, OpenFlags.Vector | (update ? OpenFlags.Update : OpenFlags.ReadOnly));

    /// <summary>Opens a multidimensional dataset (netCDF, HDF5, Zarr, ...) exposing <see cref="Dataset.RootGroup"/>.</summary>
    public static Dataset OpenMultidimensional(string path, bool update = false)
        => Open(path, OpenFlags.MultidimensionalRaster | (update ? OpenFlags.Update : OpenFlags.ReadOnly));

    /// <summary>Tries to open a dataset; returns null instead of throwing.</summary>
    public static Dataset? TryOpen(string path, OpenFlags flags = OpenFlags.All)
    {
        var h = GdalNative.GDALOpenEx(path, (uint)flags, 0, 0, 0);
        if (h == 0)
        {
            GdalNative.CPLErrorReset();
            return null;
        }
        return new Dataset(h);
    }

    // ------------------------------------------------------------------ create helpers

    /// <summary>Creates an in-memory raster (MEM driver) — ideal for intermediate processing.</summary>
    public static Dataset CreateMemoryRaster(int width, int height, int bandCount = 1, DataType dataType = DataType.Float32)
        => GetDriver("MEM").Create(string.Empty, width, height, bandCount, dataType);

    /// <summary>Creates an in-memory vector dataset (MEM driver).</summary>
    public static Dataset CreateMemoryVector(string name = "memory")
        => GetDriver("MEM").CreateVector(name);
}

internal sealed class ActionDisposable(Action action) : IDisposable
{
    private Action? _action = action;

    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}
