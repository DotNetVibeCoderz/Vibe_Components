using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using GeoPandas.Net.Geometry.Crs;

namespace GeoPandas.Net.Raster;

public enum TiffCompression { None = 1, Lzw = 5, Deflate = 8 }

/// <summary>
/// Pure managed GeoTIFF / BigTIFF reader and GeoTIFF writer. Reads strips or tiles (Cloud-Optimized GeoTIFFs),
/// uncompressed / Deflate / LZW / PackBits, horizontal and floating-point predictors, chunky or planar layout, 8–64-bit integer and
/// float samples. Georeferencing from ModelPixelScale + ModelTiepoint or ModelTransformation, CRS from GeoKeys.
/// </summary>
public static class GeoTiff
{
    private const ushort ImageWidth = 256, ImageLength = 257, BitsPerSample = 258, Compression = 259, Photometric = 262,
        StripOffsets = 273, SamplesPerPixel = 277, RowsPerStrip = 278, StripByteCounts = 279, PlanarConfig = 284,
        Predictor = 317, TileWidth = 322, TileLength = 323, TileOffsets = 324, TileByteCounts = 325, SampleFormat = 339,
        ModelPixelScale = 33550, ModelTiepoint = 33922, ModelTransformation = 34264, GeoKeyDirectory = 34735,
        GeoDoubleParams = 34736, GeoAsciiParams = 34737, GdalNoData = 42113;

    private sealed class Ifd
    {
        public readonly Dictionary<ushort, double[]> Num = [];
        public readonly Dictionary<ushort, string> Ascii = [];
        public double Get(ushort tag, double def = 0) => Num.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : def;
        public double[]? Arr(ushort tag) => Num.GetValueOrDefault(tag);
    }

    public static Raster Read(string path) => Read(File.ReadAllBytes(path));

