using GeoPandas.Net.Analytics;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Network;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Tests;

public class AnalyticsTests
{
    private static GeoDataFrame Grid(int n, Func<int, int, double> value)
    {
        var geoms = new List<NtsGeometry?>(); var vals = new List<double>();
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) { geoms.Add(Geo.Box(c, r, c + 1, r + 1)); vals.Add(value(r, c)); }
        return GeoDataFrame.FromGeometries(geoms, null, Column.Create("v", vals.ToArray()));
    }

    [Fact]
    public void QueenAndRook_OnGrid()
    {
        var g = Grid(5, (_, _) => 0);
        var q = SpatialWeights.Queen(g);
        var r = SpatialWeights.Rook(g);
        Assert.Equal(8, q.Neighbors[12].Length); // centre cell
        Assert.Equal(4, r.Neighbors[12].Length);
        Assert.Equal(3, q.Neighbors[0].Length);  // corner
        Assert.Equal(2, r.Neighbors[0].Length);
    }

    [Fact]
    public void MoransI_DetectsPatterns()
    {
        var checker = Grid(8, (r, c) => (r + c) % 2);
        var w = SpatialWeights.Rook(checker).RowStandardize();
        var mi = Autocorrelation.MoransI(checker["v"].ToDoubleArray(), w, 199);
        Assert.True(mi.Value < -0.9, mi.ToString());

        var gradient = Grid(8, (r, c) => r + c);
        var mi2 = Autocorrelation.MoransI(gradient["v"].ToDoubleArray(), SpatialWeights.Queen(gradient).RowStandardize(), 199);
        Assert.True(mi2.Value > 0.7, mi2.ToString());
        Assert.True(mi2.PValueSimulated < 0.01);
        Assert.True(mi2.ZScore > 3);

        var gc = Autocorrelation.GearysC(gradient["v"].ToDoubleArray(), SpatialWeights.Queen(gradient), 99);
        Assert.True(gc.Value < 0.5, gc.ToString());
    }

    [Fact]
    public void LocalMoran_And_GiStar_FindHotSpot()
    {
        var g = Grid(9, (r, c) => r >= 6 && c >= 6 ? 100 : 1);
        var w = SpatialWeights.Queen(g);
        var lisa = Autocorrelation.LocalMoran(g["v"].ToDoubleArray(), w, 299);
        Assert.Equal("HH", lisa.Quadrants[7 * 9 + 7]);
        var hot = Autocorrelation.HotSpots(g, "v", w);
        Assert.StartsWith("Hot spot", (string)hot["hotspot"].GetValue(7 * 9 + 7)!);
    }

    [Fact]
    public void Dbscan_And_KMeans()
    {
        var r = new Random(1);
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var (cx, cy) in new[] { (0.0, 0.0), (100.0, 100.0) })
            for (int i = 0; i < 50; i++) { xs.Add(cx + r.NextDouble() * 5); ys.Add(cy + r.NextDouble() * 5); }
        xs.Add(50); ys.Add(50); // noise
        var labels = Clustering.Dbscan(xs.ToArray(), ys.ToArray(), eps: 3, minPoints: 4);
        Assert.Equal(2, labels.Where(l => l >= 0).Distinct().Count());
        Assert.Equal(-1, labels[^1]);
        var (km, centroids, _) = Clustering.KMeans(xs.ToArray(), ys.ToArray(), 2);
        Assert.Equal(2, km.Take(100).Distinct().Count());
        Assert.Contains(centroids, c => c.X < 10);
        Assert.Contains(centroids, c => c.X > 90);
    }

    [Fact]
    public void Idw_And_Kriging_AreExactAtSamples()
    {
        double[] x = [0, 10, 0, 10, 5], y = [0, 0, 10, 10, 5], z = [1, 2, 3, 4, 10];
        var idw = Interpolation.Idw(x, y, z, [5.0], [5.0]);
        Assert.Equal(10, idw[0], 6);
        var (k, var) = Interpolation.OrdinaryKriging(x, y, z, [0.0, 2.0], [0.0, 2.0], new Variogram(VariogramModel.Spherical, 0, 10, 20));
        Assert.Equal(1, k[0], 6);
        Assert.Equal(0, var[0], 6);
        Assert.InRange(k[1], 1, 10);
    }
}

