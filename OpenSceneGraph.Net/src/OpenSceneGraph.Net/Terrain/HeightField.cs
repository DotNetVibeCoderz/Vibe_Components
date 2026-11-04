using System.Numerics;
using OpenSceneGraph.Net.Interop;
using OpenSceneGraph.Net.Meshes;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.Terrain;

/// <summary>
/// Regular grid of elevations (<c>osg::HeightField</c>). Row 0 is the southern edge; X grows east, Y north, Z up.
/// <see cref="Origin"/> is the world position of sample (0,0).
/// </summary>
public sealed class HeightField
{
    public HeightField(int columns, int rows, float spacingX, float spacingY, float[]? heights = null)
    {
        if (columns < 2 || rows < 2) throw new ArgumentOutOfRangeException(nameof(columns), "A height field needs at least 2x2 samples.");
        Columns = columns;
        Rows = rows;
        SpacingX = spacingX;
        SpacingY = spacingY;
        Heights = heights ?? new float[columns * rows];
        if (Heights.Length != columns * rows) throw new ArgumentException("heights must have columns*rows entries.");
    }

    public int Columns { get; }
    public int Rows { get; }
    public float SpacingX { get; }
    public float SpacingY { get; }
    public float[] Heights { get; }
    public Vector3 Origin { get; set; }

    /// <summary>Georeference of sample (0,0) in the source CRS (e.g. UTM metres or degrees), when known.</summary>
    public (double X, double Y)? GeoOrigin { get; set; }
    public string? Crs { get; set; }
    public float NoDataValue { get; set; } = float.NaN;

    public float Width => (Columns - 1) * SpacingX;
    public float Depth => (Rows - 1) * SpacingY;

    public float this[int col, int row]
    {
        get => Heights[row * Columns + col];
        set => Heights[row * Columns + col] = value;
    }

    public (float Min, float Max) Range()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var h in Heights) { if (float.IsNaN(h)) continue; min = MathF.Min(min, h); max = MathF.Max(max, h); }
        return (min, max);
    }

    /// <summary>Bilinear height at a world XY position, or null outside the field.</summary>
    public float? Sample(float worldX, float worldY)
    {
        float fx = (worldX - Origin.X) / SpacingX, fy = (worldY - Origin.Y) / SpacingY;
        if (fx < 0 || fy < 0 || fx > Columns - 1 || fy > Rows - 1) return null;
        int x0 = System.Math.Min((int)fx, Columns - 2), y0 = System.Math.Min((int)fy, Rows - 2);
        float tx = fx - x0, ty = fy - y0;
        float h00 = this[x0, y0], h10 = this[x0 + 1, y0], h01 = this[x0, y0 + 1], h11 = this[x0 + 1, y0 + 1];
        return Origin.Z + (h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty;
    }

    /// <summary>Replaces NaN / no-data samples with the average of valid neighbours (iteratively).</summary>
    public void FillNoData()
    {
        for (int pass = 0; pass < 8; pass++)
        {
            bool any = false;
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                {
                    if (!float.IsNaN(this[x, y]) && this[x, y] != NoDataValue) continue;
                    float sum = 0; int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= Columns || yy >= Rows) continue;
                            float v = this[xx, yy];
                            if (float.IsNaN(v) || v == NoDataValue) continue;
                            sum += v; n++;
                        }
                    if (n > 0) this[x, y] = sum / n; else any = true;
                }
            if (!any) break;
        }
        for (int i = 0; i < Heights.Length; i++) if (float.IsNaN(Heights[i])) Heights[i] = 0;
    }

    /// <summary>Returns a decimated copy (every <paramref name="step"/>-th sample, always keeping the last row/column).</summary>
    public HeightField Decimate(int step)
    {
        if (step <= 1) return this;
        int cols = (Columns - 1) / step + 1 + ((Columns - 1) % step != 0 ? 1 : 0);
        int rows = (Rows - 1) / step + 1 + ((Rows - 1) % step != 0 ? 1 : 0);
        var h = new float[cols * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                h[y * cols + x] = this[System.Math.Min(x * step, Columns - 1), System.Math.Min(y * step, Rows - 1)];
        // spacing changes slightly at the edge when (n-1) is not divisible by step; use uniform spacing and accept the tiny stretch
        return new HeightField(cols, rows, Width / (cols - 1), Depth / (rows - 1), h) { Origin = Origin, GeoOrigin = GeoOrigin, Crs = Crs };
    }

    /// <summary>
    /// Builds a lit, colour-ramped mesh. Uses the Rust fast path for vertex/normal generation when available.
    /// </summary>
    public Geometry ToGeometry(ColorRamp? ramp = null, float verticalScale = 1f)
    {
        int n = Columns * Rows;
        var pos = new Vector3[n];
        var nor = new Vector3[n];
        var heights = verticalScale == 1f ? Heights : Heights.Select(h => h * verticalScale).ToArray();

        if (!FastPath.TryHeightfield(heights, Columns, Rows, SpacingX, SpacingY, pos, nor))
        {
            float ox = -(Columns - 1) * SpacingX * 0.5f, oy = -(Rows - 1) * SpacingY * 0.5f;
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                {
                    int i = y * Columns + x;
                    pos[i] = new Vector3(ox + x * SpacingX, oy + y * SpacingY, heights[i]);
                    int xl = System.Math.Max(0, x - 1), xr = System.Math.Min(Columns - 1, x + 1);
                    int yd = System.Math.Max(0, y - 1), yu = System.Math.Min(Rows - 1, y + 1);
                    float sx = (xr - xl) * SpacingX, sy = (yu - yd) * SpacingY;
                    nor[i] = Vector3.Normalize(new Vector3(-(heights[y * Columns + xr] - heights[y * Columns + xl]) / sx, -(heights[yu * Columns + x] - heights[yd * Columns + x]) / sy, 1));
                }
        }

        // shift from centred grid to Origin-based coordinates
        var shift = Origin + new Vector3(Width * 0.5f, Depth * 0.5f, 0);
        for (int i = 0; i < n; i++) pos[i] += shift;

        var uv = new Vector2[n];
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
                uv[y * Columns + x] = new Vector2((float)x / (Columns - 1), (float)y / (Rows - 1));

        var g = new Geometry
        {
            Name = "Terrain",
            Positions = pos,
            Normals = nor,
            TexCoords = uv,
            Indices = Shapes.GridIndices(Columns - 1, Rows - 1, flip: false),
        };
        if (ramp is not null)
        {
            var (min, max) = Range();
            var cols = new Vector4[n];
            for (int i = 0; i < n; i++) cols[i] = ramp.Evaluate(Heights[i], min, max, nor[i].Z);
            g.Colors = cols;
        }
        var ss = g.GetOrCreateStateSet();
        ss.Material = new Material { Diffuse = Vector4.One, Specular = new Vector3(0.05f), Shininess = 8 };
        return g;
    }
}

