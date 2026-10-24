using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace GnuRadio.Blocks;

/// <summary>
/// Reads raw binary items from a file (GNU Radio's <c>blocks.file_source</c>). The format is the
/// in-memory layout of <typeparamref name="T"/>, so complex recordings made by GNU Radio
/// (<c>.cfile</c>, <c>.iq</c> of interleaved float32) load as <see cref="Complex32"/> directly.
/// </summary>
public sealed class FileSource<T> : SourceBlock<T> where T : unmanaged
{
    private FileStream? _stream;

    public FileSource(string path, bool repeat = false, int vectorLength = 1) : base("file_source", vectorLength)
    {
        Path = path;
        Repeat = repeat;
    }

    public string Path { get; }
    public bool Repeat { get; }

    public override void Start() =>
        _stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);

    public override void Stop()
    {
        _stream?.Dispose();
        _stream = null;
    }

    protected override int Generate(Span<T> output)
    {
        var bytes = MemoryMarshal.AsBytes(output);
        var itemBytes = bytes.Length / (output.Length / VectorLength);
        var read = 0;
        while (read < bytes.Length)
        {
            var n = _stream!.Read(bytes[read..]);
            if (n == 0)
            {
                if (!Repeat || _stream.Length == 0) break;
                _stream.Position = 0;
                continue;
            }
            read += n;
        }
        var items = read / itemBytes;
        // A trailing partial item is pushed back for the next call.
        if (read % itemBytes != 0) _stream!.Position -= read % itemBytes;
        return items == 0 && read == 0 ? -1 : items;
    }
}

/// <summary>Writes raw binary items to a file (GNU Radio's <c>blocks.file_sink</c>).</summary>
public sealed class FileSink<T> : SinkBlock<T> where T : unmanaged
{
    private FileStream? _stream;

    public FileSink(string path, bool append = false, int vectorLength = 1) : base("file_sink", vectorLength)
    {
        Path = path;
        Append = append;
    }

    public string Path { get; }
    public bool Append { get; }

    /// <summary>When false, items are consumed but not written (GNU Radio's <c>set_unbuffered</c>/<c>close</c> toggle).</summary>
    public bool Enabled { get; set; } = true;

    public override void Start() =>
        _stream = new FileStream(Path, Append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);

    public override void Stop()
    {
        _stream?.Flush();
        _stream?.Dispose();
        _stream = null;
    }

    protected override bool Consume(ReadOnlySpan<T> input)
    {
        if (Enabled) _stream!.Write(MemoryMarshal.AsBytes(input));
        return true;
    }
}

/// <summary>Reads a PCM WAV file (8/16/24/32-bit integer or 32-bit float), one float output per channel (GNU Radio's <c>blocks.wavfile_source</c>).</summary>
public sealed class WavFileSource : Block
{
    private readonly bool _repeat;
    private BinaryReader? _reader;
    private long _dataStart;
    private long _dataLength;
    private long _position;
    private byte[] _scratch = [];

    public WavFileSource(string path, bool repeat = false) : base("wavfile_source")
    {
        Path = path;
        _repeat = repeat;
        using (var reader = new BinaryReader(File.OpenRead(path)))
            (Channels, SampleRate, BitsPerSample, IsFloat, _, _) = ReadHeader(reader);
        for (var c = 0; c < Channels; c++) AddOutput<float>(name: $"ch{c}", optional: c > 0);
    }

    public string Path { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int BitsPerSample { get; }
    public bool IsFloat { get; }

    private static (int Channels, int Rate, int Bits, bool IsFloat, long DataStart, long DataLength) ReadHeader(BinaryReader r)
    {
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        r.ReadUInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");
        int channels = 0, rate = 0, bits = 0;
        var isFloat = false;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            var size = r.ReadUInt32();
            if (id == "fmt ")
            {
                var format = r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = (int)r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt16();
                bits = r.ReadUInt16();
                isFloat = format == 3;
                if (format == 0xFFFE && size >= 40)
                {
                    r.ReadBytes(8);
                    isFloat = r.ReadUInt16() == 3;
                    r.ReadBytes((int)size - 26);
                }
                else
                {
                    r.ReadBytes((int)size - 16);
                }
            }
            else if (id == "data")
            {
                return (channels, rate, bits, isFloat, r.BaseStream.Position, size);
            }
            else
            {
                r.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException("WAV file has no data chunk.");
    }

    public override void Start()
    {
        _reader = new BinaryReader(File.OpenRead(Path));
        (_, _, _, _, _dataStart, _dataLength) = ReadHeader(_reader);
        _position = 0;
    }

    public override void Stop()
    {
        _reader?.Dispose();
        _reader = null;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var frameBytes = Channels * BitsPerSample / 8;
        var frames = (int)Math.Min(context.MinOutputItems(), (_dataLength - _position) / frameBytes);
        if (frames == 0)
        {
            if (!_repeat) return WorkResult.Done;
            _position = 0;
            _reader!.BaseStream.Position = _dataStart;
            return WorkResult.Ok;
        }
        if (_scratch.Length < frames * frameBytes) _scratch = new byte[frames * frameBytes];
        var span = _scratch.AsSpan(0, frames * frameBytes);
        _reader!.BaseStream.ReadExactly(span);
        _position += span.Length;
        var bytesPerSample = BitsPerSample / 8;
        for (var c = 0; c < Channels; c++)
        {
            var output = context.Output<float>(c);
            for (var f = 0; f < frames; f++)
                output[f] = Decode(span.Slice((f * Channels + c) * bytesPerSample, bytesPerSample));
        }
        context.ProduceEach(frames);
        return WorkResult.Ok;
    }

    private float Decode(ReadOnlySpan<byte> s) => BitsPerSample switch
    {
        8 => (s[0] - 128) / 128f,
        16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
        32 when IsFloat => BinaryPrimitives.ReadSingleLittleEndian(s),
        32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        _ => throw new NotSupportedException($"{BitsPerSample}-bit WAV is not supported."),
    };
}

/// <summary>Writes a 16-bit PCM WAV file with one input per channel (GNU Radio's <c>blocks.wavfile_sink</c>).</summary>
public sealed class WavFileSink : Block
{
    private FileStream? _stream;
    private long _frames;
    private byte[] _scratch = [];

    public WavFileSink(string path, int channels, int sampleRate) : base("wavfile_sink")
    {
        Path = path;
        Channels = channels;
        SampleRate = sampleRate;
        for (var c = 0; c < channels; c++) AddInput<float>();
    }

    public string Path { get; }
    public int Channels { get; }
    public int SampleRate { get; }

    public override void Start()
    {
        _stream = new FileStream(Path, FileMode.Create, FileAccess.Write);
        _stream.Write(new byte[44]);
        _frames = 0;
    }

    public override void Stop()
    {
        if (_stream is null) return;
        var dataBytes = _frames * Channels * 2;
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(SampleRate * Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataBytes);
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Dispose();
        _stream = null;
    }

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = context.MinInputItems();
        if (n == 0) return WorkResult.Ok;
        if (_scratch.Length < n * Channels * 2) _scratch = new byte[n * Channels * 2];
        for (var c = 0; c < Channels; c++)
        {
            var input = context.Input<float>(c);
            for (var f = 0; f < n; f++)
            {
                var v = (short)Math.Clamp(MathF.Round(input[f] * 32767f), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(_scratch.AsSpan((f * Channels + c) * 2), v);
            }
        }
        _stream!.Write(_scratch, 0, n * Channels * 2);
        _frames += n;
        context.ConsumeEach(n);
        return WorkResult.Ok;
    }
}
