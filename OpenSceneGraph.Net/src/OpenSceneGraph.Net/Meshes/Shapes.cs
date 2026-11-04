using System.Numerics;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Meshes;

/// <summary>
/// Tessellated primitive shapes (<c>osg::ShapeDrawable</c> equivalents). All shapes are Z-up, centred at the origin,
/// with normals and texture coordinates.
/// </summary>
public static class Shapes
{
    public static Geometry Box(float sx, float sy, float sz, Vector4? color = null)
    {
        var h = new Vector3(sx, sy, sz) * 0.5f;
        var pos = new List<Vector3>(24);
        var nor = new List<Vector3>(24);
        var uv = new List<Vector2>(24);
        var idx = new List<uint>(36);

        void Face(Vector3 n, Vector3 u, Vector3 v)
        {
            uint b = (uint)pos.Count;
            var c = n * h;
            var du = u * h;
            var dv = v * h;
            pos.Add(c - du - dv); pos.Add(c + du - dv); pos.Add(c + du + dv); pos.Add(c - du + dv);
            for (int i = 0; i < 4; i++) nor.Add(n);
            uv.Add(new(0, 0)); uv.Add(new(1, 0)); uv.Add(new(1, 1)); uv.Add(new(0, 1));
            idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }

        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        Face(-Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ);
        Face(Vector3.UnitY, -Vector3.UnitX, Vector3.UnitZ);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY);

