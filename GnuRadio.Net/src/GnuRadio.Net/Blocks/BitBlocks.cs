namespace GnuRadio.Blocks;

/// <summary>Bit order used when packing and unpacking.</summary>
public enum Endianness
{
    MsbFirst,
    LsbFirst,
}

/// <summary>Bit helpers shared by framing, FEC and modulators. "Unpacked" means one bit (0/1) per byte.</summary>
public static class Bits
{
    public static byte[] Unpack(ReadOnlySpan<byte> packed, int bitsPerByte = 8, Endianness order = Endianness.MsbFirst)
    {
        var result = new byte[packed.Length * bitsPerByte];
        Unpack(packed, result, bitsPerByte, order);
        return result;
    }

    public static void Unpack(ReadOnlySpan<byte> packed, Span<byte> bits, int bitsPerByte = 8, Endianness order = Endianness.MsbFirst)
    {
        for (var i = 0; i < packed.Length; i++)
            for (var b = 0; b < bitsPerByte; b++)
            {
                var shift = order == Endianness.MsbFirst ? bitsPerByte - 1 - b : b;
                bits[i * bitsPerByte + b] = (byte)((packed[i] >> shift) & 1);
            }
    }

    public static byte[] Pack(ReadOnlySpan<byte> bits, int bitsPerByte = 8, Endianness order = Endianness.MsbFirst)
    {
        var result = new byte[bits.Length / bitsPerByte];
        Pack(bits, result, bitsPerByte, order);
        return result;
    }

    public static void Pack(ReadOnlySpan<byte> bits, Span<byte> packed, int bitsPerByte = 8, Endianness order = Endianness.MsbFirst)
    {
        for (var i = 0; i < packed.Length; i++)
        {
            var v = 0;
            for (var b = 0; b < bitsPerByte; b++)
            {
                var shift = order == Endianness.MsbFirst ? bitsPerByte - 1 - b : b;
                v |= (bits[i * bitsPerByte + b] & 1) << shift;
            }
            packed[i] = (byte)v;
        }
    }

    /// <summary>Number of positions where <paramref name="a"/> and <paramref name="b"/> differ.</summary>
    public static int HammingDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var d = 0;
        for (var i = 0; i < n; i++)
            if ((a[i] & 1) != (b[i] & 1)) d++;
        return d;
    }

    /// <summary>Bits of a string-of-ones-and-zeros access code such as <c>"1010110011011101"</c>.</summary>
    public static byte[] FromString(string bits) => bits.Where(c => c is '0' or '1').Select(c => (byte)(c - '0')).ToArray();
}

/// <summary>Splits each byte into <c>k</c> one-bit bytes, MSB first (GNU Radio's <c>blocks.unpack_k_bits_bb</c>).</summary>
public sealed class UnpackKBits(int k = 8) : InterpolatorBlock<byte, byte>("unpack_k_bits_bb", k)
{
    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output) => Bits.Unpack(input, output, Interpolation);
}

/// <summary>Packs <c>k</c> one-bit bytes into one byte, MSB first (GNU Radio's <c>blocks.pack_k_bits_bb</c>).</summary>
public sealed class PackKBits(int k = 8) : DecimatorBlock<byte, byte>("pack_k_bits_bb", k)
{
    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output) => Bits.Pack(input, output, Decimation);
}

/// <summary>
/// Regroups a bit stream from <c>k</c> to <c>l</c> bits per byte (GNU Radio's <c>blocks.repack_bits_bb</c>);
/// for example 8→2 turns packed bytes into QPSK symbols.
/// </summary>
public sealed class RepackBits : Block
{
    private readonly int _k;
    private readonly int _l;
    private readonly Endianness _order;
    private int _acc;
    private int _accBits;

