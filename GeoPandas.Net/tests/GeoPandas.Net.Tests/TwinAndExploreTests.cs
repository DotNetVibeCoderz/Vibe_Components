using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GeoPandas.Net.Data;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Plotting;
using GeoPandas.Net.Twin;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

public sealed class TwinAndExploreTests
{
    // ------------------------------------------------------------------ explore

    [Fact]
    public void Explore_EmbedsColouredGeoJsonLegendAndPinnedMapLibre()
    {
        var d = Datasets.SampleData.Districts().WithColumn<double>("density", r => r.GetDouble("population") / r.GetDouble("area_km2"));
        var map = d.Explore("density", ColorMaps.YlOrRd, tooltip: ["name", "density"], basemap: "none", title: "Density");
        var html = map.ToHtml();
        Assert.Contains("maplibre-gl@4.7.1/dist/maplibre-gl.js", html);
        Assert.Contains("Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil", html);
        var json = Regex.Match(html, @"const layers=(\[.*?\]);const map=", RegexOptions.Singleline).Groups[1].Value;
        var layers = JsonNode.Parse(json)!.AsArray();
        var features = layers[0]!["data"]!["features"]!.AsArray();
        Assert.Equal(d.RowCount, features.Count);
        // the colours are the quantile classes of Plot(): five distinct swatches, highest density → darkest
        var fills = features.Select(f => (string)f!["properties"]!["__fill"]!).ToList();
        Assert.Equal(5, fills.Distinct().Count());
        var breaks = Classifier.Breaks(d["density"].ToDoubleArray(), 5, ClassificationScheme.Quantiles);
        var swatches = ColorMaps.YlOrRd.Discrete(5);
        for (int i = 0; i < d.RowCount; i++)
        {
            var c = swatches[Classifier.ClassOf(d["density"].GetDouble(i), breaks)];
            Assert.Equal($"#{c.R:x2}{c.G:x2}{c.B:x2}", fills[i]);
        }
        Assert.InRange((double)features[0]!["geometry"]!["coordinates"]![0]![0]![0]!, 106, 108);   // WGS 84
        Assert.Equal(["name", "density"], layers[0]!["tooltip"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(5, Regex.Matches(html, "<div><span style=\"background:").Count);
        // categorical layer
        var cats = new ExploreMap { Basemap = "none" }.Add(Datasets.SampleData.PointsOfInterest().Head(50), new ExploreStyle { Column = "category" }).ToHtml();
        Assert.Contains("\"__fill\":\"#4e79a7\"", cats);
    }

    // ------------------------------------------------------------------ twin

    private static LiveLayer Layer() =>
        new LiveLayer("fleet", new Envelope(0, 1000, 0, 1000), historyWindow: TimeSpan.FromMinutes(30))
            .AddZone("A", Geo.Box(0, 0, 100, 100))
            .AddZone("B", Geo.Box(200, 0, 300, 100))
            .AddRule(new ThresholdRule("hot", "temp", Above: 40, Hysteresis: 2));

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);
    private static SensorReading R(string id, int minute, double x, double y, double temp) =>
        new(id, T0.AddMinutes(minute), x, y, new Dictionary<string, double> { ["temp"] = temp });

    [Fact]
    public void LiveLayer_GeofenceEventsThresholdsAndStatistics()
    {
        var layer = Layer();
        var events = new List<TwinEvent>();
        layer.EventRaised += events.Add;
        layer.Ingest(R("truck", 0, 50, 50, 30));     // enters A
        layer.Ingest(R("truck", 1, 150, 50, 41));    // leaves A, hot
        layer.Ingest(R("truck", 2, 250, 50, 39.5));  // enters B, still within hysteresis (> 38) → no clear
        layer.Ingest(R("truck", 3, 260, 60, 37.9));  // cleared
        layer.Ingest(R("truck", 4, 260, 60, 41));    // hot again
        layer.Ingest(R("car", 4, 20, 20, 25));
        Assert.Equal(
        [
            (TwinEventKind.ZoneEnter, "A"), (TwinEventKind.ZoneExit, "A"), (TwinEventKind.ThresholdExceeded, null), (TwinEventKind.ZoneEnter, "B"),
            (TwinEventKind.ThresholdCleared, null), (TwinEventKind.ThresholdExceeded, null), (TwinEventKind.ZoneEnter, "A"),
        ], events.Select(e => (e.Kind, e.Zone)).ToList<(TwinEventKind, string?)>());
        Assert.Equal(["truck"], layer.InZone("B").Select(s => s.EntityId));
        Assert.Equal(["car"], layer.Near(0, 0, 50).Select(s => s.EntityId));
        Assert.Equal(5, layer.Trajectory("truck")!.NumPoints);
        var stats = layer.ZoneStatistics("temp", TimeSpan.FromMinutes(10), T0.AddMinutes(5));
        int b = Enumerable.Range(0, stats.RowCount).Single(i => (string)stats["zone"].GetValue(i)! == "B");
        Assert.Equal(3L, stats["count"].GetValue(b));
        Assert.Equal((39.5 + 37.9 + 41) / 3, stats["mean"].GetDouble(b), 12);
        var snap = layer.Snapshot();
        Assert.Equal(2, snap.RowCount);
        Assert.Equal("B", snap["zones"].GetValue(Enumerable.Range(0, 2).Single(i => (string)snap["entity"].GetValue(i)! == "truck")));
        Assert.Single(layer.CheckStale(T0.AddMinutes(20), TimeSpan.FromMinutes(15)), e => e.EntityId == "truck");
        Assert.DoesNotContain(layer.CheckStale(T0.AddMinutes(21), TimeSpan.FromMinutes(15)), e => e.EntityId == "truck");   // only once
    }

    [Fact]
    public async Task LiveLayer_ConcurrentIngestionLosesNothing()
    {
        var layer = new LiveLayer("sensors", new Envelope(0, 1000, 0, 1000)).AddZone("all", Geo.Box(0, 0, 1000, 1000));
        int total = 0;
        layer.EventRaised += e => { if (e.Kind == TwinEventKind.ZoneEnter) Interlocked.Increment(ref total); };
        await Parallel.ForAsync(0, 20_000, (i, _) =>
        {
            layer.Ingest(new SensorReading($"s{i % 500}", T0.AddSeconds(i), (i * 37) % 1000, (i * 91) % 1000, new Dictionary<string, double> { ["v"] = i }));
            return ValueTask.CompletedTask;
        });
        Assert.Equal(20_000, layer.ReadingCount);
        Assert.Equal(500, layer.EntityCount);
        Assert.Equal(500, total);              // each sensor enters the zone exactly once
        Assert.Equal(500, layer.Near(500, 500, 2000).Count);
    }

    [Fact]
    public void TwinJson_AndBuildingRollUp()
    {
        var utm = CoordinateReferenceSystem.FromEpsg(32748);
        var r = TwinJson.Parse("""{"id":"th-7","time":"2026-11-18T08:00:00Z","lon":106.8,"lat":-6.2,"values":{"temp":27.5,"rh":71}}""", utm);
        Assert.Equal("th-7", r.EntityId);
        Assert.Equal(27.5, r.Values["temp"]);
        var (x, y) = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, utm).Transform(106.8, -6.2);
        Assert.Equal(x, r.X, 6); Assert.Equal(y, r.Y, 6);
        var flat = TwinJson.Parse("""{"entity":"m1","time":1763452800000,"x":10,"y":20,"flow":3.5}""");
        Assert.Equal(3.5, flat.Values["flow"]);

        var buildings = GeoDataFrame.FromGeometries([Geo.Box(0, 0, 50, 50), Geo.Box(100, 0, 150, 50)], null, Column.Create("bid", new[] { "B1", "B2" }));
        var twin = new DigitalTwin("campus").AddStatic("buildings", buildings);
        var live = twin.AddLive("climate", zonesFrom: "buildings", zoneIdColumn: "bid");
        live.Ingest(R("t1", 0, 10, 10, 22)); live.Ingest(R("t2", 0, 20, 30, 24)); live.Ingest(R("t3", 0, 120, 10, 30));
        var rolled = twin.RollUp("climate", "buildings", "bid", "temp", TimeSpan.FromHours(1), T0.AddMinutes(1));
        Assert.Equal(23, rolled["temp_mean"].GetDouble(0), 12);
        Assert.Equal(30, rolled["temp_max"].GetDouble(1), 12);
    }
}
