using System.Buffers.Binary;
using System.IO.Compression;

namespace Electrical.Visualization;

/// <summary>Minimal, dependency-free RGBA8 PNG encoder (zlib via <see cref="ZLibStream"/>), deterministic output.</summary>
public static class PngEncoder
{
    private static readonly uint[] Crc = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>CRC-32 (ISO-HDLC, as used by PNG chunks).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint seed = 0)
    {
        uint c = seed ^ 0xFFFFFFFF;
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    /// <summary>Encodes RGBA8 pixels (row-major, width×height×4 bytes) as PNG (color type 6, filter "Sub").</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgba.Length != width * height * 4) throw new ArgumentException("Buffer must be width*height*4 bytes.", nameof(rgba));
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR"u8, ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 4;
                var line = new byte[stride + 1];
                for (int y = 0; y < height; y++)
                {
                    var row = rgba.Slice(y * stride, stride);
                    line[0] = 1; // Sub filter: good for smooth rasters
                    for (int i = 0; i < stride; i++) line[i + 1] = (byte)(row[i] - (i >= 4 ? row[i - 4] : 0));
                    z.Write(line);
                }
            }
            Chunk(ms, "IDAT"u8, raw.ToArray());
        }
        Chunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len); s.Write(type); s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(data, Crc32(type)));
        s.Write(len);
    }

    /// <summary>Converts an interleaved RGB buffer to PNG.</summary>
    public static byte[] FromRgb(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var buf = new byte[width * height * 4];
        for (int i = 0, j = 0; i + 2 < rgb.Length && j < buf.Length; i += 3, j += 4)
        {
            buf[j] = rgb[i]; buf[j + 1] = rgb[i + 1]; buf[j + 2] = rgb[i + 2]; buf[j + 3] = 255;
        }
        return Encode(buf, width, height);
    }

    /// <summary><c>data:image/png;base64,…</c> URI for embedding.</summary>
    public static string ToDataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
}

/// <summary>Simple scalar raster (row-major, row 0 at the top) for heatmaps and contour-free field plots.</summary>
public sealed class Raster
{
    /// <summary>Creates a raster of the given size filled with zeros.</summary>
    public Raster(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; Data = new double[width * height];
    }

    /// <summary>Creates a raster from a matrix (<c>values[row, col]</c>).</summary>
    public static Raster FromMatrix(double[,] values)
    {
        var r = new Raster(values.GetLength(1), values.GetLength(0));
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++) r[x, y] = values[y, x];
        return r;
    }

    public int Width { get; }
    public int Height { get; }
    public double[] Data { get; }

    public double this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    /// <summary>Fills every cell from a function of (x, y).</summary>
    public Raster Fill(Func<int, int, double> f)
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++) this[x, y] = f(x, y);
        return this;
    }

    /// <summary>Minimum and maximum of the finite values.</summary>
    public (double Min, double Max) Range()
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in Data) if (double.IsFinite(v)) { min = Math.Min(min, v); max = Math.Max(max, v); }
        return min > max ? (0, 1) : (min, max);
    }

    /// <summary>
    /// Maps values to RGBA8 through <paramref name="ramp"/> with nearest-neighbour upscaling by <paramref name="scale"/>.
    /// Values are normalised to the ramp domain using [min, max] (defaults to the data range). NaN cells are transparent.
    /// </summary>
    public byte[] ToRgba(ColorRamp ramp, int scale = 1, double? min = null, double? max = null)
    {
        if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale));
        var (dmin, dmax) = Range();
        double lo = min ?? dmin, hi = max ?? dmax;
        if (hi - lo < 1e-12) hi = lo + 1;
        int w = Width * scale, h = Height * scale;
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = this[x / scale, y / scale];
                int i = (y * w + x) * 4;
                if (double.IsNaN(v)) continue;
                var c = ramp.Map(ramp.Min + (v - lo) / (hi - lo) * (ramp.Max - ramp.Min));
                buf[i] = c.R; buf[i + 1] = c.G; buf[i + 2] = c.B; buf[i + 3] = c.A;
            }
        return buf;
    }

    /// <summary>Renders the raster through a ramp to PNG bytes.</summary>
    public byte[] ToPng(ColorRamp ramp, int scale = 1, double? min = null, double? max = null) =>
        PngEncoder.Encode(ToRgba(ramp, scale, min, max), Width * scale, Height * scale);
}
