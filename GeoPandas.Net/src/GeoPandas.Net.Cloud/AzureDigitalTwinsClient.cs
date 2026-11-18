using System.Text.Json;
using GeoPandas.Net.Twin;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using static GeoPandas.Net.Cloud.CloudJson;

namespace GeoPandas.Net.Cloud;

/// <summary>
/// Azure Digital Twins data-plane client (API 2023-10-31): twin queries as (Geo)DataFrames, JSON-patch property updates, telemetry
/// and synchronisation of a <see cref="LiveLayer"/>'s latest state onto twins. Authentication is a bearer token for the
/// <c>https://digitaltwins.azure.net/.default</c> scope supplied by an <see cref="AccessTokenProvider"/>.
/// </summary>
public sealed class AzureDigitalTwinsClient
{
    public const string ApiVersion = "2023-10-31";
    private readonly HttpClient _http;
    private readonly AccessTokenProvider _token;

    /// <param name="http">HTTP client used for all calls.</param>
    /// <param name="endpoint">Instance URL, e.g. https://my-twins.api.weu.digitaltwins.azure.net</param>
    /// <param name="tokenProvider">Bearer tokens for the https://digitaltwins.azure.net/.default scope.</param>
    public AzureDigitalTwinsClient(HttpClient http, Uri endpoint, AccessTokenProvider tokenProvider)
    {
        _http = http; Endpoint = endpoint; _token = tokenProvider;
    }

    public Uri Endpoint { get; }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, string mediaType, CancellationToken ct, string? messageId = null)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint, $"{path}{(path.Contains('?') ? '&' : '?')}api-version={ApiVersion}"));
        request.Headers.Authorization = new("Bearer", await _token(ct).ConfigureAwait(false));
        if (messageId is not null) request.Headers.TryAddWithoutValidation("Message-Id", messageId);
        if (body is not null) request.Content = JsonContent(body, mediaType);
        return await CloudJson.SendAsync(_http, request, "Azure Digital Twins", ct).ConfigureAwait(false);
    }

    private static string Id(string twinId) => Uri.EscapeDataString(twinId);

    /// <summary>Runs an ADT query (all pages); each result row becomes a table row (twin properties flattened at the top level).</summary>
    public async Task<DataFrame> QueryAsync(string query, CancellationToken ct = default)
    {
        var rows = new List<JsonElement>();
        string? token = null;
        do
        {
            var body = new Dictionary<string, object> { ["query"] = query };
            if (token is not null) body["continuationToken"] = token;
            using var doc = await SendAsync(HttpMethod.Post, "/query", Serialize(body), "application/json", ct).ConfigureAwait(false);
            rows.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(e => e.Clone()));
            token = Str(doc.RootElement, "continuationToken");
        } while (token is not null);
        var cols = Columns(rows, new HashSet<string> { "$metadata", "$etag" });
        cols.Add(new Column<string?>("$model", rows.Select(r => Str(r, "$metadata", "$model")).ToArray()));
        return new DataFrame(cols.ToArray());
    }

    /// <summary>A query whose twins carry longitude/latitude properties, as a WGS 84 GeoDataFrame (rows without both are kept with a null geometry).</summary>
    public async Task<GeoDataFrame> QueryGeoAsync(string query, string longitudeProperty = "Longitude", string latitudeProperty = "Latitude", CancellationToken ct = default)
    {
        var df = await QueryAsync(query, ct).ConfigureAwait(false);
        var geoms = new NtsGeometry?[df.RowCount];
        if (df.ColumnNames.Contains(longitudeProperty) && df.ColumnNames.Contains(latitudeProperty))
            for (int i = 0; i < df.RowCount; i++)
            {
                double x = df[longitudeProperty].GetDouble(i), y = df[latitudeProperty].GetDouble(i);
                if (!double.IsNaN(x) && !double.IsNaN(y)) geoms[i] = Geometry.Geo.Point(x, y);
            }
        return Frame(geoms, df.Columns);
    }

    /// <summary>Gets one twin as raw JSON.</summary>
    public async Task<JsonElement> GetTwinAsync(string twinId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"/digitaltwins/{Id(twinId)}", null, "application/json", ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    /// <summary>Sets top-level properties with a JSON patch (<c>add</c> creates or replaces each property).</summary>
    public async Task UpdatePropertiesAsync(string twinId, IReadOnlyDictionary<string, object?> properties, CancellationToken ct = default)
    {
        if (properties.Count == 0) return;
        var patch = properties.Select(p => new Dictionary<string, object?> { ["op"] = "add", ["path"] = "/" + EscapePointer(p.Key), ["value"] = p.Value }).ToArray();
        using var _ = await SendAsync(HttpMethod.Patch, $"/digitaltwins/{Id(twinId)}", Serialize(patch), "application/json-patch+json", ct).ConfigureAwait(false);
    }

    /// <summary>Publishes telemetry for a twin (routed to event routes); a message id is generated when not given.</summary>
    public async Task PublishTelemetryAsync(string twinId, IReadOnlyDictionary<string, object?> telemetry, string? messageId = null, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"/digitaltwins/{Id(twinId)}/telemetry", Serialize(telemetry), "application/json", ct,
            messageId ?? Guid.NewGuid().ToString("N")).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes each entity's latest position (WGS 84 Longitude/Latitude), sensor values and zones to its twin. <paramref name="twinIdOf"/>
    /// maps entity ids to twin ids (identity by default). Returns the number of twins updated.
    /// </summary>
    public async Task<int> SyncAsync(LiveLayer layer, Func<string, string>? twinIdOf = null, string longitudeProperty = "Longitude",
        string latitudeProperty = "Latitude", string? zonesProperty = "Zones", CancellationToken ct = default)
    {
        var snap = layer.Snapshot();
        var ll = LonLat(snap);
        var variables = snap.ColumnNames.Where(n => n is not ("entity" or "time" or "zones") && n != snap.GeometryColumnName).ToList();
        int n = 0;
        for (int i = 0; i < snap.RowCount; i++)
        {
            var entity = (string)snap["entity"].GetValue(i)!;
            var props = new Dictionary<string, object?>();
            if (!double.IsNaN(ll[i].Lon)) { props[longitudeProperty] = ll[i].Lon; props[latitudeProperty] = ll[i].Lat; }
            foreach (var v in variables) if (snap[v].GetValue(i) is double d && !double.IsNaN(d)) props[v] = d;
            if (zonesProperty is not null) props[zonesProperty] = (string?)snap["zones"].GetValue(i) ?? "";
            await UpdatePropertiesAsync(twinIdOf?.Invoke(entity) ?? entity, props, ct).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    private static string EscapePointer(string s) => s.Replace("~", "~0").Replace("/", "~1");
}
