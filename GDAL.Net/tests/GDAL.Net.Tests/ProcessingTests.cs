using GDAL.Net.MultiDimensional;
using GDAL.Net.Processing;
using GDAL.Net.Rendering;

namespace GDAL.Net.Tests;

public class ProcessingTests
{
    [Theory]
    [InlineData("(A - B) / (A + B)", 0.6f)]
    [InlineData("A * 2 + B ^ 2", 1.64f)]
    [InlineData("A > B ? 1 : 0", 1f)]
    [InlineData("where(A < B, 10, 20)", 20f)]
    [InlineData("max(A, B) + min(A, B)", 1.0f)]
    [InlineData("clamp(A * 10, 0, 5)", 5f)]
    [InlineData("sqrt(abs(-A))", 0.894427f)]
    [InlineData("-A + 1", 0.2f)]
    [InlineData("A >= 0.8 && B <= 0.2", 1f)]
    [InlineData("!(A == B)", 1f)]
    [InlineData("2 * pi", 6.283185f)]
    public void BandMathExpressions(string expr, float expected)
    {
        var bm = BandMath.Compile(expr);
        var output = new float[3];
        bm.Evaluate(new Dictionary<string, float[]> { ["A"] = [0.8f, 0.8f, 0.8f], ["B"] = [0.2f, 0.2f, 0.2f] }, output);
        Assert.All(output, v => Assert.Equal(expected, v, 4));
    }

    [Fact]
    public void BandMathRejectsBadSyntax()
    {
        Assert.Throws<FormatException>(() => BandMath.Compile("(A + "));
        Assert.Throws<FormatException>(() => BandMath.Compile("max(A)"));
    }

    [Fact]
    public void BandMathOverDataset()
    {
        using var ms = TestData.CreateMultispectral();
        using var ndvi = BandMath.Calculate(ms, "(NIR - RED) / (NIR + RED)", new Dictionary<string, int> { ["NIR"] = 4, ["RED"] = 3 });
        var data = ndvi.GetBand(1).ReadAll<float>();
        Assert.Equal((1500f - 1400f) / 2900f, data[0], 5);
        Assert.Equal((4000f - 600f) / 4600f, data[63], 5);
        Assert.Equal(ms.GeoTransform, ndvi.GeoTransform);
    }

    [Fact]
    public void SpectralIndices()
    {
        using var ms = TestData.CreateMultispectral();
        using var ndvi = Processing.SpectralIndices.Ndvi(ms.GetBand(4), ms.GetBand(3));
        using var ndwi = Processing.SpectralIndices.Ndwi(ms.GetBand(2), ms.GetBand(4));
        using var savi = Processing.SpectralIndices.Savi(ms.GetBand(4), ms.GetBand(3));
        var v = ndvi.GetBand(1).ReadAll<float>();
        Assert.True(v[63] > 0.7f);
        Assert.True(v[0] < 0.1f);
        Assert.True(ndwi.GetBand(1).ReadAll<float>()[63] < 0);
        Assert.True(savi.GetBand(1).ReadAll<float>()[63] > 0);
    }

    [Fact]
    public void NativeAndManagedKernelsAgree()
    {
        var rnd = new Random(42);
        var data = Enumerable.Range(0, 300_000).Select(_ => (float)(rnd.NextDouble() * 1000)).ToArray();
        data[5] = -1;
        data[10] = float.NaN;
        var b = Enumerable.Range(0, data.Length).Select(_ => (float)(rnd.NextDouble() * 1000)).ToArray();

        var nativeAvailable = Kernels.IsNativeAccelerated;
        var s1 = Kernels.Statistics(data, -1);
        var h1 = Kernels.Histogram(data, 0, 1000, 50, -1);
        var n1 = new float[data.Length];
        Kernels.NormalizedDifference(data, b, n1, -1);
        var c1 = new byte[data.Length * 4];
        Kernels.Colorize(data, 0, 1000, ColorRamp.Viridis.Lut, c1, -1);

        Kernels.ForceManaged = true;
        try
        {
            var s2 = Kernels.Statistics(data, -1);
            var h2 = Kernels.Histogram(data, 0, 1000, 50, -1);
            var n2 = new float[data.Length];
            Kernels.NormalizedDifference(data, b, n2, -1);
            var c2 = new byte[data.Length * 4];
            Kernels.Colorize(data, 0, 1000, ColorRamp.Viridis.Lut, c2, -1);

            Assert.Equal(s1.ValidCount, s2.ValidCount);
            Assert.Equal(s1.Mean, s2.Mean, 6);
            Assert.Equal(s1.StdDev, s2.StdDev, 6);
            Assert.Equal(h1, h2);
            for (var i = 0; i < n1.Length; i += 997) Assert.Equal(n1[i], n2[i], 5);
            Assert.Equal(c1, c2);
        }
        finally
        {
            Kernels.ForceManaged = false;
        }
        // win-x64 kernels ship in the repo; CI adds the other runtimes.
        Assert.True(nativeAvailable || !OperatingSystem.IsWindows());
    }

