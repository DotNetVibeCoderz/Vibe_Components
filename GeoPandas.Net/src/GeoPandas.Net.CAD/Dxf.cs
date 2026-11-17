using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GeoPandas.Net.Data;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.CAD;

/// <summary>Options for converting CAD drawings to GIS features.</summary>
public sealed record DxfReadOptions
{
    /// <summary>Turn closed polylines and circles into polygons (otherwise rings stay lines).</summary>
    public bool ClosedAsPolygons { get; init; } = true;
    /// <summary>Segments used to approximate a full circle.</summary>
    public int CircleSegments { get; init; } = 64;
    /// <summary>CRS assigned to the drawing coordinates (CAD files carry none).</summary>
    public CoordinateReferenceSystem? Crs { get; init; }
    /// <summary>Optional CAD → world georeferencing.</summary>
    public CadGeoreference? Georeference { get; init; }
    /// <summary>Only read these layers (null = all).</summary>
    public IReadOnlySet<string>? Layers { get; init; }
}

/// <summary>
/// Similarity transform from CAD drawing units to world coordinates:
/// world = origin + scale · R(rotation) · (cad − cadBasePoint).
/// </summary>
public sealed record CadGeoreference(double OriginX, double OriginY, double Scale = 1, double RotationDegrees = 0, double CadBaseX = 0, double CadBaseY = 0)
{
    public (double X, double Y) Apply(double x, double y)
    {
        double r = RotationDegrees * Math.PI / 180, dx = (x - CadBaseX) * Scale, dy = (y - CadBaseY) * Scale;
        return (OriginX + dx * Math.Cos(r) - dy * Math.Sin(r), OriginY + dx * Math.Sin(r) + dy * Math.Cos(r));
    }

    /// <summary>Solves the transform from two control-point pairs (CAD → world).</summary>
    public static CadGeoreference FromControlPoints((double X, double Y) cad1, (double X, double Y) world1, (double X, double Y) cad2, (double X, double Y) world2)
    {
        double cdx = cad2.X - cad1.X, cdy = cad2.Y - cad1.Y, wdx = world2.X - world1.X, wdy = world2.Y - world1.Y;
        double scale = Math.Sqrt(wdx * wdx + wdy * wdy) / Math.Sqrt(cdx * cdx + cdy * cdy);
        double rot = (Math.Atan2(wdy, wdx) - Math.Atan2(cdy, cdx)) * 180 / Math.PI;
        return new CadGeoreference(world1.X, world1.Y, scale, rot, cad1.X, cad1.Y);
    }
}

