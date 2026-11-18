using System.Globalization;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.BIM;

/// <summary>Local engineering → map coordinates: E = s·(x·cosθ − y·sinθ) + E0, N = s·(x·sinθ + y·cosθ) + N0, H = z·s + H0 (x, y, z in metres).</summary>
public sealed record IfcGeoreference(double Eastings, double Northings, double OrthogonalHeight, double Rotation, double Scale, CoordinateReferenceSystem? Crs, string Source)
{
    public (double E, double N, double H) ToMap(double x, double y, double z) =>
        (Scale * (x * Math.Cos(Rotation) - y * Math.Sin(Rotation)) + Eastings, Scale * (x * Math.Sin(Rotation) + y * Math.Cos(Rotation)) + Northings, z * Scale + OrthogonalHeight);
}

/// <summary>A building element (wall, slab, column…) with its footprint, height range, volume and properties.</summary>
public sealed record IfcElement(long Id, string GlobalId, string IfcType, string? Name, string? ObjectType, string? Storey, NtsGeometry? Footprint,
    double ZMin, double ZMax, double Volume, IReadOnlyDictionary<string, object?> Properties);

/// <summary>A building storey with its elevation (metres).</summary>
public sealed record IfcStorey(long Id, string GlobalId, string? Name, double Elevation, string? Building);

/// <summary>
/// An IFC model (IFC2x3, IFC4, IFC4x3) read from a STEP file: spatial structure, elements with property and quantity sets, geometry reduced
/// to footprints and height ranges (extrusions, B-reps, tessellations, mapped items), and georeferencing. Converts to GeoDataFrames in map
/// coordinates for GIS and city models.
/// </summary>
public sealed class IfcModel
{
    private readonly StepFile _step;
    private readonly Dictionary<long, List<long>> _containedIn = [];     // element → structure
    private readonly Dictionary<long, long> _aggregatedBy = [];           // part → whole
    private readonly Dictionary<long, List<long>> _propertyDefs = [];     // object → psets/qtos
    private readonly Dictionary<long, long> _typeOf = [];                 // occurrence → type
    private List<IfcElement>? _elements;

    private IfcModel(StepFile step)
    {
        _step = step;
        Schema = step.Schemas.FirstOrDefault() ?? "IFC4";
        LengthUnit = ReadLengthUnit();
        foreach (var e in step.Entities.Values)
            switch (e.Type)
            {
                case "IFCRELCONTAINEDINSPATIALSTRUCTURE":
                    foreach (var r in Refs(e[4])) (_containedIn.TryGetValue(r, out var l) ? l : _containedIn[r] = []).Add(Ref(e[5]));
                    break;
                case "IFCRELAGGREGATES":
                    foreach (var r in Refs(e[5])) _aggregatedBy[r] = Ref(e[4]);
                    break;
                case "IFCRELDEFINESBYPROPERTIES":
                    foreach (var r in Refs(e[4])) (_propertyDefs.TryGetValue(r, out var l) ? l : _propertyDefs[r] = []).Add(Ref(e[5]));
                    break;
                case "IFCRELDEFINESBYTYPE":
                    foreach (var r in Refs(e[4])) _typeOf[r] = Ref(e[5]);
                    break;
            }
        Georeference = ReadGeoreference();
    }

    public static IfcModel Read(string path) => new(StepFile.Read(path));
    public static IfcModel Parse(string text) => new(StepFile.Parse(text));

    public StepFile Step => _step;
    public string Schema { get; }
    /// <summary>Metres per project length unit.</summary>
    public double LengthUnit { get; }
    public IfcGeoreference? Georeference { get; }
    public string? ProjectName => _step.Entities.Values.FirstOrDefault(e => e.Type == "IFCPROJECT")?[2] as string;

    // ------------------------------------------------------------------ helpers

    private StepEntity E(long id) => _step.Entities[id];
    private StepEntity? E(object? v) => v is StepRef r && _step.Entities.TryGetValue(r.Id, out var e) ? e : null;
    private static long Ref(object? v) => v is StepRef r ? r.Id : -1;
    private static IEnumerable<long> Refs(object? v) => v is List<object?> l ? l.OfType<StepRef>().Select(r => r.Id) : [];
    private static double Num(object? v, double def = 0) => v switch
    {
        double d => d, long l => l, StepTyped t => Num(t.Value, def), _ => def
    };
    private static double[] Nums(object? v) => v is List<object?> l ? l.Select(x => Num(x)).ToArray() : [];

    private double ReadLengthUnit()
    {
        var project = _step.Entities.Values.FirstOrDefault(e => e.Type == "IFCPROJECT");
        var assignment = project is null ? null : E(project[8]);
        foreach (var u in assignment is null ? [] : Refs(assignment[0]).Select(E))
        {
            if (u.Type == "IFCSIUNIT" && u[1] is StepEnum { Value: "LENGTHUNIT" })
                return Prefix(u[2] as StepEnum?) * 1.0;
            if (u.Type == "IFCCONVERSIONBASEDUNIT" && u[1] is StepEnum { Value: "LENGTHUNIT" } && E(u[3]) is { } measure)
            {
                double factor = Num(measure[0]);
                var baseUnit = E(measure[1]);
                return factor * (baseUnit is not null && baseUnit.Type == "IFCSIUNIT" ? Prefix(baseUnit[2] as StepEnum?) : 1);
            }
        }
        return 1;
        static double Prefix(StepEnum? p) => p?.Value switch
        {
            "MILLI" => 1e-3, "CENTI" => 1e-2, "DECI" => 1e-1, "KILO" => 1e3, "MICRO" => 1e-6, _ => 1
        };
    }

