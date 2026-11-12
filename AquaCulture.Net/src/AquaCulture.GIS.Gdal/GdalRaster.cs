using AquaCulture.Core;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Hosting;
using GDAL.Net;
using GDAL.Net.Srs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AquaCulture.GIS.Gdal;

/// <summary>
/// Raster I/O through GDAL (GDAL.Net): GeoTIFF and every other GDAL raster format ↔ <see cref="GeoRaster"/>.
/// Rasters in a projected CRS (UTM Sentinel-2 tiles, national DEMs) are warped to WGS-84 on read.
/// </summary>
public static class GdalRaster
{
    private static readonly Lazy<bool> Init = new(() => { GDAL.Net.Gdal.Initialize(); return true; });
    private static int _mem;

    /// <summary>Makes sure the native GDAL runtime is loaded (called by every method).</summary>
    public static void EnsureInitialized() => _ = Init.Value;

    /// <summary>The GDAL version string (for diagnostics).</summary>
    public static string Version { get { EnsureInitialized(); return GDAL.Net.Gdal.Version; } }

    /// <summary>Reads one band (1-based) of a raster file. NoData becomes NaN.</summary>
    public static GeoRaster Read(string path, int band = 1)
    {
        EnsureInitialized();
        using var ds = GDAL.Net.Gdal.OpenRaster(path);
        return Read(ds, band);
    }

    /// <summary>Reads every band of a raster file (for multispectral scenes).</summary>
    public static IReadOnlyList<GeoRaster> ReadAllBands(string path)
    {
        EnsureInitialized();
        using var ds = GDAL.Net.Gdal.OpenRaster(path);
        using var wgs = ToWgs84(ds, out bool warped);
        var src = warped ? wgs! : ds;
        return Enumerable.Range(1, src.RasterCount).Select(b => ReadBand(src, b)).ToList();
    }

    /// <summary>Reads one band of an open dataset, warping to WGS-84 when needed.</summary>
    public static GeoRaster Read(Dataset ds, int band = 1)
    {
        EnsureInitialized();
        using var wgs = ToWgs84(ds, out bool warped);
        return ReadBand(warped ? wgs! : ds, band);
    }

    private static Dataset? ToWgs84(Dataset ds, out bool warped)
    {
        warped = false;
        var srs = ds.SpatialReference;
        if (srs is null || srs.Epsg == 4326) return null;
        warped = true;
        string dst = $"/vsimem/aquaculture-warp-{Interlocked.Increment(ref _mem)}.tif";
        return GdalUtils.Warp(ds, dst, new WarpOptions { TargetSrs = "EPSG:4326" });
    }

    private static GeoRaster ReadBand(Dataset ds, int band)
    {
        if (!ds.HasGeoTransform) throw new InvalidOperationException("Raster has no geotransform.");
        var gt = ds.GeoTransform;
        if (!gt.IsNorthUp) throw new NotSupportedException("Rotated rasters are not supported; warp to a north-up grid first.");
        int w = ds.RasterWidth, h = ds.RasterHeight;
        var b = ds.GetBand(band);
        var data = new float[w * h];
        b.Read<float>(0, 0, w, h, data);
        if (b.NoDataValue is { } nd)
        {
            float ndf = (float)nd;
            for (int i = 0; i < data.Length; i++) if (data[i] == ndf) data[i] = float.NaN;
        }
        if (b.Scale is var s && s != 0 && s != 1 || b.Offset != 0)
        {
            double scale = b.Scale == 0 ? 1 : b.Scale, off = b.Offset;
            for (int i = 0; i < data.Length; i++) data[i] = (float)(data[i] * scale + off);
        }
        double minLon = gt.OriginX, maxLat = gt.OriginY, maxLon = gt.OriginX + gt.PixelWidth * w, minLat = gt.OriginY + gt.PixelHeight * h;
        return new GeoRaster(new GeoBounds(Math.Min(minLat, maxLat), Math.Min(minLon, maxLon), Math.Max(minLat, maxLat), Math.Max(minLon, maxLon)), w, h, data);
    }

    /// <summary>Writes rasters as bands of a WGS-84 GeoTIFF (Float32, DEFLATE, NaN = NoData). All rasters must share bounds and size.</summary>
    public static void WriteGeoTiff(string path, params GeoRaster[] bands)
    {
        if (bands.Length == 0) throw new ArgumentException("At least one band is required.", nameof(bands));
        EnsureInitialized();
        var r0 = bands[0];
        if (bands.Any(b => b.Width != r0.Width || b.Height != r0.Height)) throw new ArgumentException("All bands must have the same size.", nameof(bands));
        var driver = GDAL.Net.Gdal.GetDriver("GTiff");
        using var ds = driver.Create(path, r0.Width, r0.Height, bands.Length, DataType.Float32, new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE", ["TILED"] = "YES" });
        var bb = r0.Bounds;
        ds.GeoTransform = GeoTransform.FromOrigin(bb.MinLon, bb.MaxLat, (bb.MaxLon - bb.MinLon) / r0.Width, (bb.MaxLat - bb.MinLat) / r0.Height);
        ds.SpatialReference = SpatialReference.Wgs84;
        for (int i = 0; i < bands.Length; i++)
        {
            var band = ds.GetBand(i + 1);
            band.NoDataValue = double.NaN;
            band.Write<float>(0, 0, r0.Width, r0.Height, bands[i].Data);
        }
        ds.FlushCache();
    }
}

public static class GdalBuilderExtensions
{
    /// <summary>Loads the GDAL runtime at start-up (registers the module once).</summary>
    public static AquaBuilder AddGdal(this AquaBuilder b) => b.TryAddModule("GIS.Gdal", _ => GdalRaster.EnsureInitialized());
}
