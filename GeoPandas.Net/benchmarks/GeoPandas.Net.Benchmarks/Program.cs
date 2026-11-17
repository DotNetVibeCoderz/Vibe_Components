using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using GeoPandas.Net;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Native;
using GeoPandas.Net.Network;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

// Run: dotnet run -c Release --project benchmarks/GeoPandas.Net.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(SpatialJoinBench).Assembly).Run(args);

[MemoryDiagnoser]
public class SpatialJoinBench
{
    private GeoDataFrame _points = null!, _districts = null!;

    [Params(10_000, 100_000)] public int Points;

    [GlobalSetup]
    public void Setup()
    {
        _districts = SampleData.Districts();
        var env = _districts.TotalBounds;
        var r = new Random(1);
        _points = GeoDataFrame.FromGeometries(Enumerable.Range(0, Points).Select(_ => (NtsGeometry?)Geo.Point(env.MinX + r.NextDouble() * env.Width, env.MinY + r.NextDouble() * env.Height)), _districts.Crs);
    }

    [Benchmark(Description = "sjoin (STR-tree + prepared, parallel)")]
    public int SJoin() => _points.SJoin(_districts, SpatialPredicate.Within).RowCount;

    [Benchmark(Baseline = true, Description = "naive nested loop")]
    public int Naive()
    {
        int n = 0;
        for (int i = 0; i < _points.RowCount; i++)
            for (int j = 0; j < _districts.RowCount; j++)
                if (_points.Geometry[i]!.Within(_districts.Geometry[j]!)) n++;
        return n;
    }
}

public class CrsBench
{
    private double[] _x = null!, _y = null!;
    private readonly CoordinateTransformer _utm = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.FromEpsg(32748));
    private readonly CoordinateTransformer _bng = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.FromEpsg(27700));

    [GlobalSetup]
    public void Setup()
    {
        var r = new Random(2);
        _x = Enumerable.Range(0, 1_000_000).Select(_ => 104 + r.NextDouble() * 4).ToArray();
        _y = Enumerable.Range(0, 1_000_000).Select(_ => -8 + r.NextDouble() * 4).ToArray();
    }

    [Benchmark(Description = "1M points WGS84 → UTM 48S")]
    public void ToUtm() => _utm.Transform(_x.AsSpan().ToArray(), _y.AsSpan().ToArray());

    [Benchmark(Description = "1M points WGS84 → BNG (7-param datum shift)")]
    public void ToBng() => _bng.Transform(_x.AsSpan().ToArray(), _y.AsSpan().ToArray());
}

public class KernelBench
{
    private double[] _a = null!, _b = null!, _c = null!, _d = null!, _out = null!;
    private byte[] _mask = null!;

    [GlobalSetup]
    public void Setup()
    {
        var r = new Random(3);
        double[] Make(double lo, double span) => Enumerable.Range(0, 2_000_000).Select(_ => lo + r.NextDouble() * span).ToArray();
        _a = Make(-180, 360); _b = Make(-80, 160); _c = Make(-180, 360); _d = Make(-80, 160);
        _out = new double[_a.Length]; _mask = new byte[_a.Length];
    }

    [Benchmark(Description = "haversine 2M pairs")]
    public void Haversine() => NativeKernels.Haversine(_a, _b, _c, _d, _out);

    [Benchmark(Description = "bbox filter 2M points")]
    public void BoxFilter() => NativeKernels.BoxFilter(_a, _b, -10, -10, 10, 10, _mask);
}

public class RoutingBench
{
    private NetworkGraph _g = null!;
    private ContractionHierarchy _ch = null!;
    private (int, int)[] _pairs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _g = NetworkGraph.FromLines(SampleData.Roads(spacing: 250), "travel_time_s");
        _ch = _g.BuildContractionHierarchy();
        var r = new Random(4);
        _pairs = Enumerable.Range(0, 200).Select(_ => (r.Next(_g.NodeCount), r.Next(_g.NodeCount))).ToArray();
    }

    [Benchmark(Baseline = true)] public double Dijkstra() => _pairs.Sum(p => _g.ShortestPath(p.Item1, p.Item2).Cost is var c && double.IsFinite(c) ? c : 0);
    [Benchmark] public double AStar() => _pairs.Sum(p => _g.ShortestPath(p.Item1, p.Item2, PathAlgorithm.AStar).Cost is var c && double.IsFinite(c) ? c : 0);
    [Benchmark] public double ContractionHierarchies() => _pairs.Sum(p => _ch.Distance(p.Item1, p.Item2) is var c && double.IsFinite(c) ? c : 0);
}
