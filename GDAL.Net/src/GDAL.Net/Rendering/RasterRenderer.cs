using System.Buffers;
using System.Runtime.CompilerServices;
using GDAL.Net.Processing;

namespace GDAL.Net.Rendering;

/// <summary>How raster values are mapped to the 0..255 display range.</summary>
public enum StretchMode
{
    /// <summary>Values are used as-is (expects Byte data).</summary>
    None,
    MinMax,
    /// <summary>mean ± k·stddev.</summary>
    StdDev,
    /// <summary>Clip at low/high percentiles (default 2–98 %).</summary>
    Percentile,
    /// <summary>Explicit <see cref="RenderOptions.Min"/> / <see cref="RenderOptions.Max"/>.</summary>
    Custom,
}

public enum RenderMode
{
    /// <summary>RGB for 3+ band imagery, palette for color-table bands, single band otherwise.</summary>
    Auto,
    SingleBand,
    Rgb,
    Palette,
}

/// <summary>Display options for <see cref="RasterRenderer"/>.</summary>
public sealed record RenderOptions
{
    public RenderMode Mode { get; init; } = RenderMode.Auto;
    /// <summary>1-based band(s) to display: one for single band/palette, three for RGB.</summary>
    public IReadOnlyList<int>? Bands { get; init; }
    public ColorRamp Ramp { get; init; } = ColorRamp.Viridis;
    public StretchMode Stretch { get; init; } = StretchMode.Percentile;
    public double StdDevFactor { get; init; } = 2;
    public double PercentileLow { get; init; } = 2;
    public double PercentileHigh { get; init; } = 98;
    /// <summary>Custom stretch range (single band), or per-channel when <see cref="Mins"/>/<see cref="Maxs"/> are set.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }
    public IReadOnlyList<double>? Mins { get; init; }
    public IReadOnlyList<double>? Maxs { get; init; }
    public ResampleAlgorithm Resampling { get; init; } = ResampleAlgorithm.Bilinear;
    /// <summary>Shade a single band (DEM) with a hillshade computed at display resolution.</summary>
    public bool Hillshade { get; init; }
    public double HillshadeStrength { get; init; } = 0.6;
    public double HillshadeAzimuth { get; init; } = 315;
    public double HillshadeAltitude { get; init; } = 45;
    public double ZFactor { get; init; } = 1;
    /// <summary>Background color for areas outside the raster (transparent when null).</summary>
    public Rgba? Background { get; init; }
    /// <summary>Global opacity 0..1.</summary>
    public double Opacity { get; init; } = 1;
}

/// <summary>Resolved stretch range of a band.</summary>
public readonly record struct StretchRange(double Min, double Max);

/// <summary>
/// Renders rasters to RGBA for display: reads only the visible window at display resolution (GDAL picks overviews
/// automatically), stretches values, applies color ramps / palettes / RGB composites and optional hillshading.
/// Used by both the Blazor and Avalonia components.
/// </summary>
public static class RasterRenderer
{
    private static readonly ConditionalWeakTable<Dataset, Dictionary<string, StretchRange>> s_stretchCache = new();

    /// <summary>Renders the whole raster into an image of the given size.</summary>
    public static RgbaImage Render(Dataset dataset, RenderOptions options, int width, int height)
        => Render(dataset, options, FullView(dataset), width, height);

    /// <summary>The world-coordinate extent of a raster (pixel space when it has no georeferencing).</summary>
    public static Envelope FullView(Dataset dataset)
    {
        var gt = dataset.GeoTransform;
        return gt.GetExtent(dataset.RasterWidth, dataset.RasterHeight);
    }

    /// <summary>Resolves the effective render mode for a dataset.</summary>
    public static RenderMode ResolveMode(Dataset dataset, RenderOptions options)
    {
        if (options.Mode != RenderMode.Auto) return options.Mode;
        if (options.Bands is { Count: >= 3 }) return RenderMode.Rgb;
        var first = dataset.GetBand(options.Bands is { Count: > 0 } b ? b[0] : 1);
        if (options.Bands is not { Count: 1 } && dataset.RasterCount >= 3 &&
            (first.ColorInterpretation == ColorInterpretation.Red || first.DataType == DataType.Byte))
        {
            return RenderMode.Rgb;
        }
        if (first.ColorInterpretation == ColorInterpretation.Palette || first.ColorTable is not null) return RenderMode.Palette;
        return RenderMode.SingleBand;
    }

