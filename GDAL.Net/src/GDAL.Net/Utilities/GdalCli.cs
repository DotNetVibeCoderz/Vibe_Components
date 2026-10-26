using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>Result of a <see cref="GdalCli"/> run.</summary>
/// <param name="Output">Text output (for info-like algorithms), if any.</param>
/// <param name="Dataset">Output dataset (for in-memory outputs), if any. Caller owns it.</param>
public sealed record GdalCliResult(string? Output, Dataset? Dataset);

/// <summary>
/// Runs any algorithm of the unified "gdal" command line (GDAL ≥ 3.11) in-process, e.g.
/// <c>GdalCli.Run("raster", "tile", "--min-zoom", "0", "--max-zoom", "5", "in.tif", "tiles/")</c>,
/// <c>GdalCli.Run("raster", "calc", "-i", "A=a.tif", "--calc", "A*2", "-o", "out.tif")</c> or
/// <c>GdalCli.Run("vector", "info", "--format", "json", "file.gpkg")</c>.
/// </summary>
public static unsafe class GdalCli
{
    /// <summary>True when the GDAL build exposes the algorithm API.</summary>
    public static bool IsAvailable => Gdal.VersionNumber >= 3110000;

    /// <summary>Top-level algorithm names ("raster", "vector", "mdim", "convert", "info", ...).</summary>
    public static IReadOnlyList<string> AlgorithmNames
    {
        get
        {
            var reg = GdalNative.GDALGetGlobalAlgorithmRegistry();
            try
            {
                var alg = GdalNative.GDALAlgorithmRegistryInstantiateAlg(reg, "gdal");
                if (alg == 0) return [];
                try
                {
                    return Interop.TakeStringList(GdalNative.GDALAlgorithmGetSubAlgorithmNames(alg));
                }
                finally
                {
                    GdalNative.GDALAlgorithmRelease(alg);
                }
            }
            finally
            {
                GdalNative.GDALAlgorithmRegistryRelease(reg);
            }
        }
    }

    /// <summary>Usage of an algorithm path (e.g. "raster", "reproject") as JSON.</summary>
    public static string GetUsageJson(params string[] path)
    {
        var reg = GdalNative.GDALGetGlobalAlgorithmRegistry();
        var current = GdalNative.GDALAlgorithmRegistryInstantiateAlg(reg, "gdal");
        try
        {
            foreach (var name in path)
            {
                var sub = GdalNative.GDALAlgorithmInstantiateSubAlgorithm(current, name);
                if (sub == 0) throw new ArgumentException($"Unknown gdal algorithm '{string.Join(' ', path)}'.");
                GdalNative.GDALAlgorithmRelease(current);
                current = sub;
            }
            return Interop.TakeString(GdalNative.GDALAlgorithmGetUsageAsJSON(current)) ?? string.Empty;
        }
        finally
        {
            if (current != 0) GdalNative.GDALAlgorithmRelease(current);
            GdalNative.GDALAlgorithmRegistryRelease(reg);
        }
    }

    /// <summary>Runs a "gdal ..." command line (without the leading "gdal").</summary>
    public static GdalCliResult Run(params string[] args) => Run(args, null, default);

    /// <summary>Runs a "gdal ..." command line with progress and cancellation.</summary>
    public static GdalCliResult Run(IReadOnlyList<string> args, IProgress<GdalProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new NotSupportedException($"The 'gdal' algorithm API requires GDAL 3.11+, loaded {Gdal.Version}.");
        return WithAlgorithm(args, actual =>
        {
            using var bridge = ProgressBridge.Create(progress, cancellationToken);
            var ok = GdalNative.GDALAlgorithmRun(actual, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
            if (!ok) throw GdalNative.LastError($"gdal {string.Join(' ', args)}");

            string? output = null;
            var outArg = GdalNative.GDALAlgorithmGetArg(actual, "output-string");
            if (outArg != 0)
            {
                output = Interop.PtrToString(GdalNative.GDALAlgorithmArgGetAsString(outArg));
                GdalNative.GDALAlgorithmArgRelease(outArg);
            }

            Dataset? dataset = null;
            var dsArg = GdalNative.GDALAlgorithmGetArg(actual, "output");
            if (dsArg != 0)
            {
                // Only dataset-typed "output" arguments carry an opened dataset.
                if (GdalNative.GDALAlgorithmArgGetType(dsArg) == GaatDataset)
                {
                    var value = GdalNative.GDALAlgorithmArgGetAsDatasetValue(dsArg);
                    if (value != 0)
                    {
                        var h = GdalNative.GDALArgDatasetValueGetDatasetIncreaseRefCount(value);
                        if (h != 0) dataset = new Dataset(h);
                        GdalNative.GDALArgDatasetValueRelease(value);
                    }
                }
                GdalNative.GDALAlgorithmArgRelease(dsArg);
            }

            if (!GdalNative.GDALAlgorithmFinalize(actual))
            {
                dataset?.Dispose();
                throw GdalNative.LastError($"gdal {string.Join(' ', args)} (finalize)");
            }
            return new GdalCliResult(output, dataset);
        });
    }

    private const int GaatDataset = 4;

    private static T WithAlgorithm<T>(IReadOnlyList<string> args, Func<nint, T> action)
    {
        var reg = GdalNative.GDALGetGlobalAlgorithmRegistry();
        var root = GdalNative.GDALAlgorithmRegistryInstantiateAlg(reg, "gdal");
        try
        {
            if (root == 0) throw GdalNative.LastError("gdal algorithm registry");
            nint actual;
            using (var list = new NativeStringList(args))
            {
                if (!GdalNative.GDALAlgorithmParseCommandLineArguments(root, list.Pointer))
                {
                    throw GdalNative.LastError($"gdal {string.Join(' ', args)}: invalid arguments");
                }
            }
            actual = GdalNative.GDALAlgorithmGetActualAlgorithm(root);
            try
            {
                return action(actual == 0 ? root : actual);
            }
            finally
            {
                if (actual != 0 && actual != root) GdalNative.GDALAlgorithmRelease(actual);
            }
        }
        finally
        {
            if (root != 0) GdalNative.GDALAlgorithmRelease(root);
            GdalNative.GDALAlgorithmRegistryRelease(reg);
        }
    }
}
