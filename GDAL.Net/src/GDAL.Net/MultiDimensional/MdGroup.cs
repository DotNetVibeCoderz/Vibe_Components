using GDAL.Net.Native;
using GDAL.Net.Srs;

namespace GDAL.Net.MultiDimensional;

/// <summary>A dimension of a multidimensional array (snapshot).</summary>
/// <param name="Name">Dimension name, e.g. "time", "lat", "lon".</param>
/// <param name="Size">Number of elements.</param>
/// <param name="Type">Semantic type: HORIZONTAL_X, HORIZONTAL_Y, TEMPORAL, VERTICAL, ...</param>
/// <param name="Direction">EAST, NORTH, FUTURE, UP, ...</param>
/// <param name="IndexingVariable">Name of the array holding coordinate values, if any.</param>
public sealed record MdDimension(string Name, ulong Size, string? Type, string? Direction, string? IndexingVariable);

/// <summary>An attribute (snapshot) of a group or array.</summary>
public sealed record MdAttribute(string Name, string? StringValue, double NumericValue);

/// <summary>A group of a multidimensional dataset (netCDF/HDF5/Zarr hierarchy).</summary>
public sealed unsafe class MdGroup : GdalObject
{
    internal MdGroup(nint handle, Dataset dataset) : base(new GdalSafeHandle(handle, &Release))
    {
        Dataset = dataset;
    }

    private static void Release(nint h) => GdalNative.GDALGroupRelease(h);

    /// <summary>Dataset the group comes from (kept alive by this object).</summary>
    public Dataset Dataset { get; }

    public string Name => Interop.PtrToString(GdalNative.GDALGroupGetName(Handle)) ?? string.Empty;
    public string FullName => Interop.PtrToString(GdalNative.GDALGroupGetFullName(Handle)) ?? string.Empty;

    public IReadOnlyList<string> ArrayNames => Interop.TakeStringList(GdalNative.GDALGroupGetMDArrayNames(Handle, 0));
    public IReadOnlyList<string> GroupNames => Interop.TakeStringList(GdalNative.GDALGroupGetGroupNames(Handle, 0));

    public MdArray OpenArray(string name)
    {
        var h = GdalNative.GDALGroupOpenMDArray(Handle, name, 0);
        return h == 0 ? throw GdalNative.LastError($"Array '{name}' not found") : new MdArray(h, Dataset);
    }

    public MdGroup OpenGroup(string name)
    {
        var h = GdalNative.GDALGroupOpenGroup(Handle, name, 0);
        return h == 0 ? throw GdalNative.LastError($"Group '{name}' not found") : new MdGroup(h, Dataset);
    }

    public IReadOnlyList<MdDimension> Dimensions
    {
        get
        {
            nuint count;
            var dims = GdalNative.GDALGroupGetDimensions(Handle, &count, 0);
            return MdArray.ReadDimensions(dims, count);
        }
    }

    public IReadOnlyList<MdAttribute> Attributes
    {
        get
        {
            nuint count;
            var attrs = GdalNative.GDALGroupGetAttributes(Handle, &count, 0);
            return MdArray.ReadAttributes(attrs, count);
        }
    }

    // ------------------------------------------------------------------ creation

    public MdGroup CreateGroup(string name)
    {
        var h = GdalNative.GDALGroupCreateGroup(Handle, name, 0);
        return h == 0 ? throw GdalNative.LastError($"CreateGroup '{name}'") : new MdGroup(h, Dataset);
    }

    /// <summary>Creates a dimension. Type examples: "HORIZONTAL_X", "HORIZONTAL_Y", "TEMPORAL"; direction: "EAST", "NORTH", "FUTURE".</summary>
    private nint CreateDimensionHandle(string name, ulong size, string? type = null, string? direction = null)
    {
        var h = GdalNative.GDALGroupCreateDimension(Handle, name, type, direction, size, 0);
        return h == 0 ? throw GdalNative.LastError($"CreateDimension '{name}'") : h;
    }