    public RepackBits(int k, int l, Endianness order = Endianness.MsbFirst) : base("repack_bits_bb")
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(k, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(l, 8);
        _k = k;
        _l = l;
        _order = order;
        RelativeRate = (double)k / l;
        AddInput<byte>();
        AddOutput<byte>();
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var input = context.Input<byte>(0);
        var output = context.Output<byte>(0);
        var inCount = context.InputItems(0);
        var outSpace = context.OutputItems(0);
        int consumed = 0, produced = 0;
        while (produced < outSpace)
        {
            if (_accBits >= _l)
            {
                int value;
                if (_order == Endianness.MsbFirst)
                {
                    value = (_acc >> (_accBits - _l)) & ((1 << _l) - 1);
                }
                else
                {
                    value = _acc & ((1 << _l) - 1);
                    _acc >>= _l;
                }
                _accBits -= _l;
                output[produced++] = (byte)value;
                continue;
            }
            if (consumed == inCount) break;
            var b = input[consumed++] & ((1 << _k) - 1);
            if (_order == Endianness.MsbFirst) _acc = ((_acc << _k) | b) & 0xFFFF;
            else _acc |= b << _accBits;
            _accBits += _k;
        }
        context.Consume(0, consumed);
        context.Produce(0, produced);
        return WorkResult.Ok;
    }
}

/// <summary>Maps each byte through a lookup table (GNU Radio's <c>digital.map_bb</c>).</summary>
public sealed class MapBytes(byte[] map) : SyncBlock<byte, byte>("map_bb")
{
    protected override void Work(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = map[input[i]];
    }
}

/// <summary>Hard decision: 1 when x &gt; 0, else 0 (GNU Radio's <c>digital.binary_slicer_fb</c>).</summary>
public sealed class BinarySlicer() : SyncBlock<float, byte>("binary_slicer_fb")
{
    protected override void Work(ReadOnlySpan<float> input, Span<byte> output)
    {
        for (var i = 0; i < input.Length; i++) output[i] = input[i] > 0 ? (byte)1 : (byte)0;
    }
}

/// <summary>Converts fixed-length chunks of the stream into PDU messages on port <c>pdus</c>.</summary>
public sealed class StreamToPdu<T> : SinkBlock<T> where T : unmanaged
{
    private readonly List<T> _pending = [];

    public StreamToPdu(int packetLength) : base("stream_to_pdu")
    {
        PacketLength = packetLength;
        RegisterMessageOutput("pdus");
    }

    public int PacketLength { get; }

    protected override bool Consume(ReadOnlySpan<T> input)
    {
        _pending.AddRange(input);
        while (_pending.Count >= PacketLength)
        {
            var chunk = _pending.GetRange(0, PacketLength).ToArray();
            _pending.RemoveRange(0, PacketLength);
            PublishMessage("pdus", Pmt.Pdu(Pmt.EmptyDict, ToVector(chunk)));
        }
        return true;
    }

    private static Pmt ToVector(T[] chunk) => chunk switch
    {
        byte[] b => Pmt.Vector(b),
        float[] f => Pmt.Vector(f),
        Complex32[] c => Pmt.Vector(c),
        _ => throw new NotSupportedException($"PDUs of {typeof(T).Name} are not supported."),
    };
}

/// <summary>Plays PDUs received on <c>pdus</c> out as a stream; idles (produces nothing) between packets.</summary>
public sealed class PduToStream<T> : Block where T : unmanaged
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<T[]> _queue = new();
    private T[]? _current;
    private int _offset;

    public PduToStream() : base("pdu_to_stream")
    {
        AddOutput<T>();
        RegisterMessageInput("pdus", OnPdu);
    }

    /// <summary>When true, the block emits zeros while no packet is queued, keeping a continuous stream.</summary>
    public bool FillGaps { get; set; }

    private void OnPdu(Pmt pdu)
    {
        var v = pdu.IsPdu ? pdu.Cdr : pdu;
        T[] data = typeof(T) == typeof(byte) ? (T[])(object)v.AsBytes().ToArray()
                 : typeof(T) == typeof(float) ? (T[])(object)v.AsFloats().ToArray()
                 : typeof(T) == typeof(Complex32) ? (T[])(object)v.AsComplex().ToArray()
                 : throw new NotSupportedException();
        _queue.Enqueue(data);
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var output = context.Output<T>(0);
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
                context.AddTag(0, context.ItemsWritten(0) + written, "packet_len", _current.Length);
            }
            var n = Math.Min(space - written, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(output[written..]);
            _offset += n;
            written += n;
        }
        if (written == 0 && FillGaps)
        {
            written = Math.Min(space, 1024);
            output[..written].Clear();
        }
        context.Produce(0, written);
        return WorkResult.Ok;
    }
}
