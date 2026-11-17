using System.Globalization;
using System.Text.Json.Nodes;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Arrow;

/// <summary>The GeoParquet / GeoArrow <c>"geo"</c> file metadata (spec 1.1.0).</summary>
internal static class GeoMetadata
{
    public const string Version = "1.1.0";

    public static string GeometryTypeName(NtsGeometry g)
    {
        var t = g switch
        {
            Point => "Point", LineString => "LineString", Polygon => "Polygon", MultiPoint => "MultiPoint",
            MultiLineString => "MultiLineString", MultiPolygon => "MultiPolygon", _ => "GeometryCollection"
        };
        return Geometry.Geo.HasZ(g) ? t + " Z" : t;
    }

    public static JsonObject Build(GeoSeries geometry, string columnName, bool covering)
    {
        var types = geometry.NonNull().Select(g => GeometryTypeName(g!)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var col = new JsonObject
        {
            ["encoding"] = "WKB",
            ["geometry_types"] = new JsonArray(types.Select(t => (JsonNode)t).ToArray()),
        };
        col["crs"] = geometry.Crs is { } crs ? ProjJson.ToNode(crs) : null;
        var b = geometry.TotalBounds;
        if (!b.IsNull) col["bbox"] = new JsonArray(b.MinX, b.MinY, b.MaxX, b.MaxY);
        if (covering)
            col["covering"] = new JsonObject
            {
                ["bbox"] = new JsonObject
                {
                    ["xmin"] = new JsonArray("bbox", "xmin"), ["ymin"] = new JsonArray("bbox", "ymin"),
                    ["xmax"] = new JsonArray("bbox", "xmax"), ["ymax"] = new JsonArray("bbox", "ymax")
                }
            };
        return new JsonObject
        {
            ["version"] = Version,
            ["primary_column"] = columnName,
            ["columns"] = new JsonObject { [columnName] = col },
            ["creator"] = new JsonObject { ["library"] = "GeoPandas.Net", ["version"] = typeof(GeoMetadata).Assembly.GetName().Version?.ToString(3) }
        };
    }

    public sealed record ColumnInfo(string Name, string Encoding, CoordinateReferenceSystem? Crs, string? CoveringBbox, Envelope? Bbox);

    /// <summary>Parses "geo" metadata. A missing "crs" key means OGC:CRS84 (WGS 84 lon/lat); an explicit null means unknown.</summary>
    public static (string Primary, Dictionary<string, ColumnInfo> Columns) Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("Empty 'geo' metadata.");
        var primary = root["primary_column"]?.GetValue<string>() ?? "geometry";
        var cols = new Dictionary<string, ColumnInfo>();
        if (root["columns"] is JsonObject co)
            foreach (var (name, node) in co)
            {
                if (node is null) continue;
                var enc = node["encoding"]?.GetValue<string>() ?? "WKB";
                CoordinateReferenceSystem? crs;
                if (node is JsonObject o && !o.ContainsKey("crs")) crs = CoordinateReferenceSystem.Wgs84;
                else if (node["crs"] is null) crs = null;
                else crs = node["crs"] is JsonValue v && v.TryGetValue<string>(out var s)
                    ? (CoordinateReferenceSystem.TryParse(s, out var c1) ? c1 : null)
                    : ProjJson.TryParse(node["crs"]!.ToJsonString(), out var c2) ? c2 : null;
                string? cov = node["covering"]?["bbox"]?["xmin"]?[0]?.GetValue<string>();
                Envelope? bbox = null;
                if (node["bbox"] is JsonArray ba && ba.Count >= 4)
                {
                    double D(int i) => ba[i]!.GetValue<double>();
                    bbox = ba.Count == 6 ? new Envelope(D(0), D(3), D(1), D(4)) : new Envelope(D(0), D(2), D(1), D(3));
                }
                cols[name] = new ColumnInfo(name, enc, crs, cov, bbox);
            }
        return (primary, cols);
    }

    public static string Str(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
