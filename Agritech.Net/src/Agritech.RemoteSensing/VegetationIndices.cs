using Agritech.Core.Data;
using Agritech.Core.Geo;
using Agritech.Native;

namespace Agritech.RemoteSensing;

public enum VegetationIndex { Ndvi, Evi, Savi, Ndwi, Ndmi, Ndre, Gndvi, Msavi2, Nbr }

/// <summary>Spectral indices over <see cref="MultispectralImage"/> using native/SIMD kernels.</summary>
public static class VegetationIndices
{
    public static RasterBand Compute(MultispectralImage img, VegetationIndex index) => index switch
    {
        VegetationIndex.Ndvi => Nd(img, SpectralBand.Nir, SpectralBand.Red, "NDVI"),
        VegetationIndex.Ndwi => Nd(img, SpectralBand.Green, SpectralBand.Nir, "NDWI"), // McFeeters (open water)
        VegetationIndex.Ndmi => Nd(img, SpectralBand.Nir, SpectralBand.Swir1, "NDMI"), // canopy moisture
        VegetationIndex.Ndre => Nd(img, SpectralBand.Nir, SpectralBand.RedEdge1, "NDRE"),
        VegetationIndex.Gndvi => Nd(img, SpectralBand.Nir, SpectralBand.Green, "GNDVI"),
        VegetationIndex.Nbr => Nd(img, SpectralBand.Nir, SpectralBand.Swir2, "NBR"),
        VegetationIndex.Savi => Savi(img),
        VegetationIndex.Evi => Evi(img),
        VegetationIndex.Msavi2 => Msavi2(img),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static RasterBand Ndvi(MultispectralImage img) => Compute(img, VegetationIndex.Ndvi);

    private static RasterBand Nd(MultispectralImage img, SpectralBand a, SpectralBand b, string name)
    {
        var ra = img[a]; var rb = img[b];
        var o = ra.Like(name);
        AgriKernels.NormalizedDifference(ra.Data, rb.Data, o.Data);
        return o;
    }

    public static RasterBand Savi(MultispectralImage img, float l = 0.5f)
    {
        var o = img[SpectralBand.Nir].Like("SAVI");
        AgriKernels.Savi(img[SpectralBand.Nir].Data, img[SpectralBand.Red].Data, o.Data, l);
        return o;
    }

    public static RasterBand Evi(MultispectralImage img)
    {
        var o = img[SpectralBand.Nir].Like("EVI");
        AgriKernels.Evi(img[SpectralBand.Nir].Data, img[SpectralBand.Red].Data, img[SpectralBand.Blue].Data, o.Data);
        return o;
    }

    /// <summary>MSAVI2 = (2NIR + 1 − √((2NIR+1)² − 8(NIR−Red))) / 2 — no soil-line parameter.</summary>
    public static RasterBand Msavi2(MultispectralImage img)
    {
        var nir = img[SpectralBand.Nir].Data; var red = img[SpectralBand.Red].Data;
        var o = img[SpectralBand.Nir].Like("MSAVI2");
        for (int i = 0; i < nir.Length; i++)
        {
            float t = 2 * nir[i] + 1;
            o.Data[i] = (t - MathF.Sqrt(MathF.Max(0, t * t - 8 * (nir[i] - red[i])))) / 2;
        }
        return o;
    }

    /// <summary>Scalar NDVI.</summary>
    public static double Ndvi(double nir, double red) => nir + red == 0 ? double.NaN : (nir - red) / (nir + red);
}

public enum VegetationHealth { NoData, BareSoilOrWater, Sparse, Moderate, Healthy, VeryDense }

public sealed record ZonalStatistics(string Zone, RasterStats Stats, int PixelCount, IReadOnlyDictionary<VegetationHealth, double> HealthShare);

/// <summary>Classification and zonal analysis of index rasters.</summary>
public static class RasterAnalysis
{
    public static VegetationHealth ClassifyNdvi(float v) => float.IsNaN(v) ? VegetationHealth.NoData
        : v < 0.15f ? VegetationHealth.BareSoilOrWater
        : v < 0.35f ? VegetationHealth.Sparse
        : v < 0.55f ? VegetationHealth.Moderate
        : v < 0.75f ? VegetationHealth.Healthy
        : VegetationHealth.VeryDense;

    /// <summary>Statistics of raster pixels whose centre falls inside the polygon.</summary>
    public static ZonalStatistics Zonal(RasterBand band, GeoPolygon zone, string name = "zone")
    {
        var b = zone.Bounds;
        var (c0, r0) = band.Transform.ToPixel(new GeoCoordinate(b.MaxLat, b.MinLon));
        var (c1, r1) = band.Transform.ToPixel(new GeoCoordinate(b.MinLat, b.MaxLon));
        c0 = Math.Max(0, c0); r0 = Math.Max(0, r0); c1 = Math.Min(band.Width - 1, c1); r1 = Math.Min(band.Height - 1, r1);
        var values = new List<float>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (zone.Contains(band.Transform.PixelCenter(c, r))) values.Add(band[c, r]);
        var arr = values.ToArray();
        var stats = AgriKernels.Statistics(arr);
        var share = arr.Where(v => !float.IsNaN(v)).GroupBy(ClassifyNdvi)
            .ToDictionary(g => g.Key, g => Math.Round(g.Count() * 100.0 / Math.Max(1, stats.Count), 2));
        return new ZonalStatistics(name, stats, arr.Length, share);
    }

    /// <summary>Splits a field into management zones (low/medium/high vigour) using quantile thresholds.</summary>
    public static byte[] ManagementZones(RasterBand index, int zones = 3)
    {
        var valid = index.Data.Where(v => !float.IsNaN(v)).Select(v => (double)v).ToArray();
        var cuts = Enumerable.Range(1, zones - 1).Select(i => Stats.Percentile(valid, 100.0 * i / zones)).ToArray();
        var res = new byte[index.Data.Length];
        for (int i = 0; i < res.Length; i++)
        {
            float v = index.Data[i];
            if (float.IsNaN(v)) { res[i] = 0; continue; }
            byte z = 1;
            foreach (var c in cuts) if (v > c) z++;
            res[i] = z;
        }
        return res;
    }

    /// <summary>Change detection: per-pixel difference (after − before).</summary>
    public static RasterBand Difference(RasterBand before, RasterBand after)
    {
        var o = after.Like($"Δ{after.Name}");
        for (int i = 0; i < o.Data.Length; i++) o.Data[i] = after.Data[i] - before.Data[i];
        return o;
    }
}
