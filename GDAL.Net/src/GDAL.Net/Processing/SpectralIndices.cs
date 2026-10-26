using System.Buffers;

namespace GDAL.Net.Processing;

/// <summary>Helpers to create rasters that share the grid (size, geotransform, CRS) of another raster.</summary>
public static class RasterFactory
{
    /// <summary>Creates a raster with the same grid as <paramref name="template"/> (in memory by default).</summary>
    public static Dataset CreateLike(Dataset template, int bandCount, DataType dataType, string driver = "MEM", string path = "",
        IDictionary<string, string>? creationOptions = null)
    {
        var ds = Gdal.GetDriver(driver).Create(path, template.RasterWidth, template.RasterHeight, bandCount, dataType, creationOptions);
        if (template.HasGeoTransform) ds.GeoTransform = template.GeoTransform;
        using var srs = template.SpatialReference;
        if (srs is not null) ds.SpatialReference = srs;
        return ds;
    }

    /// <summary>Creates a georeferenced in-memory raster from an array (row-major).</summary>
    public static Dataset FromArray<T>(ReadOnlySpan<T> data, int width, int height, GeoTransform? geoTransform = null, Srs.SpatialReference? srs = null,
        double? noData = null) where T : unmanaged
    {
        var ds = Gdal.CreateMemoryRaster(width, height, 1, DataTypes.Of<T>());
        if (geoTransform is { } gt) ds.GeoTransform = gt;
        if (srs is not null) ds.SpatialReference = srs;
        var band = ds.GetBand(1);
        band.WriteAll(data);
        if (noData is { } nd) band.NoDataValue = nd;
        return ds;
    }
}

/// <summary>
/// Remote sensing spectral indices. Bands are streamed in row blocks; normalized-difference indices use the
/// Rust/SIMD kernels. Outputs are Float32 rasters (NaN = nodata) with the input georeferencing.
/// </summary>
public static class SpectralIndices
{
    /// <summary>Normalized difference (a - b) / (a + b) between two bands of identical size.</summary>
    public static Dataset NormalizedDifference(RasterBand a, RasterBand b, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("Bands must have the same size.");
        var result = RasterFactory.CreateLike(a.Dataset, 1, DataType.Float32);
        var outBand = result.GetBand(1);
        var width = a.Width;
        var height = a.Height;
        var rows = Math.Clamp((1 << 20) / Math.Max(1, width), 1, height);
        var pool = ArrayPool<float>.Shared;
        var ba = pool.Rent(rows * width);
        var bb = pool.Rent(rows * width);
        var bo = pool.Rent(rows * width);
        var ndA = a.NoDataValue;
        var ndB = b.NoDataValue;
        try
        {
            for (var y = 0; y < height; y += rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = Math.Min(rows, height - y);
                var count = n * width;
                var sa = ba.AsSpan(0, count);
                var sb = bb.AsSpan(0, count);
                a.Read<float>(0, y, width, n, sa);
                b.Read<float>(0, y, width, n, sb);
                // Map both nodata values to NaN so a single kernel pass handles them.
                if (ndA is { } va) ReplaceWithNaN(sa, (float)va);
                if (ndB is { } vb) ReplaceWithNaN(sb, (float)vb);
                Kernels.NormalizedDifference(sa, sb, bo.AsSpan(0, count), null, float.NaN);
                outBand.Write<float>(0, y, width, n, bo.AsSpan(0, count));
                progress?.Report(new GdalProgress((double)(y + n) / height, null));
            }
        }
        finally
        {
            pool.Return(ba);
            pool.Return(bb);
            pool.Return(bo);
        }
        return result;
    }

    private static void ReplaceWithNaN(Span<float> s, float nodata)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == nodata) s[i] = float.NaN;
        }
    }

    /// <summary>NDVI = (NIR - Red) / (NIR + Red): vegetation vigor, [-1, 1].</summary>
    public static Dataset Ndvi(RasterBand nir, RasterBand red, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => NormalizedDifference(nir, red, progress, cancellationToken);

    /// <summary>NDWI (McFeeters) = (Green - NIR) / (Green + NIR): open water, [-1, 1].</summary>
    public static Dataset Ndwi(RasterBand green, RasterBand nir, IProgress<GdalProgress>? progress = null, CancellationToken cancellationToken = default)
        => NormalizedDifference(green, nir, progress, cancellationToken);

    /// <summary>MNDWI = (Green - SWIR1) / (Green + SWIR1): water in built-up areas.</summary>
    public static Dataset Mndwi(RasterBand green, RasterBand swir1) => NormalizedDifference(green, swir1);

    /// <summary>NDBI = (SWIR1 - NIR) / (SWIR1 + NIR): built-up areas.</summary>
    public static Dataset Ndbi(RasterBand swir1, RasterBand nir) => NormalizedDifference(swir1, nir);

    /// <summary>NBR = (NIR - SWIR2) / (NIR + SWIR2): burn severity.</summary>
    public static Dataset Nbr(RasterBand nir, RasterBand swir2) => NormalizedDifference(nir, swir2);

    /// <summary>SAVI = (1 + L)(NIR - Red) / (NIR + Red + L): soil adjusted vegetation index.</summary>
    public static Dataset Savi(RasterBand nir, RasterBand red, float l = 0.5f)
    {
        var bm = BandMath.Compile($"(1 + {l.ToString(System.Globalization.CultureInfo.InvariantCulture)}) * (NIR - RED) / (NIR + RED + {l.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        var result = RasterFactory.CreateLike(nir.Dataset, 1, DataType.Float32);
        bm.Evaluate(new Dictionary<string, RasterBand> { ["NIR"] = nir, ["RED"] = red }, result.GetBand(1));
        return result;
    }

    /// <summary>EVI = 2.5 (NIR - Red) / (NIR + 6 Red - 7.5 Blue + 1) (reflectance in [0,1]).</summary>
    public static Dataset Evi(RasterBand nir, RasterBand red, RasterBand blue)
    {
        var bm = BandMath.Compile("2.5 * (NIR - RED) / (NIR + 6 * RED - 7.5 * BLUE + 1)");
        var result = RasterFactory.CreateLike(nir.Dataset, 1, DataType.Float32);
        bm.Evaluate(new Dictionary<string, RasterBand> { ["NIR"] = nir, ["RED"] = red, ["BLUE"] = blue }, result.GetBand(1));
        return result;
    }
}
