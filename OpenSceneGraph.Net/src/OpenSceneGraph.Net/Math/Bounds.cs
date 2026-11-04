using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenSceneGraph.Net.Math;

/// <summary>Bounding sphere, mirroring <c>osg::BoundingSphere</c>. A negative radius means "invalid / empty".</summary>
public readonly record struct BoundingSphere(Vector3 Center, float Radius)
{
    public static readonly BoundingSphere Invalid = new(Vector3.Zero, -1f);

    public bool IsValid => Radius >= 0f;

    /// <summary>Returns the smallest sphere that contains both spheres.</summary>
    public BoundingSphere ExpandBy(in BoundingSphere other)
    {
        if (!other.IsValid) return this;
        if (!IsValid) return other;
        var d = other.Center - Center;
        float dist = d.Length();
        if (dist + other.Radius <= Radius) return this;
        if (dist + Radius <= other.Radius) return other;
        float newRadius = (Radius + dist + other.Radius) * 0.5f;
        var center = dist > 1e-12f ? Center + d * ((newRadius - Radius) / dist) : Center;
        return new BoundingSphere(center, newRadius);
    }

    public BoundingSphere ExpandBy(Vector3 point)
    {
        if (!IsValid) return new BoundingSphere(point, 0f);
        var d = point - Center;
        float dist = d.Length();
        if (dist <= Radius) return this;
        float newRadius = (Radius + dist) * 0.5f;
        return new BoundingSphere(Center + d * ((newRadius - Radius) / dist), newRadius);
    }

    /// <summary>Transforms the sphere; the radius is scaled by the largest axis scale of the matrix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BoundingSphere Transform(in Matrix4x4 m)
    {
        if (!IsValid) return this;
        var c = Vector3.Transform(Center, m);
        float sx = new Vector3(m.M11, m.M12, m.M13).LengthSquared();
        float sy = new Vector3(m.M21, m.M22, m.M23).LengthSquared();
        float sz = new Vector3(m.M31, m.M32, m.M33).LengthSquared();
        return new BoundingSphere(c, Radius * MathF.Sqrt(MathF.Max(sx, MathF.Max(sy, sz))));
    }

    public bool Contains(Vector3 p) => IsValid && Vector3.DistanceSquared(p, Center) <= Radius * Radius;

    public static BoundingSphere FromBox(in BoundingBox box) =>
        box.IsValid ? new BoundingSphere(box.Center, box.Radius) : Invalid;
}

/// <summary>Axis aligned bounding box, mirroring <c>osg::BoundingBox</c>.</summary>
public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    public static readonly BoundingBox Invalid = new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

    public bool IsValid => Max.X >= Min.X && Max.Y >= Min.Y && Max.Z >= Min.Z;
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;
    public float Radius => (Max - Min).Length() * 0.5f;

    public BoundingBox ExpandBy(Vector3 p) => new(Vector3.Min(Min, p), Vector3.Max(Max, p));
    public BoundingBox ExpandBy(in BoundingBox b) => !b.IsValid ? this : !IsValid ? b : new(Vector3.Min(Min, b.Min), Vector3.Max(Max, b.Max));

    public static BoundingBox FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty) return Invalid;
        Vector3 min = points[0], max = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        return new BoundingBox(min, max);
    }

    public bool Contains(Vector3 p) =>
        p.X >= Min.X && p.Y >= Min.Y && p.Z >= Min.Z && p.X <= Max.X && p.Y <= Max.Y && p.Z <= Max.Z;
}

/// <summary>Six-plane view frustum extracted from a view-projection matrix (System.Numerics row-vector convention, depth 0..1).</summary>
public struct Frustum
{
    // Planes stored as (normal.xyz, d); a point is inside when dot(n,p)+d >= 0.
    private Vector4 _left, _right, _bottom, _top, _near, _far;

    public static Frustum FromViewProjection(in Matrix4x4 m) => new()
    {
        _left = Normalize(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
        _right = Normalize(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
        _bottom = Normalize(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
        _top = Normalize(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
        _near = Normalize(new Vector4(m.M13, m.M23, m.M33, m.M43)),
        _far = Normalize(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)),
    };

    private static Vector4 Normalize(Vector4 p)
    {
        float len = new Vector3(p.X, p.Y, p.Z).Length();
        return len > 0 ? p / len : p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dist(in Vector4 p, Vector3 c) => p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W;

    /// <summary>True when the sphere is at least partially inside the frustum.</summary>
    public readonly bool Intersects(in BoundingSphere s)
    {
        if (!s.IsValid) return false;
        float r = -s.Radius;
        var c = s.Center;
        return Dist(_left, c) >= r && Dist(_right, c) >= r && Dist(_bottom, c) >= r &&
               Dist(_top, c) >= r && Dist(_near, c) >= r && Dist(_far, c) >= r;
    }

    public readonly bool Contains(Vector3 p) => Intersects(new BoundingSphere(p, 0));
}

public static class MathUtil
{
    public const float DegToRad = MathF.PI / 180f;
    public const float RadToDeg = 180f / MathF.PI;

    public static float Radians(float degrees) => degrees * DegToRad;
    public static float Degrees(float radians) => radians * RadToDeg;

    public static Matrix4x4 Perspective(float fovYDegrees, float aspect, float near, float far) =>
        Matrix4x4.CreatePerspectiveFieldOfView(Radians(fovYDegrees), MathF.Max(aspect, 1e-4f), near, far);

    /// <summary>
    /// Heading (degrees clockwise from +Y/north about +Z), pitch (about +X), roll (about +Y) to a quaternion.
    /// This is the OSG / simulation (Z-up, Y-north) convention.
    /// </summary>
    public static Quaternion FromHeadingPitchRoll(float headingDeg, float pitchDeg, float rollDeg)
    {
        var r = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Radians(rollDeg));
        var p = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Radians(pitchDeg));
        var h = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -Radians(headingDeg));
        // Hamilton product: (h * p * r) rotates by r first, then p, then h (all about world axes),
        // which equals intrinsic heading -> pitch -> roll.
        return h * p * r;
    }

    public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }
}
