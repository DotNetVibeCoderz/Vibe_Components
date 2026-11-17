using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GeoPandas.Net.Geometry;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Versioning;

/// <summary>Kind of row change in a <see cref="FrameDiff"/>.</summary>
public enum ChangeKind { Added, Removed, Modified }

/// <summary>One modified row: its key and the columns whose values changed.</summary>
public sealed record RowChange(object? Key, int BeforeRow, int AfterRow, IReadOnlyList<string> ChangedColumns);

/// <summary>
/// Difference between two immutable frames (dataset versions). With a key column, rows are matched by key and
/// modified rows report which columns changed; without a key, rows are compared as a multiset of content hashes.
/// Geometries compare by exact WKB; numbers compare by value (an int 3 equals a long 3 and a double 3.0).
/// </summary>
public sealed class FrameDiff
{
    private FrameDiff(DataFrame before, DataFrame after, string? key, int[] added, int[] removed, RowChange[] modified,
        string[] addedColumns, string[] removedColumns)
    {
        Before = before; After = after; Key = key;
        AddedRows = added; RemovedRows = removed; Modified = modified;
        AddedColumns = addedColumns; RemovedColumns = removedColumns;
    }

    public DataFrame Before { get; }
    public DataFrame After { get; }
    public string? Key { get; }
    /// <summary>Row indices in <see cref="After"/> that are new.</summary>
    public IReadOnlyList<int> AddedRows { get; }
    /// <summary>Row indices in <see cref="Before"/> that disappeared.</summary>
    public IReadOnlyList<int> RemovedRows { get; }
    public IReadOnlyList<RowChange> Modified { get; }
    public IReadOnlyList<string> AddedColumns { get; }
    public IReadOnlyList<string> RemovedColumns { get; }

    public bool IsEmpty => AddedRows.Count == 0 && RemovedRows.Count == 0 && Modified.Count == 0 && AddedColumns.Count == 0 && RemovedColumns.Count == 0;

    public DataFrame Added => After.Take(AddedRows.ToArray());
    public DataFrame Removed => Before.Take(RemovedRows.ToArray());
    /// <summary>Modified rows with their new values.</summary>
    public DataFrame ModifiedAfter => After.Take(Modified.Select(m => m.AfterRow).ToArray());
    /// <summary>Modified rows with their old values.</summary>
    public DataFrame ModifiedBefore => Before.Take(Modified.Select(m => m.BeforeRow).ToArray());

    public string Summary =>
        $"+{AddedRows.Count} added, -{RemovedRows.Count} removed, ~{Modified.Count} modified" +
        (AddedColumns.Count + RemovedColumns.Count > 0 ? $"; columns +[{string.Join(", ", AddedColumns)}] -[{string.Join(", ", RemovedColumns)}]" : "");

    public override string ToString() => Summary;

    /// <summary>
    /// All changed rows in one frame with a <c>_change</c> column (added / removed / modified), ready to map.
    /// Removed rows carry their old values, the others their new values.
    /// </summary>
    public DataFrame ToChangeFrame()
    {
        var parts = new List<DataFrame>();
        void Add(DataFrame part, string tag)
        {
            if (part.RowCount == 0) return;
            parts.Add(part.WithColumn(Column.Create("_change", Enumerable.Repeat(tag, part.RowCount).ToArray())));
        }
        Add(Added, "added");
        Add(Removed, "removed");
        Add(ModifiedAfter, "modified");
        if (parts.Count == 0) return After.Head(0).WithColumn(Column.Create("_change", Array.Empty<string>()));
        return parts.All(p => p is GeoDataFrame) ? GeoDataFrame.Concat(parts.Cast<GeoDataFrame>().ToArray()) : DataFrame.Concat(parts.ToArray());
    }

    /// <summary>Compares two versions. <paramref name="key"/> must be unique in both frames when given.</summary>
    public static FrameDiff Compare(DataFrame before, DataFrame after, string? key = null)
    {
        var bNames = before.ColumnNames.ToHashSet();
        var aNames = after.ColumnNames.ToHashSet();
        var common = after.ColumnNames.Where(bNames.Contains).ToArray();
        var addedCols = after.ColumnNames.Where(n => !bNames.Contains(n)).ToArray();
        var removedCols = before.ColumnNames.Where(n => !aNames.Contains(n)).ToArray();

        var bCols = common.Select(n => before[n]).ToArray();
        var aCols = common.Select(n => after[n]).ToArray();

        if (key is null)
        {
            var bHash = RowHashes(before, common);
            var aHash = RowHashes(after, common);
            var pool = new Dictionary<ulong, Queue<int>>();
            for (int i = 0; i < bHash.Length; i++)
            {
                if (!pool.TryGetValue(bHash[i], out var q)) pool[bHash[i]] = q = new Queue<int>();
                q.Enqueue(i);
            }
            var added = new List<int>();
            for (int j = 0; j < aHash.Length; j++)
            {
                if (pool.TryGetValue(aHash[j], out var q) && q.Count > 0) q.Dequeue();
                else added.Add(j);
            }
            var removed = pool.Values.SelectMany(q => q).Order().ToArray();
            return new FrameDiff(before, after, null, added.ToArray(), removed, [], addedCols, removedCols);
        }

        if (!bNames.Contains(key) || !aNames.Contains(key)) throw new ArgumentException($"Key column '{key}' must exist in both frames.");
        var bIndex = KeyIndex(before[key]);
        var aIndex = KeyIndex(after[key]);
        var addedRows = new List<int>();
        var modified = new List<RowChange>();
        foreach (var (k, j) in aIndex)
        {
            if (!bIndex.TryGetValue(k, out int i)) { addedRows.Add(j); continue; }
            var changed = new List<string>();
            for (int c = 0; c < common.Length; c++)
                if (CellHash(bCols[c], i) != CellHash(aCols[c], j)) changed.Add(common[c]);
            if (changed.Count > 0) modified.Add(new RowChange(after[key].GetValue(j), i, j, changed));
        }
        var removedRows = bIndex.Where(kv => !aIndex.ContainsKey(kv.Key)).Select(kv => kv.Value).Order().ToArray();
        return new FrameDiff(before, after, key, addedRows.Order().ToArray(), removedRows, modified.OrderBy(m => m.AfterRow).ToArray(), addedCols, removedCols);
    }

