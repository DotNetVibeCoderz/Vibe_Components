using System.Collections.Concurrent;
using GnuRadio.Blocks;
using GnuRadio.Fec;

namespace GnuRadio.Digital;

/// <summary>
/// A simple packet format: preamble, access code, a 32-bit header holding the payload length twice
/// (so corrupted headers are rejected), the payload, and a CRC-32. Comparable to GNU Radio's
/// <c>packet_utils</c>/<c>header_format_default</c>.
/// </summary>
public sealed class PacketFormat
{
    /// <summary>GNU Radio's default 64-bit access code.</summary>
    public const string DefaultAccessCode = "1010110011011101101001001110001011110010100011000010000011111100";

    public PacketFormat(string accessCode = DefaultAccessCode, int preambleBytes = 4, int threshold = 3)
    {
        AccessCode = Bits.FromString(accessCode);
        AccessCodeString = accessCode;
        PreambleBytes = preambleBytes;
        Threshold = threshold;
    }

    public byte[] AccessCode { get; }
    public string AccessCodeString { get; }
    public int PreambleBytes { get; }

    /// <summary>Access-code bit errors tolerated when searching.</summary>
    public int Threshold { get; }

    public const int MaxPayload = 4095;

    /// <summary>Frames <paramref name="payload"/> into unpacked bits (one bit per byte).</summary>
    public byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException($"Payload exceeds {MaxPayload} bytes.", nameof(payload));
        var crc = Crc.Crc32.Compute(payload);
        var body = new byte[4 + payload.Length + 4];
        var len = (ushort)payload.Length;
        body[0] = (byte)(len >> 8);
        body[1] = (byte)len;
        body[2] = (byte)(len >> 8);
        body[3] = (byte)len;
        payload.CopyTo(body.AsSpan(4));
        body[^4] = (byte)(crc >> 24);
        body[^3] = (byte)(crc >> 16);
        body[^2] = (byte)(crc >> 8);
        body[^1] = (byte)crc;

        var preamble = Enumerable.Repeat((byte)0x55, PreambleBytes).ToArray();
        return [.. Bits.Unpack(preamble), .. AccessCode, .. Bits.Unpack(body)];
    }
}

/// <summary>
/// Turns PDUs received on <c>in</c> into a framed bit stream (one bit per byte). Between packets it emits
/// idle bits (alternating) so a modulator downstream keeps running.
/// </summary>
public sealed class PacketFramer : Block
{
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private byte[]? _current;
    private int _offset;
    private byte _idle;

    public PacketFramer(PacketFormat? format = null, bool idleFill = true) : base("packet_framer")
    {
        Format = format ?? new PacketFormat();
        IdleFill = idleFill;
        AddOutput<byte>();
        RegisterMessageInput("in", m => Enqueue(m.IsPdu ? m.Cdr.AsBytes() : m.AsBytes()));
    }

    public PacketFormat Format { get; }
    public bool IdleFill { get; }
    public int Pending => _queue.Count;

    /// <summary>Queues a payload directly (thread-safe), without going through a message port.</summary>
    public void Enqueue(ReadOnlySpan<byte> payload)
    {
        _queue.Enqueue(Format.Frame(payload));
        Wake();
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var output = context.Output<byte>(0);
        var space = context.OutputItems(0);
        var written = 0;
        while (written < space)
        {
            if (_current is null || _offset == _current.Length)
            {
                if (!_queue.TryDequeue(out _current))
                {
                    _current = null;
                    break;
                }
                _offset = 0;
                context.AddTag(0, context.ItemsWritten(0) + written, "packet_start", _current.Length);
            }
            var n = Math.Min(space - written, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(output[written..]);
            _offset += n;
            written += n;
        }
        if (written == 0 && IdleFill)
        {
            written = Math.Min(space, 256);
            for (var i = 0; i < written; i++) output[i] = _idle ^= 1;
        }
        context.Produce(0, written);
        return WorkResult.Ok;
    }
}

/// <summary>
/// Recovers packets from a hard-bit stream: searches for the access code, validates the duplicated length
/// header, collects the payload and checks its CRC-32. Good packets are published on <c>pdus</c>.
/// </summary>
public sealed class PacketDeframer : SinkBlock<byte>
{
    private readonly ulong _code;
    private readonly ulong _mask;
    private readonly int _codeLength;
    private ulong _register;
    private State _state;
    private readonly List<byte> _bits = [];
    private int _payloadLength;

    private enum State
    {
        Search,
        Header,
        Payload,
    }

    public PacketDeframer(PacketFormat? format = null) : base("packet_deframer")
    {
        Format = format ?? new PacketFormat();
        _codeLength = Format.AccessCode.Length;
        foreach (var b in Format.AccessCode) _code = (_code << 1) | b;
        _mask = _codeLength == 64 ? ulong.MaxValue : (1UL << _codeLength) - 1;
        RegisterMessageOutput("pdus");
    }

    public PacketFormat Format { get; }
    public long PacketsReceived { get; private set; }
    public long CrcErrors { get; private set; }
    public long HeaderErrors { get; private set; }

    public event Action<byte[]>? PacketReceived;

    protected override bool Consume(ReadOnlySpan<byte> input)
    {
        foreach (var bit in input)
        {
            switch (_state)
            {
                case State.Search:
                    _register = ((_register << 1) | (uint)(bit & 1)) & _mask;
                    if ((int)ulong.PopCount(_register ^ _code) <= Format.Threshold)
                    {
                        _state = State.Header;
                        _bits.Clear();
                    }
                    break;
                case State.Header:
                    _bits.Add((byte)(bit & 1));
                    if (_bits.Count == 32)
                    {
                        var h = Bits.Pack(_bits.ToArray());
                        var a = (h[0] << 8) | h[1];
                        var b = (h[2] << 8) | h[3];
                        if (a == b && a <= PacketFormat.MaxPayload)
                        {
                            _payloadLength = a;
                            _state = State.Payload;
                        }
                        else
                        {
                            HeaderErrors++;
                            _state = State.Search;
                        }
                        _bits.Clear();
                    }
                    break;
                case State.Payload:
                    _bits.Add((byte)(bit & 1));
                    if (_bits.Count == (_payloadLength + 4) * 8)
                    {
                        var bytes = Bits.Pack(_bits.ToArray());
                        var payload = bytes.AsSpan(0, _payloadLength);
                        var crc = (uint)(bytes[^4] << 24 | bytes[^3] << 16 | bytes[^2] << 8 | bytes[^1]);
                        if (Crc.Crc32.Compute(payload) == crc)
                        {
                            PacketsReceived++;
                            var copy = payload.ToArray();
                            PublishMessage("pdus", Pmt.Pdu(copy));
                            PacketReceived?.Invoke(copy);
                        }
                        else
                        {
                            CrcErrors++;
                        }
                        _bits.Clear();
                        _register = 0;
                        _state = State.Search;
                    }
                    break;
            }
        }
        return true;
    }
}
