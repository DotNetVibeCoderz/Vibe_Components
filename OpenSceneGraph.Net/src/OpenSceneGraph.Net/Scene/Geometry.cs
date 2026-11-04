using System.Numerics;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Meshes;

namespace OpenSceneGraph.Net.Scene;

/// <summary>Leaf node that can be drawn (<c>osg::Drawable</c>).</summary>
public abstract class Drawable : Node
{
    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

public enum PrimitiveType : byte { Points, Lines, LineStrip, Triangles, TriangleStrip }

/// <summary>How the renderer should treat GPU buffers: static meshes are uploaded once, dynamic ones every change.</summary>
public enum BufferUsage : byte { Static, Dynamic, Stream }

/// <summary>
/// Vertex arrays plus one primitive set (<c>osg::Geometry</c>).
/// Arrays are plain .NET arrays; after mutating them in place call <see cref="MarkDirty"/> so renderers re-upload.
/// </summary>
public class Geometry : Drawable
{
    private Vector3[] _positions = [];

    public Vector3[] Positions
    {
        get => _positions;
        set { _positions = value ?? []; MarkDirty(); }
    }

    public Vector3[]? Normals { get; set; }

    /// <summary>Per-vertex RGBA colors in linear 0..1 space. When null the material color is used.</summary>
    public Vector4[]? Colors { get; set; }

    public Vector2[]? TexCoords { get; set; }

    /// <summary>Indices into the vertex arrays; when null, vertices are drawn in order.</summary>
    public uint[]? Indices { get; set; }

    /// <summary>Up to four bone indices per vertex for skeletal animation (see <see cref="Animation.Skeleton"/>).</summary>
    public Vector4[]? BoneIndices { get; set; }
    public Vector4[]? BoneWeights { get; set; }

    public PrimitiveType PrimitiveType { get; set; } = PrimitiveType.Triangles;
    public BufferUsage Usage { get; set; } = BufferUsage.Static;

    /// <summary>Incremented on every <see cref="MarkDirty"/>; renderers compare it to decide when to re-upload.</summary>
    public int Version { get; private set; }

    /// <summary>Optional element range to draw (defaults to everything).</summary>
    public int DrawFirst { get; set; }
    public int DrawCount { get; set; } = -1;

    public int VertexCount => _positions.Length;
    public int ElementCount => Indices?.Length ?? _positions.Length;

    public long TriangleCount => PrimitiveType switch
    {
        PrimitiveType.Triangles => ElementCount / 3,
        PrimitiveType.TriangleStrip => System.Math.Max(0, ElementCount - 2),
        _ => 0,
    };

    public void MarkDirty()
    {
        Version++;
        DirtyBound();
    }

    protected override BoundingSphere ComputeBound() => BoundingSphere.FromBox(BoundingBox.FromPoints(_positions));

    public BoundingBox ComputeBoundingBox() => BoundingBox.FromPoints(_positions);

    /// <summary>Computes smooth per-vertex normals (uses the native Rust fast path when available).</summary>
    public Geometry ComputeNormals()
    {
        Normals = MeshUtil.ComputeSmoothNormals(_positions, Indices, PrimitiveType);
        MarkDirty();
        return this;
    }

    /// <summary>Sets a single color on every vertex.</summary>
    public Geometry SetColor(Vector4 color)
    {
        var c = new Vector4[_positions.Length];
        Array.Fill(c, color);
        Colors = c;
        MarkDirty();
        return this;
    }

    /// <summary>Enumerates triangles as index triples regardless of the primitive layout.</summary>
    public IEnumerable<(int A, int B, int C)> EnumerateTriangles()
    {
        int n = ElementCount;
        int Idx(int i) => Indices is null ? i : (int)Indices[i];
        if (PrimitiveType == PrimitiveType.Triangles)
        {
            for (int i = 0; i + 2 < n; i += 3) yield return (Idx(i), Idx(i + 1), Idx(i + 2));
        }
        else if (PrimitiveType == PrimitiveType.TriangleStrip)
        {
            for (int i = 0; i + 2 < n; i++)
                yield return (i & 1) == 0 ? (Idx(i), Idx(i + 1), Idx(i + 2)) : (Idx(i + 1), Idx(i), Idx(i + 2));
        }
    }

    /// <summary>Deep copy of the vertex data (the clone has a new Id and no parents).</summary>
    public Geometry Clone() => new()
    {
        Name = Name,
        _positions = (Vector3[])_positions.Clone(),
        Normals = (Vector3[]?)Normals?.Clone(),
        Colors = (Vector4[]?)Colors?.Clone(),
        TexCoords = (Vector2[]?)TexCoords?.Clone(),
        Indices = (uint[]?)Indices?.Clone(),
        BoneIndices = (Vector4[]?)BoneIndices?.Clone(),
        BoneWeights = (Vector4[]?)BoneWeights?.Clone(),
        PrimitiveType = PrimitiveType,
        Usage = Usage,
        StateSet = StateSet,
    };
}
