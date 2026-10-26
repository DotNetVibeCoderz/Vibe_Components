using System.Collections;
using GDAL.Net.Native;
using GDAL.Net.Srs;

namespace GDAL.Net.Vector;

/// <summary>A snapshot of a feature detached from native memory (handy for UI binding and LINQ).</summary>
public sealed record FeatureRecord(long Id, IReadOnlyDictionary<string, object?> Attributes, string? GeometryWkt, Envelope? Envelope, GeometryType GeometryType);

/// <summary>
/// A vector layer (table of features). Owned by its <see cref="Dataset"/>.
/// Enumerating yields owned <see cref="Feature"/> objects: dispose them, or use <see cref="ForEach(Action{Feature})"/>.
/// </summary>
public unsafe class Layer : GdalObject, IEnumerable<Feature>
{
    internal Layer(GdalSafeHandle handle, Dataset dataset) : base(handle)
    {
        Dataset = dataset;
    }

    public Dataset Dataset { get; }

    public string Name => Interop.PtrToString(GdalNative.OGR_L_GetName(Handle)) ?? string.Empty;

    public GeometryType GeometryType => (GeometryType)GdalNative.OGR_L_GetGeomType(Handle);

    /// <summary>Feature count honoring current filters (-1 if unknown and not forced).</summary>
    public long GetFeatureCount(bool force = true) => GdalNative.OGR_L_GetFeatureCount(Handle, force ? 1 : 0);

    public long FeatureCount => GetFeatureCount();

    /// <summary>Extent of the layer (computed when not stored).</summary>
    public Envelope GetExtent(bool force = true)
    {
        OGREnvelope e;
        var err = GdalNative.OGR_L_GetExtent(Handle, &e, force ? 1 : 0);
        if (err != 0)
        {
            GdalNative.CPLErrorReset();
            return Envelope.Empty;
        }
        return new Envelope(e.MinX, e.MinY, e.MaxX, e.MaxY);
    }

    public Envelope Extent => GetExtent();

    /// <summary>Layer CRS (a copy), or null.</summary>
    public SpatialReference? SpatialReference
    {
        get
        {
            var h = GdalNative.OGR_L_GetSpatialRef(Handle);
            return h == 0 ? null : SpatialReference.CloneFrom(h);
        }
    }

    public FeatureDefinition Definition => new(GdalSafeHandle.Borrowed(GdalNative.OGR_L_GetLayerDefn(Handle), Handle));

    /// <summary>Snapshot of the attribute schema.</summary>
    public IReadOnlyList<FieldInfo> Fields
    {
        get
        {
            using var d = Definition;
            return d.Fields;
        }
    }

    public string FidColumn => Interop.PtrToString(GdalNative.OGR_L_GetFIDColumn(Handle)) ?? string.Empty;
    public string GeometryColumn => Interop.PtrToString(GdalNative.OGR_L_GetGeometryColumn(Handle)) ?? string.Empty;

    /// <summary>Tests a capability (OLC*: "RandomRead", "SequentialWrite", "FastSpatialFilter", "FastFeatureCount", "Transactions", ...).</summary>
    public bool TestCapability(string capability) => GdalNative.OGR_L_TestCapability(Handle, capability) != 0;

    // ------------------------------------------------------------------ reading

    public void ResetReading() => GdalNative.OGR_L_ResetReading(Handle);

    /// <summary>Next feature of the current read cursor, or null at the end.</summary>
    public Feature? GetNextFeature()
    {
        var h = GdalNative.OGR_L_GetNextFeature(Handle);
        return h == 0 ? null : new Feature(h);
    }

    /// <summary>Random read by feature id.</summary>
    public Feature? GetFeature(long fid)
    {
        var h = GdalNative.OGR_L_GetFeature(Handle, fid);
        if (h == 0) GdalNative.CPLErrorReset();
        return h == 0 ? null : new Feature(h);
    }

    /// <summary>Iterates every feature (respecting filters) and disposes each one after <paramref name="action"/>.</summary>
    public void ForEach(Action<Feature> action)
    {
        ResetReading();
        while (GetNextFeature() is { } f)
        {
            using (f)
            {
                action(f);
            }
        }
    }

    /// <summary>Reads all features (respecting filters) as detached records.</summary>
    public List<FeatureRecord> ReadRecords(int maxFeatures = int.MaxValue, bool includeGeometry = true)
    {
        var list = new List<FeatureRecord>();
        ResetReading();
        while (list.Count < maxFeatures && GetNextFeature() is { } f)
        {
            using (f)
            {
                var g = f.Geometry;
                list.Add(new FeatureRecord(f.Id, f.GetAttributes(), includeGeometry ? g?.ToWkt() : null, g?.Envelope, g?.GeometryType ?? GeometryType.None));
            }
        }
        return list;
    }

