using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>
/// The GDAL command line utilities as in-process library calls (gdalinfo, gdal_translate, gdalwarp, ogr2ogr, ogrinfo,
/// gdalbuildvrt, gdaldem, gdal_rasterize, gdal_grid, gdalmdiminfo). Every method accepts the exact command line switches
/// documented for the utility, or a strongly-typed options object.
/// </summary>
public static unsafe class GdalUtils
{
    private delegate nint OptionsFactory(nint argv, nint binary);

    private static nint CreateOptions(string utility, IEnumerable<string>? args, OptionsFactory factory)
    {
        using var argv = new NativeStringList(args);
        var options = factory(argv.Pointer, 0);
        return options == 0 ? throw GdalNative.LastError($"{utility}: invalid options '{string.Join(' ', args ?? [])}'") : options;
    }

    private static Dataset Result(nint handle, int usageError, string utility, ProgressBridge? bridge, CancellationToken token)
    {
        ProgressBridge.ThrowIfCancelled(bridge, token);
        if (handle == 0)
        {
            throw GdalNative.LastError(usageError != 0 ? $"{utility}: usage error" : $"{utility} failed");
        }
        return new Dataset(handle);
    }

    // ------------------------------------------------------------------ info

    /// <summary>gdalinfo. Pass "-json" for JSON output, "-stats", "-hist", "-mm", "-checksum", ...</summary>
    public static string Info(Dataset dataset, params string[] args)
    {
        var options = CreateOptions("gdalinfo", args, GdalNative.GDALInfoOptionsNew);
        try
        {
            return Interop.TakeString(GdalNative.GDALInfo(dataset.Handle, options)) ?? throw GdalNative.LastError("gdalinfo failed");
        }
        finally
        {
            GdalNative.GDALInfoOptionsFree(options);
        }
    }

    /// <summary>ogrinfo (gdal vector info). Pass "-json", "-so" (summary only), "-al" (all layers), "-sql", ...</summary>
    public static string VectorInfo(Dataset dataset, params string[] args)
    {
        var options = CreateOptions("ogrinfo", args, GdalNative.GDALVectorInfoOptionsNew);
        try
        {
            return Interop.TakeString(GdalNative.GDALVectorInfo(dataset.Handle, options)) ?? throw GdalNative.LastError("ogrinfo failed");
        }
        finally
        {
            GdalNative.GDALVectorInfoOptionsFree(options);
        }
    }

    /// <summary>gdalmdiminfo: JSON description of a multidimensional dataset.</summary>
    public static string MultiDimInfo(Dataset dataset, params string[] args)
    {
        var options = CreateOptions("gdalmdiminfo", args, GdalNative.GDALMultiDimInfoOptionsNew);
        try
        {
            return Interop.TakeString(GdalNative.GDALMultiDimInfo(dataset.Handle, options)) ?? throw GdalNative.LastError("gdalmdiminfo failed");
        }
        finally
        {
            GdalNative.GDALMultiDimInfoOptionsFree(options);
        }
    }

    // ------------------------------------------------------------------ gdal_translate

    /// <summary>gdal_translate: format conversion, subsetting, scaling, band selection, COG creation, ...</summary>
    public static Dataset Translate(Dataset source, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdal_translate", args, GdalNative.GDALTranslateOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        try
        {
            GdalNative.GDALTranslateOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALTranslate(destination, source.Handle, options, &usage);
            return Result(h, usage, "gdal_translate", bridge, cancellationToken);
        }
        finally
        {
            GdalNative.GDALTranslateOptionsFree(options);
        }
    }

