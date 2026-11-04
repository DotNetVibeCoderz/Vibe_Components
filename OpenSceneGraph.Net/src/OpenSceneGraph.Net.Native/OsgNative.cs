using System.Numerics;
using System.Runtime.InteropServices;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Native;

/// <summary>
/// Entry point of the native OpenSceneGraph runtime. Call <see cref="Initialize"/> once; afterwards
/// <see cref="Registry.ReadNodeFile"/> transparently uses OSG's plugins for formats the managed core lacks.
/// </summary>
public static unsafe class OsgNative
{
    private static readonly Lazy<bool> s_available = new(Probe);
    private static int s_initialized;

    /// <summary>True when <c>osgnet</c> and OpenSceneGraph could be loaded on this machine.</summary>
    public static bool IsAvailable => s_available.Value;

    private static bool Probe()
    {
        try { return NativeMethods.osgnet_abi_version() == 1; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>Version string of the loaded OpenSceneGraph (e.g. "3.6.5").</summary>
    public static string OsgVersion
    {
        get
        {
            EnsureAvailable();
            Span<byte> buf = stackalloc byte[64];
            fixed (byte* p = buf) { NativeMethods.osgnet_osg_version(p, buf.Length); return Marshal.PtrToStringUTF8((nint)p) ?? ""; }
        }
    }

    internal static string LastError
    {
        get
        {
            Span<byte> buf = stackalloc byte[512];
            fixed (byte* p = buf) { NativeMethods.osgnet_last_error(p, buf.Length); return Marshal.PtrToStringUTF8((nint)p) ?? ""; }
        }
    }

    /// <summary>
    /// Registers the plugin directory and installs the native fallback reader. Safe to call repeatedly.
    /// Returns false when the native runtime is unavailable (the managed library keeps working).
    /// </summary>
    public static bool Initialize(bool installAsRegistryFallback = true)
    {
        if (!IsAvailable) return false;
        if (Interlocked.Exchange(ref s_initialized, 1) == 1) return true;
        NativeMethods.osgnet_set_notify_level(1); // WARN
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native") })
            if (Directory.Exists(dir)) NativeMethods.osgnet_add_library_path(dir);
        if (installAsRegistryFallback)
        {
            Registry.NativeFallback = (path, options) =>
            {
                using var native = NativeNode.Read(path);
                return native.ToManaged();
            };
        }
        return true;
    }

    internal static void EnsureAvailable()
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("The native OpenSceneGraph runtime (osgnet) is not available on this platform.");
        Initialize(installAsRegistryFallback: false);
    }
}

/// <summary>A reference-counted native <c>osg::Node</c>.</summary>
public sealed unsafe class NativeNode : SafeHandle
{
    internal NativeNode(nint handle) : base(0, true) => SetHandle(handle);

    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { NativeMethods.osgnet_node_release(handle); return true; }

    /// <summary>Reads any file OpenSceneGraph has a plugin for.</summary>
    public static NativeNode Read(string path)
    {
        OsgNative.EnsureAvailable();
        var h = NativeMethods.osgnet_read_node(Path.GetFullPath(path));
        if (h == 0) throw new IOException($"OpenSceneGraph could not read '{path}': {OsgNative.LastError}");
        return new NativeNode(h);
    }

    /// <summary>Writes with OpenSceneGraph's writers (.osgt, .osgb, .osg, .ive, .obj, .stl...).</summary>
    public void Write(string path)
    {
        if (NativeMethods.osgnet_write_node(handle, Path.GetFullPath(path)) != 0)
            throw new IOException($"OpenSceneGraph could not write '{path}': {OsgNative.LastError}");
    }

    public Math.BoundingSphere Bound
    {
        get
        {
            float* b = stackalloc float[4];
            NativeMethods.osgnet_node_bound(handle, b);
            return new Math.BoundingSphere(new Vector3(b[0], b[1], b[2]), b[3]);
        }
    }

    public (int Nodes, int Drawables, int Vertices, int Triangles) Statistics
    {
        get
        {
            int n, d, v, t;
            NativeMethods.osgnet_node_stats(handle, &n, &d, &v, &t);
            return (n, d, v, t);
        }
    }

    /// <summary>Runs <c>osgUtil::Optimizer</c> with the default optimisations.</summary>
    public void Optimize() => NativeMethods.osgnet_optimize(handle);

    public void SmoothNormals() => NativeMethods.osgnet_smooth_normals(handle);