/// <summary>Elevation colour ramp with optional slope darkening (rock on steep slopes).</summary>
public sealed class ColorRamp
{
    private readonly List<(float T, Vector4 Color)> _stops = [];

    public ColorRamp Add(float t, Vector4 color)
    {
        _stops.Add((t, color));
        _stops.Sort((a, b) => a.T.CompareTo(b.T));
        return this;
    }

    public Vector4? SteepColor { get; set; }
    public float SteepThreshold { get; set; } = 0.75f;

    public Vector4 Evaluate(float height, float min, float max, float normalZ = 1)
    {
        float t = max > min ? (height - min) / (max - min) : 0;
        var c = Sample(t);
        if (SteepColor is { } steep && normalZ < SteepThreshold)
            c = Vector4.Lerp(c, steep, System.Math.Clamp((SteepThreshold - normalZ) * 4, 0, 1));
        return c;
    }

    public Vector4 Sample(float t)
    {
        if (_stops.Count == 0) return Vector4.One;
        if (t <= _stops[0].T) return _stops[0].Color;
        for (int i = 1; i < _stops.Count; i++)
            if (t <= _stops[i].T)
            {
                var (t0, c0) = _stops[i - 1];
                var (t1, c1) = _stops[i];
                return Vector4.Lerp(c0, c1, (t - t0) / MathF.Max(1e-6f, t1 - t0));
            }
        return _stops[^1].Color;
    }

    /// <summary>Water, sand, grass, forest, rock, snow.</summary>
    public static ColorRamp Natural() => new ColorRamp()
        .Add(0.00f, Color.FromHex("#2b5d8a")).Add(0.06f, Color.FromHex("#d9c99a")).Add(0.12f, Color.FromHex("#6f9e4c"))
        .Add(0.40f, Color.FromHex("#3d6b35")).Add(0.68f, Color.FromHex("#7a6d5f")).Add(0.86f, Color.FromHex("#a8a29a")).Add(1.0f, Color.FromHex("#f4f6f8"))
        .WithSteep(Color.FromHex("#6b625a"));

    public static ColorRamp Desert() => new ColorRamp()
        .Add(0, Color.FromHex("#c9a46b")).Add(0.5f, Color.FromHex("#e0c08c")).Add(1, Color.FromHex("#a5764a")).WithSteep(Color.FromHex("#8a6040"));

