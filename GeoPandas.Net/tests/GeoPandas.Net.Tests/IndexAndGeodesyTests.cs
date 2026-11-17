using GeoPandas.Net.Geometry.Index;
using GeoPandas.Net.Geometry.Native;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

public class IndexAndGeodesyTests
{
    private static (double[] X, double[] Y) RandomPoints(int n, int seed)
    {
        var r = new Random(seed);
        return (Enumerable.Range(0, n).Select(_ => r.NextDouble() * 1000).ToArray(), Enumerable.Range(0, n).Select(_ => r.NextDouble() * 1000).ToArray());
    }

    [Fact]
    public void StrTree_Query_MatchesBruteForce()
    {
        var r = new Random(1);
        var envs = Enumerable.Range(0, 3000).Select(i =>
        {
            double x = r.NextDouble() * 1000, y = r.NextDouble() * 1000;
            return (new Envelope(x, x + r.NextDouble() * 20, y, y + r.NextDouble() * 20), i);
        }).ToList();
        var tree = new StrTree<int>(envs);
        for (int q = 0; q < 50; q++)
        {
            double x = r.NextDouble() * 1000, y = r.NextDouble() * 1000;
            var search = new Envelope(x, x + 60, y, y + 60);
            var expected = envs.Where(e => e.Item1.Intersects(search)).Select(e => e.i).Order().ToArray();
            Assert.Equal(expected, tree.Query(search).Order().ToArray());
        }
    }

    [Fact]
    public void StrTree_Nearest_MatchesBruteForce()
    {
        var (xs, ys) = RandomPoints(2000, 2);
        var tree = new StrTree<int>(Enumerable.Range(0, xs.Length).Select(i => (new Envelope(xs[i], xs[i], ys[i], ys[i]), i)).ToList());
        var r = new Random(3);
        for (int q = 0; q < 30; q++)
        {
            double x = r.NextDouble() * 1000, y = r.NextDouble() * 1000;
            var best = Enumerable.Range(0, xs.Length).OrderBy(i => Math.Pow(xs[i] - x, 2) + Math.Pow(ys[i] - y, 2)).Take(5).ToArray();
            var got = tree.Nearest(x, y, 5).Select(t => t.Item).ToArray();
            Assert.Equal(best, got);
        }
    }

    [Fact]
    public void KdTree_KnnAndRadius_MatchBruteForce()
    {
        var (xs, ys) = RandomPoints(5000, 4);
        var tree = new KdTree(xs, ys);
        var r = new Random(5);
        for (int q = 0; q < 30; q++)
        {
            double x = r.NextDouble() * 1000, y = r.NextDouble() * 1000;
            var brute = Enumerable.Range(0, xs.Length).Select(i => (i, d: Math.Sqrt(Math.Pow(xs[i] - x, 2) + Math.Pow(ys[i] - y, 2)))).OrderBy(t => t.d).ToArray();
            Assert.Equal(brute.Take(7).Select(t => t.i), tree.Nearest(x, y, 7).Select(t => t.Index));
            Assert.Equal(brute.Where(t => t.d <= 40).Select(t => t.i).Order(), tree.Radius(x, y, 40).Order());
        }
    }

    [Fact]
    public void QuadTree_InsertQueryRemove()
    {
        var qt = new QuadTree<int>(new Envelope(0, 100, 0, 100), maxItemsPerNode: 4);
        for (int i = 0; i < 200; i++) qt.Insert(new Envelope(i % 100, i % 100 + 1, i / 2.0 % 100, i / 2.0 % 100 + 1), i);
        qt.Insert(new Envelope(500, 501, 500, 501), 999); // forces growth
        Assert.Equal(201, qt.Count);
        Assert.Contains(999, qt.Query(new Envelope(499, 502, 499, 502)));
        Assert.True(qt.Remove(new Envelope(500, 501, 500, 501), 999));
        Assert.DoesNotContain(999, qt.Query(new Envelope(499, 502, 499, 502)));
    }

