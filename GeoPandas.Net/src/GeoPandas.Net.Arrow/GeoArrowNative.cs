using GeoPandas.Net.Geometry;
using NetTopologySuite.Geometries;
using Parquet;
using Parquet.Schema;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Arrow;

/// <summary>GeoParquet geometry column encoding.</summary>
public enum GeoParquetEncoding
{
    /// <summary>Well-known binary (GeoParquet 1.0 default; any geometry type, mixed allowed).</summary>
    Wkb,
    /// <summary>Native GeoArrow struct/list layout (GeoParquet 1.1 "point", "linestring", "polygon", "multi…"): one geometry type per column, columnar coordinates.</summary>
    Native,
}

/// <summary>Reads and writes the native GeoArrow encodings of GeoParquet 1.1 through Parquet repetition / definition levels.</summary>
internal static class GeoArrowNative
{
    public static readonly string[] Encodings = ["point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon"];

    public static int Depth(string encoding) => encoding switch
    {
        "point" => 0, "linestring" or "multipoint" => 1, "polygon" or "multilinestring" => 2, "multipolygon" => 3,
        _ => throw new NotSupportedException($"GeoArrow encoding '{encoding}' is not supported.")
    };

    /// <summary>Picks the native encoding for a series (Polygon + MultiPolygon → multipolygon, etc.), or throws for incompatible mixes.</summary>
    public static string EncodingFor(GeoSeries s)
    {
        var types = s.NonNull().Select(g => g!.OgcGeometryType).Distinct().ToHashSet();
        if (types.Count == 0) return "point";
        if (types.SetEquals([OgcGeometryType.Point])) return "point";
        if (types.SetEquals([OgcGeometryType.LineString])) return "linestring";
        if (types.SetEquals([OgcGeometryType.Polygon])) return "polygon";
        if (types.IsSubsetOf([OgcGeometryType.Point, OgcGeometryType.MultiPoint])) return "multipoint";
        if (types.IsSubsetOf([OgcGeometryType.LineString, OgcGeometryType.MultiLineString])) return "multilinestring";
        if (types.IsSubsetOf([OgcGeometryType.Polygon, OgcGeometryType.MultiPolygon])) return "multipolygon";
        throw new NotSupportedException($"Native GeoArrow needs one geometry family per column, found {string.Join(", ", types)}; use WKB encoding.");
    }

    public static Field SchemaField(string name, string encoding, bool hasZ, out DataField[] leaves)
    {
        var x = new DataField<double>("x"); var y = new DataField<double>("y");
        Field coord = hasZ ? new StructField("xyz", x, y, new DataField<double>("z")) : new StructField("xy", x, y);
        int depth = Depth(encoding);
        Field f = coord;
        for (int d = 0; d < depth; d++) f = new ListField(d == depth - 1 ? name : "element", f);
        if (depth == 0) f = hasZ ? new StructField(name, x, y, new DataField<double>("z")) : new StructField(name, x, y);
        leaves = hasZ ? [x, y, (DataField)((StructField)(depth == 0 ? f : coord)).Fields[2]] : [x, y];
        return f;
    }

    /// <summary>Nested coordinate lists of one geometry (depth = list levels).</summary>
    private static List<object> Nest(NtsGeometry g, string encoding)
    {
        List<object> Coords(Coordinate[] cs) => cs.Select(c => (object)c).ToList();
        List<object> Poly(Polygon p) => [Coords(p.ExteriorRing.Coordinates), .. p.InteriorRings.Select(r => (object)Coords(r.Coordinates))];
        return encoding switch
        {
            "linestring" => Coords(g.Coordinates),
            "multipoint" => g.Coordinates.Select(c => (object)c).ToList(),
            "polygon" => Poly((Polygon)g),
            "multilinestring" => Enumerable.Range(0, g.NumGeometries).Select(i => (object)Coords(g.GetGeometryN(i).Coordinates)).ToList(),
            "multipolygon" => Enumerable.Range(0, g.NumGeometries).Select(i => (object)Poly((Polygon)g.GetGeometryN(i))).ToList(),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>Writes one row group of native geometries (values + definition / repetition levels for each coordinate leaf).</summary>
    public static async Task WriteAsync(ParquetRowGroupWriter rg, DataField[] leaves, string encoding, NtsGeometry?[] geoms, int maxDef)
    {
        int depth = Depth(encoding);
        var xs = new List<double>(); var ys = new List<double>(); var zs = new List<double>();
        var def = new List<int>(); var rep = new List<int>();
        foreach (var g0 in geoms)
        {
            var g = g0;
            if (encoding.StartsWith("multi") && g is not null && g is not GeometryCollection)
                g = encoding switch
                {
                    "multipoint" => Geo.Factory.CreateMultiPoint([(Point)g]),
                    "multilinestring" => Geo.Factory.CreateMultiLineString([(LineString)g]),
                    _ => Geo.Factory.CreateMultiPolygon([(Polygon)g])
                };
            if (g is null || g.IsEmpty) { def.Add(0); rep.Add(0); continue; }
            if (depth == 0)
            {
                var c = g.Coordinate;
                xs.Add(c.X); ys.Add(c.Y); zs.Add(double.IsNaN(c.Z) ? 0 : c.Z); def.Add(maxDef);
                continue;
            }
            // walk the nested lists; the repetition level of a coordinate is the depth of the list in which a new element starts
            void Walk(List<object> list, int level, int startRep)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    int r = i == 0 ? startRep : level + 1;
                    if (list[i] is Coordinate c)
                    {
                        xs.Add(c.X); ys.Add(c.Y); zs.Add(double.IsNaN(c.Z) ? 0 : c.Z); def.Add(maxDef); rep.Add(r);
                    }
                    else Walk((List<object>)list[i], level + 1, r);
                }
            }
            Walk(Nest(g, encoding), 0, 0);
        }
        int[] defs = def.ToArray(); int[]? reps = depth == 0 ? null : rep.ToArray();
        await rg.WriteAllPartsAsync<double>(leaves[0], xs.ToArray(), defs, reps, default).ConfigureAwait(false);
        await rg.WriteAllPartsAsync<double>(leaves[1], ys.ToArray(), defs, reps, default).ConfigureAwait(false);
        if (leaves.Length > 2) await rg.WriteAllPartsAsync<double>(leaves[2], zs.ToArray(), defs, reps, default).ConfigureAwait(false);
    }

