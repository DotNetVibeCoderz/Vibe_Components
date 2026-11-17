using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Geometries.Utilities;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Geometry;

/// <summary>
/// Robust geometry operations built on OverlayNG (snap-rounding fallback), with helpers used by GeoDataFrame
/// overlay / dissolve / clip.
/// </summary>
public static class GeometryOps
{
    public static NtsGeometry Intersection(NtsGeometry a, NtsGeometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Intersection);
    public static NtsGeometry Union(NtsGeometry a, NtsGeometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Union);
    public static NtsGeometry Difference(NtsGeometry a, NtsGeometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference);
    public static NtsGeometry SymmetricDifference(NtsGeometry a, NtsGeometry b) => OverlayNGRobust.Overlay(a, b, SpatialFunction.SymDifference);

    /// <summary>Union of many geometries (cascaded for polygons, OverlayNG otherwise).</summary>
    public static NtsGeometry UnaryUnion(IEnumerable<NtsGeometry?> geometries)
    {
        var list = geometries.Where(g => g is { IsEmpty: false }).Cast<NtsGeometry>().ToList();
        if (list.Count == 0) return Geo.Factory.CreateGeometryCollection();
        if (list.Count == 1) return OverlayNGRobust.Union(list[0]);
        if (list.All(g => g is IPolygonal))
        {
            try { return CascadedPolygonUnion.Union(list); }
            catch (TopologyException) { /* fall through to robust path */ }
        }
        return OverlayNGRobust.Union(Geo.Factory.BuildGeometry(list));
    }

    /// <summary>Repairs invalid geometries (self-intersections, bow-ties) preserving area where possible.</summary>
    public static NtsGeometry MakeValid(NtsGeometry g) => g.IsValid ? g : GeometryFixer.Fix(g);

    /// <summary>Keeps only the parts with the requested topological dimension (0 = points, 1 = lines, 2 = polygons).</summary>
    public static NtsGeometry KeepDimension(NtsGeometry g, Dimension dim)
    {
        if (g is not GeometryCollection gc || g is MultiPoint or MultiLineString or MultiPolygon)
            return g.Dimension == dim ? g : Geo.Factory.CreateGeometryCollection();
        var parts = new List<NtsGeometry>();
        for (int i = 0; i < gc.NumGeometries; i++)
        {
            var p = KeepDimension(gc.GetGeometryN(i), dim);
            for (int k = 0; k < p.NumGeometries; k++) if (!p.GetGeometryN(k).IsEmpty) parts.Add(p.GetGeometryN(k));
        }
        if (parts.Count == 0) return Geo.Factory.CreateGeometryCollection();
        if (parts.Count == 1) return parts[0];
        return Geo.Factory.BuildGeometry(parts);
    }

    /// <summary>Splits multi-part geometries into their single parts.</summary>
    public static IEnumerable<NtsGeometry> Explode(NtsGeometry g)
    {
        if (g is GeometryCollection gc)
        {
            for (int i = 0; i < gc.NumGeometries; i++)
                foreach (var p in Explode(gc.GetGeometryN(i))) yield return p;
        }
        else yield return g;
    }

    /// <summary>2-D affine transform: translate, rotate (degrees, about origin), scale.</summary>
    public static NtsGeometry Affine(NtsGeometry g, double dx = 0, double dy = 0, double rotateDeg = 0, double scaleX = 1, double scaleY = 1, Coordinate? origin = null)
    {
        var o = origin ?? g.Centroid.Coordinate;
        var t = new AffineTransformation();
        t.Translate(-o.X, -o.Y);
        t.Scale(scaleX, scaleY);
        t.Rotate(rotateDeg * Math.PI / 180);
        t.Translate(o.X + dx, o.Y + dy);
        return t.Transform(g);
    }
}
