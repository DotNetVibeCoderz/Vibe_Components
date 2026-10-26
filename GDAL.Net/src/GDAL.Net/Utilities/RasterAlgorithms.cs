using System.Globalization;
using GDAL.Net.Native;
using GDAL.Net.Vector;

namespace GDAL.Net;

/// <summary>Options for <c>RasterAlgorithms.GenerateContours</c>.</summary>
public sealed record ContourOptions
{
    /// <summary>Elevation interval between contour lines (ignored when <see cref="FixedLevels"/> is set).</summary>
    public double Interval { get; init; } = 10;
    public double Base { get; init; }
    public IReadOnlyList<double>? FixedLevels { get; init; }
    /// <summary>Generate polygons (elevation bands) instead of lines.</summary>
    public bool Polygonize { get; init; }
    /// <summary>Field receiving the elevation (lines) — must exist in the target layer.</summary>
    public string? ElevationField { get; init; } = "elev";
    /// <summary>Fields receiving min/max elevation of polygons.</summary>
    public string? MinField { get; init; }
    public string? MaxField { get; init; }
    public double? NoData { get; init; }
}

/// <summary>Raster analysis algorithms from gdal_alg.h: contours, polygonize, viewshed, nodata filling, sieve.</summary>
public static unsafe class RasterAlgorithms
{
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Generates contour lines (or polygons) from a DEM band into <paramref name="target"/>.</summary>
    public static void GenerateContours(RasterBand band, Layer target, ContourOptions? options = null, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new ContourOptions();
        using var opts = new NativeStringList(null);
        if (options.FixedLevels is { Count: > 0 } levels)
        {
            opts.Add("FIXED_LEVELS=" + string.Join(',', levels.Select(F)));
        }
        else
        {
            opts.Add("LEVEL_INTERVAL=" + F(options.Interval));
            opts.Add("LEVEL_BASE=" + F(options.Base));
        }
        if (options.Polygonize) opts.Add("POLYGONIZE=YES");
        if (options.NoData is { } nd) opts.Add("NODATA=" + F(nd));

        using var defn = target.Definition;
        if (!options.Polygonize && options.ElevationField is { } ef)
        {
            var idx = defn.GetFieldIndex(ef);
            if (idx >= 0) opts.Add("ELEV_FIELD=" + idx.ToString(CultureInfo.InvariantCulture));
        }
        if (options.Polygonize)
        {
            if (options.MinField is { } mn && defn.GetFieldIndex(mn) is var i1 and >= 0) opts.Add("ELEV_FIELD_MIN=" + i1.ToString(CultureInfo.InvariantCulture));
            if (options.MaxField is { } mx && defn.GetFieldIndex(mx) is var i2 and >= 0) opts.Add("ELEV_FIELD_MAX=" + i2.ToString(CultureInfo.InvariantCulture));
        }

        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var err = GdalNative.GDALContourGenerateEx(band.Handle, target.Handle, opts.Pointer, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        GdalNative.Check(err, "ContourGenerate");
    }

    /// <summary>
    /// Convenience: contours into a new in-memory layer named "contours" with an "elev" (lines) or "elev_min"/"elev_max" (polygons) field.
    /// The returned dataset owns the layer.
    /// </summary>
    public static Dataset GenerateContours(RasterBand band, double interval, bool polygons = false, double baseLevel = 0)
    {
        var ds = Gdal.CreateMemoryVector("contours");
        var layer = ds.CreateLayer("contours", band.Dataset.SpatialReference, polygons ? GeometryType.Polygon : GeometryType.LineString);
        if (polygons)
        {
            layer.CreateField("elev_min", FieldType.Real);
            layer.CreateField("elev_max", FieldType.Real);
        }
        else
        {
            layer.CreateField("elev", FieldType.Real);
        }
        GenerateContours(band, layer, new ContourOptions
        {
            Interval = interval,
            Base = baseLevel,
            Polygonize = polygons,
            ElevationField = polygons ? null : "elev",
            MinField = polygons ? "elev_min" : null,
            MaxField = polygons ? "elev_max" : null,
            NoData = band.NoDataValue,
        });
        return ds;
    }

    /// <summary>Converts connected regions of equal pixel value into polygons (raster → vector).</summary>
    public static void Polygonize(RasterBand band, Layer target, int valueFieldIndex = 0, RasterBand? mask = null, bool eightConnected = false, bool floatingPoint = false,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var opts = new NativeStringList(eightConnected ? ["8CONNECTED=8"] : null);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var maskHandle = mask is null ? 0 : mask.Handle.DangerousGetHandle();
        var err = floatingPoint
            ? GdalNative.GDALFPolygonize(band.Handle, maskHandle, target.Handle, valueFieldIndex, opts.Pointer, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge))
            : GdalNative.GDALPolygonize(band.Handle, maskHandle, target.Handle, valueFieldIndex, opts.Pointer, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        GC.KeepAlive(mask);
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        GdalNative.Check(err, "Polygonize");
    }

    /// <summary>Convenience: polygonizes into a new in-memory dataset with a "value" field; nodata pixels are skipped.</summary>
    public static Dataset Polygonize(RasterBand band, bool eightConnected = false)
    {
        var ds = Gdal.CreateMemoryVector("polygons");
        var layer = ds.CreateLayer("polygons", band.Dataset.SpatialReference, GeometryType.Polygon);
        var fp = !DataTypes.IsInteger(band.DataType);
        layer.CreateField("value", fp ? FieldType.Real : FieldType.Integer);
        var mask = band.NoDataValue is null ? null : band.MaskBand;
        Polygonize(band, layer, 0, mask, eightConnected, fp);
        return ds;
    }

    /// <summary>
    /// Viewshed analysis: which cells of a DEM are visible from an observer. Output is a Byte raster
    /// (visible=255, invisible=0, out of range=0 by default).
    /// </summary>
    public static Dataset Viewshed(RasterBand dem, double observerX, double observerY, double observerHeight = 2, double targetHeight = 0,
        double maxDistance = 0, string driver = "MEM", string destination = "", double visibleValue = 255, double invisibleValue = 0,
        double outOfRangeValue = 0, double noDataValue = -1, double curvatureCoefficient = 0.85714, ViewshedMode mode = ViewshedMode.Edge,
        IDictionary<string, string>? creationOptions = null, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var co = NativeStringList.FromOptions(creationOptions);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var h = GdalNative.GDALViewshedGenerate(dem.Handle, driver, destination, co.Pointer, observerX, observerY, observerHeight, targetHeight,
            visibleValue, invisibleValue, outOfRangeValue, noDataValue, curvatureCoefficient, (int)mode, maxDistance,
            ProgressBridge.Function(bridge), ProgressBridge.Data(bridge), 1, 0);
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        return h == 0 ? throw GdalNative.LastError("Viewshed") : new Dataset(h);
    }

    /// <summary>Fills nodata holes by inverse distance interpolation from the edges.</summary>
    public static void FillNoData(RasterBand band, double maxSearchDistance = 100, int smoothingIterations = 0, RasterBand? mask = null,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var err = GdalNative.GDALFillNodata(band.Handle, mask is null ? 0 : mask.Handle.DangerousGetHandle(), maxSearchDistance, 0, smoothingIterations, 0,
            ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        GC.KeepAlive(mask);
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        GdalNative.Check(err, "FillNodata");
    }

    /// <summary>Removes raster polygons smaller than <paramref name="threshold"/> pixels (classification cleanup).</summary>
    public static void Sieve(RasterBand source, RasterBand destination, int threshold, bool eightConnected = false,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var err = GdalNative.GDALSieveFilter(source.Handle, 0, destination.Handle, threshold, eightConnected ? 8 : 4, 0,
            ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        GdalNative.Check(err, "SieveFilter");
    }
}
