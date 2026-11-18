using System.Globalization;
using System.Runtime.CompilerServices;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using GeoPandas.Net.Data;
using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.CAD;

/// <summary>
/// AutoCAD DWG reader and writer (R14 / AC1014 to 2018 / AC1032) built on ACadSharp (MIT). Produces the same columns as <see cref="Dxf"/>:
/// layer, entity, color (ACI, BYLAYER resolved), linetype, text, height, radius, block. Block references are exploded with their transforms.
/// </summary>
public static class Dwg
{
    /// <summary>Registers the DWG driver with <see cref="GeoIO"/> (done automatically when this assembly loads).</summary>
    public static void Register() =>
        GeoIO.Register(new GeoDriver("DWG", [".dwg"], (p, _) => Read(p), (g, p, l) => Write(g, p, layerColumn: "layer")));

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void AutoRegister() => Register();
#pragma warning restore CA2255

    public static GeoDataFrame Read(string path, DxfReadOptions? options = null)
    {
        var doc = DwgReader.Read(path);
        return FromDocument(doc, options);
    }

    /// <summary>Reads any ACadSharp document (DWG or DXF, ASCII or binary).</summary>
    public static GeoDataFrame FromDocument(CadDocument doc, DxfReadOptions? options = null)
    {
        options ??= new DxfReadOptions();
        var f = Geo.Factory;
        var geo = options.Georeference;
        var geoms = new List<NtsGeometry?>();
        var layer = new List<string?>(); var etype = new List<string?>(); var color = new List<long?>();
        var ltype = new List<string?>(); var txt = new List<string?>(); var height = new List<double?>(); var radius = new List<double?>(); var block = new List<string?>();
        int segs = options.CircleSegments;

        Coordinate C(double x, double y, double z)
        {
            if (geo is not null) (x, y) = geo.Apply(x, y);
            return double.IsNaN(z) || z == 0 ? new Coordinate(x, y) : new CoordinateZ(x, y, z);
        }
        NtsGeometry Ring(List<Coordinate> pts, bool closed)
        {
            if (closed && pts.Count >= 3)
            {
                if (!pts[0].Equals2D(pts[^1])) pts.Add(pts[0].Copy());
                return options.ClosedAsPolygons && pts.Count >= 4 ? f.CreatePolygon(pts.ToArray()) : f.CreateLineString(pts.ToArray());
            }
            if (pts.Count == 1) pts.Add(pts[0].Copy());
            return f.CreateLineString(pts.ToArray());
        }
        void Add(NtsGeometry g, Entity e, string? blockName, string? text = null, double? h = null, double? r = null)
        {
            var lyr = e.Layer?.Name ?? "0";
            if (options.Layers is not null && !options.Layers.Contains(lyr)) return;
            geoms.Add(g); layer.Add(lyr); etype.Add(e.ObjectName); txt.Add(text); height.Add(h); radius.Add(r); block.Add(blockName);
            var c = e.GetActiveColor();
            color.Add(c.IsTrueColor ? c.GetApproxIndex() : c.Index);
            ltype.Add(e.LineType?.Name is { } lt && !e.LineType.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ? lt.ToUpperInvariant() : "BYLAYER");
        }
        List<Coordinate> Pts(IEnumerable<XYZ> v) => v.Select(p => C(p.X, p.Y, p.Z)).ToList();

        void Entity(Entity e, string? blockName, int depth)
        {
            if (e.IsInvisible) return;
            switch (e)
            {
                case ACadSharp.Entities.Point p: Add(f.CreatePoint(C(p.Location.X, p.Location.Y, p.Location.Z)), e, blockName); break;
                case Line l: Add(f.CreateLineString([C(l.StartPoint.X, l.StartPoint.Y, l.StartPoint.Z), C(l.EndPoint.X, l.EndPoint.Y, l.EndPoint.Z)]), e, blockName); break;
                case Arc a: Add(f.CreateLineString(Pts(a.PolygonalVertexes(Math.Max(4, (int)(segs * Math.Abs(a.Sweep) / (2 * Math.PI))))).ToArray()), e, blockName, r: a.Radius); break;
                case Circle c: Add(Ring(Pts(c.PolygonalVertexes(segs)), true), e, blockName, r: c.Radius); break;
                case Ellipse el: Add(el.IsFullEllipse ? Ring(Pts(el.PolygonalVertexes(segs)), true) : f.CreateLineString(Pts(el.PolygonalVertexes(segs)).ToArray()), e, blockName); break;
                case Spline s:
                    if (s.TryPolygonalVertexes(Math.Max(segs, 8 * Math.Max(1, s.ControlPoints.Count)), out var sp) && sp.Count >= 2) Add(Ring(Pts(sp), s.IsClosed), e, blockName);
                    else if (s.FitPoints.Count >= 2) Add(Ring(Pts(s.FitPoints), s.IsClosed), e, blockName);
                    break;
                case IPolyline pl:
                {
                    var verts = pl.Vertices.Select(v => (P: v.Location switch { XYZ q => (q.X, q.Y, q.Z), XY q => (q.X, q.Y, pl.Elevation), _ => (0.0, 0.0, 0.0) }, v.Bulge)).ToList();
                    if (verts.Count == 0) break;
                    var pts = new List<Coordinate>();
                    int n = verts.Count, segCount = pl.IsClosed ? n : n - 1;
                    for (int i = 0; i < n; i++)
                    {
                        pts.Add(C(verts[i].P.Item1, verts[i].P.Item2, verts[i].P.Item3));
                        if (i >= segCount || verts[i].Bulge == 0) continue;
                        var a = verts[i].P; var b = verts[(i + 1) % n].P;
                        double theta = 4 * Math.Atan(verts[i].Bulge), chord = Math.Sqrt((b.Item1 - a.Item1) * (b.Item1 - a.Item1) + (b.Item2 - a.Item2) * (b.Item2 - a.Item2));
                        if (chord == 0) continue;
                        double r = chord / (2 * Math.Sin(theta / 2)), mx = (a.Item1 + b.Item1) / 2, my = (a.Item2 + b.Item2) / 2, d = r * Math.Cos(theta / 2);
                        double cx = mx - (b.Item2 - a.Item2) / chord * d, cy = my + (b.Item1 - a.Item1) / chord * d;
                        double a1 = Math.Atan2(a.Item2 - cy, a.Item1 - cx);
                        int k = Math.Max(2, (int)(Math.Abs(theta) / (Math.PI / 16)));
                        for (int s = 1; s < k; s++) { double ang = a1 + theta * s / k; pts.Add(C(cx + Math.Abs(r) * Math.Cos(ang), cy + Math.Abs(r) * Math.Sin(ang), a.Item3)); }
                    }
                    Add(Ring(pts, pl.IsClosed), e, blockName);
                    break;
                }
                case TextEntity t: Add(f.CreatePoint(C(t.InsertPoint.X, t.InsertPoint.Y, t.InsertPoint.Z)), e, blockName, t.Value, t.Height); break;
                case MText mt: Add(f.CreatePoint(C(mt.InsertPoint.X, mt.InsertPoint.Y, mt.InsertPoint.Z)), e, blockName, mt.PlainText, mt.Height); break;
                case Face3D fc:
                {
                    var pts = new List<Coordinate> { C(fc.FirstCorner.X, fc.FirstCorner.Y, fc.FirstCorner.Z), C(fc.SecondCorner.X, fc.SecondCorner.Y, fc.SecondCorner.Z),
                        C(fc.ThirdCorner.X, fc.ThirdCorner.Y, fc.ThirdCorner.Z), C(fc.FourthCorner.X, fc.FourthCorner.Y, fc.FourthCorner.Z) };
                    var distinct = pts.Where((p, i) => i == 0 || !p.Equals2D(pts[i - 1])).ToList();
                    if (distinct.Count >= 3) Add(Ring(distinct, true), e, blockName);
                    break;
                }
                case Solid so:
                {
                    // SOLID corners are stored as 1-2-4-3
                    var pts = new List<Coordinate> { C(so.FirstCorner.X, so.FirstCorner.Y, 0), C(so.SecondCorner.X, so.SecondCorner.Y, 0),
                        C(so.FourthCorner.X, so.FourthCorner.Y, 0), C(so.ThirdCorner.X, so.ThirdCorner.Y, 0) };
                    var distinct = pts.Where((p, i) => i == 0 || !p.Equals2D(pts[i - 1])).ToList();
                    if (distinct.Count >= 3) Add(Ring(distinct, true), e, blockName);
                    break;
                }
                case Insert ins when depth < 8:
                    foreach (var sub in ins.Explode()) Entity(sub, blockName ?? ins.Block?.Name, depth + 1);
                    break;
                case Hatch h:
                    foreach (var sub in h.Explode()) Entity(sub, blockName ?? "*HATCH", depth + 1);   // hatch boundaries, tagged
                    break;
            }
        }

        foreach (var e in doc.Entities) Entity(e, null, 0);
        return new GeoDataFrame(
        [
            new Column<string?>("layer", layer.ToArray()),
            new Column<string?>("entity", etype.ToArray()),
            Column<long>.FromNullable("color", color.ToArray()),
            new Column<string?>("linetype", ltype.ToArray()),
            new Column<string?>("text", txt.ToArray()),
            Column<double>.FromNullable("height", height.ToArray()),
            Column<double>.FromNullable("radius", radius.ToArray()),
            new Column<string?>("block", block.ToArray()),
            new GeoSeries("geometry", geoms.ToArray(), options.Crs)
        ]);
    }

