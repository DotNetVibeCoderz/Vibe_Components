using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using H = GDAL.Net.Native.GdalSafeHandle;

namespace GDAL.Net.Native;

/// <summary>
/// Raw P/Invoke declarations for the GDAL C API (gdal.h, cpl_*.h). Source-generated with LibraryImport
/// (no runtime marshalling stubs, trimming/AOT friendly). Handles of owned/borrowed objects are passed as
/// <see cref="GdalSafeHandle"/> so their owners stay alive for the duration of every call.
/// </summary>
internal static unsafe partial class GdalNative
{
    internal const string Lib = NativeLibraryLoader.GdalLibraryName;

    static GdalNative()
    {
        GdalRuntime.Bootstrap();
    }

    // ---------------------------------------------------------------- CPL / VSI memory
    [LibraryImport(Lib)] public static partial void VSIFree(nint ptr);
    [LibraryImport(Lib)] public static partial nint VSIMalloc(nuint size);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint CSLAddString(nint list, string item);
    [LibraryImport(Lib)] public static partial void CSLDestroy(nint list);
    [LibraryImport(Lib)] public static partial int CSLCount(nint list);

    // ---------------------------------------------------------------- Config / errors / version
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void CPLSetConfigOption(string key, string? value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void CPLSetThreadLocalConfigOption(string key, string? value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint CPLGetConfigOption(string key, string? defaultValue);
    [LibraryImport(Lib)] public static partial nint CPLGetLastErrorMsg();
    [LibraryImport(Lib)] public static partial int CPLGetLastErrorNo();
    [LibraryImport(Lib)] public static partial int CPLGetLastErrorType();
    [LibraryImport(Lib)] public static partial void CPLErrorReset();
    [LibraryImport(Lib)] public static partial nint CPLSetErrorHandler(nint handler);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALVersionInfo(string request);
    [LibraryImport(Lib)] public static partial int GDALCheckVersion(int major, int minor, nint programName);
    [LibraryImport(Lib)] public static partial void GDALAllRegister();
    [LibraryImport(Lib)] public static partial long GDALGetCacheMax64();
    [LibraryImport(Lib)] public static partial void GDALSetCacheMax64(long bytes);
    [LibraryImport(Lib)] public static partial long GDALGetCacheUsed64();
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void VSISetPathSpecificOption(string pathPrefix, string key, string? value);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void VSIClearPathSpecificOptions(string? pathPrefix);
    [LibraryImport(Lib)] public static partial void VSICurlClearCache();

    // ---------------------------------------------------------------- Drivers
    [LibraryImport(Lib)] public static partial int GDALGetDriverCount();
    [LibraryImport(Lib)] public static partial nint GDALGetDriver(int index);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGetDriverByName(string name);
    [LibraryImport(Lib)] public static partial nint GDALGetDriverShortName(nint driver);
    [LibraryImport(Lib)] public static partial nint GDALGetDriverLongName(nint driver);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALIdentifyDriver(string filename, nint fileList);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALDeleteDataset(nint driver, string filename);

    // ---------------------------------------------------------------- Major objects (metadata / description)
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGetMetadata(nint obj, string? domain);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGetMetadata(H obj, string? domain);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALSetMetadata(H obj, nint metadata, string? domain);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGetMetadataItem(nint obj, string name, string? domain);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint GDALGetMetadataItem(H obj, string name, string? domain);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int GDALSetMetadataItem(H obj, string name, string? value, string? domain);
    [LibraryImport(Lib)] public static partial nint GDALGetMetadataDomainList(H obj);
    [LibraryImport(Lib)] public static partial nint GDALGetDescription(H obj);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial void GDALSetDescription(H obj, string description);

    // ---------------------------------------------------------------- Error plumbing helpers

    /// <summary>Last error message for the calling thread (may be empty).</summary>
    public static string LastErrorMessage() => Interop.PtrToString(CPLGetLastErrorMsg()) ?? string.Empty;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static GdalException LastError(string fallback)
    {
        var msg = LastErrorMessage();
        var no = CPLGetLastErrorNo();
        CPLErrorReset();
        return new GdalException(string.IsNullOrWhiteSpace(msg) ? fallback : $"{fallback}: {msg}", no);
    }

    /// <summary>Throws when a CPLErr result is Failure or Fatal.</summary>
    public static void Check(int cplErr, string operation)
    {
        if (cplErr >= 3)
        {
            throw LastError(operation);
        }
    }

    /// <summary>Throws when an OGRErr result is not OGRERR_NONE.</summary>
    public static void CheckOgr(int ogrErr, string operation)
    {
        if (ogrErr != 0)
        {
            var msg = LastErrorMessage();
            CPLErrorReset();
            throw new GdalException($"{operation} failed ({(OgrError)ogrErr}){(string.IsNullOrWhiteSpace(msg) ? "" : ": " + msg)}", ogrErr);
        }
    }
}