    public static Raster Read(byte[] data)
    {
        bool le = data[0] == 'I' && data[1] == 'I';
        if (!le && !(data[0] == 'M' && data[1] == 'M')) throw new InvalidDataException("Not a TIFF file.");
        ushort magic = U16(data, 2, le);
        bool big = magic == 43;
        if (magic != 42 && !big) throw new InvalidDataException("Bad TIFF magic number.");
        long ifdOff = big ? (long)U64(data, 8, le) : U32(data, 4, le);
        var ifd = ReadIfd(data, ifdOff, le, big);

        int width = (int)ifd.Get(ImageWidth), height = (int)ifd.Get(ImageLength);
        int spp = (int)ifd.Get(SamplesPerPixel, 1);
        int bits = (int)ifd.Get(BitsPerSample, 8);
        int fmt = (int)ifd.Get(SampleFormat, 1);
        int comp = (int)ifd.Get(Compression, 1);
        int pred = (int)ifd.Get(Predictor, 1);
        int planar = (int)ifd.Get(PlanarConfig, 1);
        int bps = bits / 8;
        if (bits % 8 != 0) throw new NotSupportedException($"{bits}-bit samples are not supported.");

        var bands = Enumerable.Range(0, spp).Select(_ => new float[width * height]).ToArray();
        bool tiled = ifd.Num.ContainsKey(TileOffsets);
        int blockW = tiled ? (int)ifd.Get(TileWidth) : width;
        int blockH = tiled ? (int)ifd.Get(TileLength) : (int)Math.Min(ifd.Get(RowsPerStrip, height), height);
        var offsets = ifd.Arr(tiled ? TileOffsets : StripOffsets) ?? throw new InvalidDataException("No strip/tile offsets.");
        var counts = ifd.Arr(tiled ? TileByteCounts : StripByteCounts) ?? throw new InvalidDataException("No strip/tile byte counts.");
        int blocksAcross = (width + blockW - 1) / blockW;
        int blocksDown = (height + blockH - 1) / blockH;
        int blocksPerPlane = blocksAcross * blocksDown;
        int samplesInBlock = planar == 1 ? spp : 1;

        Parallel.For(0, offsets.Length, b =>
        {
            int plane = planar == 2 ? b / blocksPerPlane : 0;
            int bi = b % blocksPerPlane;
            int bx = bi % blocksAcross, by = bi / blocksAcross;
            var raw = data.AsSpan((int)offsets[b], (int)counts[b]);
            int rowBytes = blockW * samplesInBlock * bps;
            var block = Decompress(raw, comp, rowBytes * blockH);
            if (pred == 2) UndoPredictor(block, blockW, blockH, samplesInBlock, bps, le);
            else if (pred == 3) UndoFloatPredictor(block, blockW, blockH, samplesInBlock, bps, le);
            for (int r = 0; r < blockH; r++)
            {
                int y = by * blockH + r;
                if (y >= height) break;
                for (int c = 0; c < blockW; c++)
                {
                    int x = bx * blockW + c;
                    if (x >= width) break;
                    for (int s = 0; s < samplesInBlock; s++)
                    {
                        int off = (r * blockW + c) * samplesInBlock * bps + s * bps;
                        if (off + bps > block.Length) continue;
                        float v = Decode(block, off, bps, fmt, le);
                        bands[planar == 2 ? plane : s][y * width + x] = v;
                    }
                }
            }
        });

        float? noData = ifd.Ascii.TryGetValue(GdalNoData, out var nd) && float.TryParse(nd.Trim('\0', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var ndv) ? ndv : null;
        var (transform, crs) = ReadGeo(ifd);
        return new Raster(width, height, bands, transform, crs, noData);
    }

    private static (GeoTransform, CoordinateReferenceSystem?) ReadGeo(Ifd ifd)
    {
        GeoTransform t = new(0, 1, 0, 0, 0, -1);
        int rasterType = 1;
        CoordinateReferenceSystem? crs = null;
        if (ifd.Arr(GeoKeyDirectory) is { } keys && keys.Length >= 4)
        {
            int n = (int)keys[3];
            int model = 0, geog = 0, proj = 0;
            for (int k = 0; k < n; k++)
            {
                int id = (int)keys[4 + k * 4], loc = (int)keys[5 + k * 4], val = (int)keys[7 + k * 4];
                if (loc != 0) continue;
                switch (id)
                {
                    case 1024: model = val; break;
                    case 1025: rasterType = val; break;
                    case 2048: geog = val; break;
                    case 3072: proj = val; break;
                }
            }
            int code = model == 2 || (proj is 0 or 32767) ? geog : proj;
            if (code is > 0 and not 32767 && CrsRegistry.TryGet(code, out var c)) crs = c;
        }
        if (ifd.Arr(ModelTransformation) is { Length: >= 16 } m)
            t = new GeoTransform(m[3], m[0], m[1], m[7], m[4], m[5]);
        else if (ifd.Arr(ModelPixelScale) is { Length: >= 2 } sc && ifd.Arr(ModelTiepoint) is { Length: >= 6 } tp)
            t = new GeoTransform(tp[3] - tp[0] * sc[0], sc[0], 0, tp[4] + tp[1] * sc[1], 0, -sc[1]);
        if (rasterType == 2) // PixelIsPoint → shift to corner
            t = t with { OriginX = t.OriginX - t.PixelWidth / 2, OriginY = t.OriginY - t.PixelHeight / 2 };
        return (t, crs);
    }

    private static Ifd ReadIfd(byte[] d, long off, bool le, bool big)
    {
        var ifd = new Ifd();
        long n = big ? (long)U64(d, off, le) : U16(d, off, le);
        long p = off + (big ? 8 : 2);
        int entrySize = big ? 20 : 12;
        for (long i = 0; i < n; i++, p += entrySize)
        {
            ushort tag = U16(d, p, le), type = U16(d, p + 2, le);
            long count = big ? (long)U64(d, p + 4, le) : U32(d, p + 4, le);
            int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 or 16 or 17 or 18 => 8, _ => 1 };
            long total = size * count;
            long valOff = total <= (big ? 8 : 4) ? p + (big ? 12 : 8) : (big ? (long)U64(d, p + 12, le) : U32(d, p + 8, le));
            if (type == 2)
            {
                ifd.Ascii[tag] = Encoding.ASCII.GetString(d, (int)valOff, (int)count).TrimEnd('\0');
                continue;
            }
            var vals = new double[count];
            for (long k = 0; k < count; k++)
            {
                long o = valOff + k * size;
                vals[k] = type switch
                {
                    1 or 7 => d[o],
                    6 => (sbyte)d[o],
                    3 => U16(d, o, le),
                    8 => (short)U16(d, o, le),
                    4 or 13 => U32(d, o, le),
                    9 => (int)U32(d, o, le),
                    5 => U32(d, o, le) / (double)U32(d, o + 4, le),
                    10 => (int)U32(d, o, le) / (double)(int)U32(d, o + 4, le),
                    11 => BitConverter.Int32BitsToSingle((int)U32(d, o, le)),
                    12 => BitConverter.Int64BitsToDouble((long)U64(d, o, le)),
                    16 or 18 => U64(d, o, le),
                    17 => (long)U64(d, o, le),
                    _ => 0
                };
            }
            ifd.Num[tag] = vals;
        }
        return ifd;
    }

