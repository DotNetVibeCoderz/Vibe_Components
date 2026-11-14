using System.Buffers.Binary;
using System.Text;

namespace Electrical.IEC61850;

/// <summary>
/// IEC 61850 UtcTime (8 octets): seconds since 1970-01-01 UTC, 24-bit binary fraction of a second and a time-quality octet
/// (bit 7 leap seconds known, 6 clock failure, 5 clock not synchronized, 4–0 accuracy in bits).
/// </summary>
public readonly record struct UtcTime(uint Seconds, uint Fraction, byte Quality = 0x0A)
{
    public const byte LeapSecondsKnown = 0x80, ClockFailure = 0x40, ClockNotSynchronized = 0x20;

    public static UtcTime FromDateTimeOffset(DateTimeOffset t, byte quality = 0x0A)
    {
        long ticks = t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        uint sec = (uint)(ticks / TimeSpan.TicksPerSecond);
        long rem = ticks % TimeSpan.TicksPerSecond;
        uint frac = (uint)Math.Min(0xFFFFFF, Math.Round(rem * (double)(1 << 24) / TimeSpan.TicksPerSecond));
        return new UtcTime(sec, frac, quality);
    }

    /// <summary>Converts back (resolution 2⁻²⁴ s ≈ 60 ns, rounded to 100 ns ticks).</summary>
    public DateTimeOffset ToDateTimeOffset() =>
        DateTimeOffset.UnixEpoch.AddTicks(Seconds * TimeSpan.TicksPerSecond + (long)Math.Round(Fraction * (double)TimeSpan.TicksPerSecond / (1 << 24)));

    /// <summary>Accuracy in number of significant fraction bits (0–24, 31 = unspecified).</summary>
    public int AccuracyBits => Quality & 0x1F;

    public void WriteTo(Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dest, Seconds);
        dest[4] = (byte)(Fraction >> 16); dest[5] = (byte)(Fraction >> 8); dest[6] = (byte)Fraction;
        dest[7] = Quality;
    }

    public byte[] ToBytes() { var b = new byte[8]; WriteTo(b); return b; }

    public static UtcTime FromBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length != 8) throw new FormatException("UtcTime must be 8 octets.");
        return new UtcTime(BinaryPrimitives.ReadUInt32BigEndian(b), (uint)(b[4] << 16 | b[5] << 8 | b[6]), b[7]);
    }

    public override string ToString() => ToDateTimeOffset().ToString("O");
}

/// <summary>Double-point status (Dbpos, e.g. XCBR.Pos.stVal) encoded as a 2-bit BIT STRING.</summary>
public enum DoublePoint { Intermediate = 0, Off = 1, On = 2, Bad = 3 }

/// <summary>MMS <c>Data</c> value as carried in GOOSE <c>allData</c> (IEC 61850-8-1 / ISO 9506 context tags).</summary>
public abstract record MmsData
{
    // Context-specific tags of the MMS Data CHOICE.
    internal const byte TagArray = 0xA1, TagStructure = 0xA2, TagBoolean = 0x83, TagBitString = 0x84, TagInteger = 0x85, TagUnsigned = 0x86,
        TagFloat = 0x87, TagOctetString = 0x89, TagVisibleString = 0x8A, TagBinaryTime = 0x8C, TagMmsString = 0x90, TagUtcTime = 0x91;

    internal abstract void Encode(BerWriter w);

    /// <summary>Decodes one Data element.</summary>
    public static MmsData Decode(BerTlv t)
    {
        var v = t.Value.Span;
        return t.Tag switch
        {
            TagBoolean => new MmsBoolean(t.AsBoolean()),
            TagInteger => new MmsInteger(t.AsInteger()),
            TagUnsigned => new MmsUnsigned(t.AsUnsigned()),
            TagFloat when v.Length == 5 => new MmsFloat(BinaryPrimitives.ReadSingleBigEndian(v[1..])),
            TagFloat when v.Length == 9 => new MmsDouble(BinaryPrimitives.ReadDoubleBigEndian(v[1..])),
            TagBitString => MmsBitString.FromBer(v),
            TagOctetString => new MmsOctetString(v.ToArray()),
            TagVisibleString or TagMmsString => new MmsVisibleString(Encoding.UTF8.GetString(v)),
            TagUtcTime => new MmsUtcTime(UtcTime.FromBytes(v)),
            TagStructure => new MmsStructure(t.Children().Select(Decode).ToArray()),
            TagArray => new MmsArray(t.Children().Select(Decode).ToArray()),
            _ => throw new FormatException($"Unsupported MMS data tag 0x{t.Tag:X2}."),
        };
    }
}

