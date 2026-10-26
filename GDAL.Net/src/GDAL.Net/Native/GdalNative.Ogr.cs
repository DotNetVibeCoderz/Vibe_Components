using System.Runtime.InteropServices;
using H = GDAL.Net.Native.GdalSafeHandle;

namespace GDAL.Net.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct OGREnvelope
{
    public double MinX, MaxX, MinY, MaxY;
}

internal static unsafe partial class GdalNative
{
    // ---------------------------------------------------------------- Dataset (vector side)
    [LibraryImport(Lib)] public static partial int GDALDatasetGetLayerCount(H dataset);
    [LibraryImport(Lib)] public static partial nint GDALDatasetGetLayer(H dataset, int index);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALDatasetGetLayerByName(H dataset, string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALDatasetCreateLayer(H dataset, string name, nint srs, uint geomType, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALDatasetCopyLayer(H dataset, H srcLayer, string newName, nint options);
    [LibraryImport(Lib)] public static partial int GDALDatasetDeleteLayer(H dataset, int index);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALDatasetTestCapability(H dataset, string capability);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALDatasetExecuteSQL(H dataset, string sql, nint spatialFilter, string? dialect);
    [LibraryImport(Lib)] public static partial void GDALDatasetReleaseResultSet(nint dataset, nint layer);
    [LibraryImport(Lib)] public static partial int GDALDatasetStartTransaction(H dataset, int force);
    [LibraryImport(Lib)] public static partial int GDALDatasetCommitTransaction(H dataset);
    [LibraryImport(Lib)] public static partial int GDALDatasetRollbackTransaction(H dataset);

    // ---------------------------------------------------------------- Layer
    [LibraryImport(Lib)] public static partial nint OGR_L_GetName(H layer);
    [LibraryImport(Lib)] public static partial uint OGR_L_GetGeomType(H layer);
    [LibraryImport(Lib)] public static partial long OGR_L_GetFeatureCount(H layer, int force);
    [LibraryImport(Lib)] public static partial int OGR_L_GetExtent(H layer, OGREnvelope* extent, int force);
    [LibraryImport(Lib)] public static partial void OGR_L_ResetReading(H layer);
    [LibraryImport(Lib)] public static partial nint OGR_L_GetNextFeature(H layer);
    [LibraryImport(Lib)] public static partial nint OGR_L_GetFeature(H layer, long fid);
    [LibraryImport(Lib)] public static partial int OGR_L_SetFeature(H layer, H feature);
    [LibraryImport(Lib)] public static partial int OGR_L_CreateFeature(H layer, H feature);
    [LibraryImport(Lib)] public static partial int OGR_L_DeleteFeature(H layer, long fid);
    [LibraryImport(Lib)] public static partial nint OGR_L_GetLayerDefn(H layer);
    [LibraryImport(Lib)] public static partial nint OGR_L_GetSpatialRef(H layer);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OGR_L_SetAttributeFilter(H layer, string? filter);
    [LibraryImport(Lib)] public static partial void OGR_L_SetSpatialFilter(H layer, nint geometry);
    [LibraryImport(Lib)] public static partial void OGR_L_SetSpatialFilterRect(H layer, double minX, double minY, double maxX, double maxY);
    [LibraryImport(Lib)] public static partial int OGR_L_CreateField(H layer, H fieldDefn, int approxOk);
    [LibraryImport(Lib)] public static partial int OGR_L_DeleteField(H layer, int field);
    [LibraryImport(Lib)] public static partial int OGR_L_StartTransaction(H layer);
    [LibraryImport(Lib)] public static partial int OGR_L_CommitTransaction(H layer);
    [LibraryImport(Lib)] public static partial int OGR_L_RollbackTransaction(H layer);
    [LibraryImport(Lib)] public static partial int OGR_L_SyncToDisk(H layer);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OGR_L_TestCapability(H layer, string capability);
    [LibraryImport(Lib)] public static partial nint OGR_L_GetFIDColumn(H layer);
    [LibraryImport(Lib)] public static partial nint OGR_L_GetGeometryColumn(H layer);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OGR_L_SetIgnoredFields(H layer, nint fields);

    [LibraryImport(Lib)] public static partial int OGR_L_Intersection(H input, H method, H result, nint options, nint progress, nint progressData);
    [LibraryImport(Lib)] public static partial int OGR_L_Union(H input, H method, H result, nint options, nint progress, nint progressData);
    [LibraryImport(Lib)] public static partial int OGR_L_SymDifference(H input, H method, H result, nint options, nint progress, nint progressData);
    [LibraryImport(Lib)] public static partial int OGR_L_Identity(H input, H method, H result, nint options, nint progress, nint progressData);
    [LibraryImport(Lib)] public static partial int OGR_L_Update(H input, H method, H result, nint options, nint progress, nint progressData);
    [LibraryImport(Lib)] public static partial int OGR_L_Clip(H input, H method, H result, nint options, nint progress, nint progressData);
    [LibraryImport(Lib)] public static partial int OGR_L_Erase(H input, H method, H result, nint options, nint progress, nint progressData);

    // ---------------------------------------------------------------- Feature definition / fields
    [LibraryImport(Lib)] public static partial nint OGR_FD_GetName(H defn);
    [LibraryImport(Lib)] public static partial int OGR_FD_GetFieldCount(H defn);
    [LibraryImport(Lib)] public static partial nint OGR_FD_GetFieldDefn(H defn, int index);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OGR_FD_GetFieldIndex(H defn, string name);
    [LibraryImport(Lib)] public static partial uint OGR_FD_GetGeomType(H defn);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OGR_Fld_Create(string name, int type);
    [LibraryImport(Lib)] public static partial void OGR_Fld_Destroy(nint field);
    [LibraryImport(Lib)] public static partial nint OGR_Fld_GetNameRef(H field);
    [LibraryImport(Lib)] public static partial int OGR_Fld_GetType(H field);
    [LibraryImport(Lib)] public static partial int OGR_Fld_GetSubType(H field);
    [LibraryImport(Lib)] public static partial void OGR_Fld_SetSubType(H field, int subType);
    [LibraryImport(Lib)] public static partial int OGR_Fld_GetWidth(H field);
    [LibraryImport(Lib)] public static partial void OGR_Fld_SetWidth(H field, int width);
    [LibraryImport(Lib)] public static partial int OGR_Fld_GetPrecision(H field);
    [LibraryImport(Lib)] public static partial void OGR_Fld_SetPrecision(H field, int precision);
    [LibraryImport(Lib)] public static partial int OGR_Fld_IsNullable(H field);
    [LibraryImport(Lib)] public static partial void OGR_Fld_SetNullable(H field, int nullable);

    // ---------------------------------------------------------------- Feature
    [LibraryImport(Lib)] public static partial nint OGR_F_Create(H defn);
    [LibraryImport(Lib)] public static partial void OGR_F_Destroy(nint feature);
    [LibraryImport(Lib)] public static partial nint OGR_F_Clone(H feature);
    [LibraryImport(Lib)] public static partial long OGR_F_GetFID(H feature);
    [LibraryImport(Lib)] public static partial int OGR_F_SetFID(H feature, long fid);
    [LibraryImport(Lib)] public static partial nint OGR_F_GetDefnRef(H feature);
    [LibraryImport(Lib)] public static partial nint OGR_F_GetGeometryRef(H feature);
    [LibraryImport(Lib)] public static partial int OGR_F_SetGeometry(H feature, H geometry);
    [LibraryImport(Lib)] public static partial int OGR_F_SetGeometryDirectly(H feature, nint geometry);
    [LibraryImport(Lib)] public static partial int OGR_F_GetFieldCount(H feature);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OGR_F_GetFieldIndex(H feature, string name);
    [LibraryImport(Lib)] public static partial int OGR_F_IsFieldSetAndNotNull(H feature, int index);
    [LibraryImport(Lib)] public static partial nint OGR_F_GetFieldAsString(H feature, int index);
    [LibraryImport(Lib)] public static partial int OGR_F_GetFieldAsInteger(H feature, int index);
    [LibraryImport(Lib)] public static partial long OGR_F_GetFieldAsInteger64(H feature, int index);
    [LibraryImport(Lib)] public static partial double OGR_F_GetFieldAsDouble(H feature, int index);
    [LibraryImport(Lib)]
    public static partial int OGR_F_GetFieldAsDateTimeEx(H feature, int index, int* year, int* month, int* day, int* hour, int* minute, float* second, int* tzFlag);
    [LibraryImport(Lib)] public static partial nint OGR_F_GetFieldAsBinary(H feature, int index, int* count);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void OGR_F_SetFieldString(H feature, int index, string value);
    [LibraryImport(Lib)] public static partial void OGR_F_SetFieldInteger(H feature, int index, int value);
    [LibraryImport(Lib)] public static partial void OGR_F_SetFieldInteger64(H feature, int index, long value);
    [LibraryImport(Lib)] public static partial void OGR_F_SetFieldDouble(H feature, int index, double value);
    [LibraryImport(Lib)] public static partial void OGR_F_SetFieldDateTimeEx(H feature, int index, int year, int month, int day, int hour, int minute, float second, int tzFlag);
    [LibraryImport(Lib)] public static partial void OGR_F_SetFieldBinary(H feature, int index, int count, byte* data);
    [LibraryImport(Lib)] public static partial void OGR_F_SetFieldNull(H feature, int index);
    [LibraryImport(Lib)] public static partial void OGR_F_UnsetField(H feature, int index);

    // ---------------------------------------------------------------- Geometry construction / export
    [LibraryImport(Lib)] public static partial int OGR_G_CreateFromWkt(byte** text, nint srs, nint* geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_CreateFromWkb(byte* data, nint srs, nint* geometry, int bytes);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OGR_G_CreateGeometryFromJson(string json);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OGR_G_CreateFromGML(string gml);
    [LibraryImport(Lib)] public static partial nint OGR_G_CreateGeometry(uint type);
    [LibraryImport(Lib)] public static partial void OGR_G_DestroyGeometry(nint geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_Clone(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_GetLinearGeometry(H geometry, double maxAngleStep, nint options);
    [LibraryImport(Lib)] public static partial int OGR_G_ExportToIsoWkt(H geometry, nint* text);
    [LibraryImport(Lib)] public static partial nint OGR_G_ExportToJson(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_ExportToJsonEx(H geometry, nint options);
    [LibraryImport(Lib)] public static partial nint OGR_G_ExportToGML(H geometry);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OGR_G_ExportToKML(H geometry, string? altitudeMode);
    [LibraryImport(Lib)] public static partial int OGR_G_WkbSize(H geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_ExportToIsoWkb(H geometry, int byteOrder, byte* buffer);

    // ---------------------------------------------------------------- Geometry properties
    [LibraryImport(Lib)] public static partial uint OGR_G_GetGeometryType(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_GetGeometryName(H geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_GetDimension(H geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_CoordinateDimension(H geometry);
    [LibraryImport(Lib)] public static partial void OGR_G_GetEnvelope(H geometry, OGREnvelope* envelope);
    [LibraryImport(Lib)] public static partial int OGR_G_IsEmpty(H geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_IsValid(H geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_IsSimple(H geometry);
    [LibraryImport(Lib)] public static partial double OGR_G_Area(H geometry);
    [LibraryImport(Lib)] public static partial double OGR_G_Length(H geometry);
    [LibraryImport(Lib)] public static partial double OGR_G_Distance(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_GetPointCount(H geometry);
    [LibraryImport(Lib)] public static partial double OGR_G_GetX(H geometry, int i);
    [LibraryImport(Lib)] public static partial double OGR_G_GetY(H geometry, int i);
    [LibraryImport(Lib)] public static partial double OGR_G_GetZ(H geometry, int i);
    [LibraryImport(Lib)] public static partial int OGR_G_GetPoints(H geometry, void* x, int xStride, void* y, int yStride, void* z, int zStride);
    [LibraryImport(Lib)] public static partial void OGR_G_AddPoint_2D(H geometry, double x, double y);
    [LibraryImport(Lib)] public static partial void OGR_G_AddPoint(H geometry, double x, double y, double z);
    [LibraryImport(Lib)] public static partial void OGR_G_SetPoints(H geometry, int count, void* x, int xStride, void* y, int yStride, void* z, int zStride);
    [LibraryImport(Lib)] public static partial int OGR_G_GetGeometryCount(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_GetGeometryRef(H geometry, int index);
    [LibraryImport(Lib)] public static partial int OGR_G_AddGeometry(H geometry, H other);
    [LibraryImport(Lib)] public static partial int OGR_G_AddGeometryDirectly(H geometry, nint other);
    [LibraryImport(Lib)] public static partial void OGR_G_FlattenTo2D(H geometry);
    [LibraryImport(Lib)] public static partial void OGR_G_CloseRings(H geometry);
    [LibraryImport(Lib)] public static partial void OGR_G_Segmentize(H geometry, double maxLength);
    [LibraryImport(Lib)] public static partial nint OGR_G_GetSpatialReference(H geometry);
    [LibraryImport(Lib)] public static partial void OGR_G_AssignSpatialReference(H geometry, nint srs);
    [LibraryImport(Lib)] public static partial int OGR_G_Transform(H geometry, H transformation);
    [LibraryImport(Lib)] public static partial int OGR_G_TransformTo(H geometry, H srs);
    [LibraryImport(Lib)] public static partial uint OGR_GT_Flatten(uint type);

    // ---------------------------------------------------------------- Geometry predicates
    [LibraryImport(Lib)] public static partial int OGR_G_Intersects(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Equals(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Disjoint(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Touches(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Crosses(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Within(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Contains(H a, H b);
    [LibraryImport(Lib)] public static partial int OGR_G_Overlaps(H a, H b);

    // ---------------------------------------------------------------- Geometry operations (GEOS)
    [LibraryImport(Lib)] public static partial nint OGR_G_Buffer(H geometry, double distance, int quadSegs);
    [LibraryImport(Lib)] public static partial nint OGR_G_Intersection(H a, H b);
    [LibraryImport(Lib)] public static partial nint OGR_G_Union(H a, H b);
    [LibraryImport(Lib)] public static partial nint OGR_G_UnionCascaded(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_UnaryUnion(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_Difference(H a, H b);
    [LibraryImport(Lib)] public static partial nint OGR_G_SymDifference(H a, H b);
    [LibraryImport(Lib)] public static partial nint OGR_G_ConvexHull(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_ConcaveHull(H geometry, double ratio, int allowHoles);
    [LibraryImport(Lib)] public static partial nint OGR_G_Boundary(H geometry);
    [LibraryImport(Lib)] public static partial int OGR_G_Centroid(H geometry, H point);
    [LibraryImport(Lib)] public static partial nint OGR_G_PointOnSurface(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_Simplify(H geometry, double tolerance);
    [LibraryImport(Lib)] public static partial nint OGR_G_SimplifyPreserveTopology(H geometry, double tolerance);
    [LibraryImport(Lib)] public static partial nint OGR_G_MakeValid(H geometry);
    [LibraryImport(Lib)] public static partial nint OGR_G_DelaunayTriangulation(H geometry, double tolerance, int onlyEdges);
}
