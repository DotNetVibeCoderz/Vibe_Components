using System.Numerics;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Animation;

/// <summary>A joint (<c>osgAnimation::Bone</c>): a transform with a bind pose. Bones nest to form a hierarchy.</summary>
public sealed class Bone : Transform
{
    private Vector3 _t;
    private Quaternion _r = Quaternion.Identity;
    private Vector3 _s = Vector3.One;

    public Bone(string name) => Name = name;

    public Vector3 Translation { get => _t; set { _t = value; DirtyBound(); } }
    public Quaternion Rotation { get => _r; set { _r = value; DirtyBound(); } }
    public Vector3 Scale { get => _s; set { _s = value; DirtyBound(); } }

    /// <summary>Inverse of the bone's skeleton-space matrix in the bind pose.</summary>
    public Matrix4x4 InverseBindMatrix { get; set; } = Matrix4x4.Identity;

    public override Matrix4x4 GetMatrix() =>
        Matrix4x4.CreateScale(_s) * Matrix4x4.CreateFromQuaternion(_r) * Matrix4x4.CreateTranslation(_t);

    public Bone AddBone(Bone child) { AddChild(child); return child; }
}

/// <summary>Root of a bone hierarchy (<c>osgAnimation::Skeleton</c>).</summary>
public sealed class Skeleton : MatrixTransform
{
    private readonly List<Bone> _bones = [];

    public IReadOnlyList<Bone> Bones => _bones;

    /// <summary>Collects bones in depth-first order; the index is the bone index used in vertex weights.</summary>
    public void Rebuild()
    {
        _bones.Clear();
        void Walk(Node n)
        {
            if (n is Bone b) _bones.Add(b);
            if (n is Group g) foreach (var c in g.Children) Walk(c);
        }
        foreach (var c in Children) Walk(c);
    }

    /// <summary>Captures the current pose as the bind pose (sets every bone's inverse bind matrix).</summary>
    public void SetBindPose()
    {
        Rebuild();
        foreach (var b in _bones)
            b.InverseBindMatrix = Matrix4x4.Invert(BoneToSkeleton(b), out var inv) ? inv : Matrix4x4.Identity;
    }

    /// <summary>Bone matrix in skeleton space (product of bone transforms up to, not including, the skeleton).</summary>
    public Matrix4x4 BoneToSkeleton(Bone bone)
    {
        var m = Matrix4x4.Identity;
        for (Node? n = bone; n is not null && !ReferenceEquals(n, this); n = n.Parents.Count > 0 ? n.Parents[0] : null)
            if (n is Bone b) m *= b.GetMatrix();
        return m;
    }

    /// <summary>Fills <paramref name="palette"/> with skinning matrices (inverse bind * current pose).</summary>
    public void ComputePalette(Span<Matrix4x4> palette)
    {
        for (int i = 0; i < _bones.Count && i < palette.Length; i++)
            palette[i] = _bones[i].InverseBindMatrix * BoneToSkeleton(_bones[i]);
    }
}

/// <summary>
/// Geometry deformed by a skeleton (<c>osgAnimation::RigGeometry</c>), skinned on the CPU with linear blend
/// skinning (parallelised for large meshes) so every renderer backend can draw it.
/// Place it under the <see cref="Skeleton"/> node (not under a bone).
/// </summary>
public sealed class RigGeometry : Geometry
{
    private Vector3[] _bindPositions = [];
    private Vector3[] _bindNormals = [];
    private Matrix4x4[] _palette = [];

    public RigGeometry() => Usage = BufferUsage.Dynamic;

    public Skeleton? Skeleton { get; set; }

    /// <summary>Takes the current Positions/Normals as the rest pose.</summary>
    public void CaptureBindPose()
    {
        _bindPositions = (Vector3[])Positions.Clone();
        _bindNormals = (Vector3[])(Normals ?? MeshUtil.ComputeSmoothNormals(Positions, Indices, PrimitiveType)).Clone();
    }

    public void Skin()
    {
        if (Skeleton is null || BoneIndices is null || BoneWeights is null) return;
        if (_bindPositions.Length != Positions.Length) CaptureBindPose();
        int boneCount = Skeleton.Bones.Count;
        if (_palette.Length != boneCount) _palette = new Matrix4x4[boneCount];
        Skeleton.ComputePalette(_palette);

        var pos = Positions;
        var nor = Normals ??= new Vector3[pos.Length];
        var bi = BoneIndices;
        var bw = BoneWeights;
        var palette = _palette;
        var bindP = _bindPositions;
        var bindN = _bindNormals;

        void SkinVertex(int v)
        {
            var w = bw[v];
            var idx = bi[v];
            Vector3 p = Vector3.Zero, n = Vector3.Zero;
            float total = 0;
            for (int k = 0; k < 4; k++)
            {
                float wk = k switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
                if (wk <= 0) continue;
                int b = (int)(k switch { 0 => idx.X, 1 => idx.Y, 2 => idx.Z, _ => idx.W });
                if ((uint)b >= (uint)palette.Length) continue;
                p += Vector3.Transform(bindP[v], palette[b]) * wk;
                n += Vector3.TransformNormal(bindN[v], palette[b]) * wk;
                total += wk;
            }
            if (total <= 0) { pos[v] = bindP[v]; nor[v] = bindN[v]; return; }
            pos[v] = p / total;
            nor[v] = Vector3.Normalize(n);
        }

        if (pos.Length > 4096) Parallel.For(0, pos.Length, SkinVertex);
        else for (int v = 0; v < pos.Length; v++) SkinVertex(v);
        MarkDirty();
    }
}

