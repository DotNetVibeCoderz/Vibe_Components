
using System.Numerics;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Science;

/// <summary>Perceptual and classic colormaps for scientific data.</summary>
public static class ColorMap
{
    private static readonly Vector3[] s_viridis = [new(0.267f, 0.005f, 0.329f), new(0.283f, 0.141f, 0.458f), new(0.254f, 0.265f, 0.530f), new(0.207f, 0.372f, 0.553f), new(0.164f, 0.471f, 0.558f), new(0.128f, 0.567f, 0.551f), new(0.135f, 0.659f, 0.518f), new(0.267f, 0.749f, 0.441f), new(0.478f, 0.821f, 0.318f), new(0.741f, 0.873f, 0.150f), new(0.993f, 0.906f, 0.144f)];
    private static readonly Vector3[] s_inferno = [new(0.001f, 0.000f, 0.014f), new(0.087f, 0.045f, 0.224f), new(0.258f, 0.039f, 0.406f), new(0.416f, 0.090f, 0.433f), new(0.578f, 0.148f, 0.404f), new(0.735f, 0.216f, 0.330f), new(0.865f, 0.317f, 0.226f), new(0.954f, 0.469f, 0.098f), new(0.988f, 0.645f, 0.040f), new(0.964f, 0.844f, 0.273f), new(0.988f, 0.998f, 0.645f)];
    private static readonly Vector3[] s_coolwarm = [new(0.230f, 0.299f, 0.754f), new(0.484f, 0.619f, 0.983f), new(0.865f, 0.865f, 0.865f), new(0.968f, 0.555f, 0.437f), new(0.706f, 0.016f, 0.150f)];
    private static readonly Vector3[] s_jet = [new(0, 0, 0.5f), new(0, 0, 1), new(0, 0.5f, 1), new(0, 1, 1), new(0.5f, 1, 0.5f), new(1, 1, 0), new(1, 0.5f, 0), new(1, 0, 0), new(0.5f, 0, 0)];
    private static readonly Vector3[] s_bone = [new(0, 0, 0), new(0.32f, 0.32f, 0.44f), new(0.65f, 0.78f, 0.78f), new(1, 1, 1)];

    public enum Kind { Viridis, Inferno, CoolWarm, Jet, Bone, Grayscale }

    /// <summary>Maps <paramref name="t"/> in [0,1] to a colour.</summary>
    public static Vector4 Sample(Kind kind, float t, float alpha = 1)
    {
        t = float.IsNaN(t) ? 0 : System.Math.Clamp(t, 0, 1);
        if (kind == Kind.Grayscale) return new Vector4(t, t, t, alpha);
        var lut = kind switch { Kind.Inferno => s_inferno, Kind.CoolWarm => s_coolwarm, Kind.Jet => s_jet, Kind.Bone => s_bone, _ => s_viridis };
        float f = t * (lut.Length - 1);
        int i = System.Math.Min((int)f, lut.Length - 2);
        return new Vector4(Vector3.Lerp(lut[i], lut[i + 1], f - i), alpha);
    }

    public static Vector4 Map(Kind kind, float value, float min, float max, float alpha = 1) =>
        Sample(kind, max > min ? (value - min) / (max - min) : 0, alpha);

    /// <summary>A horizontal legend texture for the colormap.</summary>
    public static Image Legend(Kind kind, int width = 256, int height = 16) => Image.Generate(width, height, (u, _) => Sample(kind, u));
}

/// <summary>A regular 3D grid of scalar samples (CT/MRI volumes, CFD pressure, seismic cubes, FEM fields).</summary>
public sealed class ScalarField3D
{
    public ScalarField3D(int nx, int ny, int nz, Vector3 spacing, Vector3 origin = default, float[]? values = null)
    {
        Nx = nx; Ny = ny; Nz = nz; Spacing = spacing; Origin = origin;
        Values = values ?? new float[nx * ny * nz];
        if (Values.Length != nx * ny * nz) throw new ArgumentException("values must have nx*ny*nz entries");
    }

    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    public Vector3 Spacing { get; }
    public Vector3 Origin { get; }
    public float[] Values { get; }

    public float this[int x, int y, int z]
    {
        get => Values[(z * Ny + y) * Nx + x];
        set => Values[(z * Ny + y) * Nx + x] = value;
    }

