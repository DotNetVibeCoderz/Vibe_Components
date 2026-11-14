using System.Buffers.Binary;

namespace Electrical.SCADA.Dnp3;

/// <summary>Application-layer function codes (IEEE 1815 Table 4-1, subset).</summary>
public enum Dnp3Function : byte
{
    Confirm = 0,
    Read = 1,
    Write = 2,
    Select = 3,
    Operate = 4,
    DirectOperate = 5,
    DirectOperateNoAck = 6,
    ColdRestart = 13,
    WarmRestart = 14,
    EnableUnsolicited = 20,
    DisableUnsolicited = 21,
    DelayMeasure = 23,
    RecordCurrentTime = 24,
    Response = 129,
    UnsolicitedResponse = 130,
}

/// <summary>Internal indications: low octet IIN1, high octet IIN2 (IEEE 1815 §4.2.2.7).</summary>
[Flags]
public enum Dnp3Iin : ushort
{
    None = 0,
    /// <summary>IIN1.0 broadcast message received.</summary>
    AllStations = 0x0001,
    /// <summary>IIN1.1 class 1 events available.</summary>
    Class1Events = 0x0002,
    /// <summary>IIN1.2 class 2 events available.</summary>
    Class2Events = 0x0004,
    /// <summary>IIN1.3 class 3 events available.</summary>
    Class3Events = 0x0008,
    /// <summary>IIN1.4 time synchronisation required.</summary>
    NeedTime = 0x0010,
    /// <summary>IIN1.5 some output points in local mode.</summary>
    LocalControl = 0x0020,
    /// <summary>IIN1.6 device trouble.</summary>
    DeviceTrouble = 0x0040,
    /// <summary>IIN1.7 device restarted (cleared by the master writing g80v1 index 7 = 0).</summary>
    DeviceRestart = 0x0080,
    /// <summary>IIN2.0 function code not supported.</summary>
    NoFunctionCodeSupport = 0x0100,
    /// <summary>IIN2.1 object unknown.</summary>
    ObjectUnknown = 0x0200,
    /// <summary>IIN2.2 parameter error (qualifier/range).</summary>
    ParameterError = 0x0400,
    /// <summary>IIN2.3 event buffer overflow.</summary>
    EventBufferOverflow = 0x0800,
    /// <summary>IIN2.4 operation already executing.</summary>
    AlreadyExecuting = 0x1000,
    /// <summary>IIN2.5 configuration corrupt.</summary>
    ConfigCorrupt = 0x2000,
}

/// <summary>Application control octet: FIR (bit 7), FIN (bit 6), CON (bit 5), UNS (bit 4), SEQ (4 bits).</summary>
public readonly record struct Dnp3AppControl(bool Fir, bool Fin, bool Con, bool Uns, byte Seq)
{
    public static Dnp3AppControl Single(byte seq, bool con = false, bool uns = false) => new(true, true, con, uns, (byte)(seq & 0x0F));
    public byte Encode() => (byte)((Fir ? 0x80 : 0) | (Fin ? 0x40 : 0) | (Con ? 0x20 : 0) | (Uns ? 0x10 : 0) | (Seq & 0x0F));
    public static Dnp3AppControl Decode(byte b) => new((b & 0x80) != 0, (b & 0x40) != 0, (b & 0x20) != 0, (b & 0x10) != 0, (byte)(b & 0x0F));
}

/// <summary>Application fragment: control, function, IIN (responses only) and the object headers/data.</summary>
public sealed record Dnp3Fragment(Dnp3AppControl Control, Dnp3Function Function, Dnp3Iin Iin, byte[] Objects)
{
    public bool IsResponse => Function is Dnp3Function.Response or Dnp3Function.UnsolicitedResponse;

    public static Dnp3Fragment Request(byte seq, Dnp3Function f, byte[]? objects = null) => new(Dnp3AppControl.Single(seq), f, Dnp3Iin.None, objects ?? []);

    public byte[] Encode()
    {
        int h = IsResponse ? 4 : 2;
        var b = new byte[h + Objects.Length];
        b[0] = Control.Encode();
        b[1] = (byte)Function;
        if (IsResponse) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Iin);
        Objects.CopyTo(b, h);
        return b;
    }

    public static Dnp3Fragment Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) throw new InvalidDataException("DNP3 application fragment too short.");
        var fc = (Dnp3Function)b[1];
        bool resp = fc is Dnp3Function.Response or Dnp3Function.UnsolicitedResponse;
        if (resp && b.Length < 4) throw new InvalidDataException("DNP3 response without IIN.");
        var iin = resp ? (Dnp3Iin)BinaryPrimitives.ReadUInt16LittleEndian(b[2..]) : Dnp3Iin.None;
        return new Dnp3Fragment(Dnp3AppControl.Decode(b[0]), fc, iin, b[(resp ? 4 : 2)..].ToArray());
    }
}

