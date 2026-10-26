using GDAL.Net.Processing;
using GDAL.Net.Srs;

namespace GDAL.Net.Tests;

/// <summary>Synthetic, deterministic test data generated with GDAL.Net itself.</summary>
internal static class TestData
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gdalnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A 200x150 Float32 DEM in UTM 48S (Bandung area) with a gaussian hill, 30 m pixels.</summary>
    public static Dataset CreateDem(int width = 200, int height = 150, double? noData = -9999)
    {
        var data = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = (x - width * 0.5) / (width * 0.25);
                var dy = (y - height * 0.5) / (height * 0.25);
                data[y * width + x] = (float)(700 + 1500 * Math.Exp(-(dx * dx + dy * dy)) + 20 * Math.Sin(x * 0.2));
            }
        }
        if (noData is { } nd) data[0] = (float)nd;
        using var srs = SpatialReference.FromEpsg(32748);
        return RasterFactory.FromArray<float>(data, width, height, GeoTransform.FromOrigin(780000, 9240000, 30, 30), srs, noData);
    }

    /// <summary>4-band (B,G,R,NIR) reflectance-like UInt16 image.</summary>
    public static Dataset CreateMultispectral(int width = 64, int height = 64)
    {
        var ds = Gdal.CreateMemoryRaster(width, height, 4, DataType.UInt16);
        ds.GeoTransform = GeoTransform.FromOrigin(107.5, -6.8, 0.0001, 0.0001);
        using (var srs = SpatialReference.Wgs84) ds.SpatialReference = srs;
        for (var b = 1; b <= 4; b++)
        {
            var px = new ushort[width * height];
            for (var i = 0; i < px.Length; i++)
            {
                var x = i % width;
                var vegetated = x >= width / 2;
                px[i] = b switch
                {
                    1 => 800,
                    2 => (ushort)(vegetated ? 900 : 1200),
                    3 => (ushort)(vegetated ? 600 : 1400),
                    _ => (ushort)(vegetated ? 4000 : 1500),
                };
            }
            ds.GetBand(b).WriteAll<ushort>(px);
        }
        return ds;
    }
}
