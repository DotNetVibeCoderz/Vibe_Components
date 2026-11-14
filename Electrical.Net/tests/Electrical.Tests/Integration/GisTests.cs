using System.Text.Json;
using System.Xml.Linq;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.GIS;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class GisTests
{
    [Fact]
    public void WebMercator_KnownValuesAndInverse()
    {
        var (x0, y0) = WebMercator.Project(new GeoPoint(0, 0));
        Assert.Equal(0, x0, 9);
        Assert.Equal(0, y0, 6);
        var (x, y) = WebMercator.Project(new GeoPoint(WebMercator.MaxLatitude, 180));
        Assert.Equal(20037508.342789244, x, 3); // EPSG:3857 half-world extent
        Assert.Equal(20037508.342789244, y, 3);
        var jakarta = new GeoPoint(-6.2088, 106.8456);
        var (jx, jy) = WebMercator.Project(jakarta);
        var back = WebMercator.Unproject(jx, jy);
        Assert.Equal(jakarta.Latitude, back.Latitude, 10);
        Assert.Equal(jakarta.Longitude, back.Longitude, 10);
        Assert.Equal((0, 0), WebMercator.Tile(new GeoPoint(10, 10), 0));
        Assert.Equal((5, 2), WebMercator.Tile(new GeoPoint(45, 45), 3)); // OSM slippy-map tile numbering
        var (px, py) = WebMercator.ToPixel(jakarta, 12);
        var p2 = WebMercator.FromPixel(px, py, 12);
        Assert.Equal(jakarta.Longitude, p2.Longitude, 9);
    }

    [Fact]
    public void Haversine_AndLineLengthAssignment()
    {
        // Paris – London great-circle distance ≈ 343.5 km
        double d = Geo.HaversineKm(new GeoPoint(48.8566, 2.3522), new GeoPoint(51.5074, -0.1278));
        Assert.InRange(d, 342.5, 344.5);
        var net = TestCases.SyntheticGrid(16);
        foreach (var br in net.Branches) br.LengthKm = 0;
        int n = LineLengths.Assign(net);
        Assert.Equal(net.Branches.Count, n);
        // horizontal neighbours are 0.05° of longitude apart at ≈ 6.2° S: 0.05 · 111.195 · cos(6.2°) ≈ 5.53 km
        Assert.Equal(0.05 * Math.PI * Geo.EarthRadiusKm / 180 * Math.Cos(6.2 * Math.PI / 180), net.Branches[0].LengthKm, 3);
        var path = new[] { new GeoPoint(0, 0), new GeoPoint(0, 1), new GeoPoint(1, 1) };
        Assert.Equal(2 * Math.PI * Geo.EarthRadiusKm / 180, Geo.PathLengthKm(path), 3); // 2 × 111.195 km (1° on the equator + 1° along a meridian)
        var bounds = GeoBounds.Of(path);
        Assert.Equal(new GeoBounds(0, 0, 1, 1), bounds);
        Assert.True(bounds.Contains(new GeoPoint(0.5, 0.5)));
        Assert.False(bounds.Contains(new GeoPoint(1.5, 0.5)));
        Assert.True(bounds.Expand(10).Contains(new GeoPoint(1.05, 1.05)));
    }

    [Fact]
    public void GeoJson_ExportWithResults_AndImportRoundTrip()
    {
        var net = TestCases.SyntheticGrid(25);
        var pf = PowerFlowSolver.Solve(net);
        string json = GeoJsonExporter.Export(net, pf);
        using (var doc = JsonDocument.Parse(json))
        {
            var features = doc.RootElement.GetProperty("features").EnumerateArray().ToList();
            Assert.Equal(25, features.Count(f => f.GetProperty("properties").GetProperty("kind").GetString() == "bus"));
            var line = features.First(f => f.GetProperty("geometry").ValueKind == JsonValueKind.Object && f.GetProperty("geometry").GetProperty("type").GetString() == "LineString");
            Assert.True(line.GetProperty("properties").GetProperty("loading").GetDouble() >= 0);
            var bus5 = features.First(f => f.GetProperty("id").GetString() == "bus-5");
            Assert.Equal(pf.Bus(5).Vm, bus5.GetProperty("properties").GetProperty("vm").GetDouble(), 12);
            var coords = bus5.GetProperty("geometry").GetProperty("coordinates");
            Assert.Equal(net.GetBus(5).Location!.Value.Longitude, coords[0].GetDouble()); // [lon, lat]
        }
        var imported = GeoJsonImporter.Import(json);
        Assert.Empty(imported.Warnings);
        var back = imported.Network;
        Assert.Equal(net.BusCount, back.BusCount);
        Assert.Equal(net.Branches.Count, back.Branches.Count);
        Assert.Equal(net.Generators.Count, back.Generators.Count);
        Assert.Equal(pf.LossesMw, PowerFlowSolver.Solve(back).LossesMw, 9);
    }

    [Fact]
    public void GeoJson_Import_SnapsLinesWithoutTerminals()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[106.80,-6.20]},"properties":{"id":1,"baseKv":20,"type":"Slack"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[106.85,-6.20]},"properties":{"id":2,"baseKv":20}},
              {"type":"Feature","geometry":{"type":"LineString","coordinates":[[106.8001,-6.2],[106.82,-6.21],[106.8499,-6.2]]},"properties":{"name":"routed"}}
            ]}
            """;
        var r = GeoJsonImporter.Import(json, snapToleranceKm: 0.05);
        var br = Assert.Single(r.Network.Branches);
        Assert.Equal((1, 2), (br.From, br.To));
        Assert.True(r.BranchPaths.ContainsKey(0));
        Assert.True(br.LengthKm > new GeoPoint(-6.2, 106.8).DistanceKm(new GeoPoint(-6.2, 106.85)) * 0.99);
        var target = TestCases.Case9();
        Assert.Equal(2, GeoJsonImporter.ApplyLocations(target, json));
        Assert.Equal(new GeoPoint(-6.20, 106.85), target.GetBus(2).Location);
    }

    [Fact]
    public void SpatialIndex_MatchesBruteForce()
    {
        var rng = new Random(123);
        var items = Enumerable.Range(1, 500).Select(i => (i, new GeoPoint(-8 + rng.NextDouble() * 4, 105 + rng.NextDouble() * 6))).ToList();
        var index = new BusSpatialIndex(items);
        for (int q = 0; q < 50; q++)
        {
            var p = new GeoPoint(-8 + rng.NextDouble() * 4, 105 + rng.NextDouble() * 6);
            var brute = items.Select(t => (t.i, d: p.DistanceKm(t.Item2))).OrderBy(t => t.d).ToList();
            var hit = index.Nearest(p)!.Value;
            Assert.Equal(brute[0].i, hit.BusId);
            Assert.Equal(brute[0].d, hit.DistanceKm, 6);
            var k5 = index.KNearest(p, 5);
            Assert.Equal(brute.Take(5).Select(t => t.i), k5.Select(h => h.BusId));
            var within = index.WithinRadius(p, 40);
            Assert.Equal(brute.Where(t => t.d <= 40).Select(t => t.i).Order(), within.Select(h => h.BusId).Order());
        }
        var net = TestCases.SyntheticGrid(36);
        var nearest = new BusSpatialIndex(net).Nearest(new GeoPoint(-6.2001, 106.8501));
        Assert.Equal(2, nearest!.Value.BusId);
    }

    [Fact]
    public void ForceDirectedLayout_IsDeterministicAndCompact()
    {
        var center = new GeoPoint(-6.9, 107.6);
        var a = TestCases.Case14();
        var b = TestCases.Case14();
        Assert.Equal(14, GeoLayout.ForceDirected(a, center, new GeoLayoutOptions { RadiusKm = 15 }));
        GeoLayout.ForceDirected(b, center, new GeoLayoutOptions { RadiusKm = 15 });
        Assert.Equal(a.Buses.Select(x => x.Location), b.Buses.Select(x => x.Location));
        foreach (var bus in a.Buses) Assert.InRange(center.DistanceKm(bus.Location!.Value), 0, 15.5);
        double minD = double.MaxValue;
        for (int i = 0; i < 14; i++)
            for (int j = i + 1; j < 14; j++) minD = Math.Min(minD, a.Buses[i].Location!.Value.DistanceKm(a.Buses[j].Location!.Value));
        Assert.True(minD > 0.3, $"buses too close: {minD} km");
        // Adjacent buses should on average be closer than non-adjacent ones (force-directed property).
        var adj = new HashSet<(int, int)>(a.Branches.Select(br => (Math.Min(br.From, br.To), Math.Max(br.From, br.To))));
        var pairs = from x in a.Buses from y in a.Buses where x.Id < y.Id select (x, y, adj.Contains((x.Id, y.Id)), x.Location!.Value.DistanceKm(y.Location!.Value));
        var list = pairs.ToList();
        Assert.True(list.Where(t => t.Item3).Average(t => t.Item4) < list.Where(t => !t.Item3).Average(t => t.Item4));
        // Existing locations act as fixed anchors.
        var c = TestCases.SyntheticGrid(9);
        var anchor = c.Buses[0].Location;
        foreach (var bus in c.Buses.Skip(1)) bus.Location = null;
        Assert.Equal(8, GeoLayout.ForceDirected(c, anchor!.Value));
        Assert.Equal(anchor, c.Buses[0].Location);
    }

    [Fact]
    public void OutageArea_ConvexHullOfDeEnergizedBuses()
    {
        var square = new[] { new GeoPoint(0, 0), new GeoPoint(0, 1), new GeoPoint(1, 1), new GeoPoint(1, 0), new GeoPoint(0.5, 0.5), new GeoPoint(0.2, 0.7) };
        Assert.Equal(4, OutageArea.ConvexHull(square).Count);
        var net = TestCases.Case33BW();
        GeoLayout.ForceDirected(net, new GeoPoint(-6.2, 106.8), new GeoLayoutOptions { RadiusKm = 5 });
        net.Branches.First(br => br.From == 6 && br.To == 7).InService = false;
        var dead = OutageArea.DeEnergizedBuses(net);
        Assert.Equal(Enumerable.Range(7, 12), dead); // buses 7..18 lose supply
        var ring = OutageArea.Polygon(net, dead, bufferKm: 0.3);
        Assert.True(ring.Count >= 3);
        Assert.All(dead, id => Assert.True(OutageArea.Contains(ring, net.GetBus(id).Location!.Value)));
        Assert.True(OutageArea.AreaKm2(ring) > 0.25);
        string feature = GeoJsonExporter.Polygon(ring, new Dictionary<string, string> { ["kind"] = "outage" });
        using var doc = JsonDocument.Parse(feature);
        var outer = doc.RootElement.GetProperty("geometry").GetProperty("coordinates")[0];
        Assert.Equal(outer[0].GetRawText(), outer[outer.GetArrayLength() - 1].GetRawText()); // closed ring
    }

    [Fact]
    public void SvgMap_UsesCssVariablesAndColorsByState()
    {
        var net = TestCases.Case14();
        GeoLayout.ForceDirected(net, new GeoPoint(40.0, -75.0));
        foreach (var br in net.Branches) br.RateA = 60;
        var pf = PowerFlowSolver.Solve(net);
        string svg = GisSvgRenderer.Render(net, pf, new GisSvgOptions { Title = "IEEE 14" });
        var doc = XDocument.Parse(svg); // well-formed
        XNamespace s = "http://www.w3.org/2000/svg";
        Assert.Equal(20, doc.Descendants(s + "polyline").Count());
        Assert.Equal(14, doc.Descendants().Count(e => (string?)e.Attribute("class") == "eln-bus"));
        Assert.Contains("var(--eln-bg, #0b1626)", svg);
        Assert.Contains("var(--eln-danger, #e5484d)", svg); // branch 1-2 carries > 150 MVA → overloaded at 60 MVA
        Assert.Contains(" km</text>", svg); // scale bar
        string continuous = GisSvgRenderer.Render(net, pf, new GisSvgOptions { ContinuousColors = true, ShowLegend = false });
        Assert.Contains(GisColors.LoadingRamp(pf.Branches[0].LoadingPercent), continuous);
        Assert.Equal("#2bb673", GisColors.VoltageRamp(1.0));
        Assert.Equal("#2f6bff", GisColors.VoltageRamp(0.85));
        Assert.Equal("#e5484d", GisColors.LoadingRamp(130));
        Assert.Equal(GisColors.VoltageLow, GisColors.VoltageBand(0.93));
    }

    [Fact]
    public void AddGis_RegistersService()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddGis()).BuildServiceProvider();
        var gis = sp.GetRequiredService<IGisService>();
        var net = TestCases.Case9();
        Assert.Equal(9, gis.Layout(net, new GeoPoint(-6.9, 107.6)));
        Assert.Equal(9, gis.AssignLengths(net));
        Assert.StartsWith("<svg", gis.RenderSvg(net));
    }
}
