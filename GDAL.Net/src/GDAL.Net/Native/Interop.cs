using System.Runtime.InteropServices;
using System.Text;

namespace GDAL.Net.Native;

/// <summary>Low-level helpers for UTF-8 strings and GDAL string lists (char**).</summary>
internal static unsafe class Interop
{
    /// <summary>Reads a GDAL-owned (const) UTF-8 string. Never frees it.</summary>
    public static string? PtrToString(nint ptr) => ptr == 0 ? null : Marshal.PtrToStringUTF8(ptr);

    /// <summary>Reads a UTF-8 string allocated by GDAL and frees it with VSIFree.</summary>
    public static string? TakeString(nint ptr)
    {
        if (ptr == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            GdalNative.VSIFree(ptr);
        }
    }

    /// <summary>Reads a NULL terminated char** list without freeing it.</summary>
    public static string[] PtrToStringList(nint list)
    {
        if (list == 0) return [];
        var p = (nint*)list;
        var count = 0;
        while (p[count] != 0) count++;
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Marshal.PtrToStringUTF8(p[i]) ?? string.Empty;
        }
        return result;
    }

    /// <summary>Reads then destroys (CSLDestroy) a char** list owned by the caller.</summary>
    public static string[] TakeStringList(nint list)
    {
        if (list == 0) return [];
        try
        {
            return PtrToStringList(list);
        }
        finally
        {
            GdalNative.CSLDestroy(list);
        }
    }

    /// <summary>Parses a "KEY=VALUE" list into a dictionary.</summary>
    public static Dictionary<string, string> PtrToKeyValues(nint list)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in PtrToStringList(list))
        {
            var idx = item.IndexOf('=');
            if (idx < 0)
            {
                idx = item.IndexOf(':');
            }
            if (idx < 0)
            {
                dict[item] = string.Empty;
            }
            else
            {
                dict[item[..idx]] = item[(idx + 1)..];
            }
        }
        return dict;
    }
}

/// <summary>
/// A NULL terminated char** list allocated with GDAL's own allocator (CSLAddString) so it can be handed to
/// any GDAL function, including those that take ownership. Disposing frees it unless ownership was released.
/// </summary>
internal struct NativeStringList : IDisposable
{
    private nint _list;

    public NativeStringList(IEnumerable<string>? items)
    {
        _list = 0;
        if (items is null) return;
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public static NativeStringList FromOptions(IEnumerable<KeyValuePair<string, string>>? options)
    {
        var list = new NativeStringList(null);
        if (options is null) return list;
        foreach (var kv in options)
        {
            list.Add($"{kv.Key}={kv.Value}");
        }
        return list;
    }

    public readonly nint Pointer => _list;

    public void Add(string item) => _list = GdalNative.CSLAddString(_list, item);

    /// <summary>Gives the list to native code that takes ownership.</summary>
    public nint Release()
    {
        var l = _list;
        _list = 0;
        return l;
    }

    public void Dispose()
    {
        if (_list != 0)
        {
            GdalNative.CSLDestroy(_list);
            _list = 0;
        }
    }
}

/// <summary>Stack/pool friendly UTF-8 encoder for hot paths that pass a string by pointer.</summary>
internal static class Utf8
{
    public static byte[] Encode(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }
}
