using GDAL.Net.Srs;

namespace GDAL.Net.Tests;

public class RasterTests
{
    [Fact]
    public void CreateWriteReadGeoTiff()
    {
        var dir = TestData.NewTempDir();
        var path = Path.Combine(dir, "test.tif");
        using (var ds = Gdal.GetDriver("GTiff").Create(path, 100, 50, 2, DataType.Int16, new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE", ["TILED"] = "YES" }))
        {
            ds.GeoTransform = GeoTransform.FromOrigin(106.8, -6.1, 0.01, 0.01);
            using var srs = SpatialReference.Wgs84;
            ds.SpatialReference = srs;
            var data = Enumerable.Range(0, 5000).Select(i => (short)(i % 300)).ToArray();
            ds.GetBand(1).WriteAll<short>(data);
            ds.GetBand(1).NoDataValue = -1;
            ds.GetBand(2).Fill(42);
            ds.SetMetadataItem("AUTHOR", "Gravicode Studios");
        }

        using var read = Gdal.OpenRaster(path);
        Assert.Equal(100, read.RasterWidth);
        Assert.Equal(50, read.RasterHeight);
        Assert.Equal(2, read.RasterCount);
        Assert.Equal("GTiff", read.Driver.ShortName);
        Assert.Equal(DataType.Int16, read.GetBand(1).DataType);
        Assert.Equal(-1, read.GetBand(1).NoDataValue);
        Assert.Equal(0.01, read.GeoTransform.PixelWidth, 9);
        Assert.Equal(4326, read.SpatialReference!.Epsg);
        Assert.Equal("Gravicode Studios", read.GetMetadataItem("AUTHOR"));
        Assert.Equal("DEFLATE", read.GetMetadata("IMAGE_STRUCTURE")["COMPRESSION"]);

        var row = new short[100];
        read.GetBand(1).Read<short>(0, 3, 100, 1, row);
        Assert.Equal((short)(300 % 300), row[0]);
        Assert.All(read.GetBand(2).ReadAll<byte>(), v => Assert.Equal(42, v));

        var extent = read.Extent;
        Assert.Equal(106.8, extent.MinX, 9);
        Assert.Equal(-6.1, extent.MaxY, 9);
        Assert.Equal(-6.6, extent.MinY, 9);
    }

    [Fact]
    public void StatisticsHistogramAndChecksum()
    {
        using var dem = TestData.CreateDem();
        var band = dem.GetBand(1);
        var stats = band.ComputeStatistics();
        Assert.InRange(stats.Minimum, 650, 720);
        Assert.InRange(stats.Maximum, 2150, 2250);
        Assert.True(stats.StdDev > 0);

        var hist = band.GetHistogram(64);
        Assert.Equal(64, hist.BucketCount);
        Assert.Equal((ulong)(200 * 150 - 1), hist.Total);
        Assert.NotEqual(0, band.Checksum());
    }

    [Fact]
    public void ResampledReadAndProcessRows()
    {
        using var dem = TestData.CreateDem();
        var band = dem.GetBand(1);
        var small = band.ReadResampled<float>(50, 30, ResampleAlgorithm.Average);
        Assert.Equal(1500, small.Length);

        double sum = 0;
        long count = 0;
        band.ProcessRows<float>((y, rows, data) =>
        {
            foreach (var v in data) if (v != -9999) { sum += v; count++; }
        }, rowsPerChunk: 7);
        Assert.Equal(200 * 150 - 1, count);
        Assert.InRange(sum / count, 700, 2200);
    }

    [Fact]
    public void OverviewsColorTableAndRat()
    {
        var path = Path.Combine(TestData.NewTempDir(), "classes.tif");
        using (var ds = Gdal.GetDriver("GTiff").Create(path, 512, 512, 1, DataType.Byte))
        {
            var px = new byte[512 * 512];
            for (var i = 0; i < px.Length; i++) px[i] = (byte)((i % 512) / 128);
            ds.GetBand(1).WriteAll<byte>(px);
            using var ct = new ColorTable();
            ct[0] = ColorEntry.FromRgb(0, 0, 255);
            ct[1] = ColorEntry.FromRgb(0, 255, 0);
            ct[2] = ColorEntry.FromRgb(255, 255, 0);
            ct[3] = ColorEntry.FromRgb(255, 0, 0);
            ds.GetBand(1).ColorTable = ct;
            using var rat = new RasterAttributeTable();
            rat.AddColumn("Value", RatFieldType.Integer, RatFieldUsage.MinMax);
            rat.AddColumn("Class", RatFieldType.String, RatFieldUsage.Name);
            string[] names = ["Water", "Forest", "Field", "Urban"];
            for (var i = 0; i < 4; i++)
            {
                rat.Set(i, 0, i);
                rat.Set(i, 1, names[i]);
            }
            ds.GetBand(1).AttributeTable = rat;
            ds.BuildOverviews("NEAREST", [2, 4, 8]);
        }

        using var read = Gdal.OpenRaster(path);
        var band = read.GetBand(1);
        Assert.Equal(3, band.OverviewCount);
        Assert.Equal(64, band.GetOverview(2).Width);
        using var table = band.ColorTable!;
        Assert.Equal(255, table[0].C3);
        var attr = band.AttributeTable!;
        Assert.Equal(4, attr.RowCount);
        Assert.Equal("Forest", attr.GetString(1, 1));
        Assert.Equal(64, band.GetBestOverview(60, 60).Width);
    }

    [Fact]
    public void GeoTransformMath()
    {
        var gt = GeoTransform.FromOrigin(100, 200, 2, 2);
        var (x, y) = gt.PixelToWorld(10, 5);
        Assert.Equal(120, x);
        Assert.Equal(190, y);
        var (p, l) = gt.WorldToPixel(120, 190);
        Assert.Equal(10, p, 9);
        Assert.Equal(5, l, 9);

        var rotated = new GeoTransform(100, 2, 0.5, 200, 0.3, -2);
        var w = rotated.PixelToWorld(7, 3);
        var back = rotated.WorldToPixel(w.X, w.Y);
        Assert.Equal(7, back.Pixel, 9);
        Assert.Equal(3, back.Line, 9);
    }

    [Fact]
    public void GcpsRoundTrip()
    {
        using var ds = Gdal.CreateMemoryRaster(100, 100, 1, DataType.Byte);
        using var srs = SpatialReference.Wgs84;
        ds.SetGcps([new(0, 0, 106.0, -6.0), new(100, 0, 107.0, -6.0), new(0, 100, 106.0, -7.0), new(100, 100, 107.0, -7.0)], srs);
        Assert.Equal(4, ds.Gcps.Count);
        Assert.Equal(107.0, ds.Gcps[1].X);
        Assert.Equal(4326, ds.GcpSpatialReference!.Epsg);

        using var rectified = GdalUtils.Warp(ds, "", new WarpOptions { Format = "MEM", PolynomialOrder = 1, TargetSrs = "EPSG:4326" });
        Assert.True(rectified.HasGeoTransform);
        Assert.Equal(106.0, rectified.Extent.MinX, 3);
    }

    [Fact]
    public void DisposedDatasetThrowsInsteadOfCrashing()
    {
        var ds = TestData.CreateDem(10, 10);
        var band = ds.GetBand(1);
        ds.Dispose();
        Assert.Throws<ObjectDisposedException>(() => band.Width);
        Assert.Throws<ObjectDisposedException>(() => ds.RasterCount);
    }
}
