using System.Collections.Concurrent;
using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net;

public enum OverlayHow { Intersection, Union, Difference, SymmetricDifference, Identity }

public sealed partial class GeoDataFrame
{
    private void RequireSameCrs(GeoDataFrame other, string op)
    {
        if (Crs is not null && other.Crs is not null && Crs != other.Crs)
            throw new InvalidOperationException($"{op}: CRS mismatch between left ({Crs}) and right ({other.Crs}). Use ToCrs to align them first.");
    }

    // ------------------------------------------------------------------ spatial join

    /// <summary>
    /// Spatial join (GeoPandas <c>sjoin</c>): pairs rows where <c>left.geometry.predicate(right.geometry)</c> holds.
    /// Uses the right frame's STR-tree plus prepared geometries, in parallel. Adds an <c>index_right</c> column.
    /// </summary>
    public GeoDataFrame SJoin(GeoDataFrame right, SpatialPredicate predicate = SpatialPredicate.Intersects, JoinHow how = JoinHow.Inner,
        string leftSuffix = "_left", string rightSuffix = "_right")
    {
        RequireSameCrs(right, "sjoin");
        if (predicate == SpatialPredicate.Disjoint) throw new NotSupportedException("sjoin with 'disjoint' is not supported (it is a cross product).");
        if (how == JoinHow.Outer) throw new NotSupportedException("sjoin supports Inner, Left and Right.");
        if (how == JoinHow.Right)
        {
            // right join == left join with swapped roles and inverse predicate
            var swapped = right.SJoin(this, predicate.Inverse(), JoinHow.Left, rightSuffix, leftSuffix);
            return swapped;
        }

        var pairs = FindPairs(right, predicate);
        var li = new List<int>(pairs.Count); var ri = new List<int>(pairs.Count);
        int p = 0;
        for (int i = 0; i < RowCount; i++)
        {
            bool any = false;
            while (p < pairs.Count && pairs[p].L == i) { li.Add(i); ri.Add(pairs[p].R); p++; any = true; }
            if (!any && how == JoinHow.Left) { li.Add(i); ri.Add(-1); }
        }
        return JoinResult(right, li.ToArray(), ri.ToArray(), leftSuffix, rightSuffix);
    }