    public IEnumerator<Feature> GetEnumerator()
    {
        ResetReading();
        while (GetNextFeature() is { } f)
        {
            yield return f;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ------------------------------------------------------------------ filters

    /// <summary>Attribute filter (OGR SQL WHERE clause), null to clear.</summary>
    public void SetAttributeFilter(string? where) => GdalNative.CheckOgr(GdalNative.OGR_L_SetAttributeFilter(Handle, where), "SetAttributeFilter");

    /// <summary>Spatial filter by geometry, null to clear.</summary>
    public void SetSpatialFilter(Geometry? geometry)
    {
        GdalNative.OGR_L_SetSpatialFilter(Handle, geometry is null ? 0 : geometry.Handle.DangerousGetHandle());
        GC.KeepAlive(geometry);
    }

    /// <summary>Spatial filter by rectangle.</summary>
    public void SetSpatialFilter(Envelope bounds) => GdalNative.OGR_L_SetSpatialFilterRect(Handle, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);

    /// <summary>Fields that should not be fetched (speeds up reading wide tables). Use "OGR_GEOMETRY" to skip geometries.</summary>
    public void SetIgnoredFields(IEnumerable<string>? fields)
    {
        using var list = new NativeStringList(fields);
        GdalNative.CheckOgr(GdalNative.OGR_L_SetIgnoredFields(Handle, list.Pointer), "SetIgnoredFields");
    }

    // ------------------------------------------------------------------ schema

    public void CreateField(FieldDefinition field, bool approxOk = true)
        => GdalNative.CheckOgr(GdalNative.OGR_L_CreateField(Handle, field.Handle, approxOk ? 1 : 0), $"CreateField '{field.Name}'");

    public void CreateField(string name, FieldType type, int width = 0, int precision = 0, FieldSubType subType = FieldSubType.None)
    {
        using var f = new FieldDefinition(name, type, width, precision, subType);
        CreateField(f);
    }

    public void DeleteField(int index) => GdalNative.CheckOgr(GdalNative.OGR_L_DeleteField(Handle, index), "DeleteField");

    // ------------------------------------------------------------------ writing

    /// <summary>Creates a new feature in the layer (its id is assigned by the driver).</summary>
    public void CreateFeature(Feature feature) => GdalNative.CheckOgr(GdalNative.OGR_L_CreateFeature(Handle, feature.Handle), "CreateFeature");

    /// <summary>Rewrites an existing feature (by id).</summary>
    public void SetFeature(Feature feature) => GdalNative.CheckOgr(GdalNative.OGR_L_SetFeature(Handle, feature.Handle), "SetFeature");

    public void DeleteFeature(long fid) => GdalNative.CheckOgr(GdalNative.OGR_L_DeleteFeature(Handle, fid), "DeleteFeature");

    /// <summary>Convenience: adds a feature from a geometry (copied) and attribute values. Returns the new feature id.</summary>
    public long AddFeature(Geometry? geometry, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        using var defn = Definition;
        using var feature = new Feature(defn);
        if (geometry is not null) feature.Geometry = geometry;
        if (attributes is not null)
        {
            foreach (var kv in attributes)
            {
                var idx = defn.GetFieldIndex(kv.Key);
                if (idx < 0) throw new KeyNotFoundException($"Field '{kv.Key}' does not exist in layer '{Name}'.");
                feature.SetValue(idx, kv.Value);
            }
        }
        CreateFeature(feature);
        return feature.Id;
    }

    public void StartTransaction() => GdalNative.CheckOgr(GdalNative.OGR_L_StartTransaction(Handle), "StartTransaction");
    public void CommitTransaction() => GdalNative.CheckOgr(GdalNative.OGR_L_CommitTransaction(Handle), "CommitTransaction");
    public void RollbackTransaction() => GdalNative.CheckOgr(GdalNative.OGR_L_RollbackTransaction(Handle), "RollbackTransaction");

    public void SyncToDisk() => GdalNative.CheckOgr(GdalNative.OGR_L_SyncToDisk(Handle), "SyncToDisk");

    // ------------------------------------------------------------------ overlay (geoprocessing between layers)

    /// <summary>
    /// Spatial overlay between this (input) layer and <paramref name="method"/>, written to <paramref name="result"/>
    /// (Intersection, Union, SymDifference, Identity, Update, Clip, Erase).
    /// </summary>
    public void Overlay(LayerOverlay operation, Layer method, Layer result, IDictionary<string, string>? options = null,
        IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var opts = NativeStringList.FromOptions(options);
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var pf = ProgressBridge.Function(bridge);
        var pd = ProgressBridge.Data(bridge);
        var err = operation switch
        {
            LayerOverlay.Intersection => GdalNative.OGR_L_Intersection(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            LayerOverlay.Union => GdalNative.OGR_L_Union(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            LayerOverlay.SymDifference => GdalNative.OGR_L_SymDifference(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            LayerOverlay.Identity => GdalNative.OGR_L_Identity(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            LayerOverlay.Update => GdalNative.OGR_L_Update(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            LayerOverlay.Clip => GdalNative.OGR_L_Clip(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            LayerOverlay.Erase => GdalNative.OGR_L_Erase(Handle, method.Handle, result.Handle, opts.Pointer, pf, pd),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        GdalNative.CheckOgr(err, $"Layer {operation}");
    }

    /// <summary>Collects (clones) all geometries of the layer.</summary>
    public List<Geometry> CollectGeometries()
    {
        var list = new List<Geometry>();
        ForEach(f =>
        {
            if (f.Geometry is { } g) list.Add(g.Clone());
        });
        return list;
    }

    /// <summary>Dissolves (unions) all geometries of the layer into one geometry.</summary>
    public Geometry? Dissolve()
    {
        var geoms = CollectGeometries();
        try
        {
            return geoms.Count == 0 ? null : Geometry.UnionAll(geoms);
        }
        finally
        {
            foreach (var g in geoms) g.Dispose();
        }
    }

    public override string ToString() => $"Layer '{Name}' ({GeometryType})";
}

/// <summary>Result layer of <see cref="Dataset.ExecuteSql"/>. Dispose it to release the result set.</summary>
public sealed unsafe class SqlResultLayer : Layer
{
    internal SqlResultLayer(nint handle, Dataset dataset)
        : base(new GdalSafeHandle(handle, &Release, dataset.RawHandle), dataset) { }

    private static void Release(nint layer, nint dataset) => GdalNative.GDALDatasetReleaseResultSet(dataset, layer);
}
