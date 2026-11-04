using System.Buffers.Binary;
using System.Globalization;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Terrain;

namespace OpenSceneGraph.Net.IO;

/// <summary>
/// Digital elevation model readers producing terrain nodes: ESRI ASCII Grid (<c>.asc</c>) and GeoTIFF
/// (<c>.tif/.tiff</c>, uncompressed strips or tiles, 16/32-bit integer or 32/64-bit float, single band).
/// Use <see cref="ReadHeightField(string)"/> to get the raw <see cref="HeightField"/>.
/// </summary>
public sealed class HeightFieldReaderWriter : IReaderWriter
{
    public string Name => "DEM (ASCII grid / GeoTIFF)";
    public IReadOnlyList<string> Extensions { get; } = ["asc", "tif", "tiff"];
    public bool CanRead => true;
    public bool CanWrite => false;
    public void WriteNode(Node node, Stream stream) => throw new NotSupportedException();

    public Node ReadNode(Stream stream, ReadOptions options)
    {
        var hf = ReadHeightField(stream);
        return TerrainBuilder.Build(hf, tilesPerSide: System.Math.Clamp(hf.Columns / 128, 1, 8));
    }

    public static HeightField ReadHeightField(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadHeightField(fs);
    }

    public static HeightField ReadHeightField(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var d = ms.ToArray();
        if (d.Length > 4 && (d[0] == 'I' && d[1] == 'I' || d[0] == 'M' && d[1] == 'M')) return ReadGeoTiff(d);
        return ReadAsciiGrid(System.Text.Encoding.ASCII.GetString(d));
    }

    // ------------------------------------------------------------------ ESRI ASCII grid
    public static HeightField ReadAsciiGrid(string text)
    {
        var tokens = text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        int ncols = 0, nrows = 0;
        double xll = 0, yll = 0, cell = 1;
        float nodata = -9999;
        bool center = false;
        while (i + 1 < tokens.Length && char.IsLetter(tokens[i][0]))
        {
            string key = tokens[i].ToLowerInvariant();
            string val = tokens[i + 1];
            switch (key)
            {
                case "ncols": ncols = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "nrows": nrows = int.Parse(val, CultureInfo.InvariantCulture); break;
                case "xllcorner": xll = double.Parse(val, CultureInfo.InvariantCulture); break;
                case "yllcorner": yll = double.Parse(val, CultureInfo.InvariantCulture); break;
                case "xllcenter": xll = double.Parse(val, CultureInfo.InvariantCulture); center = true; break;
                case "yllcenter": yll = double.Parse(val, CultureInfo.InvariantCulture); center = true; break;
                case "cellsize": cell = double.Parse(val, CultureInfo.InvariantCulture); break;
                case "nodata_value": nodata = float.Parse(val, CultureInfo.InvariantCulture); break;
            }
            i += 2;
        }
        if (ncols < 2 || nrows < 2) throw new InvalidDataException("Invalid ASCII grid header.");
        var hf = new HeightField(ncols, nrows, (float)cell, (float)cell) { NoDataValue = nodata };
        // file rows go north to south; HeightField row 0 is south
        for (int r = 0; r < nrows; r++)
            for (int c = 0; c < ncols; c++)
            {
                float v = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
                hf[c, nrows - 1 - r] = v == nodata ? float.NaN : v;
            }
        hf.FillNoData();
        hf.GeoOrigin = center ? (xll, yll) : (xll + cell / 2, yll + cell / 2);
        hf.Origin = new System.Numerics.Vector3(-hf.Width / 2, -hf.Depth / 2, 0);
        return hf;
    }

    // ------------------------------------------------------------------ GeoTIFF
    private static HeightField ReadGeoTiff(byte[] d)
    {
        bool le = d[0] == 'I';
        ushort U16(int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
        uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        double F64(int o) => BitConverter.Int64BitsToDouble((long)(le ? BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o))));

