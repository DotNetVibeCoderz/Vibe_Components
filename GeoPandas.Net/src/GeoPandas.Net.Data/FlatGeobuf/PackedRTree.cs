using System.Buffers.Binary;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Data.FlatGeobufInternal;

/// <summary>
/// Static packed Hilbert R-tree as specified by FlatGeobuf (derived from flatbush): a flat array of 40-byte nodes
/// (minX, minY, maxX, maxY, offset), root first and leaves last. Leaf offsets are byte offsets of features in the data section.
/// </summary>
internal static class PackedRTree
{
    public const int NodeItemSize = 40;

    /// <summary>[start, end) node ranges per level, leaves first.</summary>
    public static (long Start, long End)[] LevelBounds(ulong numItems, ushort nodeSize)
    {
        if (nodeSize < 2) throw new ArgumentException("Node size must be at least 2.");
        if (numItems == 0) throw new ArgumentException("Number of items must be greater than 0.");
        ulong n = numItems, numNodes = n;
        var levelNumNodes = new List<ulong> { n };
        do
        {
            n = (n + nodeSize - 1) / nodeSize;
            numNodes += n;
            levelNumNodes.Add(n);
        } while (n != 1);
        var bounds = new (long, long)[levelNumNodes.Count];
        n = numNodes;
        for (int i = 0; i < levelNumNodes.Count; i++)
        {
            ulong start = n - levelNumNodes[i];
            bounds[i] = ((long)start, (long)(start + levelNumNodes[i]));
            n -= levelNumNodes[i];
        }
        return bounds;
    }

    public static long Size(ulong numItems, ushort nodeSize)
    {
        if (nodeSize == 0 || numItems == 0) return 0;
        var lb = LevelBounds(numItems, nodeSize);
        return lb[0].End * NodeItemSize; // leaves end at the last node
    }

    /// <summary>16-bit Hilbert curve index (flatbush / FlatGeobuf implementation).</summary>
    public static uint Hilbert(uint x, uint y)
    {
        uint a = x ^ y;
        uint b = 0xFFFF ^ a;
        uint c = 0xFFFF ^ (x | y);
        uint d = x & (y ^ 0xFFFF);
        uint A = a | (b >> 1);
        uint B = (a >> 1) ^ a;
        uint C = ((c >> 1) ^ (b & (d >> 1))) ^ c;
        uint D = ((a & (c >> 1)) ^ (d >> 1)) ^ d;

        a = A; b = B; c = C; d = D;
        A = (a & (a >> 2)) ^ (b & (b >> 2));
        B = (a & (b >> 2)) ^ (b & ((a ^ b) >> 2));
        C ^= (a & (c >> 2)) ^ (b & (d >> 2));
        D ^= (b & (c >> 2)) ^ ((a ^ b) & (d >> 2));

        a = A; b = B; c = C; d = D;
        A = (a & (a >> 4)) ^ (b & (b >> 4));
        B = (a & (b >> 4)) ^ (b & ((a ^ b) >> 4));
        C ^= (a & (c >> 4)) ^ (b & (d >> 4));
        D ^= (b & (c >> 4)) ^ ((a ^ b) & (d >> 4));

        a = A; b = B; c = C; d = D;
        C ^= (a & (c >> 8)) ^ (b & (d >> 8));
        D ^= (b & (c >> 8)) ^ ((a ^ b) & (d >> 8));

        a = C ^ (C >> 1);
        b = D ^ (D >> 1);

        uint i0 = x ^ y;
        uint i1 = b | (0xFFFF ^ (i0 | a));

        i0 = (i0 | (i0 << 8)) & 0x00FF00FF;
        i0 = (i0 | (i0 << 4)) & 0x0F0F0F0F;
        i0 = (i0 | (i0 << 2)) & 0x33333333;
        i0 = (i0 | (i0 << 1)) & 0x55555555;

        i1 = (i1 | (i1 << 8)) & 0x00FF00FF;
        i1 = (i1 | (i1 << 4)) & 0x0F0F0F0F;
        i1 = (i1 | (i1 << 2)) & 0x33333333;
        i1 = (i1 | (i1 << 1)) & 0x55555555;

        return (i1 << 1) | i0;
    }

    /// <summary>Row order sorted by descending Hilbert value of envelope centres (null envelopes last).</summary>
    public static int[] HilbertOrder(Envelope?[] envelopes, Envelope extent)
    {
        const uint max = (1 << 16) - 1;
        double w = extent.Width, h = extent.Height;
        var keys = new uint[envelopes.Length];
        for (int i = 0; i < envelopes.Length; i++)
        {
            if (envelopes[i] is not { IsNull: false } e) { keys[i] = 0; continue; }
            uint x = w > 0 ? (uint)Math.Floor(max * ((e.MinX + e.MaxX) / 2 - extent.MinX) / w) : 0;
            uint y = h > 0 ? (uint)Math.Floor(max * ((e.MinY + e.MaxY) / 2 - extent.MinY) / h) : 0;
            keys[i] = Hilbert(x, y);
        }
        var order = Enumerable.Range(0, envelopes.Length).ToArray();
        // stable: equal keys keep input order
        Array.Sort(order, (p, q) => keys[p] != keys[q] ? keys[q].CompareTo(keys[p]) : p.CompareTo(q));
        return order;
    }

