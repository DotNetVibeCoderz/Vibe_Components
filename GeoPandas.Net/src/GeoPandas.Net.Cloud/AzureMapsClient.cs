using System.Text.Json;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using static GeoPandas.Net.Cloud.CloudJson;

namespace GeoPandas.Net.Cloud;

/// <summary>Bearer-token source (e.g. <c>ct =&gt; credential.GetTokenAsync(...)</c> from Azure.Identity), called per request.</summary>
public delegate ValueTask<string> AccessTokenProvider(CancellationToken cancellationToken);

/// <summary>
/// Azure Maps REST client: geocoding and reverse geocoding (GeoJSON API 2023-06-01), route directions and reachable-range
/// isochrones (Route API 1.0). Authenticates with a subscription key or Microsoft Entra ID (bearer token + <c>x-ms-client-id</c>).
/// Results are WGS 84 GeoDataFrames.
/// </summary>
public sealed class AzureMapsClient
{
    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly AccessTokenProvider? _token;
    private readonly string? _clientId;

    public AzureMapsClient(HttpClient http, string subscriptionKey)
    {
        _http = http; _key = subscriptionKey;
    }

    public AzureMapsClient(HttpClient http, AccessTokenProvider tokenProvider, string mapsClientId)
    {
        _http = http; _token = tokenProvider; _clientId = mapsClientId;
    }

    /// <summary>Service root (geography-specific endpoints such as https://eu.atlas.microsoft.com also work).</summary>
    public Uri BaseAddress { get; init; } = new("https://atlas.microsoft.com");