    /// <summary>
    /// Writes a DWG (default AutoCAD 2018 format): points as POINT (or TEXT when <paramref name="textColumn"/> is set), lines and polygon rings as
    /// LWPOLYLINE (3-D lines as 3-D polylines), on layers from <paramref name="layerColumn"/> with ACI colours from <paramref name="colorColumn"/>.
    /// </summary>
    public static void Write(GeoDataFrame gdf, string path, string? layerColumn = null, string? textColumn = null, string? colorColumn = null, double textHeight = 2.5,
        ACadVersion version = ACadVersion.AC1032)
    {
        var doc = new CadDocument(version);
        string? Str(string? col, int i) => col is not null && gdf.Columns.Any(c => c.Name == col) && gdf[col].GetValue(i) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        for (int i = 0; i < gdf.RowCount; i++)
        {
            var g = gdf.Geometry[i];
            if (g is null || g.IsEmpty) continue;
            var layerName = Str(layerColumn, i) ?? "0";
            if (!doc.Layers.TryGetValue(layerName, out var layer)) { layer = new ACadSharp.Tables.Layer(layerName); doc.Layers.Add(layer); }
            Color color = Str(colorColumn, i) is { } cs && short.TryParse(cs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ci) ? new Color(ci) : Color.ByLayer;
            var text = Str(textColumn, i);
            foreach (var e in Entities(g, text, textHeight))
            {
                e.Layer = layer; e.Color = color;
                doc.Entities.Add(e);
            }
        }
        DwgWriter.Write(path, doc);
    }

