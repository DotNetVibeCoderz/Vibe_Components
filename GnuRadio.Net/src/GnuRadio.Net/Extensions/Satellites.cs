using System.Text;
using GnuRadio.Blocks;
using GnuRadio.Fec;

namespace GnuRadio.Extensions;

/// <summary>
/// HDLC framing as used by AX.25 amateur and CubeSat telemetry (the framing layer of <c>gr-satellites</c>):
/// 0x7E flags, bit stuffing after five ones, LSB-first bytes and a CRC-16/X-25 frame check sequence.
/// </summary>
public static class Hdlc
{
    private static readonly byte[] Flag = [0, 1, 1, 1, 1, 1, 1, 0];

    /// <summary>Frames <paramref name="payload"/> into unpacked bits: flags, stuffed body with FCS, flags.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload, int flags = 4)
    {
        var fcs = Crc.Crc16X25.Compute(payload);
        var body = new byte[payload.Length + 2];
        payload.CopyTo(body);
        body[^2] = (byte)fcs;
        body[^1] = (byte)(fcs >> 8);
        var bits = new List<byte>();
        for (var i = 0; i < flags; i++) bits.AddRange(Flag);
        var ones = 0;
        foreach (var b in body)
            for (var k = 0; k < 8; k++)
            {
                var bit = (byte)((b >> k) & 1);
                bits.Add(bit);
                if (bit == 1 && ++ones == 5)
                {
                    bits.Add(0);
                    ones = 0;
                }
                else if (bit == 0)
                {
                    ones = 0;
                }
            }
        for (var i = 0; i < flags; i++) bits.AddRange(Flag);
        return [.. bits];
    }

    /// <summary>NRZI as used by AX.25: a 0 toggles the line, a 1 keeps it.</summary>
    public static byte[] NrziEncode(ReadOnlySpan<byte> bits, byte initial = 0)
    {
        var result = new byte[bits.Length];
        var level = initial;
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == 0) level ^= 1;
            result[i] = level;
        }
        return result;
    }

    public static byte[] NrziDecode(ReadOnlySpan<byte> levels, byte initial = 0)
    {
        var result = new byte[levels.Length];
        var last = initial;
        for (var i = 0; i < levels.Length; i++)
        {
            result[i] = (byte)(levels[i] == last ? 1 : 0);
            last = levels[i];
        }
        return result;
    }
}

/// <summary>Streaming NRZI encoder: a 0 toggles the line, a 1 keeps it (AX.25 convention).</summary>
public sealed class NrziEncoder() : SyncBlock<byte, byte>("nrzi_encode")
{
    private byte _level;

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            if ((input[i] & 1) == 0) _level ^= 1;
            output[i] = _level;
        }
    }
}

/// <summary>Streaming NRZI decoder: an unchanged level is a 1, a transition a 0.</summary>
public sealed class NrziDecoder() : SyncBlock<byte, byte>("nrzi_decode")
{
    private byte _last;

    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            var level = (byte)(input[i] & 1);
            output[i] = level == _last ? (byte)1 : (byte)0;
            _last = level;
        }
    }
}