    public Vector3 PositionOf(int x, int y, int z) => Origin + new Vector3(x, y, z) * Spacing;

    public (float Min, float Max) Range()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in Values) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
        return (min, max);
    }

    /// <summary>Fills the field from a function of world position (parallel over slices).</summary>
    public static ScalarField3D FromFunction(int n, float extent, Func<Vector3, float> f)
    {
        float s = extent / (n - 1);
        var field = new ScalarField3D(n, n, n, new Vector3(s), new Vector3(-extent / 2));
        Parallel.For(0, n, z =>
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    field[x, y, z] = f(field.PositionOf(x, y, z));
        });
        return field;
    }

    /// <summary>A synthetic "head phantom" CT-like volume: skull shell, brain, ventricles.</summary>
    public static ScalarField3D Phantom(int n = 64) => FromFunction(n, 2f, p =>
    {
        var q = p / new Vector3(0.75f, 0.9f, 0.95f);
        float r = q.Length();
        float skull = MathF.Exp(-MathF.Pow((r - 0.9f) / 0.05f, 2)) * 1000;
        float brain = r < 0.85f ? 380 + 40 * MathF.Sin(p.X * 25) * MathF.Sin(p.Y * 25) : 0;
        var v1 = (p - new Vector3(0.18f, 0.05f, 0.1f)) / new Vector3(0.08f, 0.3f, 0.12f);
        var v2 = (p - new Vector3(-0.18f, 0.05f, 0.1f)) / new Vector3(0.08f, 0.3f, 0.12f);
        float ventricles = v1.LengthSquared() < 1 || v2.LengthSquared() < 1 ? -300 : 0;
        return skull + brain + ventricles;
    });
}

/// <summary>
/// Isosurface extraction by marching tetrahedra (each cube split into 6 tetrahedra): watertight, no ambiguity cases,
/// no large lookup tables. Vertices on shared edges are welded; normals come from the field gradient.
/// </summary>
public static class Isosurface
{
    private static readonly int[][] s_tets =
    [
        [0, 5, 1, 6], [0, 1, 2, 6], [0, 2, 3, 6], [0, 3, 7, 6], [0, 7, 4, 6], [0, 4, 5, 6],
    ];

