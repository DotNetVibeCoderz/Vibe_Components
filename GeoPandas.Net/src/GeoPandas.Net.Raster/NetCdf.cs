using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.IO;

namespace GeoPandas.Net.Raster;

/// <summary>NetCDF external data types (classic + CDF-5 + NetCDF-4 strings).</summary>
public enum NetCdfType { Byte = 1, Char = 2, Short = 3, Int = 4, Float = 5, Double = 6, UByte = 7, UShort = 8, UInt = 9, Int64 = 10, UInt64 = 11, String = 12 }

public sealed record NetCdfDimension(string Name, long Length, bool IsUnlimited);

/// <summary>A NetCDF variable: name, dimensions, type and attributes (CF metadata such as units, scale_factor, _FillValue).</summary>
public sealed class NetCdfVariable
{
    internal NetCdfVariable(string name, string[] dims, long[] shape, NetCdfType type, Dictionary<string, object> attrs)
    {
        Name = name; Dimensions = dims; Shape = shape; Type = type; Attributes = attrs;
    }

    public string Name { get; }
    public IReadOnlyList<string> Dimensions { get; }
    public IReadOnlyList<long> Shape { get; }
    public NetCdfType Type { get; }
    public IReadOnlyDictionary<string, object> Attributes { get; }
    public long Size => Shape.Aggregate(1L, (a, b) => a * b);
    public string? Units => Attributes.TryGetValue("units", out var u) ? u as string : null;
    public string? LongName => Attributes.TryGetValue("long_name", out var u) ? u as string : Attributes.TryGetValue("standard_name", out var s) ? s as string : null;

    // classic-format storage
    internal long Begin;
    internal long VSize;
    internal bool IsRecord;

    public override string ToString() => $"{Type.ToString().ToLowerInvariant()} {Name}({string.Join(", ", Dimensions)})" + (Units is null ? "" : $" [{Units}]");
}

/// <summary>
/// NetCDF reader for gridded climate, ocean and weather data: classic CDF-1, 64-bit offset CDF-2 and 64-bit data CDF-5
/// (own memory-mapped parser) and NetCDF-4 / HDF5 (via PureHDF). Applies CF conventions: <c>_FillValue</c> / <c>missing_value</c> /
/// <c>valid_range</c> → NaN, <c>scale_factor</c> / <c>add_offset</c> unpacking, <c>lat</c>/<c>lon</c> (or projected <c>x</c>/<c>y</c>)
/// coordinate variables → <see cref="GeoTransform"/>, <c>"days since …"</c> time axes → <see cref="DateTime"/>.
/// </summary>
/// <example>
/// <code>
/// using var nc = NetCdfDataset.Open("era5_t2m.nc");
/// var t2m = nc.ToRaster("t2m", 0);                    // first time step, north-up, WGS 84
/// var times = nc.Times();
/// </code>
/// </example>
public sealed class NetCdfDataset : IDisposable
{
    private readonly MappedFile? _file;
    private readonly PureHDF.VOL.Native.NativeFile? _h5;
    private readonly int _version;
    private readonly long _numRecs;
    private readonly long _recSize;
    private readonly List<NetCdfDimension> _dims = [];
    private readonly List<NetCdfVariable> _vars = [];
    private readonly Dictionary<string, object> _gatts = [];
    private readonly Dictionary<string, PureHDF.IH5Dataset> _h5Datasets = [];

    public string Path { get; }
    /// <summary>"CDF-1 (classic)", "CDF-2 (64-bit offset)", "CDF-5 (64-bit data)" or "NetCDF-4 (HDF5)".</summary>
    public string Format { get; }
    public IReadOnlyList<NetCdfDimension> Dimensions => _dims;
    public IReadOnlyList<NetCdfVariable> Variables => _vars;
    public IReadOnlyDictionary<string, object> GlobalAttributes => _gatts;

    private NetCdfDataset(string path, MappedFile file)
    {
        Path = path; _file = file;
        var h = file.Span(0, 4);
        _version = h[3];
        Format = _version switch { 1 => "CDF-1 (classic)", 2 => "CDF-2 (64-bit offset)", 5 => "CDF-5 (64-bit data)", _ => throw new InvalidDataException($"Unknown NetCDF version {_version}.") };
        long pos = 4;
        _numRecs = _version == 5 ? (long)ReadU64(ref pos) : ReadU32(ref pos);
        if (_numRecs == 0xFFFFFFFF) _numRecs = 0; // streaming: computed below
        ReadDimList(ref pos);
        ReadAttList(ref pos, _gatts);
        ReadVarList(ref pos);
        var recVars = _vars.Where(v => v.IsRecord).ToList();
        _recSize = recVars.Count == 1 ? ElemSize(recVars[0].Type) * recVars[0].Shape.Skip(1).Aggregate(1L, (a, b) => a * b) : recVars.Sum(v => v.VSize);
        // record dimension length = numrecs
        for (int i = 0; i < _dims.Count; i++)
            if (_dims[i].IsUnlimited)
            {
                _dims[i] = _dims[i] with { Length = _numRecs };
                foreach (var v in _vars.Where(v => v.Dimensions.Count > 0 && v.Dimensions[0] == _dims[i].Name))
                    ((long[])v.Shape)[0] = _numRecs;
            }
    }

