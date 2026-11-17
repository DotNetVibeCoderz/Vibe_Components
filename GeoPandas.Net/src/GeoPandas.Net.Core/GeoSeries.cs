using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net;

/// <summary>
/// A geometry column with a CRS (GeoPandas <c>GeoSeries</c>). Element-wise operations run in parallel for
/// large inputs and return new series; nulls propagate.
/// </summary>
public sealed class GeoSeries : Column<NtsGeometry?>
{
    private const int ParallelThreshold = 2048;
    private StrTree<int>? _sindex;

    public GeoSeries(string name, NtsGeometry?[] geometries, CoordinateReferenceSystem? crs = null) : base(name, geometries)
    {
        Crs = crs;
    }

    public GeoSeries(IEnumerable<NtsGeometry?> geometries, CoordinateReferenceSystem? crs = null, string name = "geometry")
        : this(name, geometries.ToArray(), crs) { }

    public CoordinateReferenceSystem? Crs { get; }

    protected override Column<NtsGeometry?> CreateNew(string name, NtsGeometry?[] values, ulong[]? validity) => new GeoSeries(name, values, Crs);

    /// <summary>Spatial index over the series (built lazily, thread-safe).</summary>
    public StrTree<int> SIndex => LazyInitializer.EnsureInitialized(ref _sindex, () => StrTree<int>.FromGeometries(_values));

    /// <summary>Same geometries with a CRS assigned (no reprojection).</summary>
    public GeoSeries SetCrs(CoordinateReferenceSystem? crs) => new(Name, _values, crs);
    public GeoSeries SetCrs(string crs) => SetCrs(CoordinateReferenceSystem.Parse(crs));
    public GeoSeries SetCrs(int epsg) => SetCrs(CoordinateReferenceSystem.FromEpsg(epsg));

    /// <summary>Reprojects geometries to <paramref name="target"/>.</summary>
    public GeoSeries ToCrs(CoordinateReferenceSystem target)
    {
        if (Crs is null) throw new InvalidOperationException("Cannot transform naive geometries. Please set a CRS on the object first (SetCrs).");
        var t = CoordinateTransformer.Create(Crs, target);
        if (t.IsIdentity) return SetCrs(target);
        return new GeoSeries(Name, MapGeom(g => t.Transform(g)), target);
    }

    public GeoSeries ToCrs(int epsg) => ToCrs(CoordinateReferenceSystem.FromEpsg(epsg));
    public GeoSeries ToCrs(string crs) => ToCrs(CoordinateReferenceSystem.Parse(crs));

    /// <summary>Suggests a UTM CRS from the centre of the data (data must have a CRS).</summary>
    public CoordinateReferenceSystem EstimateUtmCrs()
    {
        if (Crs is null) throw new InvalidOperationException("CRS is not set.");
        var env = TotalBounds;
        var t = CoordinateTransformer.Create(Crs, CoordinateReferenceSystem.Wgs84);
        var (lon, lat) = t.Transform(env.Centre.X, env.Centre.Y);
        return CoordinateReferenceSystem.EstimateUtm(lon, lat);
    }

    // ------------------------------------------------------------------ internal helpers

    private NtsGeometry?[] MapGeom(Func<NtsGeometry, NtsGeometry?> f)
    {
        var res = new NtsGeometry?[Length];
        if (Length >= ParallelThreshold)
            Parallel.For(0, Length, i => res[i] = _values[i] is { } g ? f(g) : null);
        else
            for (int i = 0; i < Length; i++) res[i] = _values[i] is { } g ? f(g) : null;
        return res;
    }

    private double[] MapDouble(Func<NtsGeometry, double> f)
    {
        var res = new double[Length];
        if (Length >= ParallelThreshold)
            Parallel.For(0, Length, i => res[i] = _values[i] is { } g ? f(g) : double.NaN);
        else
            for (int i = 0; i < Length; i++) res[i] = _values[i] is { } g ? f(g) : double.NaN;
        return res;
    }

    private bool[] MapBool(Func<NtsGeometry, bool> f)
    {
        var res = new bool[Length];
        if (Length >= ParallelThreshold)
            Parallel.For(0, Length, i => res[i] = _values[i] is { } g && f(g));
        else
            for (int i = 0; i < Length; i++) res[i] = _values[i] is { } g && f(g);
        return res;
    }

    private GeoSeries With(NtsGeometry?[] geoms) => new(Name, geoms, Crs);

    private void WarnIfGeographic(string op)
    {
        if (Crs?.IsGeographic == true)
            System.Diagnostics.Trace.TraceWarning($"GeoPandas.Net: '{op}' on a geographic CRS ({Crs}) returns results in degrees. Use ToCrs(...) to a projected CRS, or the Geodesic* methods.");
    }

    // ------------------------------------------------------------------ measures