    private static IEnumerable<Entity> Entities(NtsGeometry g, string? text, double textHeight)
    {
        switch (g)
        {
            case NetTopologySuite.Geometries.Point p:
                if (text is not null) yield return new TextEntity { InsertPoint = new XYZ(p.X, p.Y, double.IsNaN(p.Coordinate.Z) ? 0 : p.Coordinate.Z), Value = text, Height = textHeight };
                else yield return new ACadSharp.Entities.Point(new XYZ(p.X, p.Y, double.IsNaN(p.Coordinate.Z) ? 0 : p.Coordinate.Z));
                break;
            case LineString ls:
                yield return Polyline(ls.Coordinates, ls.IsClosed);
                break;
            case Polygon poly:
                yield return Polyline(poly.ExteriorRing.Coordinates, true);
                foreach (var h in poly.InteriorRings) yield return Polyline(h.Coordinates, true);
                break;
            case GeometryCollection gc:
                foreach (var part in gc.Geometries) foreach (var e in Entities(part, text, textHeight)) yield return e;
                break;
        }
    }

    private static Entity Polyline(Coordinate[] cs, bool closed)
    {
        if (closed && cs.Length > 1 && cs[0].Equals2D(cs[^1])) cs = cs[..^1];
        bool has3D = cs.Any(c => !double.IsNaN(c.Z) && c.Z != cs[0].Z);
        if (has3D)
        {
            var p3 = new Polyline3D();
            foreach (var c in cs) p3.Vertices.Add(new Vertex3D(new XYZ(c.X, c.Y, double.IsNaN(c.Z) ? 0 : c.Z)));
            if (closed) p3.Flags |= PolylineFlags.ClosedPolylineOrClosedPolygonMeshInM;
            return p3;
        }
        var lw = new LwPolyline { Elevation = double.IsNaN(cs[0].Z) ? 0 : cs[0].Z };
        foreach (var c in cs) lw.Vertices.Add(new LwPolyline.Vertex(new XY(c.X, c.Y)));
        lw.IsClosed = closed;
        return lw;
    }
}