/// <summary>AutoCAD DXF (ASCII) reader / writer.</summary>
public static class Dxf
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Registers the DXF driver with <see cref="GeoIO"/> (also done automatically when this assembly loads).</summary>
    public static void Register() =>
        GeoIO.Register(new GeoDriver("DXF", [".dxf"], (p, _) => Read(p), (g, p, l) => Write(g, p, layerColumn: "layer")));

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void AutoRegister() => Register();
#pragma warning restore CA2255

    private sealed class Entity
    {
        public string Type = "";
        public readonly List<(int Code, string Value)> Pairs = [];
        public string? Get(int code) => Pairs.FirstOrDefault(p => p.Code == code).Value;
        public double D(int code, double def = 0) => double.TryParse(Get(code), NumberStyles.Float, Inv, out var v) ? v : def;
        public int I(int code, int def = 0) => int.TryParse(Get(code)?.Trim(), NumberStyles.Integer, Inv, out var v) ? v : def;
        public IEnumerable<double> All(int code) => Pairs.Where(p => p.Code == code).Select(p => double.Parse(p.Value, Inv));
    }

    public static GeoDataFrame Read(string path, DxfReadOptions? options = null) => Parse(File.ReadAllText(path, Encoding.UTF8), options);

    /// <summary>Parses DXF text into a GeoDataFrame with columns layer, entity, color, linetype, text, height, radius, block.</summary>
    public static GeoDataFrame Parse(string text, DxfReadOptions? options = null)
    {
        options ??= new DxfReadOptions();
        var lines = text.Split('\n');
        var pairs = new List<(int, string)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), NumberStyles.Integer, Inv, out int code)) pairs.Add((code, lines[i + 1].TrimEnd('\r')));

        // locate ENTITIES section and the LAYER table colours
        var layerColors = new Dictionary<string, int>();
        var entities = new List<Entity>();
        string section = "";
        Entity? current = null;
        Entity? layerRec = null;
        for (int i = 0; i < pairs.Count; i++)
        {
            var (code, val) = pairs[i];
            if (code == 0 && val == "SECTION" && i + 1 < pairs.Count) { section = pairs[i + 1].Item2.Trim(); continue; }
            if (code == 0 && val == "ENDSEC") { Flush(); section = ""; continue; }
            if (section == "TABLES")
            {
                if (code == 0) { if (layerRec is not null) layerColors[layerRec.Get(2) ?? "0"] = Math.Abs(layerRec.I(62, 7)); layerRec = val == "LAYER" ? new Entity { Type = "LAYER" } : null; }
                else layerRec?.Pairs.Add((code, val));
                continue;
            }
            if (section != "ENTITIES") continue;
            if (code == 0)
            {
                Flush();
                current = new Entity { Type = val.Trim() };
            }
            else current?.Pairs.Add((code, val.Trim()));
        }
        Flush();
        void Flush() { if (current is not null) { entities.Add(current); current = null; } }

        var geoms = new List<NtsGeometry?>();
        var layer = new List<string?>(); var etype = new List<string?>(); var color = new List<long?>();
        var ltype = new List<string?>(); var txt = new List<string?>(); var height = new List<double?>(); var radius = new List<double?>(); var block = new List<string?>();
        var f = Geo.Factory;
        var geo = options.Georeference;
        Coordinate C(double x, double y, double z = double.NaN)
        {
            if (geo is not null) (x, y) = geo.Apply(x, y);
            return double.IsNaN(z) || z == 0 ? new Coordinate(x, y) : new CoordinateZ(x, y, z);
        }

        void Add(NtsGeometry g, Entity e, string? text = null, double? h = null, double? r = null, string? blk = null)
        {
            var lyr = e.Get(8) ?? "0";
            if (options.Layers is not null && !options.Layers.Contains(lyr)) return;
            geoms.Add(g); layer.Add(lyr); etype.Add(e.Type);
            int col = e.I(62, 256);
            color.Add(col == 256 ? layerColors.GetValueOrDefault(lyr, 7) : col);
            ltype.Add(e.Get(6) ?? "BYLAYER"); txt.Add(text); height.Add(h); radius.Add(r); block.Add(blk);
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

        for (int k = 0; k < entities.Count; k++)
        {
            var e = entities[k];
            switch (e.Type)
            {
                case "POINT": Add(f.CreatePoint(C(e.D(10), e.D(20), e.D(30))), e); break;
                case "LINE": Add(f.CreateLineString([C(e.D(10), e.D(20), e.D(30)), C(e.D(11), e.D(21), e.D(31))]), e); break;
                case "LWPOLYLINE":
                {
                    var xs = e.All(10).ToArray(); var ys = e.All(20).ToArray();
                    var bulges = new double[xs.Length];
                    // bulge (42) follows the vertex it belongs to
                    int vi = -1;
                    foreach (var (code, val) in e.Pairs)
                    {
                        if (code == 10) vi++;
                        else if (code == 42 && vi >= 0 && vi < bulges.Length) bulges[vi] = double.Parse(val, Inv);
                    }
                    bool closed = (e.I(70) & 1) == 1;
                    Add(Ring(WithBulges(xs, ys, bulges, closed, C), closed), e);
                    break;
                }
                case "POLYLINE":
                {
                    bool closed = (e.I(70) & 1) == 1;
                    var pts = new List<Coordinate>();
                    while (k + 1 < entities.Count && entities[k + 1].Type == "VERTEX") { k++; pts.Add(C(entities[k].D(10), entities[k].D(20), entities[k].D(30))); }
                    if (k + 1 < entities.Count && entities[k + 1].Type == "SEQEND") k++;
                    if (pts.Count > 0) Add(Ring(pts, closed), e);
                    break;
                }
                case "CIRCLE":
                {
                    double cx = e.D(10), cy = e.D(20), r = e.D(40);
                    var pts = Arc(cx, cy, r, 0, 360, options.CircleSegments, C);
                    Add(Ring(pts, true), e, r: r * (geo?.Scale ?? 1));
                    break;
                }
                case "ARC":
                {
                    double r = e.D(40);
                    double a0 = e.D(50), a1 = e.D(51);
                    if (a1 < a0) a1 += 360;
                    int segs = Math.Max(4, (int)(options.CircleSegments * (a1 - a0) / 360));
                    Add(f.CreateLineString(Arc(e.D(10), e.D(20), r, a0, a1, segs, C).ToArray()), e, r: r * (geo?.Scale ?? 1));
                    break;
                }
                case "ELLIPSE":
                {
                    double cx = e.D(10), cy = e.D(20), mx = e.D(11), my = e.D(21), ratio = e.D(40, 1), t0 = e.D(41), t1 = e.D(42, 2 * Math.PI);
                    if (t1 <= t0) t1 += 2 * Math.PI;
                    int segs = options.CircleSegments;
                    var pts = new List<Coordinate>();
                    for (int s = 0; s <= segs; s++)
                    {
                        double t = t0 + (t1 - t0) * s / segs;
                        pts.Add(C(cx + mx * Math.Cos(t) - my * ratio * Math.Sin(t), cy + my * Math.Cos(t) + mx * ratio * Math.Sin(t)));
                    }
                    bool full = Math.Abs(t1 - t0 - 2 * Math.PI) < 1e-6;
                    Add(Ring(pts, full), e);
                    break;
                }
                case "SPLINE":
                {
                    var xs = e.All(11).ToArray(); var ys = e.All(21).ToArray(); // fit points
                    if (xs.Length < 2) { xs = e.All(10).ToArray(); ys = e.All(20).ToArray(); } // control points
                    var pts = xs.Select((x, i) => C(x, ys[i])).ToList();
                    if (pts.Count >= 2) Add(Ring(pts, (e.I(70) & 1) == 1), e);
                    break;
                }
                case "TEXT":
                    Add(f.CreatePoint(C(e.D(10), e.D(20), e.D(30))), e, e.Get(1), e.D(40));
                    break;
                case "MTEXT":
                {
                    var sb = new StringBuilder();
                    foreach (var (code, val) in e.Pairs) if (code is 3 or 1) sb.Append(val);
                    var clean = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\\[A-Za-z][^;]*;|\\P|[{}]", " ").Trim();
                    Add(f.CreatePoint(C(e.D(10), e.D(20), e.D(30))), e, clean, e.D(40));
                    break;
                }
                case "INSERT":
                    Add(f.CreatePoint(C(e.D(10), e.D(20), e.D(30))), e, blk: e.Get(2));
                    break;
                case "3DFACE" or "SOLID":
                {
                    var pts = new List<Coordinate> { C(e.D(10), e.D(20), e.D(30)), C(e.D(11), e.D(21), e.D(31)), C(e.D(12), e.D(22), e.D(32)), C(e.D(13), e.D(23), e.D(33)) };
                    var distinct = pts.Where((p, i) => i == 0 || !p.Equals2D(pts[i - 1])).ToList();
                    if (distinct.Count >= 3) Add(Ring(distinct, true), e);
                    break;
                }
            }
        }

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

    private static List<Coordinate> Arc(double cx, double cy, double r, double a0, double a1, int segs, Func<double, double, double, Coordinate> c)
    {
        var pts = new List<Coordinate>(segs + 1);
        for (int s = 0; s <= segs; s++)
        {
            double a = (a0 + (a1 - a0) * s / segs) * Math.PI / 180;
            pts.Add(c(cx + r * Math.Cos(a), cy + r * Math.Sin(a), double.NaN));
        }
        return pts;
    }

    /// <summary>Expands LWPOLYLINE bulge arcs (bulge = tan(θ/4)) into vertices.</summary>
    private static List<Coordinate> WithBulges(double[] xs, double[] ys, double[] bulges, bool closed, Func<double, double, double, Coordinate> c)
    {
        var pts = new List<Coordinate>();
        int n = xs.Length;
        int segCount = closed ? n : n - 1;
        for (int i = 0; i < n; i++)
        {
            pts.Add(c(xs[i], ys[i], double.NaN));
            if (i >= segCount || bulges[i] == 0) continue;
            int j = (i + 1) % n;
            double x1 = xs[i], y1 = ys[i], x2 = xs[j], y2 = ys[j], b = bulges[i];
            double theta = 4 * Math.Atan(b);
            double chord = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (chord == 0) continue;
            double r = chord / (2 * Math.Sin(theta / 2));
            double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
            double d = r * Math.Cos(theta / 2);
            double nx = -(y2 - y1) / chord, ny = (x2 - x1) / chord;
            double cx = mx + nx * d, cy = my + ny * d;
            double a1 = Math.Atan2(y1 - cy, x1 - cx);
            int segs = Math.Max(2, (int)(Math.Abs(theta) / (Math.PI / 16)));
            for (int s = 1; s < segs; s++)
            {
                double a = a1 + theta * s / segs;
                pts.Add(c(cx + Math.Abs(r) * Math.Cos(a), cy + Math.Abs(r) * Math.Sin(a), double.NaN));
            }
        }
        return pts;
    }

    // ------------------------------------------------------------------ writer

    /// <summary>
    /// Writes an R12-compatible ASCII DXF (readable by AutoCAD, BricsCAD, LibreCAD, QGIS…).
    /// Points → POINT (or TEXT when <paramref name="textColumn"/> is set), lines/rings → POLYLINE.
    /// </summary>
    public static void Write(GeoDataFrame gdf, string path, string? layerColumn = null, string? textColumn = null, string? colorColumn = null, double textHeight = 2.5)
    {
        var sb = new StringBuilder();
        void P(int code, object value) => sb.Append(code).Append('\n').Append(Convert.ToString(value, Inv)).Append('\n');

        string LayerOf(int i) => layerColumn is not null && gdf.HasColumn(layerColumn) ? Sanitize(gdf[layerColumn].GetValue(i)?.ToString() ?? "0") : "0";
        var layers = Enumerable.Range(0, gdf.RowCount).Select(LayerOf).Distinct().ToList();
        if (!layers.Contains("0")) layers.Insert(0, "0");

        var env = gdf.TotalBounds;
        P(0, "SECTION"); P(2, "HEADER");
        P(9, "$ACADVER"); P(1, "AC1009");
        P(9, "$EXTMIN"); P(10, env.IsNull ? 0 : env.MinX); P(20, env.IsNull ? 0 : env.MinY);
        P(9, "$EXTMAX"); P(10, env.IsNull ? 0 : env.MaxX); P(20, env.IsNull ? 0 : env.MaxY);
        P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "TABLES");
        P(0, "TABLE"); P(2, "LAYER"); P(70, layers.Count);
        for (int k = 0; k < layers.Count; k++) { P(0, "LAYER"); P(2, layers[k]); P(70, 0); P(62, k == 0 ? 7 : (k % 254) + 1); P(6, "CONTINUOUS"); }
        P(0, "ENDTAB");
        P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "ENTITIES");

        for (int i = 0; i < gdf.RowCount; i++)
        {
            var g = gdf.Geometry[i];
            if (g is null) continue;
            string lyr = LayerOf(i);
            int? col = colorColumn is not null && !gdf[colorColumn].IsNull(i) ? (int)gdf[colorColumn].GetDouble(i) : null;
            string? text = textColumn is not null ? gdf[textColumn].GetValue(i)?.ToString() : null;
            foreach (var part in GeometryOps.Explode(g))
            {
                switch (part)
                {
                    case Point p when text is not null:
                        P(0, "TEXT"); P(8, lyr); if (col is { } c1) P(62, c1);
                        P(10, p.X); P(20, p.Y); P(30, double.IsNaN(p.Z) ? 0 : p.Z); P(40, textHeight); P(1, text);
                        break;
                    case Point p:
                        P(0, "POINT"); P(8, lyr); if (col is { } c2) P(62, c2);
                        P(10, p.X); P(20, p.Y); P(30, double.IsNaN(p.Z) ? 0 : p.Z);
                        break;
                    case LineString ls:
                        WritePolyline(ls.Coordinates, ls.IsClosed && ls is LinearRing, lyr, col);
                        break;
                    case Polygon poly:
                        WritePolyline(poly.ExteriorRing.Coordinates, true, lyr, col);
                        foreach (var h in poly.InteriorRings) WritePolyline(h.Coordinates, true, lyr, col);
                        break;
                }
            }
        }
        P(0, "ENDSEC");
        P(0, "EOF");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        void WritePolyline(Coordinate[] cs, bool closed, string lyr, int? col)
        {
            if (closed && cs.Length > 1 && cs[0].Equals2D(cs[^1])) cs = cs[..^1];
            bool is3d = cs.Any(c => !double.IsNaN(c.Z) && c.Z != 0);
            P(0, "POLYLINE"); P(8, lyr); if (col is { } c) P(62, c);
            P(66, 1); P(10, 0); P(20, 0); P(30, 0); P(70, (closed ? 1 : 0) | (is3d ? 8 : 0));
            foreach (var v in cs)
            {
                P(0, "VERTEX"); P(8, lyr); P(10, v.X); P(20, v.Y); P(30, double.IsNaN(v.Z) ? 0 : v.Z);
                if (is3d) P(70, 32);
            }
            P(0, "SEQEND"); P(8, lyr);
        }
    }

    private static string Sanitize(string s)
    {
        var clean = new string(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '$' ? ch : '_').ToArray());
        return clean.Length == 0 ? "0" : clean;
    }
}