    private NetCdfDataset(string path, PureHDF.VOL.Native.NativeFile h5)
    {
        Path = path; _h5 = h5; Format = "NetCDF-4 (HDF5)";
        foreach (var a in h5.Attributes()) _gatts[a.Name] = H5Attr(a);
        var datasets = h5.Children().OfType<PureHDF.IH5Dataset>().ToList();
        var scaleNames = new HashSet<string>();
        foreach (var d in datasets)
        {
            if (d.AttributeExists("CLASS") && H5Attr(d.Attribute("CLASS")) as string == "DIMENSION_SCALE")
            {
                scaleNames.Add(d.Name);
                var len = (long)d.Space.Dimensions[0];
                bool unlimited = d.Space.MaxDimensions.Length > 0 && d.Space.MaxDimensions[0] == ulong.MaxValue;
                _dims.Add(new NetCdfDimension(d.Name, len, unlimited));
            }
        }
        foreach (var d in datasets)
        {
            var attrs = new Dictionary<string, object>();
            foreach (var a in d.Attributes())
            {
                if (a.Name is "CLASS" or "DIMENSION_LIST" or "REFERENCE_LIST" or "_Netcdf4Dimid" or "_Netcdf4Coordinates" or "_NCProperties") continue;
                if (a.Name == "NAME" && scaleNames.Contains(d.Name)) continue;
                attrs[a.Name] = H5Attr(a);
            }
            if (d.AttributeExists("NAME") && H5Attr(d.Attribute("NAME")) is string nm && nm.StartsWith("This is a netCDF dimension but not a netCDF variable", StringComparison.Ordinal))
                continue;
            var shape = d.Space.Dimensions.Select(x => (long)x).ToArray();
            var dims = ResolveDims(h5, d, shape, scaleNames);
            var type = d.Type.Class switch
            {
                PureHDF.H5DataTypeClass.FloatingPoint => d.Type.Size == 4 ? NetCdfType.Float : NetCdfType.Double,
                PureHDF.H5DataTypeClass.FixedPoint => (d.Type.Size, d.Type.FixedPoint.IsSigned) switch
                {
                    (1, true) => NetCdfType.Byte, (1, false) => NetCdfType.UByte, (2, true) => NetCdfType.Short, (2, false) => NetCdfType.UShort,
                    (4, true) => NetCdfType.Int, (4, false) => NetCdfType.UInt, (8, true) => NetCdfType.Int64, _ => NetCdfType.UInt64
                },
                PureHDF.H5DataTypeClass.String or PureHDF.H5DataTypeClass.VariableLength => NetCdfType.String,
                _ => NetCdfType.Char
            };
            _vars.Add(new NetCdfVariable(d.Name, dims, shape, type, attrs));
            _h5Datasets[d.Name] = d;
        }
    }

    private string[] ResolveDims(PureHDF.VOL.Native.NativeFile h5, PureHDF.IH5Dataset d, long[] shape, HashSet<string> scales)
    {
        if (scales.Contains(d.Name)) return [d.Name];
        try
        {
            if (d.AttributeExists("DIMENSION_LIST"))
            {
                var refs = d.Attribute("DIMENSION_LIST").Read<PureHDF.VOL.Native.NativeObjectReference1[][]>();
                var names = refs.Select(r => h5.Get(r[0]).Name ?? "").ToArray();
                if (names.Length == shape.Length && names.All(n => n.Length > 0)) return names.Select(n => n.TrimStart('/')).ToArray();
            }
        }
        catch (Exception) { /* fall back to length matching */ }
        var used = new HashSet<string>();
        var res = new string[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            var match = _dims.FirstOrDefault(x => x.Length == shape[i] && !used.Contains(x.Name));
            res[i] = match?.Name ?? $"dim{i}_{shape[i]}";
            if (match is not null) used.Add(match.Name);
        }
        return res;
    }

    private static object H5Attr(PureHDF.IH5Attribute a)
    {
        try
        {
            switch (a.Type.Class)
            {
                case PureHDF.H5DataTypeClass.String:
                case PureHDF.H5DataTypeClass.VariableLength:
                {
                    var s = a.Read<string[]>();
                    return s.Length == 1 ? s[0].TrimEnd('\0') : string.Join(",", s);
                }
                case PureHDF.H5DataTypeClass.FloatingPoint:
                {
                    var v = a.Type.Size == 4 ? a.Read<float[]>().Select(x => (double)x).ToArray() : a.Read<double[]>();
                    return v.Length == 1 ? v[0] : v;
                }
                case PureHDF.H5DataTypeClass.FixedPoint:
                {
                    long[] v = (a.Type.Size, a.Type.FixedPoint.IsSigned) switch
                    {
                        (1, true) => a.Read<sbyte[]>().Select(x => (long)x).ToArray(),
                        (1, false) => a.Read<byte[]>().Select(x => (long)x).ToArray(),
                        (2, true) => a.Read<short[]>().Select(x => (long)x).ToArray(),
                        (2, false) => a.Read<ushort[]>().Select(x => (long)x).ToArray(),
                        (4, true) => a.Read<int[]>().Select(x => (long)x).ToArray(),
                        (4, false) => a.Read<uint[]>().Select(x => (long)x).ToArray(),
                        _ => a.Read<long[]>()
                    };
                    return v.Length == 1 ? v[0] : v;
                }
                default: return $"<{a.Type.Class}>";
            }
        }
        catch (Exception e) { return $"<unreadable: {e.Message}>"; }
    }

    /// <summary>Opens a NetCDF file (format detected from the magic bytes).</summary>
    public static NetCdfDataset Open(string path)
    {
        Span<byte> magic = stackalloc byte[8];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(magic);
        if (magic[0] == 'C' && magic[1] == 'D' && magic[2] == 'F') return new NetCdfDataset(path, new MappedFile(path));
        if (magic[0] == 0x89 && magic[1] == 'H' && magic[2] == 'D' && magic[3] == 'F')
            return new NetCdfDataset(path, (PureHDF.VOL.Native.NativeFile)PureHDF.H5File.OpenRead(path));
        throw new InvalidDataException($"'{path}' is not a NetCDF file (neither CDF nor HDF5 magic).");
    }

    public void Dispose()
    {
        _file?.Dispose();
        _h5?.Dispose();
    }

    public NetCdfVariable Variable(string name) =>
        _vars.FirstOrDefault(v => v.Name == name) ?? throw new KeyNotFoundException($"Variable '{name}' not found. Available: {string.Join(", ", _vars.Select(v => v.Name))}");

    /// <summary>Gridded data variables (≥ 2 dimensions, not coordinate variables).</summary>
    public IEnumerable<NetCdfVariable> DataVariables => _vars.Where(v => v.Dimensions.Count >= 2 && !_dims.Any(d => d.Name == v.Name));

    // ================================================================== classic header parsing

