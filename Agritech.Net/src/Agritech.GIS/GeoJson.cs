using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agritech.Core.Geo;

namespace Agritech.GIS;

/// <summary>GeoJSON feature with a geometry and string-keyed properties.</summary>
public sealed record GeoFeature(GeoGeometry Geometry, IReadOnlyDictionary<string, JsonNode?> Properties, string? Id = null)
{
    public string? GetString(string key) => Properties.TryGetValue(key, out var v) ? v?.ToString() : null;
}

public abstract record GeoGeometry;
public sealed record PointGeometry(GeoCoordinate Point) : GeoGeometry;
public sealed record LineStringGeometry(IReadOnlyList<GeoCoordinate> Points) : GeoGeometry;
public sealed record PolygonGeometry(GeoPolygon Polygon) : GeoGeometry;
public sealed record MultiPolygonGeometry(IReadOnlyList<GeoPolygon> Polygons) : GeoGeometry;

/// <summary>RFC 7946 GeoJSON reader/writer (Point, LineString, Polygon, MultiPolygon). AOT-friendly (JsonNode based).</summary>
public static class GeoJson
{
    public static IReadOnlyList<GeoFeature> ReadFeatures(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Empty GeoJSON.");
        string type = root["type"]?.GetValue<string>() ?? throw new FormatException("Missing 'type'.");
        return type switch
        {
            "FeatureCollection" => root["features"]!.AsArray().Select(f => ReadFeature(f!)).ToList(),
            "Feature" => [ReadFeature(root)],
            _ => [new GeoFeature(ReadGeometry(root), new Dictionary<string, JsonNode?>())],
        };
    }

    private static GeoFeature ReadFeature(JsonNode f)
    {
        var props = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (f["properties"] is JsonObject po) foreach (var kv in po) props[kv.Key] = kv.Value?.DeepClone();
        return new GeoFeature(ReadGeometry(f["geometry"]!), props, f["id"]?.ToString());
    }

    public static GeoGeometry ReadGeometry(JsonNode g)
    {
        var coords = g["coordinates"]!;
        return g["type"]!.GetValue<string>() switch
        {
            "Point" => new PointGeometry(Pt(coords)),
            "LineString" => new LineStringGeometry(coords.AsArray().Select(c => Pt(c!)).ToList()),
            "Polygon" => new PolygonGeometry(Poly(coords)),
            "MultiPolygon" => new MultiPolygonGeometry(coords.AsArray().Select(p => Poly(p!)).ToList()),
            var t => throw new NotSupportedException($"Geometry type '{t}' is not supported."),
        };
    }

    private static GeoCoordinate Pt(JsonNode c)
    {
        var a = c.AsArray();
        return new GeoCoordinate(a[1]!.GetValue<double>(), a[0]!.GetValue<double>(), a.Count > 2 ? a[2]!.GetValue<double>() : 0);
    }

    private static GeoPolygon Poly(JsonNode rings)
    {
        var r = rings.AsArray();
        var outer = r[0]!.AsArray().Select(c => Pt(c!));
        var holes = r.Skip(1).Select(h => h!.AsArray().Select(c => Pt(c!)));
        return new GeoPolygon(outer, holes);
    }

    public static string Write(IEnumerable<GeoFeature> features, bool indented = false)
    {
        var fc = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = new JsonArray(features.Select(f => (JsonNode)FeatureNode(f)).ToArray()),
        };
        return fc.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

    private static JsonObject FeatureNode(GeoFeature f)
    {
        var props = new JsonObject();
        foreach (var kv in f.Properties) props[kv.Key] = kv.Value?.DeepClone();
        var o = new JsonObject { ["type"] = "Feature", ["geometry"] = GeometryNode(f.Geometry), ["properties"] = props };
        if (f.Id is not null) o["id"] = f.Id;
        return o;
    }

    public static JsonObject GeometryNode(GeoGeometry g) => g switch
    {
        PointGeometry p => new JsonObject { ["type"] = "Point", ["coordinates"] = Coord(p.Point) },
        LineStringGeometry l => new JsonObject { ["type"] = "LineString", ["coordinates"] = new JsonArray(l.Points.Select(c => (JsonNode)Coord(c)).ToArray()) },
        PolygonGeometry p => new JsonObject { ["type"] = "Polygon", ["coordinates"] = Rings(p.Polygon) },
        MultiPolygonGeometry m => new JsonObject { ["type"] = "MultiPolygon", ["coordinates"] = new JsonArray(m.Polygons.Select(p => (JsonNode)Rings(p)).ToArray()) },
        _ => throw new NotSupportedException(),
    };

    private static JsonArray Coord(GeoCoordinate c) => new(Math.Round(c.Longitude, 8), Math.Round(c.Latitude, 8));

    private static JsonArray Ring(IReadOnlyList<GeoCoordinate> ring)
    {
        var arr = new JsonArray(ring.Select(c => (JsonNode)Coord(c)).ToArray());
        arr.Add((JsonNode)Coord(ring[0])); // close ring per RFC 7946
        return arr;
    }

    private static JsonArray Rings(GeoPolygon p)
    {
        var arr = new JsonArray(Ring(p.Outer));
        foreach (var h in p.Holes) arr.Add((JsonNode)Ring(h));
        return arr;
    }

    public static GeoFeature Feature(GeoPolygon polygon, params (string Key, object? Value)[] props) =>
        new(new PolygonGeometry(polygon), props.ToDictionary(p => p.Key, p => p.Value switch
        {
            null => null,
            double d => JsonValue.Create(d),
            int i => JsonValue.Create(i),
            bool b => JsonValue.Create(b),
            var o => (JsonNode?)JsonValue.Create(Convert.ToString(o, CultureInfo.InvariantCulture)),
        }));
}
