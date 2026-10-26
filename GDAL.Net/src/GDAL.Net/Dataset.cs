using System.Runtime.InteropServices;
using GDAL.Net.MultiDimensional;
using GDAL.Net.Native;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

namespace GDAL.Net;

/// <summary>
/// A GDAL dataset: a raster (bands), a vector source (layers), a multidimensional container, or a mix.
/// Dispose it to close the file and flush pending writes. Not thread-safe: use one dataset per thread.
/// </summary>
public sealed unsafe class Dataset : MajorObject
{
    internal Dataset(nint handle) : base(new GdalSafeHandle(handle, &Release)) { }

    private static void Release(nint h) => GdalNative.GDALClose(h);

    /// <summary>Closes the dataset, reporting errors raised while flushing (Dispose swallows them).</summary>
    public void Close()
    {
        if (RawHandle.IsClosed) return;
        var h = RawHandle.Detach();
        if (h != 0)
        {
            GdalNative.Check(GdalNative.GDALClose(h), "Close dataset");
        }
    }

    /// <summary>The driver that opened/created this dataset.</summary>
    public Driver Driver => new(GdalNative.GDALGetDatasetDriver(Handle));

    /// <summary>Files composing the dataset (main file + side-cars).</summary>
    public IReadOnlyList<string> FileList => Interop.TakeStringList(GdalNative.GDALGetFileList(Handle));

    /// <summary>Flushes cached writes to disk.</summary>
    public void FlushCache() => GdalNative.Check(GdalNative.GDALFlushCache(Handle), "FlushCache");

    // ================================================================== Raster

    public int RasterWidth => GdalNative.GDALGetRasterXSize(Handle);
    public int RasterHeight => GdalNative.GDALGetRasterYSize(Handle);
    public int RasterCount => GdalNative.GDALGetRasterCount(Handle);
    public bool IsRaster => RasterCount > 0;

    /// <summary>Gets a band by 1-based index (GDAL convention).</summary>
    public RasterBand GetBand(int index)
    {
        var h = GdalNative.GDALGetRasterBand(Handle, index);
        return h == 0 ? throw GdalNative.LastError($"Band {index} does not exist") : new RasterBand(GdalSafeHandle.Borrowed(h, Handle), this);
    }

    /// <summary>All raster bands (index 1..RasterCount).</summary>
    public IReadOnlyList<RasterBand> Bands => Enumerable.Range(1, RasterCount).Select(GetBand).ToArray();

    /// <summary>Adds a band (formats that support it, e.g. MEM, VRT).</summary>
    public RasterBand AddBand(DataType dataType, IDictionary<string, string>? options = null)
    {
        using var opts = NativeStringList.FromOptions(options);
        GdalNative.Check(GdalNative.GDALAddBand(Handle, (int)dataType, opts.Pointer), "AddBand");
        return GetBand(RasterCount);
    }

    /// <summary>True when the dataset has an explicit affine georeferencing.</summary>
    public bool HasGeoTransform
    {
        get
        {
            var gt = stackalloc double[6];
            return GdalNative.GDALGetGeoTransform(Handle, gt) == 0;
        }
    }

    /// <summary>Affine georeferencing (identity when the dataset has none).</summary>
    public GeoTransform GeoTransform
    {
        get
        {
            var gt = stackalloc double[6];
            GdalNative.GDALGetGeoTransform(Handle, gt);
            GdalNative.CPLErrorReset();
            return GeoTransform.FromArray(new ReadOnlySpan<double>(gt, 6));
        }
        set
        {
            var gt = stackalloc double[6] { value.OriginX, value.PixelWidth, value.RotationX, value.OriginY, value.RotationY, value.PixelHeight };
            GdalNative.Check(GdalNative.GDALSetGeoTransform(Handle, gt), "SetGeoTransform");
        }
    }

    /// <summary>Georeferenced extent of the raster.</summary>
    public Envelope Extent => GeoTransform.GetExtent(RasterWidth, RasterHeight);

    /// <summary>Coordinate reference system (a managed copy), or null when unknown.</summary>
    public SpatialReference? SpatialReference
    {
        get
        {
            var h = GdalNative.GDALGetSpatialRef(Handle);
            return h == 0 ? null : SpatialReference.CloneFrom(h);
        }
        set
        {
            var err = value is null ? GdalNative.GDALSetSpatialRef(Handle, (nint)0) : GdalNative.GDALSetSpatialRef(Handle, value.Handle);
            GdalNative.Check(err, "SetSpatialRef");
        }
    }

    /// <summary>CRS as WKT ("" when unknown).</summary>
    public string Projection
    {
        get => Interop.PtrToString(GdalNative.GDALGetProjectionRef(Handle)) ?? string.Empty;
        set => GdalNative.Check(GdalNative.GDALSetProjection(Handle, value), "SetProjection");
    }