/// <summary>Object header qualifier codes (prefix code in bits 6–4, range specifier in bits 3–0).</summary>
public enum Dnp3Qualifier : byte
{
    /// <summary>1-octet start/stop indexes.</summary>
    Range8 = 0x00,
    /// <summary>2-octet start/stop indexes.</summary>
    Range16 = 0x01,
    /// <summary>All objects, no range field.</summary>
    All = 0x06,
    /// <summary>1-octet count, no prefix.</summary>
    Count8 = 0x07,
    /// <summary>2-octet count, no prefix.</summary>
    Count16 = 0x08,
    /// <summary>1-octet count, 1-octet index prefix.</summary>
    Prefix8Count8 = 0x17,
    /// <summary>2-octet count, 2-octet index prefix.</summary>
    Prefix16Count16 = 0x28,
}

/// <summary>One object instance: point index and its encoded value (for bit-packed variations: one octet 0/1).</summary>
public sealed record Dnp3ObjectItem(uint Index, byte[] Data);

/// <summary>A parsed object header with its items. For range qualifiers <see cref="Start"/>/<see cref="Stop"/> are set.</summary>
public sealed record Dnp3ObjectHeader(byte Group, byte Variation, Dnp3Qualifier Qualifier, uint Start, uint Stop, int Count, IReadOnlyList<Dnp3ObjectItem> Items)
{
    public override string ToString() => $"g{Group}v{Variation} q0x{(byte)Qualifier:X2} n={Count}";
}

/// <summary>Object sizes and header parsing.</summary>
public static class Dnp3ObjectParser
{
    /// <summary>Encoded size of one object [octets]; −1 = bit-packed (1 bit per point); 0 = no data; null = unsupported.</summary>
    public static int? DataSize(byte group, byte variation) => (group, variation) switch
    {
        (_, 0) => 0,
        (1, 1) or (10, 1) or (80, 1) => -1,
        (1, 2) or (2, 1) or (10, 2) => 1,
        (2, 2) => 7,
        (12, 1) => 11,
        (30, 1) or (30, 5) or (32, 1) or (32, 5) or (40, 1) or (40, 3) => 5,
        (30, 2) or (32, 2) or (40, 2) => 3,
        (32, 3) or (32, 7) => 11,
        (41, 1) or (41, 3) => 5,
        (41, 2) => 3,
        (50, 1) => 6,
        (52, 1) or (52, 2) => 2,
        (60, _) => 0,
        _ => null,
    };

    /// <summary>
    /// Parses the object part of a fragment. <paramref name="withData"/> is false for READ-style requests (headers only).
    /// Throws <see cref="NotSupportedException"/> for unknown objects and <see cref="InvalidDataException"/> for malformed data.
    /// </summary>
    public static List<Dnp3ObjectHeader> Parse(ReadOnlySpan<byte> b, bool withData)
    {
        var list = new List<Dnp3ObjectHeader>();
        int p = 0;
        while (p < b.Length)
        {
            if (p + 3 > b.Length) throw new InvalidDataException("Truncated DNP3 object header.");
            byte g = b[p], v = b[p + 1];
            var q = (Dnp3Qualifier)b[p + 2];
            p += 3;
            int size = DataSize(g, v) ?? throw new NotSupportedException($"DNP3 object g{g}v{v} not supported.");
            if (!withData) size = 0;
            uint start = 0, stop = 0;
            int count;
            int prefix = 0;
            switch (q)
            {
                case Dnp3Qualifier.Range8: Need(b, p, 2); start = b[p]; stop = b[p + 1]; p += 2; count = (int)(stop - start + 1); break;
                case Dnp3Qualifier.Range16: Need(b, p, 4); start = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); stop = BinaryPrimitives.ReadUInt16LittleEndian(b[(p + 2)..]); p += 4; count = (int)(stop - start + 1); break;
                case Dnp3Qualifier.All: count = 0; break;
                case Dnp3Qualifier.Count8: Need(b, p, 1); count = b[p]; p += 1; break;
                case Dnp3Qualifier.Count16: Need(b, p, 2); count = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); p += 2; break;
                case Dnp3Qualifier.Prefix8Count8: Need(b, p, 1); count = b[p]; p += 1; prefix = 1; break;
                case Dnp3Qualifier.Prefix16Count16: Need(b, p, 2); count = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); p += 2; prefix = 2; break;
                default: throw new InvalidDataException($"DNP3 qualifier 0x{(byte)q:X2} not supported.");
            }
            if (count < 0) throw new InvalidDataException("DNP3 range stop < start.");
            var items = new List<Dnp3ObjectItem>();
            if (size == -1)
            {
                if (prefix != 0) throw new InvalidDataException("Bit-packed objects cannot use index prefixes.");
                int bytes = (count + 7) / 8;
                Need(b, p, bytes);
                for (int i = 0; i < count; i++) items.Add(new((uint)(start + i), [(byte)((b[p + i / 8] >> (i % 8)) & 1)]));
                p += bytes;
            }
            else if (size > 0 || prefix > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    uint idx = start + (uint)i;
                    if (prefix == 1) { Need(b, p, 1); idx = b[p]; p += 1; }
                    else if (prefix == 2) { Need(b, p, 2); idx = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); p += 2; }
                    Need(b, p, size);
                    items.Add(new(idx, b.Slice(p, size).ToArray()));
                    p += size;
                }
            }
            list.Add(new Dnp3ObjectHeader(g, v, q, start, stop, count, items));
        }
        return list;
    }

    private static void Need(ReadOnlySpan<byte> b, int p, int n)
    {
        if (p + n > b.Length) throw new InvalidDataException("Truncated DNP3 object data.");
    }
}

