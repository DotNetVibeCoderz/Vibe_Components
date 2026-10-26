using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>A GDAL format driver (GTiff, COG, GPKG, GeoJSON, netCDF, ...). Drivers are owned by GDAL and never freed.</summary>
public sealed class Driver : IEquatable<Driver>
{
    private readonly nint _handle;

    internal Driver(nint handle) => _handle = handle;

    internal nint Handle => _handle;

    /// <summary>Short name, e.g. "GTiff".</summary>
    public string ShortName => Interop.PtrToString(GdalNative.GDALGetDriverShortName(_handle)) ?? string.Empty;

    /// <summary>Long name, e.g. "GeoTIFF".</summary>
    public string LongName => Interop.PtrToString(GdalNative.GDALGetDriverLongName(_handle)) ?? string.Empty;

    /// <summary>Driver metadata item (DCAP_*, DMD_*).</summary>
    public string? GetMetadataItem(string name, string? domain = null) => Interop.PtrToString(GdalNative.GDALGetMetadataItem(_handle, name, domain));

    /// <summary>All driver metadata (capabilities, extensions, creation options XML, ...).</summary>
    public IReadOnlyDictionary<string, string> Metadata => Interop.PtrToKeyValues(GdalNative.GDALGetMetadata(_handle, null));

    private bool Cap(string name) => string.Equals(GetMetadataItem(name), "YES", StringComparison.OrdinalIgnoreCase);

    public bool SupportsRaster => Cap("DCAP_RASTER");
    public bool SupportsVector => Cap("DCAP_VECTOR");
    public bool SupportsMultidimensional => Cap("DCAP_MULTIDIM_RASTER");
    public bool SupportsCreate => Cap("DCAP_CREATE");
    public bool SupportsCreateCopy => Cap("DCAP_CREATECOPY");
    public bool SupportsVirtualIO => Cap("DCAP_VIRTUALIO");
    public bool SupportsMultidimensionalCreate => Cap("DCAP_CREATE_MULTIDIMENSIONAL");

    /// <summary>Typical file extensions (space separated in GDAL).</summary>
    public IReadOnlyList<string> Extensions => (GetMetadataItem("DMD_EXTENSIONS") ?? GetMetadataItem("DMD_EXTENSION") ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Help topic URL fragment.</summary>
    public string? HelpTopic => GetMetadataItem("DMD_HELPTOPIC");

    /// <summary>XML description of supported creation options.</summary>
    public string? CreationOptionList => GetMetadataItem("DMD_CREATIONOPTIONLIST");

    /// <summary>Creates a new raster dataset.</summary>
    public Dataset Create(string path, int width, int height, int bandCount, DataType dataType, IDictionary<string, string>? creationOptions = null)
    {
        using var options = NativeStringList.FromOptions(creationOptions);
        var h = GdalNative.GDALCreate(_handle, path, width, height, bandCount, (int)dataType, options.Pointer);
        return h == 0 ? throw GdalNative.LastError($"{ShortName}: unable to create '{path}'") : new Dataset(h);
    }

    /// <summary>Creates a new (empty) vector dataset.</summary>
    public Dataset CreateVector(string path, IDictionary<string, string>? creationOptions = null)
    {
        using var options = NativeStringList.FromOptions(creationOptions);
        var h = GdalNative.GDALCreate(_handle, path, 0, 0, 0, (int)DataType.Unknown, options.Pointer);
        return h == 0 ? throw GdalNative.LastError($"{ShortName}: unable to create '{path}'") : new Dataset(h);
    }

    /// <summary>Creates a multidimensional dataset (MEM, netCDF, Zarr, ...).</summary>
    public Dataset CreateMultidimensional(string path, IDictionary<string, string>? options = null)
    {
        using var opts = NativeStringList.FromOptions(options);
        var h = GdalNative.GDALCreateMultiDimensional(_handle, path, 0, opts.Pointer);
        return h == 0 ? throw GdalNative.LastError($"{ShortName}: unable to create multidimensional '{path}'") : new Dataset(h);
    }

    /// <summary>Creates a copy of <paramref name="source"/> in this format (e.g. GeoTIFF → COG/PNG/JPEG).</summary>
    public Dataset CreateCopy(string path, Dataset source, IDictionary<string, string>? creationOptions = null, bool strict = false,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var options = NativeStringList.FromOptions(creationOptions);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var h = GdalNative.GDALCreateCopy(_handle, path, source.Handle, strict ? 1 : 0, options.Pointer, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        return h == 0 ? throw GdalNative.LastError($"{ShortName}: unable to copy to '{path}'") : new Dataset(h);
    }

    /// <summary>Deletes a dataset and all its side-car files.</summary>
    public void Delete(string path) => GdalNative.Check(GdalNative.GDALDeleteDataset(_handle, path), $"Delete '{path}'");

    public bool Equals(Driver? other) => other is not null && other._handle == _handle;
    public override bool Equals(object? obj) => obj is Driver d && Equals(d);
    public override int GetHashCode() => _handle.GetHashCode();
    public override string ToString() => $"{ShortName} ({LongName})";
}
