using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Electrical.Pmu.C37118;

/// <summary>
/// Encoder/decoder for IEEE C37.118.2-2011 frames (network byte order). Every frame is
/// SYNC(2) FRAMESIZE(2) IDCODE(2) SOC(4) FRACSEC(4) … CHK(2) where CHK is <see cref="CrcCcitt"/> over all preceding bytes.
/// Data frames need the configuration of the stream to be decoded.
/// </summary>
public static class C37Codec
{
    /// <summary>First SYNC byte.</summary>
    public const byte SyncByte = 0xAA;
    /// <summary>Common header length (SYNC … FRACSEC) [bytes].</summary>
    public const int HeaderLength = 14;
    /// <summary>Largest frame allowed by the 16-bit FRAMESIZE [bytes].</summary>
    public const int MaxFrameLength = 65535;

    // ---------------- common ----------------

    /// <summary>Frame type of a raw frame (from the second SYNC byte).</summary>
    public static C37FrameType PeekType(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2 || frame[0] != SyncByte) throw new InvalidDataException("Not a C37.118 frame (SYNC 0xAA expected).");
        int t = (frame[1] >> 4) & 0x07;
        if (t > 5) throw new InvalidDataException($"Unknown C37.118 frame type {t}.");
        return (C37FrameType)t;
    }

    /// <summary>IDCODE of a raw frame.</summary>
    public static ushort PeekIdCode(ReadOnlySpan<byte> frame) => BinaryPrimitives.ReadUInt16BigEndian(frame[4..]);

    /// <summary>True when FRAMESIZE matches and CHK equals the CRC of the preceding bytes.</summary>
    public static bool IsValid(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength + 2 || frame[0] != SyncByte) return false;
        int size = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        if (size != frame.Length) return false;
        return CrcCcitt.Compute(frame[..^2]) == BinaryPrimitives.ReadUInt16BigEndian(frame[^2..]);
    }

    private static void Check(ReadOnlySpan<byte> frame, C37FrameType expected)
    {
        var t = PeekType(frame);
        if (t != expected) throw new InvalidDataException($"Expected a {expected} frame, got {t}.");
        if (frame.Length < HeaderLength + 2 || BinaryPrimitives.ReadUInt16BigEndian(frame[2..]) != frame.Length)
            throw new InvalidDataException("FRAMESIZE does not match the frame length.");
        if (CrcCcitt.Compute(frame[..^2]) != BinaryPrimitives.ReadUInt16BigEndian(frame[^2..]))
            throw new InvalidDataException("C37.118 CHK (CRC-CCITT) mismatch.");
    }

    private static void WriteHeader(Writer w, C37FrameType type, byte version, ushort idcode, C37Time time)
    {
        w.U8(SyncByte);
        w.U8((byte)(((byte)type << 4) | (version & 0x0F)));
        w.U16(0); // FRAMESIZE patched in Finish
        w.U16(idcode);
        w.U32(time.Soc);
        w.U32(time.FracSecField);
    }

    private static (ushort IdCode, C37Time Time, byte Version) ReadHeader(Reader r)
    {
        r.Skip(1);
        byte version = (byte)(r.U8() & 0x0F);
        r.Skip(2);
        ushort id = r.U16();
        uint soc = r.U32();
        uint frac = r.U32();
        return (id, C37Time.FromFields(soc, frac), version);
    }

    // ---------------- command ----------------

    public static byte[] Encode(C37CommandFrame f)
    {
        var w = new Writer(HeaderLength + 4 + (f.ExtendedData?.Length ?? 0));
        WriteHeader(w, C37FrameType.Command, f.Version, f.IdCode, f.Time);
        w.U16((ushort)f.Command);
        if (f.ExtendedData is { } ext) w.Bytes(ext);
        return w.Finish();
    }

    public static C37CommandFrame DecodeCommand(ReadOnlySpan<byte> frame)
    {
        Check(frame, C37FrameType.Command);
        var r = new Reader(frame.ToArray());
        var (id, time, ver) = ReadHeader(r);
        var cmd = (C37Command)r.U16();
        byte[]? ext = frame.Length - 2 > r.Position ? frame[r.Position..^2].ToArray() : null;
        return new C37CommandFrame(id, time, cmd, ext) { Version = ver };
    }

    // ---------------- header ----------------

    public static byte[] Encode(C37HeaderFrame f)
    {
        byte[] text = Encoding.ASCII.GetBytes(f.Text);
        var w = new Writer(HeaderLength + 2 + text.Length);
        WriteHeader(w, C37FrameType.Header, f.Version, f.IdCode, f.Time);
        w.Bytes(text);
        return w.Finish();
    }

    public static C37HeaderFrame DecodeHeader(ReadOnlySpan<byte> frame)
    {
        Check(frame, C37FrameType.Header);
        var r = new Reader(frame.ToArray());
        var (id, time, ver) = ReadHeader(r);
        return new C37HeaderFrame(id, time, Encoding.ASCII.GetString(frame[r.Position..^2])) { Version = ver };
    }

    // ---------------- configuration ----------------

    public static byte[] Encode(C37ConfigFrame c)
    {
        if (c.Type is not (C37FrameType.Config1 or C37FrameType.Config2 or C37FrameType.Config3))
            throw new ArgumentException("Configuration frame type must be Config1, Config2 or Config3.");
        bool v3 = c.Type == C37FrameType.Config3;
        var w = new Writer(256);
        WriteHeader(w, c.Type, c.Version, c.IdCode, c.Time);
        if (v3) w.U16(c.ContinuationIndex);
        w.U32(c.TimeBase & 0xFFFFFF);
        w.U16((ushort)c.Pmus.Count);
        foreach (var p in c.Pmus)
        {
            if (v3) w.Name3(p.StationName); else w.Name16(p.StationName);
            w.U16(p.IdCode);
            if (v3) w.Bytes(p.GlobalPmuId.ToByteArray(bigEndian: true));
            w.U16((ushort)p.Format);
            w.U16((ushort)p.Phasors.Count);
            w.U16((ushort)p.Analogs.Count);
            w.U16((ushort)p.Digitals.Count);
            void Name(string s) { if (v3) w.Name3(s); else w.Name16(s); }
            foreach (var ph in p.Phasors) Name(ph.Name);
            foreach (var an in p.Analogs) Name(an.Name);
            foreach (var dg in p.Digitals)
                for (int i = 0; i < 16; i++) Name(i < dg.Names.Count ? dg.Names[i] : "");
            if (v3)
            {
                foreach (var ph in p.Phasors)
                {
                    w.U16(0); // modification flags
                    w.U8((byte)(((byte)ph.Kind << 3) | (ph.Component & 0x07)));
                    w.U8(0); // user designation
                    w.F32(ph.MagnitudeScale);
                    w.F32(ph.AngleOffsetRad);
                }
                foreach (var an in p.Analogs) { w.F32(an.ScaleFactor); w.F32(an.Offset); }
            }
            else
            {
                foreach (var ph in p.Phasors) w.U32(((uint)ph.Kind << 24) | (ph.ScaleFactor & 0xFFFFFF));
                foreach (var an in p.Analogs) w.U32(((uint)an.Kind << 24) | ((uint)an.ScaleFactor & 0xFFFFFF));
            }
            foreach (var dg in p.Digitals) { w.U16(dg.NormalMask); w.U16(dg.ValidMask); }
            if (v3)
            {
                w.F32(p.Latitude); w.F32(p.Longitude); w.F32(p.Elevation);
                w.U8((byte)p.ServiceClass);
                w.U32((uint)p.WindowMicroseconds);
                w.U32((uint)p.GroupDelayMicroseconds);
            }
            w.U16((ushort)(p.NominalFrequencyHz == 50 ? 1 : 0));
            w.U16(p.ConfigCount);
        }
        w.U16((ushort)c.DataRate);
        return w.Finish();
    }

    public static C37ConfigFrame DecodeConfig(ReadOnlySpan<byte> frame)
    {
        var type = PeekType(frame);
        if (type is not (C37FrameType.Config1 or C37FrameType.Config2 or C37FrameType.Config3))
            throw new InvalidDataException($"Expected a configuration frame, got {type}.");
        Check(frame, type);
        bool v3 = type == C37FrameType.Config3;
        var r = new Reader(frame.ToArray());
        var (id, time, ver) = ReadHeader(r);
        ushort cont = v3 ? r.U16() : (ushort)0;
        uint timeBase = r.U32() & 0xFFFFFF;
        int n = r.U16();
        var pmus = new List<PmuConfiguration>(n);
        for (int k = 0; k < n; k++)
        {
            string stn = v3 ? r.Name3() : r.Name16();
            ushort pid = r.U16();
            Guid gid = v3 ? new Guid(r.Span(16), bigEndian: true) : Guid.Empty;
            var fmt = (C37DataFormat)(r.U16() & 0x0F);
            int ph = r.U16(), an = r.U16(), dg = r.U16();
            string Name() => v3 ? r.Name3() : r.Name16();
            var phNames = new string[ph];
            for (int i = 0; i < ph; i++) phNames[i] = Name();
            var anNames = new string[an];
            for (int i = 0; i < an; i++) anNames[i] = Name();
            var dgNames = new string[dg][];
            for (int d = 0; d < dg; d++)
            {
                dgNames[d] = new string[16];
                for (int i = 0; i < 16; i++) dgNames[d][i] = Name();
            }
            var phasors = new PhasorChannel[ph];
            var analogs = new AnalogChannel[an];
            if (v3)
            {
                for (int i = 0; i < ph; i++)
                {
                    r.Skip(2);
                    byte typ = r.U8();
                    r.Skip(1);
                    float scale = r.F32(), angle = r.F32();
                    phasors[i] = new PhasorChannel(phNames[i], (PhasorKind)((typ >> 3) & 1))
                    { MagnitudeScale = scale, AngleOffsetRad = angle, Component = (byte)(typ & 0x07) };
                }
                for (int i = 0; i < an; i++)
                {
                    float scale = r.F32(), off = r.F32();
                    analogs[i] = new AnalogChannel(anNames[i], AnalogKind.PointOnWave, (int)scale) { Offset = off };
                }
            }
            else
            {
                for (int i = 0; i < ph; i++)
                {
                    uint u = r.U32();
                    phasors[i] = new PhasorChannel(phNames[i], (PhasorKind)(u >> 24), u & 0xFFFFFF);
                }
                for (int i = 0; i < an; i++)
                {
                    uint u = r.U32();
                    int scale = (int)(u << 8) >> 8; // sign-extend 24 bits
                    analogs[i] = new AnalogChannel(anNames[i], (AnalogKind)(u >> 24), scale);
                }
            }
            var digitals = new DigitalWord[dg];
            for (int d = 0; d < dg; d++)
            {
                ushort normal = r.U16(), valid = r.U16();
                digitals[d] = new DigitalWord(dgNames[d], normal, valid);
            }
            float lat = float.NaN, lon = float.NaN, elev = float.NaN;
            char svc = 'P';
            int window = 0, delay = 0;
            if (v3)
            {
                lat = r.F32(); lon = r.F32(); elev = r.F32();
                svc = (char)r.U8();
                window = (int)r.U32(); delay = (int)r.U32();
            }
            double fnom = (r.U16() & 1) != 0 ? 50 : 60;
            ushort cfgcnt = r.U16();
            pmus.Add(new PmuConfiguration
            {
                StationName = stn, IdCode = pid, Format = fmt, Phasors = phasors, Analogs = analogs, Digitals = digitals,
                NominalFrequencyHz = fnom, ConfigCount = cfgcnt, GlobalPmuId = gid, Latitude = lat, Longitude = lon, Elevation = elev,
                ServiceClass = svc, WindowMicroseconds = window, GroupDelayMicroseconds = delay,
            });
        }
        short rate = (short)r.U16();
        return new C37ConfigFrame
        {
            Type = type, IdCode = id, Time = time, TimeBase = timeBase, Pmus = pmus, DataRate = rate, Version = ver, ContinuationIndex = cont,
        };
    }

    // ---------------- data ----------------

    /// <summary>Encodes a data frame using the formats and scale factors of <paramref name="config"/> (one block per configured PMU).</summary>
    public static byte[] Encode(C37DataFrame f, C37ConfigFrame config)
    {
        if (f.Blocks.Count != config.Pmus.Count) throw new ArgumentException($"Data frame has {f.Blocks.Count} blocks but the configuration has {config.Pmus.Count} PMUs.");
        int size = HeaderLength + 2 + config.Pmus.Sum(p => p.DataBlockSize);
        var w = new Writer(size);
        WriteHeader(w, C37FrameType.Data, f.Version, f.IdCode, f.Time);
        for (int k = 0; k < f.Blocks.Count; k++)
        {
            var p = config.Pmus[k];
            var b = f.Blocks[k];
            if (b.Phasors.Count != p.Phasors.Count || b.AnalogValues.Count != p.Analogs.Count || b.DigitalValues.Count != p.Digitals.Count)
                throw new ArgumentException($"Data block {k} does not match the channel counts of PMU {p.IdCode}.");
            w.U16(b.RawStatus);
            bool polar = (p.Format & C37DataFormat.PhasorPolar) != 0;
            bool phFloat = (p.Format & C37DataFormat.PhasorFloat) != 0;
            for (int i = 0; i < p.Phasors.Count; i++)
            {
                var ch = p.Phasors[i];
                var x = b.Phasors[i];
                if (phFloat)
                {
                    if (polar) { w.F32((float)x.Magnitude); w.F32((float)x.Phase); }
                    else { w.F32((float)x.Real); w.F32((float)x.Imaginary); }
                }
                else
                {
                    double res = ch.Resolution;
                    if (polar)
                    {
                        w.U16((ushort)Math.Clamp(Math.Round(x.Magnitude / res), 0, ushort.MaxValue));
                        w.U16((ushort)(short)Math.Clamp(Math.Round(x.Phase * 1e4), -31416, 31416));
                    }
                    else
                    {
                        w.U16((ushort)ClampI16(x.Real / res));
                        w.U16((ushort)ClampI16(x.Imaginary / res));
                    }
                }
            }
            if ((p.Format & C37DataFormat.FrequencyFloat) != 0) { w.F32((float)b.FrequencyHz); w.F32((float)b.RocofHzPerS); }
            else
            {
                w.U16((ushort)ClampI16((b.FrequencyHz - p.NominalFrequencyHz) * 1000));
                w.U16((ushort)ClampI16(b.RocofHzPerS * 100));
            }
            for (int i = 0; i < p.Analogs.Count; i++)
            {
                if ((p.Format & C37DataFormat.AnalogFloat) != 0) w.F32((float)b.AnalogValues[i]);
                else w.U16((ushort)ClampI16(b.AnalogValues[i] / (p.Analogs[i].ScaleFactor == 0 ? 1 : p.Analogs[i].ScaleFactor)));
            }
            for (int i = 0; i < p.Digitals.Count; i++) w.U16(b.DigitalValues[i]);
        }
        return w.Finish();
    }

    /// <summary>Decodes a data frame with the stream's configuration (CFG-2 or CFG-3).</summary>
    public static C37DataFrame DecodeData(ReadOnlySpan<byte> frame, C37ConfigFrame config)
    {
        Check(frame, C37FrameType.Data);
        int expected = HeaderLength + 2 + config.Pmus.Sum(p => p.DataBlockSize);
        if (frame.Length != expected) throw new InvalidDataException($"Data frame length {frame.Length} does not match the configuration ({expected} bytes).");
        var r = new Reader(frame.ToArray());
        var (id, time, ver) = ReadHeader(r);
        var blocks = new PmuDataBlock[config.Pmus.Count];
        for (int k = 0; k < blocks.Length; k++)
        {
            var p = config.Pmus[k];
            ushort stat = r.U16();
            bool polar = (p.Format & C37DataFormat.PhasorPolar) != 0;
            bool phFloat = (p.Format & C37DataFormat.PhasorFloat) != 0;
            var phasors = new Complex[p.Phasors.Count];
            for (int i = 0; i < phasors.Length; i++)
            {
                double res = p.Phasors[i].Resolution;
                if (phFloat)
                {
                    float a = r.F32(), c = r.F32();
                    phasors[i] = polar ? Complex.FromPolarCoordinates(a, c) : new Complex(a, c);
                }
                else if (polar)
                {
                    ushort mag = r.U16();
                    short ang = (short)r.U16();
                    phasors[i] = Complex.FromPolarCoordinates(mag * res, ang * 1e-4);
                }
                else
                {
                    short re = (short)r.U16(), im = (short)r.U16();
                    phasors[i] = new Complex(re * res, im * res);
                }
            }
            double freq, rocof;
            if ((p.Format & C37DataFormat.FrequencyFloat) != 0) { freq = r.F32(); rocof = r.F32(); }
            else { freq = p.NominalFrequencyHz + (short)r.U16() / 1000.0; rocof = (short)r.U16() / 100.0; }
            var analogs = new double[p.Analogs.Count];
            for (int i = 0; i < analogs.Length; i++)
                analogs[i] = (p.Format & C37DataFormat.AnalogFloat) != 0 ? r.F32() : (short)r.U16() * (double)(p.Analogs[i].ScaleFactor == 0 ? 1 : p.Analogs[i].ScaleFactor);
            var digitals = new ushort[p.Digitals.Count];
            for (int i = 0; i < digitals.Length; i++) digitals[i] = r.U16();
            blocks[k] = new PmuDataBlock((PmuStatus)(stat & 0xFE00), phasors, freq, rocof, analogs, digitals) { RawStatus = stat };
        }
        return new C37DataFrame(id, time, blocks) { Version = ver };
    }

    private static short ClampI16(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);

    // ---------------- stream framing ----------------

    /// <summary>
    /// Reads one complete frame from a stream (SYNC + FRAMESIZE, then the rest). Returns null at end of stream before the first byte.
    /// </summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var head = new byte[4];
        int got = await stream.ReadAtLeastAsync(head, 4, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new EndOfStreamException("Truncated C37.118 frame.");
        if (head[0] != SyncByte) throw new InvalidDataException("Lost C37.118 frame synchronisation (SYNC 0xAA expected).");
        int size = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
        if (size < HeaderLength + 2) throw new InvalidDataException($"Invalid FRAMESIZE {size}.");
        var frame = new byte[size];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), ct).ConfigureAwait(false);
        return frame;
    }

    // ---------------- helpers ----------------

    private sealed class Writer
    {
        private byte[] _buf;
        private int _pos;
        public Writer(int capacity) { _buf = new byte[Math.Max(capacity, 16)]; _pos = 0; }
        private Span<byte> Take(int n)
        {
            if (_pos + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
            var s = _buf.AsSpan(_pos, n);
            _pos += n;
            return s;
        }
        public void U8(byte v) => Take(1)[0] = v;
        public void U16(ushort v) => BinaryPrimitives.WriteUInt16BigEndian(Take(2), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32BigEndian(Take(4), v);
        public void F32(float v) => BinaryPrimitives.WriteSingleBigEndian(Take(4), v);
        public void Bytes(ReadOnlySpan<byte> b) => b.CopyTo(Take(b.Length));
        public void Name16(string s)
        {
            var d = Take(16);
            d.Fill((byte)' ');
            int n = Math.Min(16, s.Length);
            for (int i = 0; i < n; i++) d[i] = s[i] < 128 ? (byte)s[i] : (byte)'?';
        }
        public void Name3(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = Math.Min(255, bytes.Length);
            U8((byte)n);
            Bytes(bytes.AsSpan(0, n));
        }
        public byte[] Finish()
        {
            int total = _pos + 2;
            if (total > MaxFrameLength) throw new InvalidOperationException("C37.118 frame exceeds 65535 bytes.");
            BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(2), (ushort)total);
            ushort crc = CrcCcitt.Compute(_buf.AsSpan(0, _pos));
            U16(crc);
            return _buf.AsSpan(0, total).ToArray();
        }
    }

    private sealed class Reader(byte[] data)
    {
        private readonly byte[] _d = data;
        public int Position { get; private set; }
        private ReadOnlySpan<byte> Take(int n)
        {
            if (Position + n > _d.Length - 2) throw new InvalidDataException("Truncated C37.118 frame.");
            var s = _d.AsSpan(Position, n);
            Position += n;
            return s;
        }
        public void Skip(int n) => Take(n);
        public ReadOnlySpan<byte> Span(int n) => Take(n);
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
        public string Name16() => Encoding.ASCII.GetString(Take(16)).TrimEnd(' ', '\0');
        public string Name3() { int n = U8(); return Encoding.UTF8.GetString(Take(n)); }
    }
}
