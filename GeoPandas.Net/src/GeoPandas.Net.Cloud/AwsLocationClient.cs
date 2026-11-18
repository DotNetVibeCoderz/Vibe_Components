using System.Globalization;
using System.Text.Json;
using GeoPandas.Net.Twin;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using static GeoPandas.Net.Cloud.CloudJson;

namespace GeoPandas.Net.Cloud;

/// <summary>
/// Amazon Location Service over its REST API: place search (geocoding / reverse geocoding), route calculation, tracker position
/// updates and geofence collections. Authenticates with SigV4 (<see cref="AwsCredentials"/>, service <c>geo</c>) or an API key
/// (places and routes only). Results are WGS 84 GeoDataFrames.
/// </summary>
public sealed class AwsLocationClient
{
    private readonly HttpClient _http;
    private readonly AwsCredentials? _credentials;
    private readonly string? _apiKey;

    public AwsLocationClient(HttpClient http, string region, AwsCredentials credentials)
    {
        _http = http; Region = region; _credentials = credentials;
    }

    /// <summary>API-key authentication (Amazon Location API keys allow places, routes and maps actions).</summary>
    public AwsLocationClient(HttpClient http, string region, string apiKey)
    {
        _http = http; Region = region; _apiKey = apiKey;
    }

    public string Region { get; }
    /// <summary>Clock used for request signing (overridable for tests).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    private async Task<JsonDocument> PostAsync(string api, string path, object body, CancellationToken ct)
    {
        var url = $"https://{api}.geo.{Region}.amazonaws.com{path}";
        if (_apiKey is not null) url += (path.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(_apiKey);
        var bytes = Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(bytes) };
        if (_credentials is not null) AwsSigV4.Sign(request, bytes, _credentials, Region, "geo", Clock());
        return await SendAsync(_http, request, "AWS Location", ct).ConfigureAwait(false);
    }

    private static string Seg(string name) => Uri.EscapeDataString(name);

    private static GeoDataFrame Places(JsonDocument doc)
    {
        var results = doc.RootElement.TryGetProperty("Results", out var r) ? r.EnumerateArray().ToList() : [];
        var geoms = new List<NtsGeometry?>();
        var places = new List<JsonElement>();
        foreach (var res in results)
        {
            var place = res.GetProperty("Place");
            var pt = place.GetProperty("Geometry").GetProperty("Point");
            geoms.Add(Geometry.Geo.Point(pt[0].GetDouble(), pt[1].GetDouble()));
            places.Add(place);
        }
        var cols = Columns(places, new HashSet<string> { "Geometry" });
        cols.Add(Column.Create("Relevance", results.Select(x => Num(x, "Relevance")).ToArray()));
        cols.Add(Column.Create("Distance", results.Select(x => Num(x, "Distance")).ToArray()));
        cols.Add(new Column<string?>("PlaceId", results.Select(x => Str(x, "PlaceId")).ToArray()));
        return Frame(geoms, cols);
    }

