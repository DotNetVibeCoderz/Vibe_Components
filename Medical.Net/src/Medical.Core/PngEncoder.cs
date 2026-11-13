using System.Buffers.Binary;
using System.IO.Compression;

namespace Medical.Core;

/// <summary>Dependency-free PNG encoder (grayscale-8 and RGBA-8), AOT-friendly. Used for DICOM previews and charts.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrc();

    /// <summary>Encodes 8-bit grayscale pixels (row-major, <paramref name="width"/> × <paramref name="height"/>).</summary>
    public static byte[] EncodeGray8(ReadOnlySpan<byte> pixels, int width, int height) => Encode(pixels, width, height, 1, 0);

    /// <summary>Encodes RGBA pixels (4 bytes per pixel).</summary>
    public static byte[] EncodeRgba(ReadOnlySpan<byte> pixels, int width, int height) => Encode(pixels, width, height, 4, 6);

    private static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, byte colorType)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int stride = width * channels;
        if (pixels.Length < stride * height) throw new ArgumentException("Pixel buffer too small.", nameof(pixels));

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(output, "IHDR"u8, ihdr);

        using (var raw = new MemoryStream(height * (stride + 1) / 2))
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[stride + 1];
                var prev = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    var cur = pixels.Slice(y * stride, stride);
                    // "Up" filter (type 2) compresses medical images and charts well and is cheap.
                    row[0] = 2;
                    for (int i = 0; i < stride; i++) row[i + 1] = (byte)(cur[i] - prev[i]);
                    z.Write(row);
                    cur.CopyTo(prev);
                }
            }
            Chunk(output, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

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
}