    /// <summary>Builds the serialised tree from leaf envelopes (in final feature order) and feature byte offsets.</summary>
    public static byte[] Build(Envelope?[] leaves, ulong[] offsets, ushort nodeSize)
    {
        ulong n = (ulong)leaves.Length;
        var lb = LevelBounds(n, nodeSize);
        long numNodes = lb[0].End;
        var minX = new double[numNodes]; var minY = new double[numNodes];
        var maxX = new double[numNodes]; var maxY = new double[numNodes];
        var off = new ulong[numNodes];
        long leafStart = numNodes - (long)n;
        for (long i = 0; i < (long)n; i++)
        {
            long k = leafStart + i;
            if (leaves[i] is { IsNull: false } e) { minX[k] = e.MinX; minY[k] = e.MinY; maxX[k] = e.MaxX; maxY[k] = e.MaxY; }
            else { minX[k] = minY[k] = double.PositiveInfinity; maxX[k] = maxY[k] = double.NegativeInfinity; }
            off[k] = offsets[i];
        }
        for (int level = 0; level < lb.Length - 1; level++)
        {
            long pos = lb[level].Start, end = lb[level].End, newPos = lb[level + 1].Start;
            while (pos < end)
            {
                double a = double.PositiveInfinity, b = double.PositiveInfinity, c = double.NegativeInfinity, d = double.NegativeInfinity;
                off[newPos] = (ulong)pos;
                for (int j = 0; j < nodeSize && pos < end; j++, pos++)
                {
                    a = Math.Min(a, minX[pos]); b = Math.Min(b, minY[pos]);
                    c = Math.Max(c, maxX[pos]); d = Math.Max(d, maxY[pos]);
                }
                minX[newPos] = a; minY[newPos] = b; maxX[newPos] = c; maxY[newPos] = d;
                newPos++;
            }
        }
        var bytes = new byte[numNodes * NodeItemSize];
        for (long i = 0; i < numNodes; i++)
        {
            var s = bytes.AsSpan((int)(i * NodeItemSize), NodeItemSize);
            BinaryPrimitives.WriteDoubleLittleEndian(s, minX[i]);
            BinaryPrimitives.WriteDoubleLittleEndian(s[8..], minY[i]);
            BinaryPrimitives.WriteDoubleLittleEndian(s[16..], maxX[i]);
            BinaryPrimitives.WriteDoubleLittleEndian(s[24..], maxY[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(s[32..], off[i]);
        }
        return bytes;
    }

    /// <summary>
    /// Searches the tree. <paramref name="node"/> reads one 40-byte node by index, so the tree can live in a
    /// memory-mapped file or behind HTTP range requests. Returns (feature byte offset, feature index) in file order.
    /// </summary>
    public static List<(ulong Offset, ulong Index)> Search(ulong numItems, ushort nodeSize, Envelope box, Func<long, NodeItem> node)
    {
        var lb = LevelBounds(numItems, nodeSize);
        long numNodes = lb[0].End;
        long leafStart = numNodes - (long)numItems;
        var results = new List<(ulong, ulong)>();
        var queue = new Stack<(long Node, int Level)>();
        queue.Push((0, lb.Length - 1));
        while (queue.Count > 0)
        {
            var (nodeIndex, level) = queue.Pop();
            bool isLeaf = nodeIndex >= leafStart;
            long levelEnd = lb[level].End;
            long end = Math.Min(nodeIndex + nodeSize, levelEnd);
            for (long pos = nodeIndex; pos < end; pos++)
            {
                var it = node(pos);
                if (box.MaxX < it.MinX || box.MaxY < it.MinY || box.MinX > it.MaxX || box.MinY > it.MaxY) continue;
                if (isLeaf) results.Add((it.Offset, (ulong)(pos - leafStart)));
                else queue.Push(((long)it.Offset, level - 1));
            }
        }
        results.Sort((p, q) => p.Item1.CompareTo(q.Item1));
        return results;
    }

    public readonly record struct NodeItem(double MinX, double MinY, double MaxX, double MaxY, ulong Offset)
    {
        public static NodeItem Read(ReadOnlySpan<byte> s) => new(
            BinaryPrimitives.ReadDoubleLittleEndian(s), BinaryPrimitives.ReadDoubleLittleEndian(s[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(s[16..]), BinaryPrimitives.ReadDoubleLittleEndian(s[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(s[32..]));
    }
}