    public static Dataset Translate(Dataset source, string destination, TranslateOptions options, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => Translate(source, destination, options.ToArgs(), progress, cancellationToken);

    // ------------------------------------------------------------------ gdalwarp

    /// <summary>gdalwarp: reprojection, resampling, mosaicking, clipping by cutline, orthorectification (RPC/GCP/TPS).</summary>
    public static Dataset Warp(IReadOnlyList<Dataset> sources, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdalwarp", args, GdalNative.GDALWarpAppOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var raw = stackalloc nint[Math.Max(1, sources.Count)];
        var refs = AddRefs(sources.Select(s => s.Handle).ToArray(), raw);
        try
        {
            GdalNative.GDALWarpAppOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALWarp(destination, 0, sources.Count, raw, options, &usage);
            return Result(h, usage, "gdalwarp", bridge, cancellationToken);
        }
        finally
        {
            ReleaseRefs(refs);
            GdalNative.GDALWarpAppOptionsFree(options);
        }
    }

    public static Dataset Warp(Dataset source, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => Warp([source], destination, args, progress, cancellationToken);

    public static Dataset Warp(Dataset source, string destination, WarpOptions options, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => Warp([source], destination, options.ToArgs(), progress, cancellationToken);

    public static Dataset Warp(IReadOnlyList<Dataset> sources, string destination, WarpOptions options, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => Warp(sources, destination, options.ToArgs(), progress, cancellationToken);

    /// <summary>Warps sources into an existing dataset (e.g. mosaicking into a prepared canvas).</summary>
    public static void WarpInto(IReadOnlyList<Dataset> sources, Dataset destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdalwarp", args, GdalNative.GDALWarpAppOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var raw = stackalloc nint[Math.Max(1, sources.Count)];
        var all = sources.Select(s => s.Handle).Append(destination.Handle).ToArray();
        var refs = AddRefs(all, null);
        try
        {
            for (var i = 0; i < sources.Count; i++) raw[i] = all[i].DangerousGetHandle();
            GdalNative.GDALWarpAppOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALWarp(null, destination.Handle.DangerousGetHandle(), sources.Count, raw, options, &usage);
            ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
            if (h == 0) throw GdalNative.LastError("gdalwarp failed");
        }
        finally
        {
            ReleaseRefs(refs);
            GdalNative.GDALWarpAppOptionsFree(options);
        }
    }

    // ------------------------------------------------------------------ ogr2ogr

    /// <summary>ogr2ogr: vector format conversion, reprojection, filtering, SQL, clipping, simplification.</summary>
    public static Dataset VectorTranslate(IReadOnlyList<Dataset> sources, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("ogr2ogr", args, GdalNative.GDALVectorTranslateOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var raw = stackalloc nint[Math.Max(1, sources.Count)];
        var refs = AddRefs(sources.Select(s => s.Handle).ToArray(), raw);
        try
        {
            GdalNative.GDALVectorTranslateOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALVectorTranslate(destination, 0, sources.Count, raw, options, &usage);
            return Result(h, usage, "ogr2ogr", bridge, cancellationToken);
        }
        finally
        {
            ReleaseRefs(refs);
            GdalNative.GDALVectorTranslateOptionsFree(options);
        }
    }

    public static Dataset VectorTranslate(Dataset source, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => VectorTranslate([source], destination, args, progress, cancellationToken);

    public static Dataset VectorTranslate(Dataset source, string destination, VectorTranslateOptions options, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => VectorTranslate([source], destination, options.ToArgs(), progress, cancellationToken);

    // ------------------------------------------------------------------ gdalbuildvrt

    /// <summary>gdalbuildvrt from open datasets: virtual mosaics / band stacks without copying pixels.</summary>
    public static Dataset BuildVrt(string destination, IReadOnlyList<Dataset> sources, IEnumerable<string>? args = null, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdalbuildvrt", args, GdalNative.GDALBuildVRTOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var raw = stackalloc nint[Math.Max(1, sources.Count)];
        var refs = AddRefs(sources.Select(s => s.Handle).ToArray(), raw);
        try
        {
            GdalNative.GDALBuildVRTOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALBuildVRT(destination, sources.Count, raw, 0, options, &usage);
            return Result(h, usage, "gdalbuildvrt", bridge, cancellationToken);
        }
        finally
        {
            ReleaseRefs(refs);
            GdalNative.GDALBuildVRTOptionsFree(options);
        }
    }

    /// <summary>gdalbuildvrt from file names (sources are opened lazily by the VRT).</summary>
    public static Dataset BuildVrt(string destination, IEnumerable<string> sourceFiles, IEnumerable<string>? args = null, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdalbuildvrt", args, GdalNative.GDALBuildVRTOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        using var names = new NativeStringList(sourceFiles);
        try
        {
            GdalNative.GDALBuildVRTOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALBuildVRT(destination, GdalNative.CSLCount(names.Pointer), null, names.Pointer, options, &usage);
            return Result(h, usage, "gdalbuildvrt", bridge, cancellationToken);
        }
        finally
        {
            GdalNative.GDALBuildVRTOptionsFree(options);
        }
    }

    public static Dataset BuildVrt(string destination, IReadOnlyList<Dataset> sources, BuildVrtOptions options)
        => BuildVrt(destination, sources, options.ToArgs());

    // ------------------------------------------------------------------ gdaldem

    /// <summary>gdaldem: hillshade, slope, aspect, color-relief, TRI, TPI, roughness.</summary>
    public static Dataset DemProcessing(Dataset source, string destination, DemProcessingMode mode, IEnumerable<string>? args = null, string? colorFile = null,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var processing = mode switch
        {
            DemProcessingMode.Hillshade => "hillshade",
            DemProcessingMode.Slope => "slope",
            DemProcessingMode.Aspect => "aspect",
            DemProcessingMode.ColorRelief => "color-relief",
            DemProcessingMode.TerrainRuggednessIndex => "TRI",
            DemProcessingMode.TopographicPositionIndex => "TPI",
            DemProcessingMode.Roughness => "roughness",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (mode == DemProcessingMode.ColorRelief && colorFile is null)
        {
            throw new ArgumentException("Color relief requires a color file (see ColorRamp.ToGdalColorFile).", nameof(colorFile));
        }
        var options = CreateOptions("gdaldem", args, GdalNative.GDALDEMProcessingOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        try
        {
            GdalNative.GDALDEMProcessingOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALDEMProcessing(destination, source.Handle, processing, colorFile, options, &usage);
            return Result(h, usage, "gdaldem " + processing, bridge, cancellationToken);
        }
        finally
        {
            GdalNative.GDALDEMProcessingOptionsFree(options);
        }
    }

    public static Dataset DemProcessing(Dataset source, string destination, DemProcessingMode mode, DemOptions options, string? colorFile = null,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => DemProcessing(source, destination, mode, options.ToArgs(mode), colorFile, progress, cancellationToken);

    // ------------------------------------------------------------------ gdal_rasterize

    /// <summary>gdal_rasterize: burns vector geometries into a new raster.</summary>
    public static Dataset Rasterize(Dataset vectorSource, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdal_rasterize", args, GdalNative.GDALRasterizeOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        try
        {
            GdalNative.GDALRasterizeOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALRasterize(destination, 0, vectorSource.Handle, options, &usage);
            return Result(h, usage, "gdal_rasterize", bridge, cancellationToken);
        }
        finally
        {
            GdalNative.GDALRasterizeOptionsFree(options);
        }
    }

    public static Dataset Rasterize(Dataset vectorSource, string destination, RasterizeOptions options, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => Rasterize(vectorSource, destination, options.ToArgs(), progress, cancellationToken);

    /// <summary>gdal_rasterize into an existing raster.</summary>
    public static void RasterizeInto(Dataset vectorSource, Dataset target, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdal_rasterize", args, GdalNative.GDALRasterizeOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var refs = AddRefs([target.Handle], null);
        try
        {
            GdalNative.GDALRasterizeOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALRasterize(null, target.Handle.DangerousGetHandle(), vectorSource.Handle, options, &usage);
            ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
            if (h == 0) throw GdalNative.LastError("gdal_rasterize failed");
        }
        finally
        {
            ReleaseRefs(refs);
            GdalNative.GDALRasterizeOptionsFree(options);
        }
    }

    // ------------------------------------------------------------------ gdal_grid

    /// <summary>gdal_grid: interpolates scattered points into a raster (invdist, average, nearest, linear, ...).</summary>
    public static Dataset Grid(Dataset pointsSource, string destination, IEnumerable<string> args, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions("gdal_grid", args, GdalNative.GDALGridOptionsNew);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        try
        {
            GdalNative.GDALGridOptionsSetProgress(options, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            int usage;
            var h = GdalNative.GDALGrid(destination, pointsSource.Handle, options, &usage);
            return Result(h, usage, "gdal_grid", bridge, cancellationToken);
        }
        finally
        {
            GdalNative.GDALGridOptionsFree(options);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>AddRefs a set of handles for the duration of a call taking raw handle arrays.</summary>
    private static GdalSafeHandle[] AddRefs(GdalSafeHandle[] handles, nint* raw)
    {
        var added = 0;
        try
        {
            for (; added < handles.Length; added++)
            {
                var ok = false;
                handles[added].DangerousAddRef(ref ok);
                if (raw != null) raw[added] = handles[added].DangerousGetHandle();
            }
        }
        catch
        {
            for (var i = 0; i < added; i++) handles[i].DangerousRelease();
            throw;
        }
        return handles;
    }

    private static void ReleaseRefs(GdalSafeHandle[] handles)
    {
        foreach (var h in handles) h.DangerousRelease();
    }
}