        if (U16(2) != 42) throw new NotSupportedException("BigTIFF is not supported.");
        int ifd = (int)U32(4);
        int count = U16(ifd);
        var tags = new Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)>();
        for (int k = 0; k < count; k++)
        {
            int e = ifd + 2 + k * 12;
            ushort tag = U16(e), type = U16(e + 2);
            uint n = U32(e + 4);
            int size = type switch { 3 => 2, 4 => 4, 11 => 4, 12 => 8, 16 => 8, _ => 1 } * (int)n;
            tags[tag] = (type, n, size <= 4 ? e + 8 : (int)U32(e + 8));
        }

        uint Scalar(ushort tag, uint def = 0) => !tags.TryGetValue(tag, out var t) ? def : t.Type == 3 ? U16(t.ValueOffset) : U32(t.ValueOffset);
        uint[] Array32(ushort tag)
        {
            var t = tags[tag];
            var r = new uint[t.Count];
            for (int k = 0; k < t.Count; k++) r[k] = t.Type == 3 ? U16(t.ValueOffset + k * 2) : U32(t.ValueOffset + k * 4);
            return r;
        }
        double[] Doubles(ushort tag)
        {
            if (!tags.TryGetValue(tag, out var t)) return [];
            var r = new double[t.Count];
            for (int k = 0; k < t.Count; k++) r[k] = F64(t.ValueOffset + k * 8);
            return r;
        }

        int width = (int)Scalar(256), height = (int)Scalar(257);
        int bits = (int)Scalar(258, 8);
        int compression = (int)Scalar(259, 1);
        int sampleFormat = (int)Scalar(339, 1); // 1 uint, 2 int, 3 float
        int samplesPerPixel = (int)Scalar(277, 1);
        if (compression != 1) throw new NotSupportedException("Only uncompressed GeoTIFF is supported (convert with: gdal_translate -co COMPRESS=NONE).");
        if (samplesPerPixel != 1) throw new NotSupportedException("Only single-band GeoTIFF elevation is supported.");

        int bps = bits / 8;
        float ReadSample(int o) => (sampleFormat, bits) switch
        {
            (3, 32) => BitConverter.Int32BitsToSingle((int)U32(o)),
            (3, 64) => (float)F64(o),
            (2, 16) => (short)U16(o),
            (2, 32) => (int)U32(o),
            (1, 16) => U16(o),
            (1, 32) => U32(o),
            (_, 8) => d[o],
            _ => throw new NotSupportedException($"Unsupported sample format {sampleFormat}/{bits}."),
        };

        var raster = new float[width * height];
        if (tags.ContainsKey(324)) // tiled
        {
            int tw = (int)Scalar(322), th = (int)Scalar(323);
            var offsets = Array32(324);
            int tilesAcross = (width + tw - 1) / tw;
            for (int t = 0; t < offsets.Length; t++)
            {
                int tx0 = t % tilesAcross * tw, ty0 = t / tilesAcross * th;
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                    {
                        int gx = tx0 + x, gy = ty0 + y;
                        if (gx >= width || gy >= height) continue;
                        raster[gy * width + gx] = ReadSample((int)offsets[t] + (y * tw + x) * bps);
                    }
            }
        }
        else
        {
            int rowsPerStrip = (int)Scalar(278, (uint)height);
            var offsets = Array32(273);
            for (int y = 0; y < height; y++)
            {
                int strip = y / rowsPerStrip, rowInStrip = y % rowsPerStrip;
                int baseOff = (int)offsets[strip] + rowInStrip * width * bps;
                for (int x = 0; x < width; x++) raster[y * width + x] = ReadSample(baseOff + x * bps);
            }
        }

        var scale = Doubles(33550);   // ModelPixelScaleTag
        var tie = Doubles(33922);     // ModelTiepointTag (i,j,k,x,y,z)
        float sx = scale.Length >= 2 ? (float)scale[0] : 1, sy = scale.Length >= 2 ? (float)scale[1] : 1;
        bool geographic = sx < 0.1 && sy < 0.1; // degrees: convert spacing to metres approximately
        float mx = sx, my = sy;
        double originY = tie.Length >= 6 ? tie[4] : 0;
        if (geographic)
        {
            double latMid = originY - height * sy / 2.0;
            mx = (float)(sx * 111_320.0 * System.Math.Cos(latMid * System.Math.PI / 180));
            my = (float)(sy * 110_540.0);
        }

        float nodata = float.NaN;
        if (tags.TryGetValue(42113, out var nd)) // GDAL_NODATA (ASCII)
        {
            var s = System.Text.Encoding.ASCII.GetString(d, nd.ValueOffset, (int)nd.Count).TrimEnd('\0');
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) nodata = v;
        }

        var hf = new HeightField(width, height, mx, my) { NoDataValue = nodata, Crs = geographic ? "EPSG:4326" : null };
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float v = raster[y * width + x];
                hf[x, height - 1 - y] = v == nodata || v < -1e30f ? float.NaN : v;
            }
        hf.FillNoData();
        if (tie.Length >= 6) hf.GeoOrigin = (tie[3], tie[4] - (height - 1) * sy);
        hf.Origin = new System.Numerics.Vector3(-hf.Width / 2, -hf.Depth / 2, 0);
        return hf;
    }
}