    private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var url = new Uri(BaseAddress, pathAndQuery).ToString();
        if (_key is not null) url += "&subscription-key=" + Uri.EscapeDataString(_key);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_token is not null)
        {
            request.Headers.Authorization = new("Bearer", await _token(ct).ConfigureAwait(false));
            request.Headers.TryAddWithoutValidation("x-ms-client-id", _clientId);
        }
        return await SendAsync(_http, request, "Azure Maps", ct).ConfigureAwait(false);
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    private static GeoDataFrame Features(JsonDocument doc)
    {
        var features = doc.RootElement.TryGetProperty("features", out var f) ? f.EnumerateArray().ToList() : [];
        var geoms = new List<NtsGeometry?>();
        var props = new List<JsonElement>();
        foreach (var feat in features)
        {
            var c = feat.GetProperty("geometry").GetProperty("coordinates");
            geoms.Add(Geometry.Geo.Point(c[0].GetDouble(), c[1].GetDouble()));
            props.Add(feat.TryGetProperty("properties", out var p) ? p : default);
        }
        var cols = Columns(props, new HashSet<string> { "address", "geocodePoints", "matchCodes", "bbox" });
        cols.Add(new Column<string?>("formattedAddress", props.Select(p => Str(p, "address", "formattedAddress")).ToArray()));
        cols.Add(new Column<string?>("locality", props.Select(p => Str(p, "address", "locality")).ToArray()));
        cols.Add(new Column<string?>("countryRegion", props.Select(p => Str(p, "address", "countryRegion", "name")).ToArray()));
        cols.Add(new Column<string?>("postalCode", props.Select(p => Str(p, "address", "postalCode")).ToArray()));
        return Frame(geoms, cols);
    }

    /// <summary>Geocodes free text (Search – Get Geocoding); optional ISO country code filter.</summary>
    public async Task<GeoDataFrame> GeocodeAsync(string query, int top = 5, string? countryRegion = null, CancellationToken ct = default)
    {
        var q = $"/geocode?api-version=2023-06-01&query={E(query)}&top={top}";
        if (countryRegion is not null) q += "&countryRegion=" + E(countryRegion);
        using var doc = await GetAsync(q, ct).ConfigureAwait(false);
        return Features(doc);
    }

    /// <summary>Reverse geocoding (Search – Get Reverse Geocoding).</summary>
    public async Task<GeoDataFrame> ReverseGeocodeAsync(double lon, double lat, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"/reverseGeocode?api-version=2023-06-01&coordinates={Q(lon)},{Q(lat)}", ct).ConfigureAwait(false);
        return Features(doc);
    }

    /// <summary>Route directions through two or more (lon, lat) points: one row per leg (LineString) with length (m) and travel time (s).</summary>
    public async Task<GeoDataFrame> RouteAsync(IReadOnlyList<(double Lon, double Lat)> points, string travelMode = "car", bool traffic = false, CancellationToken ct = default)
    {
        if (points.Count < 2) throw new ArgumentException("A route needs at least two points.", nameof(points));
        var query = string.Join(':', points.Select(p => $"{Q(p.Lat)},{Q(p.Lon)}"));
        using var doc = await GetAsync($"/route/directions/json?api-version=1.0&query={query}&travelMode={E(travelMode)}&traffic={(traffic ? "true" : "false")}", ct).ConfigureAwait(false);
        var route = doc.RootElement.GetProperty("routes")[0];
        var legs = route.GetProperty("legs").EnumerateArray().ToList();
        var geoms = legs.Select(l => (NtsGeometry?)Geometry.Geo.LineString(l.GetProperty("points").EnumerateArray()
            .Select(p => (p.GetProperty("longitude").GetDouble(), p.GetProperty("latitude").GetDouble())).ToArray())).ToList();
        return Frame(geoms,
        [
            Column.Create("leg", Enumerable.Range(0, legs.Count).Select(i => (long)i).ToArray()),
            Column.Create("length_m", legs.Select(l => Num(l, "summary", "lengthInMeters")).ToArray()),
            Column.Create("travel_time_s", legs.Select(l => Num(l, "summary", "travelTimeInSeconds")).ToArray()),
            Column.Create("traffic_delay_s", legs.Select(l => Num(l, "summary", "trafficDelayInSeconds")).ToArray()),
        ]);
    }

    /// <summary>Reachable range (isochrone) polygon from a point for a time budget (s) or a distance budget (m).</summary>
    public async Task<GeoDataFrame> IsochroneAsync(double lon, double lat, double? timeBudgetSeconds = null, double? distanceBudgetMeters = null,
        string travelMode = "car", CancellationToken ct = default)
    {
        if (timeBudgetSeconds is null == distanceBudgetMeters is null) throw new ArgumentException("Give exactly one of the time or distance budget.");
        var budget = timeBudgetSeconds is { } t ? $"timeBudgetInSec={Q(t)}" : $"distanceBudgetInMeters={Q(distanceBudgetMeters!.Value)}";
        using var doc = await GetAsync($"/route/range/json?api-version=1.0&query={Q(lat)},{Q(lon)}&{budget}&travelMode={E(travelMode)}", ct).ConfigureAwait(false);
        var range = doc.RootElement.GetProperty("reachableRange");
        var ring = range.GetProperty("boundary").EnumerateArray().Select(p => new Coordinate(p.GetProperty("longitude").GetDouble(), p.GetProperty("latitude").GetDouble())).ToList();
        if (ring.Count > 0 && !ring[0].Equals2D(ring[^1])) ring.Add(ring[0].Copy());
        var poly = Geometry.Geo.Factory.CreatePolygon(ring.ToArray());
        return Frame([poly],
        [
            Column.Create("budget", new[] { timeBudgetSeconds ?? distanceBudgetMeters!.Value }),
            Column.Create("budget_unit", new[] { timeBudgetSeconds is null ? "m" : "s" }),
            Column.Create("center_lon", new[] { Num(range, "center", "longitude") }),
            Column.Create("center_lat", new[] { Num(range, "center", "latitude") }),
        ]);
    }

    /// <summary>Isochrones for several budgets (largest first, so smaller polygons draw on top).</summary>
    public async Task<GeoDataFrame> IsochronesAsync(double lon, double lat, IEnumerable<double> timeBudgetsSeconds, string travelMode = "car", CancellationToken ct = default)
    {
        var frames = new List<GeoDataFrame>();
        foreach (var t in timeBudgetsSeconds.OrderDescending())
            frames.Add(await IsochroneAsync(lon, lat, t, null, travelMode, ct).ConfigureAwait(false));
        return GeoDataFrame.Concat(frames.ToArray());
    }

}
