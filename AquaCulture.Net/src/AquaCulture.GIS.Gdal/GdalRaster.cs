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

/// <summary>
/// Water-colour remote sensing for ponds, reservoirs and floating-net-cage (KJA) zones from Sentinel-2 L2A surface reflectance.
/// Band names follow Sentinel-2: B3 green 560 nm, B4 red 665 nm, B5 red-edge 705 nm, B8 NIR 842 nm, B11 SWIR 1610 nm.
/// </summary>
public static class WaterColour
{
    /// <summary>Normalised difference (a − b) / (a + b), NaN where undefined.</summary>
    public static GeoRaster NormalizedDifference(GeoRaster a, GeoRaster b)
    {
        Check(a, b);
        var o = new GeoRaster(a.Bounds, a.Width, a.Height);
        for (int i = 0; i < o.Data.Length; i++)
        {
            float s = a.Data[i] + b.Data[i];
            o.Data[i] = s == 0 || float.IsNaN(s) ? float.NaN : (a.Data[i] - b.Data[i]) / s;
        }
        return o;
    }

    /// <summary>NDWI (McFeeters 1996) = (green − NIR) / (green + NIR); water &gt; 0.</summary>
    public static GeoRaster Ndwi(GeoRaster green, GeoRaster nir) => NormalizedDifference(green, nir);

    /// <summary>MNDWI (Xu 2006) = (green − SWIR) / (green + SWIR); separates water from built-up land better than NDWI.</summary>
    public static GeoRaster Mndwi(GeoRaster green, GeoRaster swir) => NormalizedDifference(green, swir);

    /// <summary>NDCI (Mishra &amp; Mishra 2012) = (red-edge 705 − red 665) / (red-edge + red).</summary>
    public static GeoRaster Ndci(GeoRaster redEdge, GeoRaster red) => NormalizedDifference(redEdge, red);

    /// <summary>
    /// Chlorophyll-a (µg/L) from NDCI with the Mishra &amp; Mishra (2012) polynomial: 14.039 + 86.115·NDCI + 194.325·NDCI².
    /// Valid roughly for NDCI −0.1 … 0.5 (≈ 5 – 100 µg/L); values outside are clamped to ≥ 0. Calibrate locally for absolute accuracy.
    /// </summary>
    public static double ChlorophyllFromNdci(double ndci) => Math.Max(0, 14.039 + 86.115 * ndci + 194.325 * ndci * ndci);

    public static GeoRaster Chlorophyll(GeoRaster ndci, GeoRaster? waterMask = null) =>
        Map(ndci, waterMask, v => (float)ChlorophyllFromNdci(v));

    /// <summary>
    /// Turbidity (FNU) from red reflectance with the Dogliotti et al. (2015) single-band algorithm at 645–665 nm:
    /// T = A·ρ / (1 − ρ / C), A = 228.1, C = 0.1641.
    /// </summary>
    public static double TurbidityFromRed(double rhoRed) => rhoRed >= 0.1641 ? double.NaN : Math.Max(0, 228.1 * rhoRed / (1 - rhoRed / 0.1641));

    public static GeoRaster Turbidity(GeoRaster red, GeoRaster? waterMask = null) => Map(red, waterMask, v => (float)TurbidityFromRed(v));

    /// <summary>Water mask: 1 where NDWI &gt; threshold, otherwise 0 (NaN stays NaN).</summary>
    public static GeoRaster WaterMask(GeoRaster ndwi, double threshold = 0.0)
    {
        var o = new GeoRaster(ndwi.Bounds, ndwi.Width, ndwi.Height);
        for (int i = 0; i < o.Data.Length; i++) o.Data[i] = float.IsNaN(ndwi.Data[i]) ? float.NaN : ndwi.Data[i] > threshold ? 1 : 0;
        return o;
    }

    /// <summary>Trophic class from chlorophyll-a (µg/L), after Carlson-type thresholds used for tropical reservoirs.</summary>
    public static Text2 TrophicState(double chlUgL) => chlUgL switch
    {
        < 2.6 => new("oligotrophic", "oligotrofik"),
        < 20 => new("mesotrophic", "mesotrofik"),
        < 56 => new("eutrophic", "eutrofik"),
        _ => new("hypereutrophic (bloom risk)", "hipereutrofik (risiko blooming)"),
    };

    /// <summary>Summary statistics of a raster over the water pixels (mask = 1), ignoring NaN.</summary>
    public static WaterStats Summarize(GeoRaster values, GeoRaster? waterMask = null)
    {
        var v = new List<double>();
        for (int i = 0; i < values.Data.Length; i++)
        {
            if (waterMask is not null && waterMask.Data[i] != 1) continue;
            if (!float.IsNaN(values.Data[i])) v.Add(values.Data[i]);
        }
        if (v.Count == 0) return new WaterStats(0, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        v.Sort();
        double area = v.Count * values.CellSizeM * values.CellSizeM / 10_000;
        return new WaterStats(v.Count, v.Average(), v[v.Count / 2], v[0], v[^1], area);
    }

    private static GeoRaster Map(GeoRaster src, GeoRaster? mask, Func<float, float> f)
    {
        if (mask is not null) Check(src, mask);
        var o = new GeoRaster(src.Bounds, src.Width, src.Height);
        for (int i = 0; i < o.Data.Length; i++)
            o.Data[i] = float.IsNaN(src.Data[i]) || mask is not null && mask.Data[i] != 1 ? float.NaN : f(src.Data[i]);
        return o;
    }

    private static void Check(GeoRaster a, GeoRaster b)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("Rasters must have the same size (resample first).");
    }
}

/// <param name="Pixels">Number of valid water pixels.</param>
/// <param name="AreaHa">Approximate area of those pixels in hectares.</param>
public sealed record WaterStats(int Pixels, double Mean, double Median, double Min, double Max, double AreaHa);

public static class GdalBuilderExtensions
{
    /// <summary>Loads the GDAL runtime at start-up (registers the module once).</summary>
    public static AquaBuilder AddGdal(this AquaBuilder b) => b.TryAddModule("GIS.Gdal", _ => GdalRaster.EnsureInitialized());
}
