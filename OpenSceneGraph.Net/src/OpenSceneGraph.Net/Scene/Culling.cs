using System.Numerics;
using OpenSceneGraph.Net.Math;

namespace OpenSceneGraph.Net.Scene;

/// <summary>
/// Cluster culling data (<c>osg::ClusterCullingCallback</c>): a node is skipped when it faces away from the eye,
/// e.g. terrain tiles on the far side of a planet. The node is culled when
/// <c>dot(normalize(eye - ControlPoint), Normal) &lt; Deviation</c>.
/// </summary>
public sealed record ClusterCull(Vector3 ControlPoint, Vector3 Normal, float Deviation, float Radius = -1);

/// <summary>
/// A group carrying a convex planar occluder (<c>osg::OccluderNode</c> with <c>osg::ConvexPlanarOccluder</c>).
/// Objects completely hidden behind the occluder polygon are culled (CPU occlusion culling).
/// </summary>
public class OccluderNode : Group
{
    /// <summary>Convex polygon in local coordinates (at least 3 coplanar points, any winding).</summary>
    public Vector3[] Occluder { get; set; } = [];

    public override void Accept(NodeVisitor visitor) => visitor.Apply(this);
}

/// <summary>World-space occlusion volume built from an occluder polygon and the eye position.</summary>
internal readonly struct OcclusionVolume
{
    private readonly Vector4[] _planes; // [0] = occluder plane (eye side positive), rest = edge planes (inside positive)

    private OcclusionVolume(Vector4[] planes) => _planes = planes;

    public static bool TryCreate(ReadOnlySpan<Vector3> poly, Vector3 eye, out OcclusionVolume volume)
    {
        volume = default;
        if (poly.Length < 3) return false;
        var n = Vector3.Cross(poly[1] - poly[0], poly[2] - poly[0]);
        if (n.LengthSquared() < 1e-12f) return false;
        n = Vector3.Normalize(n);
        float d = -Vector3.Dot(n, poly[0]);
        float eyeSide = Vector3.Dot(n, eye) + d;
        if (MathF.Abs(eyeSide) < 1e-5f) return false; // edge-on
        if (eyeSide < 0) { n = -n; d = -d; }

        var centroid = Vector3.Zero;
        foreach (var p in poly) centroid += p;
        centroid /= poly.Length;

        var planes = new Vector4[poly.Length + 1];
        planes[0] = new Vector4(n, d);
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var en = Vector3.Cross(a - eye, b - eye);
            if (en.LengthSquared() < 1e-12f) return false;
            en = Vector3.Normalize(en);
            float ed = -Vector3.Dot(en, eye);
            if (Vector3.Dot(en, centroid) + ed < 0) { en = -en; ed = -ed; }
            planes[i + 1] = new Vector4(en, ed);
        }
        volume = new OcclusionVolume(planes);
        return true;
    }

    /// <summary>True when the sphere lies completely inside the occluded region.</summary>
    public bool Occludes(in BoundingSphere s)
    {
        var p0 = _planes[0];
        if (Vector3.Dot(new Vector3(p0.X, p0.Y, p0.Z), s.Center) + p0.W > -s.Radius) return false;
        for (int i = 1; i < _planes.Length; i++)
        {
            var p = _planes[i];
            if (Vector3.Dot(new Vector3(p.X, p.Y, p.Z), s.Center) + p.W < s.Radius) return false;
        }
        return true;
    }
}
