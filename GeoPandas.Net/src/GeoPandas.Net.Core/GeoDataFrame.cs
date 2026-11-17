using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net;

/// <summary>
/// A <see cref="DataFrame"/> with an active geometry column (<see cref="GeoSeries"/>) and a CRS —
/// the central type of GeoPandas.Net, API-compatible in spirit with Python GeoPandas.
/// </summary>
/// <example>
/// <code>
/// var gdf = GeoDataFrame.FromGeometries(points, CoordinateReferenceSystem.Wgs84, Column.Create("name", names));
/// var joined = gdf.ToCrs(32748).SJoin(districts.ToCrs(32748), SpatialPredicate.Within);
/// </code>
/// </example>
public sealed partial class GeoDataFrame : DataFrame
{
    public const string DefaultGeometryColumn = "geometry";

    public GeoDataFrame(IEnumerable<Column> columns, string geometryColumn = DefaultGeometryColumn, CoordinateReferenceSystem? crs = null)
        : base(EnsureGeo(columns, geometryColumn, crs))
    {
        GeometryColumnName = geometryColumn;
    }

    private static IEnumerable<Column> EnsureGeo(IEnumerable<Column> columns, string geometryColumn, CoordinateReferenceSystem? crs)
    {
        bool found = false;
        foreach (var c in columns)
        {
            if (c.Name == geometryColumn)
            {
                found = true;
                var gs = c as GeoSeries ?? throw new ArgumentException($"Column '{geometryColumn}' must be a GeoSeries.");
                yield return crs is not null && gs.Crs != crs ? gs.SetCrs(crs) : gs;
            }
            else yield return c;
        }
        if (!found) throw new ArgumentException($"Geometry column '{geometryColumn}' not found.");
    }

    /// <summary>Builds a frame from geometries plus attribute columns.</summary>
    public static GeoDataFrame FromGeometries(IEnumerable<NtsGeometry?> geometries, CoordinateReferenceSystem? crs = null, params Column[] attributes)
    {
        var geo = new GeoSeries(DefaultGeometryColumn, geometries.ToArray(), crs);
        return new GeoDataFrame([.. attributes, geo]);
    }

    /// <summary>Promotes a DataFrame with x/y columns into point geometries (GeoPandas <c>points_from_xy</c>).</summary>
    public static GeoDataFrame FromXY(DataFrame df, string xColumn, string yColumn, CoordinateReferenceSystem? crs = null, bool dropXY = false)
    {
        var geo = GeoSeries.PointsFromXY(df[xColumn].ToDoubleArray(), df[yColumn].ToDoubleArray(), crs);
        var cols = df.Columns.Where(c => !dropXY || (c.Name != xColumn && c.Name != yColumn)).ToList();
        cols.Add(geo);
        return new GeoDataFrame(cols);
    }

    /// <summary>Promotes a DataFrame with a WKT text column into geometries.</summary>
    public static GeoDataFrame FromWkt(DataFrame df, string wktColumn, CoordinateReferenceSystem? crs = null)
    {
        var c = df[wktColumn];
        var geo = GeoSeries.FromWkt(Enumerable.Range(0, df.RowCount).Select(i => c.GetValue(i)?.ToString()), crs);
        var cols = df.Columns.Where(x => x.Name != wktColumn).Append(geo);
        return new GeoDataFrame(cols);
    }

    public string GeometryColumnName { get; }
    public GeoSeries Geometry => (GeoSeries)this[GeometryColumnName];
    public CoordinateReferenceSystem? Crs => Geometry.Crs;
    /// <summary>Spatial index of the active geometry column.</summary>
    public StrTree<int> SIndex => Geometry.SIndex;
    public Envelope TotalBounds => Geometry.TotalBounds;
    public bool IsGeographic => Crs?.IsGeographic ?? false;

    protected override DataFrame CreateLike(IReadOnlyList<Column> columns) =>
        columns.Any(c => c.Name == GeometryColumnName && c is GeoSeries)
            ? new GeoDataFrame(columns, GeometryColumnName)
            : new DataFrame(columns);

    /// <summary>Plain attribute table without geometry.</summary>
    public DataFrame Attributes() => new(Columns.Where(c => c.Name != GeometryColumnName));

    // ------------------------------------------------------------------ typed wrappers

    public new GeoDataFrame Head(int n = 5) => (GeoDataFrame)base.Head(n);
    public new GeoDataFrame Tail(int n = 5) => (GeoDataFrame)base.Tail(n);
    public new GeoDataFrame Take(ReadOnlySpan<int> rows) => (GeoDataFrame)base.Take(rows);
    public new GeoDataFrame Filter(bool[] mask) => (GeoDataFrame)base.Filter(mask);
    public new GeoDataFrame Filter(Func<DataRow, bool> predicate) => (GeoDataFrame)base.Filter(predicate);
    public new GeoDataFrame Filter<T>(string column, Func<T, bool> predicate) => (GeoDataFrame)base.Filter(column, predicate);
    public new GeoDataFrame SortBy(string column, bool ascending = true) => (GeoDataFrame)base.SortBy(column, ascending);
    public new GeoDataFrame SortBy(string[] columns, bool[]? ascending = null) => (GeoDataFrame)base.SortBy(columns, ascending);
    public new GeoDataFrame Sample(int n, int? seed = null) => (GeoDataFrame)base.Sample(n, seed);
    public new GeoDataFrame Rename(string from, string to) =>
        from == GeometryColumnName ? new GeoDataFrame(Columns.Select(c => c.Name == from ? c.Rename(to) : c), to) : (GeoDataFrame)base.Rename(from, to);

