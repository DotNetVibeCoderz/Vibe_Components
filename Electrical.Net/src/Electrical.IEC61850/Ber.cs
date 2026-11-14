using System.Buffers.Binary;
using System.Text;

namespace Electrical.IEC61850;

/// <summary>One decoded BER tag-length-value element. <see cref="Tag"/> holds the identifier octet(s) (high-tag-number form packed big-endian).</summary>
public readonly record struct BerTlv(int Tag, ReadOnlyMemory<byte> Value)
{
    /// <summary>True when the constructed bit (0x20) of the first identifier octet is set.</summary>
    public bool IsConstructed => ((Tag > 0xFF ? Tag >> (8 * ((BitLength(Tag) - 1) / 8)) : Tag) & 0x20) != 0;

    private static int BitLength(int v) => 32 - int.LeadingZeroCount(v);

    /// <summary>Children of a constructed element.</summary>
    public IReadOnlyList<BerTlv> Children() => BerReader.ReadAll(Value);

    public long AsInteger() => Ber.DecodeInteger(Value.Span);
    public ulong AsUnsigned() => Ber.DecodeUnsigned(Value.Span);
    public bool AsBoolean() => Value.Length > 0 && Value.Span[0] != 0;
    public string AsString() => Encoding.ASCII.GetString(Value.Span);
}

/// <summary>Static helpers for ASN.1 Basic Encoding Rules (X.690) primitives.</summary>
public static class Ber
{
    /// <summary>Encodes a definite length (short form &lt; 128, otherwise long form 0x8n + n bytes).</summary>
    public static byte[] EncodeLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length < 0x80) return [(byte)length];
        int n = length <= 0xFF ? 1 : length <= 0xFFFF ? 2 : length <= 0xFFFFFF ? 3 : 4;
        var b = new byte[n + 1];
        b[0] = (byte)(0x80 | n);
        for (int i = 0; i < n; i++) b[n - i] = (byte)(length >> (8 * i));
        return b;
    }

    /// <summary>Minimal two's-complement content octets of a signed integer.</summary>
    public static byte[] EncodeInteger(long value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(tmp, value);
        int start = 0;
        while (start < 7 && ((tmp[start] == 0x00 && (tmp[start + 1] & 0x80) == 0) || (tmp[start] == 0xFF && (tmp[start + 1] & 0x80) != 0))) start++;
        return tmp[start..].ToArray();
    }

    /// <summary>Content octets of a non-negative integer (a leading 0x00 is added when the top bit is set).</summary>
    public static byte[] EncodeUnsigned(ulong value)
    {
        Span<byte> tmp = stackalloc byte[9];
        tmp[0] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(tmp[1..], value);
        int start = 0;
        while (start < 8 && tmp[start] == 0 && (tmp[start + 1] & 0x80) == 0) start++;
        return tmp[start..].ToArray();
    }

    /// <summary>Decodes two's-complement content octets (1–8 bytes).</summary>
    public static long DecodeInteger(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0) return 0;
        if (content.Length > 8) throw new FormatException("INTEGER longer than 8 bytes.");
        long v = (sbyte)content[0];
        for (int i = 1; i < content.Length; i++) v = (v << 8) | content[i];
        return v;
    }

    /// <summary>Decodes unsigned content octets (up to 8 significant bytes, optional leading zero).</summary>
    public static ulong DecodeUnsigned(ReadOnlySpan<byte> content)
    {
        while (content.Length > 1 && content[0] == 0) content = content[1..];
        if (content.Length > 8) throw new FormatException("Unsigned value longer than 8 bytes.");
        ulong v = 0;
        foreach (var b in content) v = (v << 8) | b;
        return v;
    }

    /// <summary>Encodes a complete TLV.</summary>
    public static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        var len = EncodeLength(content.Length);
        var r = new byte[1 + len.Length + content.Length];
        r[0] = tag;
        len.CopyTo(r, 1);
        content.CopyTo(r.AsSpan(1 + len.Length));
        return r;
    }
}

/// <summary>Append-only BER writer. Constructed elements are written with <see cref="WriteConstructed"/>.</summary>
public sealed class BerWriter
{
    private readonly MemoryStream _ms = new();

    public int Length => (int)_ms.Length;

    public BerWriter WriteRaw(ReadOnlySpan<byte> bytes) { _ms.Write(bytes); return this; }

    public BerWriter WriteTlv(byte tag, ReadOnlySpan<byte> content)
    {
        _ms.WriteByte(tag);
        _ms.Write(Ber.EncodeLength(content.Length));
        _ms.Write(content);
        return this;
    }

    public BerWriter WriteInteger(byte tag, long value) => WriteTlv(tag, Ber.EncodeInteger(value));
    public BerWriter WriteUnsigned(byte tag, ulong value) => WriteTlv(tag, Ber.EncodeUnsigned(value));
    public BerWriter WriteBoolean(byte tag, bool value) => WriteTlv(tag, [value ? (byte)0xFF : (byte)0x00]);
    public BerWriter WriteVisibleString(byte tag, string value) => WriteTlv(tag, Encoding.ASCII.GetBytes(value));

    /// <summary>Writes a constructed element whose content is produced by <paramref name="content"/>.</summary>
    public BerWriter WriteConstructed(byte tag, Action<BerWriter> content)
    {
        var inner = new BerWriter();
        content(inner);
        return WriteTlv(tag, inner.ToArray());
    }

    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>Sequential BER reader over a buffer.</summary>
public sealed class BerReader(ReadOnlyMemory<byte> buffer)
{
    private int _pos;

    public bool HasData => _pos < buffer.Length;
    public int Position => _pos;

    /// <summary>Reads the next TLV (definite lengths only).</summary>
    public BerTlv Read()
    {
        var s = buffer.Span;
        if (_pos >= s.Length) throw new FormatException("Unexpected end of BER data.");
        int tag = s[_pos++];
        if ((tag & 0x1F) == 0x1F)
        {
            byte b;
            do
            {
                if (_pos >= s.Length) throw new FormatException("Truncated BER tag.");
                b = s[_pos++];
                tag = (tag << 8) | b;
            } while ((b & 0x80) != 0);
        }
        if (_pos >= s.Length) throw new FormatException("Truncated BER length.");
        int len = s[_pos++];
        if (len == 0x80) throw new FormatException("Indefinite BER length is not supported.");
        if ((len & 0x80) != 0)
        {
            int n = len & 0x7F;
            if (n > 4 || _pos + n > s.Length) throw new FormatException("Invalid BER long-form length.");
            len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | s[_pos++];
        }
        if (len < 0 || _pos + len > s.Length) throw new FormatException($"BER length {len} exceeds buffer.");
        var v = buffer.Slice(_pos, len);
        _pos += len;
        return new BerTlv(tag, v);
    }

    /// <summary>Decodes all consecutive TLVs in a buffer.</summary>
    public static IReadOnlyList<BerTlv> ReadAll(ReadOnlyMemory<byte> buffer)
    {
        var r = new BerReader(buffer);
        var list = new List<BerTlv>();
        while (r.HasData) list.Add(r.Read());
        return list;
    }
}