    private static readonly Vector3[] s_corner =
    [
        new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1),
    ];

    public static Geometry Extract(ScalarField3D f, float iso, Vector4? color = null, ColorMap.Kind? colorByHeight = null)
    {
        var positions = new List<Vector3>();
        var indices = new List<uint>();
        var weld = new Dictionary<(long, long), uint>();
        var v = new float[8];
        var id = new long[8];

        for (int z = 0; z < f.Nz - 1; z++)
            for (int y = 0; y < f.Ny - 1; y++)
                for (int x = 0; x < f.Nx - 1; x++)
                {
                    bool any = false, below = false;
                    for (int c = 0; c < 8; c++)
                    {
                        var o = s_corner[c];
                        int cx = x + (int)o.X, cy = y + (int)o.Y, cz = z + (int)o.Z;
                        v[c] = f[cx, cy, cz];
                        id[c] = ((long)cz * f.Ny + cy) * f.Nx + cx;
                        if (v[c] >= iso) any = true; else below = true;
                    }
                    if (!any || !below) continue;

                    foreach (var tet in s_tets) Tetra(f, iso, tet, x, y, z, v, id, positions, indices, weld);
                }

        var pos = positions.ToArray();
        var nor = new Vector3[pos.Length];
        for (int i = 0; i < pos.Length; i++) nor[i] = -Gradient(f, pos[i]);
        for (int i = 0; i < nor.Length; i++) nor[i] = nor[i].LengthSquared() > 1e-20f ? Vector3.Normalize(nor[i]) : Vector3.UnitZ;

        var g = new Geometry { Name = $"isosurface {iso}", Positions = pos, Normals = nor, Indices = indices.ToArray() };
        var ss = g.GetOrCreateStateSet();
        ss.Material = Material.FromColor(color ?? new Vector4(0.9f, 0.85f, 0.75f, 1));
        ss.Material.Specular = new Vector3(0.4f);
        ss.CullFace = CullFaceMode.None;
        if (colorByHeight is { } kind && pos.Length > 0)
        {
            var bb = Math.BoundingBox.FromPoints(pos);
            g.Colors = pos.Select(p => ColorMap.Map(kind, p.Z, bb.Min.Z, bb.Max.Z)).ToArray();
            ss.Material = new Material { Diffuse = Vector4.One, Specular = new Vector3(0.35f), Shininess = 48 };
        }
        return g;
    }

    private static void Tetra(ScalarField3D f, float iso, int[] t, int x, int y, int z, float[] v, long[] id,
        List<Vector3> pos, List<uint> idx, Dictionary<(long, long), uint> weld)
    {
        int mask = 0;
        for (int i = 0; i < 4; i++) if (v[t[i]] >= iso) mask |= 1 << i;
        if (mask == 0 || mask == 15) return;

        uint E(int a, int b)
        {
            int ca = t[a], cb = t[b];
            long ka = id[ca], kb = id[cb];
            var key = ka < kb ? (ka, kb) : (kb, ka);
            if (weld.TryGetValue(key, out uint i)) return i;
            float va = v[ca], vb = v[cb];
            float s = MathF.Abs(vb - va) > 1e-12f ? (iso - va) / (vb - va) : 0.5f;
            var pa = f.PositionOf(x + (int)s_corner[ca].X, y + (int)s_corner[ca].Y, z + (int)s_corner[ca].Z);
            var pb = f.PositionOf(x + (int)s_corner[cb].X, y + (int)s_corner[cb].Y, z + (int)s_corner[cb].Z);
            i = (uint)pos.Count;
            pos.Add(Vector3.Lerp(pa, pb, s));
            weld[key] = i;
            return i;
        }

        void Tri(uint a, uint b, uint c) { idx.Add(a); idx.Add(b); idx.Add(c); }

        // 14 non-trivial cases reduce to "one vertex separated" (triangle) or "two/two" (quad)
        switch (mask)
        {
            case 0x1: Tri(E(0, 1), E(0, 3), E(0, 2)); break;
            case 0xE: Tri(E(0, 1), E(0, 2), E(0, 3)); break;
            case 0x2: Tri(E(1, 0), E(1, 2), E(1, 3)); break;
            case 0xD: Tri(E(1, 0), E(1, 3), E(1, 2)); break;
            case 0x4: Tri(E(2, 0), E(2, 3), E(2, 1)); break;
            case 0xB: Tri(E(2, 0), E(2, 1), E(2, 3)); break;
            case 0x8: Tri(E(3, 0), E(3, 1), E(3, 2)); break;
            case 0x7: Tri(E(3, 0), E(3, 2), E(3, 1)); break;
            case 0x3: { uint a = E(0, 3), b = E(0, 2), c = E(1, 3), d = E(1, 2); Tri(a, b, c); Tri(c, b, d); break; }
            case 0xC: { uint a = E(0, 3), b = E(0, 2), c = E(1, 3), d = E(1, 2); Tri(a, c, b); Tri(c, d, b); break; }
            case 0x5: { uint a = E(0, 1), b = E(2, 1), c = E(0, 3), d = E(2, 3); Tri(a, b, c); Tri(c, b, d); break; }
            case 0xA: { uint a = E(0, 1), b = E(2, 1), c = E(0, 3), d = E(2, 3); Tri(a, c, b); Tri(c, d, b); break; }
            case 0x6: { uint a = E(1, 0), b = E(1, 3), c = E(2, 0), d = E(2, 3); Tri(a, b, c); Tri(c, b, d); break; }
            case 0x9: { uint a = E(1, 0), b = E(1, 3), c = E(2, 0), d = E(2, 3); Tri(a, c, b); Tri(c, d, b); break; }
        }
    }

    /// <summary>Central-difference gradient at a world position (nearest sample).</summary>
    private static Vector3 Gradient(ScalarField3D f, Vector3 p)
    {
        var g = (p - f.Origin) / f.Spacing;
        int x = System.Math.Clamp((int)MathF.Round(g.X), 1, f.Nx - 2);
        int y = System.Math.Clamp((int)MathF.Round(g.Y), 1, f.Ny - 2);
        int z = System.Math.Clamp((int)MathF.Round(g.Z), 1, f.Nz - 2);
        return new Vector3(
            (f[x + 1, y, z] - f[x - 1, y, z]) / (2 * f.Spacing.X),
            (f[x, y + 1, z] - f[x, y - 1, z]) / (2 * f.Spacing.Y),
            (f[x, y, z + 1] - f[x, y, z - 1]) / (2 * f.Spacing.Z));
    }
}