    private IfcGeoreference? ReadGeoreference()
    {
        var mc = _step.Entities.Values.FirstOrDefault(e => e.Type is "IFCMAPCONVERSION" or "IFCMAPCONVERSIONSCALED");
        if (mc is not null)
        {
            var crsEnt = E(mc[1]);
            CoordinateReferenceSystem? crs = null;
            if (crsEnt?[0] is string name && CoordinateReferenceSystem.TryParse(name, out var c)) crs = c;
            double scale = mc[7] is null ? 1 : Num(mc[7], 1);
            // IfcOpenShell semantics: Scale converts project units to map units. When it is 1 but the map unit differs from the project unit
            // (IFC4.3 style, unit conversion via MapUnit), apply the unit ratio.
            double mapUnit = crsEnt is not null && E(crsEnt[6]) is { Type: "IFCSIUNIT" } mu ? (mu[2] is StepEnum { Value: "MILLI" } ? 1e-3 : 1) : double.NaN;
            if (scale == 1 && !double.IsNaN(mapUnit) && Math.Abs(mapUnit - LengthUnit) > 1e-12) scale = LengthUnit / mapUnit;
            return Make(Num(mc[2]), Num(mc[3]), Num(mc[4]), Num(mc[5], 1), Num(mc[6]), scale, crs, "IfcMapConversion");
        }
        // IFC2x3 georeferencing guide: ePSet_MapConversion / ePSet_ProjectedCRS on the project
        var project = _step.Entities.Values.FirstOrDefault(e => e.Type == "IFCPROJECT");
        if (project is not null)
        {
            var props = Properties(project.Id);
            if (props.ContainsKey("ePSet_MapConversion.Eastings"))
            {
                double P(string n, double d) => props.TryGetValue("ePSet_MapConversion." + n, out var v) && v is not null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : d;
                CoordinateReferenceSystem? crs = props.TryGetValue("ePSet_ProjectedCRS.Name", out var cn) && cn is string s && CoordinateReferenceSystem.TryParse(s, out var c) ? c : null;
                return Make(P("Eastings", 0), P("Northings", 0), P("OrthogonalHeight", 0), P("XAxisAbscissa", 1), P("XAxisOrdinate", 0), P("Scale", 1), crs, "ePSet_MapConversion");
            }
        }
        // site latitude/longitude → the UTM zone of the site, rotated by true north
        var site = _step.Entities.Values.FirstOrDefault(e => e.Type == "IFCSITE");
        if (site?[9] is List<object?> lat && site[10] is List<object?> lon)
        {
            static double Dms(List<object?> v)
            {
                var n = v.Select(x => Num(x)).ToArray();
                return n[0] + (n.Length > 1 ? n[1] / 60 : 0) + (n.Length > 2 ? n[2] / 3600 : 0) + (n.Length > 3 ? n[3] / 3.6e9 : 0);
            }
            double la = Dms(lat), lo = Dms(lon), elev = Num(site[11]) * LengthUnit;
            int zone = Math.Clamp((int)Math.Floor((lo + 180) / 6) + 1, 1, 60);
            var utm = CoordinateReferenceSystem.FromEpsg((la < 0 ? 32700 : 32600) + zone);
            var (e0, n0) = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, utm).Transform(lo, la);
            // the site placement's origin is the reference point; true north rotates the local y axis
            double trueNorth = 0;
            var ctx = _step.Entities.Values.FirstOrDefault(e => e.Type == "IFCGEOMETRICREPRESENTATIONCONTEXT" && e[5] is StepRef);
            if (ctx is not null && E(ctx[5]) is { } tn) { var d = Nums(tn[0]); trueNorth = Math.Atan2(d[0], d[1]); }
            var sitePlacement = Placement(site[5]);
            var o = sitePlacement.Apply(0, 0, 0);
            var g = new IfcGeoreference(e0, n0, elev, trueNorth, 1, utm, "IfcSite RefLatitude/RefLongitude");
            var (ex, ny, _) = g.ToMap(o.X * LengthUnit, o.Y * LengthUnit, 0);
            return g with { Eastings = 2 * e0 - ex, Northings = 2 * n0 - ny };
        }
        return null;

