using System.Text;
using GDAL.Net.Native;
using GDAL.Net.Srs;

namespace GDAL.Net.Vector;

/// <summary>
/// An OGR geometry (points, lines, polygons, multi-geometries, curves). Spatial predicates and operations
/// (buffer, union, intersection, convex hull, simplify, ...) are powered by GEOS.
/// Geometries obtained from a feature are borrowed: clone them to keep them beyond the feature's lifetime.
/// </summary>
public sealed unsafe class Geometry : GdalObject
{
    internal Geometry(GdalSafeHandle handle) : base(handle) { }

    internal static Geometry Owned(nint handle) => new(new GdalSafeHandle(handle, &Release));

    internal static Geometry? OwnedOrNull(nint handle) => handle == 0 ? null : Owned(handle);

    private static Geometry OwnedOrThrow(nint handle, string operation) => handle == 0 ? throw GdalNative.LastError(operation) : Owned(handle);

    private static void Release(nint h) => GdalNative.OGR_G_DestroyGeometry(h);

    /// <summary>True when this geometry is owned by a feature or a parent geometry.</summary>
    public bool IsBorrowed => !RawHandle.IsOwned;

    // ------------------------------------------------------------------ factories

    /// <summary>Creates an empty geometry of the given type.</summary>
    public static Geometry Create(GeometryType type) => OwnedOrThrow(GdalNative.OGR_G_CreateGeometry((uint)type), $"Create {type}");

    /// <summary>Parses WKT (ISO or OGC).</summary>
    public static Geometry FromWkt(string wkt, SpatialReference? srs = null)
    {
        var bytes = Utf8.Encode(wkt);
        nint geom;
        fixed (byte* p = bytes)
        {
            var ptr = p;
            GdalNative.CheckOgr(GdalNative.OGR_G_CreateFromWkt(&ptr, srs is null ? 0 : srs.Handle.DangerousGetHandle(), &geom), "Parse WKT");
        }
        GC.KeepAlive(srs);
        return Owned(geom);
    }

    /// <summary>Parses WKB (ISO or extended).</summary>
    public static Geometry FromWkb(ReadOnlySpan<byte> wkb, SpatialReference? srs = null)
    {
        nint geom;
        fixed (byte* p = wkb)
        {
            GdalNative.CheckOgr(GdalNative.OGR_G_CreateFromWkb(p, srs is null ? 0 : srs.Handle.DangerousGetHandle(), &geom, wkb.Length), "Parse WKB");
        }
        GC.KeepAlive(srs);
        return Owned(geom);
    }

    /// <summary>Parses a GeoJSON geometry object.</summary>
    public static Geometry FromGeoJson(string json) => OwnedOrThrow(GdalNative.OGR_G_CreateGeometryFromJson(json), "Parse GeoJSON");

    /// <summary>Parses a GML geometry fragment.</summary>
    public static Geometry FromGml(string gml) => OwnedOrThrow(GdalNative.OGR_G_CreateFromGML(gml), "Parse GML");

    public static Geometry Point(double x, double y)
    {
        var g = Create(GeometryType.Point);
        GdalNative.OGR_G_AddPoint_2D(g.Handle, x, y);
        return g;
    }

    public static Geometry Point(double x, double y, double z)
    {
        var g = Create(GeometryType.PointZ);
        GdalNative.OGR_G_AddPoint(g.Handle, x, y, z);
        return g;
    }

    public static Geometry LineString(params ReadOnlySpan<(double X, double Y)> points)
    {
        var g = Create(GeometryType.LineString);
        g.AddPoints(points);
        return g;
    }

    /// <summary>Creates a polygon from an exterior ring (closed automatically) and optional holes.</summary>
    public static Geometry Polygon(ReadOnlySpan<(double X, double Y)> exterior, params (double X, double Y)[][] holes)
    {
        var poly = Create(GeometryType.Polygon);
        using (var ring = Create(GeometryType.LinearRing))
        {
            ring.AddPoints(exterior);
            GdalNative.OGR_G_CloseRings(ring.Handle);
            poly.AddGeometry(ring);
        }
        foreach (var hole in holes)
        {
            using var ring = Create(GeometryType.LinearRing);
            ring.AddPoints(hole);
            GdalNative.OGR_G_CloseRings(ring.Handle);
            poly.AddGeometry(ring);
        }
        return poly;
    }

    /// <summary>Rectangle polygon from an envelope.</summary>
    public static Geometry FromEnvelope(Envelope e)
        => Polygon([(e.MinX, e.MinY), (e.MaxX, e.MinY), (e.MaxX, e.MaxY), (e.MinX, e.MaxY), (e.MinX, e.MinY)]);

