using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Geometry;

/// <summary>
/// Entry point for building geometries. GeoPandas.Net uses NetTopologySuite (the .NET port of JTS/GEOS)
/// as its robust geometry engine, exactly like Python GeoPandas builds on Shapely/GEOS.
/// </summary>
/// <remarks>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.</remarks>
public static class Geo
{
    /// <summary>Shared floating-precision factory (SRID 0). Thread-safe.</summary>
    public static GeometryFactory Factory { get; } =
        NtsGeometryServices.Instance.CreateGeometryFactory(new PrecisionModel(PrecisionModels.Floating), 0);

    private static readonly ThreadLocal<WKTReader> s_wktReader = new(() => new WKTReader(NtsGeometryServices.Instance));
    private static readonly ThreadLocal<WKBReader> s_wkbReader = new(() => new WKBReader(NtsGeometryServices.Instance));

    public static Point Point(double x, double y) => Factory.CreatePoint(new Coordinate(x, y));
    public static Point Point(double x, double y, double z) => Factory.CreatePoint(new CoordinateZ(x, y, z));

    public static LineString LineString(params (double X, double Y)[] coords) =>
        Factory.CreateLineString(coords.Select(c => new Coordinate(c.X, c.Y)).ToArray());

    public static LineString LineString(IEnumerable<Coordinate> coords) => Factory.CreateLineString(coords.ToArray());

    /// <summary>Creates a polygon from a shell (auto-closed) and optional holes.</summary>
    public static Polygon Polygon((double X, double Y)[] shell, params (double X, double Y)[][] holes) =>
        Factory.CreatePolygon(Ring(shell), holes.Select(Ring).ToArray());

    public static Polygon Polygon(IEnumerable<Coordinate> shell) => Factory.CreatePolygon(Ring(shell.Select(c => (c.X, c.Y)).ToArray()));

    /// <summary>Axis-aligned rectangle polygon (Shapely's <c>box</c>).</summary>
    public static Polygon Box(double minX, double minY, double maxX, double maxY) =>
        Factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY), new Coordinate(maxX, minY), new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY), new Coordinate(minX, minY)
        });

    public static MultiPoint MultiPoint(params (double X, double Y)[] points) =>
        Factory.CreateMultiPoint(points.Select(p => Point(p.X, p.Y)).ToArray());

    public static MultiPolygon MultiPolygon(params Polygon[] polygons) => Factory.CreateMultiPolygon(polygons);
    public static MultiLineString MultiLineString(params LineString[] lines) => Factory.CreateMultiLineString(lines);
    public static GeometryCollection Collection(params NtsGeometry[] geoms) => Factory.CreateGeometryCollection(geoms);

    /// <summary>Regular polygon approximating a circle.</summary>
    public static Polygon Circle(double cx, double cy, double radius, int segments = 64)
    {
        var pts = new (double, double)[segments];
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments;
            pts[i] = (cx + radius * Math.Cos(a), cy + radius * Math.Sin(a));
        }
        return Polygon(pts);
    }

    public static NtsGeometry FromWkt(string wkt) => s_wktReader.Value!.Read(wkt);
    public static NtsGeometry FromWkb(byte[] wkb) => s_wkbReader.Value!.Read(wkb);
    public static string ToWkt(NtsGeometry g) => g.AsText();
    public static byte[] ToWkb(NtsGeometry g) => new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: HasZ(g)).Write(g);

    /// <summary>True when any coordinate carries a real Z value.</summary>
    public static bool HasZ(NtsGeometry g)
    {
        foreach (var c in g.Coordinates) if (!double.IsNaN(c.Z)) return true;
        return false;
    }

    private static LinearRing Ring((double X, double Y)[] pts)
    {
        if (pts.Length < 3) throw new ArgumentException("A ring needs at least 3 points.", nameof(pts));
        var coords = new List<Coordinate>(pts.Length + 1);
        foreach (var (x, y) in pts) coords.Add(new Coordinate(x, y));
        if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
        return Factory.CreateLinearRing(coords.ToArray());
    }
}