/// <summary>Builds the object part of a fragment.</summary>
public sealed class Dnp3ObjectWriter
{
    private readonly List<byte> _b = [];

    public int Length => _b.Count;
    public byte[] ToArray() => [.. _b];

    private void Header(byte g, byte v, Dnp3Qualifier q) { _b.Add(g); _b.Add(v); _b.Add((byte)q); }
    private void U16(int x) { _b.Add((byte)x); _b.Add((byte)(x >> 8)); }

    /// <summary>Header with qualifier 0x06 (all objects), e.g. a class poll g60v2.</summary>
    public Dnp3ObjectWriter All(byte g, byte v) { Header(g, v, Dnp3Qualifier.All); return this; }

    /// <summary>Range header without data (READ of a range).</summary>
    public Dnp3ObjectWriter RangeHeader(byte g, byte v, uint start, uint stop)
    {
        if (stop <= 0xFF) { Header(g, v, Dnp3Qualifier.Range8); _b.Add((byte)start); _b.Add((byte)stop); }
        else { Header(g, v, Dnp3Qualifier.Range16); U16((int)start); U16((int)stop); }
        return this;
    }

    /// <summary>Contiguous range with data (qualifier 0x00 when indexes fit one octet, else 0x01).</summary>
    public Dnp3ObjectWriter Range(byte g, byte v, uint start, IReadOnlyList<byte[]> items)
    {
        RangeHeader(g, v, start, start + (uint)items.Count - 1);
        foreach (var it in items) _b.AddRange(it);
        return this;
    }

    /// <summary>Bit-packed range (g1v1, g10v1, g80v1).</summary>
    public Dnp3ObjectWriter PackedRange(byte g, byte v, uint start, IReadOnlyList<bool> bits)
    {
        RangeHeader(g, v, start, start + (uint)bits.Count - 1);
        var packed = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) packed[i / 8] |= (byte)(1 << (i % 8));
        _b.AddRange(packed);
        return this;
    }

    /// <summary>Index-prefixed objects (qualifier 0x17 when all indexes and the count fit one octet, else 0x28).</summary>
    public Dnp3ObjectWriter Prefixed(byte g, byte v, IReadOnlyList<(uint Index, byte[] Data)> items, bool forceWide = false)
    {
        bool narrow = !forceWide && items.Count <= 0xFF && items.All(i => i.Index <= 0xFF);
        if (narrow) { Header(g, v, Dnp3Qualifier.Prefix8Count8); _b.Add((byte)items.Count); }
        else { Header(g, v, Dnp3Qualifier.Prefix16Count16); U16(items.Count); }
        foreach (var (idx, data) in items)
        {
            if (narrow) _b.Add((byte)idx); else U16((int)idx);
            _b.AddRange(data);
        }
        return this;
    }

    /// <summary>Count header (qualifier 0x07/0x08) followed by the objects, e.g. g50v1 time.</summary>
    public Dnp3ObjectWriter Count(byte g, byte v, IReadOnlyList<byte[]> items)
    {
        if (items.Count <= 0xFF) { Header(g, v, Dnp3Qualifier.Count8); _b.Add((byte)items.Count); }
        else { Header(g, v, Dnp3Qualifier.Count16); U16(items.Count); }
        foreach (var it in items) _b.AddRange(it);
        return this;
    }

    /// <summary>Count header without data, limiting the number of events returned by a class/event READ.</summary>
    public Dnp3ObjectWriter CountHeader(byte g, byte v, int count)
    {
        if (count <= 0xFF) { Header(g, v, Dnp3Qualifier.Count8); _b.Add((byte)count); }
        else { Header(g, v, Dnp3Qualifier.Count16); U16(count); }
        return this;
    }

    public Dnp3ObjectWriter Raw(ReadOnlySpan<byte> bytes) { foreach (var x in bytes) _b.Add(x); return this; }
}

