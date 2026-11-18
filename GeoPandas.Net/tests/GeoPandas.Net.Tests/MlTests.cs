using GeoPandas.Net.Geometry;
using GeoPandas.Net.ML;
using GeoPandas.Net.Raster;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Tests;

/// <summary>ONNX segmentation against onnxruntime-python 1.32 on the same model and input; ML.NET land-cover classification on synthetic imagery.</summary>
public sealed class MlTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

    private static float[] F32(string file)
    {
        var bytes = File.ReadAllBytes(Data(file));
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    [Theory]
    [InlineData(512, 0)]      // one tile
    [InlineData(24, 4)]       // 3 × 2 overlapping tiles
    public void OnnxSegmenter_MatchesOnnxRuntimePython(int tile, int overlap)
    {
        int w = 64, h = 48;
        var x = F32("seg_input_4x48x64.f32");
        var bands = Enumerable.Range(0, 4).Select(b => x.AsSpan(b * w * h, w * h).ToArray()).ToArray();
        var image = new RasterGrid(w, h, bands, new GeoTransform(0, 1, 0, h, 0, -1));
        using var seg = new OnnxSegmenter(Data("seg_tiny.onnx")) { TileSize = tile, Overlap = overlap, ClassNames = ["water", "built", "vegetation"] };
        var (classes, probs) = seg.Predict(image);
        var expected = F32("seg_probs_3x48x64.f32");
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < w * h; i++)
                Assert.True(Math.Abs(expected[k * w * h + i] - probs.Bands[k][i]) < 1e-6, $"class {k} pixel {i}");
        for (int i = 0; i < w * h; i++)
        {
            int best = Enumerable.Range(0, 3).MaxBy(k => expected[k * w * h + i]);
            Assert.Equal(best, (int)classes.Bands[0][i]);
        }
        Assert.Equal(["water", "built", "vegetation"], probs.BandNames);
    }

    private static double _t1, _t2;   // NDVI tertiles of the scene (set per test)
    private static string Truth(float red, float nir)
    {
        double ndvi = (nir - red) / (nir + red);
        return ndvi < _t1 ? "urban" : ndvi < _t2 ? "grass" : "forest";
    }

    [Theory]
    [InlineData(LandCoverAlgorithm.RandomForest)]
    [InlineData(LandCoverAlgorithm.MaximumEntropy)]
    public void LandCover_LearnsNdviClasses(LandCoverAlgorithm algorithm)
    {
        var img = Datasets.SampleData.Multispectral(96, 72);   // green, red, nir, swir1
        var ndvis = Enumerable.Range(0, img.Width * img.Height).Select(i => (img.Bands[2][i] - img.Bands[1][i]) / (double)(img.Bands[2][i] + img.Bands[1][i])).Order().ToArray();
        (_t1, _t2) = (ndvis[ndvis.Length / 3], ndvis[2 * ndvis.Length / 3]);
        var rng = new Random(3);
        GeoDataFrame Samples(int n)
        {
            var pts = new List<NetTopologySuite.Geometries.Geometry?>(); var labels = new List<string>();
            for (int k = 0; k < n; k++)
            {
                int r = rng.Next(img.Height), c = rng.Next(img.Width);
                var (px, py) = img.Transform.PixelToWorld(c + 0.5, r + 0.5);
                pts.Add(Geo.Point(px, py));
                labels.Add(Truth(img.Bands[1][r * img.Width + c], img.Bands[2][r * img.Width + c]));
            }
            return GeoDataFrame.FromGeometries(pts, img.Crs, Column.Create("class", labels.ToArray()));
        }
        var train = Samples(600);
        var clf = LandCoverClassifier.Train(img, train, "class", algorithm: algorithm);
        Assert.Equal(["forest", "grass", "urban"], clf.Labels);
        var test = LandCoverClassifier.SamplePixels(img, Samples(400), "class", [0, 1, 2, 3]);
        var report = clf.Evaluate(test);
        Assert.True(report.OverallAccuracy > 0.85, report.ToString());
        Assert.True(report.Kappa > 0.7, report.ToString());
        Assert.Equal(400, report.ToFrame()["forest"].ToDoubleArray().Sum() + report.ToFrame()["grass"].ToDoubleArray().Sum() + report.ToFrame()["urban"].ToDoubleArray().Sum());
        var map = clf.Predict(img);
        int agree = 0;
        for (int i = 0; i < map.Bands[0].Length; i++)
            if (clf.Labels[(int)map.Bands[0][i]] == Truth(img.Bands[1][i], img.Bands[2][i])) agree++;
        Assert.True(agree > 0.85 * map.Bands[0].Length, $"{agree} / {map.Bands[0].Length}");
        // polygons as training areas
        var poly = GeoDataFrame.FromGeometries([img.Bounds is var b ? Geo.Box(b.MinX, b.MinY, b.MinX + b.Width / 4, b.MinY + b.Height / 4) : null], img.Crs, Column.Create("class", new[] { "x" }));
        Assert.Equal(50, LandCoverClassifier.SamplePixels(img, poly, "class", [0, 1], maxPixelsPerFeature: 50).Count);
    }
}
