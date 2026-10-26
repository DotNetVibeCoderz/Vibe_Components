using System.Runtime.InteropServices;
using H = GDAL.Net.Native.GdalSafeHandle;

namespace GDAL.Net.Native;

internal static unsafe partial class GdalNative
{
    // ---------------------------------------------------------------- gdal_utils.h
    [LibraryImport(Lib)] public static partial nint GDALInfoOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALInfoOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial nint GDALInfo(H dataset, nint options);

    [LibraryImport(Lib)] public static partial nint GDALVectorInfoOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALVectorInfoOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial nint GDALVectorInfo(H dataset, nint options);

    [LibraryImport(Lib)] public static partial nint GDALTranslateOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALTranslateOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALTranslateOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALTranslate(string destination, H source, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALWarpAppOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALWarpAppOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALWarpAppOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALWarp(string? destination, nint destDataset, int sourceCount, nint* sources, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALVectorTranslateOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALVectorTranslateOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALVectorTranslateOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALVectorTranslate(string? destination, nint destDataset, int sourceCount, nint* sources, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALDEMProcessingOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALDEMProcessingOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALDEMProcessingOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALDEMProcessing(string destination, H source, string processing, string? colorFile, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALBuildVRTOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALBuildVRTOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALBuildVRTOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALBuildVRT(string destination, int sourceCount, nint* sources, nint sourceNames, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALRasterizeOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALRasterizeOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALRasterizeOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALRasterize(string? destination, nint destDataset, H source, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALGridOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALGridOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial void GDALGridOptionsSetProgress(nint options, nint progress, nint progressData);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGrid(string destination, H source, nint options, int* usageError);

    [LibraryImport(Lib)] public static partial nint GDALMultiDimInfoOptionsNew(nint argv, nint binaryOptions);
    [LibraryImport(Lib)] public static partial void GDALMultiDimInfoOptionsFree(nint options);
    [LibraryImport(Lib)] public static partial nint GDALMultiDimInfo(H dataset, nint options);

    // ---------------------------------------------------------------- GDAL algorithm API ("gdal" CLI, GDAL >= 3.11)
    [LibraryImport(Lib)] public static partial nint GDALGetGlobalAlgorithmRegistry();
    [LibraryImport(Lib)] public static partial void GDALAlgorithmRegistryRelease(nint registry);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmRegistryGetAlgNames(nint registry);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALAlgorithmRegistryInstantiateAlg(nint registry, string name);
    [LibraryImport(Lib)] public static partial void GDALAlgorithmRelease(nint algorithm);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmGetName(nint algorithm);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmGetDescription(nint algorithm);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmGetSubAlgorithmNames(nint algorithm);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmGetUsageAsJSON(nint algorithm);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALAlgorithmInstantiateSubAlgorithm(nint algorithm, string name);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool GDALAlgorithmParseCommandLineArguments(nint algorithm, nint args);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmGetActualAlgorithm(nint algorithm);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool GDALAlgorithmRun(nint algorithm, nint progress, nint progressData);
    [LibraryImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static partial bool GDALAlgorithmFinalize(nint algorithm);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALAlgorithmGetArg(nint algorithm, string argName);
    [LibraryImport(Lib)] public static partial void GDALAlgorithmArgRelease(nint arg);
    [LibraryImport(Lib)] public static partial int GDALAlgorithmArgGetType(nint arg);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmArgGetAsString(nint arg);
    [LibraryImport(Lib)] public static partial nint GDALAlgorithmArgGetAsDatasetValue(nint arg);
    [LibraryImport(Lib)] public static partial nint GDALArgDatasetValueGetDatasetIncreaseRefCount(nint value);
    [LibraryImport(Lib)] public static partial void GDALArgDatasetValueRelease(nint value);

    // ---------------------------------------------------------------- VSI (virtual file systems)
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint VSIFileFromMemBuffer(string filename, byte* data, ulong length, int takeOwnership);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial byte* VSIGetMemFileBuffer(string filename, ulong* length, int unlinkAndSeize);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSIUnlink(string filename);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSIRename(string oldPath, string newPath);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSIMkdir(string path, int mode);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSIMkdirRecursive(string path, int mode);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSIRmdirRecursive(string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint VSIReadDir(string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint VSIReadDirRecursive(string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSIStatExL(string path, byte* statBuffer, int flags);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint VSIFOpenL(string filename, string access);
    [LibraryImport(Lib)] public static partial int VSIFCloseL(nint file);
    [LibraryImport(Lib)] public static partial int VSIFSeekL(nint file, ulong offset, int whence);
    [LibraryImport(Lib)] public static partial ulong VSIFTellL(nint file);
    [LibraryImport(Lib)] public static partial nuint VSIFReadL(void* buffer, nuint size, nuint count, nint file);
    [LibraryImport(Lib)] public static partial nuint VSIFWriteL(void* buffer, nuint size, nuint count, nint file);
    [LibraryImport(Lib)] public static partial int VSIFFlushL(nint file);
    [LibraryImport(Lib)] public static partial int VSIFTruncateL(nint file, ulong newSize);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint VSIGetFileSystemsPrefixes();
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int VSICopyFile(string? source, string target, nint fpSource, ulong sourceSize, nint options, nint progress, nint progressData);
}
