using System.Globalization;
using System.Text;
using System.Text.Json;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Data;

/// <summary>RFC 7946 GeoJSON reader / writer (FeatureCollection, Feature or bare geometry).</summary>
public static class GeoJson
{
    public static GeoDataFrame ReadFile(string path) => Read(File.ReadAllText(path));

    /// <summary>Parses GeoJSON text into a GeoDataFrame (CRS defaults to WGS 84 per RFC 7946).</summary>
    public static GeoDataFrame Read(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var crs = ReadLegacyCrs(root) ?? CoordinateReferenceSystem.Wgs84;
        var type = root.GetProperty("type").GetString();

        var features = new List<JsonElement>();
        if (type == "FeatureCollection") features.AddRange(root.GetProperty("features").EnumerateArray());
        else if (type == "Feature") features.Add(root);
        else
        {
            var g = ParseGeometry(root);
            return GeoDataFrame.FromGeometries([g], crs);
        }

        var geoms = new NtsGeometry?[features.Count];
        var props = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        var order = new List<string>();
        bool hasId = false;
        var ids = new object?[features.Count];
        for (int i = 0; i < features.Count; i++)
        {
            var f = features[i];
            geoms[i] = f.TryGetProperty("geometry", out var ge) && ge.ValueKind == JsonValueKind.Object ? ParseGeometry(ge) : null;
            if (f.TryGetProperty("id", out var idEl)) { hasId = true; ids[i] = ToValue(idEl); }
            if (!f.TryGetProperty("properties", out var pe) || pe.ValueKind != JsonValueKind.Object) continue;
            foreach (var p in pe.EnumerateObject())
            {
                if (!props.TryGetValue(p.Name, out var arr))
                {
                    arr = new object?[features.Count];
                    props[p.Name] = arr;
                    order.Add(p.Name);
                }
                arr[i] = ToValue(p.Value);
            }
        }
        var cols = new List<Column>();
        if (hasId && !props.ContainsKey("id")) cols.Add(Column.FromObjects("id", ids));
        foreach (var name in order) cols.Add(Column.FromObjects(name == GeoDataFrame.DefaultGeometryColumn ? name + "_prop" : name, props[name]));
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, geoms, crs));
        return new GeoDataFrame(cols);
    }

    private static CoordinateReferenceSystem? ReadLegacyCrs(JsonElement root)
    {
        if (!root.TryGetProperty("crs", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (c.TryGetProperty("properties", out var p) && p.TryGetProperty("name", out var n) && n.GetString() is { } name)
            return CoordinateReferenceSystem.TryParse(name, out var crs) ? crs : null;
        return null;
    }

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.TryGetDateTime(out var dt) && e.GetString()!.Length >= 10 && e.GetString()![4] == '-' ? dt : e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText()
    };

    public static NtsGeometry ParseGeometry(JsonElement g)
    {
        var f = Geo.Factory;
        var type = g.GetProperty("type").GetString();
        if (type == "GeometryCollection")
            return f.CreateGeometryCollection(g.GetProperty("geometries").EnumerateArray().Select(ParseGeometry).ToArray());
        var c = g.GetProperty("coordinates");
        return type switch
        {
            "Point" => c.GetArrayLength() == 0 ? f.CreatePoint() : f.CreatePoint(Coord(c)),
            "MultiPoint" => f.CreateMultiPoint(c.EnumerateArray().Select(p => f.CreatePoint(Coord(p))).ToArray()),
            "LineString" => f.CreateLineString(Coords(c)),
            "MultiLineString" => f.CreateMultiLineString(c.EnumerateArray().Select(l => f.CreateLineString(Coords(l))).ToArray()),
            "Polygon" => Poly(c),
            "MultiPolygon" => f.CreateMultiPolygon(c.EnumerateArray().Select(Poly).ToArray()),
            _ => throw new FormatException($"Unsupported GeoJSON geometry type '{type}'.")
        };
    }

    private static Polygon Poly(JsonElement rings)
    {
        var f = Geo.Factory;
        var list = rings.EnumerateArray().Select(r => f.CreateLinearRing(Close(Coords(r)))).ToArray();
        if (list.Length == 0) return f.CreatePolygon();
        return f.CreatePolygon(list[0], list[1..]);
    }

    private static Coordinate[] Close(Coordinate[] c) => c.Length > 0 && !c[0].Equals2D(c[^1]) ? [.. c, c[0].Copy()] : c;

    private static Coordinate Coord(JsonElement p)
    {
        double x = p[0].GetDouble(), y = p[1].GetDouble();
        return p.GetArrayLength() > 2 ? new CoordinateZ(x, y, p[2].GetDouble()) : new Coordinate(x, y);
    }

    private static Coordinate[] Coords(JsonElement arr)
    {
        var res = new Coordinate[arr.GetArrayLength()];
        int i = 0;
        foreach (var p in arr.EnumerateArray()) res[i++] = Coord(p);
        return res;
    }

    // ------------------------------------------------------------------ writer

    public static void WriteFile(GeoDataFrame gdf, string path, bool indented = false, int? precision = null) =>
        File.WriteAllText(path, Write(gdf, indented, precision), new UTF8Encoding(false));

    /// <summary>Serializes to a FeatureCollection. Non-WGS 84 data gets a legacy "crs" member.</summary>
    public static string Write(GeoDataFrame gdf, bool indented = false, int? precision = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = indented }))
        {
            w.WriteStartObject();
            w.WriteString("type", "FeatureCollection");
            if (gdf.Crs is { } crs && crs.Epsg != 4326)
            {
                w.WriteStartObject("crs");
                w.WriteString("type", "name");
                w.WriteStartObject("properties");
                w.WriteString("name", crs.Epsg is { } code ? $"urn:ogc:def:crs:EPSG::{code}" : crs.ToProj4());
                w.WriteEndObject();
                w.WriteEndObject();
            }
            var env = gdf.TotalBounds;
            if (!env.IsNull)
            {
                w.WriteStartArray("bbox");
                w.WriteNumberValue(env.MinX); w.WriteNumberValue(env.MinY); w.WriteNumberValue(env.MaxX); w.WriteNumberValue(env.MaxY);
                w.WriteEndArray();
            }
            w.WriteStartArray("features");
            var attrs = gdf.Columns.Where(c => c.Name != gdf.GeometryColumnName && c is not GeoSeries).ToArray();
            var geo = gdf.Geometry;
            for (int i = 0; i < gdf.RowCount; i++)
            {
                w.WriteStartObject();
                w.WriteString("type", "Feature");
                w.WriteStartObject("properties");
                foreach (var c in attrs)
                {
                    w.WritePropertyName(c.Name);
                    WriteValue(w, c.GetValue(i));
                }
                w.WriteEndObject();
                w.WritePropertyName("geometry");
                if (geo[i] is { } g) WriteGeometry(w, g, precision); else w.WriteNullValue();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case double d when double.IsNaN(d) || double.IsInfinity(d): w.WriteNullValue(); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            case long l: w.WriteNumberValue(l); break;
            case int n: w.WriteNumberValue(n); break;
            case bool b: w.WriteBooleanValue(b); break;
            case DateTime dt: w.WriteStringValue(dt.ToString("o", CultureInfo.InvariantCulture)); break;
            default: w.WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
        }
    }

    public static void WriteGeometry(Utf8JsonWriter w, NtsGeometry g, int? precision = null)
    {
        w.WriteStartObject();
        switch (g)
        {
            case Point p:
                w.WriteString("type", "Point");
                w.WritePropertyName("coordinates");
                if (p.IsEmpty) { w.WriteStartArray(); w.WriteEndArray(); } else WriteCoord(w, p.Coordinate, precision);
                break;
            case LineString ls:
                w.WriteString("type", "LineString");
                w.WritePropertyName("coordinates");
                WriteSeq(w, ls.Coordinates, precision);
                break;
            case Polygon poly:
                w.WriteString("type", "Polygon");
                w.WritePropertyName("coordinates");
                WritePoly(w, poly, precision);
                break;
            case MultiPoint mp:
                w.WriteString("type", "MultiPoint");
                w.WritePropertyName("coordinates");
                w.WriteStartArray();
                foreach (var pt in mp.Geometries) WriteCoord(w, pt.Coordinate, precision);
                w.WriteEndArray();
                break;
            case MultiLineString ml:
                w.WriteString("type", "MultiLineString");
                w.WritePropertyName("coordinates");
                w.WriteStartArray();
                foreach (var l in ml.Geometries) WriteSeq(w, l.Coordinates, precision);
                w.WriteEndArray();
                break;
            case MultiPolygon mpoly:
                w.WriteString("type", "MultiPolygon");
                w.WritePropertyName("coordinates");
                w.WriteStartArray();
                foreach (Polygon pg in mpoly.Geometries) WritePoly(w, pg, precision);
                w.WriteEndArray();
                break;
            case GeometryCollection gc:
                w.WriteString("type", "GeometryCollection");
                w.WriteStartArray("geometries");
                foreach (var part in gc.Geometries) WriteGeometry(w, part, precision);
                w.WriteEndArray();
                break;
        }
        w.WriteEndObject();
    }

    private static void WritePoly(Utf8JsonWriter w, Polygon p, int? precision)
    {
        w.WriteStartArray();
        // RFC 7946: exterior counter-clockwise, holes clockwise.
        var shell = p.ExteriorRing.Coordinates;
        WriteSeq(w, NetTopologySuite.Algorithm.Orientation.IsCCW(shell) ? shell : shell.Reverse().ToArray(), precision);
        foreach (var h in p.InteriorRings)
        {
            var hc = h.Coordinates;
            WriteSeq(w, NetTopologySuite.Algorithm.Orientation.IsCCW(hc) ? hc.Reverse().ToArray() : hc, precision);
        }
        w.WriteEndArray();
    }

    private static void WriteSeq(Utf8JsonWriter w, Coordinate[] coords, int? precision)
    {
        w.WriteStartArray();
        foreach (var c in coords) WriteCoord(w, c, precision);
        w.WriteEndArray();
    }

    private static void WriteCoord(Utf8JsonWriter w, Coordinate c, int? precision)
    {
        w.WriteStartArray();
        w.WriteNumberValue(precision is { } p ? Math.Round(c.X, p) : c.X);
        w.WriteNumberValue(precision is { } q ? Math.Round(c.Y, q) : c.Y);
        if (!double.IsNaN(c.Z)) w.WriteNumberValue(c.Z);
        w.WriteEndArray();
    }
}
