using System.Runtime.InteropServices;
using H = GDAL.Net.Native.GdalSafeHandle;

namespace GDAL.Net.Native;

internal static unsafe partial class GdalNative
{
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALCreateMultiDimensional(nint driver, string name, nint rootGroupOptions, nint options);

    [LibraryImport(Lib)] public static partial nint GDALDatasetGetRootGroup(H dataset);

    // Groups
    [LibraryImport(Lib)] public static partial void GDALGroupRelease(nint group);
    [LibraryImport(Lib)] public static partial nint GDALGroupGetName(H group);
    [LibraryImport(Lib)] public static partial nint GDALGroupGetFullName(H group);
    [LibraryImport(Lib)] public static partial nint GDALGroupGetMDArrayNames(H group, nint options);
    [LibraryImport(Lib)] public static partial nint GDALGroupGetGroupNames(H group, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGroupOpenMDArray(H group, string name, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGroupOpenGroup(H group, string name, nint options);
    [LibraryImport(Lib)] public static partial nint* GDALGroupGetDimensions(H group, nuint* count, nint options);
    [LibraryImport(Lib)] public static partial nint* GDALGroupGetAttributes(H group, nuint* count, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALGroupCreateGroup(H group, string name, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALGroupCreateDimension(H group, string name, string? type, string? direction, ulong size, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALGroupCreateMDArray(H group, string name, nuint dimensionCount, nint* dimensions, nint dataType, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALGroupCreateAttribute(H group, string name, nuint dimensionCount, ulong* sizes, nint dataType, nint options);

    // Arrays
    [LibraryImport(Lib)] public static partial void GDALMDArrayRelease(nint array);
    [LibraryImport(Lib)] public static partial nint GDALMDArrayGetName(H array);
    [LibraryImport(Lib)] public static partial nint GDALMDArrayGetFullName(H array);
    [LibraryImport(Lib)] public static partial ulong GDALMDArrayGetTotalElementsCount(H array);
    [LibraryImport(Lib)] public static partial nuint GDALMDArrayGetDimensionCount(H array);
    [LibraryImport(Lib)] public static partial nint* GDALMDArrayGetDimensions(H array, nuint* count);
    [LibraryImport(Lib)] public static partial nint GDALMDArrayGetDataType(H array);
    [LibraryImport(Lib)] public static partial nint GDALMDArrayGetUnit(H array);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALMDArraySetUnit(H array, string unit);
    [LibraryImport(Lib)] public static partial double GDALMDArrayGetNoDataValueAsDouble(H array, int* hasNoData);
    [LibraryImport(Lib)] public static partial int GDALMDArraySetNoDataValueAsDouble(H array, double value);
    [LibraryImport(Lib)] public static partial int GDALMDArraySetRawNoDataValue(H array, void* value);
    [LibraryImport(Lib)] public static partial double GDALMDArrayGetOffset(H array, int* hasValue);
    [LibraryImport(Lib)] public static partial double GDALMDArrayGetScale(H array, int* hasValue);
    [LibraryImport(Lib)] public static partial nint GDALMDArrayGetSpatialRef(H array);
    [LibraryImport(Lib)] public static partial nint* GDALMDArrayGetAttributes(H array, nuint* count, nint options);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GDALMDArrayCreateAttribute(H array, string name, nuint dimensionCount, ulong* sizes, nint dataType, nint options);
    [LibraryImport(Lib)] public static partial nint GDALMDArrayAsClassicDataset(H array, nuint xDim, nuint yDim);

    [LibraryImport(Lib)]
    public static partial int GDALMDArrayRead(H array, ulong* arrayStartIdx, nuint* count, long* arrayStep, nint* bufferStride, nint bufferDataType, void* dstBuffer, void* dstBufferAllocStart, nuint dstBufferAllocSize);

    [LibraryImport(Lib)]
    public static partial int GDALMDArrayWrite(H array, ulong* arrayStartIdx, nuint* count, long* arrayStep, nint* bufferStride, nint bufferDataType, void* srcBuffer, void* srcBufferAllocStart, nuint srcBufferAllocSize);

    // Dimensions
    [LibraryImport(Lib)] public static partial void GDALReleaseDimensions(nint* dimensions, nuint count);
    [LibraryImport(Lib)] public static partial void GDALDimensionRelease(nint dimension);
    [LibraryImport(Lib)] public static partial nint GDALDimensionGetName(nint dimension);
    [LibraryImport(Lib)] public static partial nint GDALDimensionGetType(nint dimension);
    [LibraryImport(Lib)] public static partial nint GDALDimensionGetDirection(nint dimension);
    [LibraryImport(Lib)] public static partial ulong GDALDimensionGetSize(nint dimension);
    [LibraryImport(Lib)] public static partial nint GDALDimensionGetIndexingVariable(nint dimension);
    [LibraryImport(Lib)] public static partial int GDALDimensionSetIndexingVariable(nint dimension, H array);

    // Attributes
    [LibraryImport(Lib)] public static partial void GDALReleaseAttributes(nint* attributes, nuint count);
    [LibraryImport(Lib)] public static partial void GDALAttributeRelease(nint attribute);
    [LibraryImport(Lib)] public static partial nint GDALAttributeGetName(nint attribute);
    [LibraryImport(Lib)] public static partial nint GDALAttributeReadAsString(nint attribute);
    [LibraryImport(Lib)] public static partial double GDALAttributeReadAsDouble(nint attribute);
    [LibraryImport(Lib)] public static partial nint GDALAttributeGetDataType(nint attribute);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALAttributeWriteString(nint attribute, string value);
    [LibraryImport(Lib)] public static partial int GDALAttributeWriteDouble(nint attribute, double value);

    // Extended data types
    [LibraryImport(Lib)] public static partial nint GDALExtendedDataTypeCreate(int dataType);
    [LibraryImport(Lib)] public static partial nint GDALExtendedDataTypeCreateString(nuint maxLength);
    [LibraryImport(Lib)] public static partial void GDALExtendedDataTypeRelease(nint type);
    [LibraryImport(Lib)] public static partial int GDALExtendedDataTypeGetClass(nint type);
    [LibraryImport(Lib)] public static partial int GDALExtendedDataTypeGetNumericDataType(nint type);
}
