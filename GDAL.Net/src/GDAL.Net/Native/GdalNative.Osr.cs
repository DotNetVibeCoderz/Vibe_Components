using System.Runtime.InteropServices;
using H = GDAL.Net.Native.GdalSafeHandle;

namespace GDAL.Net.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct OSRCRSInfo
{
    public nint pszAuthName;
    public nint pszCode;
    public nint pszName;
    public int eType;
    public int bDeprecated;
    public int bBboxValid;
    public double dfWestLongitudeDeg;
    public double dfSouthLatitudeDeg;
    public double dfEastLongitudeDeg;
    public double dfNorthLatitudeDeg;
    public nint pszAreaName;
    public nint pszProjectionMethod;
}

internal static unsafe partial class GdalNative
{
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OSRNewSpatialReference(string? wkt);
    [LibraryImport(Lib)] public static partial void OSRDestroySpatialReference(nint srs);
    [LibraryImport(Lib)] public static partial int OSRRelease(nint srs);
    [LibraryImport(Lib)] public static partial nint OSRClone(H srs);
    [LibraryImport(Lib)] public static partial nint OSRClone(nint srs);
    [LibraryImport(Lib)] public static partial int OSRImportFromEPSG(H srs, int code);
    [LibraryImport(Lib)] public static partial int OSRImportFromWkt(H srs, byte** wkt);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OSRImportFromProj4(H srs, string proj4);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OSRSetFromUserInput(H srs, string definition);
    [LibraryImport(Lib)] public static partial int OSRImportFromESRI(H srs, nint lines);
    [LibraryImport(Lib)] public static partial int OSRExportToWktEx(H srs, nint* result, nint options);
    [LibraryImport(Lib)] public static partial int OSRExportToPrettyWkt(H srs, nint* result, int simplify);
    [LibraryImport(Lib)] public static partial int OSRExportToProj4(H srs, nint* result);
    [LibraryImport(Lib)] public static partial int OSRExportToPROJJSON(H srs, nint* result, nint options);
    [LibraryImport(Lib)] public static partial int OSRMorphToESRI(H srs);
    [LibraryImport(Lib)] public static partial nint OSRGetName(H srs);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OSRGetAuthorityName(H srs, string? targetKey);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint OSRGetAuthorityCode(H srs, string? targetKey);
    [LibraryImport(Lib)] public static partial int OSRAutoIdentifyEPSG(H srs);
    [LibraryImport(Lib)] public static partial int OSRIsGeographic(H srs);
    [LibraryImport(Lib)] public static partial int OSRIsProjected(H srs);
    [LibraryImport(Lib)] public static partial int OSRIsGeocentric(H srs);
    [LibraryImport(Lib)] public static partial int OSRIsCompound(H srs);
    [LibraryImport(Lib)] public static partial int OSRIsSame(H a, H b);
    [LibraryImport(Lib)] public static partial void OSRSetAxisMappingStrategy(H srs, int strategy);
    [LibraryImport(Lib)] public static partial void OSRSetAxisMappingStrategy(nint srs, int strategy);
    [LibraryImport(Lib)] public static partial int OSRGetAxisMappingStrategy(H srs);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OSRSetWellKnownGeogCS(H srs, string name);
    [LibraryImport(Lib)] public static partial int OSRSetUTM(H srs, int zone, int north);
    [LibraryImport(Lib)] public static partial double OSRGetLinearUnits(H srs, nint* name);
    [LibraryImport(Lib)] public static partial double OSRGetAngularUnits(H srs, nint* name);
    [LibraryImport(Lib)] public static partial int OSRGetAreaOfUse(H srs, double* west, double* south, double* east, double* north, nint* areaName);
    [LibraryImport(Lib)] public static partial int OSRGetUTMZone(H srs, int* north);
    [LibraryImport(Lib)] public static partial int OSRValidate(H srs);
    [LibraryImport(Lib)] public static partial int OSRSetPROJSearchPaths(nint paths);
    [LibraryImport(Lib)] public static partial void OSRGetPROJVersion(int* major, int* minor, int* patch);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial OSRCRSInfo** OSRGetCRSInfoListFromDatabase(string? authName, nint parameters, int* count);
    [LibraryImport(Lib)] public static partial void OSRDestroyCRSInfoList(OSRCRSInfo** list);

    // ---------------------------------------------------------------- Coordinate transformation
    [LibraryImport(Lib)] public static partial nint OCTNewCoordinateTransformation(H source, H target);
    [LibraryImport(Lib)] public static partial nint OCTNewCoordinateTransformationEx(H source, H target, nint options);
    [LibraryImport(Lib)] public static partial void OCTDestroyCoordinateTransformation(nint ct);
    [LibraryImport(Lib)] public static partial int OCTTransformEx(H ct, int count, double* x, double* y, double* z, int* success);
    [LibraryImport(Lib)] public static partial int OCTTransform4D(H ct, int count, double* x, double* y, double* z, double* t, int* success);
    [LibraryImport(Lib)] public static partial int OCTTransformBounds(H ct, double minX, double minY, double maxX, double maxY, double* outMinX, double* outMinY, double* outMaxX, double* outMaxY, int densifyPoints);
    [LibraryImport(Lib)] public static partial nint OCTNewCoordinateTransformationOptions();
    [LibraryImport(Lib)] public static partial void OCTDestroyCoordinateTransformationOptions(nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int OCTCoordinateTransformationOptionsSetOperation(nint options, string pipeline, int reversible);
    [LibraryImport(Lib)] public static partial int OCTCoordinateTransformationOptionsSetAreaOfInterest(nint options, double west, double south, double east, double north);
    [LibraryImport(Lib)] public static partial int OCTCoordinateTransformationOptionsSetBallparkAllowed(nint options, int allowed);
    [LibraryImport(Lib)] public static partial int OCTCoordinateTransformationOptionsSetOnlyBest(nint options, int onlyBest);
}