    [Fact]
    public void Octree_BoxQuery()
    {
        var r = new Random(6);
        var x = Enumerable.Range(0, 3000).Select(_ => r.NextDouble() * 100).ToArray();
        var y = Enumerable.Range(0, 3000).Select(_ => r.NextDouble() * 100).ToArray();
        var z = Enumerable.Range(0, 3000).Select(_ => r.NextDouble() * 100).ToArray();
        var oct = new Octree(x, y, z);
        var expected = Enumerable.Range(0, 3000).Where(i => x[i] is >= 10 and <= 30 && y[i] is >= 40 and <= 60 && z[i] is >= 0 and <= 50).Order();
        Assert.Equal(expected, oct.QueryBox(10, 40, 0, 30, 60, 50).Order());
    }

    [Fact]
    public void GeoHash_WikipediaExample()
    {
        Assert.Equal("u4pruydqqvj", GeoHash.Encode(10.40744, 57.64911, 11));
        var (lon, lat) = GeoHash.Decode("u4pruydqqvj");
        Assert.Equal(10.40744, lon, 4);
        Assert.Equal(57.64911, lat, 4);
    }

    [Fact]
    public void Vincenty_FlindersPeakToBuninyong()
    {
        // Classic geodesy test: 54 972.271 m, initial azimuth 306°52'05.37"
        double lat1 = -(37 + 57 / 60.0 + 3.72030 / 3600), lon1 = 144 + 25 / 60.0 + 29.52440 / 3600;
        double lat2 = -(37 + 39 / 60.0 + 10.15610 / 3600), lon2 = 143 + 55 / 60.0 + 35.38390 / 3600;
        var (d, az, _) = Geodesy.Vincenty(lon1, lat1, lon2, lat2);
        Assert.Equal(54972.271, d, 2);
        Assert.Equal(306 + 52 / 60.0 + 5.37 / 3600, az, 4);
    }

    [Fact]
    public void Haversine_JakartaBandung()
    {
        double d = Geodesy.Haversine(106.8456, -6.2088, 107.6191, -6.9175);
        Assert.InRange(d, 114_000, 118_000);
    }

    [Fact]
    public void GeodesicArea_OneDegreeSquareAtEquator()
    {
        var sq = Geo.Box(0, 0, 1, 1);
        double a = Geodesy.Area(sq);
        Assert.InRange(a, 12_300e6, 12_400e6); // ≈ 12 364 km² (sphere)
    }

    [Fact]
    public void NativeKernels_ManagedAndNativeAgree()
    {
        var (xs, ys) = RandomPoints(20000, 7);
        var outBox = new byte[xs.Length];
        NativeKernels.BoxFilter(xs, ys, 100, 100, 400, 600, outBox);
        for (int i = 0; i < xs.Length; i++)
            Assert.Equal(xs[i] >= 100 && xs[i] <= 400 && ys[i] >= 100 && ys[i] <= 600, outBox[i] == 1);

        double[] rx = [0, 1000, 500, 0], ry = [0, 0, 1000, 0];
        var pip = new byte[xs.Length];
        NativeKernels.PointInRing(xs, ys, rx, ry, pip);
        var tri = Geo.Polygon([(0, 0), (1000, 0), (500, 1000)]);
        int mismatches = Enumerable.Range(0, xs.Length).Count(i => (pip[i] == 1) != tri.Contains(Geo.Point(xs[i], ys[i])));
        Assert.Equal(0, mismatches);

        var lon = xs.Select(v => v / 10 - 50).ToArray(); var lat = ys.Select(v => v / 20 - 25).ToArray();
        var d = new double[xs.Length];
        NativeKernels.Haversine(lon, lat, lat, lon, d);
        for (int i = 0; i < 100; i++) Assert.Equal(Geodesy.Haversine(lon[i], lat[i], lat[i], lon[i]), d[i], 3);

        var (mn, mx) = NativeKernels.MinMax(xs);
        Assert.Equal(xs.Min(), mn);
        Assert.Equal(xs.Max(), mx);
    }
}
