using System.Buffers.Binary;
using System.Globalization;

namespace Electrical.IEC61850;

/// <summary>48-bit IEEE 802 MAC address.</summary>
public readonly record struct MacAddress(ulong Value)
{
    /// <summary>Parses "01-0C-CD-01-00-01" or "01:0C:CD:01:00:01".</summary>
    public static MacAddress Parse(string text)
    {
        var parts = text.Trim().Split('-', ':');
        if (parts.Length != 6) throw new FormatException($"Invalid MAC address '{text}'.");
        ulong v = 0;
        foreach (var p in parts) v = (v << 8) | byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new MacAddress(v);
    }

    public static MacAddress FromBytes(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        for (int i = 0; i < 6; i++) v = (v << 8) | b[i];
        return new MacAddress(v);
    }

    public void WriteTo(Span<byte> dest)
    {
        for (int i = 0; i < 6; i++) dest[i] = (byte)(Value >> (8 * (5 - i)));
    }

    public byte[] ToBytes() { var b = new byte[6]; WriteTo(b); return b; }

    /// <summary>True for group (multicast) addresses (I/G bit set).</summary>
    public bool IsMulticast => ((Value >> 40) & 1) != 0;

    /// <summary>GOOSE multicast range 01-0C-CD-01-00-00 … 01-0C-CD-01-01-FF (IEC 61850-8-1 Annex B).</summary>
    public static MacAddress Goose(ushort index) => new(0x010CCD010000UL | (index & 0x1FFUL));

    /// <summary>Sampled-values multicast range 01-0C-CD-04-00-00 … 01-0C-CD-04-01-FF (IEC 61850-9-2).</summary>
    public static MacAddress SampledValues(ushort index) => new(0x010CCD040000UL | (index & 0x1FFUL));

    public override string ToString()
    {
        Span<byte> b = stackalloc byte[6];
        WriteTo(b);
        return string.Create(CultureInfo.InvariantCulture, $"{b[0]:X2}-{b[1]:X2}-{b[2]:X2}-{b[3]:X2}-{b[4]:X2}-{b[5]:X2}");
    }
}

/// <summary>IEEE 802.1Q tag: priority (PCP 0–7), drop-eligible indicator and VLAN id (0–4095).</summary>
public readonly record struct VlanTag(byte Priority, ushort VlanId, bool DropEligible = false)
{
    /// <summary>Tag control information (TCI) word.</summary>
    public ushort Tci => (ushort)(((Priority & 7) << 13) | (DropEligible ? 0x1000 : 0) | (VlanId & 0x0FFF));

    public static VlanTag FromTci(ushort tci) => new((byte)(tci >> 13), (ushort)(tci & 0x0FFF), (tci & 0x1000) != 0);
}

/// <summary>Common Ethernet header for IEC 61850 link-layer protocols (GOOSE 0x88B8, SV 0x88BA).</summary>
public static class EthernetFrame
{
    public const ushort EtherTypeVlan = 0x8100;
    public const ushort EtherTypeGoose = 0x88B8;
    public const ushort EtherTypeGse = 0x88B9;
    public const ushort EtherTypeSampledValues = 0x88BA;
    /// <summary>Minimum Ethernet frame size without FCS [bytes].</summary>
    public const int MinimumFrameSize = 60;

    /// <summary>Builds dst/src/[802.1Q]/EtherType/APPID/Length/Reserved1/Reserved2 + PDU, padded to 60 bytes when requested.</summary>
    internal static byte[] Build(MacAddress dst, MacAddress src, VlanTag? vlan, ushort etherType, ushort appId, ushort reserved1, ushort reserved2,
        ReadOnlySpan<byte> pdu, bool pad)
    {
        int hdr = 12 + (vlan is null ? 0 : 4) + 2;
        int total = hdr + 8 + pdu.Length;
        if (8 + pdu.Length > ushort.MaxValue) throw new ArgumentException("PDU too large for an Ethernet frame.");
        var frame = new byte[pad ? Math.Max(total, MinimumFrameSize) : total];
        var s = frame.AsSpan();
        dst.WriteTo(s);
        src.WriteTo(s[6..]);
        int p = 12;
        if (vlan is VlanTag v)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[p..], EtherTypeVlan);
            BinaryPrimitives.WriteUInt16BigEndian(s[(p + 2)..], v.Tci);
            p += 4;
        }
        BinaryPrimitives.WriteUInt16BigEndian(s[p..], etherType); p += 2;
        BinaryPrimitives.WriteUInt16BigEndian(s[p..], appId);
        BinaryPrimitives.WriteUInt16BigEndian(s[(p + 2)..], (ushort)(8 + pdu.Length));
        BinaryPrimitives.WriteUInt16BigEndian(s[(p + 4)..], reserved1);
        BinaryPrimitives.WriteUInt16BigEndian(s[(p + 6)..], reserved2);
        pdu.CopyTo(s[(p + 8)..]);
        return frame;
    }

    internal readonly record struct Header(MacAddress Dst, MacAddress Src, VlanTag? Vlan, ushort EtherType, ushort AppId, ushort Reserved1,
        ushort Reserved2, int PduOffset, int PduLength);

    internal static Header Parse(ReadOnlySpan<byte> f)
    {
        if (f.Length < 22) throw new FormatException("Frame too short.");
        var dst = MacAddress.FromBytes(f);
        var src = MacAddress.FromBytes(f[6..]);
        int p = 12;
        VlanTag? vlan = null;
        ushort et = BinaryPrimitives.ReadUInt16BigEndian(f[p..]);
        if (et == EtherTypeVlan)
        {
            vlan = VlanTag.FromTci(BinaryPrimitives.ReadUInt16BigEndian(f[(p + 2)..]));
            p += 4;
            et = BinaryPrimitives.ReadUInt16BigEndian(f[p..]);
        }
        p += 2;
        if (f.Length < p + 8) throw new FormatException("Frame too short.");
        ushort appId = BinaryPrimitives.ReadUInt16BigEndian(f[p..]);
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(f[(p + 2)..]);
        ushort r1 = BinaryPrimitives.ReadUInt16BigEndian(f[(p + 4)..]);
        ushort r2 = BinaryPrimitives.ReadUInt16BigEndian(f[(p + 6)..]);
        if (len < 8 || p + len > f.Length) throw new FormatException($"Invalid APPID length field {len}.");
        return new Header(dst, src, vlan, et, appId, r1, r2, p + 8, len - 8);
    }
}