        IfcGeoreference Make(double e, double n, double h, double xa, double xo, double scale, CoordinateReferenceSystem? crs, string src) =>
            new(e, n, h, Math.Atan2(xo, xa), scale / LengthUnit, crs, src);
    }

    // ------------------------------------------------------------------ properties

    /// <summary>Property and quantity values of an object (and its type), keyed "SetName.PropertyName".</summary>
    public Dictionary<string, object?> Properties(long objectId)
    {
        var res = new Dictionary<string, object?>(StringComparer.Ordinal);
        void FromSet(StepEntity set)
        {
            string setName = set[2] as string ?? "";
            if (set.Type == "IFCPROPERTYSET")
                foreach (var p in Refs(set[4]).Select(E))
                {
                    if (p.Type == "IFCPROPERTYSINGLEVALUE") res[$"{setName}.{p[0]}"] = Value(p[2]);
                    else if (p.Type == "IFCPROPERTYENUMERATEDVALUE") res[$"{setName}.{p[0]}"] = p[2] is List<object?> l ? string.Join(";", l.Select(Value)) : null;
                }
            else if (set.Type == "IFCELEMENTQUANTITY")
                foreach (var q in Refs(set[5]).Select(E))
                    res[$"{setName}.{q[0]}"] = q.Type switch
                    {
                        "IFCQUANTITYLENGTH" => Num(q[3]) * LengthUnit,
                        "IFCQUANTITYAREA" => Num(q[3]),
                        "IFCQUANTITYVOLUME" => Num(q[3]),
                        _ => Num(q[3])
                    };
        }
        if (_typeOf.TryGetValue(objectId, out var typeId) && E(typeId)[5] is List<object?> typeSets)
            foreach (var s in typeSets.OfType<StepRef>().Select(r => E(r.Id))) FromSet(s);
        foreach (var d in _propertyDefs.GetValueOrDefault(objectId) ?? []) FromSet(E(d));
        return res;
    }

    private static object? Value(object? v) => v switch
    {
        StepTyped t => Value(t.Value),
        StepEnum { Value: "T" } => true,
        StepEnum { Value: "F" } => false,
        StepEnum e => e.Value,
        long l => l,
        double d => d,
        string s => s,
        _ => null
    };

    // ------------------------------------------------------------------ spatial structure

    public IReadOnlyList<IfcStorey> Storeys => _step.Entities.Values.Where(e => e.Type == "IFCBUILDINGSTOREY").Select(e =>
        new IfcStorey(e.Id, e[0] as string ?? "", e[2] as string, e[9] is null ? Placement(e[5]).Apply(0, 0, 0).Z * LengthUnit : Num(e[9]) * LengthUnit,
            _aggregatedBy.TryGetValue(e.Id, out var b) ? E(b)[2] as string : null)).OrderBy(s => s.Elevation).ToList();

    private string? StoreyOf(long id)
    {
        for (int guard = 0; guard < 32; guard++)
        {
            long parent = _containedIn.TryGetValue(id, out var l) ? l[0] : _aggregatedBy.TryGetValue(id, out var a) ? a : -1;
            if (parent < 0) return null;
            var p = E(parent);
            if (p.Type == "IFCBUILDINGSTOREY") return p[2] as string;
            id = parent;
        }
        return null;
    }

    // ------------------------------------------------------------------ placement

    /// <summary>World transform of an IfcObjectPlacement (project units).</summary>
    private Mat4 Placement(object? placement)
    {
        var p = E(placement);
        if (p is null) return Mat4.Identity;
        if (p.Type == "IFCLOCALPLACEMENT") return Placement(p[0]) * Axis(E(p[1]));
        if (p.Type == "IFCGRIDPLACEMENT") return Mat4.Identity;
        return Mat4.Identity;
    }

    private (double X, double Y, double Z) Point(object? v)
    {
        var e = E(v);
        if (e is null) return (0, 0, 0);
        var c = Nums(e[0]);
        return (c[0], c.Length > 1 ? c[1] : 0, c.Length > 2 ? c[2] : 0);
    }

    private double[]? Direction(object? v) => E(v) is { } e ? Nums(e[0]) : null;

    /// <summary>IfcAxis2Placement3D/2D → matrix.</summary>
    private Mat4 Axis(StepEntity? a)
    {
        if (a is null) return Mat4.Identity;
        var (x, y, z) = Point(a[0]);
        if (a.Type == "IFCAXIS2PLACEMENT2D")
        {
            var rd = Direction(a[1]) ?? [1, 0];
            return Mat4.FromAxes([rd[0], rd[1], 0], [0, 0, 1], (x, y, 0));
        }
        var zAxis = Direction(a[1]) ?? [0, 0, 1];
        var xAxis = Direction(a[2]) ?? [1, 0, 0];
        return Mat4.FromAxes(xAxis, zAxis, (x, y, z));
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Polygons (3-D rings: outer + holes) of an element in world project units.</summary>
    private List<List<(double X, double Y, double Z)[]>> Faces(StepEntity product, out double volume)
    {
        volume = 0;
        var faces = new List<List<(double X, double Y, double Z)[]>>();
        var world = Placement(product[5]);
        var shape = E(product[6]);
        if (shape is null) return faces;
        var reps = Refs(shape[2]).Select(E).ToList();
        var body = reps.FirstOrDefault(r => r[1] as string is "Body" or "Facetation" or "Brep" or "Tessellation")
                   ?? reps.FirstOrDefault(r => r[1] as string is not ("Axis" or "FootPrint" or "Box" or "Annotation" or "Profile"))
                   ?? reps.FirstOrDefault(r => r[1] as string == "Box");
        if (body is null) return faces;
        double vol = 0;
        foreach (var item in Refs(body[3]).Select(E)) Item(item, world, faces, ref vol);
        volume = vol;
        return faces;
    }

    private void Item(StepEntity item, Mat4 m, List<List<(double X, double Y, double Z)[]>> faces, ref double volume)
    {
        switch (item.Type)
        {
            case "IFCEXTRUDEDAREASOLID":
            case "IFCEXTRUDEDAREASOLIDTAPERED":
            {
                var rings = Profile(E(item[0]));
                if (rings.Count == 0) return;
                var pos = m * Axis(E(item[1]));
                var d = Direction(item[2]) ?? [0, 0, 1];
                double depth = Num(item[3]);
                double dl = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                var ext = (d[0] / dl * depth, d[1] / dl * depth, d[2] / dl * depth);
                var bottom = rings.Select(r => r.Select(p => pos.Apply(p.X, p.Y, 0)).ToArray()).ToList();
                var top = rings.Select(r => r.Select(p => pos.Apply(p.X + ext.Item1, p.Y + ext.Item2, ext.Item3)).ToArray()).ToList();
                faces.Add(bottom); faces.Add(top);
                foreach (var r in rings)
                    for (int i = 0; i < r.Length; i++)
                    {
                        var a = r[i]; var b = r[(i + 1) % r.Length];
                        faces.Add([[pos.Apply(a.X, a.Y, 0), pos.Apply(b.X, b.Y, 0), pos.Apply(b.X + ext.Item1, b.Y + ext.Item2, ext.Item3), pos.Apply(a.X + ext.Item1, a.Y + ext.Item2, ext.Item3)]]);
                    }
                double area = Math.Abs(SignedArea(rings[0])) - rings.Skip(1).Sum(r => Math.Abs(SignedArea(r)));
                volume += area * Math.Abs(ext.Item3) * pos.Determinant;
                break;
            }
            case "IFCFACETEDBREP":
            case "IFCFACETEDBREPWITHVOIDS":
            case "IFCADVANCEDBREP":
                Shell(E(item[0]), m, faces, ref volume, closed: true);
                break;
            case "IFCFACEBASEDSURFACEMODEL":
                foreach (var s in Refs(item[0]).Select(E)) Shell(s, m, faces, ref volume, closed: false);
                break;
            case "IFCSHELLBASEDSURFACEMODEL":
                foreach (var s in Refs(item[0]).Select(E)) Shell(s, m, faces, ref volume, closed: false);
                break;
            case "IFCTRIANGULATEDFACESET":
            case "IFCTRIANGULATEDIRREGULARNETWORK":
            {
                var coords = CoordList(item[0]);
                var pn = item.Args.Count > 4 && item[4] is List<object?> pl ? pl.Select(x => (int)Num(x)).ToArray() : null;
                var tris = new List<(double X, double Y, double Z)[]>();
                foreach (var t in (item[3] as List<object?>) ?? [])
                {
                    var ix = Nums(t).Select(v => (int)v).ToArray();
                    var tri = ix.Select(k => coords[(pn is null ? k : pn[k - 1]) - 1]).Select(p => m.Apply(p.X, p.Y, p.Z)).ToArray();
                    tris.Add(tri);
                    faces.Add([tri]);
                }
                volume += MeshVolume(tris);
                break;
            }
            case "IFCPOLYGONALFACESET":
            {
                var coords = CoordList(item[0]);
                var pn = item.Args.Count > 3 && item[3] is List<object?> pl ? pl.Select(x => (int)Num(x)).ToArray() : null;
                var tris = new List<(double X, double Y, double Z)[]>();
                foreach (var f in Refs(item[2]).Select(E))
                {
                    var rings = new List<(double X, double Y, double Z)[]>();
                    var lists = new List<object?> { f[0] };
                    if (f.Type == "IFCINDEXEDPOLYGONALFACEWITHVOIDS" && f[1] is List<object?> inner) lists.AddRange(inner);
                    foreach (var l in lists)
                        rings.Add(Nums(l).Select(v => coords[(pn is null ? (int)v : pn[(int)v - 1]) - 1]).Select(p => m.Apply(p.X, p.Y, p.Z)).ToArray());
                    faces.Add(rings);
                    tris.AddRange(Fan(rings[0]));
                }
                volume += MeshVolume(tris);
                break;
            }
            case "IFCMAPPEDITEM":
            {
                var map = E(item[0])!;
                var origin = Axis(E(map[0]));
                var target = Operator(E(item[1]));
                var rep = E(map[1])!;
                foreach (var sub in Refs(rep[3]).Select(E)) Item(sub, m * target * origin, faces, ref volume);
                break;
            }
            case "IFCBOOLEANCLIPPINGRESULT":
            case "IFCBOOLEANRESULT":
                if (E(item[1]) is { } first) Item(first, m, faces, ref volume);   // clipping is not evaluated (upper bound)
                break;
            case "IFCBOUNDINGBOX":
            {
                var (x, y, z) = Point(item[0]);
                double dx = Num(item[1]), dy = Num(item[2]), dz = Num(item[3]);
                var ring = new[] { (x, y), (x + dx, y), (x + dx, y + dy), (x, y + dy) };
                faces.Add([ring.Select(p => m.Apply(p.Item1, p.Item2, z)).ToArray()]);
                faces.Add([ring.Select(p => m.Apply(p.Item1, p.Item2, z + dz)).ToArray()]);
                volume += dx * dy * dz * m.Determinant;
                break;
            }
        }
    }

    private void Shell(StepEntity? shell, Mat4 m, List<List<(double X, double Y, double Z)[]>> faces, ref double volume, bool closed)
    {
        if (shell is null) return;
        var tris = new List<(double X, double Y, double Z)[]>();
        foreach (var face in Refs(shell[0]).Select(E))
        {
            var rings = new List<(double X, double Y, double Z)[]>();
            foreach (var bound in Refs(face[0]).Select(E).OrderBy(b => b.Type == "IFCFACEOUTERBOUND" ? 0 : 1))
            {
                var loop = E(bound[0]);
                if (loop?.Type != "IFCPOLYLOOP") continue;
                var pts = Refs(loop[0]).Select(r => { var (x, y, z) = Point(new StepRef(r)); return m.Apply(x, y, z); }).ToArray();
                if (bound[1] is StepEnum { Value: "F" }) Array.Reverse(pts);
                rings.Add(pts);
            }
            if (rings.Count == 0) continue;
            faces.Add(rings);
            tris.AddRange(Fan(rings[0]));
        }
        if (closed) volume += MeshVolume(tris);
    }

    private (double X, double Y, double Z)[] CoordList(object? v)
    {
        var e = E(v);
        return e?[0] is List<object?> l ? l.Select(p => { var n = Nums(p); return (n[0], n[1], n.Length > 2 ? n[2] : 0.0); }).ToArray() : [];
    }

    private Mat4 Operator(StepEntity? op)
    {
        if (op is null) return Mat4.Identity;
        var x = Direction(op[0]) ?? [1, 0, 0];
        var (ox, oy, oz) = Point(op[2]);
        double s = op[3] is null ? 1 : Num(op[3], 1);
        if (op.Type.StartsWith("IFCCARTESIANTRANSFORMATIONOPERATOR2D", StringComparison.Ordinal))
            return Mat4.FromAxes([x[0], x[1], 0], [0, 0, 1], (ox, oy, 0)).Scaled(s);
        var z = (op.Args.Count > 4 ? Direction(op[4]) : null) ?? [0, 0, 1];
        return Mat4.FromAxes(x, z, (ox, oy, oz)).Scaled(s);
    }

    /// <summary>Profile rings (outer first, then voids) in the profile's 2-D coordinates.</summary>
    private List<(double X, double Y)[]> Profile(StepEntity? p)
    {
        var res = new List<(double X, double Y)[]>();
        if (p is null) return res;
        Mat4 pos = p.Args.Count > 2 && p[2] is StepRef ? Axis(E(p[2])) : Mat4.Identity;
        (double X, double Y)[] Tx(IEnumerable<(double X, double Y)> pts) => pts.Select(q => { var r = pos.Apply(q.X, q.Y, 0); return (r.X, r.Y); }).ToArray();
        static IEnumerable<(double, double)> Ellipse(double a, double b, int n = 32) => Enumerable.Range(0, n).Select(i => (a * Math.Cos(2 * Math.PI * i / n), b * Math.Sin(2 * Math.PI * i / n)));
        static IEnumerable<(double, double)> Rect(double w, double h) => [(-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2)];
        switch (p.Type)
        {
            case "IFCRECTANGLEPROFILEDEF":
            case "IFCROUNDEDRECTANGLEPROFILEDEF":
                res.Add(Tx(Rect(Num(p[3]), Num(p[4])))); break;
            case "IFCRECTANGLEHOLLOWPROFILEDEF":
            {
                double w = Num(p[3]), h = Num(p[4]), t = Num(p[5]);
                res.Add(Tx(Rect(w, h))); res.Add(Tx(Rect(w - 2 * t, h - 2 * t)));
                break;
            }
            case "IFCCIRCLEPROFILEDEF": res.Add(Tx(Ellipse(Num(p[3]), Num(p[3])))); break;
            case "IFCCIRCLEHOLLOWPROFILEDEF":
                res.Add(Tx(Ellipse(Num(p[3]), Num(p[3])))); res.Add(Tx(Ellipse(Num(p[3]) - Num(p[4]), Num(p[3]) - Num(p[4])))); break;
            case "IFCELLIPSEPROFILEDEF": res.Add(Tx(Ellipse(Num(p[3]), Num(p[4])))); break;
            case "IFCISHAPEPROFILEDEF":
            {
                double b = Num(p[3]), d = Num(p[4]), tw = Num(p[5]), tf = Num(p[6]);
                res.Add(Tx([(-b / 2, -d / 2), (b / 2, -d / 2), (b / 2, -d / 2 + tf), (tw / 2, -d / 2 + tf), (tw / 2, d / 2 - tf), (b / 2, d / 2 - tf), (b / 2, d / 2),
                    (-b / 2, d / 2), (-b / 2, d / 2 - tf), (-tw / 2, d / 2 - tf), (-tw / 2, -d / 2 + tf), (-b / 2, -d / 2 + tf)]));
                break;
            }
            case "IFCARBITRARYCLOSEDPROFILEDEF":
            case "IFCARBITRARYPROFILEDEFWITHVOIDS":
                res.Add(Curve(E(p[2])));
                if (p.Type == "IFCARBITRARYPROFILEDEFWITHVOIDS") res.AddRange(Refs(p[3]).Select(r => Curve(E(r))));
                break;
            case "IFCDERIVEDPROFILEDEF":
            {
                var parent = Profile(E(p[2]));
                var op = Operator(E(p[3]));
                res.AddRange(parent.Select(r => r.Select(q => { var t = op.Apply(q.X, q.Y, 0); return (t.X, t.Y); }).ToArray()));
                break;
            }
        }
        return res.Where(r => r.Length >= 3).ToList();
    }

    /// <summary>A closed curve as a 2-D point ring (arcs densified).</summary>
    private (double X, double Y)[] Curve(StepEntity? c)
    {
        if (c is null) return [];
        switch (c.Type)
        {
            case "IFCPOLYLINE":
            {
                var pts = Refs(c[0]).Select(r => { var (x, y, _) = Point(new StepRef(r)); return (x, y); }).ToList();
                if (pts.Count > 1 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
                return [.. pts];
            }
            case "IFCINDEXEDPOLYCURVE":
            {
                var coords = CoordList(c[0]).Select(p => (p.X, p.Y)).ToArray();
                var pts = new List<(double X, double Y)>();
                if (c[1] is List<object?> segs)
                    foreach (var seg in segs.OfType<StepTyped>())
                    {
                        var ix = (seg.Value is List<object?> l ? l : [seg.Value]).Select(v => (int)Num(v) - 1).ToArray();
                        if (seg.Type == "IFCARCINDEX" && ix.Length == 3) pts.AddRange(Arc(coords[ix[0]], coords[ix[1]], coords[ix[2]]));
                        else pts.AddRange(ix.Select(i => coords[i]));
                    }
                else pts.AddRange(coords);
                var dedup = pts.Where((p, i) => i == 0 || p != pts[i - 1]).ToList();
                if (dedup.Count > 1 && dedup[0] == dedup[^1]) dedup.RemoveAt(dedup.Count - 1);
                return [.. dedup];
            }
            case "IFCCOMPOSITECURVE":
            {
                var pts = new List<(double X, double Y)>();
                foreach (var seg in Refs(c[0]).Select(E))
                {
                    var part = Curve(E(seg[2])).ToList();
                    if (seg[1] is StepEnum { Value: "F" }) part.Reverse();
                    pts.AddRange(part);
                }
                return [.. pts];
            }
            case "IFCCIRCLE":
            {
                var pos = Axis(E(c[0]));
                double r = Num(c[1]);
                return Enumerable.Range(0, 32).Select(i => { var p = pos.Apply(r * Math.Cos(Math.PI * i / 16), r * Math.Sin(Math.PI * i / 16), 0); return (p.X, p.Y); }).ToArray();
            }
            case "IFCTRIMMEDCURVE":
                return Curve(E(c[0]));
        }
        return [];
    }

    private static IEnumerable<(double X, double Y)> Arc((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, int n = 16)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) { yield return a; yield return b; yield return c; yield break; }
        double ux = ((a.X * a.X + a.Y * a.Y) * (b.Y - c.Y) + (b.X * b.X + b.Y * b.Y) * (c.Y - a.Y) + (c.X * c.X + c.Y * c.Y) * (a.Y - b.Y)) / d;
        double uy = ((a.X * a.X + a.Y * a.Y) * (c.X - b.X) + (b.X * b.X + b.Y * b.Y) * (a.X - c.X) + (c.X * c.X + c.Y * c.Y) * (b.X - a.X)) / d;
        double r = Math.Sqrt((a.X - ux) * (a.X - ux) + (a.Y - uy) * (a.Y - uy));
        double t0 = Math.Atan2(a.Y - uy, a.X - ux), t1 = Math.Atan2(b.Y - uy, b.X - ux), t2 = Math.Atan2(c.Y - uy, c.X - ux);
        double Norm(double t) { while (t < 0) t += 2 * Math.PI; return t; }
        double sweep = Norm(t2 - t0);
        if (Norm(t1 - t0) > sweep) sweep -= 2 * Math.PI;      // b must lie on the arc
        for (int i = 0; i <= n; i++) { double t = t0 + sweep * i / n; yield return (ux + r * Math.Cos(t), uy + r * Math.Sin(t)); }
    }

    private static double SignedArea((double X, double Y)[] r)
    {
        double s = 0;
        for (int i = 0; i < r.Length; i++) { var a = r[i]; var b = r[(i + 1) % r.Length]; s += a.X * b.Y - b.X * a.Y; }
        return s / 2;
    }

    private static IEnumerable<(double X, double Y, double Z)[]> Fan((double X, double Y, double Z)[] ring)
    {
        for (int i = 1; i + 1 < ring.Length; i++) yield return [ring[0], ring[i], ring[i + 1]];
    }

    private static double MeshVolume(List<(double X, double Y, double Z)[]> tris)
    {
        double v = 0;
        foreach (var t in tris)
        {
            var (a, b, c) = (t[0], t[1], t[2]);
            v += a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
        }
        return Math.Abs(v / 6);
    }

    // ------------------------------------------------------------------ elements

    private static readonly HashSet<string> NotElements = ["IFCPROJECT", "IFCSITE", "IFCBUILDING", "IFCBUILDINGSTOREY", "IFCSPACE", "IFCGRID", "IFCANNOTATION",
        "IFCFACILITY", "IFCFACILITYPART", "IFCBRIDGE", "IFCROAD", "IFCRAILWAY", "IFCMARINEFACILITY", "IFCOPENINGELEMENT", "IFCOPENINGSTANDARDCASE"];

    /// <summary>Products with a body representation, with footprints in local metres (see <see cref="ElementsFrame"/> for map coordinates).</summary>
    public IReadOnlyList<IfcElement> Elements => _elements ??= BuildElements(includeSpaces: false);

    /// <summary>Spaces (rooms) with their footprints and properties.</summary>
    public IReadOnlyList<IfcElement> Spaces => BuildElements(includeSpaces: true).Where(e => e.IfcType == "IFCSPACE").ToList();

    private List<IfcElement> BuildElements(bool includeSpaces)
    {
        var list = new List<IfcElement>();
        foreach (var e in _step.Entities.Values)
        {
            if ((NotElements.Contains(e.Type) && !(includeSpaces && e.Type == "IFCSPACE")) || e.Args.Count < 7) continue;
            if (E(e[6]) is not { Type: "IFCPRODUCTDEFINITIONSHAPE" }) continue;
            if (e[5] is not null && E(e[5]) is not { Type: "IFCLOCALPLACEMENT" or "IFCGRIDPLACEMENT" or "IFCLINEARPLACEMENT" }) continue;
            var faces = Faces(e, out double vol);
            NtsGeometry? foot = null;
            double zmin = double.NaN, zmax = double.NaN;
            if (faces.Count > 0)
            {
                var polys = new List<NtsGeometry>();
                foreach (var f in faces)
                {
                    var shell = f[0];
                    foreach (var p in f.SelectMany(r => r)) { zmin = double.IsNaN(zmin) ? p.Z : Math.Min(zmin, p.Z); zmax = double.IsNaN(zmax) ? p.Z : Math.Max(zmax, p.Z); }
                    if (Math.Abs(SignedArea(shell.Select(p => (p.X, p.Y)).ToArray())) * LengthUnit * LengthUnit < 1e-8) continue;
                    var rings = f.Select(r => Geo.Factory.CreateLinearRing(r.Select(p => new Coordinate(p.X * LengthUnit, p.Y * LengthUnit)).Append(new Coordinate(r[0].X * LengthUnit, r[0].Y * LengthUnit)).ToArray())).ToArray();
                    NtsGeometry poly = Geo.Factory.CreatePolygon(rings[0], rings.Skip(1).Where(h => h.NumPoints >= 4).ToArray());
                    if (!poly.IsValid) poly = poly.Buffer(0);
                    if (!poly.IsEmpty) polys.Add(poly);
                }
                if (polys.Count > 0) foot = CascadedPolygonUnion.Union(polys);
            }
            list.Add(new IfcElement(e.Id, e[0] as string ?? "", e.Type, e[2] as string, e[4] as string, StoreyOf(e.Id), foot,
                zmin * LengthUnit, zmax * LengthUnit, vol * LengthUnit * LengthUnit * LengthUnit, Properties(e.Id)));
        }
        return list;
    }

    private NtsGeometry? ToMap(NtsGeometry? g)
    {
        if (g is null || Georeference is not { } geo) return g;
        var copy = g.Copy();
        copy.Apply(new MapFilter(geo));
        copy.GeometryChanged();
        return copy;
    }

    private sealed class MapFilter(IfcGeoreference geo) : ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;
        public void Filter(CoordinateSequence seq, int i)
        {
            var (e, n, _) = geo.ToMap(seq.GetX(i), seq.GetY(i), 0);
            seq.SetX(i, e); seq.SetY(i, n);
        }
    }

    /// <summary>
    /// Elements as a GeoDataFrame: footprint (map coordinates when the model is georeferenced), IFC type, ids, storey, z range and height
    /// (map heights), volume, and every property/quantity as a column ("Pset_WallCommon.IsExternal", …).
    /// </summary>
    public GeoDataFrame ElementsFrame(bool includeProperties = true, bool mapCoordinates = true) => Frame(Elements, includeProperties, mapCoordinates);

    public GeoDataFrame SpacesFrame(bool includeProperties = true, bool mapCoordinates = true) => Frame(Spaces, includeProperties, mapCoordinates);

    private GeoDataFrame Frame(IReadOnlyList<IfcElement> els, bool includeProperties, bool mapCoordinates)
    {
        var geo = mapCoordinates ? Georeference : null;
        double H(double z) => geo is null ? z : geo.ToMap(0, 0, z).H;
        var cols = new List<Column>
        {
            Column.Create("global_id", els.Select(e => e.GlobalId).ToArray()),
            Column.Create("ifc_type", els.Select(e => e.IfcType).ToArray()),
            Column.Create("name", els.Select(e => e.Name).ToArray()),
            Column.Create("object_type", els.Select(e => e.ObjectType).ToArray()),
            Column.Create("storey", els.Select(e => e.Storey).ToArray()),
            Column.Create("z_min", els.Select(e => H(e.ZMin)).ToArray()),
            Column.Create("z_max", els.Select(e => H(e.ZMax)).ToArray()),
            Column.Create("height", els.Select(e => e.ZMax - e.ZMin).ToArray()),
            Column.Create("volume", els.Select(e => e.Volume).ToArray()),
        };
        if (includeProperties)
            foreach (var key in els.SelectMany(e => e.Properties.Keys).Distinct().Order(StringComparer.Ordinal))
            {
                var vals = els.Select(e => e.Properties.GetValueOrDefault(key)).ToArray();
                if (vals.All(v => v is null or double or long)) cols.Add(Column<double>.FromNullable(key, vals.Select(v => v is null ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray()));
                else if (vals.All(v => v is null or bool)) cols.Add(Column.Create(key, vals.Select(v => v is true).ToArray()));
                else cols.Add(Column.Create(key, vals.Select(v => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray()));
            }
        return GeoDataFrame.FromGeometries(els.Select(e => mapCoordinates ? ToMap(e.Footprint) : e.Footprint), mapCoordinates ? Georeference?.Crs : null, cols.ToArray());
    }

    /// <summary>
    /// The building as a city-model feature (CityGML LOD1 style): footprint (union of element footprints), ground and roof heights, height,
    /// storey count, gross floor area (sum of the storeys' footprint areas) and element counts per IFC type.
    /// </summary>
    public GeoDataFrame BuildingFootprint(bool mapCoordinates = true)
    {
        var els = Elements.Where(e => e.Footprint is not null).ToList();
        var union = CascadedPolygonUnion.Union(els.Select(e => e.Footprint!).ToList()) ?? Geo.Factory.CreatePolygon();
        // close courtyards smaller than 1 m² that come from wall joints
        double zmin = els.Count == 0 ? double.NaN : els.Min(e => e.ZMin), zmax = els.Count == 0 ? double.NaN : els.Max(e => e.ZMax);
        var storeys = Storeys;
        double gfa = 0;
        foreach (var g in els.GroupBy(e => e.Storey))
            gfa += CascadedPolygonUnion.Union(g.Select(e => e.Footprint!.ConvexHull()).ToList())?.Area ?? 0;
        var geo = mapCoordinates ? Georeference : null;
        double H(double z) => geo is null ? z : geo.ToMap(0, 0, z).H;
        var building = _step.Entities.Values.FirstOrDefault(e => e.Type == "IFCBUILDING");
        var footprint = mapCoordinates ? ToMap(union) : union;
        return GeoDataFrame.FromGeometries([footprint], mapCoordinates ? Georeference?.Crs : null,
            Column.Create("name", new[] { building?[2] as string ?? ProjectName }),
            Column.Create("global_id", new[] { building?[0] as string }),
            Column.Create("ground_z", new[] { H(zmin) }), Column.Create("roof_z", new[] { H(zmax) }),
            Column.Create("height", new[] { zmax - zmin }), Column.Create("storeys", new[] { (long)storeys.Count }),
            Column.Create("gross_floor_area", new[] { gfa }), Column.Create("elements", new[] { (long)els.Count }),
            Column.Create("source", new[] { "IFC " + Schema }));
    }

    public override string ToString() => $"IfcModel {Schema}: {ProjectName}, {_step.Entities.Count} entities, {Elements.Count} elements, {Storeys.Count} storeys, unit {LengthUnit} m" +
        (Georeference is { } g ? $", georeferenced ({g.Source}{(g.Crs is null ? "" : ", " + g.Crs)})" : "");
}

/// <summary>A 4×4 affine transform (row-major, column vectors).</summary>
internal readonly struct Mat4
{
    private readonly double[] _m;
    private Mat4(double[] m) => _m = m;
    public static Mat4 Identity => new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

    /// <summary>From an X reference direction and a Z axis (IFC rules: X is made orthogonal to Z, Y = Z × X).</summary>
    public static Mat4 FromAxes(double[] xRef, double[] z, (double X, double Y, double Z) o)
    {
        double zl = Math.Sqrt(z[0] * z[0] + z[1] * z[1] + z[2] * z[2]);
        double z0 = z[0] / zl, z1 = z[1] / zl, z2 = (z.Length > 2 ? z[2] : 0) / zl;
        double x0 = xRef[0], x1 = xRef.Length > 1 ? xRef[1] : 0, x2 = xRef.Length > 2 ? xRef[2] : 0;
        double d = x0 * z0 + x1 * z1 + x2 * z2;
        x0 -= d * z0; x1 -= d * z1; x2 -= d * z2;
        double xl = Math.Sqrt(x0 * x0 + x1 * x1 + x2 * x2);
        if (xl < 1e-12) { (x0, x1, x2) = Math.Abs(z0) < 0.9 ? (1.0 - z0 * z0, -z0 * z1, -z0 * z2) : (-z1 * z0, 1.0 - z1 * z1, -z1 * z2); xl = Math.Sqrt(x0 * x0 + x1 * x1 + x2 * x2); }
        x0 /= xl; x1 /= xl; x2 /= xl;
        double y0 = z1 * x2 - z2 * x1, y1 = z2 * x0 - z0 * x2, y2 = z0 * x1 - z1 * x0;
        return new([x0, y0, z0, o.X, x1, y1, z1, o.Y, x2, y2, z2, o.Z, 0, 0, 0, 1]);
    }

    public Mat4 Scaled(double s)
    {
        var m = (double[])_m.Clone();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) m[r * 4 + c] *= s;
        return new(m);
    }

    public double Determinant
    {
        get
        {
            var m = _m;
            return Math.Abs(m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]));
        }
    }

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a._m[i * 4 + k] * b._m[k * 4 + j];
                r[i * 4 + j] = s;
            }
        return new(r);
    }

    public (double X, double Y, double Z) Apply(double x, double y, double z) =>
        (_m[0] * x + _m[1] * y + _m[2] * z + _m[3], _m[4] * x + _m[5] * y + _m[6] * z + _m[7], _m[8] * x + _m[9] * y + _m[10] * z + _m[11]);
}
