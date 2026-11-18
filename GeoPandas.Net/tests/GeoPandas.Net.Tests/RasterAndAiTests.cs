using GeoPandas.Net.AI;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Raster;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Tests;

public sealed class RasterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-raster-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static RasterGrid Plane(int w, int h, double dzdx, double cell = 10)
    {
        var z = new float[w * h];
        for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) z[r * w + c] = (float)(c * cell * dzdx);
        return new RasterGrid(w, h, [z], GeoTransform.NorthUp(0, h * cell, cell), CoordinateReferenceSystem.FromEpsg(32748));
    }

    [Theory]
    [InlineData(TiffCompression.None, 0)]
    [InlineData(TiffCompression.Deflate, 0)]
    [InlineData(TiffCompression.Deflate, 64)]
    [InlineData(TiffCompression.None, 32)]
    public void GeoTiff_RoundTrip(TiffCompression comp, int tile)
    {
        var dem = SampleData.Dem(130, 75);
        var path = Path.Combine(_dir, $"dem_{comp}_{tile}.tif");
        GeoTiff.Write(dem, path, comp, tile);
        var back = GeoTiff.Read(path);
        Assert.Equal(dem.Width, back.Width);
        Assert.Equal(dem.Height, back.Height);
        Assert.Equal(dem.Bands[0], back.Bands[0]);
        Assert.Equal(dem.Transform, back.Transform);
        Assert.Equal(32748, back.Crs!.Epsg);
        Assert.Equal(-9999f, back.NoData);
    }

    [Fact]
    public void GeoTiff_MultiBand()
    {
        var ms = SampleData.Multispectral(64, 48);
        var path = Path.Combine(_dir, "ms.tif");
        GeoTiff.Write(ms, path);
        var back = GeoTiff.Read(path);
        Assert.Equal(4, back.BandCount);
        Assert.Equal(ms.Bands[2], back.Bands[2]);
    }

    [Fact]
    public void Slope_Of_45DegreePlane()
    {
        var s = Terrain.Slope(Plane(20, 20, 1));
        Assert.Equal(45, s[0, 10, 10], 3);
        var aspect = Terrain.Aspect(Plane(20, 20, 1));
        Assert.Equal(270, aspect[0, 10, 10], 3); // rising to the east → faces west
        var hs = Terrain.Hillshade(SampleData.Dem(64, 64));
        var st = hs.Statistics();
        Assert.InRange(st.Min, 0, 255);
        Assert.InRange(st.Max, 0, 255);
    }

    [Fact]
    public void Hydrology_FillFlowAccumulation()
    {
        var dem = SampleData.Dem(80, 60);
        dem.Bands[0][30 * 80 + 40] = -500; // artificial pit
        var filled = Hydrology.FillSinks(dem);
        Assert.True(filled[0, 30, 40] > -500);
        var fd = Hydrology.FlowDirection(filled);
        var acc = Hydrology.FlowAccumulation(fd);
        Assert.True(acc.Statistics().Max > 100);
        var (_, maxV, _, _, _) = acc.Statistics();
        int idx = Array.IndexOf(acc.Bands[0], (float)maxV);
        var (x, y) = acc.Transform.PixelToWorld(idx % 80 + 0.5, idx / 80 + 0.5);
        var ws = Hydrology.Watershed(fd, x, y);
        Assert.Equal(maxV, ws.Bands[0].Count(v => v == 1), 0);
    }

    [Fact]
    public void Ndvi_And_ChangeDetection()
    {
        var before = SampleData.Multispectral(64, 48);
        var ndvi = SpectralIndex.Ndvi(SpectralIndex.Band(before, 2), SpectralIndex.Band(before, 1));
        var st = ndvi.Statistics();
        Assert.InRange(st.Min, -1, 1);
        Assert.True(st.Max > 0.5);
        var after = SampleData.Multispectral(64, 48, urbanGrowth: 0.12);
        var (_, change) = SpectralIndex.ChangeDetection(
            SpectralIndex.Ndvi(SpectralIndex.Band(before, 2), SpectralIndex.Band(before, 1)),
            SpectralIndex.Ndvi(SpectralIndex.Band(after, 2), SpectralIndex.Band(after, 1)));
        Assert.True(change.Bands[0].Count(v => v == -1) > 50); // vegetation loss
    }

    [Fact]
    public void Zonal_Rasterize_Contours_Viewshed()
    {
        var plane = Plane(50, 50, 0.1);
        var zones = GeoDataFrame.FromGeometries([Geo.Box(0, 0, 100, 100), Geo.Box(400, 400, 500, 500)], plane.Crs);
        var zs = plane.ZonalStatistics(zones);
        Assert.Equal(100, zs["count"].GetDouble(0));
        Assert.Equal(4.5 * 10 * 0.1, zs["mean"].GetDouble(0), 3);

        var burned = RasterVector.Rasterize(zones, plane, fill: 0);
        Assert.Equal(200, burned.Bands[0].Count(v => v == 1));

        var contours = plane.Contours(10);
        Assert.True(contours.RowCount >= 4);
        Assert.All(contours.Geometry, g => Assert.Equal(490, g!.Length, 6)); // 50 rows of 10 m between cell centres

        var dem = SampleData.Dem(100, 80);
        var c = dem.Bounds.Centre;
        var vs = Terrain.Viewshed(dem, c.X, c.Y, 30);
        Assert.True(vs.Bands[0].Count(v => v == 1) > 100);
    }
}

