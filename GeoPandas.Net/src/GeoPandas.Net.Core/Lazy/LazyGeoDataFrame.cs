using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Lazy;

/// <summary>
/// A deferred GeoDataFrame: a query plan over an <see cref="IGeoScanSource"/> that is optimised (bbox, column, attribute and
/// limit pushdown) and executed in streaming batches only when <see cref="Collect"/>, <see cref="Stream"/> or <see cref="Count"/> is called.
/// Like Polars' <c>LazyFrame</c> or Dask-GeoPandas: memory stays bounded by the batch size, not the dataset size.
/// </summary>
/// <example>
/// <code>
/// var parks = GeoIO.Scan("city.fgb")
///     .FilterBbox(700_000, 9_300_000, 705_000, 9_305_000)   // pushed into the FlatGeobuf Hilbert R-tree
///     .Where("landuse", CompareOp.Eq, "park")
///     .Select("name", "area_m2")
///     .Collect();
/// </code>
/// </example>
public sealed class LazyGeoDataFrame
{
    private readonly IGeoScanSource _source;
    private readonly PlanNode[] _plan;
    private readonly int _batchSize;

    private LazyGeoDataFrame(IGeoScanSource source, PlanNode[] plan, int batchSize)
    {
        _source = source; _plan = plan; _batchSize = batchSize;
    }

    public static LazyGeoDataFrame From(IGeoScanSource source, int batchSize = 65_536) => new(source, [], batchSize);

    public IGeoScanSource Source => _source;

    private LazyGeoDataFrame With(PlanNode node) => new(_source, [.. _plan, node], _batchSize);

    /// <summary>Rows per batch requested from the source.</summary>
    public LazyGeoDataFrame WithBatchSize(int rows) => new(_source, _plan, Math.Max(1, rows));

    // ------------------------------------------------------------------ plan builders

    /// <summary>Rows whose geometry envelope intersects the box (GeoPandas <c>read_file(bbox=...)</c>), in the current CRS.</summary>
    public LazyGeoDataFrame FilterBbox(double minX, double minY, double maxX, double maxY) => FilterBbox(new Envelope(minX, maxX, minY, maxY));
    public LazyGeoDataFrame FilterBbox(Envelope box) => With(new BboxNode(box));

    /// <summary>Rows where <c>geometry.predicate(other)</c> holds. The envelope of <paramref name="other"/> is pushed down as a bbox pre-filter.</summary>
    public LazyGeoDataFrame Filter(SpatialPredicate predicate, NtsGeometry other) => With(new SpatialNode(predicate, other));

    /// <summary>Attribute comparison that sources such as SQL databases and GeoParquet can push down.</summary>
    public LazyGeoDataFrame Where(string column, CompareOp op, object value) => With(new AttributeNode(new AttributePredicate(column, op, value)));

    /// <summary>Typed predicate on one column (evaluated by the engine; only that column is read).</summary>
    public LazyGeoDataFrame Filter<T>(string column, Func<T, bool> predicate) =>
        With(new RowNode($"{column}: {typeof(T).Name} predicate", [column], f =>
        {
            var c = f[column];
            if (c is Column<T> typed) return typed.Mask(predicate);
            var m = new bool[f.RowCount];
            for (int i = 0; i < m.Length; i++)
                m[i] = c.GetValue(i) is { } v && predicate((T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture));
            return m;
        }));

    /// <summary>Arbitrary row predicate. Declare <paramref name="dependsOn"/> so column pruning still works; without it all columns are read.</summary>
    public LazyGeoDataFrame Filter(Func<DataRow, bool> predicate, params string[] dependsOn) =>
        With(new RowNode("row predicate", dependsOn.Length == 0 ? null : dependsOn, f =>
        {
            var m = new bool[f.RowCount];
            for (int i = 0; i < m.Length; i++) m[i] = predicate(new DataRow(f, i));
            return m;
        }));

    /// <summary>Keeps only these attribute columns (the geometry is always kept).</summary>
    public LazyGeoDataFrame Select(params string[] columns) => With(new ProjectNode(columns));

