using System.Numerics;
using OpenSceneGraph.Net.Math;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.Rendering;

/// <summary>Result of a ray / scene intersection.</summary>
public readonly record struct Intersection(Node Drawable, IReadOnlyList<Node> NodePath, Vector3 WorldPoint, Vector3 WorldNormal, float Distance, int TriangleIndex);

/// <summary>
/// Ray picking against triangle geometry (<c>osgUtil::LineSegmentIntersector</c> + <c>IntersectionVisitor</c>).
/// Bounding spheres prune subgraphs before the per-triangle Möller–Trumbore test.
/// </summary>
public sealed class RayIntersector : MatrixTrackingVisitor
{
    private readonly Vector3 _origin;
    private readonly Vector3 _dir;
    private readonly float _maxDistance;

    public RayIntersector(Vector3 origin, Vector3 direction, float maxDistance = float.MaxValue) : base(TraversalMode.ActiveChildren)
    {
        _origin = origin;
        _dir = Vector3.Normalize(direction);
        _maxDistance = maxDistance;
    }

    public List<Intersection> Intersections { get; } = [];

    public Intersection? First => Intersections.Count == 0 ? null : Intersections.MinBy(i => i.Distance);

    private bool HitsSphere(in BoundingSphere s)
    {
        if (!s.IsValid) return false;
        var oc = _origin - s.Center;
        float b = Vector3.Dot(oc, _dir);
        float c = oc.LengthSquared() - s.Radius * s.Radius;
        if (c > 0 && b > 0) return false;
        return b * b - c >= 0;
    }

    public override void Apply(Node node)
    {
        if (!ValidNodeMask(node)) return;
        if (node is not Transform && node.Bound.IsValid && !HitsSphere(node.Bound.Transform(CurrentMatrix))) return;
        Traverse(node);
    }

    public override void Apply(Transform transform)
    {
        if (!ValidNodeMask(transform)) return;
        if (transform.Bound.IsValid && !HitsSphere(transform.Bound.Transform(CurrentMatrix))) return;
        base.Apply(transform);
    }

    public override void Apply(Drawable drawable)
    {
        if (!ValidNodeMask(drawable) || drawable is not Geometry g) return;
        var world = CurrentMatrix;
        if (!HitsSphere(g.Bound.Transform(world))) return;
        if (!Matrix4x4.Invert(world, out var inv)) return;

        // intersect in local space
        var lo = Vector3.Transform(_origin, inv);
        var ld = Vector3.Normalize(Vector3.TransformNormal(_dir, inv));
        var p = g.Positions;
        int tri = 0;
        foreach (var (a, b, c) in g.EnumerateTriangles())
        {
            if (RayTriangle(lo, ld, p[a], p[b], p[c], out float t))
            {
                var hitLocal = lo + ld * t;
                var hit = Vector3.Transform(hitLocal, world);
                float dist = Vector3.Distance(_origin, hit);
                if (dist <= _maxDistance)
                {
                    var n = Vector3.Normalize(Vector3.TransformNormal(Vector3.Cross(p[b] - p[a], p[c] - p[a]), Matrix4x4.Transpose(inv)));
                    var path = new List<Node>(NodePath) { drawable };
                    Intersections.Add(new Intersection(drawable, path, hit, n, dist, tri));
                }
            }
            tri++;
        }
    }

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        var tv = o - v0;
        float u = Vector3.Dot(tv, pv) * inv;
        if (u < 0 || u > 1) return false;
        var qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, qv) * inv;
        return t > 1e-6f;
    }
}