    /// <summary>
    /// Creates an array over new dimensions given as (name, size, type, direction) tuples. Coordinate variables can be created
    /// as 1-D arrays named like their dimension and detected automatically by drivers such as netCDF.
    /// </summary>
    public MdArray CreateArray(string name, DataType dataType, IReadOnlyList<(string Name, ulong Size, string? Type, string? Direction)> dimensions,
        IDictionary<string, string>? options = null)
    {
        var dimHandles = new nint[dimensions.Count];
        try
        {
            for (var i = 0; i < dimensions.Count; i++)
            {
                var d = dimensions[i];
                dimHandles[i] = ResolveDimension(d.Name, d.Size, d.Type, d.Direction);
            }
            return CreateArray(name, dataType, dimHandles, options);
        }
        finally
        {
            foreach (var h in dimHandles) if (h != 0) GdalNative.GDALDimensionRelease(h);
        }
    }

    private nint ResolveDimension(string name, ulong size, string? type, string? direction)
    {
        nuint count;
        var existing = GdalNative.GDALGroupGetDimensions(Handle, &count, 0);
        nint found = 0;
        if (existing != null)
        {
            for (nuint i = 0; i < count; i++)
            {
                if (Interop.PtrToString(GdalNative.GDALDimensionGetName(existing[i])) == name)
                {
                    found = existing[i];
                    existing[i] = 0;
                    break;
                }
            }
            for (nuint i = 0; i < count; i++) if (existing[i] != 0) GdalNative.GDALDimensionRelease(existing[i]);
            GdalNative.VSIFree((nint)existing);
        }
        return found != 0 ? found : CreateDimensionHandle(name, size, type, direction);
    }

    private MdArray CreateArray(string name, DataType dataType, nint[] dims, IDictionary<string, string>? options)
    {
        var type = GdalNative.GDALExtendedDataTypeCreate((int)dataType);
        using var opts = NativeStringList.FromOptions(options);
        try
        {
            fixed (nint* pd = dims)
            {
                var h = GdalNative.GDALGroupCreateMDArray(Handle, name, (nuint)dims.Length, pd, type, opts.Pointer);
                return h == 0 ? throw GdalNative.LastError($"CreateMDArray '{name}'") : new MdArray(h, Dataset);
            }
        }
        finally
        {
            GdalNative.GDALExtendedDataTypeRelease(type);
        }
    }

    /// <summary>Writes a string attribute on the group.</summary>
    public void SetAttribute(string name, string value)
    {
        var type = GdalNative.GDALExtendedDataTypeCreateString(0);
        try
        {
            var attr = GdalNative.GDALGroupCreateAttribute(Handle, name, 0, null, type, 0);
            if (attr == 0) throw GdalNative.LastError($"CreateAttribute '{name}'");
            GdalNative.GDALAttributeWriteString(attr, value);
            GdalNative.GDALAttributeRelease(attr);
        }
        finally
        {
            GdalNative.GDALExtendedDataTypeRelease(type);
        }
    }

    public override string ToString() => $"Group '{FullName}'";
}

/// <summary>An N-dimensional array (variable) of a multidimensional dataset.</summary>
public sealed unsafe class MdArray : GdalObject
{
    internal MdArray(nint handle, Dataset dataset) : base(new GdalSafeHandle(handle, &Release))
    {
        Dataset = dataset;
    }

    private static void Release(nint h) => GdalNative.GDALMDArrayRelease(h);

    public Dataset Dataset { get; }

    public string Name => Interop.PtrToString(GdalNative.GDALMDArrayGetName(Handle)) ?? string.Empty;
    public string FullName => Interop.PtrToString(GdalNative.GDALMDArrayGetFullName(Handle)) ?? string.Empty;
    public ulong ElementCount => GdalNative.GDALMDArrayGetTotalElementsCount(Handle);
    public int DimensionCount => (int)GdalNative.GDALMDArrayGetDimensionCount(Handle);

    public string Unit
    {
        get => Interop.PtrToString(GdalNative.GDALMDArrayGetUnit(Handle)) ?? string.Empty;
        set => GdalNative.GDALMDArraySetUnit(Handle, value);
    }

    public double? NoDataValue
    {
        get { int has; var v = GdalNative.GDALMDArrayGetNoDataValueAsDouble(Handle, &has); return has != 0 ? v : null; }
        set
        {
            var ok = value is { } v
                ? GdalNative.GDALMDArraySetNoDataValueAsDouble(Handle, v)
                : GdalNative.GDALMDArraySetRawNoDataValue(Handle, null);
            if (ok == 0) throw GdalNative.LastError($"Set nodata of '{Name}' (set it before writing data for netCDF)");
        }
    }