    /// <summary>SearchPlaceIndexForText: geocodes free text; optional bias position (lon, lat) and ISO-3 country filter.</summary>
    public async Task<GeoDataFrame> GeocodeAsync(string indexName, string text, int maxResults = 5, (double Lon, double Lat)? biasPosition = null,
        IReadOnlyList<string>? countries = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["Text"] = text, ["MaxResults"] = maxResults };
        if (biasPosition is { } b) body["BiasPosition"] = new[] { b.Lon, b.Lat };
        if (countries is { Count: > 0 }) body["FilterCountries"] = countries;
        using var doc = await PostAsync("places", $"/places/v0/indexes/{Seg(indexName)}/search/text", body, ct).ConfigureAwait(false);
        return Places(doc);
    }

    /// <summary>SearchPlaceIndexForPosition: reverse geocoding.</summary>
    public async Task<GeoDataFrame> ReverseGeocodeAsync(string indexName, double lon, double lat, int maxResults = 1, CancellationToken ct = default)
    {
        using var doc = await PostAsync("places", $"/places/v0/indexes/{Seg(indexName)}/search/position",
            new Dictionary<string, object> { ["Position"] = new[] { lon, lat }, ["MaxResults"] = maxResults }, ct).ConfigureAwait(false);
        return Places(doc);
    }

    /// <summary>Geocodes every value of a text column; adds the best match's point (null when nothing is found) and label/relevance columns.</summary>
    public async Task<GeoDataFrame> GeocodeColumnAsync(DataFrame df, string addressColumn, string indexName, CancellationToken ct = default)
    {
        var geoms = new NtsGeometry?[df.RowCount];
        var labels = new string?[df.RowCount];
        var relevance = new double[df.RowCount];
        for (int i = 0; i < df.RowCount; i++)
        {
            relevance[i] = double.NaN;
            if (Convert.ToString(df[addressColumn].GetValue(i), CultureInfo.InvariantCulture) is not { Length: > 0 } text) continue;
            var hit = await GeocodeAsync(indexName, text, 1, ct: ct).ConfigureAwait(false);
            if (hit.RowCount == 0) continue;
            geoms[i] = hit.Geometry[0];
            labels[i] = (string?)hit["Label"].GetValue(0);
            relevance[i] = hit["Relevance"].GetDouble(0);
        }
        return GeoDataFrame.FromGeometries(geoms, Geometry.Crs.CoordinateReferenceSystem.Wgs84,
            [.. df.Columns, new Column<string?>("geocoded_label", labels), Column.Create("geocode_relevance", relevance)]);
    }

    /// <summary>CalculateRoute: one row per leg (LineString), with distance (km, as returned) and duration (s). Waypoints are optional.</summary>
    public async Task<GeoDataFrame> CalculateRouteAsync(string calculatorName, (double Lon, double Lat) departure, (double Lon, double Lat) destination,
        IReadOnlyList<(double Lon, double Lat)>? waypoints = null, string travelMode = "Car", CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["DeparturePosition"] = new[] { departure.Lon, departure.Lat },
            ["DestinationPosition"] = new[] { destination.Lon, destination.Lat },
            ["IncludeLegGeometry"] = true,
            ["TravelMode"] = travelMode,
            ["DistanceUnit"] = "Kilometers",
        };
        if (waypoints is { Count: > 0 }) body["WaypointPositions"] = waypoints.Select(w => new[] { w.Lon, w.Lat }).ToArray();
        using var doc = await PostAsync("routes", $"/routes/v0/calculators/{Seg(calculatorName)}/calculate/route", body, ct).ConfigureAwait(false);
        var legs = doc.RootElement.GetProperty("Legs").EnumerateArray().ToList();
        var geoms = legs.Select(l => l.TryGetProperty("Geometry", out var g) && g.TryGetProperty("LineString", out var ls)
            ? (NtsGeometry?)Geometry.Geo.LineString(ls.EnumerateArray().Select(c => (c[0].GetDouble(), c[1].GetDouble())).ToArray())
            : null).ToList();
        return Frame(geoms,
        [
            Column.Create("leg", Enumerable.Range(0, legs.Count).Select(i => (long)i).ToArray()),
            Column.Create("distance_km", legs.Select(l => Num(l, "Distance")).ToArray()),
            Column.Create("duration_s", legs.Select(l => Num(l, "DurationSeconds")).ToArray()),
            Column.Create("steps", legs.Select(l => l.TryGetProperty("Steps", out var s) ? (long)s.GetArrayLength() : 0L).ToArray()),
        ]);
    }

    /// <summary>BatchUpdateDevicePosition (batches of 10). Positions are WGS 84 longitude/latitude.</summary>
    public async Task<int> UpdateDevicePositionsAsync(string trackerName, IEnumerable<(string DeviceId, double Lon, double Lat, DateTimeOffset Time)> positions,
        CancellationToken ct = default)
    {
        int sent = 0;
        foreach (var batch in positions.Chunk(10))
        {
            var updates = batch.Select(p => new Dictionary<string, object>
            {
                ["DeviceId"] = p.DeviceId,
                ["Position"] = new[] { p.Lon, p.Lat },
                ["SampleTime"] = p.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            }).ToArray();
            using var doc = await PostAsync("tracking", $"/tracking/v0/trackers/{Seg(trackerName)}/positions", new Dictionary<string, object> { ["Updates"] = updates }, ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("Errors", out var errors) && errors.GetArrayLength() > 0)
                throw new CloudServiceException("AWS Location", 200, errors.GetRawText());
            sent += batch.Length;
        }
        return sent;
    }

    /// <summary>Pushes the current positions of a live layer to a tracker (reprojected to WGS 84).</summary>
    public Task<int> SyncTrackerAsync(string trackerName, LiveLayer layer, CancellationToken ct = default)
    {
        var snap = layer.Snapshot();
        var ll = LonLat(snap);
        var times = Enumerable.Range(0, snap.RowCount).Select(i => new DateTimeOffset(DateTime.SpecifyKind((DateTime)snap["time"].GetValue(i)!, DateTimeKind.Utc))).ToArray();
        return UpdateDevicePositionsAsync(trackerName, Enumerable.Range(0, snap.RowCount)
            .Where(i => !double.IsNaN(ll[i].Lon))
            .Select(i => ((string)snap["entity"].GetValue(i)!, ll[i].Lon, ll[i].Lat, times[i])), ct);
    }

    /// <summary>Current positions of the devices of a tracker (ListDevicePositions, all pages).</summary>
    public async Task<GeoDataFrame> ListDevicePositionsAsync(string trackerName, CancellationToken ct = default)
    {
        var entries = new List<JsonElement>();
        string? next = null;
        do
        {
            var body = new Dictionary<string, object>();
            if (next is not null) body["NextToken"] = next;
            using var doc = await PostAsync("tracking", $"/tracking/v0/trackers/{Seg(trackerName)}/list-positions", body, ct).ConfigureAwait(false);
            entries.AddRange(doc.RootElement.GetProperty("Entries").EnumerateArray().Select(e => e.Clone()));
            next = Str(doc.RootElement, "NextToken");
        } while (next is not null);
        var geoms = entries.Select(e => (NtsGeometry?)Geometry.Geo.Point(e.GetProperty("Position")[0].GetDouble(), e.GetProperty("Position")[1].GetDouble())).ToList();
        return Frame(geoms, Columns(entries, new HashSet<string> { "Position" }));
    }

    /// <summary>BatchPutGeofence (batches of 10): polygons of a frame as geofences (exterior rings counter-clockwise, holes clockwise).</summary>
    public async Task<int> PutGeofencesAsync(string collectionName, GeoDataFrame zones, string idColumn, CancellationToken ct = default)
    {
        var src = zones.Crs is { IsGeographic: false } ? zones.ToCrs(4326) : zones;
        var entries = new List<Dictionary<string, object>>();
        for (int i = 0; i < src.RowCount; i++)
        {
            if (src.Geometry[i] is not Polygon poly) throw new ArgumentException($"Row {i}: geofences must be polygons (got {src.Geometry[i]?.GeometryType ?? "null"}).");
            static double[][] Ring(LineString ring, bool ccw)
            {
                var cs = ring.Coordinates;
                if (NetTopologySuite.Algorithm.Orientation.IsCCW(cs) != ccw) cs = cs.Reverse().ToArray();
                return cs.Select(c => new[] { c.X, c.Y }).ToArray();
            }
            var rings = new List<double[][]> { Ring(poly.Shell, true) };
            rings.AddRange(poly.Holes.Select(h => Ring(h, false)));
            entries.Add(new Dictionary<string, object>
            {
                ["GeofenceId"] = Convert.ToString(src[idColumn].GetValue(i), CultureInfo.InvariantCulture)!,
                ["Geometry"] = new Dictionary<string, object> { ["Polygon"] = rings },
            });
        }
        int sent = 0;
        foreach (var batch in entries.Chunk(10))
        {
            using var doc = await PostAsync("geofencing", $"/geofencing/v0/collections/{Seg(collectionName)}/put-geofences",
                new Dictionary<string, object> { ["Entries"] = batch }, ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("Errors", out var errors) && errors.GetArrayLength() > 0)
                throw new CloudServiceException("AWS Location", 200, errors.GetRawText());
            sent += batch.Length;
        }
        return sent;
    }
}