public class NetworkTests
{
    private static GeoDataFrame GridLines(int n)
    {
        var lines = new List<NtsGeometry?>();
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                if (i < n) lines.Add(Geo.LineString((i, j), (i + 1, j)));
                if (j < n) lines.Add(Geo.LineString((i, j), (i, j + 1)));
            }
        return GeoDataFrame.FromGeometries(lines, null);
    }

    [Fact]
    public void Grid_ShortestPath_IsManhattan()
    {
        var g = NetworkGraph.FromLines(GridLines(10));
        Assert.Equal(121, g.NodeCount);
        var route = g.ShortestPath(Geo.Point(0, 0), Geo.Point(7, 4));
        Assert.True(route.Found);
        Assert.Equal(11, route.Cost, 9);
        Assert.Equal(11, route.Geometry!.Length, 9);
        Assert.Equal(12, route.Nodes.Length);
    }

    [Fact]
    public void AllAlgorithms_Agree_OnCityRoads()
    {
        var roads = SampleData.Roads();
        var g = NetworkGraph.FromLines(roads, costColumn: "travel_time_s", onewayColumn: "oneway");
        var ch = g.BuildContractionHierarchy();
        var comp = g.ConnectedComponents();
        var main = comp.GroupBy(c => c).OrderByDescending(x => x.Count()).First().Key;
        var nodes = Enumerable.Range(0, g.NodeCount).Where(i => comp[i] == main).ToArray();
        var r = new Random(3);
        int checkedPairs = 0;
        for (int k = 0; k < 40; k++)
        {
            int s = nodes[r.Next(nodes.Length)], t = nodes[r.Next(nodes.Length)];
            var dj = g.ShortestPath(s, t, PathAlgorithm.Dijkstra);
            var astar = g.ShortestPath(s, t, PathAlgorithm.AStar);
            var bi = g.ShortestPath(s, t, PathAlgorithm.BidirectionalDijkstra);
            var chr = ch.Route(s, t);
            Assert.Equal(dj.Found, chr.Found);
            if (!dj.Found) continue;
            checkedPairs++;
            Assert.Equal(dj.Cost, astar.Cost, 6);
            Assert.Equal(dj.Cost, bi.Cost, 6);
            Assert.Equal(dj.Cost, chr.Cost, 6);
            Assert.Equal(dj.Cost, chr.Edges.Sum(e => g.Edges[e].Cost), 6);
            Assert.Equal(dj.Cost, bi.Edges.Sum(e => g.Edges[e].Cost), 6);
        }
        Assert.True(checkedPairs > 20);
    }

    [Fact]
    public void Oneway_IsRespected()
    {
        var lines = GeoDataFrame.FromGeometries([Geo.LineString((0, 0), (1, 0)), Geo.LineString((1, 0), (1, 5)), Geo.LineString((1, 5), (0, 0))], null,
            Column.Create("oneway", new[] { true, false, false }));
        var g = NetworkGraph.FromLines(lines, onewayColumn: "oneway");
        Assert.Equal(1, g.ShortestPath(Geo.Point(0, 0), Geo.Point(1, 0)).Cost, 9);
        Assert.True(g.ShortestPath(Geo.Point(1, 0), Geo.Point(0, 0)).Cost > 5);
    }

    [Fact]
    public void ServiceArea_And_UtilityTrace()
    {
        var g = NetworkGraph.FromLines(GridLines(10));
        var (edges, poly) = g.ServiceArea(g.NearestNode(5, 5), 2, 0.2);
        Assert.True(edges.RowCount > 4);
        Assert.True(poly.Contains(Geo.Point(5, 6)));
        Assert.False(poly.Contains(Geo.Point(9, 9)));

        var water = SampleData.WaterNetwork();
        var wg = NetworkGraph.FromLines(water, directed: true);
        int source = wg.NearestNode(SampleData.CenterX - 6_500, SampleData.CenterY - 4_000);
        var all = wg.Trace(source, downstreamOnly: true);
        var open = wg.Trace(source, e => (string?)water["valve"].GetValue(e.SourceRow) == "open", downstreamOnly: true);
        Assert.Equal(wg.Edges.Count, all.Edges.Length);
        Assert.True(open.Edges.Length < all.Edges.Length);
    }
}