    /// <summary>Converts triangle geometry to a managed scene graph (one transform + geometry per drawable).</summary>
    public Group ToManaged() => NativeSceneConverter.ToManaged(this);
}

/// <summary>Converts between managed and native scene graphs.</summary>
public static unsafe class NativeSceneConverter
{
    /// <summary>Extracts triangle meshes (with world transform, material colour and texture) from a native node.</summary>
    public static Group ToManaged(NativeNode node)
    {
        var root = new Group { Name = "osg" };
        nint meshes = NativeMethods.osgnet_extract_meshes(node.DangerousGetHandle());
        try
        {
            int count = NativeMethods.osgnet_meshes_count(meshes);
            var diffuse = stackalloc float[4];
            for (int i = 0; i < count; i++)
            {
                int vc, ic, flags, tw, th;
                var world = default(Matrix4x4);
                NativeMethods.osgnet_meshes_info(meshes, i, &vc, &ic, &flags, diffuse, (float*)&world, &tw, &th);
                var pos = new Vector3[vc];
                var nor = (flags & 1) != 0 ? new Vector3[vc] : null;
                var col = (flags & 2) != 0 ? new Vector4[vc] : null;
                var uv = (flags & 4) != 0 ? new Vector2[vc] : null;
                var idx = new uint[ic];
                var tex = (flags & 8) != 0 ? new byte[tw * th * 4] : null;
                fixed (Vector3* p = pos) fixed (Vector3* n = nor) fixed (Vector4* c = col) fixed (Vector2* t = uv) fixed (uint* ix = idx) fixed (byte* tx = tex)
                    NativeMethods.osgnet_meshes_copy(meshes, i, (float*)p, (float*)n, (float*)c, (float*)t, ix, tx);

                var g = new Geometry { Name = $"mesh{i}", Positions = pos, Normals = nor, Colors = col, TexCoords = uv, Indices = idx };
                if (nor is null) g.ComputeNormals();
                var ss = g.GetOrCreateStateSet();
                ss.Material = Material.FromColor(new Vector4(diffuse[0], diffuse[1], diffuse[2], diffuse[3]));
                if (tex is not null) ss.Texture = new Texture2D(new Image(tw, th, tex));
                if ((flags & 16) != 0) ss.Lighting = false;
                root.AddChild(world.IsIdentity ? g : new MatrixTransform(world).AddChild(g));
            }
        }
        finally { NativeMethods.osgnet_meshes_release(meshes); }
        return root;
    }

    /// <summary>Builds a native OSG scene from a managed one (transforms are baked into matrices; LOD/Switch are flattened to active children).</summary>
    public static NativeNode ToNative(Node node)
    {
        OsgNative.EnsureAvailable();
        return new NativeNode(Convert(node));
    }

    private static nint Convert(Node node)
    {
        switch (node)
        {
            case Geometry g when g.VertexCount > 0:
            {
                var mat = g.StateSet?.Material ?? new Material();
                var d = mat.Diffuse;
                bool lighting = g.StateSet?.Lighting ?? true;
                int count = g.DrawCount >= 0 ? System.Math.Min(g.DrawCount, g.VertexCount) : g.VertexCount;
                fixed (Vector3* p = g.Positions) fixed (Vector3* n = g.Normals) fixed (Vector4* c = g.Colors) fixed (Vector2* t = g.TexCoords) fixed (uint* ix = g.Indices)
                {
                    var geo = NativeMethods.osgnet_geometry_create((float*)p, (float*)n, (float*)c, (float*)t, g.Indices is null ? count : g.VertexCount,
                        ix, g.Indices?.Length ?? 0, (int)g.PrimitiveType, (float*)&d, lighting ? 1 : 0);
                    if (g.StateSet?.Texture?.Image is { } img)
                        fixed (byte* px = img.Pixels) NativeMethods.osgnet_node_set_texture_rgba(geo, px, img.Width, img.Height);
                    return geo;
                }
            }
            case Transform t:
            {
                var m = t.GetMatrix();
                var h = NativeMethods.osgnet_transform_create((float*)&m);
                AddChildren(h, t);
                return h;
            }
            case Group grp:
            {
                var h = NativeMethods.osgnet_group_create(grp.Name);
                AddChildren(h, grp);
                return h;
            }
            default:
                return NativeMethods.osgnet_group_create(node.Name);
        }
    }