    /// <summary>Candidate pairs (left, right) satisfying the predicate, sorted by left then right index.</summary>
    internal List<(int L, int R)> FindPairs(GeoDataFrame right, SpatialPredicate predicate)
    {
        var tree = right.SIndex;
        var lg = Geometry; var rg = right.Geometry;
        var bag = new ConcurrentBag<List<(int, int)>>();
        var inverse = predicate.Inverse();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, RowCount, 256),
            () => (Pairs: new List<(int, int)>(), Prepared: new Dictionary<int, IPreparedGeometry>()),
            (range, _, local) =>
        {
            var candidates = new List<int>();
            for (int i = range.Item1; i < range.Item2; i++)
            {
                var g = lg[i];
                if (g is null || g.IsEmpty) continue;
                candidates.Clear();
                tree.Query(g.EnvelopeInternal, candidates.Add);
                if (candidates.Count == 0) continue;
                if (g is IPuntal)
                {
                    // point-in-polygon: prepare the (reused) right geometry and test the inverse predicate
                    foreach (var j in candidates)
                    {
                        if (!local.Prepared.TryGetValue(j, out var prepJ)) local.Prepared[j] = prepJ = PreparedGeometryFactory.Prepare(rg[j]!);
                        if (inverse.Evaluate(prepJ, g)) local.Pairs.Add((i, j));
                    }
                }
                else if (candidates.Count > 4)
                {
                    var prep = PreparedGeometryFactory.Prepare(g);
                    foreach (var j in candidates) if (predicate.Evaluate(prep, rg[j]!)) local.Pairs.Add((i, j));
                }
                else
                {
                    foreach (var j in candidates) if (predicate.Evaluate(g, rg[j]!)) local.Pairs.Add((i, j));
                }
            }
            return local;
        }, local => bag.Add(local.Pairs));
        var all = bag.SelectMany(x => x).ToList();
        all.Sort();
        return all;
    }

    private GeoDataFrame JoinResult(GeoDataFrame right, int[] li, int[] ri, string leftSuffix, string rightSuffix, Column? extra = null)
    {
        var rightAttrs = right.Columns.Where(c => c.Name != right.GeometryColumnName).ToList();
        var rightNames = new HashSet<string>(rightAttrs.Select(c => c.Name)) { "index_right" };
        var leftNames = new HashSet<string>(ColumnNames);
        var cols = new List<Column>();
        foreach (var c in Columns)
        {
            var t = c.Take(li);
            cols.Add(c.Name != GeometryColumnName && rightNames.Contains(c.Name) ? t.Rename(c.Name + leftSuffix) : t);
        }
        cols.Add(Column<long>.FromNullable("index_right", ri.Select(j => j < 0 ? (long?)null : j).ToArray()));
        foreach (var c in rightAttrs)
        {
            var t = c.Take(ri);
            cols.Add(leftNames.Contains(c.Name) ? t.Rename(c.Name + rightSuffix) : t);
        }
        if (extra is not null) cols.Add(extra);
        return new GeoDataFrame(cols, GeometryColumnName);
    }

    /// <summary>
    /// Nearest-neighbour spatial join (GeoPandas <c>sjoin_nearest</c>): each left row is matched with the closest right
    /// geometry (exact geometry distance, branch-and-bound on the STR-tree).
    /// </summary>
    public GeoDataFrame SJoinNearest(GeoDataFrame right, double maxDistance = double.PositiveInfinity, string? distanceColumn = "distance",
        JoinHow how = JoinHow.Inner, string leftSuffix = "_left", string rightSuffix = "_right")
    {
        RequireSameCrs(right, "sjoin_nearest");
        if (IsGeographic) System.Diagnostics.Trace.TraceWarning("sjoin_nearest on a geographic CRS: distances are in degrees. Reproject for metric results.");
        var tree = right.SIndex;
        var rg = right.Geometry; var lg = Geometry;
        var match = new int[RowCount];
        var dist = new double[RowCount];
        Parallel.For(0, RowCount, i =>
        {
            match[i] = -1; dist[i] = double.NaN;
            var g = lg[i];
            if (g is null || g.IsEmpty) return;
            var res = tree.Nearest(g.EnvelopeInternal, 1, j => rg[j]!.Distance(g), maxDistance);
            if (res.Count > 0) { match[i] = res[0].Item; dist[i] = res[0].Distance; }
        });
        var li = new List<int>(); var ri = new List<int>(); var dl = new List<double>();
        for (int i = 0; i < RowCount; i++)
        {
            if (match[i] < 0 && how == JoinHow.Inner) continue;
            li.Add(i); ri.Add(match[i]); dl.Add(dist[i]);
        }
        var extra = distanceColumn is null ? null : Column.Create(distanceColumn, dl.ToArray());
        return JoinResult(right, li.ToArray(), ri.ToArray(), leftSuffix, rightSuffix, extra);
    }

    // ------------------------------------------------------------------ overlay

    /// <summary>
    /// Polygon overlay (GeoPandas <c>overlay</c>): intersection, union, difference, symmetric difference or identity.
    /// Attributes from both frames are carried (suffixes <c>_1</c>/<c>_2</c> on collisions).
    /// </summary>
    public GeoDataFrame Overlay(GeoDataFrame right, OverlayHow how = OverlayHow.Intersection, bool keepGeomType = true)
    {
        RequireSameCrs(right, "overlay");
        var lg = Geometry; var rg = right.Geometry;
        var pairs = FindPairs(right, SpatialPredicate.Intersects);
        var dim = Enumerable.Range(0, RowCount).Select(i => lg[i]?.Dimension).FirstOrDefault(d => d is not null) ?? Dimension.Surface;

        var parts = new List<(int L, int R, NtsGeometry G)>();
        if (how is OverlayHow.Intersection or OverlayHow.Union or OverlayHow.Identity)
        {
            var inter = new (int, int, NtsGeometry?)[pairs.Count];
            Parallel.For(0, pairs.Count, k =>
            {
                var (i, j) = pairs[k];
                var g = GeometryOps.Intersection(lg[i]!, rg[j]!);
                if (keepGeomType) g = GeometryOps.KeepDimension(g, dim);
                inter[k] = (i, j, g.IsEmpty ? null : g);
            });
            parts.AddRange(inter.Where(x => x.Item3 is not null).Select(x => (x.Item1, x.Item2, x.Item3!)));
        }

        if (how is OverlayHow.Difference or OverlayHow.Union or OverlayHow.SymmetricDifference or OverlayHow.Identity)
        {
            var byLeft = pairs.GroupBy(p => p.L).ToDictionary(g => g.Key, g => g.Select(p => p.R).ToArray());
            var diffs = new NtsGeometry?[RowCount];
            Parallel.For(0, RowCount, i =>
            {
                var g = lg[i];
                if (g is null) return;
                if (byLeft.TryGetValue(i, out var rs)) g = GeometryOps.Difference(g, GeometryOps.UnaryUnion(rs.Select(j => rg[j])));
                if (keepGeomType) g = GeometryOps.KeepDimension(g, dim);
                diffs[i] = g.IsEmpty ? null : g;
            });
            for (int i = 0; i < RowCount; i++) if (diffs[i] is { } d) parts.Add((i, -1, d));
        }

        if (how is OverlayHow.Union or OverlayHow.SymmetricDifference)
        {
            var byRight = pairs.GroupBy(p => p.R).ToDictionary(g => g.Key, g => g.Select(p => p.L).ToArray());
            var diffs = new NtsGeometry?[right.RowCount];
            Parallel.For(0, right.RowCount, j =>
            {
                var g = rg[j];
                if (g is null) return;
                if (byRight.TryGetValue(j, out var ls)) g = GeometryOps.Difference(g, GeometryOps.UnaryUnion(ls.Select(i => lg[i])));
                if (keepGeomType) g = GeometryOps.KeepDimension(g, dim);
                diffs[j] = g.IsEmpty ? null : g;
            });
            for (int j = 0; j < right.RowCount; j++) if (diffs[j] is { } d) parts.Add((-1, j, d));
        }

        var li = parts.Select(p => p.L).ToArray();
        var ri = parts.Select(p => p.R).ToArray();
        var leftAttrs = Columns.Where(c => c.Name != GeometryColumnName).ToList();
        var rightAttrs = how == OverlayHow.Difference ? [] : right.Columns.Where(c => c.Name != right.GeometryColumnName).ToList();
        var lNames = leftAttrs.Select(c => c.Name).ToHashSet();
        var rNames = rightAttrs.Select(c => c.Name).ToHashSet();
        var cols = new List<Column>();
        foreach (var c in leftAttrs) cols.Add(rNames.Contains(c.Name) ? c.Take(li).Rename(c.Name + "_1") : c.Take(li));
        foreach (var c in rightAttrs) cols.Add(lNames.Contains(c.Name) ? c.Take(ri).Rename(c.Name + "_2") : c.Take(ri));
        cols.Add(new GeoSeries(GeometryColumnName, parts.Select(p => (NtsGeometry?)p.G).ToArray(), Crs));
        return new GeoDataFrame(cols, GeometryColumnName);
    }

    // ------------------------------------------------------------------ dissolve / clip / explode

    /// <summary>
    /// Dissolves geometries by key column(s) (GeoPandas <c>dissolve</c>). Other columns are aggregated with
    /// <paramref name="aggFunc"/> (numeric) or First (non-numeric) unless overridden in <paramref name="aggregations"/>.
    /// </summary>
    public GeoDataFrame Dissolve(string[]? by = null, AggFunc aggFunc = AggFunc.First, IReadOnlyDictionary<string, AggFunc>? aggregations = null)
    {
        by ??= [];
        List<(object?[] Key, IReadOnlyList<int> Rows)> groups = by.Length == 0
            ? [(Array.Empty<object?>(), Enumerable.Range(0, RowCount).ToArray())]
            : GroupBy(by).Groups.ToList();
        var geoms = new NtsGeometry?[groups.Count];
        Parallel.For(0, groups.Count, g => geoms[g] = GeometryOps.UnaryUnion(groups[g].Rows.Select(r => Geometry[r])));

        var cols = new List<Column>();
        for (int k = 0; k < by.Length; k++)
            cols.Add(Column.FromObjects(by[k], groups.Select(g => g.Key[k]).ToArray()));
        foreach (var c in Columns)
        {
            if (c.Name == GeometryColumnName || by.Contains(c.Name)) continue;
            var f = aggregations is not null && aggregations.TryGetValue(c.Name, out var af) ? af : aggFunc;
            if (!c.IsNumeric && f is not (AggFunc.First or AggFunc.Last or AggFunc.Count or AggFunc.NUnique)) f = AggFunc.First;
            cols.Add(GeoPandas.Net.GroupBy.Aggregate(c, f, c.Name, groups.Select(g => g.Rows)));
        }
        cols.Add(new GeoSeries(GeometryColumnName, geoms, Crs));
        return new GeoDataFrame(cols, GeometryColumnName);
    }

    public GeoDataFrame Dissolve(string by, AggFunc aggFunc = AggFunc.First) => Dissolve([by], aggFunc);

    /// <summary>Clips geometries to a mask polygon (GeoPandas <c>clip</c>), keeping geometry types.</summary>
    public GeoDataFrame Clip(NtsGeometry mask, bool keepGeomType = true)
    {
        var prepared = PreparedGeometryFactory.Prepare(mask);
        var idx = SIndex.Query(mask.EnvelopeInternal).Order().ToArray();
        var keep = new List<int>();
        var geoms = new List<NtsGeometry?>();
        foreach (var i in idx)
        {
            var g = Geometry[i]!;
            if (!prepared.Intersects(g)) continue;
            NtsGeometry clipped = prepared.Covers(g) ? g : GeometryOps.Intersection(g, mask);
            if (keepGeomType) clipped = GeometryOps.KeepDimension(clipped, g.Dimension);
            if (clipped.IsEmpty) continue;
            keep.Add(i);
            geoms.Add(clipped);
        }
        var cols = Columns.Where(c => c.Name != GeometryColumnName).Select(c => c.Take(keep.ToArray())).ToList();
        cols.Add(new GeoSeries(GeometryColumnName, geoms.ToArray(), Crs));
        return new GeoDataFrame(cols, GeometryColumnName);
    }

    public GeoDataFrame Clip(GeoDataFrame mask) => Clip(mask.UnionAll());

    /// <summary>Explodes multi-part geometries into one row per part (GeoPandas <c>explode</c>).</summary>
    public GeoDataFrame Explode()
    {
        var counts = new int[RowCount];
        var parts = new List<NtsGeometry?>();
        for (int i = 0; i < RowCount; i++)
        {
            var g = Geometry[i];
            if (g is null) { counts[i] = 1; parts.Add(null); continue; }
            var ps = GeometryOps.Explode(g).ToList();
            counts[i] = ps.Count;
            parts.AddRange(ps);
        }
        var cols = Columns.Where(c => c.Name != GeometryColumnName).Select(c => c.Repeat(counts)).ToList();
        cols.Add(new GeoSeries(GeometryColumnName, parts.ToArray(), Crs));
        return new GeoDataFrame(cols, GeometryColumnName);
    }

    /// <summary>Concatenates GeoDataFrames (CRS must match).</summary>
    public static GeoDataFrame Concat(params GeoDataFrame[] frames)
    {
        if (frames.Length == 0) throw new ArgumentException("Nothing to concatenate.");
        var crs = frames[0].Crs;
        if (frames.Any(f => f.Crs != crs)) throw new InvalidOperationException("All frames must share the same CRS.");
        return (GeoDataFrame)DataFrame.Concat(frames);
    }
}
