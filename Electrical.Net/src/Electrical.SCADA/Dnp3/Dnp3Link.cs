using System.Buffers.Binary;

namespace Electrical.SCADA.Dnp3;

/// <summary>
/// CRC-16/DNP (IEEE 1815-2012 §9.2.4.1.2): polynomial 0x3D65 processed bit-reflected (0xA6BC), initial value 0, final complement,
/// transmitted low octet first. Check value: ASCII "123456789" → 0xEA82.
/// </summary>
public static class Dnp3Crc
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int b = 0; b < 8; b++) c = (c & 1) != 0 ? (c >> 1) ^ 0xA6BC : c >> 1;
            t[i] = (ushort)c;
        }
        return t;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data) crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return (ushort)~crc;
    }

    /// <summary>Appends the CRC (low octet first) after <paramref name="data"/> in <paramref name="dest"/>.</summary>
    internal static void Write(ReadOnlySpan<byte> data, Span<byte> dest) => BinaryPrimitives.WriteUInt16LittleEndian(dest, Compute(data));

    internal static bool Check(ReadOnlySpan<byte> data, ReadOnlySpan<byte> crc) => BinaryPrimitives.ReadUInt16LittleEndian(crc) == Compute(data);
}

/// <summary>Primary-station (PRM = 1) data-link function codes (IEEE 1815 Table 9-1).</summary>
public enum LinkFunction : byte
{
    ResetLinkStates = 0,
    TestLinkStates = 2,
    ConfirmedUserData = 3,
    UnconfirmedUserData = 4,
    RequestLinkStatus = 9,
}

/// <summary>Secondary-station (PRM = 0) data-link function codes (IEEE 1815 Table 9-2).</summary>
public enum LinkSecondaryFunction : byte
{
    Ack = 0,
    Nack = 1,
    LinkStatus = 11,
    NotSupported = 15,
}

/// <summary>
/// One FT3 data-link frame: 0x05 0x64, LEN (octets of CTRL + addresses + user data, excluding CRCs), CTRL, DEST, SRC (little endian),
/// header CRC, then user data in blocks of up to 16 octets each followed by its CRC.
/// </summary>
/// <param name="FromMaster">DIR bit: true for frames sent by the master.</param>
/// <param name="IsPrimary">PRM bit.</param>
/// <param name="Function">4-bit link function code.</param>
public sealed record Dnp3LinkFrame(bool FromMaster, bool IsPrimary, byte Function, ushort Destination, ushort Source, byte[] UserData, bool Fcb = false, bool Fcv = false)
{
    public const byte Start1 = 0x05, Start2 = 0x64;
    /// <summary>Maximum user data per frame [octets].</summary>
    public const int MaxUserData = 250;

    public byte Control => (byte)((FromMaster ? 0x80 : 0) | (IsPrimary ? 0x40 : 0) | (Fcb ? 0x20 : 0) | (Fcv ? 0x10 : 0) | (Function & 0x0F));

    public static Dnp3LinkFrame UnconfirmedData(bool fromMaster, ushort dest, ushort src, byte[] data) =>
        new(fromMaster, true, (byte)LinkFunction.UnconfirmedUserData, dest, src, data);

    public byte[] Encode()
    {
        if (UserData.Length > MaxUserData) throw new ArgumentException("Link user data exceeds 250 octets.");
        int blocks = (UserData.Length + 15) / 16;
        var b = new byte[10 + UserData.Length + 2 * blocks];
        b[0] = Start1; b[1] = Start2; b[2] = (byte)(5 + UserData.Length); b[3] = Control;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), Destination);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), Source);
        Dnp3Crc.Write(b.AsSpan(0, 8), b.AsSpan(8));
        int p = 10;
        for (int i = 0; i < UserData.Length; i += 16)
        {
            int n = Math.Min(16, UserData.Length - i);
            UserData.AsSpan(i, n).CopyTo(b.AsSpan(p));
            Dnp3Crc.Write(b.AsSpan(p, n), b.AsSpan(p + n));
            p += n + 2;
        }
        return b;
    }

    /// <summary>Total encoded length for a frame whose LEN field is <paramref name="len"/>.</summary>
    public static int EncodedLength(int len) { int ud = len - 5; return 10 + ud + 2 * ((ud + 15) / 16); }

    public static Dnp3LinkFrame Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10 || b[0] != Start1 || b[1] != Start2) throw new InvalidDataException("Not a DNP3 link frame (0x05 0x64 expected).");
        if (!Dnp3Crc.Check(b[..8], b.Slice(8, 2))) throw new InvalidDataException("DNP3 link header CRC error.");
        int len = b[2];
        if (len < 5) throw new InvalidDataException("Invalid DNP3 LEN.");
        if (b.Length < EncodedLength(len)) throw new InvalidDataException("Truncated DNP3 link frame.");
        byte c = b[3];
        var data = new byte[len - 5];
        int p = 10;
        for (int i = 0; i < data.Length; i += 16)
        {
            int n = Math.Min(16, data.Length - i);
            if (!Dnp3Crc.Check(b.Slice(p, n), b.Slice(p + n, 2))) throw new InvalidDataException("DNP3 user data block CRC error.");
            b.Slice(p, n).CopyTo(data.AsSpan(i));
            p += n + 2;
        }
        return new Dnp3LinkFrame((c & 0x80) != 0, (c & 0x40) != 0, (byte)(c & 0x0F),
            BinaryPrimitives.ReadUInt16LittleEndian(b[4..]), BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), data, (c & 0x20) != 0, (c & 0x10) != 0);
    }

    /// <summary>Reads one frame from a stream (null at a clean end of stream).</summary>
    public static async ValueTask<Dnp3LinkFrame?> ReadAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[10];
        int got = await s.ReadAtLeastAsync(head, 10, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 10) throw new EndOfStreamException("Truncated DNP3 link header.");
        if (head[0] != Start1 || head[1] != Start2) throw new InvalidDataException("Lost DNP3 frame synchronisation.");
        if (!Dnp3Crc.Check(head.AsSpan(0, 8), head.AsSpan(8, 2))) throw new InvalidDataException("DNP3 link header CRC error.");
        var all = new byte[EncodedLength(head[2])];
        head.CopyTo(all, 0);
        await s.ReadExactlyAsync(all.AsMemory(10), ct).ConfigureAwait(false);
        return Decode(all);
    }
}