    /// <summary>Ground control points (for un-georeferenced imagery).</summary>
    public IReadOnlyList<GroundControlPoint> Gcps
    {
        get
        {
            var count = GdalNative.GDALGetGCPCount(Handle);
            var gcps = GdalNative.GDALGetGCPs(Handle);
            var list = new GroundControlPoint[count];
            for (var i = 0; i < count; i++)
            {
                var g = gcps[i];
                list[i] = new GroundControlPoint(g.dfGCPPixel, g.dfGCPLine, g.dfGCPX, g.dfGCPY, g.dfGCPZ,
                    Interop.PtrToString(g.pszId), Interop.PtrToString(g.pszInfo));
            }
            return list;
        }
    }

    /// <summary>CRS of the ground control points.</summary>
    public SpatialReference? GcpSpatialReference
    {
        get
        {
            var h = GdalNative.GDALGetGCPSpatialRef(Handle);
            return h == 0 ? null : SpatialReference.CloneFrom(h);
        }
    }

    /// <summary>Assigns ground control points (georeferencing by GCPs; use <c>GdalUtils.Warp</c> with -tps/-order to rectify).</summary>
    public void SetGcps(IReadOnlyList<GroundControlPoint> gcps, SpatialReference srs)
    {
        var native = new GDAL_GCP[gcps.Count];
        var strings = new List<nint>();
        try
        {
            for (var i = 0; i < gcps.Count; i++)
            {
                var g = gcps[i];
                var id = Marshal.StringToCoTaskMemUTF8(g.Id ?? (i + 1).ToString());
                var info = Marshal.StringToCoTaskMemUTF8(g.Info ?? string.Empty);
                strings.Add(id);
                strings.Add(info);
                native[i] = new GDAL_GCP { pszId = id, pszInfo = info, dfGCPPixel = g.Pixel, dfGCPLine = g.Line, dfGCPX = g.X, dfGCPY = g.Y, dfGCPZ = g.Z };
            }
            fixed (GDAL_GCP* p = native)
            {
                GdalNative.Check(GdalNative.GDALSetGCPs2(Handle, native.Length, p, srs.Handle.DangerousGetHandle()), "SetGCPs");
            }
            GC.KeepAlive(srs);
        }
        finally
        {
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }
    }

