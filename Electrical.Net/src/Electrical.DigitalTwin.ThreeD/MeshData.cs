using System.Numerics;

namespace Electrical.DigitalTwin.ThreeD;

/// <summary>Indexed triangle mesh in world coordinates [m] (right-handed, +Y up), with per-vertex normals.</summary>
public sealed class MeshData
{
    private readonly List<Vector3> _positions = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<uint> _indices = [];

    public IReadOnlyList<Vector3> Positions => _positions;
    public IReadOnlyList<Vector3> Normals => _normals;
    public IReadOnlyList<uint> Indices => _indices;
    public int VertexCount => _positions.Count;
    public int TriangleCount => _indices.Count / 3;

    /// <summary>Axis-aligned bounds (min, max); zero for an empty mesh.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (_positions.Count == 0) return (Vector3.Zero, Vector3.Zero);
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in _positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        return (lo, hi);
    }

    private uint V(Vector3 p, Vector3 n)
    {
        _positions.Add(p);
        _normals.Add(n);
        return (uint)(_positions.Count - 1);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        uint i0 = V(a, n), i1 = V(b, n), i2 = V(c, n), i3 = V(d, n);
        _indices.AddRange([i0, i1, i2, i0, i2, i3]);
    }

    /// <summary>Oriented box centred at <paramref name="center"/> with half extents along the (orthonormal) axes.</summary>
    public MeshData AddBox(Vector3 center, Vector3 halfSize, Vector3? xAxis = null, Vector3? yAxis = null)
    {
        var ax = Vector3.Normalize(xAxis ?? Vector3.UnitX);
        var ay = Vector3.Normalize(yAxis ?? Vector3.UnitY);
        if (MathF.Abs(Vector3.Dot(ax, ay)) > 1e-3f) ay = Vector3.Normalize(ay - Vector3.Dot(ay, ax) * ax);
        var az = Vector3.Cross(ax, ay);
        Vector3 X = ax * halfSize.X, Y = ay * halfSize.Y, Z = az * halfSize.Z;
        Vector3 P(int sx, int sy, int sz) => center + sx * X + sy * Y + sz * Z;
        Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), ax);
        Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), -ax);
        Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), ay);
        Quad(P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), -ay);
        Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), az);
        Quad(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), -az);
        return this;
    }

    /// <summary>Box spanning two points (a beam) with the given cross-section half sizes.</summary>
    public MeshData AddBeam(Vector3 a, Vector3 b, float halfWidth, float halfHeight)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return this;
        var ax = d / len;
        var up = MathF.Abs(ax.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
        return AddBox((a + b) / 2, new Vector3(len / 2, halfHeight, halfWidth), ax, up);
    }

    /// <summary>Capped cylinder (or cone frustum) between two points.</summary>
    public MeshData AddCylinder(Vector3 a, Vector3 b, float radiusA, float radiusB, int segments = 12)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return this;
        var axis = d / len;
        Basis(axis, out var u, out var v);
        int s = Math.Max(3, segments);
        float slope = (radiusA - radiusB) / len;
        for (int i = 0; i < s; i++)
        {
            float t0 = 2 * MathF.PI * i / s, t1 = 2 * MathF.PI * (i + 1) / s;
            Vector3 r0 = MathF.Cos(t0) * u + MathF.Sin(t0) * v, r1 = MathF.Cos(t1) * u + MathF.Sin(t1) * v;
            var n0 = Vector3.Normalize(r0 + slope * axis); var n1 = Vector3.Normalize(r1 + slope * axis);
            uint a0 = V(a + radiusA * r0, n0), a1 = V(a + radiusA * r1, n1), b1 = V(b + radiusB * r1, n1), b0 = V(b + radiusB * r0, n0);
            _indices.AddRange([a0, a1, b1, a0, b1, b0]);
        }
        Cap(a, -axis, radiusA, u, v, s);
        Cap(b, axis, radiusB, u, v, s);
        return this;
    }

    private void Cap(Vector3 c, Vector3 n, float r, Vector3 u, Vector3 v, int s)
    {
        if (r <= 0) return;
        uint ci = V(c, n);
        uint first = (uint)_positions.Count;
        for (int i = 0; i < s; i++)
        {
            float t = 2 * MathF.PI * i / s;
            V(c + r * (MathF.Cos(t) * u + MathF.Sin(t) * v), n);
        }
        bool flip = Vector3.Dot(Vector3.Cross(u, v), n) < 0;
        for (int i = 0; i < s; i++)
        {
            uint p = first + (uint)i, q = first + (uint)((i + 1) % s);
            if (flip) _indices.AddRange([ci, q, p]); else _indices.AddRange([ci, p, q]);
        }
    }

    /// <summary>Tube of constant radius along a polyline (conductor, cable), with smooth normals and open ends.</summary>
    public MeshData AddTube(IReadOnlyList<Vector3> path, float radius, int segments = 8)
    {
        if (path.Count < 2) return this;
        int s = Math.Max(3, segments);
        var tangentStart = Vector3.Normalize(path[1] - path[0]);
        Basis(tangentStart, out var u, out var v);
        uint baseIndex = (uint)_positions.Count;
        for (int k = 0; k < path.Count; k++)
        {
            Vector3 t = k == 0 ? path[1] - path[0] : k == path.Count - 1 ? path[k] - path[k - 1] : path[k + 1] - path[k - 1];
            t = t.LengthSquared() > 0 ? Vector3.Normalize(t) : tangentStart;
            // Parallel transport of the frame (rotation-minimizing) avoids twisting.
            u = Vector3.Normalize(u - Vector3.Dot(u, t) * t);
            v = Vector3.Cross(t, u);
            for (int i = 0; i < s; i++)
            {
                float a = 2 * MathF.PI * i / s;
                var n = MathF.Cos(a) * u + MathF.Sin(a) * v;
                V(path[k] + radius * n, n);
            }
        }
        for (int k = 0; k < path.Count - 1; k++)
            for (int i = 0; i < s; i++)
            {
                uint a0 = baseIndex + (uint)(k * s + i), a1 = baseIndex + (uint)(k * s + (i + 1) % s);
                uint b0 = a0 + (uint)s, b1 = a1 + (uint)s;
                _indices.AddRange([a0, a1, b1, a0, b1, b0]);
            }
        return this;
    }

    /// <summary>Horizontal rectangle (ground) at height <paramref name="y"/>, facing up.</summary>
    public MeshData AddGround(Vector3 min, Vector3 max, float y = 0)
    {
        Quad(new(min.X, y, max.Z), new(max.X, y, max.Z), new(max.X, y, min.Z), new(min.X, y, min.Z), Vector3.UnitY);
        return this;
    }

    internal static void Basis(Vector3 axis, out Vector3 u, out Vector3 v)
    {
        var helper = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(axis, helper));
        v = Vector3.Cross(axis, u);
    }
}