/// <summary>Point quality flags shared by binary and analog objects; <see cref="State"/> (bit 7) carries the value of binaries.</summary>
[Flags]
public enum Dnp3Flags : byte
{
    None = 0,
    Online = 0x01,
    Restart = 0x02,
    CommLost = 0x04,
    RemoteForced = 0x08,
    LocalForced = 0x10,
    /// <summary>Bit 5: chatter filter (binaries) / over-range (analogs).</summary>
    ChatterOrOverRange = 0x20,
    /// <summary>Bit 6: reference error (analogs).</summary>
    ReferenceError = 0x40,
    /// <summary>Bit 7: binary state.</summary>
    State = 0x80,
}

/// <summary>CROB operation type (control code bits 0–3).</summary>
public enum Dnp3OpType : byte { Nul = 0, PulseOn = 1, PulseOff = 2, LatchOn = 3, LatchOff = 4 }

/// <summary>CROB trip/close code (control code bits 6–7).</summary>
public enum Dnp3TripClose : byte { Nul = 0, Close = 1, Trip = 2 }

/// <summary>Command status codes (IEEE 1815 Table 11-7, subset).</summary>
public enum Dnp3CommandStatus : byte
{
    Success = 0,
    Timeout = 1,
    NoSelect = 2,
    FormatError = 3,
    NotSupported = 4,
    AlreadyActive = 5,
    HardwareError = 6,
    Local = 7,
    TooManyObjects = 8,
    NotAuthorized = 9,
    AutomationInhibit = 10,
    ProcessingLimited = 11,
    OutOfRange = 12,
    NonParticipating = 126,
    Undefined = 127,
}

/// <summary>Control relay output block (g12v1, 11 octets): control code, count, on/off time [ms], status.</summary>
public sealed record Crob(Dnp3OpType OpType, Dnp3TripClose TripClose = Dnp3TripClose.Nul, byte Count = 1, uint OnTimeMs = 0, uint OffTimeMs = 0,
    bool Queue = false, bool Clear = false, Dnp3CommandStatus Status = Dnp3CommandStatus.Success)
{
    public static Crob LatchOn() => new(Dnp3OpType.LatchOn);
    public static Crob LatchOff() => new(Dnp3OpType.LatchOff);
    public static Crob Close(uint onTimeMs = 100) => new(Dnp3OpType.PulseOn, Dnp3TripClose.Close, OnTimeMs: onTimeMs);
    public static Crob Trip(uint onTimeMs = 100) => new(Dnp3OpType.PulseOn, Dnp3TripClose.Trip, OnTimeMs: onTimeMs);

    /// <summary>The output state this command requests (true = on/closed), or null for NUL.</summary>
    public bool? TargetState => TripClose switch
    {
        Dnp3TripClose.Close => true,
        Dnp3TripClose.Trip => false,
        _ => OpType switch { Dnp3OpType.LatchOn or Dnp3OpType.PulseOn => true, Dnp3OpType.LatchOff or Dnp3OpType.PulseOff => false, _ => null },
    };

    public byte[] Encode()
    {
        var b = new byte[11];
        b[0] = (byte)(((byte)OpType & 0x0F) | (Queue ? 0x10 : 0) | (Clear ? 0x20 : 0) | ((byte)TripClose << 6));
        b[1] = Count;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), OnTimeMs);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), OffTimeMs);
        b[10] = (byte)Status;
        return b;
    }

    public static Crob Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 11) throw new InvalidDataException("CROB needs 11 octets.");
        return new Crob((Dnp3OpType)(b[0] & 0x0F), (Dnp3TripClose)(b[0] >> 6), b[1],
            BinaryPrimitives.ReadUInt32LittleEndian(b[2..]), BinaryPrimitives.ReadUInt32LittleEndian(b[6..]),
            (b[0] & 0x10) != 0, (b[0] & 0x20) != 0, (Dnp3CommandStatus)(b[10] & 0x7F));
    }
}

