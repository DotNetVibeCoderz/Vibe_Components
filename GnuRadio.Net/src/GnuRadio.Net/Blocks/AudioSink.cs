using System.Runtime.InteropServices;

namespace GnuRadio.Blocks;

/// <summary>Plays audio from a <see cref="AudioSink"/>. Implement this to route audio to another API.</summary>
public interface IAudioBackend : IDisposable
{
    void Open(int sampleRate, int channels);

    /// <summary>Queues interleaved samples in [−1, 1]; blocks while the device queue is full.</summary>
    void Write(ReadOnlySpan<float> interleaved);
}

/// <summary>
/// Real-time audio output (GNU Radio's <c>audio.sink</c>), one float input per channel. On Windows the default
/// backend uses the waveOut API with no extra dependencies; on other systems pass your own
/// <see cref="IAudioBackend"/> (or write a WAV file with <see cref="WavFileSink"/>).
/// </summary>
public sealed class AudioSink : Block
{
    private readonly IAudioBackend _backend;
    private float[] _interleaved = [];

    public AudioSink(int sampleRate, int channels = 1, IAudioBackend? backend = null) : base("audio_sink")
    {
        SampleRate = sampleRate;
        Channels = channels;
        _backend = backend ?? (OperatingSystem.IsWindows()
            ? new WaveOutBackend()
            : throw new PlatformNotSupportedException("No built-in audio backend on this OS; pass an IAudioBackend or use WavFileSink."));
        for (var c = 0; c < channels; c++) AddInput<float>();
    }

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Output gain applied before clipping.</summary>
    public float Volume { get; set; } = 1f;

    public override void Start() => _backend.Open(SampleRate, Channels);

    public override void Stop() => _backend.Dispose();

    protected internal override WorkResult GeneralWork(WorkContext context)
    {
        var n = Math.Min(context.MinInputItems(), SampleRate / 50);
        if (n == 0) return WorkResult.Ok;
        if (_interleaved.Length < n * Channels) _interleaved = new float[n * Channels];
        var v = Volume;
        for (var c = 0; c < Channels; c++)
        {
            var input = context.Input<float>(c);
            for (var i = 0; i < n; i++) _interleaved[i * Channels + c] = Math.Clamp(input[i] * v, -1f, 1f);
        }
        _backend.Write(_interleaved.AsSpan(0, n * Channels));
        context.ConsumeEach(n);
        return WorkResult.Ok;
    }
}

/// <summary>Windows waveOut backend: 16-bit PCM, a small ring of 20 ms buffers.</summary>
internal sealed unsafe partial class WaveOutBackend : IAudioBackend
{
    private const int BufferCount = 6;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x1;
    private IntPtr _device;
    private WaveHeader* _headers;
    private short* _data;
    private int _bufferSamples;
    private int _next;
    private int _fill;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutPrepareHeader(IntPtr device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutUnprepareHeader(IntPtr device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutWrite(IntPtr device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutReset(IntPtr device);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutClose(IntPtr device);

    public void Open(int sampleRate, int channels)
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = (ushort)channels,
            SamplesPerSec = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(channels * 2),
            AvgBytesPerSec = (uint)(sampleRate * channels * 2),
        };
        var result = waveOutOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) throw new InvalidOperationException($"waveOutOpen failed with MMRESULT {result}.");
        _bufferSamples = sampleRate / 50 * channels;
        _headers = (WaveHeader*)NativeMemory.AllocZeroed((nuint)(BufferCount * sizeof(WaveHeader)));
        _data = (short*)NativeMemory.AllocZeroed((nuint)(BufferCount * _bufferSamples * sizeof(short)));
        for (var i = 0; i < BufferCount; i++)
        {
            _headers[i].Data = (IntPtr)(_data + i * _bufferSamples);
            _headers[i].BufferLength = (uint)(_bufferSamples * sizeof(short));
            _headers[i].Flags = WhdrDone;
        }
        _next = 0;
        _fill = 0;
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        foreach (var sample in interleaved)
        {
            var header = &_headers[_next];
            if (_fill == 0)
            {
                // Wait for the device to hand this buffer back.
                while ((Volatile.Read(ref header->Flags) & WhdrDone) == 0) Thread.Sleep(2);
                if ((header->Flags & 0x2) != 0) waveOutUnprepareHeader(_device, header, (uint)sizeof(WaveHeader));
            }
            _data[_next * _bufferSamples + _fill++] = (short)(sample * 32767f);
            if (_fill == _bufferSamples)
            {
                header->Flags = 0;
                waveOutPrepareHeader(_device, header, (uint)sizeof(WaveHeader));
                waveOutWrite(_device, header, (uint)sizeof(WaveHeader));
                _fill = 0;
                _next = (_next + 1) % BufferCount;
            }
        }
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        waveOutReset(_device);
        for (var i = 0; i < BufferCount; i++)
            if ((_headers[i].Flags & 0x2) != 0) waveOutUnprepareHeader(_device, &_headers[i], (uint)sizeof(WaveHeader));
        waveOutClose(_device);
        _device = IntPtr.Zero;
        NativeMemory.Free(_headers);
        NativeMemory.Free(_data);
        _headers = null;
        _data = null;
    }
}