public sealed record MmsBoolean(bool Value) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteBoolean(TagBoolean, Value);
}

/// <summary>Signed integer (INT8…INT64).</summary>
public sealed record MmsInteger(long Value) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteInteger(TagInteger, Value);
}

/// <summary>Unsigned integer (INT8U…INT32U).</summary>
public sealed record MmsUnsigned(ulong Value) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteUnsigned(TagUnsigned, Value);
}

/// <summary>IEEE 754 single precision (FLOAT32), encoded as exponent width 0x08 + 4 octets big-endian.</summary>
public sealed record MmsFloat(float Value) : MmsData
{
    internal override void Encode(BerWriter w)
    {
        Span<byte> b = stackalloc byte[5];
        b[0] = 8;
        BinaryPrimitives.WriteSingleBigEndian(b[1..], Value);
        w.WriteTlv(TagFloat, b);
    }
}

/// <summary>IEEE 754 double precision (FLOAT64), encoded as exponent width 0x0B + 8 octets.</summary>
public sealed record MmsDouble(double Value) : MmsData
{
    internal override void Encode(BerWriter w)
    {
        Span<byte> b = stackalloc byte[9];
        b[0] = 11;
        BinaryPrimitives.WriteDoubleBigEndian(b[1..], Value);
        w.WriteTlv(TagFloat, b);
    }
}

public sealed record MmsVisibleString(string Value) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteVisibleString(TagVisibleString, Value);
}

public sealed record MmsUtcTime(UtcTime Value) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteTlv(TagUtcTime, Value.ToBytes());
}

public sealed record MmsOctetString(byte[] Value) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteTlv(TagOctetString, Value);
    public bool Equals(MmsOctetString? other) => other is not null && Value.AsSpan().SequenceEqual(other.Value);
    public override int GetHashCode() => Value.Length;
}

/// <summary>BIT STRING; bit 0 is the most significant bit of the first octet (e.g. quality, Dbpos).</summary>
public sealed record MmsBitString(byte[] Bytes, int BitCount) : MmsData
{
    public static MmsBitString FromBits(params bool[] bits)
    {
        var b = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++) if (bits[i]) b[i / 8] |= (byte)(0x80 >> (i % 8));
        return new MmsBitString(b, bits.Length);
    }

    /// <summary>Double-point status as a 2-bit string (01 = off, 10 = on).</summary>
    public static MmsBitString FromDoublePoint(DoublePoint dp) => FromBits(((int)dp & 2) != 0, ((int)dp & 1) != 0);

    public DoublePoint ToDoublePoint() => (DoublePoint)((this[0] ? 2 : 0) | (this[1] ? 1 : 0));

    public bool this[int bit] => bit < BitCount && (Bytes[bit / 8] & (0x80 >> (bit % 8))) != 0;

    internal static MmsBitString FromBer(ReadOnlySpan<byte> v)
    {
        if (v.Length == 0) throw new FormatException("Empty BIT STRING.");
        int unused = v[0];
        return new MmsBitString(v[1..].ToArray(), (v.Length - 1) * 8 - unused);
    }

    internal override void Encode(BerWriter w)
    {
        int n = (BitCount + 7) / 8;
        var b = new byte[n + 1];
        b[0] = (byte)(n * 8 - BitCount);
        Bytes.AsSpan(0, n).CopyTo(b.AsSpan(1));
        if (n > 0 && b[0] > 0) b[n] &= (byte)(0xFF << b[0]);
        w.WriteTlv(TagBitString, b);
    }

    public bool Equals(MmsBitString? other)
    {
        if (other is null || other.BitCount != BitCount) return false;
        for (int i = 0; i < BitCount; i++) if (this[i] != other[i]) return false;
        return true;
    }

    public override int GetHashCode() => BitCount;
    public override string ToString() => string.Concat(Enumerable.Range(0, BitCount).Select(i => this[i] ? '1' : '0'));
}

public sealed record MmsStructure(IReadOnlyList<MmsData> Members) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteConstructed(TagStructure, x => { foreach (var m in Members) m.Encode(x); });
    public bool Equals(MmsStructure? other) => other is not null && Members.SequenceEqual(other.Members);
    public override int GetHashCode() => Members.Count;
}

public sealed record MmsArray(IReadOnlyList<MmsData> Elements) : MmsData
{
    internal override void Encode(BerWriter w) => w.WriteConstructed(TagArray, x => { foreach (var m in Elements) m.Encode(x); });
    public bool Equals(MmsArray? other) => other is not null && Elements.SequenceEqual(other.Elements);
    public override int GetHashCode() => Elements.Count;
}