    /// <summary>Planar area in CRS units² (see <see cref="GeodesicArea"/> for lon/lat).</summary>
    public Column<double> Area() { WarnIfGeographic("Area"); return new("area", MapDouble(g => g.Area)); }
    /// <summary>Planar length / perimeter in CRS units.</summary>
    public Column<double> GeomLength() { WarnIfGeographic("Length"); return new("length", MapDouble(g => g.Length)); }
    /// <summary>Geodesic area in m² (requires a geographic CRS or will reproject to WGS 84).</summary>
    public Column<double> GeodesicArea() => new("geodesic_area", ToGeographic().MapDouble(Geodesy.Area));
    /// <summary>Geodesic length in metres.</summary>
    public Column<double> GeodesicLength() => new("geodesic_length", ToGeographic().MapDouble(Geodesy.Length));

    private GeoSeries ToGeographic() => Crs is null || Crs.IsGeographic ? this : ToCrs(CoordinateReferenceSystem.Wgs84);

    public Column<double> X => new("x", MapDouble(g => g is Point p ? p.X : double.NaN));
    public Column<double> Y => new("y", MapDouble(g => g is Point p ? p.Y : double.NaN));
    public Column<double> Z => new("z", MapDouble(g => g is Point p ? p.Z : double.NaN));
    public Column<string?> GeomType => new("geom_type", _values.Select(g => g?.GeometryType).ToArray());
    public bool[] IsValid => MapBool(g => g.IsValid);
    public bool[] IsEmpty => _values.Select(g => g is null || g.IsEmpty).ToArray();
    public bool[] IsSimple => MapBool(g => g.IsSimple);
    public bool[] HasZ => MapBool(Geo.HasZ);

    /// <summary>Envelope of all geometries.</summary>
    public Envelope TotalBounds
    {
        get
        {
            var env = new Envelope();
            foreach (var g in _values) if (g is { IsEmpty: false }) env.ExpandToInclude(g.EnvelopeInternal);
            return env;
        }
    }

    /// <summary>minx / miny / maxx / maxy per geometry.</summary>
    public DataFrame Bounds()
    {
        var e = _values.Select(g => g is { IsEmpty: false } ? g.EnvelopeInternal : null).ToArray();
        return new DataFrame(
            Column.Create("minx", e.Select(x => x?.MinX ?? double.NaN).ToArray()),
            Column.Create("miny", e.Select(x => x?.MinY ?? double.NaN).ToArray()),
            Column.Create("maxx", e.Select(x => x?.MaxX ?? double.NaN).ToArray()),
            Column.Create("maxy", e.Select(x => x?.MaxY ?? double.NaN).ToArray()));
    }

    // ------------------------------------------------------------------ constructive

    public GeoSeries Centroid() => With(MapGeom(g => g.Centroid));
    /// <summary>A point guaranteed to lie inside the geometry.</summary>
    public GeoSeries RepresentativePoint() => With(MapGeom(g => g.InteriorPoint));
    public GeoSeries Buffer(double distance, int quadrantSegments = 8) => With(MapGeom(g => g.Buffer(distance, quadrantSegments)));
    public GeoSeries ConvexHull() => With(MapGeom(g => g.ConvexHull()));
    public GeoSeries Envelope() => With(MapGeom(g => g.Envelope));
    public GeoSeries Boundary() => With(MapGeom(g => g.Boundary));
    public GeoSeries MakeValid() => With(MapGeom(GeometryOps.MakeValid));
    public GeoSeries Normalize() => With(MapGeom(g => { var c = g.Copy(); c.Normalize(); return c; }));
    public GeoSeries Reverse() => With(MapGeom(g => g.Reverse()));

    /// <summary>Douglas–Peucker (or topology-preserving) simplification.</summary>
    public GeoSeries Simplify(double tolerance, bool preserveTopology = true) =>
        With(MapGeom(g => preserveTopology ? TopologyPreservingSimplifier.Simplify(g, tolerance) : DouglasPeuckerSimplifier.Simplify(g, tolerance)));

    public GeoSeries Translate(double dx, double dy) => With(MapGeom(g => GeometryOps.Affine(g, dx, dy)));
    public GeoSeries Rotate(double degrees, Coordinate? origin = null) => With(MapGeom(g => GeometryOps.Affine(g, rotateDeg: degrees, origin: origin)));
    public GeoSeries Scale(double sx, double sy, Coordinate? origin = null) => With(MapGeom(g => GeometryOps.Affine(g, scaleX: sx, scaleY: sy, origin: origin)));

    /// <summary>Point at a distance along each line (or a fraction when <paramref name="normalized"/>).</summary>
    public GeoSeries Interpolate(double distance, bool normalized = false) => With(MapGeom(g =>
    {
        if (g is not LineString ls) return null;
        double d = normalized ? distance * ls.Length : distance;
        var c = new NetTopologySuite.LinearReferencing.LengthIndexedLine(ls).ExtractPoint(d);
        return Geo.Factory.CreatePoint(c);
    }));

    // ------------------------------------------------------------------ binary with a single geometry

