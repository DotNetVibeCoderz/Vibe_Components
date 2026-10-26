using GDAL.Net.Native;

namespace GDAL.Net.Vsi;

/// <summary>
/// GDAL Virtual File Systems: in-memory files (/vsimem/), archives (/vsizip/, /vsitar/, /vsigzip/, /vsi7z/),
/// HTTP(S)/FTP (/vsicurl/), cloud object stores (/vsis3/, /vsiaz/, /vsiadls/, /vsigs/, /vsioss/, /vsiswift/)
/// and streaming variants (/vsicurl_streaming/, /vsis3_streaming/, ...).
/// </summary>
public static unsafe class VirtualFileSystem
{
    /// <summary>Prefixes supported by this GDAL build.</summary>
    public static IReadOnlyList<string> Prefixes => Interop.TakeStringList(GdalNative.VSIGetFileSystemsPrefixes());

    /// <summary>Creates a unique /vsimem/ path (e.g. for temporary outputs).</summary>
    public static string NewMemoryPath(string extension = "") => $"/vsimem/gdalnet/{Guid.NewGuid():N}{(extension.Length > 0 && extension[0] != '.' ? "." : "")}{extension}";

    /// <summary>Creates an in-memory file holding a copy of <paramref name="data"/>.</summary>
    public static void WriteMemoryFile(string path, ReadOnlySpan<byte> data)
    {
        var buffer = (byte*)GdalNative.VSIMalloc((nuint)Math.Max(1, data.Length));
        if (buffer == null) throw new OutOfMemoryException();
        data.CopyTo(new Span<byte>(buffer, data.Length));
        var fp = GdalNative.VSIFileFromMemBuffer(path, buffer, (ulong)data.Length, 1);
        if (fp == 0)
        {
            GdalNative.VSIFree((nint)buffer);
            throw GdalNative.LastError($"Cannot create memory file '{path}'");
        }
        GdalNative.VSIFCloseL(fp);
    }

    /// <summary>Reads the content of an in-memory file (copied). Optionally unlinks it.</summary>
    public static byte[] ReadMemoryFile(string path, bool unlink = false)
    {
        ulong length;
        var p = GdalNative.VSIGetMemFileBuffer(path, &length, unlink ? 1 : 0);
        if (p == null) throw new FileNotFoundException($"Memory file '{path}' does not exist.", path);
        try
        {
            return new ReadOnlySpan<byte>(p, checked((int)length)).ToArray();
        }
        finally
        {
            if (unlink) GdalNative.VSIFree((nint)p);
        }
    }

    /// <summary>Reads a whole file from any virtual file system.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads a whole text file (UTF-8) from any virtual file system.</summary>
    public static string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <summary>Writes a whole file to any writable virtual file system.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        using var stream = OpenWrite(path);
        stream.Write(data);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>True when a file or directory exists.</summary>
    public static bool Exists(string path)
    {
        // VSIStatBufL is platform dependent; a generous buffer avoids depending on its exact layout.
        var buffer = stackalloc byte[512];
        return GdalNative.VSIStatExL(path, buffer, 0) == 0;
    }

    public static bool Delete(string path) => GdalNative.VSIUnlink(path) == 0;

    public static bool Rename(string from, string to) => GdalNative.VSIRename(from, to) == 0;

    public static bool CreateDirectory(string path, bool recursive = true)
        => (recursive ? GdalNative.VSIMkdirRecursive(path, 0x1ED) : GdalNative.VSIMkdir(path, 0x1ED)) == 0;

    public static bool DeleteDirectory(string path) => GdalNative.VSIRmdirRecursive(path) == 0;

    /// <summary>Lists a directory (works inside archives and cloud buckets too).</summary>
    public static IReadOnlyList<string> ListDirectory(string path, bool recursive = false)
        => Interop.TakeStringList(recursive ? GdalNative.VSIReadDirRecursive(path) : GdalNative.VSIReadDir(path))
            .Where(n => n is not "." and not "..").ToArray();

    /// <summary>Copies a file between any two virtual file systems (e.g. /vsicurl/ → local, local → /vsis3/).</summary>
    public static void CopyFile(string source, string target, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var bridge = ProgressBridge.Create(progress, cancellationToken);
        var err = GdalNative.VSICopyFile(source, target, 0, ulong.MaxValue, 0, ProgressBridge.Function(bridge), ProgressBridge.Data(bridge));
        ProgressBridge.ThrowIfCancelled(bridge, cancellationToken);
        if (err != 0) throw GdalNative.LastError($"Copy '{source}' → '{target}'");
    }

    public static VsiStream OpenRead(string path) => VsiStream.Open(path, "rb");
    public static VsiStream OpenWrite(string path) => VsiStream.Open(path, "wb");

    /// <summary>Clears the /vsicurl/ cache (after remote files changed).</summary>
    public static void ClearCurlCache() => GdalNative.VSICurlClearCache();

    // ------------------------------------------------------------------ path builders

    public static string Zip(string archivePath, string? inner = null) => Combine("/vsizip/", archivePath, inner);
    public static string Tar(string archivePath, string? inner = null) => Combine("/vsitar/", archivePath, inner);
    public static string Gzip(string path) => "/vsigzip/" + path;
    public static string Curl(string url) => "/vsicurl/" + url;
    public static string S3(string bucket, string key) => $"/vsis3/{bucket}/{key}";
    public static string Azure(string container, string blob) => $"/vsiaz/{container}/{blob}";
    public static string GoogleCloud(string bucket, string key) => $"/vsigs/{bucket}/{key}";