    /// <summary>Renders the part of the raster inside <paramref name="view"/> (world coordinates) into width × height pixels.</summary>
    public static RgbaImage Render(Dataset dataset, RenderOptions options, Envelope view, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var image = new RgbaImage(width, height);
        if (options.Background is { } bg) image.Clear(bg);
        if (dataset.RasterCount == 0 || view.IsEmpty || view.Width <= 0 || view.Height <= 0) return image;

        var gt = dataset.GeoTransform;
        if (!gt.IsNorthUp) throw new NotSupportedException("Rotated rasters must be warped (GdalUtils.Warp) before rendering.");

        // Fractional source window covering the view.
        var topY = gt.PixelHeight < 0 ? view.MaxY : view.MinY;
        var bottomY = gt.PixelHeight < 0 ? view.MinY : view.MaxY;
        var px0 = (view.MinX - gt.OriginX) / gt.PixelWidth;
        var px1 = (view.MaxX - gt.OriginX) / gt.PixelWidth;
        var ln0 = (topY - gt.OriginY) / gt.PixelHeight;
        var ln1 = (bottomY - gt.OriginY) / gt.PixelHeight;
        if (px1 < px0) (px0, px1) = (px1, px0);
        if (ln1 < ln0) (ln0, ln1) = (ln1, ln0);

        var fx0 = Math.Max(0, px0);
        var fx1 = Math.Min(dataset.RasterWidth, px1);
        var fy0 = Math.Max(0, ln0);
        var fy1 = Math.Min(dataset.RasterHeight, ln1);
        if (fx1 <= fx0 || fy1 <= fy0) return image;

        // Destination rectangle of the clipped window.
        var dx0 = (int)Math.Round((fx0 - px0) / (px1 - px0) * width);
        var dx1 = (int)Math.Round((fx1 - px0) / (px1 - px0) * width);
        var dy0 = (int)Math.Round((fy0 - ln0) / (ln1 - ln0) * height);
        var dy1 = (int)Math.Round((fy1 - ln0) / (ln1 - ln0) * height);
        var dw = Math.Max(1, dx1 - dx0);
        var dh = Math.Max(1, dy1 - dy0);
        dx0 = Math.Clamp(dx0, 0, width - 1);
        dy0 = Math.Clamp(dy0, 0, height - 1);
        dw = Math.Min(dw, width - dx0);
        dh = Math.Min(dh, height - dy0);

        var window = new SourceWindow(fx0, fy0, fx1 - fx0, fy1 - fy0);
        var tile = new byte[dw * dh * 4];
        var mode = ResolveMode(dataset, options);
        switch (mode)
        {
            case RenderMode.Rgb:
                RenderRgb(dataset, options, window, dw, dh, tile);
                break;
            case RenderMode.Palette:
                RenderPalette(dataset, options, window, dw, dh, tile);
                break;
            default:
                RenderSingle(dataset, options, window, dw, dh, tile, view.Width / width, view.Height / height);
                break;
        }

        if (options.Opacity < 1)
        {
            var o = Math.Clamp(options.Opacity, 0, 1);
            for (var i = 3; i < tile.Length; i += 4) tile[i] = (byte)(tile[i] * o);
        }
        Blit(tile, dw, dh, image, dx0, dy0, options.Background is not null);
        return image;
    }

    private readonly record struct SourceWindow(double X, double Y, double Width, double Height)
    {
        public (int X, int Y, int W, int H) Integer()
        {
            var x = (int)Math.Floor(X);
            var y = (int)Math.Floor(Y);
            return (x, y, Math.Max(1, (int)Math.Ceiling(X + Width) - x), Math.Max(1, (int)Math.Ceiling(Y + Height) - y));
        }
    }

