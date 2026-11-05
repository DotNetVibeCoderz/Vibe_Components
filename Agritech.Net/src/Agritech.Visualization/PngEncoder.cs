using System.Buffers.Binary;
using System.IO.Compression;

namespace Agritech.Visualization;

/// <summary>Minimal, dependency-free RGBA PNG encoder (zlib via System.IO.Compression).</summary>
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

    /// <summary>Encodes RGBA8 pixels (row-major, width×height×4 bytes).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("Buffer must be width*height*4 bytes.");
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
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(rgba.Slice(y * stride, stride));
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
        uint c = 0xFFFFFFFF;
        foreach (var b in type) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(len, c ^ 0xFFFFFFFF);
        s.Write(len);
    }

    /// <summary>Renders a float raster through a color map to PNG, with optional integer upscaling (nearest neighbour).</summary>
    public static byte[] FromRaster(ReadOnlySpan<float> values, int width, int height, ColorMap map, int scale = 1)
    {
        int w = width * scale, h = height * scale;
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = map.Map(values[(y / scale) * width + x / scale]);
                int i = (y * w + x) * 4;
                buf[i] = c.R; buf[i + 1] = c.G; buf[i + 2] = c.B; buf[i + 3] = c.A;
            }
        return Encode(buf, w, h);
    }

    /// <summary>Converts an interleaved RGB buffer to PNG.</summary>
    public static byte[] FromRgb(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var buf = new byte[width * height * 4];
        for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4) { buf[j] = rgb[i]; buf[j + 1] = rgb[i + 1]; buf[j + 2] = rgb[i + 2]; buf[j + 3] = 255; }
        return Encode(buf, width, height);
    }

    public static string ToDataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
}
