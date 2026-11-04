using System.Runtime.InteropServices;

namespace OpenSceneGraph.Net.Native;

/// <summary>Raw P/Invoke surface of <c>osgnet</c> (see <c>native/osgnet/osgnet.h</c>).</summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "osgnet";

    [LibraryImport(Lib)] internal static partial int osgnet_abi_version();
    [LibraryImport(Lib)] internal static partial int osgnet_osg_version(byte* buffer, int length);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial void osgnet_add_library_path(string path);
    [LibraryImport(Lib)] internal static partial void osgnet_set_notify_level(int level);
    [LibraryImport(Lib)] internal static partial int osgnet_last_error(byte* buffer, int length);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint osgnet_read_node(string path);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial int osgnet_write_node(nint node, string path);
    [LibraryImport(Lib)] internal static partial void osgnet_node_release(nint node);
    [LibraryImport(Lib)] internal static partial void osgnet_node_bound(nint node, float* centerRadius4);
    [LibraryImport(Lib)] internal static partial int osgnet_node_stats(nint node, int* nodes, int* drawables, int* vertices, int* triangles);
    [LibraryImport(Lib)] internal static partial void osgnet_optimize(nint node);
    [LibraryImport(Lib)] internal static partial void osgnet_smooth_normals(nint node);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint osgnet_group_create(string? name);
    [LibraryImport(Lib)] internal static partial nint osgnet_transform_create(float* matrix16);
    [LibraryImport(Lib)] internal static partial void osgnet_transform_set_matrix(nint transform, float* matrix16);
    [LibraryImport(Lib)] internal static partial int osgnet_group_add_child(nint group, nint child);
    [LibraryImport(Lib)]
    internal static partial nint osgnet_geometry_create(float* positions, float* normals, float* colors, float* texcoords, int vertexCount,
                                                        uint* indices, int indexCount, int primitive, float* diffuse4, int lighting);
    [LibraryImport(Lib)] internal static partial void osgnet_node_set_texture_rgba(nint node, byte* rgba, int width, int height);

    [LibraryImport(Lib)] internal static partial nint osgnet_extract_meshes(nint node);
    [LibraryImport(Lib)] internal static partial int osgnet_meshes_count(nint meshes);
    [LibraryImport(Lib)]
    internal static partial int osgnet_meshes_info(nint meshes, int index, int* vertexCount, int* indexCount, int* flags, float* diffuse4,
                                                   float* world16, int* textureWidth, int* textureHeight);
    [LibraryImport(Lib)]
    internal static partial int osgnet_meshes_copy(nint meshes, int index, float* positions, float* normals, float* colors, float* texcoords,
                                                   uint* indices, byte* textureRgba);
    [LibraryImport(Lib)] internal static partial void osgnet_meshes_release(nint meshes);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint osgnet_viewer_create_window(int x, int y, int width, int height, string? title);
    [LibraryImport(Lib)] internal static partial nint osgnet_viewer_create_offscreen(int width, int height, int samples);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_set_scene(nint viewer, nint scene);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_set_manipulator(nint viewer, int kind);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_set_view(nint viewer, float* view16, float fovy, float zNear, float zFar);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_set_clear_color(nint viewer, float r, float g, float b, float a);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_set_threading(nint viewer, int model);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_add_stats(nint viewer);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_home(nint viewer);
    [LibraryImport(Lib)] internal static partial int osgnet_viewer_frame(nint viewer, double simulationTime);
    [LibraryImport(Lib)] internal static partial int osgnet_viewer_run(nint viewer);
    [LibraryImport(Lib)] internal static partial int osgnet_viewer_capture(nint viewer, byte* rgba, int width, int height);
    [LibraryImport(Lib)] internal static partial void osgnet_viewer_release(nint viewer);
}