        return Finish(new Geometry { Name = "Box", Positions = pos.ToArray(), Normals = nor.ToArray(), TexCoords = uv.ToArray(), Indices = idx.ToArray() }, color);
    }

    public static Geometry Cube(float size, Vector4? color = null) => Box(size, size, size, color);

    public static Geometry Sphere(float radius, int segments = 32, int rings = 16, Vector4? color = null)
    {
        segments = System.Math.Max(3, segments);
        rings = System.Math.Max(2, rings);
        int vc = (segments + 1) * (rings + 1);
        var pos = new Vector3[vc];
        var nor = new Vector3[vc];
        var uv = new Vector2[vc];
        int k = 0;
        for (int r = 0; r <= rings; r++)
        {
            float phi = MathF.PI * r / rings; // 0 at +Z
            float sp = MathF.Sin(phi), cp = MathF.Cos(phi);
            for (int s = 0; s <= segments; s++)
            {
                float th = 2 * MathF.PI * s / segments;
                var n = new Vector3(sp * MathF.Cos(th), sp * MathF.Sin(th), cp);
                pos[k] = n * radius;
                nor[k] = n;
                uv[k] = new Vector2((float)s / segments, (float)r / rings);
                k++;
            }
        }
        var idx = GridIndices(segments, rings, flip: true);
        return Finish(new Geometry { Name = "Sphere", Positions = pos, Normals = nor, TexCoords = uv, Indices = idx }, color);
    }

    /// <summary>Cylinder along Z with caps.</summary>
    public static Geometry Cylinder(float radius, float height, int segments = 32, Vector4? color = null) =>
        Cone(radius, radius, height, segments, color, "Cylinder");

    /// <summary>Truncated cone along Z (bottom radius at -h/2, top radius at +h/2) with caps.</summary>
    public static Geometry Cone(float bottomRadius, float topRadius, float height, int segments = 32, Vector4? color = null, string name = "Cone")
    {
        segments = System.Math.Max(3, segments);
        var pos = new List<Vector3>();
        var nor = new List<Vector3>();
        var uv = new List<Vector2>();
        var idx = new List<uint>();
        float hz = height * 0.5f;
        float slope = (bottomRadius - topRadius) / height;

        for (int s = 0; s <= segments; s++)
        {
            float th = 2 * MathF.PI * s / segments;
            float c = MathF.Cos(th), sn = MathF.Sin(th);
            var n = Vector3.Normalize(new Vector3(c, sn, slope));
            pos.Add(new Vector3(c * bottomRadius, sn * bottomRadius, -hz)); nor.Add(n); uv.Add(new((float)s / segments, 0));
            pos.Add(new Vector3(c * topRadius, sn * topRadius, hz)); nor.Add(n); uv.Add(new((float)s / segments, 1));
        }
        for (uint s = 0; s < segments; s++)
        {
            uint a = s * 2, b = a + 1, c = a + 2, d = a + 3;
            idx.AddRange([a, c, b, b, c, d]);
        }

        void Cap(float z, float r, bool top)
        {
            if (r <= 0) return;
            uint center = (uint)pos.Count;
            var n = top ? Vector3.UnitZ : -Vector3.UnitZ;
            pos.Add(new Vector3(0, 0, z)); nor.Add(n); uv.Add(new(0.5f, 0.5f));
            for (int s = 0; s <= segments; s++)
            {
                float th = 2 * MathF.PI * s / segments;
                pos.Add(new Vector3(MathF.Cos(th) * r, MathF.Sin(th) * r, z)); nor.Add(n);
                uv.Add(new(0.5f + 0.5f * MathF.Cos(th), 0.5f + 0.5f * MathF.Sin(th)));
            }
            for (uint s = 0; s < segments; s++)
            {
                uint a = center + 1 + s, b = a + 1;
                if (top) idx.AddRange([center, a, b]); else idx.AddRange([center, b, a]);
            }
        }

        Cap(-hz, bottomRadius, false);
        Cap(hz, topRadius, true);
        return Finish(new Geometry { Name = name, Positions = pos.ToArray(), Normals = nor.ToArray(), TexCoords = uv.ToArray(), Indices = idx.ToArray() }, color);
    }

    public static Geometry Capsule(float radius, float height, int segments = 24, int rings = 8, Vector4? color = null)
    {
        var g = Sphere(radius, segments, rings * 2, color);
        float half = MathF.Max(0, height * 0.5f - radius);
        var p = g.Positions;
        for (int i = 0; i < p.Length; i++) p[i].Z += p[i].Z >= 0 ? half : -half;
        g.Name = "Capsule";
        g.MarkDirty();
        return g;
    }

    public static Geometry Torus(float majorRadius, float minorRadius, int majorSegments = 48, int minorSegments = 16, Vector4? color = null)
    {
        int vc = (majorSegments + 1) * (minorSegments + 1);
        var pos = new Vector3[vc];
        var nor = new Vector3[vc];
        var uv = new Vector2[vc];
        int k = 0;
        for (int j = 0; j <= minorSegments; j++)
        {
            float v = 2 * MathF.PI * j / minorSegments;
            for (int i = 0; i <= majorSegments; i++)
            {
                float u = 2 * MathF.PI * i / majorSegments;
                var dir = new Vector3(MathF.Cos(u), MathF.Sin(u), 0);
                var n = dir * MathF.Cos(v) + Vector3.UnitZ * MathF.Sin(v);
                pos[k] = dir * majorRadius + n * minorRadius;
                nor[k] = n;
                uv[k] = new((float)i / majorSegments, (float)j / minorSegments);
                k++;
            }
        }
        return Finish(new Geometry { Name = "Torus", Positions = pos, Normals = nor, TexCoords = uv, Indices = GridIndices(majorSegments, minorSegments, flip: false) }, color);
    }

    /// <summary>Flat grid in the XY plane, facing +Z. <paramref name="height"/> optionally displaces Z.</summary>
    public static Geometry Plane(float sizeX, float sizeY, int divX = 1, int divY = 1, Vector4? color = null, Func<float, float, float>? height = null)
    {
        int vc = (divX + 1) * (divY + 1);
        var pos = new Vector3[vc];
        var uv = new Vector2[vc];
        int k = 0;
        for (int y = 0; y <= divY; y++)
            for (int x = 0; x <= divX; x++)
            {
                float u = (float)x / divX, v = (float)y / divY;
                float px = (u - 0.5f) * sizeX, py = (v - 0.5f) * sizeY;
                pos[k] = new Vector3(px, py, height?.Invoke(px, py) ?? 0);
                uv[k] = new Vector2(u * divX / 4f, v * divY / 4f);
                k++;
            }
        var g = new Geometry { Name = "Plane", Positions = pos, TexCoords = uv, Indices = GridIndices(divX, divY, flip: false) };
        if (height is null)
        {
            var n = new Vector3[vc];
            Array.Fill(n, Vector3.UnitZ);
            g.Normals = n;
        }
        else g.ComputeNormals();
        return Finish(g, color);
    }

    /// <summary>Line grid in the XY plane (a ground reference).</summary>
    public static Geometry GridLines(float size, int divisions, Vector4? color = null)
    {
        var pos = new List<Vector3>();
        float h = size / 2;
        for (int i = 0; i <= divisions; i++)
        {
            float t = -h + size * i / divisions;
            pos.Add(new(t, -h, 0)); pos.Add(new(t, h, 0));
            pos.Add(new(-h, t, 0)); pos.Add(new(h, t, 0));
        }
        var g = new Geometry { Name = "Grid", Positions = pos.ToArray(), PrimitiveType = PrimitiveType.Lines };
        g.GetOrCreateStateSet().Lighting = false;
        return Finish(g, color ?? new Vector4(0.5f, 0.55f, 0.6f, 1));
    }

    /// <summary>Three colored axis lines (X red, Y green, Z blue).</summary>
    public static Geometry Axes(float length = 1f)
    {
        var g = new Geometry
        {
            Name = "Axes",
            PrimitiveType = PrimitiveType.Lines,
            Positions = [Vector3.Zero, Vector3.UnitX * length, Vector3.Zero, Vector3.UnitY * length, Vector3.Zero, Vector3.UnitZ * length],
            Colors = [Color.Red, Color.Red, Color.Green, Color.Green, Color.Blue, Color.Blue],
        };
        g.GetOrCreateStateSet().Lighting = false;
        return g;
    }

    /// <summary>Polyline through the points.</summary>
    public static Geometry LineStrip(IReadOnlyList<Vector3> points, Vector4 color)
    {
        var g = new Geometry { Name = "LineStrip", Positions = points.ToArray(), PrimitiveType = PrimitiveType.LineStrip };
        g.GetOrCreateStateSet().Lighting = false;
        return Finish(g, color);
    }

    /// <summary>Point cloud with optional per-point colors.</summary>
    public static Geometry Points(Vector3[] points, Vector4[]? colors = null, float pointSize = 3f)
    {
        var g = new Geometry { Name = "Points", Positions = points, Colors = colors, PrimitiveType = PrimitiveType.Points };
        var ss = g.GetOrCreateStateSet();
        ss.Lighting = false;
        ss.PointSize = pointSize;
        return g;
    }

    /// <summary>Arrow along +Z from the origin, made of a cylinder shaft and cone head (a vector glyph).</summary>
    public static Geometry Arrow(float length, float radius, Vector4? color = null)
    {
        float headLen = MathF.Min(length * 0.3f, radius * 6);
        var shaft = Cylinder(radius, length - headLen, 12);
        var head = Cone(radius * 2.5f, 0, headLen, 12);
        var merged = MeshUtil.Merge([
            (shaft, Matrix4x4.CreateTranslation(0, 0, (length - headLen) / 2)),
            (head, Matrix4x4.CreateTranslation(0, 0, length - headLen / 2)),
        ]);
        merged.Name = "Arrow";
        return Finish(merged, color);
    }

    internal static uint[] GridIndices(int cols, int rows, bool flip)
    {
        var idx = new uint[cols * rows * 6];
        int k = 0;
        int stride = cols + 1;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                uint a = (uint)(r * stride + c), b = a + 1, d = (uint)((r + 1) * stride + c), e = d + 1;
                if (!flip) { idx[k++] = a; idx[k++] = b; idx[k++] = e; idx[k++] = a; idx[k++] = e; idx[k++] = d; }
                else { idx[k++] = a; idx[k++] = e; idx[k++] = b; idx[k++] = a; idx[k++] = d; idx[k++] = e; }
            }
        return idx;
    }

    private static Geometry Finish(Geometry g, Vector4? color)
    {
        if (color is { } c) g.GetOrCreateStateSet().Material = Material.FromColor(c);
        return g;
    }
}
