using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Plotting;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Raster;

/// <summary>
/// Affine geotransform (GDAL order): X = c0 + col·c1 + row·c2, Y = c3 + col·c4 + row·c5.
/// </summary>
public readonly record struct GeoTransform(double OriginX, double PixelWidth, double RotationX, double OriginY, double RotationY, double PixelHeight)
{
    /// <summary>North-up transform from an upper-left origin and a positive cell size.</summary>
    public static GeoTransform NorthUp(double originX, double originY, double cellSize) => new(originX, cellSize, 0, originY, 0, -cellSize);

    public (double X, double Y) PixelToWorld(double col, double row) =>
        (OriginX + col * PixelWidth + row * RotationX, OriginY + col * RotationY + row * PixelHeight);

    /// <summary>World → fractional pixel (inverse affine).</summary>
    public (double Col, double Row) WorldToPixel(double x, double y)
    {
        double det = PixelWidth * PixelHeight - RotationX * RotationY;
        double dx = x - OriginX, dy = y - OriginY;
        return ((dx * PixelHeight - dy * RotationX) / det, (dy * PixelWidth - dx * RotationY) / det);
    }

    public double[] ToArray() => [OriginX, PixelWidth, RotationX, OriginY, RotationY, PixelHeight];
}

/// <summary>
/// In-memory multi-band raster with float32 band-sequential storage, a geotransform, CRS and nodata value.
/// Operations return new rasters (inputs are never modified).
/// </summary>
public sealed class Raster
{
    public Raster(int width, int height, int bandCount, GeoTransform transform, CoordinateReferenceSystem? crs = null, float? noData = null)
    {
        if (width <= 0 || height <= 0 || bandCount <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; Transform = transform; Crs = crs; NoData = noData;
        Bands = Enumerable.Range(0, bandCount).Select(_ => new float[width * height]).ToArray();
        BandNames = Enumerable.Range(1, bandCount).Select(i => $"band{i}").ToArray();
    }

    public Raster(int width, int height, float[][] bands, GeoTransform transform, CoordinateReferenceSystem? crs = null, float? noData = null)
    {
        if (bands.Any(b => b.Length != width * height)) throw new ArgumentException("Band size mismatch.");
        Width = width; Height = height; Bands = bands; Transform = transform; Crs = crs; NoData = noData;
        BandNames = Enumerable.Range(1, bands.Length).Select(i => $"band{i}").ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int BandCount => Bands.Length;
    public float[][] Bands { get; }
    public string[] BandNames { get; set; }
    public GeoTransform Transform { get; }
    public CoordinateReferenceSystem? Crs { get; }
    public float? NoData { get; }
    public double CellSizeX => Math.Abs(Transform.PixelWidth);
    public double CellSizeY => Math.Abs(Transform.PixelHeight);

    public float[] Band(int index = 0) => Bands[index];

    public float this[int band, int row, int col]
    {
        get => Bands[band][row * Width + col];
        set => Bands[band][row * Width + col] = value;
    }

    public bool IsNoData(float v) => float.IsNaN(v) || (NoData is { } nd && v == nd);

    /// <summary>World envelope of the raster.</summary>
    public Envelope Bounds
    {
        get
        {
            var env = new Envelope();
            foreach (var (c, r) in new[] { (0, 0), (Width, 0), (0, Height), (Width, Height) })
            {
                var (x, y) = Transform.PixelToWorld(c, r);
                env.ExpandToInclude(x, y);
            }
            return env;
        }
    }

    /// <summary>Value at a world location (nearest cell), or NaN outside / nodata.</summary>
    public float Sample(double x, double y, int band = 0)
    {
        var (c, r) = Transform.WorldToPixel(x, y);
        int col = (int)Math.Floor(c), row = (int)Math.Floor(r);
        if (col < 0 || row < 0 || col >= Width || row >= Height) return float.NaN;
        var v = Bands[band][row * Width + col];
        return IsNoData(v) ? float.NaN : v;
    }

    /// <summary>Bilinear sample at a world location.</summary>
    public float SampleBilinear(double x, double y, int band = 0)
    {
        var (c, r) = Transform.WorldToPixel(x, y);
        c -= 0.5; r -= 0.5;
        int c0 = (int)Math.Floor(c), r0 = (int)Math.Floor(r);
        if (c0 < 0 || r0 < 0 || c0 + 1 >= Width || r0 + 1 >= Height) return Sample(x, y, band);
        double fx = c - c0, fy = r - r0;
        var b = Bands[band];
        float v00 = b[r0 * Width + c0], v10 = b[r0 * Width + c0 + 1], v01 = b[(r0 + 1) * Width + c0], v11 = b[(r0 + 1) * Width + c0 + 1];
        if (IsNoData(v00) || IsNoData(v10) || IsNoData(v01) || IsNoData(v11)) return Sample(x, y, band);
        return (float)(v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) + v01 * (1 - fx) * fy + v11 * fx * fy);
    }

    /// <summary>Statistics over valid cells of a band.</summary>
    public (double Min, double Max, double Mean, double Std, long Count) Statistics(int band = 0)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0, sq = 0; long n = 0;
        foreach (var v in Bands[band])
        {
            if (IsNoData(v)) continue;
            min = Math.Min(min, v); max = Math.Max(max, v); sum += v; sq += (double)v * v; n++;
        }
        if (n == 0) return (double.NaN, double.NaN, double.NaN, double.NaN, 0);
        double mean = sum / n;
        return (min, max, mean, Math.Sqrt(Math.Max(0, sq / n - mean * mean)), n);
    }

