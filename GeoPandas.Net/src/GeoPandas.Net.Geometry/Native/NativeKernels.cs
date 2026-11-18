using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GeoPandas.Net.Geometry.Native;

/// <summary>
/// Hot-loop kernels over coordinate arrays. When the Rust library <c>geopandas_native</c> is present it is used
/// (multi-threaded with Rayon); otherwise a managed implementation with <see cref="Vector{T}"/> SIMD and
/// <see cref="Parallel"/> runs. Results are identical within floating-point tolerance.
/// Set environment variable <c>GEOPANDAS_NET_DISABLE_NATIVE=1</c> to force the managed path.
/// </summary>
public static partial class NativeKernels
{
    private const string Lib = "geopandas_native";
    private static readonly Lazy<bool> s_available = new(Probe);

    /// <summary>True when the native Rust library was found and loaded.</summary>
    public static bool IsNativeAvailable => s_available.Value;

    /// <summary>Which backend runs: "rust" or "managed-simd".</summary>
    public static string Backend => IsNativeAvailable ? "rust" : "managed-simd";

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("GEOPANDAS_NET_DISABLE_NATIVE") == "1") return false;
        try { return gpn_version() > 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    [LibraryImport(Lib)] private static partial int gpn_version();
    [LibraryImport(Lib)] private static unsafe partial int gpn_laz_decompress_file(byte* path, nuint pathLen, ulong offsetToPoints, byte* vlr, nuint vlrLen, ulong pointCount, byte* output, nuint outLen);
    [LibraryImport(Lib)] private static unsafe partial int gpn_laz_decompress_chunk(byte* vlr, nuint vlrLen, byte* chunk, nuint chunkLen, ulong pointCount, byte* output, nuint outLen);
    [LibraryImport(Lib)] private static unsafe partial nuint gpn_last_error(byte* buffer, nuint length);

    private static readonly Lazy<bool> s_laz = new(() => { try { return IsNativeAvailable && gpn_version() >= 2; } catch (EntryPointNotFoundException) { return false; } });

    /// <summary>True when the native library includes the LAZ codec (laz-rs: all point formats 0–10, layered LAS 1.4 compression, COPC chunks).</summary>
    public static bool SupportsLaz => s_laz.Value;

    private static unsafe string LastError()
    {
        nuint n = gpn_last_error(null, 0);
        var buf = new byte[(int)n];
        fixed (byte* p = buf) gpn_last_error(p, n);
        return System.Text.Encoding.UTF8.GetString(buf);
    }

    /// <summary>Decompresses all points of a LAZ file into raw LAS records (native laz-rs).</summary>
    public static unsafe void LazDecompressFile(string path, long offsetToPoints, ReadOnlySpan<byte> laszipVlr, long pointCount, Span<byte> output)
    {
        if (!SupportsLaz) throw new NotSupportedException("The native LAZ codec is not available.");
        var pb = System.Text.Encoding.UTF8.GetBytes(path);
        fixed (byte* pp = pb, v = laszipVlr, o = output)
            if (gpn_laz_decompress_file(pp, (nuint)pb.Length, (ulong)offsetToPoints, v, (nuint)laszipVlr.Length, (ulong)pointCount, o, (nuint)output.Length) != 0)
                throw new InvalidDataException("LAZ: " + LastError());
    }

    /// <summary>Decompresses one LAZ chunk (e.g. a COPC node) into raw LAS records.</summary>
    public static unsafe void LazDecompressChunk(ReadOnlySpan<byte> laszipVlr, ReadOnlySpan<byte> chunk, long pointCount, Span<byte> output)
    {
        if (!SupportsLaz) throw new NotSupportedException("The native LAZ codec is not available.");
        fixed (byte* v = laszipVlr, c = chunk, o = output)
            if (gpn_laz_decompress_chunk(v, (nuint)laszipVlr.Length, c, (nuint)chunk.Length, (ulong)pointCount, o, (nuint)output.Length) != 0)
                throw new InvalidDataException("LAZ chunk: " + LastError());
    }
    [LibraryImport(Lib)] private static unsafe partial void gpn_haversine(double* lon1, double* lat1, double* lon2, double* lat2, double* output, nuint n, double radius);
    [LibraryImport(Lib)] private static unsafe partial void gpn_point_in_polygon(double* xs, double* ys, nuint n, double* rx, double* ry, nuint m, byte* output);
    [LibraryImport(Lib)] private static unsafe partial void gpn_bbox_filter(double* xs, double* ys, nuint n, double minx, double miny, double maxx, double maxy, byte* output);

    /// <summary>Element-wise haversine distance (m).</summary>
    public static unsafe void Haversine(ReadOnlySpan<double> lon1, ReadOnlySpan<double> lat1, ReadOnlySpan<double> lon2, ReadOnlySpan<double> lat2, Span<double> output, double radius = Geodesy.EarthRadius)
    {
        int n = lon1.Length;
        if (lat1.Length != n || lon2.Length != n || lat2.Length != n || output.Length < n) throw new ArgumentException("Span lengths differ.");
        if (IsNativeAvailable)
        {
            fixed (double* a = lon1, b = lat1, c = lon2, d = lat2, o = output) gpn_haversine(a, b, c, d, o, (nuint)n, radius);
            return;
        }
        for (int i = 0; i < n; i++) output[i] = Geodesy.Haversine(lon1[i], lat1[i], lon2[i], lat2[i], radius);
    }

    /// <summary>
    /// Ray-casting point-in-polygon for many points against one ring (closed coordinate arrays).
    /// output[i] = 1 when inside. Boundary points may go either way (use NTS predicates for exact topology).
    /// </summary>
    public static unsafe void PointInRing(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, ReadOnlySpan<double> ringX, ReadOnlySpan<double> ringY, Span<byte> output)
    {
        int n = xs.Length, m = ringX.Length;
        if (ys.Length != n || ringY.Length != m || output.Length < n) throw new ArgumentException("Span lengths differ.");
        if (IsNativeAvailable)
        {
            fixed (double* px = xs, py = ys, rx = ringX, ry = ringY) fixed (byte* o = output)
                gpn_point_in_polygon(px, py, (nuint)n, rx, ry, (nuint)m, o);
            return;
        }
        double[] rxa = ringX.ToArray(), rya = ringY.ToArray(), xa = xs.ToArray(), ya = ys.ToArray();
        var res = new byte[n];
        Parallel.For(0, (n + 4095) / 4096, block =>
        {
            int start = block * 4096, end = Math.Min(n, start + 4096);
            for (int i = start; i < end; i++) res[i] = InsideRing(xa[i], ya[i], rxa, rya) ? (byte)1 : (byte)0;
        });
        res.CopyTo(output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool InsideRing(double x, double y, double[] rx, double[] ry)
    {
        bool inside = false;
        int m = rx.Length;
        for (int i = 0, j = m - 1; i < m; j = i++)
        {
            double yi = ry[i], yj = ry[j];
            if ((yi > y) != (yj > y))
            {
                double xi = rx[i], xj = rx[j];
                if (x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>SIMD bounding-box filter: output[i] = 1 when (xs[i], ys[i]) lies inside the box (inclusive).</summary>
    public static unsafe void BoxFilter(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, double minX, double minY, double maxX, double maxY, Span<byte> output)
    {
        int n = xs.Length;
        if (ys.Length != n || output.Length < n) throw new ArgumentException("Span lengths differ.");
        if (IsNativeAvailable)
        {
            fixed (double* px = xs, py = ys) fixed (byte* o = output) gpn_bbox_filter(px, py, (nuint)n, minX, minY, maxX, maxY, o);
            return;
        }
        int i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<double>.Count)
        {
            var vMinX = new Vector<double>(minX); var vMaxX = new Vector<double>(maxX);
            var vMinY = new Vector<double>(minY); var vMaxY = new Vector<double>(maxY);
            int w = Vector<double>.Count;
            for (; i <= n - w; i += w)
            {
                var vx = new Vector<double>(xs.Slice(i, w));
                var vy = new Vector<double>(ys.Slice(i, w));
                var mask = Vector.BitwiseAnd(
                    Vector.BitwiseAnd(Vector.GreaterThanOrEqual(vx, vMinX), Vector.LessThanOrEqual(vx, vMaxX)),
                    Vector.BitwiseAnd(Vector.GreaterThanOrEqual(vy, vMinY), Vector.LessThanOrEqual(vy, vMaxY)));
                for (int k = 0; k < w; k++) output[i + k] = mask[k] != 0 ? (byte)1 : (byte)0;
            }
        }
        for (; i < n; i++) output[i] = xs[i] >= minX && xs[i] <= maxX && ys[i] >= minY && ys[i] <= maxY ? (byte)1 : (byte)0;
    }

    /// <summary>SIMD min/max of a span (used by bounds and raster statistics). NaNs are ignored.</summary>
    public static (double Min, double Max) MinMax(ReadOnlySpan<double> values)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count * 2)
        {
            int w = Vector<double>.Count;
            var vmin = new Vector<double>(double.PositiveInfinity);
            var vmax = new Vector<double>(double.NegativeInfinity);
            for (; i <= values.Length - w; i += w)
            {
                var v = new Vector<double>(values.Slice(i, w));
                var notNan = Vector.Equals(v, v);
                vmin = Vector.ConditionalSelect(notNan, Vector.Min(vmin, v), vmin);
                vmax = Vector.ConditionalSelect(notNan, Vector.Max(vmax, v), vmax);
            }
            for (int k = 0; k < w; k++) { min = Math.Min(min, vmin[k]); max = Math.Max(max, vmax[k]); }
        }
        for (; i < values.Length; i++)
        {
            double v = values[i];
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }
}