    /// <summary>Builds overviews (pyramids) for all bands, e.g. levels 2,4,8,16.</summary>
    public void BuildOverviews(string resampling = "AVERAGE", int[]? levels = null, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        levels ??= DefaultOverviewLevels(RasterWidth, RasterHeight);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        fixed (int* pl = levels)
        {
            var err = GdalNative.GDALBuildOverviews(Handle, resampling, levels.Length, pl, 0, null, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
            GdalNative.Check(err, "BuildOverviews");
        }
    }

    /// <summary>Power-of-two overview levels down to ~256 pixels.</summary>
    public static int[] DefaultOverviewLevels(int width, int height)
    {
        var list = new List<int>();
        for (var f = 2; Math.Max(width, height) / f >= 256; f *= 2) list.Add(f);
        if (list.Count == 0) list.Add(2);
        return [.. list];
    }

    /// <summary>
    /// Reads several bands at once into a pixel-interleaved buffer (RGBRGB...) of <paramref name="bufferWidth"/> x <paramref name="bufferHeight"/>,
    /// resampling on the fly when the buffer size differs from the window.
    /// </summary>
    public void ReadInterleaved<T>(int xOff, int yOff, int xSize, int ySize, Span<T> buffer, int bufferWidth, int bufferHeight, ReadOnlySpan<int> bandMap,
        ResampleAlgorithm resampling = ResampleAlgorithm.NearestNeighbour) where T : unmanaged
    {
        var bandCount = bandMap.Length;
        if (buffer.Length < (long)bufferWidth * bufferHeight * bandCount) throw new ArgumentException("Buffer is too small.", nameof(buffer));
        var size = sizeof(T);
        var extra = RasterBand.MakeExtraArg(resampling);
        fixed (T* p = buffer)
        fixed (int* bands = bandMap)
        {
            var err = GdalNative.GDALDatasetRasterIOEx(Handle, 0, xOff, yOff, xSize, ySize, p, bufferWidth, bufferHeight, (int)DataTypes.Of<T>(), bandCount, bands,
                (long)size * bandCount, (long)size * bandCount * bufferWidth, size, &extra);
            GdalNative.Check(err, "Dataset RasterIO (read)");
        }
    }

    // ================================================================== Vector

    public int LayerCount => GdalNative.GDALDatasetGetLayerCount(Handle);

    public Layer GetLayer(int index)
    {
        var h = GdalNative.GDALDatasetGetLayer(Handle, index);
        return h == 0 ? throw new ArgumentOutOfRangeException(nameof(index), $"Layer {index} does not exist.") : new Layer(GdalSafeHandle.Borrowed(h, Handle), this);
    }

    public Layer GetLayer(string name)
    {
        var h = GdalNative.GDALDatasetGetLayerByName(Handle, name);
        return h == 0 ? throw new KeyNotFoundException($"Layer '{name}' does not exist.") : new Layer(GdalSafeHandle.Borrowed(h, Handle), this);
    }

    public Layer? TryGetLayer(string name)
    {
        var h = GdalNative.GDALDatasetGetLayerByName(Handle, name);
        return h == 0 ? null : new Layer(GdalSafeHandle.Borrowed(h, Handle), this);
    }

    /// <summary>All vector layers.</summary>
    public IReadOnlyList<Layer> Layers => Enumerable.Range(0, LayerCount).Select(GetLayer).ToArray();

    /// <summary>Creates a new layer.</summary>
    public Layer CreateLayer(string name, SpatialReference? srs = null, GeometryType geometryType = GeometryType.Unknown, IDictionary<string, string>? options = null)
    {
        using var opts = NativeStringList.FromOptions(options);
        var h = GdalNative.GDALDatasetCreateLayer(Handle, name, srs is null ? 0 : srs.Handle.DangerousGetHandle(), (uint)geometryType, opts.Pointer);
        GC.KeepAlive(srs);
        return h == 0 ? throw GdalNative.LastError($"CreateLayer '{name}'") : new Layer(GdalSafeHandle.Borrowed(h, Handle), this);
    }

    /// <summary>Copies a layer (schema + features) from any dataset into this one.</summary>
    public Layer CopyLayer(Layer source, string newName, IDictionary<string, string>? options = null)
    {
        using var opts = NativeStringList.FromOptions(options);
        var h = GdalNative.GDALDatasetCopyLayer(Handle, source.Handle, newName, opts.Pointer);
        return h == 0 ? throw GdalNative.LastError($"CopyLayer '{newName}'") : new Layer(GdalSafeHandle.Borrowed(h, Handle), this);
    }

    public void DeleteLayer(int index) => GdalNative.CheckOgr(GdalNative.GDALDatasetDeleteLayer(Handle, index), "DeleteLayer");

    /// <summary>Tests a dataset capability (ODsC*: "CreateLayer", "DeleteLayer", "Transactions", "CurveGeometries", ...).</summary>
    public bool TestCapability(string capability) => GdalNative.GDALDatasetTestCapability(Handle, capability) != 0;

    /// <summary>
    /// Runs an SQL statement (OGR SQL by default, or dialect "SQLITE"/"INDIRECT_SQLITE", or the native SQL of a database driver).
    /// Returns the result layer (dispose it to release the result set) or null for statements without results.
    /// </summary>
    public SqlResultLayer? ExecuteSql(string sql, Geometry? spatialFilter = null, string? dialect = null)
    {
        var h = GdalNative.GDALDatasetExecuteSQL(Handle, sql, spatialFilter is null ? 0 : spatialFilter.Handle.DangerousGetHandle(), dialect);
        GC.KeepAlive(spatialFilter);
        if (h == 0)
        {
            var msg = GdalNative.LastErrorMessage();
            if (!string.IsNullOrEmpty(msg) && GdalNative.CPLGetLastErrorType() >= 3)
            {
                throw GdalNative.LastError("ExecuteSQL");
            }
            return null;
        }
        return new SqlResultLayer(h, this);
    }

    /// <summary>Starts a dataset level transaction (GPKG, PostGIS, SQLite, ...). Dispose without commit rolls back.</summary>
    public Transaction BeginTransaction(bool force = false)
    {
        GdalNative.CheckOgr(GdalNative.GDALDatasetStartTransaction(Handle, force ? 1 : 0), "StartTransaction");
        return new Transaction(this);
    }

    internal void CommitTransaction() => GdalNative.CheckOgr(GdalNative.GDALDatasetCommitTransaction(Handle), "CommitTransaction");
    internal void RollbackTransaction() => GdalNative.CheckOgr(GdalNative.GDALDatasetRollbackTransaction(Handle), "RollbackTransaction");

    // ================================================================== Multidimensional

    /// <summary>Root group of a dataset opened with <see cref="OpenFlags.MultidimensionalRaster"/>.</summary>
    public MdGroup? RootGroup
    {
        get
        {
            var h = GdalNative.GDALDatasetGetRootGroup(Handle);
            return h == 0 ? null : new MdGroup(h, this);
        }
    }

    public override string ToString() => $"Dataset '{Description}' ({Driver.ShortName}) {RasterWidth}x{RasterHeight}x{RasterCount}, {LayerCount} layer(s)";
}

/// <summary>A dataset transaction. Call <see cref="Commit"/>; disposing an uncommitted transaction rolls it back.</summary>
public sealed class Transaction : IDisposable
{
    private Dataset? _dataset;

    internal Transaction(Dataset dataset) => _dataset = dataset;

    public void Commit()
    {
        var ds = Interlocked.Exchange(ref _dataset, null) ?? throw new InvalidOperationException("Transaction already completed.");
        ds.CommitTransaction();
    }

    public void Rollback()
    {
        var ds = Interlocked.Exchange(ref _dataset, null) ?? throw new InvalidOperationException("Transaction already completed.");
        ds.RollbackTransaction();
    }

    public void Dispose()
    {
        var ds = Interlocked.Exchange(ref _dataset, null);
        if (ds is not null && !ds.IsDisposed) ds.RollbackTransaction();
    }
}
