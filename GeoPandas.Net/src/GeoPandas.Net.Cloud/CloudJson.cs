using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Cloud;

/// <summary>A cloud service answered with an error status.</summary>
public sealed class CloudServiceException(string service, int status, string body)
    : Exception($"{service} returned HTTP {status}: {(body.Length > 500 ? body[..500] + "…" : body)}")
{
    public string Service { get; } = service;
    public int StatusCode { get; } = status;
    public string ResponseBody { get; } = body;
}

internal static class CloudJson
{
    internal static ByteArrayContent JsonContent(byte[] body, string mediaType = "application/json")
    {
        var c = new ByteArrayContent(body);
        c.Headers.ContentType = new MediaTypeHeaderValue(mediaType);   // no charset: it is part of the SigV4-signed header
        return c;
    }

    /// <summary>Trim/AOT-safe JSON for the request shapes used here: dictionaries, sequences, strings, numbers, booleans, null.</summary>
    internal static byte[] Serialize(object? value)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) Write(w, value);
        return ms.ToArray();
    }

    private static void Write(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case double d: if (double.IsFinite(d)) w.WriteNumberValue(d); else w.WriteNullValue(); break;
            case float f: w.WriteNumberValue(f); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case DateTimeOffset t: w.WriteStringValue(t); break;
            case DateTime t: w.WriteStringValue(t); break;
            case JsonElement e: e.WriteTo(w); break;
            case System.Collections.IDictionary dict:
                w.WriteStartObject();
                foreach (System.Collections.DictionaryEntry kv in dict) { w.WritePropertyName(Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!); Write(w, kv.Value); }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable seq:
                w.WriteStartArray();
                foreach (var item in seq) Write(w, item);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    internal static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, string service, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new CloudServiceException(service, (int)response.StatusCode, text);
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    internal static string Q(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    internal static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
        return e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Null or JsonValueKind.Undefined => null, _ => e.GetRawText() };
    }

    internal static double Num(JsonElement e, params string[] path)
    {
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return double.NaN; }
        return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN;
    }

    /// <summary>A flat table from JSON objects: the union of top-level keys; numbers → double, booleans → bool, strings → string,
    /// nested values → their JSON text.</summary>
    internal static List<Column> Columns(IReadOnlyList<JsonElement> rows, ISet<string>? skip = null)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (var r in rows)
            if (r.ValueKind == JsonValueKind.Object)
                foreach (var p in r.EnumerateObject())
                    if ((skip is null || !skip.Contains(p.Name)) && seen.Add(p.Name)) names.Add(p.Name);
        var cols = new List<Column>();
        foreach (var name in names)
        {
            var vals = rows.Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) ? v : default).ToList();
            var kinds = vals.Where(v => v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)).Select(v => v.ValueKind).Distinct().ToList();
            if (kinds.Count == 1 && kinds[0] == JsonValueKind.Number)
                cols.Add(Column.Create(name, vals.Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToArray()));
            else if (kinds.Count > 0 && kinds.All(k => k is JsonValueKind.True or JsonValueKind.False))
                cols.Add(Column<bool>.FromNullable(name, vals.Select(v => v.ValueKind switch { JsonValueKind.True => (bool?)true, JsonValueKind.False => false, _ => null }).ToArray()));
            else
                cols.Add(new Column<string?>(name, vals.Select(v => v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Undefined or JsonValueKind.Null => null,
                    _ => v.GetRawText(),
                }).ToArray()));
        }
        return cols;
    }

    internal static GeoDataFrame Frame(IReadOnlyList<NtsGeometry?> geometries, IEnumerable<Column> columns) =>
        GeoDataFrame.FromGeometries(geometries, CoordinateReferenceSystem.Wgs84, columns.ToArray());

    /// <summary>Longitude/latitude of a frame's geometries (centroids for non-points), reprojected to WGS 84 when needed.</summary>
    internal static (double Lon, double Lat)[] LonLat(GeoDataFrame gdf)
    {
        var src = gdf.Crs is { IsGeographic: false } ? gdf.ToCrs(4326) : gdf;
        return Enumerable.Range(0, src.RowCount).Select(i => src.Geometry[i] is { IsEmpty: false } g
            ? (g is Point p ? (p.X, p.Y) : (g.Centroid.X, g.Centroid.Y)) : (double.NaN, double.NaN)).ToArray();
    }

    internal static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
