using System.Text.Json;
using GDAL.Net.Rendering;
using GDAL.Net.Vector;
using GDAL.Net.Vsi;

namespace GDAL.Net.Tests;

public class UtilityTests
{
    [Fact]
    public void InfoAsJson()
    {
        using var dem = TestData.CreateDem();
        var json = GdalUtils.Info(dem, "-json", "-stats");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(200, doc.RootElement.GetProperty("size")[0].GetInt32());
        Assert.True(doc.RootElement.GetProperty("bands")[0].TryGetProperty("maximum", out _));
        Assert.Contains("Driver: MEM", GdalUtils.Info(dem));
    }

    [Fact]
    public void TranslateToCogAndPng()
    {
        var dir = TestData.NewTempDir();
        using var dem = TestData.CreateDem();
        using (var cog = GdalUtils.Translate(dem, Path.Combine(dir, "dem.cog.tif"), new TranslateOptions
        {
            Format = "COG",
            CreationOptions = new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE", ["PREDICTOR"] = "YES", ["OVERVIEWS"] = "AUTO" },
        }))
        {
            Assert.Equal(200, cog.RasterWidth);
        }
        using var reopened = Gdal.OpenRaster(Path.Combine(dir, "dem.cog.tif"));
        Assert.Equal("COG", reopened.GetMetadataItem("LAYOUT", "IMAGE_STRUCTURE"));

        var progress = new List<double>();
        using var png = GdalUtils.Translate(dem, Path.Combine(dir, "dem.png"), new TranslateOptions
        {
            Format = "PNG",
            OutputType = DataType.Byte,
            AutoScale = true,
            OutputSize = (100, 0),
        }, new SyncProgress(p => progress.Add(p.Fraction)));
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1], 3);
        Assert.Equal(100, png.RasterWidth);
        Assert.Equal(75, png.RasterHeight);
    }

    [Fact]
    public void WarpReprojectAndClip()
    {
        using var dem = TestData.CreateDem();
        using var wgs = GdalUtils.Warp(dem, "", new WarpOptions { Format = "MEM", TargetSrs = "EPSG:4326", Resampling = ResampleAlgorithm.Bilinear });
        Assert.Equal(4326, wgs.SpatialReference!.Epsg);
        Assert.InRange(wgs.Extent.CenterX, 107.5, 108.0);

        var ext = dem.Extent;
        var clip = $"POLYGON (({ext.MinX + 1000} {ext.MinY + 1000}, {ext.CenterX} {ext.MinY + 1000}, {ext.CenterX} {ext.CenterY}, {ext.MinX + 1000} {ext.CenterY}, {ext.MinX + 1000} {ext.MinY + 1000}))";
        using var clipped = GdalUtils.Warp(dem, "", new WarpOptions { Format = "MEM", CutlineWkt = clip, CropToCutline = true, DestinationNoData = -9999 });
        Assert.True(clipped.RasterWidth < dem.RasterWidth);
    }

    [Fact]
    public void CancelledWarpThrows()
    {
        using var dem = TestData.CreateDem(2000, 2000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            GdalUtils.Warp(dem, "", new WarpOptions { Format = "MEM", TargetSrs = "EPSG:4326" }, null, cts.Token));
    }

    [Fact]
    public void VectorTranslateToManyFormats()
    {
        var dir = TestData.NewTempDir();
        using var src = VectorTests.CreateCities(Path.Combine(dir, "cities.gpkg"));
        foreach (var (format, file) in new[] { ("GeoJSON", "c.geojson"), ("FlatGeobuf", "c.fgb"), ("ESRI Shapefile", "c.shp"), ("KML", "c.kml"), ("CSV", "c.csv") })
        {
            var opts = new VectorTranslateOptions { Format = format, ExtraArgs = format == "CSV" ? ["-lco", "GEOMETRY=AS_WKT"] : null };
            using (GdalUtils.VectorTranslate(src, Path.Combine(dir, file), opts)) { }
            using var reopened = Gdal.OpenVector(Path.Combine(dir, file));
            Assert.Equal(5, reopened.GetLayer(0).FeatureCount);
        }

        using var utm = GdalUtils.VectorTranslate(src, Path.Combine(dir, "utm.gpkg"), new VectorTranslateOptions
        {
            Format = "GPKG",
            TargetSrs = "EPSG:32749",
            Where = "population > 2000000",
            NewLayerName = "big_cities",
        });
        var layer = utm.GetLayer("big_cities");
        Assert.Equal(3, layer.FeatureCount);
        Assert.Equal(32749, layer.SpatialReference!.Epsg);
    }

    [Fact]
    public void BuildVrtMosaicAndStack()
    {
        using var dem = TestData.CreateDem(200, 200);
        using var a = GdalUtils.Translate(dem, "", new TranslateOptions { Format = "MEM", SourceWindow = (0, 0, 100, 200) });
        using var b = GdalUtils.Translate(dem, "", new TranslateOptions { Format = "MEM", SourceWindow = (100, 0, 100, 200) });
        using var mosaic = GdalUtils.BuildVrt("", [a, b]);
        Assert.Equal(200, mosaic.RasterWidth);
        Assert.Equal(dem.GetBand(1).Checksum(), mosaic.GetBand(1).Checksum());

        using var stack = GdalUtils.BuildVrt("", [dem, dem], new BuildVrtOptions { Separate = true });
        Assert.Equal(2, stack.RasterCount);
    }

    [Fact]
    public void DemAnalysis()
    {
        using var dem = TestData.CreateDem();
        using var hill = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Hillshade, new DemOptions { Format = "MEM", ZFactor = 2, HillshadeVariant = "multidirectional" });
        Assert.Equal(DataType.Byte, hill.GetBand(1).DataType);
        using var slope = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Slope, new DemOptions { Format = "MEM" });
        Assert.InRange(slope.GetBand(1).ComputeStatistics().Maximum, 1, 90);
        using var aspect = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Aspect, new DemOptions { Format = "MEM" });
        Assert.Equal(1, aspect.RasterCount);

        var colorFile = Path.Combine(TestData.NewTempDir(), "ramp.txt");
        File.WriteAllText(colorFile, ColorRamp.Terrain.ToGdalColorFile(700, 2200));
        using var relief = GdalUtils.DemProcessing(dem, "", DemProcessingMode.ColorRelief, new DemOptions { Format = "MEM", Alpha = true }, colorFile);
        Assert.Equal(4, relief.RasterCount);
    }

    [Fact]
    public void ContoursPolygonizeRasterize()
    {
        using var dem = TestData.CreateDem();
        using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), 250);
        var lines = contours.GetLayer(0);
        Assert.True(lines.FeatureCount > 3);
        using (var f = lines.GetNextFeature()!) Assert.Equal(0, f.GetDouble("elev") % 250);

        using var bands = RasterAlgorithms.GenerateContours(dem.GetBand(1), 500, polygons: true);
        Assert.True(bands.GetLayer(0).FeatureCount > 1);

        using var classes = GdalUtils.Translate(dem, "", ["-of", "MEM", "-ot", "Byte", "-scale", "700", "2200", "1", "4"]);
        using var polys = RasterAlgorithms.Polygonize(classes.GetBand(1));
        Assert.True(polys.GetLayer(0).FeatureCount >= 4);

        using var raster = GdalUtils.Rasterize(polys, "", new RasterizeOptions
        {
            Format = "MEM",
            Attribute = "value",
            Resolution = (60, 60),
            OutputType = DataType.Byte,
        });
        Assert.InRange(raster.RasterWidth, 100, 101);
        Assert.Equal(4, raster.GetBand(1).ComputeMinMax().Max);
    }

    [Fact]
    public void ViewshedAndFillNoData()
    {
        using var dem = TestData.CreateDem();
        var ext = dem.Extent;
        using var vs = RasterAlgorithms.Viewshed(dem.GetBand(1), ext.CenterX, ext.CenterY, observerHeight: 10);
        var data = vs.GetBand(1).ReadAll<byte>();
        Assert.Contains((byte)255, data);

        using var holes = TestData.CreateDem(50, 50, noData: -9999);
        var b = holes.GetBand(1);
        b.Write<float>(20, 20, 5, 5, Enumerable.Repeat(-9999f, 25).ToArray());
        RasterAlgorithms.FillNoData(b, 20);
        var filled = new float[25];
        b.Read<float>(20, 20, 5, 5, filled);
        Assert.DoesNotContain(-9999f, filled);
    }

    [Fact]
    public void GridInterpolation()
    {
        using var points = Gdal.CreateMemoryVector();
        var layer = points.CreateLayer("pts", null, GeometryType.PointZ);
        var rnd = new Random(1);
        for (var i = 0; i < 50; i++)
        {
            using var p = Geometry.Point(rnd.NextDouble() * 100, rnd.NextDouble() * 100, rnd.NextDouble() * 10);
            layer.AddFeature(p);
        }
        using var grid = GdalUtils.Grid(points, "", ["-of", "MEM", "-a", "invdist:power=2.0", "-outsize", "64", "64", "-txe", "0", "100", "-tye", "0", "100"]);
        Assert.Equal(64, grid.RasterWidth);
        Assert.InRange(grid.GetBand(1).ComputeStatistics().Mean, 0, 10);
    }

    [Fact]
    public void VectorInfo()
    {
        using var ds = VectorTests.CreateCities(Path.Combine(TestData.NewTempDir(), "vi.gpkg"));
        var text = GdalUtils.VectorInfo(ds, "-so", "-al");
        Assert.Contains("Feature Count: 5", text);
    }

    [Fact]
    public void GdalCliRasterInfoAndTiles()
    {
        Assert.True(GdalCli.IsAvailable);
        Assert.Contains("raster", GdalCli.AlgorithmNames);
        var dir = TestData.NewTempDir();
        using (var dem = TestData.CreateDem())
        using (GdalUtils.Translate(dem, Path.Combine(dir, "dem.tif"), ["-of", "GTiff"])) { }

        var info = GdalCli.Run("raster", "info", "--format", "json", Path.Combine(dir, "dem.tif"));
        Assert.Contains("\"size\"", info.Output);

        var usage = GdalCli.GetUsageJson("raster", "reproject");
        Assert.Contains("output-crs", usage);
    }

    [Fact]
    public void VirtualFileSystems()
    {
        Assert.Contains("/vsimem/", VirtualFileSystem.Prefixes);
        Assert.Contains("/vsicurl/", VirtualFileSystem.Prefixes);
        Assert.Contains("/vsis3/", VirtualFileSystem.Prefixes);

        var path = VirtualFileSystem.NewMemoryPath(".txt");
        VirtualFileSystem.WriteAllText(path, "Halo GDAL.Net");
        Assert.True(VirtualFileSystem.Exists(path));
        Assert.Equal("Halo GDAL.Net", VirtualFileSystem.ReadAllText(path));
        VirtualFileSystem.Delete(path);
        Assert.False(VirtualFileSystem.Exists(path));

        // Write a GeoJSON into a zip on /vsimem and read it back through /vsizip/
        var zip = VirtualFileSystem.NewMemoryPath(".zip");
        using (var src = VectorTests.CreateCities(Path.Combine(TestData.NewTempDir(), "z.gpkg")))
        using (GdalUtils.VectorTranslate(src, VirtualFileSystem.Zip(zip, "cities.geojson"), ["-f", "GeoJSON"])) { }
        Assert.Contains("cities.geojson", VirtualFileSystem.ListDirectory(VirtualFileSystem.Zip(zip)));
        using var fromZip = Gdal.OpenVector(VirtualFileSystem.Zip(zip, "cities.geojson"));
        Assert.Equal(5, fromZip.GetLayer(0).FeatureCount);

        // Raster from memory buffer
        var bytes = File.ReadAllBytes(WriteTempTif());
        var mem = VirtualFileSystem.NewMemoryPath(".tif");
        VirtualFileSystem.WriteMemoryFile(mem, bytes);
        using var ds = Gdal.OpenRaster(mem);
        Assert.Equal(200, ds.RasterWidth);
        Assert.Equal(bytes.Length, VirtualFileSystem.ReadMemoryFile(mem).Length);
    }

    private static string WriteTempTif()
    {
        var path = Path.Combine(TestData.NewTempDir(), "m.tif");
        using var dem = TestData.CreateDem();
        using (GdalUtils.Translate(dem, path, ["-of", "GTiff"])) { }
        return path;
    }

    private sealed class SyncProgress(Action<GdalProgress> report) : IProgress<GdalProgress>
    {
        public void Report(GdalProgress value) => report(value);
    }
}