    public double Offset { get { int has; var v = GdalNative.GDALMDArrayGetOffset(Handle, &has); return has != 0 ? v : 0; } }
    public double Scale { get { int has; var v = GdalNative.GDALMDArrayGetScale(Handle, &has); return has != 0 ? v : 1; } }

    /// <summary>Numeric data type (Unknown for compound/string arrays).</summary>
    public DataType DataType
    {
        get
        {
            var t = GdalNative.GDALMDArrayGetDataType(Handle);
            try
            {
                return (DataType)GdalNative.GDALExtendedDataTypeGetNumericDataType(t);
            }
            finally
            {
                GdalNative.GDALExtendedDataTypeRelease(t);
            }
        }
    }

    public SpatialReference? SpatialReference
    {
        get
        {
            var h = GdalNative.GDALMDArrayGetSpatialRef(Handle);
            if (h == 0) return null;
            var srs = SpatialReference.CloneFrom(h);
            GdalNative.OSRRelease(h);
            return srs;
        }
    }

    public IReadOnlyList<MdDimension> Dimensions
    {
        get
        {
            nuint count;
            var dims = GdalNative.GDALMDArrayGetDimensions(Handle, &count);
            return ReadDimensions(dims, count);
        }
    }

    public IReadOnlyList<MdAttribute> Attributes
    {
        get
        {
            nuint count;
            var attrs = GdalNative.GDALMDArrayGetAttributes(Handle, &count, 0);
            return ReadAttributes(attrs, count);
        }
    }

    /// <summary>Reads a hyper-rectangle (start/count per dimension) into <paramref name="buffer"/> as <typeparamref name="T"/> (C order).</summary>
    public void Read<T>(ReadOnlySpan<ulong> start, ReadOnlySpan<ulong> count, Span<T> buffer) where T : unmanaged
    {
        var n = start.Length;
        if (count.Length != n) throw new ArgumentException("start and count must have the same rank.");
        ulong total = 1;
        foreach (var c in count) total *= c;
        if ((ulong)buffer.Length < total) throw new ArgumentException("Buffer is too small.", nameof(buffer));
        var counts = stackalloc nuint[n];
        for (var i = 0; i < n; i++) counts[i] = (nuint)count[i];
        var type = GdalNative.GDALExtendedDataTypeCreate((int)DataTypes.Of<T>());
        try
        {
            fixed (ulong* ps = start)
            fixed (T* pb = buffer)
            {
                var ok = GdalNative.GDALMDArrayRead(Handle, ps, counts, null, null, type, pb, pb, (nuint)(buffer.Length * sizeof(T)));
                if (ok == 0) throw GdalNative.LastError($"Read array '{Name}'");
            }
        }
        finally
        {
            GdalNative.GDALExtendedDataTypeRelease(type);
        }
    }

    /// <summary>Reads the whole array.</summary>
    public T[] ReadAll<T>() where T : unmanaged
    {
        var dims = Dimensions;
        var start = new ulong[dims.Count];
        var count = dims.Select(d => d.Size).ToArray();
        var data = new T[checked((int)ElementCount)];
        Read<T>(start, count, data);
        return data;
    }

    /// <summary>Writes a hyper-rectangle.</summary>
    public void Write<T>(ReadOnlySpan<ulong> start, ReadOnlySpan<ulong> count, ReadOnlySpan<T> buffer) where T : unmanaged
    {
        var n = start.Length;
        var counts = stackalloc nuint[n];
        for (var i = 0; i < n; i++) counts[i] = (nuint)count[i];
        var type = GdalNative.GDALExtendedDataTypeCreate((int)DataTypes.Of<T>());
        try
        {
            fixed (ulong* ps = start)
            fixed (T* pb = buffer)
            {
                var ok = GdalNative.GDALMDArrayWrite(Handle, ps, counts, null, null, type, pb, pb, (nuint)(buffer.Length * sizeof(T)));
                if (ok == 0) throw GdalNative.LastError($"Write array '{Name}'");
            }
        }
        finally
        {
            GdalNative.GDALExtendedDataTypeRelease(type);
        }
    }