    /// <summary>Same georeferencing, new single band.</summary>
    public Raster WithBand(float[] data, float? noData = null) => new(Width, Height, [data], Transform, Crs, noData ?? NoData);

    /// <summary>Cell-wise map over one band (nodata preserved as NaN).</summary>
    public Raster Map(Func<float, float> f, int band = 0)
    {
        var src = Bands[band];
        var dst = new float[src.Length];
        Parallel.For(0, Height, r =>
        {
            int o = r * Width;
            for (int c = 0; c < Width; c++) { var v = src[o + c]; dst[o + c] = IsNoData(v) ? float.NaN : f(v); }
        });
        return WithBand(dst, float.NaN);
    }

    /// <summary>Cell-wise combination of two aligned rasters (band 0 of each).</summary>
    public static Raster Combine(Raster a, Raster b, Func<float, float, float> f)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("Rasters must be aligned (same size).");
        var dst = new float[a.Width * a.Height];
        var ba = a.Bands[0]; var bb = b.Bands[0];
        Parallel.For(0, a.Height, r =>
        {
            int o = r * a.Width;
            for (int c = 0; c < a.Width; c++)
            {
                float x = ba[o + c], y = bb[o + c];
                dst[o + c] = a.IsNoData(x) || b.IsNoData(y) ? float.NaN : f(x, y);
            }
        });
        return a.WithBand(dst, float.NaN);
    }

    /// <summary>Colourises a band to RGBA (NaN/nodata transparent). Range defaults to the band min/max.</summary>
    public byte[] ToRgba(ColorMap colorMap, int band = 0, double? min = null, double? max = null)
    {
        var st = Statistics(band);
        double lo = min ?? st.Min, hi = max ?? st.Max;
        double range = hi - lo == 0 ? 1 : hi - lo;
        var src = Bands[band];
        var rgba = new byte[Width * Height * 4];
        Parallel.For(0, Height, r =>
        {
            for (int c = 0; c < Width; c++)
            {
                int i = r * Width + c;
                var v = src[i];
                if (IsNoData(v)) continue;
                var col = colorMap.Sample((v - lo) / range);
                rgba[i * 4] = col.R; rgba[i * 4 + 1] = col.G; rgba[i * 4 + 2] = col.B; rgba[i * 4 + 3] = 255;
            }
        });
        return rgba;
    }

    /// <summary>Greyscale RGBA for a hillshade-like band (0–255).</summary>
    public byte[] ToGrayRgba(int band = 0, byte alpha = 255)
    {
        var src = Bands[band];
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < src.Length; i++)
        {
            if (IsNoData(src[i])) continue;
            byte g = (byte)Math.Clamp(src[i], 0, 255);
            rgba[i * 4] = g; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = g; rgba[i * 4 + 3] = alpha;
        }
        return rgba;
    }

    public override string ToString() => $"Raster {Width}×{Height}×{BandCount}, cell {CellSizeX:G4}, crs={Crs?.ToString() ?? "None"}";
}

/// <summary>Map helpers for rasters.</summary>
public static class RasterPlotting
{
    /// <summary>Adds a colourised raster band to a map with a gradient legend.</summary>
    public static MapScene AddRaster(this MapScene scene, Raster raster, ColorMap? colorMap = null, int band = 0, double opacity = 1, string? legendTitle = null, double? min = null, double? max = null)
    {
        var cm = colorMap ?? ColorMaps.Viridis;
        var st = raster.Statistics(band);
        double lo = min ?? st.Min, hi = max ?? st.Max;
        LegendEntry[]? legend = legendTitle is null ? null : Enumerable.Range(0, 11).Select(k =>
            new LegendEntry(k == 0 ? LegendFormat(lo) : k == 10 ? LegendFormat(hi) : "", cm.Sample(k / 10.0), LegendSymbol.Gradient)).ToArray();
        scene.Geographic |= raster.Crs?.IsGeographic ?? false;
        return scene.AddImage(raster.ToRgba(cm, band, lo, hi), raster.Width, raster.Height, raster.Bounds, opacity, legend, legendTitle ?? raster.BandNames[band]);
    }

    private static string LegendFormat(double v) => Math.Abs(v) >= 100 ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Adds a hillshade (greyscale) underlay.</summary>
    public static MapScene AddHillshade(this MapScene scene, Raster hillshade, double opacity = 0.45) =>
        scene.AddImage(hillshade.ToGrayRgba(), hillshade.Width, hillshade.Height, hillshade.Bounds, opacity, null, "hillshade");
}