    private uint ReadU32(ref long pos) { var v = BinaryPrimitives.ReadUInt32BigEndian(_file!.Span(pos, 4)); pos += 4; return v; }
    private ulong ReadU64(ref long pos) { var v = BinaryPrimitives.ReadUInt64BigEndian(_file!.Span(pos, 8)); pos += 8; return v; }
    private long ReadCount(ref long pos) => _version == 5 ? (long)ReadU64(ref pos) : ReadU32(ref pos);

    private string ReadName(ref long pos)
    {
        int len = (int)ReadCount(ref pos);
        var s = Encoding.UTF8.GetString(_file!.Span(pos, len));
        pos += Pad4(len);
        return s;
    }

    private static long Pad4(long n) => (n + 3) & ~3L;

    private void ReadDimList(ref long pos)
    {
        uint tag = ReadU32(ref pos);
        long n = ReadCount(ref pos);
        if (tag == 0) return;
        if (tag != 0x0A) throw new InvalidDataException("NetCDF: expected dimension list.");
        for (long i = 0; i < n; i++)
        {
            var name = ReadName(ref pos);
            long len = ReadCount(ref pos);
            _dims.Add(new NetCdfDimension(name, len, len == 0));
        }
    }

    private void ReadAttList(ref long pos, Dictionary<string, object> target)
    {
        uint tag = ReadU32(ref pos);
        long n = ReadCount(ref pos);
        if (tag == 0) return;
        if (tag != 0x0C) throw new InvalidDataException("NetCDF: expected attribute list.");
        for (long i = 0; i < n; i++)
        {
            var name = ReadName(ref pos);
            var type = (NetCdfType)ReadU32(ref pos);
            long count = ReadCount(ref pos);
            int es = ElemSize(type);
            var bytes = _file!.Span(pos, (int)(count * es));
            pos += Pad4(count * es);
            if (type == NetCdfType.Char) { target[name] = Encoding.UTF8.GetString(bytes).TrimEnd('\0'); continue; }
            var vals = new double[count];
            for (int k = 0; k < count; k++) vals[k] = Decode(bytes, k, type);
            target[name] = IsInteger(type)
                ? (count == 1 ? (object)(long)vals[0] : vals.Select(v => (long)v).ToArray())
                : (count == 1 ? vals[0] : vals);
        }
    }

    private void ReadVarList(ref long pos)
    {
        uint tag = ReadU32(ref pos);
        long n = ReadCount(ref pos);
        if (tag == 0) return;
        if (tag != 0x0B) throw new InvalidDataException("NetCDF: expected variable list.");
        for (long i = 0; i < n; i++)
        {
            var name = ReadName(ref pos);
            long nd = ReadCount(ref pos);
            var dimIds = new int[nd];
            for (int k = 0; k < nd; k++) dimIds[k] = (int)ReadCount(ref pos);
            var attrs = new Dictionary<string, object>();
            ReadAttList(ref pos, attrs);
            var type = (NetCdfType)ReadU32(ref pos);
            long vsize = ReadCount(ref pos);
            long begin = _version == 1 ? ReadU32(ref pos) : (long)ReadU64(ref pos);
            var dims = dimIds.Select(d => _dims[d].Name).ToArray();
            var shape = dimIds.Select(d => _dims[d].Length).ToArray();
            var v = new NetCdfVariable(name, dims, shape, type, attrs)
            {
                Begin = begin, VSize = vsize, IsRecord = nd > 0 && _dims[dimIds[0]].IsUnlimited
            };
            _vars.Add(v);
        }
    }

    internal static int ElemSize(NetCdfType t) => t switch
    {
        NetCdfType.Byte or NetCdfType.Char or NetCdfType.UByte => 1,
        NetCdfType.Short or NetCdfType.UShort => 2,
        NetCdfType.Int or NetCdfType.UInt or NetCdfType.Float => 4,
        _ => 8
    };

    private static bool IsInteger(NetCdfType t) => t is not (NetCdfType.Float or NetCdfType.Double);

    private static double Decode(ReadOnlySpan<byte> b, int i, NetCdfType t) => t switch
    {
        NetCdfType.Byte => (sbyte)b[i],
        NetCdfType.UByte or NetCdfType.Char => b[i],
        NetCdfType.Short => BinaryPrimitives.ReadInt16BigEndian(b[(i * 2)..]),
        NetCdfType.UShort => BinaryPrimitives.ReadUInt16BigEndian(b[(i * 2)..]),
        NetCdfType.Int => BinaryPrimitives.ReadInt32BigEndian(b[(i * 4)..]),
        NetCdfType.UInt => BinaryPrimitives.ReadUInt32BigEndian(b[(i * 4)..]),
        NetCdfType.Float => BinaryPrimitives.ReadSingleBigEndian(b[(i * 4)..]),
        NetCdfType.Double => BinaryPrimitives.ReadDoubleBigEndian(b[(i * 8)..]),
        NetCdfType.Int64 => BinaryPrimitives.ReadInt64BigEndian(b[(i * 8)..]),
        NetCdfType.UInt64 => BinaryPrimitives.ReadUInt64BigEndian(b[(i * 8)..]),
        _ => double.NaN
    };

    // ================================================================== data access

    /// <summary>Raw values of a whole variable as doubles (no CF unpacking).</summary>
    public double[] ReadRaw(string name)
    {
        var v = Variable(name);
        if (_h5 is not null) return ReadH5(v);
        int es = ElemSize(v.Type);
        long total = v.Size;
        var res = new double[total];
        if (!v.IsRecord)
        {
            var span = _file!.Span(v.Begin, checked((int)(total * es)));
            for (int i = 0; i < total; i++) res[i] = Decode(span, i, v.Type);
            return res;
        }
        long perRec = v.Shape.Skip(1).Aggregate(1L, (a, b) => a * b);
        for (long r = 0; r < _numRecs; r++)
        {
            var span = _file!.Span(v.Begin + r * _recSize, checked((int)(perRec * es)));
            for (int i = 0; i < perRec; i++) res[r * perRec + i] = Decode(span, i, v.Type);
        }
        return res;
    }

