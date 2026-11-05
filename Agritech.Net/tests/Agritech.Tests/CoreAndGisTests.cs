using Agritech.Core.Data;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;
using Agritech.GIS;
using Microsoft.Extensions.DependencyInjection;

namespace Agritech.Tests;

public class CoreTests
{
    [Fact]
    public void Haversine_JakartaToBandung_IsAbout117Km()
    {
        var d = GeoMath.Distance(new(-6.2088, 106.8456), new(-6.9175, 107.6191));
        Assert.InRange(d / 1000, 114, 120);
    }

    [Fact]
    public void Rectangle_AreaMatchesDimensions()
    {
        var p = GeoPolygon.Rectangle(new(-6.3, 107.3), 200, 100);
        Assert.InRange(p.AreaHectares, 1.99, 2.01);
        Assert.InRange(p.PerimeterM, 599, 601);
        Assert.True(p.Contains(new(-6.3, 107.3)));
    }

    [Fact]
    public void Destination_RoundTripsWithBearingAndDistance()
    {
        var a = new GeoCoordinate(-6.3, 107.3);
        var b = GeoMath.Destination(a, 45, 1000);
        Assert.InRange(GeoMath.Distance(a, b), 999.5, 1000.5);
        Assert.InRange(GeoMath.Bearing(a, b), 44.9, 45.1);
    }

    [Fact]
    public void Stats_LinearFitAndPercentile()
    {
        double[] x = [1, 2, 3, 4, 5], y = [3, 5, 7, 9, 11];
        var (a, b, r2) = Stats.LinearFit(x, y);
        Assert.Equal(1, a, 9); Assert.Equal(2, b, 9); Assert.Equal(1, r2, 9);
        Assert.Equal(3, Stats.Median(x));
        Assert.Equal(1.5811, Stats.StdDev(x), 3);
    }

    [Fact]
    public void Matrix_SolvesLinearSystem()
    {
        var a = Matrix.FromRows([[2, 1, -1], [-3, -1, 2], [-2, 1, 2]]);
        var x = Matrix.Solve(a, [8, -11, -3]);
        Assert.Equal(2, x[0], 9); Assert.Equal(3, x[1], 9); Assert.Equal(-1, x[2], 9);
    }

    [Fact]
    public void TimeSeries_InsertsOutOfOrderAndQueriesRange()
    {
        var ts = new TimeSeries<double>("t");
        var t0 = DateTimeOffset.UnixEpoch;
        ts.Add(t0.AddHours(2), 2); ts.Add(t0, 0); ts.Add(t0.AddHours(1), 1);
        Assert.Equal([0, 1, 2], ts.Values());
        Assert.Equal(2, ts.Range(t0, t0.AddHours(2)).Count());
    }

    [Fact]
    public void Otsu_SeparatesBimodalData()
    {
        var v = Enumerable.Repeat(0.1, 100).Concat(Enumerable.Repeat(0.8, 100)).ToArray();
        double t = Stats.OtsuThreshold(v);
        Assert.InRange(t, 0.1, 0.8);
    }

    [Fact]
    public void EventBus_PublishesAndUnsubscribes()
    {
        var bus = new InMemoryEventBus();
        int n = 0;
        var sub = bus.Subscribe<TestEvent>(_ => n++);
        bus.Publish(new TestEvent(DateTimeOffset.Now));
        sub.Dispose();
        bus.Publish(new TestEvent(DateTimeOffset.Now));
        Assert.Equal(1, n);
    }

    private sealed record TestEvent(DateTimeOffset Timestamp) : IAgriEvent;

    [Fact]
    public void DependencyInjection_RegistersModules()
    {
        var sp = new ServiceCollection().AddAgritech(b => Crops.CropsBuilderExtensions.AddCrops(b)).BuildServiceProvider();
        Assert.Contains("Crops", sp.GetRequiredService<AgritechModuleRegistry>().Modules);
        Assert.NotNull(sp.GetRequiredService<Crops.CropRecommender>());
    }
}

public class GisTests
{
    [Fact]
    public void Utm_RoundTripIsSubMillimetre()
    {
        var c = new GeoCoordinate(-6.3045, 107.3012);
        var u = Projections.ToUtm(c);
        Assert.Equal(48, u.Zone);
        Assert.False(u.IsNorth);
        Assert.Equal(32748, u.Epsg);
        var back = Projections.FromUtm(u);
        Assert.InRange(GeoMath.Distance(c, back), 0, 0.001);
    }

    [Fact]
    public void Utm_KnownPoint()
    {
        // Monas, Jakarta ≈ 48M 702,000 E, 9,313,700 N
        var u = Projections.ToUtm(new GeoCoordinate(-6.1754, 106.8272));
        Assert.InRange(u.Easting, 701_500, 702_500);
        Assert.InRange(u.Northing, 9_316_500, 9_317_500);
    }

    [Fact]
    public void Geohash_EncodesKnownValue()
    {
        Assert.Equal("u4pruydqq", Geohash.Encode(new GeoCoordinate(57.64911, 10.40744), 9));
        var c = Geohash.Decode("u4pruydqq");
        Assert.Equal(57.64911, c.Latitude, 3);
        Assert.Equal(9, Geohash.Neighborhood("qqguf").Length);
    }

    [Fact]
    public void QuadTree_RadiusQuery()
    {
        var qt = new QuadTree<int>();
        var center = new GeoCoordinate(-6.3, 107.3);
        for (int i = 0; i < 500; i++) qt.Insert(GeoMath.Destination(center, i * 7 % 360, i * 10), i);
        var hits = qt.Within(center, 1000.5); // points at 0,10,…,1000 m
        Assert.All(hits, h => Assert.True(h.DistanceM <= 1000.5));
        Assert.Equal(101, hits.Count);
        Assert.Equal(0, qt.Nearest(center)!.Value.Item);
    }

    [Fact]
    public void GeoJson_RoundTrip()
    {
        var poly = GeoPolygon.Rectangle(new(-6.3, 107.3), 100, 50);
        var json = GeoJson.Write([GeoJson.Feature(poly, ("name", "Blok A"), ("area", 0.5))]);
        var f = Assert.Single(GeoJson.ReadFeatures(json));
        Assert.Equal("Blok A", f.GetString("name"));
        var p = Assert.IsType<PolygonGeometry>(f.Geometry);
        Assert.InRange(p.Polygon.AreaHectares, 0.49, 0.51);
    }

    [Fact]
    public void DouglasPeucker_RemovesCollinearPoints()
    {
        var a = new GeoCoordinate(0, 0);
        var line = Enumerable.Range(0, 11).Select(i => GeoMath.Destination(a, 90, i * 10)).ToList();
        Assert.Equal(2, Geometry.Simplify(line, 0.5).Count);
    }
}