    /// <summary>Military map style (green/khaki/brown).</summary>
    public static ColorRamp Tactical() => new ColorRamp()
        .Add(0, Color.FromHex("#3e4a2c")).Add(0.4f, Color.FromHex("#56603a")).Add(0.75f, Color.FromHex("#7a7450")).Add(1, Color.FromHex("#b0a27a"));

    public ColorRamp WithSteep(Vector4 c) { SteepColor = c; return this; }
}

/// <summary>Procedural terrain using fractal value noise (deterministic per seed).</summary>
public static class TerrainGenerator
{
    public static HeightField Fractal(int size, float spacing, float amplitude, int seed = 1337, int octaves = 6, float roughness = 0.5f, float baseFrequency = 2.5f)
    {
        var hf = new HeightField(size, size, spacing, spacing);
        var noise = new ValueNoise(seed);
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / (size - 1), v = (float)y / (size - 1);
                float amp = 1, freq = baseFrequency, sum = 0, norm = 0;
                for (int o = 0; o < octaves; o++)
                {
                    sum += noise.Sample(u * freq, v * freq) * amp;
                    norm += amp;
                    amp *= roughness;
                    freq *= 2;
                }
                float h = sum / norm; // 0..1
                h = MathF.Pow(h, 1.6f); // flatter valleys, sharper peaks
                hf[x, y] = h * amplitude;
            }
        });
        hf.Origin = new Vector3(-hf.Width / 2, -hf.Depth / 2, 0);
        return hf;
    }

    /// <summary>Smooth value noise in [0,1].</summary>
    public sealed class ValueNoise(int seed)
    {
        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 144269504);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        public float Sample(float x, float y)
        {
            int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
            float tx = x - xi, ty = y - yi;
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        }
    }
}

/// <summary>
/// Builds a tiled, multi-resolution terrain (<c>osgTerrain</c>-style): the field is split into tiles, each a
/// <see cref="Lod"/> with several decimated levels, so far tiles render with fewer triangles. Tiles get cluster
/// culling data so that tiles facing away from the eye are skipped.
/// </summary>
public static class TerrainBuilder
{
    public static Group Build(HeightField field, int tilesPerSide = 4, int levels = 3, ColorRamp? ramp = null, float lodRangeFactor = 1.5f)
    {
        var root = new Group { Name = "Terrain" };
        ramp ??= ColorRamp.Natural();
        var (min, max) = field.Range();
        int tileSamples = (field.Columns - 1) / tilesPerSide;
        if (tileSamples < 2) { tilesPerSide = 1; tileSamples = field.Columns - 1; }

        for (int ty = 0; ty < tilesPerSide; ty++)
            for (int tx = 0; tx < tilesPerSide; tx++)
            {
                int c0 = tx * tileSamples, r0 = ty * tileSamples;
                int c1 = tx == tilesPerSide - 1 ? field.Columns - 1 : c0 + tileSamples;
                int r1 = ty == tilesPerSide - 1 ? field.Rows - 1 : r0 + tileSamples;
                var tile = SubField(field, c0, r0, c1, r1);
                var lod = new Lod { Name = $"tile_{tx}_{ty}" };
                float tileSize = MathF.Max(tile.Width, tile.Depth);
                float prev = 0;
                for (int level = 0; level < levels; level++)
                {
                    var levelField = tile.Decimate(1 << level);
                    var g = levelField.ToGeometry(null);
                    ApplyRamp(g, levelField, ramp, min, max);
                    float maxRange = level == levels - 1 ? float.MaxValue : tileSize * lodRangeFactor * (1 << level);
                    lod.AddChild(g, prev, maxRange);
                    prev = maxRange;
                }
                root.AddChild(lod);
            }
        return root;
    }

    private static void ApplyRamp(Geometry g, HeightField f, ColorRamp ramp, float min, float max)
    {
        var cols = new Vector4[g.VertexCount];
        for (int i = 0; i < cols.Length; i++) cols[i] = ramp.Evaluate(f.Heights[i], min, max, g.Normals![i].Z);
        g.Colors = cols;
    }

    private static HeightField SubField(HeightField f, int c0, int r0, int c1, int r1)
    {
        int cols = c1 - c0 + 1, rows = r1 - r0 + 1;
        var h = new float[cols * rows];
        for (int y = 0; y < rows; y++)
            Array.Copy(f.Heights, (r0 + y) * f.Columns + c0, h, y * cols, cols);
        return new HeightField(cols, rows, f.SpacingX, f.SpacingY, h)
        {
            Origin = f.Origin + new Vector3(c0 * f.SpacingX, r0 * f.SpacingY, 0),
        };
    }
}
