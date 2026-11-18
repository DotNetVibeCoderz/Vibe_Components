using System.Collections.Concurrent;
using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net;

/// <summary>How rows are assigned to partitions.</summary>
public enum PartitionScheme
{
    /// <summary>Regular nx × ny grid over the total bounds (by envelope centre).</summary>
    Grid,
    /// <summary>Balanced k-d tree: recursive median splits on x/y until a partition holds at most N rows.</summary>
    KdTree,
    /// <summary>H3 cell (of the envelope centre in WGS 84) at a given resolution.</summary>
    H3,
    /// <summary>The value of an attribute column.</summary>
    Column,
}

/// <summary>One partition: its key, rows and the bounds of its geometries (used for pruning).</summary>
public sealed record GeoPartition(string Key, GeoDataFrame Frame, int[] RowIds, Envelope Bounds)
{
    public int RowCount => Frame.RowCount;
}

/// <summary>
/// A GeoDataFrame split into spatial partitions (grid, k-d tree, H3 cell or attribute value) — the single-machine core of distributed
/// processing: partitions are processed in parallel (<see cref="Map"/>, <see cref="MapReduce{T}"/>), bounding-box queries skip
/// partitions whose bounds do not intersect, and the spatial join only pairs each partition with the right rows overlapping it.
/// Partitions keep the original row ids, so <see cref="Collect"/> restores the source order and a partitioned spatial join returns
/// exactly what <see cref="GeoDataFrame.SJoin"/> returns. Each partition is a plain GeoDataFrame, so the same partitions can be
/// shipped to workers (Orleans grains, Akka actors, Spark executors) or written as a partitioned GeoParquet dataset.
/// </summary>
public sealed class PartitionedGeoDataFrame
{
    public PartitionedGeoDataFrame(IReadOnlyList<GeoPartition> partitions)
    {
        Partitions = partitions;
    }

    public IReadOnlyList<GeoPartition> Partitions { get; }
    public int PartitionCount => Partitions.Count;
    public long RowCount => Partitions.Sum(p => (long)p.RowCount);
    public Envelope TotalBounds
    {
        get { var e = new Envelope(); foreach (var p in Partitions) e.ExpandToInclude(p.Bounds); return e; }
    }

