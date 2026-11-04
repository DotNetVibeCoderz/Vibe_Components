using System.Numerics;
using OpenSceneGraph.Net.Interop;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Meshes;

/// <summary>Mesh processing helpers (<c>osgUtil::SmoothingVisitor</c>, tessellation helpers, merging).</summary>
public static class MeshUtil
{
    /// <summary>
    /// Area-weighted smooth normals. Uses the Rust <c>osgnet_fast</c> library when present (several times faster
    /// on large meshes); otherwise a managed implementation with identical results.
    /// </summary>
    public static Vector3[] ComputeSmoothNormals(ReadOnlySpan<Vector3> positions, uint[]? indices, PrimitiveType type = PrimitiveType.Triangles)
    {
        var normals = new Vector3[positions.Length];
        if (positions.Length == 0) return normals;

        if (type == PrimitiveType.Triangles)
        {
            uint[] idx = indices ?? Sequential(positions.Length);
            if (FastPath.TryComputeNormals(positions, idx, normals)) return normals;
            ComputeNormalsManaged(positions, idx, normals);
            return normals;
        }

        if (type == PrimitiveType.TriangleStrip)
        {
            int n = indices?.Length ?? positions.Length;
            for (int i = 0; i + 2 < n; i++)
            {
                int a = (int)(indices?[i] ?? (uint)i), b = (int)(indices?[i + 1] ?? (uint)(i + 1)), c = (int)(indices?[i + 2] ?? (uint)(i + 2));
                if ((i & 1) == 1) (a, b) = (b, a);
                var fn = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += fn; normals[b] += fn; normals[c] += fn;
            }
            NormalizeAll(normals);
            return normals;
        }

        Array.Fill(normals, Vector3.UnitZ);
        return normals;
    }

    internal static void ComputeNormalsManaged(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> idx, Span<Vector3> normals)
    {
        normals.Clear();
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            uint a = idx[i], b = idx[i + 1], c = idx[i + 2];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
            var fn = Vector3.Cross(positions[(int)b] - positions[(int)a], positions[(int)c] - positions[(int)a]);
            normals[(int)a] += fn;
            normals[(int)b] += fn;
            normals[(int)c] += fn;
        }
        NormalizeAll(normals);
    }

    private static void NormalizeAll(Span<Vector3> normals)
    {
        for (int i = 0; i < normals.Length; i++)
        {
            float l = normals[i].Length();
            normals[i] = l > 1e-20f ? normals[i] / l : Vector3.UnitZ;
        }
    }

    public static uint[] Sequential(int count)
    {
        var a = new uint[count];
        for (int i = 0; i < count; i++) a[i] = (uint)i;
        return a;
    }

    /// <summary>
    /// Merges geometries that share the same state set into one draw call (like <c>osgUtil::Optimizer::MERGE_GEOMETRY</c>).
    /// Positions are baked with each geometry's world matrix.
    /// </summary>
    public static Geometry Merge(IEnumerable<(Geometry Geometry, Matrix4x4 World)> parts)
    {
        var pos = new List<Vector3>();
        var nor = new List<Vector3>();
        var col = new List<Vector4>();
        var uv = new List<Vector2>();
        var idx = new List<uint>();
        bool anyColor = false, anyUv = false;
        Geometry? first = null;

        foreach (var (g, world) in parts)
        {
            first ??= g;
            if (g.PrimitiveType != PrimitiveType.Triangles) throw new NotSupportedException("Merge supports triangle lists only.");
            uint baseIndex = (uint)pos.Count;
            var normals = g.Normals ?? ComputeSmoothNormals(g.Positions, g.Indices);
            Matrix4x4.Invert(world, out var inv);
            var nm = Matrix4x4.Transpose(inv);
            for (int i = 0; i < g.Positions.Length; i++)
            {
                pos.Add(Vector3.Transform(g.Positions[i], world));
                nor.Add(Vector3.Normalize(Vector3.TransformNormal(normals[i], nm)));
                col.Add(g.Colors?[i] ?? Vector4.One);
                uv.Add(g.TexCoords?[i] ?? Vector2.Zero);
            }
            anyColor |= g.Colors is not null;
            anyUv |= g.TexCoords is not null;
            if (g.Indices is null) for (uint i = 0; i < g.Positions.Length; i++) idx.Add(baseIndex + i);
            else foreach (var i in g.Indices) idx.Add(baseIndex + i);
        }

        return new Geometry
        {
            Positions = pos.ToArray(),
            Normals = nor.ToArray(),
            Colors = anyColor ? col.ToArray() : null,
            TexCoords = anyUv ? uv.ToArray() : null,
            Indices = idx.ToArray(),
            StateSet = first?.StateSet,
        };
    }
}
