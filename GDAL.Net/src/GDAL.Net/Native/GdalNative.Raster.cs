using System.Runtime.InteropServices;
using H = GDAL.Net.Native.GdalSafeHandle;

namespace GDAL.Net.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct GDALRasterIOExtraArg
{
    public int nVersion;
    public int eResampleAlg;
    public nint pfnProgress;
    public nint pProgressData;
    public int bFloatingPointWindowValidity;
    public double dfXOff;
    public double dfYOff;
    public double dfXSize;
    public double dfYSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GDALColorEntry
{
    public short c1, c2, c3, c4;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GDAL_GCP
{
    public nint pszId;
    public nint pszInfo;
    public double dfGCPPixel;
    public double dfGCPLine;
    public double dfGCPX;
    public double dfGCPY;
    public double dfGCPZ;
}

internal static unsafe partial class GdalNative
{
    // ---------------------------------------------------------------- Dataset lifecycle
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALOpenEx(string filename, uint openFlags, nint allowedDrivers, nint openOptions, nint siblingFiles);

    [LibraryImport(Lib)] public static partial int GDALClose(nint dataset);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALCreate(nint driver, string filename, int xSize, int ySize, int bands, int dataType, nint options);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALCreateCopy(nint driver, string filename, H source, int strict, nint options, nint progress, nint progressData);

    [LibraryImport(Lib)] public static partial nint GDALGetDatasetDriver(H dataset);
    [LibraryImport(Lib)] public static partial int GDALFlushCache(H dataset);
    [LibraryImport(Lib)] public static partial nint GDALGetFileList(H dataset);

    // ---------------------------------------------------------------- Dataset raster properties
    [LibraryImport(Lib)] public static partial int GDALGetRasterXSize(H dataset);
    [LibraryImport(Lib)] public static partial int GDALGetRasterYSize(H dataset);
    [LibraryImport(Lib)] public static partial int GDALGetRasterCount(H dataset);
    [LibraryImport(Lib)] public static partial nint GDALGetRasterBand(H dataset, int band);
    [LibraryImport(Lib)] public static partial int GDALGetGeoTransform(H dataset, double* transform);
    [LibraryImport(Lib)] public static partial int GDALSetGeoTransform(H dataset, double* transform);
    [LibraryImport(Lib)] public static partial nint GDALGetSpatialRef(H dataset);
    [LibraryImport(Lib)] public static partial int GDALSetSpatialRef(H dataset, H srs);
    [LibraryImport(Lib)] public static partial int GDALSetSpatialRef(H dataset, nint srs);
    [LibraryImport(Lib)] public static partial nint GDALGetProjectionRef(H dataset);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALSetProjection(H dataset, string wkt);

    [LibraryImport(Lib)] public static partial int GDALGetGCPCount(H dataset);
    [LibraryImport(Lib)] public static partial GDAL_GCP* GDALGetGCPs(H dataset);
    [LibraryImport(Lib)] public static partial nint GDALGetGCPSpatialRef(H dataset);
    [LibraryImport(Lib)] public static partial int GDALSetGCPs2(H dataset, int count, GDAL_GCP* gcps, nint srs);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GDALBuildOverviews(H dataset, string resampling, int overviewCount, int* overviewList, int bandCount, int* bandList, nint progress, nint progressData);

    [LibraryImport(Lib)]
    public static partial int GDALDatasetRasterIOEx(H dataset, int rwFlag, int xOff, int yOff, int xSize, int ySize, void* buffer, int bufXSize, int bufYSize, int bufType, int bandCount, int* bandMap, long pixelSpace, long lineSpace, long bandSpace, GDALRasterIOExtraArg* extra);

    [LibraryImport(Lib)] public static partial int GDALAddBand(H dataset, int dataType, nint options);

    // ---------------------------------------------------------------- Bands
    [LibraryImport(Lib)] public static partial int GDALGetRasterDataType(H band);
    [LibraryImport(Lib)] public static partial int GDALGetRasterBandXSize(H band);
    [LibraryImport(Lib)] public static partial int GDALGetRasterBandYSize(H band);
    [LibraryImport(Lib)] public static partial int GDALGetBandNumber(H band);
    [LibraryImport(Lib)] public static partial void GDALGetBlockSize(H band, int* xSize, int* ySize);

    [LibraryImport(Lib)]
    public static partial int GDALRasterIOEx(H band, int rwFlag, int xOff, int yOff, int xSize, int ySize, void* buffer, int bufXSize, int bufYSize, int bufType, long pixelSpace, long lineSpace, GDALRasterIOExtraArg* extra);

    [LibraryImport(Lib)] public static partial double GDALGetRasterNoDataValue(H band, int* success);
    [LibraryImport(Lib)] public static partial int GDALSetRasterNoDataValue(H band, double value);
    [LibraryImport(Lib)] public static partial int GDALDeleteRasterNoDataValue(H band);
    [LibraryImport(Lib)] public static partial int GDALGetRasterColorInterpretation(H band);
    [LibraryImport(Lib)] public static partial int GDALSetRasterColorInterpretation(H band, int interp);
    [LibraryImport(Lib)] public static partial double GDALGetRasterOffset(H band, int* success);
    [LibraryImport(Lib)] public static partial double GDALGetRasterScale(H band, int* success);
    [LibraryImport(Lib)] public static partial int GDALSetRasterOffset(H band, double value);
    [LibraryImport(Lib)] public static partial int GDALSetRasterScale(H band, double value);
    [LibraryImport(Lib)] public static partial nint GDALGetRasterUnitType(H band);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALSetRasterUnitType(H band, string unit);
    [LibraryImport(Lib)] public static partial int GDALGetMaskFlags(H band);
    [LibraryImport(Lib)] public static partial nint GDALGetMaskBand(H band);
    [LibraryImport(Lib)] public static partial int GDALFillRaster(H band, double real, double imaginary);
    [LibraryImport(Lib)] public static partial int GDALGetOverviewCount(H band);
    [LibraryImport(Lib)] public static partial nint GDALGetOverview(H band, int index);
    [LibraryImport(Lib)] public static partial int GDALChecksumImage(H band, int xOff, int yOff, int xSize, int ySize);

    [LibraryImport(Lib)]
    public static partial int GDALComputeRasterStatistics(H band, int approxOk, double* min, double* max, double* mean, double* stdDev, nint progress, nint progressData);

    [LibraryImport(Lib)]
    public static partial int GDALGetRasterStatistics(H band, int approxOk, int force, double* min, double* max, double* mean, double* stdDev);

    [LibraryImport(Lib)] public static partial int GDALComputeRasterMinMax(H band, int approxOk, double* minMax);

    [LibraryImport(Lib)]
    public static partial int GDALGetRasterHistogramEx(H band, double min, double max, int buckets, ulong* histogram, int includeOutOfRange, int approxOk, nint progress, nint progressData);

    // ---------------------------------------------------------------- Color tables
    [LibraryImport(Lib)] public static partial nint GDALGetRasterColorTable(H band);
    [LibraryImport(Lib)] public static partial int GDALSetRasterColorTable(H band, H colorTable);
    [LibraryImport(Lib)] public static partial nint GDALCreateColorTable(int paletteInterp);
    [LibraryImport(Lib)] public static partial void GDALDestroyColorTable(nint table);
    [LibraryImport(Lib)] public static partial nint GDALCloneColorTable(H table);
    [LibraryImport(Lib)] public static partial int GDALGetColorEntryCount(H table);
    [LibraryImport(Lib)] public static partial GDALColorEntry* GDALGetColorEntry(H table, int index);
    [LibraryImport(Lib)] public static partial void GDALSetColorEntry(H table, int index, GDALColorEntry* entry);
    [LibraryImport(Lib)] public static partial int GDALGetPaletteInterpretation(H table);

    // ---------------------------------------------------------------- Raster attribute tables
    [LibraryImport(Lib)] public static partial nint GDALGetDefaultRAT(H band);
    [LibraryImport(Lib)] public static partial int GDALSetDefaultRAT(H band, H rat);
    [LibraryImport(Lib)] public static partial nint GDALCreateRasterAttributeTable();
    [LibraryImport(Lib)] public static partial void GDALDestroyRasterAttributeTable(nint rat);
    [LibraryImport(Lib)] public static partial int GDALRATGetColumnCount(H rat);
    [LibraryImport(Lib)] public static partial nint GDALRATGetNameOfCol(H rat, int col);
    [LibraryImport(Lib)] public static partial int GDALRATGetTypeOfCol(H rat, int col);
    [LibraryImport(Lib)] public static partial int GDALRATGetUsageOfCol(H rat, int col);
    [LibraryImport(Lib)] public static partial int GDALRATGetRowCount(H rat);
    [LibraryImport(Lib)] public static partial void GDALRATSetRowCount(H rat, int rows);
    [LibraryImport(Lib)] public static partial nint GDALRATGetValueAsString(H rat, int row, int col);
    [LibraryImport(Lib)] public static partial int GDALRATGetValueAsInt(H rat, int row, int col);
    [LibraryImport(Lib)] public static partial double GDALRATGetValueAsDouble(H rat, int row, int col);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALRATCreateColumn(H rat, string name, int type, int usage);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void GDALRATSetValueAsString(H rat, int row, int col, string value);
    [LibraryImport(Lib)] public static partial void GDALRATSetValueAsInt(H rat, int row, int col, int value);
    [LibraryImport(Lib)] public static partial void GDALRATSetValueAsDouble(H rat, int row, int col, double value);

    // ---------------------------------------------------------------- Algorithms (gdal_alg.h)
    [LibraryImport(Lib)]
    public static partial int GDALContourGenerateEx(H band, H layer, nint options, nint progress, nint progressData);

    [LibraryImport(Lib)]
    public static partial int GDALPolygonize(H band, nint maskBand, H layer, int fieldIndex, nint options, nint progress, nint progressData);

    [LibraryImport(Lib)]
    public static partial int GDALFPolygonize(H band, nint maskBand, H layer, int fieldIndex, nint options, nint progress, nint progressData);

    [LibraryImport(Lib)]
    public static partial int GDALFillNodata(H targetBand, nint maskBand, double maxSearchDist, int deprecated, int smoothingIterations, nint options, nint progress, nint progressData);

    [LibraryImport(Lib)]
    public static partial int GDALSieveFilter(H srcBand, nint maskBand, H dstBand, int sizeThreshold, int connectedness, nint options, nint progress, nint progressData);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALViewshedGenerate(H band, string driverName, string targetRasterName, nint creationOptions,
        double observerX, double observerY, double observerHeight, double targetHeight,
        double visibleVal, double invisibleVal, double outOfRangeVal, double noDataVal, double curvCoeff,
        int mode, double maxDistance, nint progress, nint progressData, int heightMode, nint extraOptions);
}