/// <summary>Encoders/decoders of the supported object variations.</summary>
public static class Dnp3Objects
{
    /// <summary>DNP3 time: 48-bit milliseconds since 1970-01-01 UTC, little endian.</summary>
    public static void WriteTime(DateTimeOffset t, Span<byte> dest)
    {
        long ms = t.ToUnixTimeMilliseconds();
        for (int i = 0; i < 6; i++) dest[i] = (byte)(ms >> (8 * i));
    }

    public static DateTimeOffset ReadTime(ReadOnlySpan<byte> b)
    {
        long ms = 0;
        for (int i = 0; i < 6; i++) ms |= (long)b[i] << (8 * i);
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    public static byte[] Time(DateTimeOffset t) { var b = new byte[6]; WriteTime(t, b); return b; }

    /// <summary>g1v2 / g10v2 / g2v1: flags with the state in bit 7.</summary>
    public static byte[] Binary(bool value, Dnp3Flags flags = Dnp3Flags.Online) => [(byte)((flags & ~Dnp3Flags.State) | (value ? Dnp3Flags.State : 0))];

    /// <summary>g2v2: flags + 48-bit time.</summary>
    public static byte[] BinaryWithTime(bool value, Dnp3Flags flags, DateTimeOffset t)
    {
        var b = new byte[7];
        b[0] = Binary(value, flags)[0];
        WriteTime(t, b.AsSpan(1));
        return b;
    }

    /// <summary>Analog with flags: v1 (int32), v2 (int16), v5 (float32) for g30/g32/g40 (g40: v1 int32, v2 int16, v3 float); optional time (g32v3/v7).</summary>
    public static byte[] Analog(byte group, byte variation, double value, Dnp3Flags flags, DateTimeOffset? time = null)
    {
        bool isFloat = (group == 40 && variation == 3) || (group != 40 && variation is 5 or 7);
        bool is16 = variation == 2;
        int vs = isFloat || !is16 ? 4 : 2;
        bool timed = group == 32 && variation is 3 or 7;
        var b = new byte[1 + vs + (timed ? 6 : 0)];
        b[0] = (byte)flags;
        if (isFloat) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), (float)value);
        else if (is16) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
        else BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
        if (timed) WriteTime(time ?? DateTimeOffset.UnixEpoch, b.AsSpan(1 + vs));
        return b;
    }

    /// <summary>Decodes an analog with flags (g30, g32, g40); returns value, flags and the optional time.</summary>
    public static (double Value, Dnp3Flags Flags, DateTimeOffset? Time) ReadAnalog(byte group, byte variation, ReadOnlySpan<byte> b)
    {
        bool isFloat = (group == 40 && variation == 3) || (group != 40 && variation is 5 or 7);
        double v = isFloat ? BinaryPrimitives.ReadSingleLittleEndian(b[1..])
            : variation == 2 ? BinaryPrimitives.ReadInt16LittleEndian(b[1..]) : BinaryPrimitives.ReadInt32LittleEndian(b[1..]);
        DateTimeOffset? t = group == 32 && variation is 3 or 7 ? ReadTime(b[5..]) : null;
        return (v, (Dnp3Flags)b[0], t);
    }

    /// <summary>g41 analog output command: v1 int32, v2 int16, v3 float32, each followed by the status octet.</summary>
    public static byte[] AnalogOutput(byte variation, double value, Dnp3CommandStatus status = Dnp3CommandStatus.Success)
    {
        var b = new byte[variation == 2 ? 3 : 5];
        if (variation == 3) BinaryPrimitives.WriteSingleLittleEndian(b, (float)value);
        else if (variation == 2) BinaryPrimitives.WriteInt16LittleEndian(b, (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
        else BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
        b[^1] = (byte)status;
        return b;
    }

    public static (double Value, Dnp3CommandStatus Status) ReadAnalogOutput(byte variation, ReadOnlySpan<byte> b) =>
        (variation == 3 ? BinaryPrimitives.ReadSingleLittleEndian(b) : variation == 2 ? BinaryPrimitives.ReadInt16LittleEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b),
         (Dnp3CommandStatus)(b[^1] & 0x7F));
}