    private static unsafe void ReadWindow(RasterBand band, SourceWindow w, Span<float> buffer, int bw, int bh, ResampleAlgorithm resampling)
    {
        var (x, y, iw, ih) = w.Integer();
        iw = Math.Min(iw, band.Width - x);
        ih = Math.Min(ih, band.Height - y);
        var extra = RasterBand.MakeExtraArg(resampling);
        extra.bFloatingPointWindowValidity = 1;
        extra.dfXOff = w.X;
        extra.dfYOff = w.Y;
        extra.dfXSize = w.Width;
        extra.dfYSize = w.Height;
        fixed (float* p = buffer)
        {
            var err = Native.GdalNative.GDALRasterIOEx(band.Handle, 0, x, y, iw, ih, p, bw, bh, (int)DataType.Float32, 0, 0, &extra);
            Native.GdalNative.Check(err, "RasterIO (render)");
        }
    }

    private static void RenderSingle(Dataset ds, RenderOptions o, SourceWindow w, int bw, int bh, byte[] tile, double cellX, double cellY)
    {
        var band = ds.GetBand(o.Bands is { Count: > 0 } b ? b[0] : 1);
        var n = bw * bh;
        var buffer = ArrayPool<float>.Shared.Rent(n);
        try
        {
            var span = buffer.AsSpan(0, n);
            var resampling = DataTypes.IsInteger(band.DataType) && band.ColorTable is not null ? ResampleAlgorithm.NearestNeighbour : o.Resampling;
            ReadWindow(band, w, span, bw, bh, resampling);
            var nd = band.NoDataValue is { } v ? (float?)(float)v : null;
            var range = ResolveStretch(ds, band, o);
            Kernels.Colorize(span, (float)range.Min, (float)range.Max, o.Ramp.Lut, tile, nd);
            if (o.Hillshade)
            {
                var shade = ArrayPool<byte>.Shared.Rent(n);
                try
                {
                    var srs = ds.SpatialReference;
                    var geographic = srs?.IsGeographic ?? false;
                    srs?.Dispose();
                    var scale = geographic ? 111_120.0 : 1.0;
                    ComputeHillshade(span, bw, bh, Math.Abs(cellX) * scale, Math.Abs(cellY) * scale, o.ZFactor, o.HillshadeAzimuth, o.HillshadeAltitude, shade.AsSpan(0, n), nd);
                    Kernels.BlendShade(tile, shade.AsSpan(0, n), (float)o.HillshadeStrength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(shade);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private static void RenderRgb(Dataset ds, RenderOptions o, SourceWindow w, int bw, int bh, byte[] tile)
    {
        var bands = o.Bands is { Count: >= 3 } b ? b.Take(3).ToArray() : [1, 2, 3];
        var n = bw * bh;
        var pool = ArrayPool<float>.Shared;
        var buf = new float[3][];
        try
        {
            Span<float> mins = stackalloc float[3];
            Span<float> maxs = stackalloc float[3];
            float? nd = null;
            for (var c = 0; c < 3; c++)
            {
                buf[c] = pool.Rent(n);
                var band = ds.GetBand(bands[c]);
                ReadWindow(band, w, buf[c].AsSpan(0, n), bw, bh, o.Resampling);
                nd ??= band.NoDataValue is { } v ? (float)v : null;
                if (o.Mins is { Count: >= 3 } && o.Maxs is { Count: >= 3 })
                {
                    mins[c] = (float)o.Mins[c];
                    maxs[c] = (float)o.Maxs[c];
                }
                else if (o.Stretch == StretchMode.None)
                {
                    mins[c] = 0;
                    maxs[c] = 255;
                }
                else
                {
                    var r = ResolveStretch(ds, band, o);
                    mins[c] = (float)r.Min;
                    maxs[c] = (float)r.Max;
                }
            }
            Kernels.CompositeRgb(buf[0].AsSpan(0, n), buf[1].AsSpan(0, n), buf[2].AsSpan(0, n), mins, maxs, tile, nd);

            // Honor an alpha band when present.
            var alphaIndex = Enumerable.Range(1, ds.RasterCount).FirstOrDefault(i => ds.GetBand(i).ColorInterpretation == ColorInterpretation.Alpha);
            if (alphaIndex > 0)
            {
                var alpha = pool.Rent(n);
                try
                {
                    ReadWindow(ds.GetBand(alphaIndex), w, alpha.AsSpan(0, n), bw, bh, ResampleAlgorithm.NearestNeighbour);
                    for (var i = 0; i < n; i++) tile[i * 4 + 3] = (byte)Math.Clamp(alpha[i], 0, 255);
                }
                finally
                {
                    pool.Return(alpha);
                }
            }
        }
        finally
        {
            foreach (var a in buf) if (a is not null) pool.Return(a);
        }
    }

    private static void RenderPalette(Dataset ds, RenderOptions o, SourceWindow w, int bw, int bh, byte[] tile)
    {
        var band = ds.GetBand(o.Bands is { Count: > 0 } b ? b[0] : 1);
        using var ct = band.ColorTable;
        if (ct is null)
        {
            RenderSingle(ds, o with { Mode = RenderMode.SingleBand }, w, bw, bh, tile, 1, 1);
            return;
        }
        var entries = ct.Entries;
        var n = bw * bh;
        var buffer = ArrayPool<float>.Shared.Rent(n);
        try
        {
            ReadWindow(band, w, buffer.AsSpan(0, n), bw, bh, ResampleAlgorithm.NearestNeighbour);
            var nd = band.NoDataValue;
            for (var i = 0; i < n; i++)
            {
                var idx = (int)buffer[i];
                if ((nd.HasValue && idx == (int)nd.Value) || idx < 0 || idx >= entries.Count)
                {
                    tile[i * 4 + 3] = 0;
                    continue;
                }
                var e = entries[idx];
                tile[i * 4] = (byte)e.C1;
                tile[i * 4 + 1] = (byte)e.C2;
                tile[i * 4 + 2] = (byte)e.C3;
                tile[i * 4 + 3] = (byte)e.C4;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private static void Blit(byte[] tile, int tw, int th, RgbaImage image, int x0, int y0, bool composite)
    {
        for (var y = 0; y < th; y++)
        {
            var src = tile.AsSpan(y * tw * 4, tw * 4);
            var dst = image.Pixels.AsSpan(((y0 + y) * image.Width + x0) * 4, tw * 4);
            if (!composite)
            {
                src.CopyTo(dst);
                continue;
            }
            for (var i = 0; i < src.Length; i += 4)
            {
                var a = src[i + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    src.Slice(i, 4).CopyTo(dst.Slice(i, 4));
                    continue;
                }
                var inv = 255 - a;
                dst[i] = (byte)((src[i] * a + dst[i] * inv) / 255);
                dst[i + 1] = (byte)((src[i + 1] * a + dst[i + 1] * inv) / 255);
                dst[i + 2] = (byte)((src[i + 2] * a + dst[i + 2] * inv) / 255);
                dst[i + 3] = (byte)Math.Max(dst[i + 3], a);
            }
        }
    }

    // ------------------------------------------------------------------ stretch

    /// <summary>Computes the display range of a band for the given options (cached per dataset).</summary>
    public static StretchRange ResolveStretch(Dataset dataset, RasterBand band, RenderOptions options)
    {
        if (options.Stretch == StretchMode.Custom || (options.Min.HasValue && options.Max.HasValue))
        {
            return new StretchRange(options.Min ?? 0, options.Max ?? 255);
        }
        if (options.Stretch == StretchMode.None) return new StretchRange(0, 255);

        var key = $"{band.Number}|{options.Stretch}|{options.StdDevFactor}|{options.PercentileLow}|{options.PercentileHigh}";
        var cache = s_stretchCache.GetOrCreateValue(dataset);
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        var sample = SampleBand(band, 1024);
        var nd = band.NoDataValue is { } v ? (float?)(float)v : null;
        var stats = Kernels.Statistics(sample, nd);
        StretchRange range;
        if (stats.ValidCount == 0)
        {
            range = new StretchRange(0, 1);
        }
        else
        {
            switch (options.Stretch)
            {
                case StretchMode.MinMax:
                    range = new StretchRange(stats.Minimum, stats.Maximum);
                    break;
                case StretchMode.StdDev:
                    range = new StretchRange(Math.Max(stats.Minimum, stats.Mean - options.StdDevFactor * stats.StdDev),
                                             Math.Min(stats.Maximum, stats.Mean + options.StdDevFactor * stats.StdDev));
                    break;
                default:
                    const int buckets = 1024;
                    var hist = Kernels.Histogram(sample, stats.Minimum, stats.Maximum, buckets, nd);
                    range = new StretchRange(Percentile(hist, stats.Minimum, stats.Maximum, options.PercentileLow / 100),
                                             Percentile(hist, stats.Minimum, stats.Maximum, options.PercentileHigh / 100));
                    break;
            }
        }
        if (range.Max <= range.Min) range = new StretchRange(range.Min, range.Min + 1);
        lock (cache)
        {
            cache[key] = range;
        }
        return range;
    }

    /// <summary>Clears cached stretch ranges of a dataset (after its pixels changed).</summary>
    public static void InvalidateStretch(Dataset dataset) => s_stretchCache.Remove(dataset);

    private static float[] SampleBand(RasterBand band, int maxSize)
    {
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(band.Width, band.Height));
        var w = Math.Max(1, (int)(band.Width * scale));
        var h = Math.Max(1, (int)(band.Height * scale));
        return band.ReadResampled<float>(w, h, ResampleAlgorithm.NearestNeighbour);
    }

    private static double Percentile(ulong[] hist, double min, double max, double q)
    {
        ulong total = 0;
        foreach (var c in hist) total += c;
        if (total == 0) return min;
        var target = (ulong)(total * Math.Clamp(q, 0, 1));
        ulong acc = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= target) return min + (max - min) * (i + 0.5) / hist.Length;
        }
        return max;
    }

    // ------------------------------------------------------------------ hillshade (Horn)

    private static double Cell(ReadOnlySpan<float> z, int width, int x, int y, float fallback, float? noData)
    {
        var v = z[y * width + x];
        return float.IsNaN(v) || (noData.HasValue && v == noData.Value) ? fallback : v;
    }

    /// <summary>Horn hillshade of an elevation grid into 0..255 intensities.</summary>
    public static void ComputeHillshade(ReadOnlySpan<float> z, int width, int height, double cellX, double cellY, double zFactor,
        double azimuthDeg, double altitudeDeg, Span<byte> output, float? noData = null)
    {
        var zenith = (90 - altitudeDeg) * Math.PI / 180;
        var azimuth = (360 - azimuthDeg + 90) % 360 * Math.PI / 180;
        var cosZ = Math.Cos(zenith);
        var sinZ = Math.Sin(zenith);
        var kx = zFactor / (8 * Math.Max(cellX, 1e-12));
        var ky = zFactor / (8 * Math.Max(cellY, 1e-12));
        for (var y = 0; y < height; y++)
        {
            var y0 = Math.Max(0, y - 1);
            var y2 = Math.Min(height - 1, y + 1);
            for (var x = 0; x < width; x++)
            {
                var c = z[y * width + x];
                if (float.IsNaN(c) || (noData.HasValue && c == noData.Value))
                {
                    output[y * width + x] = 255;
                    continue;
                }
                var x0 = Math.Max(0, x - 1);
                var x2 = Math.Min(width - 1, x + 1);
                var a = Cell(z, width, x0, y0, c, noData); var b = Cell(z, width, x, y0, c, noData); var cc = Cell(z, width, x2, y0, c, noData);
                var d = Cell(z, width, x0, y, c, noData); var f = Cell(z, width, x2, y, c, noData);
                var g = Cell(z, width, x0, y2, c, noData); var h = Cell(z, width, x, y2, c, noData); var i = Cell(z, width, x2, y2, c, noData);
                var dzdx = ((cc + 2 * f + i) - (a + 2 * d + g)) * kx;
                var dzdy = ((g + 2 * h + i) - (a + 2 * b + cc)) * ky;
                var slope = Math.Atan(Math.Sqrt(dzdx * dzdx + dzdy * dzdy));
                var aspect = Math.Atan2(dzdy, -dzdx);
                var shade = cosZ * Math.Cos(slope) + sinZ * Math.Sin(slope) * Math.Cos(azimuth - aspect);
                output[y * width + x] = (byte)Math.Clamp(shade * 255, 0, 255);
            }
        }
    }
}
