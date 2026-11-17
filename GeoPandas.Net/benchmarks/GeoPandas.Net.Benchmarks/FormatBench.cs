using BenchmarkDotNet.Attributes;
using GeoPandas.Net;
using GeoPandas.Net.Arrow;
using GeoPandas.Net.Data;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Lazy;
using GeoPandas.Net.PointCloud;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

/// <summary>Phase 2 IO: 200 000 polygons (small squares) in FlatGeobuf / GeoParquet / GeoPackage; a 1 % window vs full reads.</summary>
[MemoryDiagnoser]
public class FormatBench
{
    private string _dir = null!, _fgb = null!, _parquet = null!, _gpkg = null!, _las = null!;
    private Envelope _window = null!;
    private GeoDataFrame _frame = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Directory.CreateTempSubdirectory("gpd-bench-").FullName;
        var r = new Random(4);
        const int n = 200_000;
        var geoms = new NtsGeometry?[n];
        for (int i = 0; i < n; i++)
        {
            double x = r.NextDouble() * 100_000, y = r.NextDouble() * 100_000;
            geoms[i] = Geo.Box(x, y, x + 20, y + 20);
        }
        _frame = GeoDataFrame.FromGeometries(geoms, SampleData.Crs,
            Column.Create("id", Enumerable.Range(0, n).Select(i => (long)i).ToArray()),
            Column.Create("value", Enumerable.Range(0, n).Select(i => r.NextDouble()).ToArray()),
            Column.Create("kind", Enumerable.Range(0, n).Select(i => i % 5 == 0 ? "a" : "b").ToArray()));
        _window = new Envelope(40_000, 50_000, 40_000, 50_000); // 1 % of the extent
        _fgb = Path.Combine(_dir, "b.fgb"); FlatGeobuf.Write(_frame, _fgb);
        _parquet = Path.Combine(_dir, "b.parquet"); GeoParquet.Write(SortByX(_frame), _parquet, new GeoParquetWriteOptions { RowGroupSize = 10_000 });
        _gpkg = Path.Combine(_dir, "b.gpkg"); GeoPackage.Write(_frame, _gpkg, "b");
        _las = Path.Combine(_dir, "b.las"); Las.Write(SampleData.LidarTile(400, 300, 8), _las);
    }

    // spatially sorted rows give GeoParquet row groups tight bboxes (what a Hilbert/geohash sort would do in production)
    private static GeoDataFrame SortByX(GeoDataFrame g) =>
        g.Take(Enumerable.Range(0, g.RowCount).OrderBy(i => g.Geometry[i]!.EnvelopeInternal.MinX).ToArray());

    [GlobalCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

    [Benchmark(Description = "FlatGeobuf full read (200k polygons)")] public int FgbFull() => FlatGeobuf.Read(_fgb).RowCount;
    [Benchmark(Description = "FlatGeobuf 1 % bbox (R-tree)")] public int FgbBbox() => FlatGeobuf.Read(_fgb, _window).RowCount;
    [Benchmark(Description = "GeoParquet full read")] public int ParquetFull() => GeoParquet.Read(_parquet).RowCount;
    [Benchmark(Description = "GeoParquet 1 % bbox (row-group pruning)")] public int ParquetBbox() => GeoParquet.Read(_parquet, _window).RowCount;
    [Benchmark(Description = "GeoPackage full read")] public int GpkgFull() => GeoPackage.Read(_gpkg).RowCount;
    [Benchmark(Description = "Lazy FGB: bbox + where + select + count")]
    public long LazyCount() => GeoIO.Scan(_fgb).FilterBbox(_window).Where("kind", CompareOp.Eq, "a").Select("id").Count();
    [Benchmark(Description = "FlatGeobuf write (Hilbert R-tree)")] public void FgbWrite() => FlatGeobuf.Write(_frame, Path.Combine(_dir, "w.fgb"));
    [Benchmark(Description = "GeoParquet write")] public void ParquetWrite() => GeoParquet.Write(_frame, Path.Combine(_dir, "w.parquet"));
    [Benchmark(Description = "LAS read (≈1M points, mmap)")] public int LasRead() => Las.Read(_las).Count;
}