    [Fact]
    public void RenderDemWithHillshadeAndEncodePng()
    {
        using var dem = TestData.CreateDem();
        var img = RasterRenderer.Render(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, 400, 300);
        Assert.Equal(400 * 300 * 4, img.Pixels.Length);
        Assert.Equal(255, img[200, 150].A);
        var png = img.EncodePng();
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);

        // Zoomed view partially outside the raster: left part must be transparent.
        var ext = dem.Extent;
        var view = new Envelope(ext.MinX - ext.Width / 2, ext.MinY, ext.CenterX, ext.MaxY);
        var zoomed = RasterRenderer.Render(dem, new RenderOptions(), view, 200, 150);
        Assert.Equal(0, zoomed[10, 75].A);
        Assert.Equal(255, zoomed[190, 75].A);
    }

    [Fact]
    public void RenderRgbComposite()
    {
        using var ms = TestData.CreateMultispectral();
        var img = RasterRenderer.Render(ms, new RenderOptions { Bands = [4, 3, 2], Stretch = StretchMode.MinMax }, 64, 64);
        Assert.Equal(RenderMode.Rgb, RasterRenderer.ResolveMode(ms, new RenderOptions { Bands = [4, 3, 2] }));
        Assert.Equal(255, img[63, 0].R);
    }

    [Fact]
    public void TileGeneration()
    {
        using var dem = TestData.CreateDem();
        using var tiles = new TileGenerator(dem, new RenderOptions { Ramp = ColorRamp.Terrain });
        var dir = TestData.NewTempDir();
        var count = tiles.Generate(dir, 8, 11);
        Assert.True(count >= 4);
        Assert.True(File.Exists(Path.Combine(dir, "leaflet.html")));
        var t = Mercator.TileAt(107.6, -6.9, 10);
        Assert.Equal(10, t.Z);
    }

    [Fact]
    public void VectorSnapshotAndHitTest()
    {
        using var ds = VectorTests.CreateCities(Path.Combine(TestData.NewTempDir(), "snap.gpkg"));
        var data = VectorData.Load(ds.GetLayer(0), Srs.SpatialReference.WebMercator);
        Assert.Equal(5, data.Features.Count);
        var (x, y) = Mercator.FromLonLat(107.6191, -6.9175);
        var hit = data.HitTest(x, y, 100).Single();
        Assert.Equal("Bandung", hit["name"]);

        var st = ScreenTransform.Fit(data.Extent, 800, 600, 20);
        var (sx, sy) = st.ToScreen(x, y);
        var (wx, wy) = st.ToWorld(sx, sy);
        Assert.Equal(x, wx, 3);
        Assert.Equal(y, wy, 3);
        Assert.StartsWith("M", st.ToSvgPath(data.Features[0].Shapes[0]));
    }

    [Fact]
    public void MultidimensionalInMemoryAndNetCdf()
    {
        foreach (var (driver, path) in new[] { ("MEM", "cube"), ("netCDF", Path.Combine(TestData.NewTempDir(), "cube.nc")) })
        {
            using (var ds = Gdal.GetDriver(driver).CreateMultidimensional(path))
            {
                using var root = ds.RootGroup!;
                using var arr = root.CreateArray("temperature", DataType.Float32,
                [
                    ("time", 3, "TEMPORAL", null),
                    ("lat", 4, "HORIZONTAL_Y", "NORTH"),
                    ("lon", 5, "HORIZONTAL_X", "EAST"),
                ]);
                arr.WriteAll<float>(Enumerable.Range(0, 60).Select(i => 20f + i * 0.1f).ToArray());
                arr.SetAttribute("long_name", "Air temperature");
                arr.Unit = "degC";
                if (driver == "MEM")
                {
                    Assert.Equal(60UL, arr.ElementCount);
                    var slice = new float[20];
                    arr.Read<float>([1, 0, 0], [1, 4, 5], slice);
                    Assert.Equal(22f, slice[0], 4);
                    using var classic = arr.AsClassicDataset(2, 1);
                    Assert.Equal(5, classic.RasterWidth);
                    Assert.Equal(3, classic.RasterCount);
                }
            }
            if (driver == "netCDF")
            {
                using var reopened = Gdal.OpenMultidimensional(path);
                using var root = reopened.RootGroup!;
                Assert.Contains("temperature", root.ArrayNames);
                using var arr = root.OpenArray("temperature");
                Assert.Equal(["time", "lat", "lon"], arr.Dimensions.Select(d => d.Name));
                Assert.Equal(20f, arr.ReadAll<float>()[0], 4);
                Assert.Contains(arr.Attributes, a => a.Name == "long_name" && a.StringValue == "Air temperature");
                Assert.Equal("degC", arr.Unit);
                Assert.Contains("temperature", GdalUtils.MultiDimInfo(reopened));
            }
        }
    }
}