    // ------------------------------------------------------------------ export

    public string ToWkt()
    {
        nint text;
        GdalNative.CheckOgr(GdalNative.OGR_G_ExportToIsoWkt(Handle, &text), "Export WKT");
        return Interop.TakeString(text) ?? string.Empty;
    }

    public byte[] ToWkb(bool bigEndian = false)
    {
        var size = GdalNative.OGR_G_WkbSize(Handle);
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            GdalNative.CheckOgr(GdalNative.OGR_G_ExportToIsoWkb(Handle, bigEndian ? 0 : 1, p), "Export WKB");
        }
        return buffer;
    }

    /// <summary>GeoJSON geometry. <paramref name="coordinatePrecision"/> limits decimals (-1 = full precision).</summary>
    public string ToGeoJson(int coordinatePrecision = -1)
    {
        if (coordinatePrecision < 0) return Interop.TakeString(GdalNative.OGR_G_ExportToJson(Handle)) ?? string.Empty;
        using var opts = new NativeStringList([$"COORDINATE_PRECISION={coordinatePrecision}"]);
        return Interop.TakeString(GdalNative.OGR_G_ExportToJsonEx(Handle, opts.Pointer)) ?? string.Empty;
    }

    public string ToGml() => Interop.TakeString(GdalNative.OGR_G_ExportToGML(Handle)) ?? string.Empty;

    public string ToKml(string? altitudeMode = null) => Interop.TakeString(GdalNative.OGR_G_ExportToKML(Handle, altitudeMode)) ?? string.Empty;

    public override string ToString() => IsDisposed ? "<disposed geometry>" : ToWkt();

    // ------------------------------------------------------------------ properties

    public GeometryType GeometryType => (GeometryType)GdalNative.OGR_G_GetGeometryType(Handle);

    /// <summary>Type without Z/M flags.</summary>
    public GeometryType FlatType => (GeometryType)GdalNative.OGR_GT_Flatten(GdalNative.OGR_G_GetGeometryType(Handle));

    public string GeometryName => Interop.PtrToString(GdalNative.OGR_G_GetGeometryName(Handle)) ?? string.Empty;

    /// <summary>Topological dimension (0 point, 1 line, 2 surface).</summary>
    public int Dimension => GdalNative.OGR_G_GetDimension(Handle);

    public int CoordinateDimension => GdalNative.OGR_G_CoordinateDimension(Handle);

    public Envelope Envelope
    {
        get
        {
            OGREnvelope e;
            GdalNative.OGR_G_GetEnvelope(Handle, &e);
            return new Envelope(e.MinX, e.MinY, e.MaxX, e.MaxY);
        }
    }

    public bool IsEmpty => GdalNative.OGR_G_IsEmpty(Handle) != 0;
    public bool IsValid => GdalNative.OGR_G_IsValid(Handle) != 0;
    public bool IsSimple => GdalNative.OGR_G_IsSimple(Handle) != 0;

    /// <summary>Area (in CRS units²) of surfaces; 0 for points/lines.</summary>
    public double Area => GdalNative.OGR_G_Area(Handle);

    /// <summary>Length (in CRS units) of curves.</summary>
    public double Length => GdalNative.OGR_G_Length(Handle);

    public int PointCount => GdalNative.OGR_G_GetPointCount(Handle);

    public int GeometryCount => GdalNative.OGR_G_GetGeometryCount(Handle);

    /// <summary>Gets a sub-geometry (ring of a polygon, part of a multi geometry). Borrowed from this geometry.</summary>
    public Geometry GetGeometry(int index)
    {
        var h = GdalNative.OGR_G_GetGeometryRef(Handle, index);
        return h == 0 ? throw new ArgumentOutOfRangeException(nameof(index)) : new Geometry(GdalSafeHandle.Borrowed(h, Handle));
    }

    /// <summary>All sub-geometries (borrowed).</summary>
    public IEnumerable<Geometry> Parts
    {
        get
        {
            var n = GeometryCount;
            for (var i = 0; i < n; i++) yield return GetGeometry(i);
        }
    }

    public (double X, double Y) GetPoint(int index) => (GdalNative.OGR_G_GetX(Handle, index), GdalNative.OGR_G_GetY(Handle, index));

    public (double X, double Y, double Z) GetPoint3D(int index)
        => (GdalNative.OGR_G_GetX(Handle, index), GdalNative.OGR_G_GetY(Handle, index), GdalNative.OGR_G_GetZ(Handle, index));

    public double X => GdalNative.OGR_G_GetX(Handle, 0);
    public double Y => GdalNative.OGR_G_GetY(Handle, 0);
    public double Z => GdalNative.OGR_G_GetZ(Handle, 0);

    /// <summary>Copies all vertices of a simple geometry (point/line/ring) into interleaved XY pairs with one native call.</summary>
    public int CopyPoints(Span<double> xy)
    {
        var n = PointCount;
        if (n == 0) return 0;
        if (xy.Length < n * 2) throw new ArgumentException("Buffer is too small.", nameof(xy));
        fixed (double* p = xy)
        {
            GdalNative.OGR_G_GetPoints(Handle, p, sizeof(double) * 2, p + 1, sizeof(double) * 2, null, 0);
        }
        return n;
    }

    /// <summary>All vertices of a simple geometry.</summary>
    public (double X, double Y)[] GetPoints()
    {
        var n = PointCount;
        var result = new (double X, double Y)[n];
        if (n == 0) return result;
        var xy = n <= 2048 ? stackalloc double[n * 2] : new double[n * 2];
        CopyPoints(xy);
        for (var i = 0; i < n; i++) result[i] = (xy[2 * i], xy[2 * i + 1]);
        return result;
    }

    // ------------------------------------------------------------------ editing

    public void AddPoint(double x, double y) => GdalNative.OGR_G_AddPoint_2D(Handle, x, y);

    public void AddPoint(double x, double y, double z) => GdalNative.OGR_G_AddPoint(Handle, x, y, z);

    public void AddPoints(ReadOnlySpan<(double X, double Y)> points)
    {
        foreach (var (x, y) in points) GdalNative.OGR_G_AddPoint_2D(Handle, x, y);
    }

    /// <summary>Adds a copy of <paramref name="part"/> (ring to polygon, geometry to collection).</summary>
    public void AddGeometry(Geometry part) => GdalNative.CheckOgr(GdalNative.OGR_G_AddGeometry(Handle, part.Handle), "AddGeometry");

    public void CloseRings() => GdalNative.OGR_G_CloseRings(Handle);
    public void FlattenTo2D() => GdalNative.OGR_G_FlattenTo2D(Handle);

    /// <summary>Adds vertices so that no segment is longer than <paramref name="maxLength"/>.</summary>
    public void Segmentize(double maxLength) => GdalNative.OGR_G_Segmentize(Handle, maxLength);

    /// <summary>Approximates curves (arcs) with line segments.</summary>
    public Geometry ToLinear(double maxAngleStepDegrees = 0) => OwnedOrThrow(GdalNative.OGR_G_GetLinearGeometry(Handle, maxAngleStepDegrees, 0), "GetLinearGeometry");

    public Geometry Clone() => OwnedOrThrow(GdalNative.OGR_G_Clone(Handle), "Clone");

    // ------------------------------------------------------------------ SRS & transformation

    /// <summary>CRS assigned to the geometry (a copy), or null.</summary>
    public SpatialReference? SpatialReference
    {
        get
        {
            var h = GdalNative.OGR_G_GetSpatialReference(Handle);
            return h == 0 ? null : SpatialReference.CloneFrom(h);
        }
        set
        {
            GdalNative.OGR_G_AssignSpatialReference(Handle, value is null ? 0 : value.Handle.DangerousGetHandle());
            GC.KeepAlive(value);
        }
    }

    /// <summary>Transforms the geometry in place.</summary>
    public void Transform(CoordinateTransformation transformation)
        => GdalNative.CheckOgr(GdalNative.OGR_G_Transform(Handle, transformation.Handle), "Transform geometry");

    /// <summary>Transforms the geometry in place to <paramref name="target"/> (requires an assigned SRS).</summary>
    public void TransformTo(SpatialReference target)
        => GdalNative.CheckOgr(GdalNative.OGR_G_TransformTo(Handle, target.Handle), "TransformTo");

    // ------------------------------------------------------------------ predicates

    public bool Intersects(Geometry other) => GdalNative.OGR_G_Intersects(Handle, other.Handle) != 0;
    public bool Equals(Geometry other) => GdalNative.OGR_G_Equals(Handle, other.Handle) != 0;
    public bool Disjoint(Geometry other) => GdalNative.OGR_G_Disjoint(Handle, other.Handle) != 0;
    public bool Touches(Geometry other) => GdalNative.OGR_G_Touches(Handle, other.Handle) != 0;
    public bool Crosses(Geometry other) => GdalNative.OGR_G_Crosses(Handle, other.Handle) != 0;
    public bool Within(Geometry other) => GdalNative.OGR_G_Within(Handle, other.Handle) != 0;
    public bool Contains(Geometry other) => GdalNative.OGR_G_Contains(Handle, other.Handle) != 0;
    public bool Overlaps(Geometry other) => GdalNative.OGR_G_Overlaps(Handle, other.Handle) != 0;
    public double Distance(Geometry other) => GdalNative.OGR_G_Distance(Handle, other.Handle);

    // ------------------------------------------------------------------ operations (return new owned geometries)

    public Geometry Buffer(double distance, int quadrantSegments = 30) => OwnedOrThrow(GdalNative.OGR_G_Buffer(Handle, distance, quadrantSegments), "Buffer");
    public Geometry Intersection(Geometry other) => OwnedOrThrow(GdalNative.OGR_G_Intersection(Handle, other.Handle), "Intersection");
    public Geometry Union(Geometry other) => OwnedOrThrow(GdalNative.OGR_G_Union(Handle, other.Handle), "Union");
    public Geometry Difference(Geometry other) => OwnedOrThrow(GdalNative.OGR_G_Difference(Handle, other.Handle), "Difference");
    public Geometry SymDifference(Geometry other) => OwnedOrThrow(GdalNative.OGR_G_SymDifference(Handle, other.Handle), "SymDifference");

    /// <summary>Dissolves all parts of a collection into a single geometry.</summary>
    public Geometry UnaryUnion() => OwnedOrThrow(GdalNative.OGR_G_UnaryUnion(Handle), "UnaryUnion");

    /// <summary>Cascaded union of a multipolygon (fast dissolve).</summary>
    public Geometry UnionCascaded() => OwnedOrThrow(GdalNative.OGR_G_UnionCascaded(Handle), "UnionCascaded");

    public Geometry ConvexHull() => OwnedOrThrow(GdalNative.OGR_G_ConvexHull(Handle), "ConvexHull");

    /// <summary>Concave hull (GEOS ≥ 3.11). <paramref name="ratio"/> in [0,1]: 1 = convex hull.</summary>
    public Geometry ConcaveHull(double ratio, bool allowHoles = false) => OwnedOrThrow(GdalNative.OGR_G_ConcaveHull(Handle, ratio, allowHoles ? 1 : 0), "ConcaveHull");

    public Geometry Boundary() => OwnedOrThrow(GdalNative.OGR_G_Boundary(Handle), "Boundary");

    public Geometry Centroid()
    {
        var point = Create(GeometryType.Point);
        GdalNative.CheckOgr(GdalNative.OGR_G_Centroid(Handle, point.Handle), "Centroid");
        return point;
    }

    public Geometry PointOnSurface() => OwnedOrThrow(GdalNative.OGR_G_PointOnSurface(Handle), "PointOnSurface");

    /// <summary>Douglas-Peucker simplification.</summary>
    public Geometry Simplify(double tolerance) => OwnedOrThrow(GdalNative.OGR_G_Simplify(Handle, tolerance), "Simplify");

    /// <summary>Simplification that never produces invalid geometries.</summary>
    public Geometry SimplifyPreserveTopology(double tolerance) => OwnedOrThrow(GdalNative.OGR_G_SimplifyPreserveTopology(Handle, tolerance), "SimplifyPreserveTopology");

    public Geometry MakeValid() => OwnedOrThrow(GdalNative.OGR_G_MakeValid(Handle), "MakeValid");

    public Geometry DelaunayTriangulation(double tolerance = 0, bool onlyEdges = false)
        => OwnedOrThrow(GdalNative.OGR_G_DelaunayTriangulation(Handle, tolerance, onlyEdges ? 1 : 0), "DelaunayTriangulation");

    /// <summary>Unions a set of geometries (dissolve) using a single cascaded union.</summary>
    public static Geometry UnionAll(IEnumerable<Geometry> geometries)
    {
        using var collection = Create(GeometryType.GeometryCollection);
        foreach (var g in geometries) collection.AddGeometry(g);
        return collection.UnaryUnion();
    }

    /// <summary>Human readable summary, e.g. "POLYGON (5 points, area 12.5)".</summary>
    public string Describe()
    {
        var sb = new StringBuilder(GeometryName);
        if (Dimension == 2) sb.Append($" area={Area:G6}");
        else if (Dimension == 1) sb.Append($" length={Length:G6}");
        return sb.ToString();
    }
}
