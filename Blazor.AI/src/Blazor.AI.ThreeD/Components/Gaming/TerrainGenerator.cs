using System.Globalization;
using System.Text;

namespace Blazor.AI.ThreeD;

/// <summary>Parameters for <see cref="TerrainGenerator"/>.</summary>
public sealed record TerrainSettings
{
    public int Seed { get; init; } = 1337;
    public int Size { get; init; } = 72;
    public int Octaves { get; init; } = 5;
    public double Persistence { get; init; } = 0.5;
    public double Lacunarity { get; init; } = 2.0;
    public double Scale { get; init; } = 2.6;

    /// <summary>Fraction of the height range under water (0–1).</summary>
    public double SeaLevel { get; init; } = 0.38;

    public int ErosionPasses { get; init; } = 2;

    /// <summary>0 = continent, 1 = archipelago (falloff towards the edges).</summary>
    public double Island { get; init; } = 0.6;

    /// <summary>Extra peak strength (volcanic cones).</summary>
    public double Ridges { get; init; } = 0.2;

    public double MaxHeight { get; init; } = 3.2;
}

/// <summary>A generated heightfield, normalised to [0, 1].</summary>
public sealed record Terrain(int Size, double[] Heights, TerrainSettings Settings)
{
    public double At(int x, int z) => Heights[Math.Clamp(z, 0, Size - 1) * Size + Math.Clamp(x, 0, Size - 1)];

    /// <summary>World-space height (metres) at grid coordinates.</summary>
    public double WorldY(int x, int z) => Map(At(x, z));

    public double Map(double h) => h <= Settings.SeaLevel ? Settings.SeaLevel * Settings.MaxHeight * 0.25 : (Settings.SeaLevel * 0.25 + (h - Settings.SeaLevel)) * Settings.MaxHeight;

    public double[] WorldPos(int x, int z, double worldSize) =>
        [(x / (Size - 1.0) - 0.5) * worldSize, WorldY(x, z), (z / (Size - 1.0) - 0.5) * worldSize];

    public double LandFraction => Heights.Count(h => h > Settings.SeaLevel) / (double)Heights.Length;
}

/// <summary>Deterministic value-noise fBm terrain with island falloff, ridges and thermal erosion.</summary>
public static class TerrainGenerator
{
    public static Terrain Generate(TerrainSettings s)
    {
        var n = Math.Clamp(s.Size, 16, 160);
        var h = new double[n * n];
        var perm = Permutation(s.Seed);
        for (var z = 0; z < n; z++)
        {
            for (var x = 0; x < n; x++)
            {
                double nx = x / (double)n * s.Scale, nz = z / (double)n * s.Scale;
                double amp = 1, freq = 1, sum = 0, norm = 0;
                for (var o = 0; o < Math.Clamp(s.Octaves, 1, 8); o++)
                {
                    sum += amp * Noise(perm, nx * freq + o * 17.3, nz * freq + o * 9.1);
                    norm += amp;
                    amp *= s.Persistence;
                    freq *= s.Lacunarity;
                }

                var v = sum / norm; // 0..1
                if (s.Ridges > 0)
                {
                    var r = 1 - Math.Abs(Noise(perm, nx * 1.7 + 50, nz * 1.7 + 50) * 2 - 1);
                    v += s.Ridges * r * r * 0.6;
                }

                if (s.Island > 0)
                {
                    double dx = x / (n - 1.0) * 2 - 1, dz = z / (n - 1.0) * 2 - 1;
                    var d = Math.Sqrt(dx * dx + dz * dz) / Math.Sqrt(2);
                    v -= s.Island * Math.Pow(d, 1.6) * 0.9;
                }

                h[z * n + x] = v;
            }
        }

        for (var p = 0; p < Math.Clamp(s.ErosionPasses, 0, 12); p++)
        {
            Erode(h, n, 0.012);
        }

        var min = h.Min();
        var max = h.Max();
        var span = Math.Max(1e-9, max - min);
        for (var i = 0; i < h.Length; i++)
        {
            h[i] = (h[i] - min) / span;
        }

        return new Terrain(n, h, s with { Size = n });
    }

    /// <summary>Colour per vertex: deep water, shallow, sand, grass, forest, rock, snow.</summary>
    public static string Color(double h, double sea)
    {
        if (h <= sea)
        {
            return h < sea * 0.7 ? "#1a4f8f" : "#2f86c9";
        }

        var t = (h - sea) / Math.Max(1e-6, 1 - sea);
        return t switch
        {
            < 0.05 => "#d8c48a",
            < 0.35 => "#5fae5a",
            < 0.6 => "#3f7f43",
            < 0.8 => "#8a7d6b",
            _ => "#f2f4ff",
        };
    }

