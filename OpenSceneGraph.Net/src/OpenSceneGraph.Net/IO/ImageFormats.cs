using System.Buffers.Binary;
using System.IO.Compression;
using OpenSceneGraph.Net.State;

namespace OpenSceneGraph.Net.IO;

/// <summary>
/// Dependency-free PNG decoder (8-bit greyscale, grey+alpha, RGB, RGBA and palette; non-interlaced) and an
/// RGBA PNG encoder used for screenshots.
/// </summary>
public sealed class PngReader : IImageReader
{
    public IReadOnlyList<string> Extensions { get; } = ["png"];

    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public Image ReadImage(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray().AsSpan();
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
            var chunk = data.Slice(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    bitDepth = chunk[8]; colorType = chunk[9]; interlace = chunk[12];
                    break;
                case "PLTE": palette = chunk.ToArray(); break;
                case "tRNS": trns = chunk.ToArray(); break;
                case "IDAT": idat.Write(chunk); break;
            }
            if (type == "IEND") break;
            pos += 12 + len;
        }
        if (bitDepth != 8) throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported (8-bit only).");
        if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not supported.");

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Bad PNG color type {colorType}.") };
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);

        var cur = new byte[stride];
        var prev = new byte[stride];
        var img = new Image(width, height);
        var px = img.Pixels;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(cur);
            Unfilter(filter, cur, prev, channels);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                int i = x * channels;
                switch (colorType)
                {
                    case 0: px[o] = px[o + 1] = px[o + 2] = cur[i]; px[o + 3] = 255; break;
                    case 4: px[o] = px[o + 1] = px[o + 2] = cur[i]; px[o + 3] = cur[i + 1]; break;
                    case 2: px[o] = cur[i]; px[o + 1] = cur[i + 1]; px[o + 2] = cur[i + 2]; px[o + 3] = 255; break;
                    case 6: px[o] = cur[i]; px[o + 1] = cur[i + 1]; px[o + 2] = cur[i + 2]; px[o + 3] = cur[i + 3]; break;
                    case 3:
                        int p = cur[i];
                        px[o] = palette![p * 3]; px[o + 1] = palette[p * 3 + 1]; px[o + 2] = palette[p * 3 + 2];
                        px[o + 3] = trns is not null && p < trns.Length ? trns[p] : (byte)255;
                        break;
                }
            }
            (cur, prev) = (prev, cur);
        }
        return img;
    }

    private static void Unfilter(int filter, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
    {
        for (int i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            cur[i] = filter switch
            {
                0 => cur[i],
                1 => (byte)(cur[i] + a),
                2 => (byte)(cur[i] + b),
                3 => (byte)(cur[i] + ((a + b) >> 1)),
                4 => (byte)(cur[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException($"Bad PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = System.Math.Abs(p - a), pb = System.Math.Abs(p - b), pc = System.Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Encodes RGBA8 pixels (top row first) as PNG.</summary>
    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> rgba)
    {
        output.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgba.Slice(y * stride, stride));
            }
        }
        WriteChunk(output, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
        WriteChunk(output, "IEND", []);
    }

    public static void Write(string path, Image image)
    {
        using var fs = File.Create(path);
        Write(fs, image.Width, image.Height, image.Pixels);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(hdr, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, hdr[4..]);
        s.Write(hdr);
        s.Write(data);
        uint crc = Crc32(hdr[4..], 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] s_crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
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

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}

/// <summary>Uncompressed TGA (24/32-bit), BMP (24/32-bit) and binary PPM/PGM readers.</summary>
public sealed class SimpleImageReaders : IImageReader
{
    public IReadOnlyList<string> Extensions { get; } = ["tga", "bmp", "ppm", "pgm"];

    public Image ReadImage(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var d = ms.ToArray();
        if (d.Length > 2 && d[0] == 'B' && d[1] == 'M') return ReadBmp(d);
        if (d.Length > 2 && d[0] == 'P' && (d[1] == '6' || d[1] == '5')) return ReadPnm(d);
        return ReadTga(d);
    }

    private static Image ReadTga(byte[] d)
    {
        int idLen = d[0], imageType = d[2];
        if (imageType != 2 && imageType != 3) throw new NotSupportedException("Only uncompressed TGA is supported.");
        int w = BitConverter.ToUInt16(d, 12), h = BitConverter.ToUInt16(d, 14), bpp = d[16] / 8;
        bool topDown = (d[17] & 0x20) != 0;
        int off = 18 + idLen;
        var img = new Image(w, h);
        for (int y = 0; y < h; y++)
        {
            int dy = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int s = off + (y * w + x) * bpp, o = (dy * w + x) * 4;
                if (bpp == 1) { img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = d[s]; img.Pixels[o + 3] = 255; continue; }
                img.Pixels[o] = d[s + 2]; img.Pixels[o + 1] = d[s + 1]; img.Pixels[o + 2] = d[s];
                img.Pixels[o + 3] = bpp == 4 ? d[s + 3] : (byte)255;
            }
        }
        return img;
    }

    private static Image ReadBmp(byte[] d)
    {
        int off = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), hRaw = BitConverter.ToInt32(d, 22);
        int bpp = BitConverter.ToUInt16(d, 28) / 8, comp = BitConverter.ToInt32(d, 30);
        if (bpp < 3 || comp is not (0 or 3)) throw new NotSupportedException("Only 24/32-bit uncompressed BMP is supported.");
        bool topDown = hRaw < 0;
        int h = System.Math.Abs(hRaw);
        int stride = (w * bpp + 3) & ~3;
        var img = new Image(w, h);
        for (int y = 0; y < h; y++)
        {
            int dy = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int s = off + y * stride + x * bpp, o = (dy * w + x) * 4;
                img.Pixels[o] = d[s + 2]; img.Pixels[o + 1] = d[s + 1]; img.Pixels[o + 2] = d[s];
                img.Pixels[o + 3] = bpp == 4 ? d[s + 3] : (byte)255;
            }
        }
        return img;
    }

    private static Image ReadPnm(byte[] d)
    {
        bool color = d[1] == '6';
        int pos = 2;
        int Next()
        {
            while (pos < d.Length && (char.IsWhiteSpace((char)d[pos]) || d[pos] == '#'))
            {
                if (d[pos] == '#') while (pos < d.Length && d[pos] != '\n') pos++;
                else pos++;
            }
            int v = 0;
            while (pos < d.Length && char.IsDigit((char)d[pos])) v = v * 10 + (d[pos++] - '0');
            return v;
        }
        int w = Next(), h = Next(), max = Next();
        pos++;
        if (max > 255) throw new NotSupportedException("16-bit PNM is not supported.");
        var img = new Image(w, h);
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (color) { img.Pixels[o] = d[pos++]; img.Pixels[o + 1] = d[pos++]; img.Pixels[o + 2] = d[pos++]; }
            else img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = d[pos++];
            img.Pixels[o + 3] = 255;
        }
        return img;
    }
}