/// <summary>Skins every <see cref="RigGeometry"/> under a skeleton each frame (install on the skeleton).</summary>
public sealed class SkinningCallback : IUpdateCallback
{
    public void Update(Node node, in FrameStamp frame)
    {
        if (node is not Skeleton sk) return;
        foreach (var c in sk.Children)
            if (c is RigGeometry rig) { rig.Skeleton ??= sk; rig.Skin(); }
    }
}

/// <summary>Builds small demo rigs used by the gallery and tests.</summary>
public static class RigFactory
{
    /// <summary>
    /// A segmented tentacle/tail: a tapered cylinder skinned to a chain of <paramref name="segments"/> bones along +Z,
    /// with a looping "wave" clip. Returns the skeleton (already carrying a skinning callback and animation manager).
    /// </summary>
    public static Skeleton Tentacle(int segments = 6, float length = 6f, float radius = 0.5f, Vector4? color = null)
    {
        var skeleton = new Skeleton { Name = "tentacle" };
        float seg = length / segments;
        Group parent = skeleton;
        for (int i = 0; i < segments; i++)
        {
            var b = new Bone($"bone{i}") { Translation = new Vector3(0, 0, i == 0 ? 0 : seg) };
            parent.AddChild(b);
            parent = b;
        }
        skeleton.SetBindPose();

        // tapered tube along +Z with many rings so the skin bends smoothly
        var mesh = Tube(length, radius, segments * 4, 24);
        var bi = new Vector4[mesh.VertexCount];
        var bw = new Vector4[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float f = MathUtil.Clamp01(mesh.Positions[v].Z / length) * segments - 0.5f;
            int b0 = System.Math.Clamp((int)MathF.Floor(f), 0, segments - 1);
            int b1 = System.Math.Min(b0 + 1, segments - 1);
            float t = MathUtil.Clamp01(f - b0);
            bi[v] = new Vector4(b0, b1, 0, 0);
            bw[v] = new Vector4(1 - t, t, 0, 0);
        }
        var rig = new RigGeometry
        {
            Name = "tentacleSkin",
            Positions = mesh.Positions,
            Normals = mesh.Normals,
            TexCoords = mesh.TexCoords,
            Indices = mesh.Indices,
            BoneIndices = bi,
            BoneWeights = bw,
            Skeleton = skeleton,
        };
        rig.GetOrCreateStateSet().Material = Material.FromColor(color ?? Color.FromHex("#e0567a"));
        rig.CaptureBindPose();
        skeleton.AddChild(rig);
        skeleton.Rebuild();

        var clip = new AnimationClip("wave");
        for (int i = 1; i < segments; i++)
        {
            var ch = clip.Rotation($"bone{i}");
            ch.Interpolation = Interpolation.CubicHermite;
            for (int k = 0; k <= 8; k++)
            {
                double t = k * 0.25;
                float angle = MathF.Sin((float)(k / 8.0 * 2 * System.Math.PI) + i * 0.7f) * 0.35f;
                ch.Add(t, Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle * 0.4f));
            }
        }
        var manager = new AnimationManager().Register(clip);
        manager.Play("wave");
        skeleton.AddUpdateCallback(manager);
        skeleton.AddUpdateCallback(new SkinningCallback());
        return skeleton;
    }

    /// <summary>Open tube along +Z with many rings (good for skinning).</summary>
    public static Geometry Tube(float length, float radius, int rings, int segments)
    {
        int vc = (rings + 1) * (segments + 1);
        var pos = new Vector3[vc];
        var nor = new Vector3[vc];
        var uv = new Vector2[vc];
        int k = 0;
        for (int r = 0; r <= rings; r++)
        {
            float z = length * r / rings;
            float rad = radius * (1 - 0.7f * r / rings);
            for (int s = 0; s <= segments; s++)
            {
                float a = 2 * MathF.PI * s / segments;
                var n = new Vector3(MathF.Cos(a), MathF.Sin(a), 0);
                pos[k] = n * rad + new Vector3(0, 0, z);
                nor[k] = n;
                uv[k] = new Vector2((float)s / segments, (float)r / rings);
                k++;
            }
        }
        return new Geometry { Positions = pos, Normals = nor, TexCoords = uv, Indices = Shapes.GridIndices(segments, rings, flip: false) };
    }
}