    /// <summary>Builds a single surface object (plus a translucent sea plane).</summary>
    public static IEnumerable<Object3D> ToObjects(Terrain t, double worldSize, string id = "terrain")
    {
        var s = t.Settings;
        yield return new Object3D
        {
            Id = id, Kind = "surface", Rows = t.Size, Cols = t.Size, Size = [worldSize, 0, worldSize],
            Values = t.Heights.Select(t.Map).ToArray(),
            Colors = t.Heights.Select(h => Color(h, s.SeaLevel)).ToArray(),
            Roughness = 0.95, FlatShading = true, Pickable = false,
        };
        yield return new Object3D
        {
            Id = id + "-sea", Kind = "plane", Size = [worldSize * 1.02, worldSize * 1.02], Rotation = [-Math.PI / 2, 0, 0],
            Position = [0, s.SeaLevel * s.MaxHeight * 0.25 + 0.02, 0], Color = "#2f86c9", Opacity = 0.55, Roughness = 0.15, Metalness = 0.2, Pickable = false,
        };
    }

    /// <summary>Picks land grid cells in a height band, spread out deterministically.</summary>
    public static List<(int X, int Z)> Sample(Terrain t, double minT, double maxT, int count, int seed, int spacing = 3)
    {
        var rng = new Random(seed);
        var picks = new List<(int, int)>();
        for (var tries = 0; tries < count * 40 && picks.Count < count; tries++)
        {
            var x = rng.Next(2, t.Size - 2);
            var z = rng.Next(2, t.Size - 2);
            var h = t.At(x, z);
            var rel = (h - t.Settings.SeaLevel) / Math.Max(1e-6, 1 - t.Settings.SeaLevel);
            if (h <= t.Settings.SeaLevel || rel < minT || rel > maxT)
            {
                continue;
            }

            if (picks.Any(p => Math.Abs(p.Item1 - x) < spacing && Math.Abs(p.Item2 - z) < spacing))
            {
                continue;
            }

            picks.Add((x, z));
        }

        return picks;
    }

    /// <summary>Heightmap as a greyscale PGM-in-text JSON-friendly array, or as a CSV.</summary>
    public static string ToCsv(Terrain t)
    {
        var sb = new StringBuilder();
        for (var z = 0; z < t.Size; z++)
        {
            sb.AppendJoin(',', Enumerable.Range(0, t.Size).Select(x => t.At(x, z).ToString("0.###", CultureInfo.InvariantCulture)));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>A coloured top-down preview as an SVG data URL (no image libraries needed).</summary>
    public static string ToSvgDataUrl(Terrain t, int pixel = 3)
    {
        var sb = new StringBuilder();
        var w = t.Size * pixel;
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{w}' shape-rendering='crispEdges'>");
        for (var z = 0; z < t.Size; z++)
        {
            for (var x = 0; x < t.Size; x++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<rect x='{x * pixel}' y='{z * pixel}' width='{pixel}' height='{pixel}' fill='{Color(t.At(x, z), t.Settings.SeaLevel)}'/>");
            }
        }

        sb.Append("</svg>");
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Erode(double[] h, int n, double talus)
    {
        var copy = (double[])h.Clone();
        for (var z = 1; z < n - 1; z++)
        {
            for (var x = 1; x < n - 1; x++)
            {
                var i = z * n + x;
                var lowest = i;
                var drop = 0.0;
                foreach (var j in new[] { i - 1, i + 1, i - n, i + n })
                {
                    var d = copy[i] - copy[j];
                    if (d > drop)
                    {
                        drop = d;
                        lowest = j;
                    }
                }

                if (drop > talus)
                {
                    var move = (drop - talus) * 0.4;
                    h[i] -= move;
                    h[lowest] += move;
                }
            }
        }
    }

    private static int[] Permutation(int seed)
    {
        var p = Enumerable.Range(0, 256).ToArray();
        var rng = new Random(seed);
        for (var i = 255; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        return [.. p, .. p];
    }

    private static double Hash(int[] p, int x, int z) => p[(p[x & 255] + z) & 511] / 255.0;

    private static double Noise(int[] p, double x, double z)
    {
        var x0 = (int)Math.Floor(x);
        var z0 = (int)Math.Floor(z);
        double fx = x - x0, fz = z - z0;
        double sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
        var a = Hash(p, x0, z0);
        var b = Hash(p, x0 + 1, z0);
        var c = Hash(p, x0, z0 + 1);
        var d = Hash(p, x0 + 1, z0 + 1);
        return a + (b - a) * sx + (c - a) * sz + (a - b - c + d) * sx * sz;
    }
}
