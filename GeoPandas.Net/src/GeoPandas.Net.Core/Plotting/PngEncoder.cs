using System.Buffers.Binary;
using System.IO.Compression;

namespace GeoPandas.Net.Plotting;

/// <summary>Minimal dependency-free PNG encoder (RGBA8, zlib) used for raster layers and thumbnails.</summary>
public static class PngEncoder
{
    private static readonly uint[] s_crcTable = BuildCrc();

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("Buffer too small for RGBA image.");
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0); // filter: none
                    z.Write(rgba.Slice(y * stride, stride));
                }
            }
            WriteChunk(ms, "IDAT", raw.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        Span<byte> t = stackalloc byte[4];
        for (int i = 0; i < 4; i++) t[i] = (byte)type[i];
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in t) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFF);
        s.Write(len);
    }

    private static uint[] BuildCrc()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
