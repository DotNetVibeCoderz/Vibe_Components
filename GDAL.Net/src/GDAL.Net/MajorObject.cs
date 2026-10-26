using GDAL.Net.Native;

namespace GDAL.Net;

/// <summary>Common metadata API of datasets and raster bands (GDALMajorObject).</summary>
public abstract class MajorObject : GdalObject
{
    internal MajorObject(GdalSafeHandle handle) : base(handle) { }

    /// <summary>Object description (file name for datasets).</summary>
    public string Description
    {
        get => Interop.PtrToString(GdalNative.GDALGetDescription(Handle)) ?? string.Empty;
        set => GdalNative.GDALSetDescription(Handle, value);
    }

    /// <summary>Metadata domains available ("" is the default domain; also IMAGE_STRUCTURE, RPC, GEOLOCATION, EXIF, xml:XMP, SUBDATASETS, ...).</summary>
    public IReadOnlyList<string> MetadataDomains => Interop.TakeStringList(GdalNative.GDALGetMetadataDomainList(Handle));

    /// <summary>Metadata of a domain as key/value pairs.</summary>
    public IReadOnlyDictionary<string, string> GetMetadata(string? domain = null)
        => Interop.PtrToKeyValues(GdalNative.GDALGetMetadata(Handle, domain));

    /// <summary>Raw metadata list of a domain (useful for xml:* domains that hold a single document).</summary>
    public IReadOnlyList<string> GetMetadataList(string? domain = null)
        => Interop.PtrToStringList(GdalNative.GDALGetMetadata(Handle, domain));

    public string? GetMetadataItem(string name, string? domain = null)
        => Interop.PtrToString(GdalNative.GDALGetMetadataItem(Handle, name, domain));

    public void SetMetadataItem(string name, string? value, string? domain = null)
        => GdalNative.Check(GdalNative.GDALSetMetadataItem(Handle, name, value, domain), $"SetMetadataItem({name})");

    /// <summary>Replaces the whole metadata of a domain.</summary>
    public void SetMetadata(IReadOnlyDictionary<string, string> metadata, string? domain = null)
    {
        using var list = NativeStringList.FromOptions(metadata);
        GdalNative.Check(GdalNative.GDALSetMetadata(Handle, list.Pointer, domain), "SetMetadata");
    }
}