    private double[] ReadH5(NetCdfVariable v, PureHDF.Selections.Selection? selection = null)
    {
        var d = _h5Datasets[v.Name];
        if (selection is not null) return ReadH5Selection(d, v, selection);
        return v.Type switch
        {
            NetCdfType.Float => d.Read<float[]>().Select(x => (double)x).ToArray(),
            NetCdfType.Double => d.Read<double[]>(),
            NetCdfType.Byte => d.Read<sbyte[]>().Select(x => (double)x).ToArray(),
            NetCdfType.UByte => d.Read<byte[]>().Select(x => (double)x).ToArray(),
            NetCdfType.Short => d.Read<short[]>().Select(x => (double)x).ToArray(),
            NetCdfType.UShort => d.Read<ushort[]>().Select(x => (double)x).ToArray(),
            NetCdfType.Int => d.Read<int[]>().Select(x => (double)x).ToArray(),
            NetCdfType.UInt => d.Read<uint[]>().Select(x => (double)x).ToArray(),
            NetCdfType.Int64 => d.Read<long[]>().Select(x => (double)x).ToArray(),
            NetCdfType.UInt64 => d.Read<ulong[]>().Select(x => (double)x).ToArray(),
            _ => throw new NotSupportedException($"Variable '{v.Name}' of type {v.Type} cannot be read as numbers.")
        };
    }

