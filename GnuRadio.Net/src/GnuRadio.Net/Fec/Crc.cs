namespace GnuRadio.Fec;

/// <summary>
/// Table-driven CRC for widths 8–32 in the Rocksoft parameter model (width, poly, init, reflect in/out,
/// xor out). Presets cover the CRCs used by common radio protocols (GNU Radio's <c>digital.crc</c>).
/// </summary>
public sealed class Crc
{
    private readonly uint[] _table = new uint[256];
    private readonly uint _mask;

    public Crc(int width, uint polynomial, uint initial, bool reflectInput, bool reflectOutput, uint xorOutput, string name = "")
    {
        if (width is < 8 or > 32) throw new ArgumentOutOfRangeException(nameof(width), "Width must be 8..32.");
        Width = width;
        Polynomial = polynomial;
        Initial = initial;
        ReflectInput = reflectInput;
        ReflectOutput = reflectOutput;
        XorOutput = xorOutput;
        Name = name;
        _mask = width == 32 ? uint.MaxValue : (1u << width) - 1;

        if (reflectInput)
        {
            var rpoly = Reflect(polynomial, width);
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var b = 0; b < 8; b++) c = (c & 1) != 0 ? (c >> 1) ^ rpoly : c >> 1;
                _table[i] = c & _mask;
            }
        }
        else
        {
            var top = 1u << (width - 1);
            for (uint i = 0; i < 256; i++)
            {
                var c = i << (width - 8);
                for (var b = 0; b < 8; b++) c = (c & top) != 0 ? (c << 1) ^ polynomial : c << 1;
                _table[i] = c & _mask;
            }
        }
    }

    public int Width { get; }
    public uint Polynomial { get; }
    public uint Initial { get; }
    public bool ReflectInput { get; }
    public bool ReflectOutput { get; }
    public uint XorOutput { get; }
    public string Name { get; }
    public int Bytes => (Width + 7) / 8;

    /// <summary>CRC-8/SMBUS (poly 0x07).</summary>
    public static Crc Crc8 { get; } = new(8, 0x07, 0x00, false, false, 0x00, "CRC-8");

    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF).</summary>
    public static Crc Crc16Ccitt { get; } = new(16, 0x1021, 0xFFFF, false, false, 0x0000, "CRC-16/CCITT-FALSE");

    /// <summary>CRC-16/X-25, the HDLC/AX.25 frame check sequence.</summary>
    public static Crc Crc16X25 { get; } = new(16, 0x1021, 0xFFFF, true, true, 0xFFFF, "CRC-16/X-25");

    /// <summary>CRC-32 (IEEE 802.3, zlib, PNG).</summary>
    public static Crc Crc32 { get; } = new(32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF, "CRC-32");

    public uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = Initial;
        if (ReflectInput)
        {
            foreach (var b in data) crc = (crc >> 8) ^ _table[(crc ^ b) & 0xFF];
        }
        else
        {
            var shift = Width - 8;
            foreach (var b in data) crc = ((crc << 8) ^ _table[((crc >> shift) ^ b) & 0xFF]) & _mask;
        }
        if (ReflectInput != ReflectOutput) crc = Reflect(crc, Width);
        return (crc ^ XorOutput) & _mask;
    }

    /// <summary>Returns <paramref name="data"/> followed by its CRC, most significant byte first.</summary>
    public byte[] Append(ReadOnlySpan<byte> data)
    {
        var crc = Compute(data);
        var result = new byte[data.Length + Bytes];
        data.CopyTo(result);
        for (var i = 0; i < Bytes; i++) result[data.Length + i] = (byte)(crc >> (8 * (Bytes - 1 - i)));
        return result;
    }

    /// <summary>Checks a buffer produced by <see cref="Append"/>.</summary>
    public bool Check(ReadOnlySpan<byte> dataWithCrc)
    {
        if (dataWithCrc.Length < Bytes) return false;
        var data = dataWithCrc[..^Bytes];
        uint stored = 0;
        foreach (var b in dataWithCrc[^Bytes..]) stored = (stored << 8) | b;
        return Compute(data) == stored;
    }

    private static uint Reflect(uint value, int width)
    {
        uint r = 0;
        for (var i = 0; i < width; i++)
            if ((value & (1u << i)) != 0) r |= 1u << (width - 1 - i);
        return r;
    }
}

/// <summary>Appends a CRC to PDUs arriving on <c>in</c>, publishing them on <c>out</c> (GNU Radio's <c>digital.crc_append</c>).</summary>
public sealed class CrcAppend : MessageBlock
{
    public CrcAppend(Crc? crc = null) : base("crc_append")
    {
        Crc = crc ?? Crc.Crc32;
        RegisterMessageOutput("out");
        RegisterMessageInput("in", m => PublishMessage("out", Pmt.Pdu(m.Car, Pmt.Vector(Crc.Append(m.Cdr.AsBytes())))));
    }

    public Crc Crc { get; }
}

/// <summary>
/// Checks and strips the CRC of PDUs on <c>in</c>; valid ones go to <c>ok</c>, invalid ones to <c>fail</c>
/// (GNU Radio's <c>digital.crc_check</c>).
/// </summary>
public sealed class CrcCheck : MessageBlock
{
    public CrcCheck(Crc? crc = null) : base("crc_check")
    {
        Crc = crc ?? Crc.Crc32;
        RegisterMessageOutput("ok");
        RegisterMessageOutput("fail");
        RegisterMessageInput("in", OnPdu);
    }

    public Crc Crc { get; }
    public long Passed { get; private set; }
    public long Failed { get; private set; }

    private void OnPdu(Pmt pdu)
    {
        var data = pdu.Cdr.AsBytes();
        if (Crc.Check(data))
        {
            Passed++;
            PublishMessage("ok", Pmt.Pdu(pdu.Car, Pmt.Vector(data[..^Crc.Bytes])));
        }
        else
        {
            Failed++;
            PublishMessage("fail", pdu);
        }
    }
}
