using System.Numerics;
using System.Runtime.InteropServices;

namespace OpenSceneGraph.Net.Interop;

/// <summary>
/// Optional native acceleration implemented in Rust (<c>native/osgnet-fast</c>, library <c>osgnet_fast</c>).
/// Every entry point has a managed fallback, so the library is never required.
/// Set <c>OSGNET_DISABLE_FAST=1</c> to force the managed paths (useful for benchmarking).
/// </summary>
public static unsafe partial class FastPath
{
    private const string Lib = "osgnet_fast";
    private static readonly Lazy<bool> s_available = new(Probe);

    /// <summary>True when the Rust library was found and reports a compatible ABI version.</summary>
    public static bool IsAvailable => s_available.Value;

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("OSGNET_DISABLE_FAST") == "1") return false;
        if (OperatingSystem.IsBrowser()) return false;
        try { return osgnet_fast_abi_version() == 1; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    [LibraryImport(Lib)] private static partial uint osgnet_fast_abi_version();

    [LibraryImport(Lib)]
    private static partial int osgnet_fast_compute_normals(Vector3* positions, nuint vertexCount, uint* indices, nuint indexCount, Vector3* normalsOut);

    [LibraryImport(Lib)]
    private static partial int osgnet_fast_heightfield_mesh(float* heights, uint cols, uint rows, float dx, float dy, Vector3* positionsOut, Vector3* normalsOut);

    [LibraryImport(Lib)]
    private static partial nuint osgnet_fast_cull_spheres(Vector4* planes, Vector4* spheres, nuint count, byte* visibleOut);

    [LibraryImport(Lib)]
    private static partial void osgnet_fast_particles_step(Vector3* pos, Vector3* vel, float* age, nuint count, float gx, float gy, float gz, float drag, float dt);

    internal static bool TryComputeNormals(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, Span<Vector3> normals)
    {
        if (!IsAvailable || positions.Length < 2048) return false; // P/Invoke overhead dominates for small meshes
        fixed (Vector3* p = positions)
        fixed (uint* i = indices)
        fixed (Vector3* n = normals)
            return osgnet_fast_compute_normals(p, (nuint)positions.Length, i, (nuint)indices.Length, n) == 0;
    }

    internal static bool TryHeightfield(ReadOnlySpan<float> heights, int cols, int rows, float dx, float dy, Span<Vector3> positions, Span<Vector3> normals)
    {
        if (!IsAvailable) return false;
        fixed (float* h = heights)
        fixed (Vector3* p = positions)
        fixed (Vector3* n = normals)
            return osgnet_fast_heightfield_mesh(h, (uint)cols, (uint)rows, dx, dy, p, n) == 0;
    }

    /// <summary>Batch frustum test of (center.xyz, radius) spheres against six planes. Returns the visible count.</summary>
    internal static bool TryCullSpheres(ReadOnlySpan<Vector4> planes, ReadOnlySpan<Vector4> spheres, Span<byte> visible, out int visibleCount)
    {
        visibleCount = 0;
        if (!IsAvailable || planes.Length != 6) return false;
        fixed (Vector4* pl = planes)
        fixed (Vector4* s = spheres)
        fixed (byte* v = visible)
            visibleCount = (int)osgnet_fast_cull_spheres(pl, s, (nuint)spheres.Length, v);
        return true;
    }

    internal static bool TryParticlesStep(Span<Vector3> pos, Span<Vector3> vel, Span<float> age, Vector3 gravity, float drag, float dt)
    {
        if (!IsAvailable || pos.Length < 4096) return false;
        fixed (Vector3* p = pos)
        fixed (Vector3* v = vel)
        fixed (float* a = age)
            osgnet_fast_particles_step(p, v, a, (nuint)pos.Length, gravity.X, gravity.Y, gravity.Z, drag, dt);
        return true;
    }
}