    private static ushort U16(byte[] d, long o, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)o)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)o));
    private static uint U32(byte[] d, long o, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)o)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)o));
    private static ulong U64(byte[] d, long o, bool le) => le ? BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)o)) : BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan((int)o));

    private static float Decode(byte[] b, int o, int bps, int fmt, bool le)
    {
        var s = b.AsSpan(o, bps);
        return (fmt, bps) switch
        {
            (3, 4) => le ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s),
            (3, 8) => (float)(le ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s)),
            (2, 1) => (sbyte)s[0],
            (_, 1) => s[0],
            (2, 2) => le ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadInt16BigEndian(s),
            (_, 2) => le ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s),
            (2, 4) => le ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s),
            (_, 4) => le ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s),
            (2, 8) => le ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s),
            (_, 8) => le ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s),
            _ => float.NaN
        };
    }

    private static byte[] Decompress(ReadOnlySpan<byte> raw, int comp, int expected)
    {
        switch (comp)
        {
            case 1: return raw.ToArray();
            case 8 or 32946:
            {
                using var ms = new MemoryStream(raw.ToArray());
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                var outBuf = new byte[expected];
                int read = 0, n;
                while (read < expected && (n = z.Read(outBuf, read, expected - read)) > 0) read += n;
                return outBuf;
            }
            case 5: return LzwDecode(raw, expected);
            case 32773: return PackBits(raw, expected);
            default: throw new NotSupportedException($"TIFF compression {comp} is not supported.");
        }
    }

    /// <summary>TIFF LZW (MSB-first, early change).</summary>
    internal static byte[] LzwDecode(ReadOnlySpan<byte> input, int expected)
    {
        var output = new List<byte>(expected);
        var table = new List<byte[]>(4096);
        void Reset() { table.Clear(); for (int i = 0; i < 256; i++) table.Add([(byte)i]); table.Add([]); table.Add([]); }
        Reset();
        int codeLen = 9;
        long bitPos = 0;
        long totalBits = input.Length * 8L;
        byte[]? prev = null;
        while (bitPos + codeLen <= totalBits)
        {
            int code = 0;
            for (int i = 0; i < codeLen; i++)
            {
                long bp = bitPos + i;
                code = (code << 1) | ((input[(int)(bp >> 3)] >> (7 - (int)(bp & 7))) & 1);
            }
            bitPos += codeLen;
            if (code == 257) break;
            if (code == 256) { Reset(); codeLen = 9; prev = null; continue; }
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (prev is not null) entry = [.. prev, prev[0]];
            else break;
            output.AddRange(entry);
            if (prev is not null) table.Add([.. prev, entry[0]]);
            prev = entry;
            int size = table.Count;
            if (size >= 511 && size < 1023) codeLen = 10;
            else if (size >= 1023 && size < 2047) codeLen = 11;
            else if (size >= 2047) codeLen = 12;
        }
        var res = output.ToArray();
        if (res.Length < expected) Array.Resize(ref res, expected);
        return res;
    }

    private static byte[] PackBits(ReadOnlySpan<byte> input, int expected)
    {
        var o = new List<byte>(expected);
        int i = 0;
        while (i < input.Length && o.Count < expected)
        {
            sbyte n = (sbyte)input[i++];
            if (n >= 0) { for (int k = 0; k <= n && i < input.Length; k++) o.Add(input[i++]); }
            else if (n != -128) { byte b = input[i++]; for (int k = 0; k < 1 - n; k++) o.Add(b); }
        }
        var res = o.ToArray();
        if (res.Length < expected) Array.Resize(ref res, expected);
        return res;
    }

    /// <summary>TIFF floating-point predictor (Adobe Technical Note 3): byte-wise horizontal differencing over byte planes (MSB first).</summary>
    private static void UndoFloatPredictor(byte[] block, int w, int h, int spp, int bps, bool le)
    {
        int n = w * spp, rowBytes = n * bps;
        var tmp = new byte[rowBytes];
        for (int r = 0; r < h; r++)
        {
            int row = r * rowBytes;
            if (row + rowBytes > block.Length) break;
            var span = block.AsSpan(row, rowBytes);
            for (int i = spp; i < rowBytes; i++) span[i] = (byte)(span[i] + span[i - spp]);
            span.CopyTo(tmp);
            for (int k = 0; k < n; k++)
                for (int j = 0; j < bps; j++)
                {
                    byte v = tmp[j * n + k];                     // byte j of sample k, most significant first
                    span[k * bps + (le ? bps - 1 - j : j)] = v;
                }
        }
    }

    private static void UndoPredictor(byte[] block, int w, int h, int spp, int bps, bool le)
    {
        int stride = w * spp * bps;
        for (int r = 0; r < h; r++)
        {
            int row = r * stride;
            for (int c = spp; c < w * spp; c++)
            {
                int o = row + c * bps, p = row + (c - spp) * bps;
                if (o + bps > block.Length) break;
                switch (bps)
                {
                    case 1: block[o] = (byte)(block[o] + block[p]); break;
                    case 2:
                        ushort a = le ? BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(o));
                        ushort b = le ? BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(p));
                        if (le) BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(o), (ushort)(a + b)); else BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(o), (ushort)(a + b));
                        break;
                    case 4:
                        uint a4 = le ? BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(o));
                        uint b4 = le ? BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(p));
                        if (le) BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(o), a4 + b4); else BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(o), a4 + b4);
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ writer

    /// <summary>
    /// Writes a float32 GeoTIFF. <paramref name="tileSize"/> &gt; 0 writes a tiled TIFF (COG-friendly layout).
    /// </summary>
    public static void Write(Raster raster, string path, TiffCompression compression = TiffCompression.Deflate, int tileSize = 256)
    {
        if (compression == TiffCompression.Lzw) throw new NotSupportedException("LZW writing is not supported; use Deflate.");
        int w = raster.Width, h = raster.Height, spp = raster.BandCount;
        bool tiled = tileSize > 0;
        int bw = tiled ? tileSize : w, bh = tiled ? tileSize : Math.Max(1, Math.Min(h, 65536 / Math.Max(1, w * spp * 4) + 1));
        int across = (w + bw - 1) / bw, down = (h + bh - 1) / bh;
        var blocks = new byte[across * down][];
        Parallel.For(0, blocks.Length, b =>
        {
            int bx = b % across, by = b / across;
            int rows = tiled ? bh : Math.Min(bh, h - by * bh);
            var buf = new byte[bw * rows * spp * 4];
            for (int r = 0; r < rows; r++)
            {
                int y = by * bh + r;
                for (int c = 0; c < bw; c++)
                {
                    int x = bx * bw + c;
                    for (int s = 0; s < spp; s++)
                    {
                        float v = x < w && y < h ? raster.Bands[s][y * w + x] : (raster.NoData ?? 0);
                        if (float.IsNaN(v) && raster.NoData is { } nd) v = nd;
                        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(((r * bw + c) * spp + s) * 4), v);
                    }
                }
            }
            if (compression == TiffCompression.Deflate)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(buf);
                buf = ms.ToArray();
            }
            blocks[b] = buf;
        });

        using var fs = new FileStream(path, FileMode.Create);
        using var bwr = new BinaryWriter(fs);
        bwr.Write((byte)'I'); bwr.Write((byte)'I'); bwr.Write((ushort)42); bwr.Write(0u); // IFD offset placeholder
        var offsets = new uint[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            offsets[i] = (uint)fs.Position;
            bwr.Write(blocks[i]);
            if ((fs.Position & 1) != 0) bwr.Write((byte)0);
        }

        var entries = new List<(ushort Tag, ushort Type, object Value)>
        {
            (ImageWidth, 4, new uint[] { (uint)w }),
            (ImageLength, 4, new uint[] { (uint)h }),
            (BitsPerSample, 3, Enumerable.Repeat((ushort)32, spp).ToArray()),
            (Compression, 3, new ushort[] { (ushort)compression }),
            (Photometric, 3, new ushort[] { 1 }),
            (SamplesPerPixel, 3, new ushort[] { (ushort)spp }),
            (PlanarConfig, 3, new ushort[] { 1 }),
            (SampleFormat, 3, Enumerable.Repeat((ushort)3, spp).ToArray()),
        };
        if (tiled)
        {
            entries.Add((TileWidth, 4, new uint[] { (uint)bw }));
            entries.Add((TileLength, 4, new uint[] { (uint)bh }));
            entries.Add((TileOffsets, 4, offsets));
            entries.Add((TileByteCounts, 4, blocks.Select(b => (uint)b.Length).ToArray()));
        }
        else
        {
            entries.Add((StripOffsets, 4, offsets));
            entries.Add((RowsPerStrip, 4, new uint[] { (uint)bh }));
            entries.Add((StripByteCounts, 4, blocks.Select(b => (uint)b.Length).ToArray()));
        }
        var t = raster.Transform;
        if (t.RotationX == 0 && t.RotationY == 0)
        {
            entries.Add((ModelPixelScale, 12, new[] { t.PixelWidth, -t.PixelHeight, 0.0 }));
            entries.Add((ModelTiepoint, 12, new[] { 0, 0, 0, t.OriginX, t.OriginY, 0.0 }));
        }
        else
        {
            entries.Add((ModelTransformation, 12, new[] { t.PixelWidth, t.RotationX, 0, t.OriginX, t.RotationY, t.PixelHeight, 0, t.OriginY, 0, 0, 0, 0, 0, 0, 0, 1.0 }));
        }
        if (raster.Crs is { Epsg: { } code } crs)
        {
            var keys = new List<ushort> { 1, 1, 0, 3 };
            keys.AddRange([1024, 0, 1, (ushort)(crs.IsGeographic ? 2 : 1)]);
            keys.AddRange([1025, 0, 1, 1]);
            keys.AddRange([(ushort)(crs.IsGeographic ? 2048 : 3072), 0, 1, (ushort)code]);
            entries.Add((GeoKeyDirectory, 3, keys.ToArray()));
        }
        if (raster.NoData is { } nodata) entries.Add((GdalNoData, 2, nodata.ToString(CultureInfo.InvariantCulture) + "\0"));
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // write out-of-line values first, then the IFD
        var valueOffsets = new Dictionary<ushort, uint>();
        foreach (var (tag, type, val) in entries)
        {
            var bytes = Serialize(type, val);
            if (bytes.Length <= 4) continue;
            valueOffsets[tag] = (uint)fs.Position;
            bwr.Write(bytes);
            if ((fs.Position & 1) != 0) bwr.Write((byte)0);
        }
        uint ifdOffset = (uint)fs.Position;
        bwr.Write((ushort)entries.Count);
        foreach (var (tag, type, val) in entries)
        {
            var bytes = Serialize(type, val);
            int count = type switch { 2 => bytes.Length, 3 => bytes.Length / 2, 4 => bytes.Length / 4, 12 => bytes.Length / 8, _ => bytes.Length };
            bwr.Write(tag); bwr.Write(type); bwr.Write((uint)count);
            if (bytes.Length <= 4) { var pad = new byte[4]; bytes.CopyTo(pad, 0); bwr.Write(pad); }
            else bwr.Write(valueOffsets[tag]);
        }
        bwr.Write(0u);
        fs.Position = 4;
        bwr.Write(ifdOffset);
    }

    private static byte[] Serialize(ushort type, object val) => val switch
    {
        string s => Encoding.ASCII.GetBytes(s),
        ushort[] a => a.SelectMany(BitConverter.GetBytes).ToArray(),
        uint[] a => a.SelectMany(BitConverter.GetBytes).ToArray(),
        double[] a => a.SelectMany(BitConverter.GetBytes).ToArray(),
        _ => throw new InvalidOperationException()
    };
}
