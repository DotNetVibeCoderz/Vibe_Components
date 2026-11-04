using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.IO;

public sealed class ReadOptions
{
    /// <summary>Directory used to resolve relative references (materials, textures, paged files).</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Load referenced textures (OBJ/MTL map_Kd, scene files).</summary>
    public bool LoadTextures { get; init; } = true;

    /// <summary>Compute smooth normals when the file has none.</summary>
    public bool GenerateNormals { get; init; } = true;

    public static ReadOptions Default { get; } = new();
}

/// <summary>A model format plugin (<c>osgDB::ReaderWriter</c>).</summary>
public interface IReaderWriter
{
    string Name { get; }

    /// <summary>Lower-case extensions without the dot, e.g. "obj".</summary>
    IReadOnlyList<string> Extensions { get; }
    bool CanRead { get; }
    bool CanWrite { get; }
    Node ReadNode(Stream stream, ReadOptions options);
    void WriteNode(Node node, Stream stream);
}

/// <summary>An image format plugin.</summary>
public interface IImageReader
{
    IReadOnlyList<string> Extensions { get; }
    Image ReadImage(Stream stream);
}

/// <summary>
/// Plugin registry and convenience readers (<c>osgDB::Registry</c>, <c>osgDB::readNodeFile</c>).
/// Built-in managed plugins: OBJ(+MTL), STL, PLY, OSGN (JSON scene), ESRI ASCII grid, GeoTIFF (uncompressed) and
/// images (PNG, TGA, BMP, PPM/PGM). The native package adds every OpenSceneGraph plugin
/// (.osg/.osgt/.osgb/.ive/.3ds/.dae/.flt/.dxf/.fbx…) through <see cref="NativeFallback"/>.
/// </summary>
public static class Registry
{
    private static readonly Lock s_lock = new();
    private static readonly List<IReaderWriter> s_writers = [new ObjReaderWriter(), new StlReaderWriter(), new PlyReader(), new OsgnReaderWriter(), new HeightFieldReaderWriter()];
    private static readonly List<IImageReader> s_images = [new PngReader(), new SimpleImageReaders()];

    /// <summary>Called for extensions no managed plugin handles. Installed by <c>OpenSceneGraph.Net.Native</c>.</summary>
    public static Func<string, ReadOptions, Node?>? NativeFallback { get; set; }

    public static IReadOnlyList<IReaderWriter> ReaderWriters { get { lock (s_lock) return s_writers.ToArray(); } }
    public static IReadOnlyList<IImageReader> ImageReaders { get { lock (s_lock) return s_images.ToArray(); } }

    public static void Register(IReaderWriter rw) { lock (s_lock) s_writers.Insert(0, rw); }
    public static void Register(IImageReader r) { lock (s_lock) s_images.Insert(0, r); }

    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static IReaderWriter? FindReaderWriter(string extension)
    {
        lock (s_lock) return s_writers.FirstOrDefault(r => r.Extensions.Contains(extension));
    }

    public static IImageReader? FindImageReader(string extension)
    {
        lock (s_lock) return s_images.FirstOrDefault(r => r.Extensions.Contains(extension));
    }

    /// <summary>Reads a model file. Throws <see cref="NotSupportedException"/> when no plugin handles the extension.</summary>
    public static Node ReadNodeFile(string path, ReadOptions? options = null)
    {
        options ??= new ReadOptions { DatabasePath = Path.GetDirectoryName(Path.GetFullPath(path)) };
        if (options.DatabasePath is null) options = new ReadOptions { DatabasePath = Path.GetDirectoryName(Path.GetFullPath(path)), LoadTextures = options.LoadTextures, GenerateNormals = options.GenerateNormals };
        var ext = Extension(path);
        var rw = FindReaderWriter(ext);
        if (rw is { CanRead: true })
        {
            using var fs = File.OpenRead(path);
            var node = rw.ReadNode(fs, options);
            node.Name ??= Path.GetFileName(path);
            return node;
        }
        if (NativeFallback?.Invoke(path, options) is { } native) return native;
        throw new NotSupportedException($"No reader for '.{ext}'. Install OpenSceneGraph.Net.Native for the full OpenSceneGraph plugin set.");
    }

    public static Task<Node> ReadNodeFileAsync(string path, ReadOptions? options = null, CancellationToken ct = default) =>
        Task.Run(() => ReadNodeFile(path, options), ct);

    public static void WriteNodeFile(Node node, string path)
    {
        var ext = Extension(path);
        var rw = FindReaderWriter(ext);
        if (rw is not { CanWrite: true }) throw new NotSupportedException($"No writer for '.{ext}'.");
        using var fs = File.Create(path);
        rw.WriteNode(node, fs);
    }

    public static Image ReadImageFile(string path)
    {
        var r = FindImageReader(Extension(path)) ?? throw new NotSupportedException($"No image reader for '{Path.GetExtension(path)}'.");
        using var fs = File.OpenRead(path);
        return r.ReadImage(fs);
    }

    /// <summary>Reads an image, returning null instead of throwing (used for optional texture references).</summary>
    public static Image? TryReadImageFile(string path)
    {
        try { return File.Exists(path) ? ReadImageFile(path) : null; }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException) { return null; }
    }
}