public class AiTests
{
    private sealed class FakeLlm(string response) : ILlmClient
    {
        public string? LastPrompt;
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task GeoAssistant_ExecutesWhitelistedPlan()
    {
        var llm = new FakeLlm("""
            Here is the plan:
            ```json
            {"steps":[{"op":"filter","column":"region","cmp":"==","value":"Pusat"},
                      {"op":"area","as":"area_m2"},
                      {"op":"sort","column":"population","ascending":false},
                      {"op":"head","n":3}],
             "explanation":"Tiga distrik terpadat di wilayah Pusat."}
            ```
            """);
        var gdf = SampleData.Districts();
        var answer = await new GeoAssistant(llm).AskAsync(gdf, "3 distrik terpadat di Pusat?");
        Assert.Contains("population", llm.LastPrompt);
        Assert.True(answer.Result.RowCount is > 0 and <= 3);
        Assert.True(answer.Result.HasColumn("area_m2"));
        Assert.All(Enumerable.Range(0, answer.Result.RowCount), i => Assert.Equal("Pusat", answer.Result["region"].GetValue(i)));
        Assert.Equal("Tiga distrik terpadat di wilayah Pusat.", answer.Explanation);
    }

    [Fact]
    public void Plan_RejectsUnknownOperations()
    {
        var plan = GeoQueryPlan.Parse("""{"steps":[{"op":"exec","code":"rm -rf /"}]}""");
        Assert.Throws<InvalidOperationException>(() => GeoAssistant.Execute(SampleData.Districts(), plan));
    }

    [Fact]
    public void Plan_GroupBy_And_Nearest()
    {
        var gdf = SampleData.PointsOfInterest(200);
        var plan = GeoQueryPlan.Parse("""{"steps":[{"op":"groupby","by":"category","agg":[{"column":"capacity","func":"sum"}]}]}""");
        var res = GeoAssistant.Execute(gdf, plan);
        Assert.Equal(gdf["capacity"].Sum(), res["capacity_sum"].Sum());
        var near = GeoAssistant.Execute(gdf, GeoQueryPlan.Parse($$"""{"steps":[{"op":"nearest_to","x":{{SampleData.CenterX}},"y":{{SampleData.CenterY}},"k":5}]}"""));
        Assert.Equal(5, near.RowCount);
        var d = near["distance"].ToDoubleArray();
        Assert.Equal(d.Order(), d);
    }

    [Fact]
    public void Plan_H3Bin_WithinDistance_Simplify_Unique()
    {
        var gdf = SampleData.PointsOfInterest(300);
        var bins = GeoAssistant.Execute(gdf, GeoQueryPlan.Parse("""{"steps":[{"op":"h3_bin","res":7,"value":"capacity"}]}"""));
        Assert.Equal(300, bins["count"].Sum());
        Assert.Equal(gdf["capacity"].Sum(), bins["capacity_sum"].Sum(), 6);

        var within = GeoAssistant.Execute(gdf, GeoQueryPlan.Parse($$"""{"steps":[{"op":"within_distance","x":{{SampleData.CenterX}},"y":{{SampleData.CenterY}},"distance":2000}]}"""));
        var c = Geo.Point(SampleData.CenterX, SampleData.CenterY);
        int expected = Enumerable.Range(0, gdf.RowCount).Count(i => gdf.Geometry[i]!.Distance(c) <= 2000);
        Assert.Equal(expected, within.RowCount);
        var d = within["distance"].ToDoubleArray();
        Assert.Equal(d.Order(), d);

        var districts = SampleData.Districts();
        var simple = GeoAssistant.Execute(districts, GeoQueryPlan.Parse("""{"steps":[{"op":"simplify","tolerance":200}]}"""));
        Assert.Equal(districts.RowCount, simple.RowCount);
        Assert.True(Enumerable.Range(0, districts.RowCount).Sum(i => ((GeoDataFrame)simple).Geometry[i]!.NumPoints) <= Enumerable.Range(0, districts.RowCount).Sum(i => districts.Geometry[i]!.NumPoints));

        var uniq = GeoAssistant.Execute(gdf, GeoQueryPlan.Parse("""{"steps":[{"op":"unique","column":"category"}]}"""));
        Assert.Equal(300, uniq["count"].Sum());
        Assert.Equal(Enumerable.Range(0, gdf.RowCount).Select(i => gdf["category"].GetValue(i)).Distinct().Count(), uniq.RowCount);
    }

    [Fact]
    public void DescribeSchema_ContainsColumnsAndCrs()
    {
        var s = GeoAssistant.DescribeSchema(SampleData.Districts());
        Assert.Contains("EPSG:32748", s);
        Assert.Contains("income_mio_idr", s);
    }
}