/// <summary>
/// Transport function (IEEE 1815 §8): one header octet FIN (bit 7), FIR (bit 6), SEQ (6 bits) in front of up to 249 octets of an
/// application fragment per link frame.
/// </summary>
public static class Dnp3Transport
{
    public const int MaxSegmentPayload = Dnp3LinkFrame.MaxUserData - 1;

    public static byte Header(bool fir, bool fin, int seq) => (byte)((fin ? 0x80 : 0) | (fir ? 0x40 : 0) | (seq & 0x3F));

    /// <summary>Splits an application fragment into transport segments (sequence numbers start at <paramref name="seq"/>, which is advanced).</summary>
    public static List<byte[]> Segment(ReadOnlySpan<byte> fragment, ref int seq)
    {
        var list = new List<byte[]>();
        int pos = 0;
        do
        {
            int n = Math.Min(MaxSegmentPayload, fragment.Length - pos);
            var seg = new byte[n + 1];
            seg[0] = Header(pos == 0, pos + n >= fragment.Length, seq);
            fragment.Slice(pos, n).CopyTo(seg.AsSpan(1));
            list.Add(seg);
            seq = (seq + 1) & 0x3F;
            pos += n;
        } while (pos < fragment.Length);
        return list;
    }
}

/// <summary>Reassembles transport segments into application fragments (discarding out-of-sequence data as the standard requires).</summary>
public sealed class Dnp3TransportReassembler
{
    private readonly MemoryStream _buf = new();
    private int _expected = -1;

    /// <summary>Adds a segment; returns the complete fragment when FIN is received.</summary>
    public byte[]? Add(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 1) return null;
        byte h = segment[0];
        bool fir = (h & 0x40) != 0, fin = (h & 0x80) != 0;
        int seq = h & 0x3F;
        if (fir) { _buf.SetLength(0); _expected = seq; }
        else if (_expected < 0 || seq != _expected) { _buf.SetLength(0); _expected = -1; return null; }
        _buf.Write(segment[1..]);
        _expected = (seq + 1) & 0x3F;
        if (!fin) return null;
        var frag = _buf.ToArray();
        _buf.SetLength(0);
        _expected = -1;
        return frag;
    }
}

/// <summary>
/// Link + transport channel over a byte stream (TCP): sends application fragments as unconfirmed user data and returns received
/// fragments. Link-layer requests (reset / request link status / test) are answered automatically.
/// </summary>
internal sealed class Dnp3Channel(Stream stream, bool isMaster, ushort localAddress)
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dnp3TransportReassembler _rx = new();
    private int _txSeq;

    public ushort LocalAddress { get; } = localAddress;
    public long FramesReceived { get; private set; }
    public long CrcErrors { get; private set; }

    public async Task SendAsync(byte[] fragment, ushort destination, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var seg in Dnp3Transport.Segment(fragment, ref _txSeq))
                await stream.WriteAsync(Dnp3LinkFrame.UnconfirmedData(isMaster, destination, LocalAddress, seg).Encode(), ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    private async Task SendLinkAsync(Dnp3LinkFrame f, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await stream.WriteAsync(f.Encode(), ct).ConfigureAwait(false); }
        finally { _write.Release(); }
    }

    /// <summary>Reads until a complete application fragment addressed to us arrives; returns (fragment, source) or null at end of stream.</summary>
    public async Task<(byte[] Fragment, ushort Source)?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            Dnp3LinkFrame? f;
            try { f = await Dnp3LinkFrame.ReadAsync(stream, ct).ConfigureAwait(false); }
            catch (InvalidDataException) { CrcErrors++; throw; }
            if (f is null) return null;
            FramesReceived++;
            if (f.Destination != LocalAddress && f.Destination < 0xFFFD) continue;
            if (f.IsPrimary)
            {
                switch ((LinkFunction)f.Function)
                {
                    case LinkFunction.ResetLinkStates or LinkFunction.TestLinkStates:
                        await SendLinkAsync(new Dnp3LinkFrame(isMaster, false, (byte)LinkSecondaryFunction.Ack, f.Source, LocalAddress, []), ct).ConfigureAwait(false);
                        continue;
                    case LinkFunction.RequestLinkStatus:
                        await SendLinkAsync(new Dnp3LinkFrame(isMaster, false, (byte)LinkSecondaryFunction.LinkStatus, f.Source, LocalAddress, []), ct).ConfigureAwait(false);
                        continue;
                    case LinkFunction.ConfirmedUserData:
                        await SendLinkAsync(new Dnp3LinkFrame(isMaster, false, (byte)LinkSecondaryFunction.Ack, f.Source, LocalAddress, []), ct).ConfigureAwait(false);
                        break;
                    case LinkFunction.UnconfirmedUserData: break;
                    default: continue;
                }
                var frag = _rx.Add(f.UserData);
                if (frag is not null) return (frag, f.Source);
            }
        }
    }
}
