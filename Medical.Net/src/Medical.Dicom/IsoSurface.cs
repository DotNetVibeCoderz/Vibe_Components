using System.Globalization;
using System.Numerics;
using System.Text;
using Medical.Native;

namespace Medical.Dicom;

/// <summary>A triangle mesh in patient coordinates (LPS, millimetres) with per-vertex normals.</summary>
public sealed record SurfaceMesh(Vector3[] Positions, Vector3[] Normals, int[] Indices)
{
    public int TriangleCount => Indices.Length / 3;

    /// <summary>Enclosed volume in mL (divergence theorem; exact for closed meshes).</summary>
    public double VolumeMl
    {
        get
        {
            double v = 0;
            for (int t = 0; t < Indices.Length; t += 3)
            {
                Vector3 a = Positions[Indices[t]], b = Positions[Indices[t + 1]], c = Positions[Indices[t + 2]];
                v += Vector3.Dot(a, Vector3.Cross(b, c));
            }
            return Math.Abs(v) / 6 / 1000;
        }
    }

    /// <summary>Axis-aligned bounds.</summary>
    public (Vector3 Min, Vector3 Max) Bounds
    {
        get
        {
            if (Positions.Length == 0) return (Vector3.Zero, Vector3.Zero);
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var p in Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (min, max);
        }
    }

    /// <summary>
    /// Taubin λ|μ smoothing (λ = 0.5, μ = −0.53): removes the staircase of thick slices without the shrinkage of plain
    /// Laplacian smoothing; normals are recomputed (area-weighted). Use for display; measure volumes on the raw mesh.
    /// </summary>
    public SurfaceMesh Smooth(int iterations = 6)
    {
        var p = (Vector3[])Positions.Clone();
        var neighbours = new List<int>[p.Length];
        for (int i = 0; i < p.Length; i++) neighbours[i] = [];
        for (int t = 0; t < Indices.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = Indices[t + e], b = Indices[t + (e + 1) % 3];
                neighbours[a].Add(b); neighbours[b].Add(a);
            }
        var tmp = new Vector3[p.Length];
        void Pass(float factor)
        {
            for (int i = 0; i < p.Length; i++)
            {
                var nb = neighbours[i];
                if (nb.Count == 0) { tmp[i] = p[i]; continue; }
                var avg = Vector3.Zero;
                foreach (int j in nb) avg += p[j];
                avg /= nb.Count;
                tmp[i] = p[i] + factor * (avg - p[i]);
            }
            (p, tmp) = (tmp, p);
        }
        for (int k = 0; k < iterations; k++) { Pass(0.5f); Pass(-0.53f); }
        return new SurfaceMesh(p, ComputeNormals(p, Indices), Indices);
    }

