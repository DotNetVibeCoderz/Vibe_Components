using System.Numerics;

namespace Medical.Native;

/// <summary>How a slab of resampled planes is reduced to one image.</summary>
public enum SlabMode
{
    /// <summary>Average intensity projection (AvgIP); with a single step this is a plain MPR.</summary>
    Mean = 0,
    /// <summary>Maximum intensity projection (MIP) — vessels, bone, contrast, calcifications.</summary>
    Max = 1,
    /// <summary>Minimum intensity projection (MinIP) — airways, emphysema.</summary>
    Min = 2,
}

/// <summary>
/// A resampling request in voxel coordinates (x fastest): output pixel (i, j) samples
/// <c>Origin + i·U + j·V + k·N</c> for <c>k = 0 … Steps-1</c> with trilinear interpolation.
/// </summary>
public readonly record struct SlabGeometry(Vector3d Origin, Vector3d U, Vector3d V, Vector3d N, int Steps, int Width, int Height);

/// <summary>Double-precision vector used by the resampling kernels (keeps both backends in f64).</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vector3d Normalized() { double l = Length; return l > 0 ? new(X / l, Y / l, Z / l) : this; }
    public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}

public static partial class MedicalKernels
{
    private static readonly Lazy<bool> _slabNative = new(() =>
    {
        try { return IsNativeAvailable && NativeMethods.Version() >= 1_02_00; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>Backend that <see cref="Slab"/> runs on (the Rust slab kernel needs medical_native ≥ 1.2).</summary>
    public static KernelBackend SlabBackend => PreferredBackend == KernelBackend.Rust && _slabNative.Value ? KernelBackend.Rust : KernelBackend.Managed;

    /// <summary>
    /// Multiplanar reformation and slab projection (MPR, MIP, MinIP, AvgIP) of a signed 16-bit volume
    /// (<paramref name="nx"/> × <paramref name="ny"/> × <paramref name="nz"/>, x fastest). Samples outside the volume are skipped;
    /// pixels with no sample inside get <paramref name="fill"/>. Results round half away from zero.
    /// </summary>
    public static unsafe void Slab(ReadOnlySpan<short> volume, int nx, int ny, int nz, in SlabGeometry g, SlabMode mode, short fill, Span<short> output)
    {
        if (nx <= 0 || ny <= 0 || nz <= 0) throw new ArgumentOutOfRangeException(nameof(nx), "Volume dimensions must be positive.");
        if ((long)nx * ny * nz > volume.Length) throw new ArgumentException("Volume buffer is smaller than nx·ny·nz.", nameof(volume));
        if (g.Steps <= 0 || g.Width <= 0 || g.Height <= 0) throw new ArgumentOutOfRangeException(nameof(g), "Steps, width and height must be positive.");
        if (output.Length < g.Width * g.Height) throw new ArgumentException("Output buffer too small.", nameof(output));
        if (SlabBackend == KernelBackend.Rust)
        {
            double* o = stackalloc double[] { g.Origin.X, g.Origin.Y, g.Origin.Z };
            double* u = stackalloc double[] { g.U.X, g.U.Y, g.U.Z };
            double* v = stackalloc double[] { g.V.X, g.V.Y, g.V.Z };
            double* n = stackalloc double[] { g.N.X, g.N.Y, g.N.Z };
            fixed (short* pv = volume) fixed (short* po = output)
                ThrowIfError(NativeMethods.SlabI16(pv, (nuint)nx, (nuint)ny, (nuint)nz, o, u, v, n, (uint)g.Steps, (int)mode, fill, po, (nuint)g.Width, (nuint)g.Height));
            return;
        }
        SlabManaged(volume, nx, ny, nz, g, mode, fill, output);
    }

    private static void SlabManaged(ReadOnlySpan<short> vol, int nx, int ny, int nz, in SlabGeometry g, SlabMode mode, short fill, Span<short> output)
    {
        for (int j = 0; j < g.Height; j++)
            for (int i = 0; i < g.Width; i++)
            {
                double fi = i, fj = j;
                double bx = g.Origin.X + fi * g.U.X + fj * g.V.X;
                double by = g.Origin.Y + fi * g.U.Y + fj * g.V.Y;
                double bz = g.Origin.Z + fi * g.U.Z + fj * g.V.Z;
                double acc = 0;
                int count = 0;
                for (int k = 0; k < g.Steps; k++)
                {
                    double fk = k;
                    if (!Sample(vol, nx, ny, nz, bx + fk * g.N.X, by + fk * g.N.Y, bz + fk * g.N.Z, out double s)) continue;
                    acc = count == 0 ? s : mode switch
                    {
                        SlabMode.Max => s > acc ? s : acc,
                        SlabMode.Min => s < acc ? s : acc,
                        _ => acc + s,
                    };
                    count++;
                }
                output[j * g.Width + i] = count == 0 ? fill : mode == SlabMode.Mean ? ToI16(acc / count) : ToI16(acc);
            }
    }

    /// <summary>Trilinear sample at voxel coordinates (same arithmetic as the Rust kernel).</summary>
    public static bool Sample(ReadOnlySpan<short> vol, int nx, int ny, int nz, double x, double y, double z, out double value)
    {
        value = 0;
        const double eps = 1e-6;   // absorbs round-off at the border, same as the Rust kernel
        double mx = nx - 1, my = ny - 1, mz = nz - 1;
        if (!(x >= -eps && y >= -eps && z >= -eps && x <= mx + eps && y <= my + eps && z <= mz + eps)) return false;
        x = Math.Min(Math.Max(x, 0.0), mx); y = Math.Min(Math.Max(y, 0.0), my); z = Math.Min(Math.Max(z, 0.0), mz);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
        int x1 = x0 + 1 < nx ? x0 + 1 : x0, y1 = y0 + 1 < ny ? y0 + 1 : y0, z1 = z0 + 1 < nz ? z0 + 1 : z0;
        double fx = x - x0, fy = y - y0, fz = z - z0;
        int plane = nx * ny;
        int r00 = z0 * plane + y0 * nx, r10 = z0 * plane + y1 * nx, r01 = z1 * plane + y0 * nx, r11 = z1 * plane + y1 * nx;
        double c00 = (double)vol[r00 + x0] * (1.0 - fx) + (double)vol[r00 + x1] * fx;
        double c10 = (double)vol[r10 + x0] * (1.0 - fx) + (double)vol[r10 + x1] * fx;
        double c01 = (double)vol[r01 + x0] * (1.0 - fx) + (double)vol[r01 + x1] * fx;
        double c11 = (double)vol[r11 + x0] * (1.0 - fx) + (double)vol[r11 + x1] * fx;
        double c0 = c00 * (1.0 - fy) + c10 * fy;
        double c1 = c01 * (1.0 - fy) + c11 * fy;
        value = c0 * (1.0 - fz) + c1 * fz;
        return true;
    }

    private static short ToI16(double v)
    {
        double r = Math.Round(v, MidpointRounding.AwayFromZero);
        return r < short.MinValue ? short.MinValue : r > short.MaxValue ? short.MaxValue : (short)r;
    }
}