    private static void AddChildren(nint parent, Group g)
    {
        for (int i = 0; i < g.ChildCount; i++)
        {
            if (g is Switch sw && !sw.GetValue(i)) continue;
            if (g is Lod && i > 0) break; // highest detail only
            var c = Convert(g.GetChild(i));
            NativeMethods.osgnet_group_add_child(parent, c);
            NativeMethods.osgnet_node_release(c); // parent holds the reference
        }
    }
}

public enum NativeManipulator { Trackball = 0, Flight = 1, Drive = 2, Terrain = 3, None = 4 }

/// <summary>
/// A real <c>osgViewer::Viewer</c>. Windowed viewers open a native window with OSG's default event handlers
/// (stats with 's', wireframe with 'w', ...); offscreen viewers render to a pbuffer and support <see cref="Capture"/>.
/// </summary>
public sealed unsafe class NativeViewer : IDisposable
{
    private nint _h;
    private NativeNode? _scene;

    private NativeViewer(nint h, int width, int height)
    {
        _h = h;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsOffscreen { get; private init; }

    public static NativeViewer CreateWindow(int width = 1280, int height = 720, string title = "OpenSceneGraph.Net", int x = 80, int y = 80)
    {
        OsgNative.EnsureAvailable();
        var h = NativeMethods.osgnet_viewer_create_window(x, y, width, height, title);
        if (h == 0) throw new InvalidOperationException("Could not create the OpenSceneGraph window: " + OsgNative.LastError);
        return new NativeViewer(h, width, height);
    }

    public static NativeViewer CreateOffscreen(int width, int height, int samples = 4)
    {
        OsgNative.EnsureAvailable();
        var h = NativeMethods.osgnet_viewer_create_offscreen(width, height, samples);
        if (h == 0) throw new InvalidOperationException("Could not create an offscreen OpenGL context: " + OsgNative.LastError);
        return new NativeViewer(h, width, height) { IsOffscreen = true };
    }

    public NativeNode? Scene
    {
        get => _scene;
        set { _scene = value; NativeMethods.osgnet_viewer_set_scene(_h, value?.DangerousGetHandle() ?? 0); }
    }

    /// <summary>Converts and shows a managed scene.</summary>
    public void SetScene(Node managed) => Scene = NativeSceneConverter.ToNative(managed);

    public void SetManipulator(NativeManipulator m) => NativeMethods.osgnet_viewer_set_manipulator(_h, (int)m);

    public void SetView(Matrix4x4 view, float fovY = 45, float near = 0.1f, float far = 10000f) =>
        NativeMethods.osgnet_viewer_set_view(_h, (float*)&view, fovY, near, far);

    public void SetClearColor(Vector4 c) => NativeMethods.osgnet_viewer_set_clear_color(_h, c.X, c.Y, c.Z, c.W);
    public void SetThreadingModel(ThreadingModel m) => NativeMethods.osgnet_viewer_set_threading(_h, m == ThreadingModel.SingleThreaded ? 0 : 2);
    public void AddStatsHandler() => NativeMethods.osgnet_viewer_add_stats(_h);
    public void Home() => NativeMethods.osgnet_viewer_home(_h);

    /// <summary>Renders one frame; returns false once the window was closed.</summary>
    public bool Frame(double simulationTime = -1) => NativeMethods.osgnet_viewer_frame(_h, simulationTime) != 0;

    /// <summary>Runs the OSG frame loop until the window closes.</summary>
    public int Run() => NativeMethods.osgnet_viewer_run(_h);

    /// <summary>Returns the last frame of an offscreen viewer as an RGBA image (top row first).</summary>
    public Image Capture()
    {
        var img = new Image(Width, Height);
        fixed (byte* p = img.Pixels)
            if (NativeMethods.osgnet_viewer_capture(_h, p, Width, Height) != 0)
                throw new InvalidOperationException("Capture failed: " + OsgNative.LastError);
        return img;
    }

    /// <summary>Renders a few frames offscreen and returns the image — handy for thumbnails and docs screenshots.</summary>
    public static Image RenderToImage(Node managed, int width, int height, Matrix4x4? view = null, Vector4? clearColor = null)
    {
        using var v = CreateOffscreen(width, height);
        v.SetClearColor(clearColor ?? new Vector4(0.06f, 0.08f, 0.11f, 1));
        v.SetScene(managed);
        if (view is { } vm) v.SetView(vm);
        for (int i = 0; i < 3; i++) v.Frame();
        return v.Capture();
    }

    public void Dispose()
    {
        if (_h == 0) return;
        NativeMethods.osgnet_viewer_release(_h);
        _h = 0;
        _scene?.Dispose();
    }
}