    /// <summary>Area-weighted vertex normals.</summary>
    public static Vector3[] ComputeNormals(Vector3[] positions, int[] indices)
    {
        var n = new Vector3[positions.Length];
        for (int t = 0; t < indices.Length; t += 3)
        {
            int a = indices[t], b = indices[t + 1], c = indices[t + 2];
            var f = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            n[a] += f; n[b] += f; n[c] += f;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 0 ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }

    /// <summary>Binary STL (millimetres) — 3D printing, surgical planning tools.</summary>
    public byte[] ToStl(string name = "Medical.Net")
    {
        using var ms = new MemoryStream(84 + TriangleCount * 50);
        using var w = new BinaryWriter(ms);
        var header = new byte[80];
        Encoding.ASCII.GetBytes(name.Length > 80 ? name[..80] : name).CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)TriangleCount);
        for (int t = 0; t < Indices.Length; t += 3)
        {
            Vector3 a = Positions[Indices[t]], b = Positions[Indices[t + 1]], c = Positions[Indices[t + 2]];
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (float.IsNaN(n.X)) n = Vector3.Zero;
            foreach (var v in (ReadOnlySpan<Vector3>)[n, a, b, c]) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Wavefront OBJ with normals.</summary>
    public string ToObj(string name = "mesh")
    {
        var sb = new StringBuilder($"# Medical.Net surface mesh (LPS, mm)\no {name}\n");
        foreach (var p in Positions) sb.Append(CultureInfo.InvariantCulture, $"v {p.X:0.###} {p.Y:0.###} {p.Z:0.###}\n");
        foreach (var n in Normals) sb.Append(CultureInfo.InvariantCulture, $"vn {n.X:0.####} {n.Y:0.####} {n.Z:0.####}\n");
        for (int t = 0; t < Indices.Length; t += 3)
            sb.Append(CultureInfo.InvariantCulture, $"f {Indices[t] + 1}//{Indices[t] + 1} {Indices[t + 1] + 1}//{Indices[t + 1] + 1} {Indices[t + 2] + 1}//{Indices[t + 2] + 1}\n");
        return sb.ToString();
    }
}

/// <summary>
/// Isosurface extraction with naive surface nets (one vertex per boundary cell at the mean of its edge crossings, one
/// quad per crossing edge). Unlike marching cubes it needs no case tables, yields a closed, well-shaped mesh and smooths
/// the staircase of thick slices. Normals come from the field gradient. Memory stays at two slices of cell indices.
/// </summary>
public static class IsoSurface
{
    /// <summary>Surface where the volume crosses <paramref name="isoValue"/> (e.g. 300 HU for bone, −400 HU for skin/air).</summary>
    public static SurfaceMesh Extract(DicomVolume volume, double isoValue, int downsample = 1)
    {
        int s = Math.Max(1, downsample);
        var vox = volume.Voxels;
        int W = volume.Width, H = volume.Height, D = volume.Depth;
        return Build(volume, (W + s - 1) / s, (H + s - 1) / s, D, s, (x, y, z) => vox[(z * H + Math.Min(y * s, H - 1)) * W + Math.Min(x * s, W - 1)] - isoValue);
    }

    /// <summary>Surface of a binary mask (non-zero = inside) defined on the volume grid — segmentations, lesions.</summary>
    public static SurfaceMesh ExtractMask(DicomVolume volume, byte[] mask, int downsample = 1)
    {
        if (mask.Length != volume.Voxels.Length) throw new ArgumentException("Mask size must match the volume.", nameof(mask));
        int s = Math.Max(1, downsample), W = volume.Width, H = volume.Height;
        return Build(volume, (W + s - 1) / s, (H + s - 1) / s, volume.Depth, s, (x, y, z) => mask[(z * H + Math.Min(y * s, H - 1)) * W + Math.Min(x * s, W - 1)] != 0 ? 0.5 : -0.5);
    }