/// <summary>Catenary sag of an overhead conductor between two attachment points.</summary>
public static class Catenary
{
    /// <summary>
    /// Catenary parameter a = H/w [m] giving mid-span sag <paramref name="sagM"/> for horizontal span <paramref name="spanM"/>:
    /// solves s = a(cosh(L/2a) − 1) by Newton's method.
    /// </summary>
    public static double Parameter(double spanM, double sagM)
    {
        if (sagM <= 0 || spanM <= 0) return double.PositiveInfinity;
        double half = spanM / 2, a = half * half / (2 * sagM); // parabolic first guess
        for (int i = 0; i < 50; i++)
        {
            double x = half / a;
            double f = a * (Math.Cosh(x) - 1) - sagM;
            double df = Math.Cosh(x) - 1 - x * Math.Sinh(x);
            double step = f / df;
            a -= step;
            if (a <= 0) a = half / 50;
            if (Math.Abs(step) < 1e-10 * a) break;
        }
        return a;
    }

    /// <summary>
    /// Points of the conductor from <paramref name="a"/> to <paramref name="b"/> (attachment points may differ in height): the chord
    /// plus the level-span catenary sag curve a(cosh((x − L/2)/a) − cosh(L/2a)), whose mid-span depth below the chord is <paramref name="sagM"/>.
    /// </summary>
    public static Vector3[] Points(Vector3 a, Vector3 b, double sagM, int segments = 16)
    {
        int n = Math.Max(2, segments);
        var h = new Vector2(b.X - a.X, b.Z - a.Z);
        double span = h.Length();
        double c = Parameter(span, sagM);
        var p = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            var chord = Vector3.Lerp(a, b, (float)t);
            double dip = double.IsInfinity(c) ? 0 : c * (Math.Cosh((t * span - span / 2) / c) - Math.Cosh(span / (2 * c)));
            p[i] = chord + new Vector3(0, (float)dip, 0);
        }
        return p;
    }
}