    private static string Combine(string prefix, string archive, string? inner)
    {
        archive = archive.Replace('\\', '/');
        return inner is null ? prefix + archive : $"{prefix}{archive}/{inner.TrimStart('/')}";
    }

    // ------------------------------------------------------------------ cloud credentials helpers

    /// <summary>Configures AWS S3 access (globally, or for a bucket prefix such as "/vsis3/my-bucket").</summary>
    public static void ConfigureS3(string? accessKeyId, string? secretAccessKey, string? region = null, string? endpoint = null, bool noSign = false, string? pathPrefix = null)
    {
        Set(pathPrefix, "AWS_ACCESS_KEY_ID", accessKeyId);
        Set(pathPrefix, "AWS_SECRET_ACCESS_KEY", secretAccessKey);
        if (region is not null) Set(pathPrefix, "AWS_REGION", region);
        if (endpoint is not null) Set(pathPrefix, "AWS_S3_ENDPOINT", endpoint);
        if (noSign) Set(pathPrefix, "AWS_NO_SIGN_REQUEST", "YES");
    }

    /// <summary>Configures Azure Blob access with a connection string or account/key.</summary>
    public static void ConfigureAzure(string? connectionString = null, string? account = null, string? accessKey = null, string? sasToken = null, string? pathPrefix = null)
    {
        if (connectionString is not null) Set(pathPrefix, "AZURE_STORAGE_CONNECTION_STRING", connectionString);
        if (account is not null) Set(pathPrefix, "AZURE_STORAGE_ACCOUNT", account);
        if (accessKey is not null) Set(pathPrefix, "AZURE_STORAGE_ACCESS_KEY", accessKey);
        if (sasToken is not null) Set(pathPrefix, "AZURE_STORAGE_SAS_TOKEN", sasToken);
    }

    /// <summary>Configures Google Cloud Storage access (service account JSON, HMAC keys or anonymous).</summary>
    public static void ConfigureGoogleCloud(string? applicationCredentialsFile = null, string? accessKeyId = null, string? secretAccessKey = null, bool noSign = false, string? pathPrefix = null)
    {
        if (applicationCredentialsFile is not null) Set(pathPrefix, "GOOGLE_APPLICATION_CREDENTIALS", applicationCredentialsFile);
        if (accessKeyId is not null) Set(pathPrefix, "GS_ACCESS_KEY_ID", accessKeyId);
        if (secretAccessKey is not null) Set(pathPrefix, "GS_SECRET_ACCESS_KEY", secretAccessKey);
        if (noSign) Set(pathPrefix, "GS_NO_SIGN_REQUEST", "YES");
    }

    private static void Set(string? prefix, string key, string? value)
    {
        if (prefix is null) Gdal.SetConfigOption(key, value);
        else Gdal.SetPathSpecificOption(prefix, key, value);
    }
}

/// <summary>A <see cref="Stream"/> over any GDAL virtual file (VSILFILE*).</summary>
public sealed unsafe class VsiStream : Stream
{
    private nint _file;
    private readonly bool _canWrite;

    private VsiStream(nint file, bool canWrite)
    {
        _file = file;
        _canWrite = canWrite;
    }

    /// <summary>Opens a virtual file. Access follows fopen ("rb", "wb", "r+b", "ab").</summary>
    public static VsiStream Open(string path, string access = "rb")
    {
        var fp = GdalNative.VSIFOpenL(path, access);
        return fp == 0 ? throw new FileNotFoundException($"Cannot open '{path}' ({access}): {GdalNative.LastErrorMessage()}", path) : new VsiStream(fp, access.Contains('w') || access.Contains('a') || access.Contains('+'));
    }

    private nint File => _file == 0 ? throw new ObjectDisposedException(nameof(VsiStream)) : _file;

    public override bool CanRead => _file != 0;
    public override bool CanSeek => _file != 0;
    public override bool CanWrite => _file != 0 && _canWrite;

    public override long Length
    {
        get
        {
            var pos = GdalNative.VSIFTellL(File);
            GdalNative.VSIFSeekL(File, 0, 2);
            var len = GdalNative.VSIFTellL(File);
            GdalNative.VSIFSeekL(File, pos, 0);
            return (long)len;
        }
    }

    public override long Position
    {
        get => (long)GdalNative.VSIFTellL(File);
        set => GdalNative.VSIFSeekL(File, (ulong)value, 0);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        fixed (byte* p = buffer)
        {
            return (int)GdalNative.VSIFReadL(p, 1, (nuint)buffer.Length, File);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty) return;
        fixed (byte* p = buffer)
        {
            var written = GdalNative.VSIFWriteL(p, 1, (nuint)buffer.Length, File);
            if (written != (nuint)buffer.Length) throw new IOException($"VSI write failed: {GdalNative.LastErrorMessage()}");
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        GdalNative.VSIFSeekL(File, (ulong)target, 0);
        return target;
    }

    public override void SetLength(long value) => GdalNative.VSIFTruncateL(File, (ulong)value);

    public override void Flush() => GdalNative.VSIFFlushL(File);

    protected override void Dispose(bool disposing)
    {
        var f = Interlocked.Exchange(ref _file, 0);
        if (f != 0) GdalNative.VSIFCloseL(f);
        base.Dispose(disposing);
    }

    ~VsiStream() => Dispose(false);
}