    public GeoSeries Intersection(NtsGeometry other) => With(MapGeom(g => GeometryOps.Intersection(g, other)));
    public GeoSeries Union(NtsGeometry other) => With(MapGeom(g => GeometryOps.Union(g, other)));
    public GeoSeries Difference(NtsGeometry other) => With(MapGeom(g => GeometryOps.Difference(g, other)));
    public GeoSeries SymmetricDifference(NtsGeometry other) => With(MapGeom(g => GeometryOps.SymmetricDifference(g, other)));
    public Column<double> Distance(NtsGeometry other) => new("distance", MapDouble(g => g.Distance(other)));

    public bool[] Intersects(NtsGeometry other) => Predicate(SpatialPredicate.Intersects, other);
    public bool[] Contains(NtsGeometry other) => Predicate(SpatialPredicate.Contains, other);
    public bool[] Within(NtsGeometry other) => Predicate(SpatialPredicate.Within, other);
    public bool[] Touches(NtsGeometry other) => Predicate(SpatialPredicate.Touches, other);
    public bool[] Crosses(NtsGeometry other) => Predicate(SpatialPredicate.Crosses, other);
    public bool[] Overlaps(NtsGeometry other) => Predicate(SpatialPredicate.Overlaps, other);
    public bool[] Covers(NtsGeometry other) => Predicate(SpatialPredicate.Covers, other);
    public bool[] CoveredBy(NtsGeometry other) => Predicate(SpatialPredicate.CoveredBy, other);
    public bool[] DWithin(NtsGeometry other, double distance) => MapBool(g => g.IsWithinDistance(other, distance));

    /// <summary>Evaluates <c>this[i].predicate(other)</c>, using the spatial index and a prepared geometry.</summary>
    public bool[] Predicate(SpatialPredicate predicate, NtsGeometry other)
    {
        var res = new bool[Length];
        if (predicate == SpatialPredicate.Disjoint)
        {
            var inter = Predicate(SpatialPredicate.Intersects, other);
            for (int i = 0; i < res.Length; i++) res[i] = _values[i] is not null && !inter[i];
            return res;
        }
        var prepared = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(other);
        var inverse = predicate.Inverse();
        foreach (int i in SIndex.Query(other.EnvelopeInternal))
            res[i] = inverse.Evaluate(prepared, _values[i]!);
        return res;
    }

    // ------------------------------------------------------------------ binary element-wise (aligned series)

    public GeoSeries Intersection(GeoSeries other) => Pairwise(other, GeometryOps.Intersection);
    public GeoSeries Union(GeoSeries other) => Pairwise(other, GeometryOps.Union);
    public GeoSeries Difference(GeoSeries other) => Pairwise(other, GeometryOps.Difference);
    public Column<double> Distance(GeoSeries other)
    {
        CheckAligned(other);
        return new("distance", Enumerable.Range(0, Length).Select(i => _values[i] is { } a && other[i] is { } b ? a.Distance(b) : double.NaN).ToArray());
    }

    private GeoSeries Pairwise(GeoSeries other, Func<NtsGeometry, NtsGeometry, NtsGeometry> op)
    {
        CheckAligned(other);
        var res = new NtsGeometry?[Length];
        Parallel.For(0, Length, i => res[i] = _values[i] is { } a && other[i] is { } b ? op(a, b) : null);
        return With(res);
    }

    private void CheckAligned(GeoSeries other)
    {
        if (other.Length != Length) throw new ArgumentException("Series must be aligned (same length).");
        if (Crs is not null && other.Crs is not null && Crs != other.Crs)
            throw new InvalidOperationException($"CRS mismatch: {Crs} vs {other.Crs}. Use ToCrs to align.");
    }

    // ------------------------------------------------------------------ aggregate

    /// <summary>Union of all geometries (GeoPandas <c>union_all</c>).</summary>
    public NtsGeometry UnionAll() => GeometryOps.UnaryUnion(_values);

    public string[] ToWkt() => _values.Select(g => g?.AsText() ?? "").ToArray();

    public static GeoSeries FromWkt(IEnumerable<string?> wkt, CoordinateReferenceSystem? crs = null, string name = "geometry") =>
        new(name, wkt.Select(w => string.IsNullOrWhiteSpace(w) ? null : Geo.FromWkt(w)).ToArray(), crs);

    /// <summary>Point series from x/y arrays (GeoPandas <c>points_from_xy</c>).</summary>
    public static GeoSeries PointsFromXY(ReadOnlySpan<double> x, ReadOnlySpan<double> y, CoordinateReferenceSystem? crs = null, string name = "geometry")
    {
        if (x.Length != y.Length) throw new ArgumentException("x and y must have the same length.");
        var g = new NtsGeometry?[x.Length];
        for (int i = 0; i < g.Length; i++) g[i] = double.IsNaN(x[i]) || double.IsNaN(y[i]) ? null : Geo.Point(x[i], y[i]);
        return new GeoSeries(name, g, crs);
    }

    public override string ToString() => $"GeoSeries '{Name}' [{Length}] crs={Crs?.ToString() ?? "None"}";
}