    /// <param name="f">Signed field at grid point (x, y, z); positive = inside. The grid is padded with outside values so surfaces close at the volume border.</param>
    private static SurfaceMesh Build(DicomVolume volume, int nx, int ny, int nz, int stride, Func<int, int, int, double> f)
    {
        // Padded field: grid points -1 … n, outside = -1.
        double F(int x, int y, int z) => x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz ? -1 : f(x, y, z);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<int>();
        int cw = nx + 1, ch = ny + 1;              // cells -1 … n-1 in x and y (offset by one)
        var prev = new int[cw * ch]; var cur = new int[cw * ch];
        Array.Fill(prev, -1);
        Span<double> c = stackalloc double[8];

        Vector3 ToMm(double gx, double gy, double gz)
        {
            var p = volume.ToPatient(gx * stride, gy * stride, gz);
            return new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        }
        Vector3 Gradient(double gx, double gy, double gz)
        {
            int x = (int)Math.Round(gx), y = (int)Math.Round(gy), z = (int)Math.Round(gz);
            double dx = (F(x + 1, y, z) - F(x - 1, y, z)) / (2 * volume.Spacing.X * stride);
            double dy = (F(x, y + 1, z) - F(x, y - 1, z)) / (2 * volume.Spacing.Y * stride);
            double dz = (F(x, y, z + 1) - F(x, y, z - 1)) / (2 * volume.Spacing.Z);
            // Outward normal points down the field (inside is positive).
            var g = volume.RowDirection * -dx + volume.ColumnDirection * -dy + volume.SliceDirection * -dz;
            var v = new Vector3((float)g.X, (float)g.Y, (float)g.Z);
            return v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitZ;
        }
        void Quad(int a, int b, int cc, int d, Vector3 outward)
        {
            if (a < 0 || b < 0 || cc < 0 || d < 0) return;
            var n = Vector3.Cross(positions[b] - positions[a], positions[cc] - positions[a]);
            if (Vector3.Dot(n, outward) < 0) (b, d) = (d, b);
            indices.AddRange([a, b, cc, a, cc, d]);
        }
        Vector3 Axis(Vector3d d) => new((float)d.X, (float)d.Y, (float)d.Z);

        for (int z = -1; z < nz; z++)
        {
            Array.Fill(cur, -1);
            for (int y = -1; y < ny; y++)
                for (int x = -1; x < nx; x++)
                {
                    int mask = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        c[k] = F(x + (k & 1), y + ((k >> 1) & 1), z + ((k >> 2) & 1));
                        if (c[k] > 0) mask |= 1 << k;
                    }
                    if (mask == 0 || mask == 255) continue;
                    double sx = 0, sy = 0, sz = 0; int count = 0;
                    for (int e = 0; e < 12; e++)
                    {
                        // 12 cube edges as corner pairs differing in one bit.
                        int axis = e / 4, rest = e % 4;
                        int a = axis switch { 0 => rest << 1, 1 => (rest & 1) | ((rest & 2) << 1), _ => rest };
                        int b = a | (1 << axis);
                        if ((c[a] > 0) == (c[b] > 0)) continue;
                        double t = c[a] / (c[a] - c[b]);
                        double ax = a & 1, ay = (a >> 1) & 1, az = (a >> 2) & 1;
                        sx += ax + (axis == 0 ? t : 0); sy += ay + (axis == 1 ? t : 0); sz += az + (axis == 2 ? t : 0);
                        count++;
                    }
                    double gx = x + sx / count, gy = y + sy / count, gz = z + sz / count;
                    cur[(y + 1) * cw + (x + 1)] = positions.Count;
                    positions.Add(ToMm(gx, gy, gz));
                    normals.Add(Gradient(gx, gy, gz));
                }
            int Cell(int[] layer, int x, int y) => x < -1 || y < -1 || x >= nx || y >= ny ? -1 : layer[(y + 1) * cw + (x + 1)];
            // Edges starting at grid points of plane z (x / y edges need cell layers z-1 and z; padded edges never cross).
            if (z >= 0)
            {
                for (int y = -1; y < ny; y++)
                    for (int x = -1; x < nx; x++)
                    {
                        double v0 = F(x, y, z);
                        // x-edge (x,y,z)-(x+1,y,z): cells (x, y-1..y, z-1..z)
                        double vx = F(x + 1, y, z);
                        if ((v0 > 0) != (vx > 0))
                            Quad(Cell(prev, x, y - 1), Cell(prev, x, y), Cell(cur, x, y), Cell(cur, x, y - 1), Axis(volume.RowDirection) * (v0 > 0 ? 1 : -1));
                        double vy = F(x, y + 1, z);
                        if ((v0 > 0) != (vy > 0))
                            Quad(Cell(prev, x - 1, y), Cell(prev, x, y), Cell(cur, x, y), Cell(cur, x - 1, y), Axis(volume.ColumnDirection) * (v0 > 0 ? 1 : -1));
                    }
            }
            // z-edges (x,y,z)-(x,y,z+1) need cell layer z only.
            for (int y = -1; y < ny; y++)
                for (int x = -1; x < nx; x++)
                {
                    double v0 = F(x, y, z), vz = F(x, y, z + 1);
                    if ((v0 > 0) != (vz > 0))
                        Quad(Cell(cur, x - 1, y - 1), Cell(cur, x, y - 1), Cell(cur, x, y), Cell(cur, x - 1, y), Axis(volume.SliceDirection) * (v0 > 0 ? 1 : -1));
                }
            (prev, cur) = (cur, prev);
        }
        return new SurfaceMesh([.. positions], [.. normals], [.. indices]);
    }
}