    private static double[] ReadH5Selection(PureHDF.IH5Dataset d, NetCdfVariable v, PureHDF.Selections.Selection sel) => v.Type switch
    {
        NetCdfType.Float => d.Read<float[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.Double => d.Read<double[]>(sel),
        NetCdfType.Byte => d.Read<sbyte[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.UByte => d.Read<byte[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.Short => d.Read<short[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.UShort => d.Read<ushort[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.Int => d.Read<int[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.UInt => d.Read<uint[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.Int64 => d.Read<long[]>(sel).Select(x => (double)x).ToArray(),
        NetCdfType.UInt64 => d.Read<ulong[]>(sel).Select(x => (double)x).ToArray(),
        _ => throw new NotSupportedException($"Variable '{v.Name}' of type {v.Type} cannot be read as numbers.")
    };

    /// <summary>
    /// Reads an N-dimensional window (start/count per dimension) with CF unpacking. NetCDF-4 uses an HDF5 hyperslab (only the needed chunks
    /// are decompressed); classic files read contiguous runs from the memory map.
    /// </summary>
    public double[] ReadWindow(string name, int[] start, int[] count)
    {
        var v = Variable(name);
        int nd = v.Shape.Count;
        if (start.Length != nd || count.Length != nd) throw new ArgumentException($"'{name}' has {nd} dimensions.");
        for (int i = 0; i < nd; i++)
            if (start[i] < 0 || count[i] < 0 || start[i] + count[i] > v.Shape[i]) throw new ArgumentOutOfRangeException(nameof(start), $"Window exceeds dimension '{v.Dimensions[i]}' (length {v.Shape[i]}).");
        long total = count.Aggregate(1L, (a, b) => a * b);
        double[] res;
        if (_h5 is not null)
        {
            var sel = new PureHDF.Selections.HyperslabSelection(nd, start.Select(x => (ulong)x).ToArray(), count.Select(x => (ulong)x).ToArray());
            res = ReadH5(v, sel);
        }
        else
        {
            res = new double[total];
            int es = ElemSize(v.Type);
            // strides in elements (record dimension handled through the record size)
            var stride = new long[nd];
            long acc = 1;
            for (int i = nd - 1; i >= 0; i--) { stride[i] = acc; acc *= v.Shape[i]; }
            int last = nd - 1;
            int run = nd == 0 ? 1 : count[last];
            var idx = new int[nd];
            long k = 0;
            while (k < total)
            {
                long elem = 0, byteBase = v.Begin;
                for (int i = 0; i < nd; i++)
                {
                    int pos = start[i] + idx[i];
                    if (i == 0 && v.IsRecord) byteBase += pos * _recSize;
                    else elem += pos * (v.IsRecord ? stride[i] : stride[i]);
                }
                if (nd > 0 && !v.IsRecord) elem -= 0;
                var span = _file!.Span(byteBase + elem * es, run * es);
                for (int j = 0; j < run; j++) res[k + j] = Decode(span, j, v.Type);
                k += run;
                // advance the multi-index (excluding the last dimension)
                for (int i = nd - 2; i >= 0; i--)
                {
                    if (++idx[i] < count[i]) break;
                    idx[i] = 0;
                }
            }
        }
        ApplyCf(v, res);
        return res;
    }

    /// <summary>Values with CF conventions applied (fill/missing/valid range → NaN, then scale_factor and add_offset).</summary>
    public double[] Read(string name)
    {
        var v = Variable(name);
        var raw = ReadRaw(name);
        ApplyCf(v, raw);
        return raw;
    }

    /// <summary>
    /// One 2-D slice (the last two dimensions) at fixed indices for the leading dimensions (e.g. time, level),
    /// with CF unpacking. Classic files read only the slice's bytes from the memory map.
    /// </summary>
    public double[] ReadSlice(string name, params int[] leading)
    {
        var v = Variable(name);
        int nd = v.Shape.Count;
        if (nd < 2) throw new ArgumentException($"Variable '{name}' has {nd} dimension(s); a slice needs ≥ 2.");
        if (leading.Length != nd - 2) throw new ArgumentException($"Variable '{name}' needs {nd - 2} leading index(es) ({string.Join(", ", v.Dimensions.Take(nd - 2))}).");
        for (int i = 0; i < leading.Length; i++)
            if (leading[i] < 0 || leading[i] >= v.Shape[i]) throw new ArgumentOutOfRangeException(nameof(leading), $"Index {leading[i]} is outside dimension '{v.Dimensions[i]}' (length {v.Shape[i]}).");
        long ny = v.Shape[nd - 2], nx = v.Shape[nd - 1];
        long slab = ny * nx;
        double[] res;
        if (_h5 is not null)
        {
            var starts = leading.Select(x => (ulong)x).Concat([0UL, 0UL]).ToArray();
            var blocks = Enumerable.Repeat(1UL, leading.Length).Concat([(ulong)ny, (ulong)nx]).ToArray();
            res = ReadH5(v, new PureHDF.Selections.HyperslabSelection(nd, starts, blocks));
        }
        else
        {
            int es = ElemSize(v.Type);
            long elemOff = 0, stride = slab, byteBase = v.Begin;
            int first = 0;
            if (v.IsRecord) { byteBase += leading[0] * _recSize; first = 1; }
            for (int i = leading.Length - 1; i >= first; i--) { elemOff += leading[i] * stride; stride *= v.Shape[i]; }
            var span = _file!.Span(byteBase + elemOff * es, checked((int)(slab * es)));
            res = new double[slab];
            for (int i = 0; i < slab; i++) res[i] = Decode(span, i, v.Type);
        }
        ApplyCf(v, res);
        return res;
    }

    private static double? Attr(NetCdfVariable v, string key) => v.Attributes.TryGetValue(key, out var o) ? o switch
    {
        double d => d, long l => l, double[] a when a.Length > 0 => a[0], long[] a when a.Length > 0 => a[0], _ => null
    } : null;

    private static void ApplyCf(NetCdfVariable v, double[] data)
    {
        double? fill = Attr(v, "_FillValue"), missing = Attr(v, "missing_value");
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        if (v.Attributes.TryGetValue("valid_range", out var vr))
        {
            if (vr is double[] dr && dr.Length == 2) { lo = dr[0]; hi = dr[1]; }
            else if (vr is long[] lr && lr.Length == 2) { lo = lr[0]; hi = lr[1]; }
        }
        if (Attr(v, "valid_min") is { } vmin) lo = vmin;
        if (Attr(v, "valid_max") is { } vmax) hi = vmax;
        // classic default fill values (when the attribute is absent)
        fill ??= v.Type switch { NetCdfType.Float => 9.9692099683868690e+36f, NetCdfType.Double => 9.9692099683868690e+36, NetCdfType.Short => -32767, NetCdfType.Int => -2147483647, _ => null };
        double scale = Attr(v, "scale_factor") ?? 1, offset = Attr(v, "add_offset") ?? 0;
        bool isFloat = v.Type is NetCdfType.Float;
        for (int i = 0; i < data.Length; i++)
        {
            double x = data[i];
            if (double.IsNaN(x)) continue;
            if (fill is { } f && (isFloat ? (float)x == (float)f : x == f) || missing is { } m && x == m || x < lo || x > hi) { data[i] = double.NaN; continue; }
            data[i] = x * scale + offset;
        }
    }

    // ================================================================== CF coordinates, time, raster

    /// <summary>Decoded CF time coordinate ("&lt;unit&gt; since &lt;date&gt;", gregorian/standard calendar), or null when there is none.</summary>
    public DateTime[]? Times(string? variable = null)
    {
        var tv = variable is not null ? Variable(variable)
            : _vars.FirstOrDefault(v => v.Dimensions.Count == 1 && (v.Name is "time" or "t" || (v.Attributes.TryGetValue("axis", out var ax) && ax as string == "T") || (v.Units?.Contains(" since ", StringComparison.OrdinalIgnoreCase) ?? false)));
        if (tv?.Units is not { } units) return null;
        var parts = units.Split(" since ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        double unitSeconds = parts[0].ToLowerInvariant() switch
        {
            "seconds" or "second" or "s" or "secs" => 1, "minutes" or "minute" or "min" or "mins" => 60,
            "hours" or "hour" or "h" or "hr" or "hrs" => 3600, "days" or "day" or "d" => 86400, _ => double.NaN
        };
        if (double.IsNaN(unitSeconds)) return null;
        var epochText = parts[1].Replace('T', ' ').TrimEnd('Z', 'z').Trim();
        if (epochText.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase)) epochText = epochText[..^4];
        if (!DateTime.TryParse(epochText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var epoch)) return null;
        epoch = DateTime.SpecifyKind(epoch, DateTimeKind.Utc);
        return ReadRaw(tv.Name).Select(x => epoch.AddSeconds(x * unitSeconds)).ToArray();
    }

    private NetCdfVariable? Coordinate(string dim) => _vars.FirstOrDefault(v => v.Name == dim && v.Dimensions.Count == 1);

    /// <summary>
    /// Converts one 2-D slice (or, with <paramref name="leading"/> omitted on a 3-D variable, every time step as a band) to a north-up <see cref="Raster"/>.
    /// The grid must be regular; the CRS is WGS 84 for lat/lon grids or comes from the CF <c>grid_mapping</c> (crs_wkt / spatial_ref / epsg_code).
    /// </summary>
    public Raster ToRaster(string variable, params int[] leading)
    {
        var v = Variable(variable);
        int nd = v.Shape.Count;
        if (nd < 2) throw new ArgumentException($"Variable '{variable}' is not gridded.");
        var yDim = v.Dimensions[nd - 2]; var xDim = v.Dimensions[nd - 1];
        int ny = (int)v.Shape[nd - 2], nx = (int)v.Shape[nd - 1];
        var xs = Coordinate(xDim) is { } xc ? ReadRaw(xc.Name) : Enumerable.Range(0, nx).Select(i => (double)i).ToArray();
        var ys = Coordinate(yDim) is { } yc ? ReadRaw(yc.Name) : Enumerable.Range(0, ny).Select(i => (double)i).ToArray();
        double dx = nx > 1 ? (xs[^1] - xs[0]) / (nx - 1) : 1;
        double dy = ny > 1 ? (ys[^1] - ys[0]) / (ny - 1) : 1;
        for (int i = 1; i < nx; i++) if (Math.Abs(xs[i] - xs[i - 1] - dx) > Math.Abs(dx) * 1e-3) throw new NotSupportedException($"'{xDim}' is not regularly spaced; regrid first.");
        for (int i = 1; i < ny; i++) if (Math.Abs(ys[i] - ys[i - 1] - dy) > Math.Abs(dy) * 1e-3) throw new NotSupportedException($"'{yDim}' is not regularly spaced; regrid first.");
        bool flipY = dy > 0; // south → north rows: flip to north-up
        bool flipX = dx < 0;

        List<(double[] Data, string Name)> slices = [];
        if (leading.Length == nd - 2) slices.Add((ReadSlice(variable, leading), variable));
        else if (leading.Length == 0 && nd == 3)
        {
            var times = Times();
            for (int t = 0; t < v.Shape[0]; t++)
                slices.Add((ReadSlice(variable, t), times is not null && t < times.Length ? times[t].ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : $"{v.Dimensions[0]}={t}"));
        }
        else throw new ArgumentException($"Variable '{variable}' has dimensions ({string.Join(", ", v.Dimensions)}); pass {nd - 2} leading index(es).");

        var bands = new float[slices.Count][];
        for (int b = 0; b < slices.Count; b++)
        {
            var src = slices[b].Data;
            var dst = new float[nx * ny];
            for (int r = 0; r < ny; r++)
            {
                int sr = flipY ? ny - 1 - r : r;
                for (int c = 0; c < nx; c++)
                {
                    int sc = flipX ? nx - 1 - c : c;
                    dst[r * nx + c] = (float)src[sr * nx + sc];
                }
            }
            bands[b] = dst;
        }
        double cellX = Math.Abs(dx), cellY = Math.Abs(dy);
        double west = Math.Min(xs[0], xs[^1]) - cellX / 2, north = Math.Max(ys[0], ys[^1]) + cellY / 2;
        var transform = new GeoTransform(west, cellX, 0, north, 0, -cellY);
        var raster = new Raster(nx, ny, bands, transform, GridCrs(v, xDim, yDim), float.NaN)
        {
            BandNames = slices.Select(s => s.Name).ToArray()
        };
        return raster;
    }

    private CoordinateReferenceSystem? GridCrs(NetCdfVariable v, string xDim, string yDim)
    {
        if (v.Attributes.TryGetValue("grid_mapping", out var gm) && gm is string gmName && _vars.FirstOrDefault(x => x.Name == gmName) is { } g)
        {
            foreach (var key in new[] { "crs_wkt", "spatial_ref", "proj4", "proj4text", "proj4_params" })
                if (g.Attributes.TryGetValue(key, out var def) && def is string s && CoordinateReferenceSystem.TryParse(s, out var crs)) return crs;
            if (g.Attributes.TryGetValue("epsg_code", out var ec) && ec is string es && CoordinateReferenceSystem.TryParse(es, out var c2)) return c2;
            if (g.Attributes.TryGetValue("grid_mapping_name", out var gmn) && gmn as string == "latitude_longitude") return CoordinateReferenceSystem.Wgs84;
        }
        bool IsLon(string d) => d.StartsWith("lon", StringComparison.OrdinalIgnoreCase) || Coordinate(d)?.Units is "degrees_east" or "degree_east" or "degrees_E";
        bool IsLat(string d) => d.StartsWith("lat", StringComparison.OrdinalIgnoreCase) || Coordinate(d)?.Units is "degrees_north" or "degree_north" or "degrees_N";
        return IsLon(xDim) && IsLat(yDim) ? CoordinateReferenceSystem.Wgs84 : null;
    }

    /// <summary>2-D latitude/longitude coordinate variables of a curvilinear variable (CF <c>coordinates</c> attribute), or null.</summary>
    public (NetCdfVariable Lon, NetCdfVariable Lat)? CurvilinearCoordinates(string variable)
    {
        var v = Variable(variable);
        var names = v.Attributes.TryGetValue("coordinates", out var c) && c is string cs ? cs.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        var candidates = names.Select(n => _vars.FirstOrDefault(x => x.Name == n)).Where(x => x is { Dimensions.Count: 2 }).ToList();
        candidates.AddRange(_vars.Where(x => x.Dimensions.Count == 2 && (x.Name is "lat" or "lon" or "latitude" or "longitude" or "nav_lat" or "nav_lon" or "XLAT" or "XLONG")));
        bool IsLat(NetCdfVariable x) => x.Units is "degrees_north" or "degree_north" || x.Name.Contains("lat", StringComparison.OrdinalIgnoreCase);
        bool IsLon(NetCdfVariable x) => x.Units is "degrees_east" or "degree_east" || x.Name.Contains("lon", StringComparison.OrdinalIgnoreCase);
        var lat = candidates.FirstOrDefault(x => x is not null && IsLat(x));
        var lon = candidates.FirstOrDefault(x => x is not null && IsLon(x));
        return lat is not null && lon is not null ? (lon, lat) : null;
    }

    /// <summary>
    /// Cell values as points (lon/lat from 1-D or curvilinear 2-D coordinates), one row per cell, with the value in a column named after
    /// the variable. NaN cells are dropped.
    /// </summary>
    public GeoDataFrame ToPoints(string variable, params int[] leading)
    {
        var v = Variable(variable);
        int nd = v.Shape.Count;
        int ny = (int)v.Shape[nd - 2], nx = (int)v.Shape[nd - 1];
        var data = ReadSlice(variable, leading);
        double[] lons, lats;
        if (CurvilinearCoordinates(variable) is { } cc && cc.Lon.Size == (long)nx * ny)
        {
            lons = ReadRaw(cc.Lon.Name); lats = ReadRaw(cc.Lat.Name);
        }
        else
        {
            var xs = Coordinate(v.Dimensions[nd - 1]) is { } xc ? ReadRaw(xc.Name) : Enumerable.Range(0, nx).Select(i => (double)i).ToArray();
            var ys = Coordinate(v.Dimensions[nd - 2]) is { } yc ? ReadRaw(yc.Name) : Enumerable.Range(0, ny).Select(i => (double)i).ToArray();
            lons = new double[nx * ny]; lats = new double[nx * ny];
            for (int r = 0; r < ny; r++) for (int c = 0; c < nx; c++) { lons[r * nx + c] = xs[c]; lats[r * nx + c] = ys[r]; }
        }
        var keep = Enumerable.Range(0, data.Length).Where(i => !double.IsNaN(data[i])).ToArray();
        var geoms = keep.Select(i => (NetTopologySuite.Geometries.Geometry?)GeoPandas.Net.Geometry.Geo.Point(lons[i] > 180 ? lons[i] - 360 : lons[i], lats[i])).ToArray();
        return GeoDataFrame.FromGeometries(geoms, CoordinateReferenceSystem.Wgs84,
            Column.Create("row", keep.Select(i => (long)(i / nx)).ToArray()), Column.Create("col", keep.Select(i => (long)(i % nx)).ToArray()),
            Column.Create(variable, keep.Select(i => data[i]).ToArray()));
    }

    /// <summary>
    /// Resamples a curvilinear (or any) variable onto a regular WGS 84 grid by nearest neighbour (KD-tree), leaving cells farther than
    /// <paramref name="maxDistanceDeg"/> from any source cell empty.
    /// </summary>
    public Raster Regrid(string variable, double cellSizeDeg, double? maxDistanceDeg = null, params int[] leading)
    {
        var pts = ToPoints(variable, leading);
        if (pts.RowCount == 0) throw new InvalidOperationException($"'{variable}' has no valid cells.");
        var xs = pts.Geometry.Select(g => g!.Coordinate.X).ToArray(); var ys = pts.Geometry.Select(g => g!.Coordinate.Y).ToArray();
        var vals = pts[variable].ToDoubleArray();
        var tree = new GeoPandas.Net.Geometry.Index.KdTree(xs, ys);
        double west = Math.Floor(xs.Min() / cellSizeDeg) * cellSizeDeg, north = Math.Ceiling(ys.Max() / cellSizeDeg) * cellSizeDeg;
        int w = Math.Max(1, (int)Math.Ceiling((xs.Max() - west) / cellSizeDeg) + 1), h = Math.Max(1, (int)Math.Ceiling((north - ys.Min()) / cellSizeDeg) + 1);
        double maxD = maxDistanceDeg ?? cellSizeDeg * 1.5;
        var band = new float[w * h];
        Parallel.For(0, h, r =>
        {
            for (int c = 0; c < w; c++)
            {
                double x = west + (c + 0.5) * cellSizeDeg, y = north - (r + 0.5) * cellSizeDeg;
                var nn = tree.Nearest(x, y, 1);
                band[r * w + c] = nn.Length > 0 && nn[0].Distance <= maxD ? (float)vals[nn[0].Index] : float.NaN;
            }
        });
        return new Raster(w, h, [band], GeoTransform.NorthUp(west, north, cellSizeDeg), CoordinateReferenceSystem.Wgs84, float.NaN) { BandNames = [variable] };
    }

    /// <summary>ncdump -h style summary.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"netcdf {System.IO.Path.GetFileName(Path)} {{  // {Format}");
        sb.AppendLine("dimensions:");
        foreach (var d in _dims) sb.AppendLine($"    {d.Name} = {(d.IsUnlimited ? $"UNLIMITED ; // ({d.Length} currently)" : d.Length + " ;")}");
        sb.AppendLine("variables:");
        foreach (var v in _vars)
        {
            sb.AppendLine($"    {v} ;");
            foreach (var (k, a) in v.Attributes) sb.AppendLine($"        {v.Name}:{k} = {FormatAttr(a)} ;");
        }
        if (_gatts.Count > 0)
        {
            sb.AppendLine("// global attributes:");
            foreach (var (k, a) in _gatts) sb.AppendLine($"        :{k} = {FormatAttr(a)} ;");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FormatAttr(object a) => a switch
    {
        string s => $"\"{s}\"",
        double d => d.ToString("G8", CultureInfo.InvariantCulture),
        double[] arr => string.Join(", ", arr.Select(x => x.ToString("G8", CultureInfo.InvariantCulture))),
        long[] arr => string.Join(", ", arr),
        _ => Convert.ToString(a, CultureInfo.InvariantCulture) ?? ""
    };
}

/// <summary>NetCDF convenience entry points and a CF-compliant classic (CDF-1/CDF-2) writer for rasters.</summary>
public static class NetCdf
{
    public static NetCdfDataset Open(string path) => NetCdfDataset.Open(path);

    /// <summary>Reads one variable as a raster (see <see cref="NetCdfDataset.ToRaster"/>).</summary>
    public static Raster ReadRaster(string path, string variable, params int[] leading)
    {
        using var nc = NetCdfDataset.Open(path);
        return nc.ToRaster(variable, leading);
    }

    /// <summary>
    /// Writes a raster as CF-1.8 NetCDF classic: <c>float var(time?, y|lat, x|lon)</c> with coordinate variables, <c>_FillValue</c>,
    /// and a <c>crs</c> grid-mapping variable carrying <c>crs_wkt</c>/<c>spatial_ref</c>. Multi-band rasters become a time series when
    /// <paramref name="times"/> is given, otherwise a <c>band</c> dimension. Uses CDF-2 (64-bit offsets) above 2 GiB.
    /// </summary>
    public static void WriteClassic(Raster raster, string path, string variable = "data", DateTime[]? times = null, string? units = null, string? longName = null)
    {
        int nx = raster.Width, ny = raster.Height, nb = raster.BandCount;
        bool geographic = raster.Crs?.IsGeographic ?? false;
        string xName = geographic ? "lon" : "x", yName = geographic ? "lat" : "y";
        bool hasLead = nb > 1 || times is not null;
        string leadName = times is not null ? "time" : "band";
        if (times is not null && times.Length != nb) throw new ArgumentException("times must have one entry per band.");
        long dataBytes = 4L * nx * ny * nb;
        int version = dataBytes + 8L * (nx + ny + nb) > int.MaxValue - 4096 ? 2 : 1;

        var dims = new List<(string Name, int Len)>();
        if (hasLead) dims.Add((leadName, nb));
        dims.Add((yName, ny)); dims.Add((xName, nx));
        int Did(string n) => dims.FindIndex(d => d.Name == n);

        var t = raster.Transform;
        var xs = Enumerable.Range(0, nx).Select(c => t.OriginX + (c + 0.5) * t.PixelWidth).ToArray();
        var ys = Enumerable.Range(0, ny).Select(r => t.OriginY + (r + 0.5) * t.PixelHeight).ToArray();

        var vars = new List<VarDef>();
        if (hasLead)
        {
            var leadAttrs = times is not null
                ? new List<(string, object)> { ("standard_name", "time"), ("units", "seconds since 1970-01-01 00:00:00"), ("calendar", "standard"), ("axis", "T") }
                : new List<(string, object)> { ("long_name", "band index") };
            var vals = times is not null ? times.Select(d => (d.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds).ToArray() : Enumerable.Range(1, nb).Select(i => (double)i).ToArray();
            vars.Add(new VarDef(leadName, [Did(leadName)], NetCdfType.Double, leadAttrs, vals, null));
        }
        vars.Add(new VarDef(yName, [Did(yName)], NetCdfType.Double, geographic
            ? [("standard_name", "latitude"), ("units", "degrees_north"), ("axis", "Y")]
            : [("standard_name", "projection_y_coordinate"), ("units", "m"), ("axis", "Y")], ys, null));
        vars.Add(new VarDef(xName, [Did(xName)], NetCdfType.Double, geographic
            ? [("standard_name", "longitude"), ("units", "degrees_east"), ("axis", "X")]
            : [("standard_name", "projection_x_coordinate"), ("units", "m"), ("axis", "X")], xs, null));
        if (raster.Crs is { } crs)
        {
            var wkt = crs.ToWkt();
            vars.Add(new VarDef("crs", [], NetCdfType.Int, [("grid_mapping_name", geographic ? "latitude_longitude" : "transverse_mercator"), ("crs_wkt", wkt), ("spatial_ref", wkt),
                ("GeoTransform", string.Join(" ", t.ToArray().Select(x => x.ToString("R", CultureInfo.InvariantCulture))))], [0], null));
        }
        var dataAttrs = new List<(string, object)> { ("_FillValue", (object)float.NaN) };
        if (longName is not null) dataAttrs.Add(("long_name", longName));
        if (units is not null) dataAttrs.Add(("units", units));
        if (raster.Crs is not null) dataAttrs.Add(("grid_mapping", "crs"));
        int[] dataDims = hasLead ? [Did(leadName), Did(yName), Did(xName)] : [Did(yName), Did(xName)];
        vars.Add(new VarDef(variable, dataDims, NetCdfType.Float, dataAttrs, null, raster));

        var globals = new List<(string, object)> { ("Conventions", "CF-1.8"), ("source", "GeoPandas.Net"), ("history", $"Created {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} by GeoPandas.Net") };

        // two passes: compute header size, then write
        byte[] header = Header(version, dims, globals, vars, 0);
        long cursor = header.Length;
        var begins = new long[vars.Count];
        for (int i = 0; i < vars.Count; i++) { begins[i] = cursor; cursor += Pad(vars[i].VSize(dims)); }
        header = Header(version, dims, globals, vars, 0, begins);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        fs.Write(header);
        Span<byte> b8 = stackalloc byte[8];
        for (int i = 0; i < vars.Count; i++)
        {
            var v = vars[i];
            long written = 0;
            if (v.Raster is { } r)
            {
                for (int band = 0; band < r.BandCount; band++)
                    foreach (var f in r.Bands[band]) { BinaryPrimitives.WriteSingleBigEndian(b8, r.IsNoData(f) ? float.NaN : f); fs.Write(b8[..4]); written += 4; }
            }
            else if (v.Type == NetCdfType.Int) foreach (var x in v.Values!) { BinaryPrimitives.WriteInt32BigEndian(b8, (int)x); fs.Write(b8[..4]); written += 4; }
            else foreach (var x in v.Values!) { BinaryPrimitives.WriteDoubleBigEndian(b8, x); fs.Write(b8); written += 8; }
            while (written % 4 != 0) { fs.WriteByte(0); written++; }
        }
    }

    private static long Pad(long n) => (n + 3) & ~3L;

    private sealed record VarDef(string Name, int[] Dims, NetCdfType Type, List<(string, object)> Attrs, double[]? Values, Raster? Raster)
    {
        public long VSize(List<(string Name, int Len)> dims) => NetCdfDataset.ElemSize(Type) * Dims.Aggregate(1L, (a, d) => a * dims[d].Len);
    }

    private static byte[] Header(int version, List<(string Name, int Len)> dims, List<(string, object)> globals, List<VarDef> vars, int numRecs, long[]? begins = null)
    {
        var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); ms.Write(b); }
        void Name(string s) { var b = Encoding.UTF8.GetBytes(s); U32((uint)b.Length); ms.Write(b); for (long i = b.Length; i < Pad(b.Length); i++) ms.WriteByte(0); }
        void Attrs(List<(string, object)> list)
        {
            if (list.Count == 0) { U32(0); U32(0); return; }
            U32(0x0C); U32((uint)list.Count);
            foreach (var (k, v) in list)
            {
                Name(k);
                switch (v)
                {
                    case string s:
                        var b = Encoding.UTF8.GetBytes(s);
                        U32((uint)NetCdfType.Char); U32((uint)b.Length); ms.Write(b);
                        for (long i = b.Length; i < Pad(b.Length); i++) ms.WriteByte(0);
                        break;
                    case float f:
                        U32((uint)NetCdfType.Float); U32(1);
                        var fb = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(fb, f); ms.Write(fb);
                        break;
                    case double d:
                        U32((uint)NetCdfType.Double); U32(1);
                        var db = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(db, d); ms.Write(db);
                        break;
                    default: throw new NotSupportedException(v.GetType().Name);
                }
            }
        }
        ms.Write("CDF"u8); ms.WriteByte((byte)version);
        U32((uint)numRecs);
        U32(0x0A); U32((uint)dims.Count);
        foreach (var (n, l) in dims) { Name(n); U32((uint)l); }
        Attrs(globals);
        U32(0x0B); U32((uint)vars.Count);
        for (int i = 0; i < vars.Count; i++)
        {
            var v = vars[i];
            Name(v.Name);
            U32((uint)v.Dims.Length);
            foreach (var d in v.Dims) U32((uint)d);
            Attrs(v.Attrs);
            U32((uint)v.Type);
            U32((uint)Math.Min(uint.MaxValue, Pad(v.VSize(dims))));
            long begin = begins?[i] ?? 0;
            if (version == 1) U32((uint)begin); else U64((ulong)begin);
        }
        return ms.ToArray();
    }
}