/// <summary>
/// Emits HDLC-framed bits for every PDU posted to <c>in</c> (or queued with <see cref="Enqueue"/>), and flag
/// bytes in between so the link stays synchronised — the transmit side of an AX.25 / CubeSat beacon.
/// </summary>
public sealed class HdlcFramer : Block
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _queue = new();
    private static readonly byte[] IdleFlag = [0, 1, 1, 1, 1, 1, 1, 0];
    private byte[] _current = IdleFlag;
    private int _offset;

    public HdlcFramer() : base("hdlc_framer")
    {
        AddOutput<byte>();
        RegisterMessageInput("in", m => Enqueue(m.IsPdu ? m.Cdr.AsBytes() : m.AsBytes()));
    }

    public void Enqueue(ReadOnlySpan<byte> frame) => _queue.Enqueue(Hdlc.Frame(frame, flags: 2));

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var output = context.Output<byte>(0);
        var space = context.OutputItems(0);
        var written = 0;
        while (written < space)
        {
            if (_offset == _current.Length)
            {
                _current = _queue.TryDequeue(out var next) ? next : IdleFlag;
                _offset = 0;
            }
            var n = Math.Min(space - written, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(output[written..]);
            _offset += n;
            written += n;
        }
        context.Produce(0, written);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Recovers HDLC frames from a hard-bit stream (one bit per byte): flag detection, de-stuffing, FCS check.
/// Valid frames (without FCS) go out on message port <c>out</c> (GNU Radio <c>gr-satellites</c>' <c>hdlc_deframer</c>).
/// </summary>
public sealed class HdlcDeframer : SinkBlock<byte>
{
    private readonly List<byte> _bits = [];
    private int _shift;
    private int _ones;
    private bool _inFrame;

    public HdlcDeframer(int minLength = 4, int maxLength = 1024) : base("hdlc_deframer")
    {
        MinLength = minLength;
        MaxLength = maxLength;
        RegisterMessageOutput("out");
    }

    public int MinLength { get; }
    public int MaxLength { get; }
    public long FramesReceived { get; private set; }
    public long FcsErrors { get; private set; }

    public event Action<byte[]>? FrameReceived;

    protected override bool Consume(ReadOnlySpan<byte> input)
    {
        foreach (var b in input)
        {
            var bit = b & 1;
            _shift = ((_shift << 1) | bit) & 0xFF;
            if (_shift == 0x7E)
            {
                // Flag: the last 7 bits pushed belong to it, drop them before closing the frame.
                if (_inFrame && _bits.Count >= 7)
                {
                    _bits.RemoveRange(_bits.Count - 7, 7);
                    TryEmit();
                }
                _bits.Clear();
                _inFrame = true;
                _ones = 0;
                continue;
            }
            if (!_inFrame) continue;
            if (bit == 1)
            {
                if (++_ones > 6)
                {
                    _inFrame = false; // abort sequence
                    _bits.Clear();
                    continue;
                }
                _bits.Add(1);
            }
            else
            {
                if (_ones == 5)
                {
                    _ones = 0; // stuffed zero
                    continue;
                }
                _ones = 0;
                _bits.Add(0);
            }
            if (_bits.Count > (MaxLength + 2) * 8 + 8)
            {
                _inFrame = false;
                _bits.Clear();
            }
        }
        return true;
    }

    private void TryEmit()
    {
        if (_bits.Count % 8 != 0 || _bits.Count / 8 < MinLength + 2) return;
        var bytes = Bits.Pack(_bits.ToArray(), 8, Endianness.LsbFirst);
        var payload = bytes.AsSpan(0, bytes.Length - 2);
        var fcs = (ushort)(bytes[^2] | bytes[^1] << 8);
        if (Crc.Crc16X25.Compute(payload) != fcs)
        {
            FcsErrors++;
            return;
        }
        FramesReceived++;
        var copy = payload.ToArray();
        PublishMessage("out", Pmt.Pdu(copy));
        FrameReceived?.Invoke(copy);
    }
}

/// <summary>AX.25 UI frames, the packet format of APRS and most amateur satellites.</summary>
public static class Ax25
{
    /// <summary>Builds a UI frame (control 0x03, PID 0xF0) from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public static byte[] UiFrame(string destination, string source, ReadOnlySpan<byte> info, byte destinationSsid = 0, byte sourceSsid = 0)
    {
        var frame = new List<byte>();
        frame.AddRange(Address(destination, destinationSsid, last: false));
        frame.AddRange(Address(source, sourceSsid, last: true));
        frame.Add(0x03);
        frame.Add(0xF0);
        frame.AddRange(info.ToArray());
        return [.. frame];
    }

    private static byte[] Address(string callsign, byte ssid, bool last)
    {
        var a = new byte[7];
        var call = callsign.ToUpperInvariant().PadRight(6)[..6];
        for (var i = 0; i < 6; i++) a[i] = (byte)(call[i] << 1);
        a[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (last ? 1 : 0));
        return a;
    }

    /// <summary>Parses a UI frame into its header fields and information text.</summary>
    public static (string Destination, string Source, byte[] Info) Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 16) throw new ArgumentException("Frame too short for AX.25.", nameof(frame));
        static string Call(ReadOnlySpan<byte> a)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < 6; i++) sb.Append((char)(a[i] >> 1));
            var ssid = (a[6] >> 1) & 0x0F;
            var call = sb.ToString().Trim();
            return ssid == 0 ? call : $"{call}-{ssid}";
        }
        return (Call(frame[..7]), Call(frame[7..14]), frame[16..].ToArray());
    }
}
