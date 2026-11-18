using GeoPandas.Net.Analytics;
using GeoPandas.Net.Gpu;
using GeoPandas.Net.Raster;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Tests;

/// <summary>ILGPU kernels against the managed implementations (CPU accelerator, so the test runs anywhere).</summary>
public sealed class GpuTests
{
    private static void Same(RasterGrid expected, RasterGrid actual, double tol)
    {
        var e = expected.Bands[0]; var a = actual.Bands[0];
        Assert.Equal(e.Length, a.Length);
        for (int i = 0; i < e.Length; i++)
        {
            if (float.IsNaN(e[i])) { Assert.True(float.IsNaN(a[i]), $"cell {i}"); continue; }
            Assert.True(Math.Abs(e[i] - a[i]) <= tol, $"cell {i}: {e[i]} vs {a[i]}");
        }
    }

    [Fact]
    public void TerrainKernels_MatchManagedTerrain()
    {
        using var gpu = GpuContext.Create(preferGpu: false);
        var dem = Datasets.SampleData.Dem(120, 90);
        dem.Bands[0][500] = float.NaN;                             // a no-data hole
        Same(Terrain.Slope(dem), GpuRaster.Slope(gpu, dem), 1e-4);
        Same(Terrain.Aspect(dem), GpuRaster.Aspect(gpu, dem), 1e-3);
        Same(Terrain.Hillshade(dem, 315, 45), GpuRaster.Hillshade(gpu, dem, 315, 45), 1e-3);
    }

    [Fact]
    public void NormalizedDifferenceConvolutionAndIdw()
    {
        using var gpu = GpuContext.Create(preferGpu: false);
        var ms = Datasets.SampleData.Multispectral(64, 48);
        var ndvi = GpuRaster.NormalizedDifference(gpu, ms, 2, 1);   // (nir − red) / (nir + red)
        for (int i = 0; i < ndvi.Bands[0].Length; i += 97)
        {
            float nir = ms.Bands[2][i], red = ms.Bands[1][i];
            Assert.Equal((nir - red) / (nir + red), ndvi.Bands[0][i], 6);
        }
        var box = new float[3, 3] { { 1, 1, 1 }, { 1, 1, 1 }, { 1, 1, 1 } };
        var mean = GpuRaster.Convolve(gpu, ms, box, 1);
        int w = ms.Width, r = 10, c = 10;
        double expected = 0;
        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) expected += ms.Bands[1][(r + dy) * w + c + dx];
        Assert.Equal(expected / 9, mean.Bands[0][r * w + c], 4);
        Assert.Equal((ms.Bands[1][0] + ms.Bands[1][1] + ms.Bands[1][w] + ms.Bands[1][w + 1]) / 4, mean.Bands[0][0], 4);   // corner: 4 valid cells
        // IDW against the managed interpolator (all samples, power 2)
        double[] xs = [10, 40, 75, 90, 20], ys = [10, 80, 30, 95, 55], zs = [5, 12, 7, 20, 9];
        var t = new GeoTransform(0, 5, 0, 100, 0, -5);
        var grid = GpuRaster.Idw(gpu, xs, ys, zs, t, 20, 20);
        var qx = new double[400]; var qy = new double[400];
        for (int k = 0; k < 400; k++) { qx[k] = (k % 20 + 0.5) * 5; qy[k] = 100 - (k / 20 + 0.5) * 5; }
        var managed = Interpolation.Idw(xs, ys, zs, qx, qy, power: 2, neighbors: 5);
        for (int k = 0; k < 400; k++) Assert.Equal(managed[k], grid.Bands[0][k], 3);
    }
}