    /// <summary>Reads native geometries of one row group from the x / y (/ z) leaves.</summary>
    public static async Task<NtsGeometry?[]> ReadAsync(ParquetRowGroupReader rg, string encoding, DataField xf, DataField yf, DataField? zf, int rows)
    {
        int depth = Depth(encoding);
        int maxDef = xf.MaxDefinitionLevel;
        var (xv, def, rep) = await Raw(rg, xf).ConfigureAwait(false);
        var (yv, _, _) = await Raw(rg, yf).ConfigureAwait(false);
        double[]? zv = zf is null ? null : (await Raw(rg, zf).ConfigureAwait(false)).Values;
        if (maxDef == 0) def = new int[xv.Length];
        if (xf.MaxRepetitionLevel == 0) rep = new int[def.Length];
        var f = Geo.Factory;
        var result = new NtsGeometry?[rows];
        int row = -1, vi = 0;
        List<object>? root = null;
        var stack = new List<object>[Math.Max(depth, 1)];
        void Finish()
        {
            if (row >= 0 && row < rows && root is not null) result[row] = Build(root, encoding, f);
        }
        for (int i = 0; i < def.Length; i++)
        {
            bool present = maxDef == 0 || def[i] == maxDef;
            Coordinate? c = null;
            if (present) { c = zv is not null ? new CoordinateZ(xv[vi], yv[vi], zv[vi]) : new Coordinate(xv[vi], yv[vi]); vi++; }
            if (rep[i] == 0)
            {
                Finish();
                row++;
                root = null;
                if (depth == 0) { if (row < rows) result[row] = c is null ? null : f.CreatePoint(c); continue; }
                if (def[i] == 0 && maxDef > 0) continue; // null geometry
                root = [];
                stack[0] = root;
                for (int d = 1; d < depth; d++) { var l = new List<object>(); stack[d - 1].Add(l); stack[d] = l; }
                if (c is not null) stack[depth - 1].Add(c);
                continue;
            }
            if (root is null) continue;
            int r = rep[i];
            for (int d = r; d < depth; d++) { var l = new List<object>(); stack[d - 1].Add(l); stack[d] = l; }
            if (c is not null) stack[depth - 1].Add(c);
        }
        Finish();
        return result;
    }

    private static async Task<(double[] Values, int[] Def, int[] Rep)> Raw(ParquetRowGroupReader rg, DataField f)
    {
        long levels = rg.GetMetadata(f)?.MetaData?.NumValues ?? rg.RowCount;
        var values = new double[levels];
        var def = new int[levels];
        var rep = new int[levels];
        await rg.ReadRawAsync<double>(f, values.AsMemory(), def.AsMemory(), rep.AsMemory(), default).ConfigureAwait(false);
        int present = f.MaxDefinitionLevel == 0 ? (int)levels : def.Count(d => d == f.MaxDefinitionLevel);
        return (values[..Math.Min(present, values.Length)], def, rep);
    }

    private static NtsGeometry Build(List<object> root, string encoding, GeometryFactory f)
    {
        Coordinate[] Cs(object o) => ((List<object>)o).Cast<Coordinate>().ToArray();
        Polygon Poly(object o)
        {
            var rings = ((List<object>)o).Select(r => f.CreateLinearRing(Cs(r))).ToArray();
            return rings.Length == 0 ? f.CreatePolygon() : f.CreatePolygon(rings[0], rings[1..]);
        }
        return encoding switch
        {
            "linestring" => f.CreateLineString(root.Cast<Coordinate>().ToArray()),
            "multipoint" => f.CreateMultiPointFromCoords(root.Cast<Coordinate>().ToArray()),
            "polygon" => Poly(root),
            "multilinestring" => f.CreateMultiLineString(root.Select(l => f.CreateLineString(Cs(l))).ToArray()),
            "multipolygon" => f.CreateMultiPolygon(root.Select(Poly).ToArray()),
            _ => throw new InvalidOperationException()
        };
    }
}