/// <summary>Vector field glyphs (velocity arrows for CFD, wind, magnetic fields).</summary>
public static class VectorField
{
    /// <summary>
    /// Samples <paramref name="field"/> on a grid and builds one merged mesh of arrows coloured by magnitude.
    /// </summary>
    public static Geometry Glyphs(Func<Vector3, Vector3> field, Vector3 min, Vector3 max, int nx, int ny, int nz, float scale = 1, ColorMap.Kind map = ColorMap.Kind.Viridis)
    {
        var samples = new List<(Vector3 P, Vector3 V)>();
        for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    var t = new Vector3(nx > 1 ? x / (nx - 1f) : 0.5f, ny > 1 ? y / (ny - 1f) : 0.5f, nz > 1 ? z / (nz - 1f) : 0.5f);
                    var p = min + (max - min) * t;
                    samples.Add((p, field(p)));
                }
        float maxMag = samples.Count == 0 ? 1 : MathF.Max(1e-6f, samples.Max(s => s.V.Length()));
        var arrow = Shapes.Arrow(1, 0.05f);
        var pos = new List<Vector3>();
        var nor = new List<Vector3>();
        var col = new List<Vector4>();
        var idx = new List<uint>();
        foreach (var (p, v) in samples)
        {
            float mag = v.Length();
            if (mag < 1e-9f) continue;
            var dir = v / mag;
            var rot = RotationFromZ(dir);
            float len = scale * (0.25f + 0.75f * mag / maxMag);
            var world = Matrix4x4.CreateScale(len) * rot * Matrix4x4.CreateTranslation(p);
            var c = ColorMap.Map(map, mag, 0, maxMag);
            uint b = (uint)pos.Count;
            for (int i = 0; i < arrow.Positions.Length; i++)
            {
                pos.Add(Vector3.Transform(arrow.Positions[i], world));
                nor.Add(Vector3.Normalize(Vector3.TransformNormal(arrow.Normals![i], rot)));
                col.Add(c);
            }
            foreach (var i in arrow.Indices!) idx.Add(b + i);
        }
        var g = new Geometry { Name = "VectorGlyphs", Positions = pos.ToArray(), Normals = nor.ToArray(), Colors = col.ToArray(), Indices = idx.ToArray() };
        g.GetOrCreateStateSet().Material = new Material { Diffuse = Vector4.One, Specular = new Vector3(0.3f) };
        return g;
    }

    /// <summary>Integrates streamlines (RK4) from seed points, returning a line mesh coloured by speed.</summary>
    public static Geometry Streamlines(Func<Vector3, Vector3> field, IEnumerable<Vector3> seeds, int steps = 200, float stepSize = 0.05f, ColorMap.Kind map = ColorMap.Kind.Inferno)
    {
        var pos = new List<Vector3>();
        var speeds = new List<float>();
        foreach (var seed in seeds)
        {
            var p = seed;
            for (int i = 0; i < steps; i++)
            {
                var k1 = field(p);
                var k2 = field(p + k1 * (stepSize / 2));
                var k3 = field(p + k2 * (stepSize / 2));
                var k4 = field(p + k3 * stepSize);
                var next = p + (k1 + 2 * k2 + 2 * k3 + k4) * (stepSize / 6);
                if (!float.IsFinite(next.X) || (next - p).LengthSquared() < 1e-12f) break;
                pos.Add(p); pos.Add(next);
                float s = k1.Length();
                speeds.Add(s); speeds.Add(s);
                p = next;
            }
        }
        float max = speeds.Count == 0 ? 1 : speeds.Max();
        var g = new Geometry
        {
            Name = "Streamlines",
            PrimitiveType = PrimitiveType.Lines,
            Positions = pos.ToArray(),
            Colors = speeds.Select(s => ColorMap.Map(map, s, 0, max)).ToArray(),
        };
        var ss = g.GetOrCreateStateSet();
        ss.Lighting = false;
        ss.LineWidth = 2;
        return g;
    }

    private static Matrix4x4 RotationFromZ(Vector3 dir)
    {
        float d = Vector3.Dot(Vector3.UnitZ, dir);
        if (d > 0.9999f) return Matrix4x4.Identity;
        if (d < -0.9999f) return Matrix4x4.CreateRotationX(MathF.PI);
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, dir));
        return Matrix4x4.CreateFromAxisAngle(axis, MathF.Acos(d));
    }
}