    private static Dictionary<string, int> KeyIndex(Column c)
    {
        var d = new Dictionary<string, int>(c.Length);
        for (int i = 0; i < c.Length; i++)
        {
            if (c.IsNull(i)) throw new ArgumentException($"Key column '{c.Name}' contains nulls (row {i}).");
            if (!d.TryAdd(DataFrame.KeyOf(c, i), i)) throw new ArgumentException($"Key column '{c.Name}' is not unique: '{c.GetValue(i)}' repeats.");
        }
        return d;
    }

    // ------------------------------------------------------------------ hashing

    private static ulong[] RowHashes(DataFrame df, string[] columns)
    {
        var cols = columns.Order(StringComparer.Ordinal).Select(n => df[n]).ToArray();
        var res = new ulong[df.RowCount];
        Parallel.For(0, res.Length, i =>
        {
            ulong h = Fnv.Offset;
            foreach (var c in cols) h = Fnv.Mix(h, CellHash(c, i));
            res[i] = h;
        });
        return res;
    }

    /// <summary>Stable 64-bit hash of one cell (FNV-1a over a canonical byte encoding).</summary>
    public static ulong CellHash(Column c, int i)
    {
        if (c.IsNull(i)) return 0x9E3779B97F4A7C15UL;
        Span<byte> buf = stackalloc byte[9];
        switch (c.GetValue(i))
        {
            case double d:
                if (double.IsNaN(d)) return 0x9E3779B97F4A7C15UL;
                return NumberHash(d);
            case float f: return float.IsNaN(f) ? 0x9E3779B97F4A7C15UL : NumberHash(f);
            case long l: return NumberHash(l);
            case int n: return NumberHash(n);
            case short s: return NumberHash(s);
            case byte b: return NumberHash(b);
            case bool b: buf[0] = 2; buf[1] = b ? (byte)1 : (byte)0; return Fnv.Hash(buf[..2]);
            case DateTime dt:
                buf[0] = 3; BinaryPrimitives.WriteInt64LittleEndian(buf[1..], (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).Ticks);
                return Fnv.Hash(buf);
            case string s: return Fnv.Mix(Fnv.Hash([4]), Fnv.Hash(Encoding.UTF8.GetBytes(s)));
            case byte[] bytes: return Fnv.Mix(Fnv.Hash([5]), Fnv.Hash(bytes));
            case NtsGeometry g: return Fnv.Mix(Fnv.Hash([6]), Fnv.Hash(Geo.ToWkb(g)));
            case var o: return Fnv.Mix(Fnv.Hash([7]), Fnv.Hash(Encoding.UTF8.GetBytes(Convert.ToString(o, CultureInfo.InvariantCulture) ?? "")));
        }
    }

    private static ulong NumberHash(double v)
    {
        Span<byte> buf = stackalloc byte[9];
        buf[0] = 1;
        if (v == 0) v = 0; // -0 == 0
        BinaryPrimitives.WriteDoubleLittleEndian(buf[1..], v);
        return Fnv.Hash(buf);
    }

    /// <summary>Order-sensitive SHA-256 content hash of a whole frame (schema + values), as lowercase hex.</summary>
    public static string ContentHash(DataFrame df)
    {
        var names = df.ColumnNames.Order(StringComparer.Ordinal).ToArray();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var n in names) { sha.AppendData(Encoding.UTF8.GetBytes(n)); sha.AppendData([0]); }
        var hashes = RowHashes(df, names);
        Span<byte> b = stackalloc byte[8];
        foreach (var h in hashes) { BinaryPrimitives.WriteUInt64LittleEndian(b, h); sha.AppendData(b); }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static class Fnv
    {
        public const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Hash(ReadOnlySpan<byte> data)
        {
            ulong h = Offset;
            foreach (var b in data) { h ^= b; h *= Prime; }
            return h;
        }

        public static ulong Mix(ulong h, ulong v)
        {
            for (int k = 0; k < 8; k++) { h ^= (byte)(v >> (8 * k)); h *= Prime; }
            return h;
        }
    }
}
