using System.Buffers.Binary;
using GeoPandas.Net.Geometry.Native;
using GeoPandas.Net.IO;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.PointCloud;

/// <summary>One node of the COPC octree: key (depth, x, y, z), its chunk location and point count, and its cube bounds.</summary>
public sealed record CopcNode(int Depth, int X, int Y, int Z, long Offset, int ByteSize, int PointCount,
    double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

/// <summary>COPC header (info VLR) and hierarchy.</summary>
public sealed record CopcInfo(double CenterX, double CenterY, double CenterZ, double HalfSize, double Spacing,
    double GpsTimeMin, double GpsTimeMax, IReadOnlyList<CopcNode> Nodes)
{
    public int MaxDepth => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Depth);
    /// <summary>Approximate point spacing at a depth (root spacing / 2^depth).</summary>
    public double SpacingAt(int depth) => Spacing / Math.Pow(2, depth);
}

/// <summary>
/// Cloud-Optimized Point Cloud (COPC 1.0, <c>.copc.laz</c>) reader: walks the octree hierarchy, selects the nodes that intersect a box and
/// are not deeper than a level of detail, and decompresses only those chunks (native laz-rs). Ideal for streaming subsets of huge LiDAR tiles.
/// </summary>
public static class Copc
{
    public static CopcInfo ReadInfo(string path)
    {
        var info = Las.ReadHeader(path);
        using var f = new MappedFile(path);
        return ReadInfo(f, info);
    }

    private static CopcInfo ReadInfo(MappedFile f, LasInfo info)
    {
        var v = info.CopcInfoVlr ?? throw new InvalidDataException("Not a COPC file (no 'copc' info VLR).");
        double D(int o) => BinaryPrimitives.ReadDoubleLittleEndian(v.AsSpan(o));
        double cx = D(0), cy = D(8), cz = D(16), half = D(24), spacing = D(32);
        long rootOff = (long)BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(40));
        long rootSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(v.AsSpan(48));
        double tmin = D(56), tmax = D(64);
        var nodes = new List<CopcNode>();
        var pages = new Queue<(long Off, long Size)>();
        pages.Enqueue((rootOff, rootSize));
        while (pages.Count > 0)
        {
            var (off, size) = pages.Dequeue();
            var page = f.Span(off, (int)size);
            for (int k = 0; k + 32 <= page.Length; k += 32)
            {
                var e = page.Slice(k, 32);
                int d = BinaryPrimitives.ReadInt32LittleEndian(e), x = BinaryPrimitives.ReadInt32LittleEndian(e[4..]),
                    y = BinaryPrimitives.ReadInt32LittleEndian(e[8..]), z = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
                long o = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
                int bs = BinaryPrimitives.ReadInt32LittleEndian(e[24..]), pc = BinaryPrimitives.ReadInt32LittleEndian(e[28..]);
                if (pc == -1) { pages.Enqueue((o, bs)); continue; }
                double side = 2 * half / Math.Pow(2, d);
                double minX = cx - half + side * x, minY = cy - half + side * y, minZ = cz - half + side * z;
                nodes.Add(new CopcNode(d, x, y, z, o, bs, pc, minX, minY, minZ, minX + side, minY + side, minZ + side));
            }
        }
        return new CopcInfo(cx, cy, cz, half, spacing, tmin, tmax, nodes);
    }

    /// <summary>
    /// Reads the points of the nodes that intersect <paramref name="bbox"/> (all when null) down to <paramref name="maxDepth"/>
    /// (coarser levels = fewer, evenly spread points). Points are filtered exactly to the box and by the other read options.
    /// </summary>
    public static PointCloud Read(string path, Envelope? bbox = null, int? maxDepth = null, LasReadOptions? options = null)
    {
        if (!NativeKernels.SupportsLaz) throw new NotSupportedException("COPC needs the native kernels (geopandas_native with laz-rs).");
        var info = Las.ReadHeader(path);
        int rec = Las.CheckFormat(info);
        var vlr = info.LaszipVlr ?? throw new InvalidDataException("COPC file has no laszip VLR.");
        options ??= new LasReadOptions();
        if (bbox is not null) options = options with { Bbox = bbox };
        using var f = new MappedFile(path);
        var copc = ReadInfo(f, info);
        var selected = copc.Nodes.Where(n => n.PointCount > 0 && (maxDepth is null || n.Depth <= maxDepth)
            && (bbox is null || !(n.MaxX < bbox.MinX || n.MinX > bbox.MaxX || n.MaxY < bbox.MinY || n.MinY > bbox.MaxY))).ToList();
        long total = selected.Sum(n => (long)n.PointCount);
        var b = new Las.Builder(info.PointFormat, (int)Math.Min(total, options.MaxPoints ?? total));
        long index = 0;
        foreach (var n in selected)
        {
            var raw = new byte[(long)n.PointCount * rec];
            NativeKernels.LazDecompressChunk(vlr, f.Span(n.Offset, n.ByteSize), n.PointCount, raw);
            bool more = Las.DecodeRecords(raw, n.PointCount, index, info, options, b);
            index += n.PointCount;
            if (!more) break;
        }
        return b.Build(info);
    }
}
