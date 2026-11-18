using System.Net;
using System.Text;
using System.Text.Json;
using GeoPandas.Net.Cloud;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Twin;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

/// <summary>SigV4 against botocore 1.44.5 (same keys, clock and requests); service clients against mocked HTTP.</summary>
public sealed class CloudTests
{
    private sealed class MockHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            var (status, text) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly AwsCredentials Keys = new("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
    private static readonly DateTimeOffset Clock = new(2026, 11, 18, 9, 30, 15, TimeSpan.Zero);

    [Fact]
    public void SigV4_MatchesBotocore()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "aws_sigv4_reference.json")));
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var body = Encoding.UTF8.GetBytes(c.GetProperty("body").GetString()!);
            var req = new HttpRequestMessage(new HttpMethod(c.GetProperty("method").GetString()!), c.GetProperty("url").GetString());
            if (body.Length > 0)
            {
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = new("application/json");
            }
            var token = c.GetProperty("token").ValueKind == JsonValueKind.String ? c.GetProperty("token").GetString() : null;
            AwsSigV4.Sign(req, body, Keys with { SessionToken = token }, c.GetProperty("region").GetString()!, "geo", Clock);
            Assert.Equal(c.GetProperty("authorization").GetString(), req.Headers.GetValues("Authorization").Single());
            Assert.Equal(c.GetProperty("amzdate").GetString(), req.Headers.GetValues("X-Amz-Date").Single());
            if (token is not null) Assert.Equal(token, req.Headers.GetValues("X-Amz-Security-Token").Single());
        }
    }

    [Fact]
    public async Task AwsLocation_Geocode_Route_Tracker_Geofences()
    {
        var handler = new MockHandler((req, body) => req.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/search/text") => (HttpStatusCode.OK, """
                {"Summary":{"Text":"Monas"},"Results":[
                  {"Place":{"Label":"Monumen Nasional, Jakarta, IDN","Geometry":{"Point":[106.8272,-6.1754]},"Country":"IDN","Municipality":"Jakarta"},"Relevance":0.98,"PlaceId":"p1"},
                  {"Place":{"Label":"Monas Street, Bandung, IDN","Geometry":{"Point":[107.61,-6.91]},"Country":"IDN"},"Relevance":0.5,"PlaceId":"p2"}]}
                """),
            var p when p.EndsWith("/calculate/route") => (HttpStatusCode.OK, """
                {"Legs":[{"Distance":3.2,"DurationSeconds":540,"Geometry":{"LineString":[[106.82,-6.17],[106.83,-6.18],[106.84,-6.2]]},"Steps":[{},{}]}],
                 "Summary":{"Distance":3.2,"DurationSeconds":540}}
                """),
            var p when p.EndsWith("/positions") => (HttpStatusCode.OK, """{"Errors":[]}"""),
            var p when p.EndsWith("/put-geofences") => (HttpStatusCode.OK, """{"Successes":[],"Errors":[]}"""),
            var p when p.EndsWith("/search/position") => (HttpStatusCode.Forbidden, """{"message":"not authorized"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var aws = new AwsLocationClient(new HttpClient(handler), "ap-southeast-1", Keys) { Clock = () => Clock };

        var places = await aws.GeocodeAsync("my-index", "Jalan Sudirman, Jakarta");
        var call = handler.Calls[^1];
        Assert.Equal("https://places.geo.ap-southeast-1.amazonaws.com/places/v0/indexes/my-index/search/text", call.Request.RequestUri!.ToString());
        Assert.Equal("""{"Text":"Jalan Sudirman, Jakarta","MaxResults":5}""", call.Body);
        // the client's own request carries the botocore signature of the same request
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261118/ap-southeast-1/geo/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=36e360fb3d37b16006cdf6114090495ecd2bb78438931fb5560ccd4a748862f6",
            call.Request.Headers.GetValues("Authorization").Single());
        Assert.Equal(2, places.RowCount);
        Assert.Equal(CoordinateReferenceSystem.Wgs84, places.Crs);
        Assert.Equal(106.8272, ((Point)places.Geometry[0]!).X);
        Assert.Equal("Monumen Nasional, Jakarta, IDN", places["Label"].GetValue(0));
        Assert.Null(places["Municipality"].GetValue(1));
        Assert.Equal(0.98, places["Relevance"].GetDouble(0));

        var route = await aws.CalculateRouteAsync("calc", (106.82, -6.17), (106.84, -6.2), waypoints: [(106.83, -6.18)]);
        Assert.Contains("\"WaypointPositions\":[[106.83,-6.18]]", handler.Calls[^1].Body);
        Assert.Equal(3, route.Geometry[0]!.NumPoints);
        Assert.Equal(540, route["duration_s"].GetDouble(0));
        Assert.Equal(2L, route["steps"].GetValue(0));

        var sent = await aws.UpdateDevicePositionsAsync("fleet", Enumerable.Range(0, 23).Select(i => ($"t{i}", 106.8 + i * 0.001, -6.2, Clock)));
        Assert.Equal(23, sent);
        var trackerCalls = handler.Calls.Where(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/positions")).ToList();
        Assert.Equal(3, trackerCalls.Count);   // batches of 10
        Assert.StartsWith("https://tracking.geo.ap-southeast-1.amazonaws.com/tracking/v0/trackers/fleet/positions", trackerCalls[0].Request.RequestUri!.ToString());
        Assert.Contains("\"SampleTime\":\"2026-11-18T09:30:15.000Z\"", trackerCalls[0].Body);

        // live layer in UTM → WGS 84 positions
        var utm = CoordinateReferenceSystem.FromEpsg(32748);
        var layer = new LiveLayer("fleet", new Envelope(600000, 800000, 9000000, 9400000), utm);
        layer.Ingest(new SensorReading("truck", Clock, 701000, 9314000, new Dictionary<string, double> { ["speed"] = 40 }));
        Assert.Equal(1, await aws.SyncTrackerAsync("fleet", layer));
        using (var pos = JsonDocument.Parse(handler.Calls[^1].Body))
        {
            var p = pos.RootElement.GetProperty("Updates")[0].GetProperty("Position");
            var ll = GeoDataFrame.FromGeometries([Geometry.Geo.Point(701000, 9314000)], utm).ToCrs(4326).Geometry[0]!.Coordinate;
            Assert.Equal(ll.X, p[0].GetDouble(), 9);
            Assert.Equal(ll.Y, p[1].GetDouble(), 9);
        }

        // geofences: clockwise input shell is re-oriented counter-clockwise
        var cw = Geometry.Geo.Factory.CreatePolygon([new(106.8, -6.2), new(106.8, -6.1), new(106.9, -6.1), new(106.9, -6.2), new(106.8, -6.2)]);
        var zones = GeoDataFrame.FromGeometries([cw], CoordinateReferenceSystem.Wgs84, Column.Create("id", new[] { "zone-1" }));
        Assert.Equal(1, await aws.PutGeofencesAsync("zones", zones, "id"));
        using (var g = JsonDocument.Parse(handler.Calls[^1].Body))
        {
            var ring = g.RootElement.GetProperty("Entries")[0].GetProperty("Geometry").GetProperty("Polygon")[0].EnumerateArray()
                .Select(c => new Coordinate(c[0].GetDouble(), c[1].GetDouble())).ToArray();
            Assert.True(NetTopologySuite.Algorithm.Orientation.IsCCW(ring));
            Assert.Equal("zone-1", g.RootElement.GetProperty("Entries")[0].GetProperty("GeofenceId").GetString());
        }

        var ex = await Assert.ThrowsAsync<CloudServiceException>(() => aws.ReverseGeocodeAsync("my-index", 106.8, -6.2));
        Assert.Equal(403, ex.StatusCode);

        // API-key mode: key in the query, no SigV4 headers
        var keyed = new AwsLocationClient(new HttpClient(handler), "us-east-1", "v1.public.abc+/=");
        await keyed.GeocodeAsync("idx", "x");
        Assert.EndsWith("/search/text?key=v1.public.abc%2B%2F%3D", handler.Calls[^1].Request.RequestUri!.AbsoluteUri);
        Assert.False(handler.Calls[^1].Request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task AzureMaps_Geocode_Route_Isochrone()
    {
        var handler = new MockHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/geocode" or "/reverseGeocode" => (HttpStatusCode.OK, """
                {"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[106.8456,-6.2088]},
                 "properties":{"type":"Address","confidence":"High","address":{"formattedAddress":"Jl. Jend. Sudirman, Jakarta","locality":"Jakarta","countryRegion":{"ISO":"ID","name":"Indonesia"}}}}]}
                """),
            "/route/directions/json" => (HttpStatusCode.OK, """
                {"routes":[{"summary":{"lengthInMeters":5000},"legs":[
                  {"summary":{"lengthInMeters":2000,"travelTimeInSeconds":300,"trafficDelayInSeconds":0},"points":[{"latitude":-6.2,"longitude":106.8},{"latitude":-6.21,"longitude":106.81}]},
                  {"summary":{"lengthInMeters":3000,"travelTimeInSeconds":420,"trafficDelayInSeconds":30},"points":[{"latitude":-6.21,"longitude":106.81},{"latitude":-6.22,"longitude":106.83},{"latitude":-6.23,"longitude":106.84}]}]}]}
                """),
            "/route/range/json" => (HttpStatusCode.OK, """
                {"reachableRange":{"center":{"latitude":-6.2,"longitude":106.8},"boundary":[{"latitude":-6.1,"longitude":106.8},{"latitude":-6.2,"longitude":106.9},{"latitude":-6.3,"longitude":106.8},{"latitude":-6.2,"longitude":106.7}]}}
                """),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var maps = new AzureMapsClient(new HttpClient(handler), "secret key");
        var geo = await maps.GeocodeAsync("Sudirman, Jakarta", top: 3, countryRegion: "ID");
        Assert.Equal("https://atlas.microsoft.com/geocode?api-version=2023-06-01&query=Sudirman%2C%20Jakarta&top=3&countryRegion=ID&subscription-key=secret%20key",
            handler.Calls[^1].Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Jl. Jend. Sudirman, Jakarta", geo["formattedAddress"].GetValue(0));
        Assert.Equal("Indonesia", geo["countryRegion"].GetValue(0));
        Assert.Equal("High", geo["confidence"].GetValue(0));
        Assert.Equal(-6.2088, ((Point)geo.Geometry[0]!).Y);

        var route = await maps.RouteAsync([(106.8, -6.2), (106.81, -6.21), (106.84, -6.23)]);
        Assert.Contains("query=-6.2,106.8:-6.21,106.81:-6.23,106.84&travelMode=car", handler.Calls[^1].Request.RequestUri!.ToString());
        Assert.Equal(2, route.RowCount);
        Assert.Equal(5000, route["length_m"].Sum());
        Assert.Equal(3, route.Geometry[1]!.NumPoints);

        var iso = await maps.IsochroneAsync(106.8, -6.2, timeBudgetSeconds: 900);
        Assert.Contains("query=-6.2,106.8&timeBudgetInSec=900", handler.Calls[^1].Request.RequestUri!.ToString());
        var poly = (Polygon)iso.Geometry[0]!;
        Assert.True(poly.IsValid);
        Assert.Equal(5, poly.Shell.NumPoints);   // closed
        Assert.Equal(0.02, poly.Area, 9);
        await Assert.ThrowsAsync<ArgumentException>(() => maps.IsochroneAsync(0, 0));

        // Entra ID mode
        var entra = new AzureMapsClient(new HttpClient(handler), _ => ValueTask.FromResult("tok"), "client-guid");
        await entra.ReverseGeocodeAsync(106.8, -6.2);
        Assert.Equal("Bearer tok", handler.Calls[^1].Request.Headers.Authorization!.ToString());
        Assert.Equal("client-guid", handler.Calls[^1].Request.Headers.GetValues("x-ms-client-id").Single());
        Assert.DoesNotContain("subscription-key", handler.Calls[^1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task AzureDigitalTwins_Query_Patch_Telemetry_Sync()
    {
        int page = 0;
        var handler = new MockHandler((req, body) =>
        {
            if (req.RequestUri!.AbsolutePath == "/query")
                return page++ == 0
                    ? (HttpStatusCode.OK, """{"value":[{"$dtId":"pump-1","$etag":"W/1","$metadata":{"$model":"dtmi:demo:Pump;1"},"Longitude":106.8,"Latitude":-6.2,"Pressure":3.1,"Running":true}],"continuationToken":"next"}""")
                    : (HttpStatusCode.OK, """{"value":[{"$dtId":"tank-1","$metadata":{"$model":"dtmi:demo:Tank;1"},"Level":0.7}],"continuationToken":null}""");
            if (req.Method == HttpMethod.Patch) return (HttpStatusCode.NoContent, "");
            if (req.RequestUri.AbsolutePath.EndsWith("/telemetry")) return (HttpStatusCode.NoContent, "");
            return (HttpStatusCode.NotFound, "{}");
        });
        var adt = new AzureDigitalTwinsClient(new HttpClient(handler), new Uri("https://demo.api.weu.digitaltwins.azure.net"), _ => ValueTask.FromResult("adt-token"));

        var twins = await adt.QueryGeoAsync("SELECT * FROM digitaltwins");
        Assert.Equal(2, twins.RowCount);
        Assert.Equal("""{"query":"SELECT * FROM digitaltwins","continuationToken":"next"}""", handler.Calls[1].Body);
        Assert.Equal("https://demo.api.weu.digitaltwins.azure.net/query?api-version=2023-10-31", handler.Calls[0].Request.RequestUri!.ToString());
        Assert.Equal("Bearer adt-token", handler.Calls[0].Request.Headers.Authorization!.ToString());
        Assert.Equal(["pump-1", "tank-1"], new[] { twins["$dtId"].GetValue(0), twins["$dtId"].GetValue(1) });
        Assert.Equal("dtmi:demo:Tank;1", twins["$model"].GetValue(1));
        Assert.NotNull(twins.Geometry[0]);
        Assert.Null(twins.Geometry[1]);
        Assert.True(double.IsNaN(twins["Pressure"].GetDouble(1)));
        Assert.Equal(true, twins["Running"].GetValue(0));

        await adt.UpdatePropertiesAsync("room/1", new Dictionary<string, object?> { ["Temperature"] = 22.5, ["a/b~c"] = "x" });
        var patch = handler.Calls[^1];
        Assert.Equal(HttpMethod.Patch, patch.Request.Method);
        Assert.Equal("https://demo.api.weu.digitaltwins.azure.net/digitaltwins/room%2F1?api-version=2023-10-31", patch.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json-patch+json", patch.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""[{"op":"add","path":"/Temperature","value":22.5},{"op":"add","path":"/a~1b~0c","value":"x"}]""", patch.Body);

        await adt.PublishTelemetryAsync("pump-1", new Dictionary<string, object?> { ["flow"] = 12.0 }, "m-1");
        Assert.Equal("m-1", handler.Calls[^1].Request.Headers.GetValues("Message-Id").Single());

        var layer = new LiveLayer("assets", new Envelope(106, 107, -7, -6), CoordinateReferenceSystem.Wgs84);
        layer.AddZone("plant", Geometry.Geo.Box(106.7, -6.3, 106.9, -6.1));
        layer.Ingest(new SensorReading("pump-1", Clock, 106.8, -6.2, new Dictionary<string, double> { ["Pressure"] = 2.9 }));
        layer.Ingest(new SensorReading("pump-2", Clock, 106.5, -6.5, new Dictionary<string, double> { ["Flow"] = 4 }));
        Assert.Equal(2, await adt.SyncAsync(layer, id => "twin-" + id));
        var synced = handler.Calls.TakeLast(2).ToList();
        Assert.EndsWith("/digitaltwins/twin-pump-1?api-version=2023-10-31", synced[0].Request.RequestUri!.ToString());
        Assert.Equal("""[{"op":"add","path":"/Longitude","value":106.8},{"op":"add","path":"/Latitude","value":-6.2},{"op":"add","path":"/Pressure","value":2.9},{"op":"add","path":"/Zones","value":"plant"}]""", synced[0].Body);
        Assert.Contains("\"path\":\"/Flow\",\"value\":4", synced[1].Body);
        Assert.DoesNotContain("Pressure", synced[1].Body);
    }
}