    /// <summary>Writes the whole array.</summary>
    public void WriteAll<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        var dims = Dimensions;
        Write(new ulong[dims.Count], dims.Select(d => d.Size).ToArray(), data);
    }

    /// <summary>Writes a string attribute.</summary>
    public void SetAttribute(string name, string value)
    {
        var type = GdalNative.GDALExtendedDataTypeCreateString(0);
        try
        {
            var attr = GdalNative.GDALMDArrayCreateAttribute(Handle, name, 0, null, type, 0);
            if (attr == 0) throw GdalNative.LastError($"CreateAttribute '{name}'");
            GdalNative.GDALAttributeWriteString(attr, value);
            GdalNative.GDALAttributeRelease(attr);
        }
        finally
        {
            GdalNative.GDALExtendedDataTypeRelease(type);
        }
    }

    /// <summary>Writes a numeric attribute.</summary>
    public void SetAttribute(string name, double value)
    {
        var type = GdalNative.GDALExtendedDataTypeCreate((int)DataType.Float64);
        try
        {
            var attr = GdalNative.GDALMDArrayCreateAttribute(Handle, name, 0, null, type, 0);
            if (attr == 0) throw GdalNative.LastError($"CreateAttribute '{name}'");
            GdalNative.GDALAttributeWriteDouble(attr, value);
            GdalNative.GDALAttributeRelease(attr);
        }
        finally
        {
            GdalNative.GDALExtendedDataTypeRelease(type);
        }
    }

    /// <summary>
    /// Views a 2-D slice of the array as a classic raster dataset (the other dimensions become bands),
    /// so it can be rendered, warped or translated with the regular raster API.
    /// </summary>
    public Dataset AsClassicDataset(int xDimension, int yDimension)
    {
        var h = GdalNative.GDALMDArrayAsClassicDataset(Handle, (nuint)xDimension, (nuint)yDimension);
        return h == 0 ? throw GdalNative.LastError($"AsClassicDataset '{Name}'") : new Dataset(h);
    }

    internal static IReadOnlyList<MdDimension> ReadDimensions(nint* dims, nuint count)
    {
        if (dims == null) return [];
        try
        {
            var list = new MdDimension[(int)count];
            for (var i = 0; i < (int)count; i++)
            {
                var d = dims[i];
                string? indexing = null;
                var iv = GdalNative.GDALDimensionGetIndexingVariable(d);
                if (iv != 0)
                {
                    using var ivh = new GdalSafeHandle(iv, &Release);
                    indexing = Interop.PtrToString(GdalNative.GDALMDArrayGetName(ivh));
                }
                list[i] = new MdDimension(
                    Interop.PtrToString(GdalNative.GDALDimensionGetName(d)) ?? string.Empty,
                    GdalNative.GDALDimensionGetSize(d),
                    NullIfEmpty(Interop.PtrToString(GdalNative.GDALDimensionGetType(d))),
                    NullIfEmpty(Interop.PtrToString(GdalNative.GDALDimensionGetDirection(d))),
                    indexing);
            }
            return list;
        }
        finally
        {
            GdalNative.GDALReleaseDimensions(dims, count);
        }
    }

    internal static IReadOnlyList<MdAttribute> ReadAttributes(nint* attrs, nuint count)
    {
        if (attrs == null) return [];
        try
        {
            var list = new MdAttribute[(int)count];
            for (var i = 0; i < (int)count; i++)
            {
                var a = attrs[i];
                var t = GdalNative.GDALAttributeGetDataType(a);
                var cls = GdalNative.GDALExtendedDataTypeGetClass(t);
                GdalNative.GDALExtendedDataTypeRelease(t);
                var name = Interop.PtrToString(GdalNative.GDALAttributeGetName(a)) ?? string.Empty;
                // GEDTC_NUMERIC = 0, GEDTC_STRING = 1
                list[i] = cls == 0
                    ? new MdAttribute(name, null, GdalNative.GDALAttributeReadAsDouble(a))
                    : new MdAttribute(name, Interop.PtrToString(GdalNative.GDALAttributeReadAsString(a)), double.NaN);
            }
            return list;
        }
        finally
        {
            GdalNative.GDALReleaseAttributes(attrs, count);
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public override string ToString() => $"Array '{FullName}' {string.Join('×', Dimensions.Select(d => $"{d.Name}={d.Size}"))} {DataType}";
}
