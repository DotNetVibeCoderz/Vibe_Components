using System.Buffers;
using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>A raster band. Owned by its <see cref="Dataset"/>; valid while the dataset is open.</summary>
public sealed unsafe class RasterBand : MajorObject
{
    internal RasterBand(GdalSafeHandle handle, Dataset dataset) : base(handle)
    {
        Dataset = dataset;
    }

    /// <summary>The dataset owning this band.</summary>
    public Dataset Dataset { get; }

    /// <summary>1-based band number (0 for overviews/masks).</summary>
    public int Number => GdalNative.GDALGetBandNumber(Handle);
    public int Width => GdalNative.GDALGetRasterBandXSize(Handle);
    public int Height => GdalNative.GDALGetRasterBandYSize(Handle);
    public DataType DataType => (DataType)GdalNative.GDALGetRasterDataType(Handle);

    /// <summary>Natural block size (tile or strip) — the most efficient read unit.</summary>
    public (int Width, int Height) BlockSize
    {
        get
        {
            int x, y;
            GdalNative.GDALGetBlockSize(Handle, &x, &y);
            return (x, y);
        }
    }

    public double? NoDataValue
    {
        get
        {
            int ok;
            var v = GdalNative.GDALGetRasterNoDataValue(Handle, &ok);
            return ok != 0 ? v : null;
        }
        set
        {
            var err = value is { } v ? GdalNative.GDALSetRasterNoDataValue(Handle, v) : GdalNative.GDALDeleteRasterNoDataValue(Handle);
            GdalNative.Check(err, "Set NoData");
        }
    }

    public ColorInterpretation ColorInterpretation
    {
        get => (ColorInterpretation)GdalNative.GDALGetRasterColorInterpretation(Handle);
        set => GdalNative.Check(GdalNative.GDALSetRasterColorInterpretation(Handle, (int)value), "SetColorInterpretation");
    }

    public double Offset
    {
        get { int ok; var v = GdalNative.GDALGetRasterOffset(Handle, &ok); return ok != 0 ? v : 0; }
        set => GdalNative.Check(GdalNative.GDALSetRasterOffset(Handle, value), "SetOffset");
    }

    public double Scale
    {
        get { int ok; var v = GdalNative.GDALGetRasterScale(Handle, &ok); return ok != 0 ? v : 1; }
        set => GdalNative.Check(GdalNative.GDALSetRasterScale(Handle, value), "SetScale");
    }

    public string UnitType
    {
        get => Interop.PtrToString(GdalNative.GDALGetRasterUnitType(Handle)) ?? string.Empty;
        set => GdalNative.Check(GdalNative.GDALSetRasterUnitType(Handle, value), "SetUnitType");
    }

    public MaskFlags MaskFlags => (MaskFlags)GdalNative.GDALGetMaskFlags(Handle);

    /// <summary>The mask band (validity mask derived from nodata/alpha/per-dataset mask).</summary>
    public RasterBand MaskBand => new(GdalSafeHandle.Borrowed(GdalNative.GDALGetMaskBand(Handle), Handle), Dataset);

    // ------------------------------------------------------------------ overviews

    public int OverviewCount => GdalNative.GDALGetOverviewCount(Handle);

    public RasterBand GetOverview(int index)
    {
        var h = GdalNative.GDALGetOverview(Handle, index);
        return h == 0 ? throw new ArgumentOutOfRangeException(nameof(index)) : new RasterBand(GdalSafeHandle.Borrowed(h, Handle), Dataset);
    }