/// <summary>Slices and point clouds for volumetric data (medical imaging, seismic, LiDAR).</summary>
public static class VolumeSlices
{
    public enum Axis { X, Y, Z }

    /// <summary>A textured quad showing one slice of the field through the colormap.</summary>
    public static Geometry Slice(ScalarField3D f, Axis axis, int index, ColorMap.Kind map = ColorMap.Kind.Bone, float? min = null, float? max = null)
    {
        var (lo, hi) = f.Range();
        float mn = min ?? lo, mx = max ?? hi;
        int w = axis == Axis.X ? f.Ny : f.Nx;
        int h = axis == Axis.Z ? f.Ny : f.Nz;
        var img = new Image(w, h);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float v = axis switch { Axis.X => f[index, i, j], Axis.Y => f[i, index, j], _ => f[i, j, index] };
                img.SetPixel(i, h - 1 - j, ColorMap.Map(map, v, mn, mx));
            }

        var size = new Vector3(f.Nx - 1, f.Ny - 1, f.Nz - 1) * f.Spacing;
        Vector3 a, b, c, d;
        switch (axis)
        {
            case Axis.X:
                float x = f.Origin.X + index * f.Spacing.X;
                a = new(x, f.Origin.Y, f.Origin.Z); b = a + new Vector3(0, size.Y, 0); c = b + new Vector3(0, 0, size.Z); d = a + new Vector3(0, 0, size.Z); break;
            case Axis.Y:
                float y = f.Origin.Y + index * f.Spacing.Y;
                a = new(f.Origin.X, y, f.Origin.Z); b = a + new Vector3(size.X, 0, 0); c = b + new Vector3(0, 0, size.Z); d = a + new Vector3(0, 0, size.Z); break;
            default:
                float z = f.Origin.Z + index * f.Spacing.Z;
                a = new(f.Origin.X, f.Origin.Y, z); b = a + new Vector3(size.X, 0, 0); c = b + new Vector3(0, size.Y, 0); d = a + new Vector3(0, size.Y, 0); break;
        }
        var n = Vector3.Normalize(Vector3.Cross(b - a, d - a));
        var g = new Geometry
        {
            Name = $"slice {axis}{index}",
            Positions = [a, b, c, d],
            Normals = [n, n, n, n],
            TexCoords = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
            Indices = [0, 1, 2, 0, 2, 3],
        };
        var ss = g.GetOrCreateStateSet();
        ss.Lighting = false;
        ss.CullFace = CullFaceMode.None;
        ss.Material = new Material { Diffuse = Vector4.One };
        ss.Texture = new Texture2D(img) { Wrap = TextureWrap.ClampToEdge, Filter = TextureFilter.Linear };
        return g;
    }

    /// <summary>Point cloud of samples above a threshold, coloured by value (quick volume preview).</summary>
    public static Geometry PointCloud(ScalarField3D f, float threshold, int stride = 1, ColorMap.Kind map = ColorMap.Kind.Inferno, float pointSize = 2)
    {
        var (min, max) = f.Range();
        var pts = new List<Vector3>();
        var cols = new List<Vector4>();
        for (int z = 0; z < f.Nz; z += stride)
            for (int y = 0; y < f.Ny; y += stride)
                for (int x = 0; x < f.Nx; x += stride)
                {
                    float v = f[x, y, z];
                    if (v < threshold) continue;
                    pts.Add(f.PositionOf(x, y, z));
                    cols.Add(ColorMap.Map(map, v, threshold, max, 0.8f));
                }
        return Shapes.Points(pts.ToArray(), cols.ToArray(), pointSize);
    }
}
