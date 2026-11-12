using System.Text.Json.Nodes;
using AquaCulture.Core.Geo;

namespace AquaCulture.GIS;

/// <summary>A map feature: a polygon (pond, farm boundary) or a point (sensor, water source).</summary>
public sealed record MapFeature(string Id, string Layer, GeoPolygon? Polygon = null, GeoCoordinate? Point = null, IReadOnlyDictionary<string, object?>? Properties = null);

/// <summary>GeoJSON (RFC 7946) reading and writing without reflection (AOT-friendly).</summary>
public static class GeoJson
{
    public static JsonObject Feature(MapFeature f)
    {
        JsonNode geometry = f.Polygon is { } poly
            ? new JsonObject { ["type"] = "Polygon", ["coordinates"] = new JsonArray(Ring(poly)) }
            : new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(f.Point!.Value.Longitude, f.Point.Value.Latitude) };
        var props = new JsonObject { ["id"] = f.Id, ["layer"] = f.Layer };
        if (f.Properties is not null)
            foreach (var (k, v) in f.Properties)
                props[k] = v switch
                {
                    null => null,
                    double d => d, float fl => fl, int i => i, long l => l, bool b => b, string s => s,
                    _ => v.ToString(),
                };
        return new JsonObject { ["type"] = "Feature", ["id"] = f.Id, ["geometry"] = geometry, ["properties"] = props };
    }

    private static JsonArray Ring(GeoPolygon p)
    {
        var ring = new JsonArray();
        foreach (var c in p.Ring.Append(p.Ring[0])) ring.Add((JsonNode)new JsonArray(Math.Round(c.Longitude, 7), Math.Round(c.Latitude, 7)));
        return ring;
    }

    public static string Write(IEnumerable<MapFeature> features, bool indented = false) =>
        new JsonObject { ["type"] = "FeatureCollection", ["features"] = new JsonArray(features.Select(f => (JsonNode)Feature(f)).ToArray()) }
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = indented });

    /// <summary>Reads Point and Polygon (outer ring) features.</summary>
    public static IReadOnlyList<MapFeature> Read(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Empty GeoJSON.");
        var feats = root["type"]?.GetValue<string>() == "FeatureCollection" ? root["features"]!.AsArray().Select(x => x!).ToList() : [root];
        var list = new List<MapFeature>();
        int n = 0;
        foreach (var f in feats)
        {
            var g = f["geometry"]; string type = g?["type"]?.GetValue<string>() ?? "";
            var props = f["properties"] as JsonObject;
            string id = f["id"]?.ToString() ?? props?["id"]?.ToString() ?? $"f{n}";
            string layer = props?["layer"]?.ToString() ?? "";
            var dict = props?.ToDictionary(kv => kv.Key, kv => (object?)(kv.Value is JsonValue jv ? jv.TryGetValue<double>(out var d) ? d : jv.ToString() : kv.Value?.ToJsonString()));
            if (type == "Point")
            {
                var c = g!["coordinates"]!.AsArray();
                list.Add(new MapFeature(id, layer, Point: new GeoCoordinate(c[1]!.GetValue<double>(), c[0]!.GetValue<double>()), Properties: dict));
            }
            else if (type == "Polygon")
            {
                var ring = g!["coordinates"]![0]!.AsArray().Select(p => new GeoCoordinate(p![1]!.GetValue<double>(), p[0]!.GetValue<double>())).ToList();
                list.Add(new MapFeature(id, layer, new GeoPolygon(ring), Properties: dict));
            }
            n++;
        }
        return list;
    }
}