    /// <summary>Picks the coarsest overview that still has at least the requested resolution (or this band).</summary>
    public RasterBand GetBestOverview(int targetWidth, int targetHeight)
    {
        var best = this;
        var count = OverviewCount;
        for (var i = 0; i < count; i++)
        {
            var ov = GetOverview(i);
            if (ov.Width >= targetWidth && ov.Height >= targetHeight && ov.Width < best.Width)
            {
                best = ov;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ I/O

    internal static GDALRasterIOExtraArg MakeExtraArg(ResampleAlgorithm resampling) => new()
    {
        nVersion = 1,
        eResampleAlg = (int)resampling,
    };

    /// <summary>
    /// Reads a window into <paramref name="buffer"/> (row-major, <paramref name="bufferWidth"/> x <paramref name="bufferHeight"/>),
    /// converting to <typeparamref name="T"/> and resampling when the buffer size differs from the window size. Zero-copy into your span.
    /// </summary>
    public void Read<T>(int xOff, int yOff, int xSize, int ySize, Span<T> buffer, int bufferWidth, int bufferHeight,
        ResampleAlgorithm resampling = ResampleAlgorithm.NearestNeighbour) where T : unmanaged
    {
        if (buffer.Length < (long)bufferWidth * bufferHeight) throw new ArgumentException("Buffer is too small for the requested size.", nameof(buffer));
        var extra = MakeExtraArg(resampling);
        fixed (T* p = buffer)
        {
            var err = GdalNative.GDALRasterIOEx(Handle, 0, xOff, yOff, xSize, ySize, p, bufferWidth, bufferHeight, (int)DataTypes.Of<T>(), 0, 0, &extra);
            GdalNative.Check(err, "RasterIO (read)");
        }
    }

    /// <summary>Reads a window at full resolution.</summary>
    public void Read<T>(int xOff, int yOff, int xSize, int ySize, Span<T> buffer) where T : unmanaged
        => Read(xOff, yOff, xSize, ySize, buffer, xSize, ySize);

    /// <summary>Reads the whole band into a new array.</summary>
    public T[] ReadAll<T>() where T : unmanaged
    {
        var w = Width;
        var h = Height;
        var data = GC.AllocateUninitializedArray<T>(checked(w * h));
        Read<T>(0, 0, w, h, data);
        return data;
    }

    /// <summary>Reads the whole band resampled to the given size (fast previews; uses overviews automatically).</summary>
    public T[] ReadResampled<T>(int bufferWidth, int bufferHeight, ResampleAlgorithm resampling = ResampleAlgorithm.Average) where T : unmanaged
    {
        var data = GC.AllocateUninitializedArray<T>(checked(bufferWidth * bufferHeight));
        Read<T>(0, 0, Width, Height, data, bufferWidth, bufferHeight, resampling);
        return data;
    }

    /// <summary>Writes a window from <paramref name="buffer"/> (row-major <paramref name="bufferWidth"/> x <paramref name="bufferHeight"/>).</summary>
    public void Write<T>(int xOff, int yOff, int xSize, int ySize, ReadOnlySpan<T> buffer, int bufferWidth, int bufferHeight) where T : unmanaged
    {
        if (buffer.Length < (long)bufferWidth * bufferHeight) throw new ArgumentException("Buffer is too small for the requested size.", nameof(buffer));
        var extra = MakeExtraArg(ResampleAlgorithm.NearestNeighbour);
        fixed (T* p = buffer)
        {
            var err = GdalNative.GDALRasterIOEx(Handle, 1, xOff, yOff, xSize, ySize, p, bufferWidth, bufferHeight, (int)DataTypes.Of<T>(), 0, 0, &extra);
            GdalNative.Check(err, "RasterIO (write)");
        }
    }

    /// <summary>Writes a window at full resolution.</summary>
    public void Write<T>(int xOff, int yOff, int xSize, int ySize, ReadOnlySpan<T> buffer) where T : unmanaged
        => Write(xOff, yOff, xSize, ySize, buffer, xSize, ySize);

    /// <summary>Writes the whole band.</summary>
    public void WriteAll<T>(ReadOnlySpan<T> data) where T : unmanaged => Write(0, 0, Width, Height, data);

    /// <summary>
    /// Processes the band block-row by block-row with a pooled buffer (constant memory for arbitrarily large rasters).
    /// The callback receives (yOffset, rows, data) where data holds <c>rows * Width</c> values.
    /// </summary>
    public void ProcessRows<T>(Action<int, int, Span<T>> process, int? rowsPerChunk = null) where T : unmanaged
    {
        var w = Width;
        var h = Height;
        var rows = rowsPerChunk ?? Math.Max(BlockSize.Height, Math.Max(1, (4 << 20) / Math.Max(1, w * sizeof(T))));
        rows = Math.Clamp(rows, 1, h);
        var pool = ArrayPool<T>.Shared.Rent(w * rows);
        try
        {
            for (var y = 0; y < h; y += rows)
            {
                var n = Math.Min(rows, h - y);
                var span = pool.AsSpan(0, w * n);
                Read(0, y, w, n, span);
                process(y, n, span);
            }
        }
        finally
        {
            ArrayPool<T>.Shared.Return(pool);
        }
    }

    /// <summary>Fills the whole band with a constant value.</summary>
    public void Fill(double value, double imaginary = 0) => GdalNative.Check(GdalNative.GDALFillRaster(Handle, value, imaginary), "FillRaster");

    /// <summary>GDAL checksum of a window (or the whole band), handy for regression tests.</summary>
    public int Checksum(int xOff = 0, int yOff = 0, int xSize = -1, int ySize = -1)
        => GdalNative.GDALChecksumImage(Handle, xOff, yOff, xSize < 0 ? Width : xSize, ySize < 0 ? Height : ySize);

    // ------------------------------------------------------------------ statistics

    /// <summary>Computes (and caches in the dataset metadata when possible) min/max/mean/stddev.</summary>
    public BandStatistics ComputeStatistics(bool approximate = false, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        double min, max, mean, std;
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var err = GdalNative.GDALComputeRasterStatistics(Handle, approximate ? 1 : 0, &min, &max, &mean, &std, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        GdalNative.Check(err, "ComputeStatistics");
        return new BandStatistics(min, max, mean, std);
    }

    /// <summary>Returns cached statistics, or computes them when <paramref name="force"/> is true.</summary>
    public BandStatistics? GetStatistics(bool approximate = true, bool force = true)
    {
        double min, max, mean, std;
        var err = GdalNative.GDALGetRasterStatistics(Handle, approximate ? 1 : 0, force ? 1 : 0, &min, &max, &mean, &std);
        if (err != 0)
        {
            GdalNative.CPLErrorReset();
            return null;
        }
        return new BandStatistics(min, max, mean, std);
    }

    /// <summary>Computes the min/max of the band.</summary>
    public (double Min, double Max) ComputeMinMax(bool approximate = false)
    {
        var mm = stackalloc double[2];
        GdalNative.Check(GdalNative.GDALComputeRasterMinMax(Handle, approximate ? 1 : 0, mm), "ComputeRasterMinMax");
        return (mm[0], mm[1]);
    }

    /// <summary>Computes a histogram of <paramref name="buckets"/> buckets between min and max (defaults to the band range).</summary>
    public Histogram GetHistogram(int buckets = 256, double? min = null, double? max = null, bool includeOutOfRange = false, bool approximate = false,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        double lo, hi;
        if (min is null || max is null)
        {
            // Auto range: make the maximum fall into the last bucket instead of being "out of range".
            var (mn, mx) = ComputeMinMax(approximate);
            lo = min ?? mn;
            hi = max ?? mx;
            includeOutOfRange = true;
        }
        else
        {
            lo = min.Value;
            hi = max.Value;
        }
        if (hi <= lo) hi = lo + 1;

        var counts = new ulong[buckets];
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        fixed (ulong* p = counts)
        {
            var err = GdalNative.GDALGetRasterHistogramEx(Handle, lo, hi, buckets, p, includeOutOfRange ? 1 : 0, approximate ? 1 : 0, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
            ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
            GdalNative.Check(err, "GetHistogram");
        }
        return new Histogram(lo, hi, counts);
    }

    // ------------------------------------------------------------------ color table / RAT

    /// <summary>Palette of the band (a managed copy), or null.</summary>
    public ColorTable? ColorTable
    {
        get
        {
            var h = GdalNative.GDALGetRasterColorTable(Handle);
            if (h == 0) return null;
            using var borrowed = GdalSafeHandle.Borrowed(h, Handle);
            return new ColorTable(GdalNative.GDALCloneColorTable(borrowed));
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            GdalNative.Check(GdalNative.GDALSetRasterColorTable(Handle, value.Handle), "SetColorTable");
        }
    }

    /// <summary>The default raster attribute table (borrowed from the band), or null.</summary>
    public RasterAttributeTable? AttributeTable
    {
        get
        {
            var h = GdalNative.GDALGetDefaultRAT(Handle);
            return h == 0 ? null : new RasterAttributeTable(GdalSafeHandle.Borrowed(h, Handle));
        }
        set
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            GdalNative.Check(GdalNative.GDALSetDefaultRAT(Handle, value.Handle), "SetDefaultRAT");
        }
    }

    public override string ToString() => $"Band {Number} {DataType} {Width}x{Height} ({ColorInterpretation})";
}

/// <summary>Maps CLR primitive types to GDAL data types.</summary>
public static class DataTypes
{
    public static DataType Of<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(byte)) return DataType.Byte;
        if (typeof(T) == typeof(sbyte)) return DataType.Int8;
        if (typeof(T) == typeof(short)) return DataType.Int16;
        if (typeof(T) == typeof(ushort)) return DataType.UInt16;
        if (typeof(T) == typeof(int)) return DataType.Int32;
        if (typeof(T) == typeof(uint)) return DataType.UInt32;
        if (typeof(T) == typeof(long)) return DataType.Int64;
        if (typeof(T) == typeof(ulong)) return DataType.UInt64;
        if (typeof(T) == typeof(float)) return DataType.Float32;
        if (typeof(T) == typeof(double)) return DataType.Float64;
        if (typeof(T) == typeof(Half)) return DataType.Float16;
        throw new NotSupportedException($"Type {typeof(T)} has no GDAL equivalent.");
    }

    /// <summary>Size in bytes of one pixel of the given type.</summary>
    public static int SizeOf(DataType type) => type switch
    {
        DataType.Byte or DataType.Int8 => 1,
        DataType.UInt16 or DataType.Int16 or DataType.Float16 => 2,
        DataType.UInt32 or DataType.Int32 or DataType.Float32 or DataType.CInt16 or DataType.CFloat16 => 4,
        DataType.Float64 or DataType.UInt64 or DataType.Int64 or DataType.CInt32 or DataType.CFloat32 => 8,
        DataType.CFloat64 => 16,
        _ => 0,
    };

    public static bool IsInteger(DataType type) => type is DataType.Byte or DataType.Int8 or DataType.UInt16 or DataType.Int16
        or DataType.UInt32 or DataType.Int32 or DataType.UInt64 or DataType.Int64;

    public static bool IsComplex(DataType type) => type is DataType.CInt16 or DataType.CInt32 or DataType.CFloat16 or DataType.CFloat32 or DataType.CFloat64;
}