    /// <summary>Adds a computed column.</summary>
    public LazyGeoDataFrame WithColumn<T>(string name, Func<DataRow, T> compute, params string[] dependsOn) =>
        With(new MapNode(name, dependsOn.Length == 0 ? null : dependsOn, f => f.WithColumn(name, compute)));

    /// <summary>Reprojects the geometry. Later bbox filters are written in the new CRS and are transformed back for pushdown.</summary>
    public LazyGeoDataFrame ToCrs(CoordinateReferenceSystem crs) => With(new CrsNode(crs));
    public LazyGeoDataFrame ToCrs(int epsg) => ToCrs(CoordinateReferenceSystem.FromEpsg(epsg));

    public LazyGeoDataFrame Limit(long n) => With(new LimitNode(Math.Max(0, n)));

    // ------------------------------------------------------------------ execution

    /// <summary>Runs the plan and concatenates every batch.</summary>
    public GeoDataFrame Collect()
    {
        var parts = Stream().ToArray();
        if (parts.Length == 1) return parts[0];
        if (parts.Length > 0) return GeoDataFrame.Concat(parts);
        return EmptyResult();
    }

    /// <summary>First n rows.</summary>
    public GeoDataFrame Head(int n = 5) => Limit(n).Collect();

    /// <summary>Counts matching rows without keeping them in memory.</summary>
    public long Count()
    {
        long n = 0;
        foreach (var b in Stream()) n += b.RowCount;
        return n;
    }

    /// <summary>Executes the optimised plan batch by batch.</summary>
    public IEnumerable<GeoDataFrame> Stream()
    {
        var opt = Optimize();
        var limits = new Dictionary<LimitNode, long>(ReferenceEqualityComparer.Instance);
        foreach (var n in _plan) if (n is LimitNode l) limits[l] = l.N;
        if (limits.Values.Any(v => v == 0)) yield break;

        var request = new ScanRequest { Bbox = opt.Bbox, Columns = opt.Columns, Limit = opt.Limit, Predicates = opt.Predicates, BatchSize = _batchSize };
        foreach (var batch in _source.Scan(request))
        {
            GeoDataFrame f = batch;
            // the engine always re-checks the pushed bbox: sources may return a superset
            if (opt.Bbox is { } pb && opt.BboxResidual) f = f.Filter(EnvelopeMask(f, pb));
            bool exhausted = false;
            foreach (var node in _plan)
            {
                if (f.RowCount == 0) break;
                switch (node)
                {
                    case BboxNode b: f = f.Filter(EnvelopeMask(f, b.Box)); break;
                    case SpatialNode s: f = f.Filter(SpatialMask(f, s)); break;
                    case AttributeNode a: f = f.Filter(AttributeMask(f, a.Predicate)); break;
                    case RowNode r: f = f.Filter(r.Mask(f)); break;
                    case ProjectNode p: f = f.Select(p.Columns.Where(c => c != f.GeometryColumnName).ToArray()); break;
                    case MapNode m: f = m.Apply(f); break;
                    case CrsNode c: f = f.ToCrs(c.Target); break;
                    case LimitNode l:
                        long left = limits[l];
                        if (f.RowCount > left) f = f.Head((int)left);
                        limits[l] = left - f.RowCount;
                        if (limits[l] == 0) exhausted = true;
                        break;
                }
            }
            if (f.RowCount > 0) yield return f;
            if (exhausted) yield break;
        }
    }