    /// <summary>Partitions a frame. <paramref name="size"/> is the grid size (nx = ny) for Grid, the maximum rows per partition for
    /// KdTree and the resolution for H3; Column uses <paramref name="column"/>.</summary>
    public static PartitionedGeoDataFrame Create(GeoDataFrame gdf, PartitionScheme scheme, int size = 4, string? column = null)
    {
        var keys = new string[gdf.RowCount];
        var env = Enumerable.Range(0, gdf.RowCount).Select(i => gdf.Geometry[i] is { IsEmpty: false } g ? g.EnvelopeInternal : null).ToArray();
        switch (scheme)
        {
            case PartitionScheme.Grid:
            {
                if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
                var tb = gdf.TotalBounds;
                for (int i = 0; i < keys.Length; i++)
                {
                    if (env[i] is not { } e) { keys[i] = "empty"; continue; }
                    var c = e.Centre;
                    int gx = tb.Width > 0 ? Math.Min(size - 1, (int)((c.X - tb.MinX) / tb.Width * size)) : 0;
                    int gy = tb.Height > 0 ? Math.Min(size - 1, (int)((c.Y - tb.MinY) / tb.Height * size)) : 0;
                    keys[i] = $"g{gx}_{gy}";
                }
                break;
            }
            case PartitionScheme.KdTree:
            {
                if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
                var rows = Enumerable.Range(0, keys.Length).Where(i => env[i] is not null).ToArray();
                foreach (var i in Enumerable.Range(0, keys.Length).Where(i => env[i] is null)) keys[i] = "empty";
                void Split(int[] r, string key, int depth)
                {
                    if (r.Length <= size) { foreach (var i in r) keys[i] = key == "" ? "k" : "k" + key; return; }
                    bool byX = depth % 2 == 0;
                    var sorted = r.OrderBy(i => byX ? env[i]!.Centre.X : env[i]!.Centre.Y).ThenBy(i => i).ToArray();
                    int half = sorted.Length / 2;
                    Split(sorted[..half], key + "0", depth + 1);
                    Split(sorted[half..], key + "1", depth + 1);
                }
                Split(rows, "", 0);
                break;
            }
            case PartitionScheme.H3:
            {
                var src = gdf.Crs is { IsGeographic: false } ? gdf.ToCrs(4326) : gdf;
                for (int i = 0; i < keys.Length; i++)
                {
                    var g = src.Geometry[i];
                    if (g is null || g.IsEmpty) { keys[i] = "empty"; continue; }
                    var c = g.EnvelopeInternal.Centre;
                    keys[i] = Geometry.Index.H3.ToString(Geometry.Index.H3.LatLngToCell(c.Y, c.X, size));
                }
                break;
            }
            case PartitionScheme.Column:
            {
                var col = gdf[column ?? throw new ArgumentNullException(nameof(column))];
                for (int i = 0; i < keys.Length; i++)
                    keys[i] = Convert.ToString(col.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
                break;
            }
        }
        var groups = Enumerable.Range(0, keys.Length).GroupBy(i => keys[i]).OrderBy(g => g.Key, StringComparer.Ordinal);
        return new PartitionedGeoDataFrame(groups.Select(g =>
        {
            var ids = g.ToArray();
            var b = new Envelope();
            foreach (var i in ids) if (env[i] is { } e) b.ExpandToInclude(e);
            return new GeoPartition(g.Key, gdf.Take(ids), ids, b);
        }).ToList());
    }

    /// <summary>All rows in the original order.</summary>
    public GeoDataFrame Collect()
    {
        if (Partitions.Count == 0) throw new InvalidOperationException("No partitions.");
        var all = GeoDataFrame.Concat(Partitions.Select(p => p.Frame).ToArray());
        var ids = Partitions.SelectMany(p => p.RowIds).ToArray();
        var order = Enumerable.Range(0, ids.Length).OrderBy(i => ids[i]).ToArray();   // stable
        return all.Take(order);
    }

    /// <summary>Applies a function to every partition in parallel. Row ids are kept when the row count is unchanged.</summary>
    public PartitionedGeoDataFrame Map(Func<GeoDataFrame, GeoDataFrame> f, int maxDegreeOfParallelism = -1)
    {
        var result = new GeoPartition[Partitions.Count];
        Parallel.For(0, Partitions.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, k =>
        {
            var p = Partitions[k];
            var f2 = f(p.Frame);
            var ids = f2.RowCount == p.RowCount ? p.RowIds : Enumerable.Repeat(p.RowIds.Length > 0 ? p.RowIds[0] : 0, f2.RowCount).ToArray();
            result[k] = new GeoPartition(p.Key, f2, ids, Bounds(f2));
        });
        return new PartitionedGeoDataFrame(result);
    }

    /// <summary>Map each partition to a value in parallel, then fold the values (in partition order).</summary>
    public T MapReduce<T>(Func<GeoDataFrame, T> map, Func<T, T, T> reduce, int maxDegreeOfParallelism = -1)
    {
        if (Partitions.Count == 0) throw new InvalidOperationException("No partitions.");
        var values = new T[Partitions.Count];
        Parallel.For(0, Partitions.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, k => values[k] = map(Partitions[k].Frame));
        return values.Aggregate(reduce);
    }

    /// <summary>Partitions whose bounds intersect a box (partition pruning).</summary>
    public IEnumerable<GeoPartition> Prune(Envelope box) => Partitions.Where(p => !p.Bounds.IsNull && p.Bounds.Intersects(box));

    /// <summary>Rows intersecting a box, reading only the partitions that can contain them (original order).</summary>
    public GeoDataFrame Query(Envelope box)
    {
        var poly = Geometry.Geo.Box(box.MinX, box.MinY, box.MaxX, box.MaxY);
        var hits = new ConcurrentBag<(int Id, GeoDataFrame Frame, int Row)>();
        Parallel.ForEach(Prune(box), p =>
        {
            foreach (int i in p.Frame.SIndex.Query(box))
                if (p.Frame.Geometry[i]!.Intersects(poly)) hits.Add((p.RowIds[i], p.Frame, i));
        });
        var sorted = hits.OrderBy(h => h.Id).ToList();
        if (sorted.Count == 0) return Partitions[0].Frame.Take(Array.Empty<int>());
        return GeoDataFrame.Concat(sorted.Select(h => h.Frame.Take([h.Row])).ToArray());
    }

    /// <summary>
    /// Partition-aware spatial join: each partition is joined, in parallel, only with the right rows whose envelopes intersect the
    /// partition bounds. The collected result equals <c>Collect().SJoin(right, …)</c> (index_right refers to the full right frame).
    /// </summary>
    public PartitionedGeoDataFrame SJoin(GeoDataFrame right, SpatialPredicate predicate = SpatialPredicate.Intersects, JoinHow how = JoinHow.Inner,
        string leftSuffix = "_left", string rightSuffix = "_right")
    {
        if (predicate == SpatialPredicate.Disjoint) throw new NotSupportedException("sjoin with 'disjoint' is not supported (it is a cross product).");
        if (how is not (JoinHow.Inner or JoinHow.Left)) throw new NotSupportedException("Partitioned sjoin supports Inner and Left.");
        var result = new GeoPartition[Partitions.Count];
        Parallel.For(0, Partitions.Count, k =>
        {
            var p = Partitions[k];
            var cands = p.Bounds.IsNull ? [] : right.SIndex.Query(p.Bounds).Distinct().Order().ToArray();
            var sub = right.Take(cands);
            var pairs = cands.Length == 0 ? [] : p.Frame.FindPairs(sub, predicate);
            var li = new List<int>(); var ri = new List<int>();
            int q = 0;
            for (int i = 0; i < p.RowCount; i++)
            {
                bool any = false;
                while (q < pairs.Count && pairs[q].L == i) { li.Add(i); ri.Add(cands[pairs[q].R]); q++; any = true; }
                if (!any && how == JoinHow.Left) { li.Add(i); ri.Add(-1); }
            }
            var joined = p.Frame.JoinResult(right, li.ToArray(), ri.ToArray(), leftSuffix, rightSuffix);
            result[k] = new GeoPartition(p.Key, joined, li.Select(i => p.RowIds[i]).ToArray(), Bounds(joined));
        });
        return new PartitionedGeoDataFrame(result);
    }

    /// <summary>Row count, bounds and row ids per partition.</summary>
    public DataFrame Summary() => new(
        Column.Create("partition", Partitions.Select(p => p.Key).ToArray()),
        Column.Create("rows", Partitions.Select(p => (long)p.RowCount).ToArray()),
        Column.Create("minx", Partitions.Select(p => p.Bounds.IsNull ? double.NaN : p.Bounds.MinX).ToArray()),
        Column.Create("miny", Partitions.Select(p => p.Bounds.IsNull ? double.NaN : p.Bounds.MinY).ToArray()),
        Column.Create("maxx", Partitions.Select(p => p.Bounds.IsNull ? double.NaN : p.Bounds.MaxX).ToArray()),
        Column.Create("maxy", Partitions.Select(p => p.Bounds.IsNull ? double.NaN : p.Bounds.MaxY).ToArray()));

    /// <summary>Partition bounds as a GeoDataFrame of rectangles (for plotting).</summary>
    public GeoDataFrame BoundsFrame() => GeoDataFrame.FromGeometries(
        Partitions.Select(p => p.Bounds.IsNull ? null : (NetTopologySuite.Geometries.Geometry?)Geometry.Geo.Box(p.Bounds.MinX, p.Bounds.MinY, p.Bounds.MaxX, p.Bounds.MaxY)),
        Partitions.Count > 0 ? Partitions[0].Frame.Crs : null,
        Column.Create("partition", Partitions.Select(p => p.Key).ToArray()),
        Column.Create("rows", Partitions.Select(p => (long)p.RowCount).ToArray()));

    internal static Envelope Bounds(GeoDataFrame f)
    {
        var e = new Envelope();
        for (int i = 0; i < f.RowCount; i++) if (f.Geometry[i] is { IsEmpty: false } g) e.ExpandToInclude(g.EnvelopeInternal);
        return e;
    }
}
