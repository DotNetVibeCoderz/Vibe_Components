using System.Text.Json.Nodes;
using GeoPandas.Net.Data;
using GeoPandas.Net.Data.Versioning;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Lazy;
using GeoPandas.Net.Versioning;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

public sealed class LazyAndVersioningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-lazy-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string P(string n) => Path.Combine(_dir, n);

    // ------------------------------------------------------------------ lazy engine

    private sealed class CountingSource(IGeoScanSource inner) : IGeoScanSource
    {
        public int RowsRead;
        public ScanRequest? LastRequest;
        public string Name => inner.Name;
        public CoordinateReferenceSystem? Crs => inner.Crs;
        public IReadOnlyList<string> ColumnNames => inner.ColumnNames;
        public long? RowCountHint => inner.RowCountHint;
        public ScanPushdown Pushdown => inner.Pushdown;
        public IEnumerable<GeoDataFrame> Scan(ScanRequest r)
        {
            LastRequest = r;
            foreach (var b in inner.Scan(r)) { RowsRead += b.RowCount; yield return b; }
        }
    }

    [Fact]
    public void Lazy_MatchesEager_AndPushesDown()
    {
        var poi = SampleData.PointsOfInterest(2000);
        var b = poi.TotalBounds;
        var box = new Envelope(b.MinX, b.Centre.X, b.MinY, b.Centre.Y);
        var src = new CountingSource(new InMemoryScanSource(poi));
        var category = (string)poi[poi.ColumnNames.First(n => n != "geometry" && poi[n].DataType == typeof(string))].GetValue(0)!;
        var catCol = poi.ColumnNames.First(n => n != "geometry" && poi[n].DataType == typeof(string));

        var lazy = LazyGeoDataFrame.From(src, batchSize: 100).FilterBbox(box).Where(catCol, CompareOp.Eq, category).Select(catCol);
        var res = lazy.Collect();
        var eager = poi.Filter(r => poi.Geometry[r.Index]!.EnvelopeInternal.Intersects(box) && (string?)r[catCol] == category);
        Assert.Equal(eager.RowCount, res.RowCount);
        Assert.Equal([catCol, "geometry"], res.ColumnNames);
        Assert.NotNull(src.LastRequest!.Bbox);
        Assert.Equal([catCol], src.LastRequest.Columns);
        Assert.True(src.RowsRead < poi.RowCount); // the index skipped rows outside the box
    }

    [Fact]
    public void Lazy_LimitStopsEarly_AndIsNotPushedPastFilters()
    {
        var poi = SampleData.PointsOfInterest(2000);
        var src = new CountingSource(new InMemoryScanSource(poi));
        var head = LazyGeoDataFrame.From(src, batchSize: 64).Limit(10).Collect();
        Assert.Equal(10, head.RowCount);
        Assert.Equal(10, src.LastRequest!.Limit); // pushed: no filter before it

        var src2 = new CountingSource(new InMemoryScanSource(poi));
        var filtered = LazyGeoDataFrame.From(src2, batchSize: 64).Filter(r => r.Index % 5 == 0).Limit(10).Collect();
        Assert.Equal(10, filtered.RowCount);
        Assert.Null(src2.LastRequest!.Limit); // an opaque filter blocks limit pushdown
        Assert.True(src2.RowsRead < poi.RowCount); // but streaming still stops early
    }

    [Fact]
    public void Lazy_BboxAfterToCrs_IsTransformedBackForPushdown()
    {
        var d = SampleData.Districts();
        var wgs = d.ToCrs(4326);
        var b = wgs.TotalBounds;
        var box = new Envelope(b.MinX, b.Centre.X, b.MinY, b.Centre.Y);
        var lazy = d.Lazy().ToCrs(4326).FilterBbox(box);
        Assert.Contains("coarse", lazy.Explain()); // pushed after transforming, re-checked exactly
        var res = lazy.Collect();
        Assert.Equal(wgs.Geometry.Count(g => g!.EnvelopeInternal.Intersects(box)), res.RowCount);
        Assert.Equal(4326, res.Crs!.Epsg);
    }

    [Fact]
    public void Lazy_SpatialPredicate_WithColumn_Count_AndEmpty()
    {
        var d = SampleData.Districts();
        var probe = d.Geometry[3]!.Centroid;
        var lazy = d.Lazy().Filter(SpatialPredicate.Contains, probe).WithColumn("double_pop", r => r.GetDouble("population") * 2, "population");
        var res = lazy.Collect();
        Assert.Equal(1, res.RowCount);
        Assert.Equal(d["population"].GetDouble(3) * 2, res["double_pop"].GetDouble(0));
        Assert.Equal(1, lazy.Count());
        var none = d.Lazy().FilterBbox(0, 0, 1, 1).Select("name").Collect();
        Assert.Equal(0, none.RowCount);
        Assert.Contains("name", none.ColumnNames);
        Assert.Equal(d.Crs, none.Crs);
    }

    [Fact]
    public void Lazy_GeoPackageScan_UsesSqlProjectionAndLimit()
    {
        var d = SampleData.Districts();
        d.ToFile(P("d.gpkg"), "districts");
        var lazy = GeoIO.Scan(P("d.gpkg")).Select("name").Limit(4);
        Assert.Contains("GeoPackage", lazy.Explain());
        var res = lazy.Collect();
        Assert.Equal(4, res.RowCount);
        Assert.Equal(["name", "geometry"], res.ColumnNames);
        var b = d.TotalBounds;
        var box = new Envelope(b.MinX, b.Centre.X, b.MinY, b.Centre.Y);
        Assert.Equal(d.Geometry.Count(g => g!.EnvelopeInternal.Intersects(box)), GeoIO.Scan(P("d.gpkg")).FilterBbox(box).Count());
    }

    // ------------------------------------------------------------------ diff & repository

    [Fact]
    public void FrameDiff_Keyed_ReportsAddedRemovedModifiedColumns()
    {
        var d = SampleData.Districts();
        var edited = d.Filter(r => r.Get<long>("district_id") != 5)                         // removes id 5
            .WithColumn<long>("population", r => r.Get<long>("district_id") == 7 ? r.Get<long>("population") + 1 : r.Get<long>("population"));
        var extra = d.Filter(r => r.Get<long>("district_id") == 1).WithColumn(Column.Create("district_id", new long[] { 999 }));
        edited = GeoDataFrame.Concat(edited, extra);
        var diff = FrameDiff.Compare(d, edited, "district_id");
        Assert.Single(diff.AddedRows);
        Assert.Single(diff.RemovedRows);
        Assert.Single(diff.Modified);
        Assert.Equal(7L, diff.Modified[0].Key);
        Assert.Equal(["population"], diff.Modified[0].ChangedColumns);
        var change = diff.ToChangeFrame();
        Assert.IsType<GeoDataFrame>(change);
        Assert.Equal(["added", "removed", "modified"], change["_change"].AsObjects().Cast<string>().ToArray());
        Assert.True(FrameDiff.Compare(d, d, "district_id").IsEmpty);
    }

    [Fact]
    public void FrameDiff_Keyless_IsMultiset_AndHashIgnoresNumericWidth()
    {
        var a = new DataFrame(Column.Create("x", new[] { 1, 2, 2, 3 }));
        var b = new DataFrame(Column.Create("x", new long[] { 2, 3, 4, 2 }));
        var diff = FrameDiff.Compare(a, b);
        Assert.Equal([2], diff.AddedRows);   // 4
        Assert.Equal([0], diff.RemovedRows); // 1
        Assert.Equal(FrameDiff.ContentHash(new DataFrame(Column.Create("x", new[] { 1, 2 }))),
                     FrameDiff.ContentHash(new DataFrame(Column.Create("x", new[] { 1.0, 2.0 }))));
        Assert.NotEqual(FrameDiff.ContentHash(a), FrameDiff.ContentHash(b));
    }

    [Fact]
    public void DatasetRepository_CommitCheckoutDiffTag()
    {
        var repo = DatasetRepository.Open(P("repo"), key: "district_id");
        var v1 = SampleData.Districts();
        var c1 = repo.Commit(v1, "import", "tester");
        Assert.Same(repo.Head!.Id, c1.Id);
        Assert.Equal(c1.Id, repo.Commit(v1, "no-op").Id); // unchanged content → no new commit
        Assert.Equal(c1.Id, repo.Commit(v1.SortBy("name"), "reordered").Id); // row order alone is not a change
        var v2 = v1.WithColumn<double>("green_ratio", r => r.Get<long>("district_id") == 3 ? 0.99 : r.GetDouble("green_ratio"));
        var c2 = repo.Commit(v2, "greening of district 3");
        Assert.Equal("+0 added, -0 removed, ~1 modified", c2.Summary);
        repo.Tag("v1", c1.ShortId);

        var reopened = DatasetRepository.Open(P("repo"));
        Assert.Equal(2, reopened.Log().Count);
        Assert.Equal(c2.Id, reopened.Resolve("HEAD").Id);
        Assert.Equal(c1.Id, reopened.Resolve("HEAD~1").Id);
        Assert.Equal(c1.Id, reopened.Resolve("v1").Id);
        var old = reopened.Checkout("v1");
        Assert.Equal(v1.RowCount, old.RowCount);
        Assert.True(FrameDiff.Compare(v1, old, "district_id").IsEmpty);
        var diff = reopened.Diff("v1", "HEAD");
        Assert.Equal(["green_ratio"], diff.Modified.Single().ChangedColumns);
        Assert.Equal(2, Directory.GetFiles(P("repo/snapshots"), "*.fgb").Length);
    }

    // ------------------------------------------------------------------ PROJJSON

    [Fact]
    public void ProjJson_RoundTrips_AndParsesProjOutput()
    {
        foreach (var code in new[] { 4326, 4269, 3857, 3395, 2154, 5070, 27700, 32748, 23835 })
        {
            var crs = CoordinateReferenceSystem.FromEpsg(code);
            var back = ProjJson.Parse(crs.ToProjJson());
            Assert.Equal(crs, back);
        }
        var reference = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "reference.json")))!;
        Assert.Equal(32748, CoordinateReferenceSystem.Parse(reference["projjson_32748"]!.GetValue<string>()).Epsg);
        // PROJ's PROJJSON without an EPSG id: rebuilt from base CRS + conversion, compared numerically with pyproj
        var lcc = CoordinateReferenceSystem.Parse(reference["projjson_custom_lcc"]!.GetValue<string>());
        Assert.Null(lcc.Epsg);
        var (x, y) = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, lcc).Transform(-90, 40);
        var expect = reference["lcc_m_-90_40"]!.AsArray();
        Assert.Equal(expect[0]!.GetValue<double>(), x, 3);
        Assert.Equal(expect[1]!.GetValue<double>(), y, 3);
        // and our export of a custom CRS survives a round trip
        var custom = ProjJson.Parse(lcc.ToProjJson());
        var (x2, y2) = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, custom).Transform(-90, 40);
        Assert.Equal(x, x2, 6); Assert.Equal(y, y2, 6);
    }
}
