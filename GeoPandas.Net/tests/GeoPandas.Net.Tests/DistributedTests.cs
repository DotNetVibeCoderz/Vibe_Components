using GeoPandas.Net.Arrow;
using GeoPandas.Net.Datasets;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

/// <summary>Partitioned frames must give exactly the unpartitioned results (order included).</summary>
public sealed class DistributedTests
{
    private static void AssertSameFrame(GeoDataFrame expected, GeoDataFrame actual)
    {
        Assert.Equal(expected.ColumnNames, actual.ColumnNames);
        Assert.Equal(expected.RowCount, actual.RowCount);
        foreach (var name in expected.ColumnNames)
            for (int i = 0; i < expected.RowCount; i++)
            {
                var a = expected[name].GetValue(i); var b = actual[name].GetValue(i);
                if (a is NetTopologySuite.Geometries.Geometry ga) Assert.True(ga.EqualsExact((NetTopologySuite.Geometries.Geometry)b!), $"{name}[{i}]");
                else Assert.Equal(a, b);
            }
    }

    [Theory]
    [InlineData(PartitionScheme.Grid, 3)]
    [InlineData(PartitionScheme.KdTree, 40)]
    [InlineData(PartitionScheme.H3, 6)]
    [InlineData(PartitionScheme.Column, 0)]
    public void Partitioned_Collect_Query_SJoin_MatchUnpartitioned(PartitionScheme scheme, int size)
    {
        var poi = SampleData.PointsOfInterest(400);
        var districts = SampleData.Districts();
        var parts = PartitionedGeoDataFrame.Create(poi, scheme, size, scheme == PartitionScheme.Column ? "category" : null);
        Assert.Equal(poi.RowCount, parts.RowCount);
        Assert.True(parts.PartitionCount > 1);
        if (scheme == PartitionScheme.KdTree) Assert.All(parts.Partitions, p => Assert.True(p.RowCount <= 40));
        AssertSameFrame(poi, parts.Collect());

        // bbox query with pruning
        var tb = poi.TotalBounds;
        var box = new Envelope(tb.MinX + tb.Width * 0.2, tb.MinX + tb.Width * 0.45, tb.MinY + tb.Height * 0.3, tb.MinY + tb.Height * 0.6);
        var poly = Geometry.Geo.Box(box.MinX, box.MinY, box.MaxX, box.MaxY);
        var expected = poi.Take(Enumerable.Range(0, poi.RowCount).Where(i => poi.Geometry[i]!.Intersects(poly)).ToArray());
        AssertSameFrame(expected, parts.Query(box));
        if (scheme is PartitionScheme.Grid or PartitionScheme.KdTree) Assert.True(parts.Prune(box).Count() < parts.PartitionCount);

        // partition-aware spatial join (inner and left) == plain sjoin
        AssertSameFrame(poi.SJoin(districts, SpatialPredicate.Within), parts.SJoin(districts, SpatialPredicate.Within).Collect());
        var buffered = poi.WithGeometry(poi.Geometry.Buffer(300));
        var bparts = PartitionedGeoDataFrame.Create(buffered, scheme, size, scheme == PartitionScheme.Column ? "category" : null);
        AssertSameFrame(buffered.SJoin(districts, how: JoinHow.Left), bparts.SJoin(districts, how: JoinHow.Left).Collect());

        // map / reduce
        Assert.Equal(poi["capacity"].Sum(), parts.MapReduce(f => f["capacity"].Sum(), (a, b) => a + b), 6);
        var mapped = parts.Map(f => f.WithGeometry(f.Geometry.Buffer(10))).Collect();
        Assert.Equal(poi.RowCount, mapped.RowCount);
        Assert.Equal(poi.Geometry.Buffer(10).Area().Sum(), mapped.Geometry.Area().Sum(), 3);
        Assert.Equal(parts.PartitionCount, parts.BoundsFrame().RowCount);
    }

    [Fact]
    public void GeoParquetDataset_RoundTrip_And_PrunesPartitions()
    {
        var poi = SampleData.PointsOfInterest(500);
        var dir = Path.Combine(Path.GetTempPath(), "gpd_ds_" + Guid.NewGuid().ToString("N"));
        try
        {
            GeoParquetDataset.Write(poi, dir, PartitionScheme.Grid, 4);
            var manifest = GeoParquetDataset.Partitions(dir);
            Assert.Equal(500, manifest.Sum(p => p.Rows));
            Assert.All(manifest, p => Assert.True(File.Exists(Path.Combine(dir, p.Path))));

            var all = GeoParquetDataset.Read(dir);
            Assert.Equal(500, all.RowCount);
            Assert.Equal(poi.Crs, all.Crs);
            Assert.Equal(poi["capacity"].Sum(), all["capacity"].Sum(), 6);

            var tb = poi.TotalBounds;
            var box = new Envelope(tb.MinX, tb.MinX + tb.Width * 0.3, tb.MinY, tb.MinY + tb.Height * 0.3);
            var pruned = GeoParquetDataset.ReadPartitioned(dir, box);
            Assert.True(pruned.PartitionCount < manifest.Count);
            var poly = Geometry.Geo.Box(box.MinX, box.MinY, box.MaxX, box.MaxY);
            int expected = Enumerable.Range(0, poi.RowCount).Count(i => poi.Geometry[i]!.Intersects(poly));
            Assert.Equal(expected, pruned.RowCount);

            // attribute partitioning: one directory per value
            var dir2 = Path.Combine(dir, "by_category");
            GeoParquetDataset.Write(poi, dir2, PartitionScheme.Column, column: "category");
            var cats = Enumerable.Range(0, poi.RowCount).Select(i => (string)poi["category"].GetValue(i)!).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(cats, GeoParquetDataset.Partitions(dir2).Select(p => p.Key).ToArray());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
