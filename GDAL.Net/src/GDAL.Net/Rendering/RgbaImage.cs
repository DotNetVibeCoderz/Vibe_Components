using GDAL.Net.Vsi;

namespace GDAL.Net.Rendering;

/// <summary>An 8-bit RGBA image (straight alpha, row-major) — the common output of the renderers.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length < width * height * 4) throw new ArgumentException("Pixel buffer is too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA bytes, <c>Width * Height * 4</c>.</summary>
    public byte[] Pixels { get; }

    /// <summary>Row stride in bytes.</summary>
    public int Stride => Width * 4;

    public Rgba this[int x, int y]
    {
        get
        {
            var o = (y * Width + x) * 4;
            return new Rgba(Pixels[o], Pixels[o + 1], Pixels[o + 2], Pixels[o + 3]);
        }
        set
        {
            var o = (y * Width + x) * 4;
            Pixels[o] = value.R;
            Pixels[o + 1] = value.G;
            Pixels[o + 2] = value.B;
            Pixels[o + 3] = value.A;
        }
    }

    public void Clear(Rgba color)
    {
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = color.R;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.B;
            Pixels[i + 3] = color.A;
        }
    }

    /// <summary>Converts to premultiplied BGRA (the native layout of Avalonia/Skia/WPF bitmaps).</summary>
    public void CopyToBgraPremultiplied(Span<byte> destination, int destinationStride)
    {
        for (var y = 0; y < Height; y++)
        {
            var src = Pixels.AsSpan(y * Stride, Stride);
            var dst = destination.Slice(y * destinationStride, Stride);
            for (var x = 0; x < Stride; x += 4)
            {
                var a = src[x + 3];
                if (a == 255)
                {
                    dst[x] = src[x + 2];
                    dst[x + 1] = src[x + 1];
                    dst[x + 2] = src[x];
                    dst[x + 3] = 255;
                }
                else
                {
                    dst[x] = (byte)(src[x + 2] * a / 255);
                    dst[x + 1] = (byte)(src[x + 1] * a / 255);
                    dst[x + 2] = (byte)(src[x] * a / 255);
                    dst[x + 3] = a;
                }
            }
        }
    }

    /// <summary>Wraps the pixels as a 4-band in-memory GDAL dataset (no georeferencing).</summary>
    public Dataset ToDataset()
    {
        var ds = Gdal.CreateMemoryRaster(Width, Height, 4, DataType.Byte);
        ds.ReadInterleavedWrite(Pixels, Width, Height);
        var interps = new[] { ColorInterpretation.Red, ColorInterpretation.Green, ColorInterpretation.Blue, ColorInterpretation.Alpha };
        for (var i = 0; i < 4; i++) ds.GetBand(i + 1).ColorInterpretation = interps[i];
        return ds;
    }

    /// <summary>Encodes to PNG (via GDAL's PNG driver, in memory).</summary>
    public byte[] EncodePng(int compressionLevel = 6) => Encode("PNG", ".png", new Dictionary<string, string> { ["ZLEVEL"] = compressionLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>Encodes to JPEG (alpha is dropped).</summary>
    public byte[] EncodeJpeg(int quality = 85)
    {
        using var rgba = ToDataset();
        using var rgb = GdalUtils.Translate(rgba, string.Empty, ["-of", "MEM", "-b", "1", "-b", "2", "-b", "3"]);
        return EncodeDataset(rgb, "JPEG", ".jpg", new Dictionary<string, string> { ["QUALITY"] = quality.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    /// <summary>Encodes to WebP.</summary>
    public byte[] EncodeWebp(int quality = 85) => Encode("WEBP", ".webp", new Dictionary<string, string> { ["QUALITY"] = quality.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private byte[] Encode(string driver, string ext, IDictionary<string, string>? options)
    {
        using var ds = ToDataset();
        return EncodeDataset(ds, driver, ext, options);
    }

    internal static byte[] EncodeDataset(Dataset ds, string driver, string ext, IDictionary<string, string>? options)
    {
        var path = VirtualFileSystem.NewMemoryPath(ext);
        try
        {
            using (Gdal.GetDriver(driver).CreateCopy(path, ds, options)) { }
            return VirtualFileSystem.ReadMemoryFile(path);
        }
        finally
        {
            VirtualFileSystem.Delete(path);
            VirtualFileSystem.Delete(path + ".aux.xml");
        }
    }

    /// <summary>"data:image/png;base64,..." for direct use in HTML/Blazor.</summary>
    public string ToPngDataUrl() => "data:image/png;base64," + Convert.ToBase64String(EncodePng(3));

    public void SavePng(string path) => File.WriteAllBytes(path, EncodePng());

    /// <summary>Decodes any GDAL-readable image (PNG, JPEG, GeoTIFF, WebP...) into RGBA.</summary>
    public static RgbaImage Load(string path)
    {
        using var ds = Gdal.OpenRaster(path);
        return RasterRenderer.Render(ds, new RenderOptions { Stretch = StretchMode.None }, ds.RasterWidth, ds.RasterHeight);
    }
}

internal static class DatasetInterleaveExtensions
{
    /// <summary>Writes pixel-interleaved RGBA bytes into bands 1..4.</summary>
    public static unsafe void ReadInterleavedWrite(this Dataset ds, byte[] rgba, int width, int height)
    {
        Span<int> bands = [1, 2, 3, 4];
        var extra = RasterBand.MakeExtraArg(ResampleAlgorithm.NearestNeighbour);
        fixed (byte* p = rgba)
        fixed (int* b = bands)
        {
            var err = Native.GdalNative.GDALDatasetRasterIOEx(ds.Handle, 1, 0, 0, width, height, p, width, height, (int)DataType.Byte, 4, b, 4, 4L * width, 1, &extra);
            Native.GdalNative.Check(err, "Dataset RasterIO (write)");
        }
    }
}