    private GeoDataFrame EmptyResult()
    {
        // nothing matched: an empty frame with the (untyped) result schema
        var cols = (Optimize().Columns ?? _source.ColumnNames).Select(n => (Column)new Column<string?>(n, [])).ToList();
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, [], CurrentCrs()));
        var g = new GeoDataFrame(cols);
        foreach (var node in _plan)
            if (node is ProjectNode p) g = g.Select(p.Columns.Where(g.HasColumn).ToArray());
        return g;
    }

    private CoordinateReferenceSystem? CurrentCrs()
    {
        var crs = _source.Crs;
        foreach (var n in _plan) if (n is CrsNode c) crs = c.Target;
        return crs;
    }

    // ------------------------------------------------------------------ optimiser

    private sealed record Optimized(Envelope? Bbox, bool BboxResidual, IReadOnlyList<string>? Columns, long? Limit, IReadOnlyList<AttributePredicate> Predicates, List<string> Notes);

    private Optimized Optimize()
    {
        var caps = _source.Pushdown;
        var notes = new List<string>();
        Envelope? bbox = null;
        var preds = new List<AttributePredicate>();
        CoordinateReferenceSystem? crs = _source.Crs;
        bool exactSoFar = true; // every filter so far is evaluated exactly by the source
        long? limit = null;
        bool barrier = false;    // a limit stops filters from being reordered before it
        bool coarse = false;     // a pushed box is only an approximation (transformed CRS or a predicate's envelope)

        foreach (var node in _plan)
        {
            switch (node)
            {
                case BboxNode or SpatialNode when !barrier:
                {
                    Envelope? env = node is BboxNode b ? b.Box
                        : node is SpatialNode s && s.Predicate != SpatialPredicate.Disjoint ? s.Other.EnvelopeInternal : null;
                    if (env is null) { exactSoFar = false; break; }
                    if (crs != _source.Crs && crs is not null && _source.Crs is not null)
                        env = CoordinateTransformer.Create(crs, _source.Crs).Transform(env);
                    bbox = bbox is null ? env : bbox.Intersection(env);
                    bool exact = node is BboxNode && crs == _source.Crs && (caps & ScanPushdown.ExactBbox) != 0;
                    if (!exact) exactSoFar = false;
                    if (node is SpatialNode || crs != _source.Crs) coarse = true;
                    break;
                }
                case AttributeNode a when !barrier:
                    if ((caps & ScanPushdown.Attributes) != 0 && _source.ColumnNames.Contains(a.Predicate.Column) && !IsMappedColumn(a.Predicate.Column, node))
                    {
                        preds.Add(a.Predicate);
                        if ((caps & ScanPushdown.ExactAttributes) == 0) exactSoFar = false;
                    }
                    else exactSoFar = false;
                    break;
                case RowNode or SpatialNode or BboxNode or AttributeNode:
                    exactSoFar = false;
                    break;
                case CrsNode c:
                    crs = c.Target;
                    break;
                case LimitNode l:
                    if (!barrier && exactSoFar && (caps & ScanPushdown.Limit) != 0) limit = limit is null ? l.N : Math.Min(limit.Value, l.N);
                    barrier = true;
                    break;
            }
        }
        if (bbox is not null && (caps & ScanPushdown.Bbox) == 0) { notes.Add("source cannot filter by bbox: filtered after reading"); bbox = null; }
        if (preds.Count > 0 && (caps & ScanPushdown.Attributes) == 0) preds.Clear();

        IReadOnlyList<string>? columns = null;
        if ((caps & ScanPushdown.Columns) != 0) columns = RequiredColumns();
        bool residual = bbox is not null && (coarse || (caps & ScanPushdown.ExactBbox) == 0);
        return new Optimized(bbox, residual, columns, limit, preds, notes);
    }

    private bool IsMappedColumn(string column, PlanNode before)
    {
        foreach (var n in _plan)
        {
            if (ReferenceEquals(n, before)) return false;
            if (n is MapNode m && m.Name == column) return true;
        }
        return false;
    }

    /// <summary>Backward pass: which source columns does the plan need? null = all.</summary>
    private IReadOnlyList<string>? RequiredColumns()
    {
        HashSet<string>? needed = null; // null = all
        for (int i = _plan.Length - 1; i >= 0; i--)
        {
            switch (_plan[i])
            {
                case ProjectNode p: needed = [.. p.Columns]; break;
                case AttributeNode a: needed?.Add(a.Predicate.Column); break;
                case RowNode r: if (r.Columns is null) needed = null; else needed?.UnionWith(r.Columns); break;
                case MapNode m:
                    if (m.Columns is null) needed = null;
                    else if (needed is not null) { needed.Remove(m.Name); needed.UnionWith(m.Columns); }
                    break;
            }
        }
        if (needed is null) return null;
        return _source.ColumnNames.Where(needed.Contains).ToArray();
    }

    /// <summary>The optimised physical plan, e.g. for logging or teaching.</summary>
    public string Explain()
    {
        var o = Optimize();
        var sb = new StringBuilder();
        sb.AppendLine($"SCAN {_source.Name}");
        sb.AppendLine($"  pushdown  bbox: {(o.Bbox is { } b ? Fmt(b) + (o.BboxResidual ? " (coarse, re-checked)" : " (exact)") : "-")}");
        sb.AppendLine($"            columns: {(o.Columns is null ? "all" : "[" + string.Join(", ", o.Columns) + "]")}");
        sb.AppendLine($"            where: {(o.Predicates.Count == 0 ? "-" : string.Join(" AND ", o.Predicates))}");
        sb.AppendLine($"            limit: {(o.Limit?.ToString(CultureInfo.InvariantCulture) ?? "-")}");
        foreach (var n in o.Notes) sb.AppendLine($"  note: {n}");
        foreach (var node in _plan) sb.AppendLine("  " + node.Describe());
        return sb.ToString();
    }

    public override string ToString() => Explain();

    private static string Fmt(Envelope e) => string.Create(CultureInfo.InvariantCulture, $"[{e.MinX:G8}, {e.MinY:G8}, {e.MaxX:G8}, {e.MaxY:G8}]");

    // ------------------------------------------------------------------ masks

    private static bool[] EnvelopeMask(GeoDataFrame f, Envelope box)
    {
        var g = f.Geometry; var m = new bool[f.RowCount];
        for (int i = 0; i < m.Length; i++) m[i] = g[i] is { } geom && geom.EnvelopeInternal.Intersects(box);
        return m;
    }

    private static bool[] SpatialMask(GeoDataFrame f, SpatialNode s)
    {
        var g = f.Geometry; var m = new bool[f.RowCount];
        var inv = s.Predicate.Inverse();
        for (int i = 0; i < m.Length; i++) m[i] = g[i] is { } geom && inv.Evaluate(s.Prepared, geom);
        return m;
    }

    private static bool[] AttributeMask(GeoDataFrame f, AttributePredicate p)
    {
        var c = f[p.Column]; var m = new bool[f.RowCount];
        for (int i = 0; i < m.Length; i++) m[i] = p.Matches(c.GetValue(i));
        return m;
    }

    // ------------------------------------------------------------------ plan nodes

    private abstract record PlanNode { public abstract string Describe(); }
    private sealed record BboxNode(Envelope Box) : PlanNode { public override string Describe() => $"FILTER bbox {Fmt(Box)}"; }
    private sealed record SpatialNode(SpatialPredicate Predicate, NtsGeometry Other) : PlanNode
    {
        public IPreparedGeometry Prepared { get; } = PreparedGeometryFactory.Prepare(Other);
        public override string Describe() => $"FILTER {Predicate.ToString().ToLowerInvariant()}({Other.GeometryType})";
    }
    private sealed record AttributeNode(AttributePredicate Predicate) : PlanNode { public override string Describe() => $"FILTER {Predicate}"; }
    private sealed record RowNode(string Description, string[]? Columns, Func<GeoDataFrame, bool[]> Mask) : PlanNode { public override string Describe() => $"FILTER {Description}"; }
    private sealed record ProjectNode(string[] Columns) : PlanNode { public override string Describe() => $"SELECT {string.Join(", ", Columns)}"; }
    private sealed record MapNode(string Name, string[]? Columns, Func<GeoDataFrame, GeoDataFrame> Apply) : PlanNode { public override string Describe() => $"WITH COLUMN {Name}"; }
    private sealed record CrsNode(CoordinateReferenceSystem Target) : PlanNode { public override string Describe() => $"TO CRS {Target}"; }
    private sealed record LimitNode(long N) : PlanNode { public override string Describe() => $"LIMIT {N}"; }
}

public static class LazyExtensions
{
    /// <summary>Starts a lazy query over an in-memory frame.</summary>
    public static LazyGeoDataFrame Lazy(this GeoDataFrame frame) => LazyGeoDataFrame.From(new InMemoryScanSource(frame));
}