    /// <summary>Adds or replaces a column, keeping the frame a GeoDataFrame.</summary>
    public new GeoDataFrame WithColumn(Column column) => (GeoDataFrame)base.WithColumn(column);
    public new GeoDataFrame WithColumn<T>(string name, Func<DataRow, T> compute) => (GeoDataFrame)base.WithColumn(name, compute);

    /// <summary>Selects columns; the geometry column is always kept.</summary>
    public new GeoDataFrame Select(params string[] columns) =>
        (GeoDataFrame)base.Select(columns.Contains(GeometryColumnName) ? columns : [.. columns, GeometryColumnName]);

    public new GeoDataFrame Drop(params string[] columns)
    {
        if (columns.Contains(GeometryColumnName)) throw new InvalidOperationException("Use Attributes() to drop the active geometry column.");
        return (GeoDataFrame)base.Drop(columns);
    }

    /// <summary>Makes another GeoSeries column the active geometry.</summary>
    public GeoDataFrame SetGeometry(string column) => new(Columns, column);

    /// <summary>Replaces the active geometry with a new series (same length).</summary>
    public GeoDataFrame WithGeometry(GeoSeries geometry) =>
        new(Columns.Select(c => c.Name == GeometryColumnName ? geometry.Rename(GeometryColumnName) : c), GeometryColumnName);

    // ------------------------------------------------------------------ CRS

    public GeoDataFrame SetCrs(CoordinateReferenceSystem? crs) => WithGeometry(Geometry.SetCrs(crs));
    public GeoDataFrame SetCrs(int epsg) => SetCrs(CoordinateReferenceSystem.FromEpsg(epsg));
    public GeoDataFrame SetCrs(string crs) => SetCrs(CoordinateReferenceSystem.Parse(crs));
    public GeoDataFrame ToCrs(CoordinateReferenceSystem crs) => WithGeometry(Geometry.ToCrs(crs));
    public GeoDataFrame ToCrs(int epsg) => ToCrs(CoordinateReferenceSystem.FromEpsg(epsg));
    public GeoDataFrame ToCrs(string crs) => ToCrs(CoordinateReferenceSystem.Parse(crs));
    public CoordinateReferenceSystem EstimateUtmCrs() => Geometry.EstimateUtmCrs();

    // ------------------------------------------------------------------ geometry shortcuts

    public Column<double> Area() => Geometry.Area();
    public Column<double> GeomLength() => Geometry.GeomLength();
    public GeoDataFrame Centroid() => WithGeometry(Geometry.Centroid());
    public GeoDataFrame RepresentativePoint() => WithGeometry(Geometry.RepresentativePoint());
    public GeoDataFrame Buffer(double distance, int quadrantSegments = 8) => WithGeometry(Geometry.Buffer(distance, quadrantSegments));
    public GeoDataFrame Simplify(double tolerance, bool preserveTopology = true) => WithGeometry(Geometry.Simplify(tolerance, preserveTopology));
    public GeoDataFrame ConvexHull() => WithGeometry(Geometry.ConvexHull());
    public GeoDataFrame Envelope() => WithGeometry(Geometry.Envelope());
    public GeoDataFrame MakeValid() => WithGeometry(Geometry.MakeValid());
    public NtsGeometry UnionAll() => Geometry.UnionAll();

    /// <summary>Rows whose geometry intersects the bounding box (GeoPandas <c>cx</c>).</summary>
    public GeoDataFrame Cx(double minX, double minY, double maxX, double maxY)
    {
        var box = Geo.Box(minX, minY, maxX, maxY);
        var hits = SIndex.Query(box.EnvelopeInternal).Where(i => Geometry[i]!.Intersects(box)).Order().ToArray();
        return Take(hits);
    }

    /// <summary>Rows satisfying <c>geometry.predicate(other)</c>.</summary>
    public GeoDataFrame Query(SpatialPredicate predicate, NtsGeometry other) => Filter(Geometry.Predicate(predicate, other));

    /// <summary>Starts a map of this frame (GeoPandas <c>plot</c>). Chain more layers on the returned scene.</summary>
    public Plotting.MapScene Plot(Plotting.LayerStyle? style = null, string? title = null)
    {
        var scene = new Plotting.MapScene { Title = title };
        return scene.AddLayer(this, style);
    }

    public override string ToString() => $"GeoDataFrame crs={Crs?.ToString() ?? "None"}\n" + base.ToString();
}
