using System.Buffers.Binary;
using System.Net.Sockets;

namespace GnuRadio.Hardware;

/// <summary>
/// Client for <c>rtl_tcp</c> servers (RTL-SDR dongles shared over the network, also offered by SDR# and
/// many embedded boards). Pure managed: no native library needed. Samples are 8-bit unsigned I/Q, converted
/// to <see cref="Complex32"/> through a lookup table.
/// </summary>
public sealed class RtlTcpDevice : ISdrDevice
{
    private static readonly float[] Lut = Enumerable.Range(0, 256).Select(i => (i - 127.5f) / 127.5f).ToArray();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private byte[] _raw = [];
    private byte _carry;
    private bool _hasCarry;
    private double _rate = 2.048e6;
    private double _frequency = 100e6;
    private double _gain = 30;
    private bool _agc;

    public RtlTcpDevice(string host = "127.0.0.1", int port = 1234)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public string Driver => "rtl_tcp";
    public string Label => $"rtl_tcp {Host}:{Port}" + (TunerType is { } t ? $" ({t})" : "");
    public bool CanTransmit => false;

    /// <summary>Tuner reported in the server greeting (E4000, R820T…), once connected.</summary>
    public string? TunerType { get; private set; }

    public double SampleRate
    {
        get => _rate;
        set
        {
            _rate = value;
            Send(0x02, (uint)value);
        }
    }

    public double CenterFrequency
    {
        get => _frequency;
        set
        {
            _frequency = value;
            Send(0x01, (uint)value);
        }
    }

    public double Bandwidth { get; set; }

    public double Gain
    {
        get => _gain;
        set
        {
            _gain = value;
            Send(0x03, 1); // manual gain mode
            Send(0x04, (uint)(value * 10)); // tenths of dB
        }
    }

    public bool AutomaticGain
    {
        get => _agc;
        set
        {
            _agc = value;
            Send(0x03, value ? 0u : 1u);
            Send(0x08, value ? 1u : 0u); // RTL2832 digital AGC
        }
    }

    /// <summary>Frequency correction in ppm.</summary>
    public void SetFrequencyCorrection(int ppm) => Send(0x05, (uint)ppm);

    public void Start(SdrDirection direction = SdrDirection.Receive)
    {
        if (direction == SdrDirection.Transmit) throw new NotSupportedException("rtl_tcp is receive-only.");
        _client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20 };
        _client.Connect(Host, Port);
        _stream = _client.GetStream();
        var header = new byte[12];
        _stream.ReadExactly(header);
        if (header[0] == 'R' && header[1] == 'T' && header[2] == 'L' && header[3] == '0')
        {
            TunerType = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) switch
            {
                1 => "E4000",
                2 => "FC0012",
                3 => "FC0013",
                4 => "FC2580",
                5 => "R820T",
                6 => "R828D",
                _ => "unknown",
            };
        }
        SampleRate = _rate;
        CenterFrequency = _frequency;
        if (_agc) AutomaticGain = true;
        else Gain = _gain;
    }

    public void Stop()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public int Read(Span<Complex32> buffer, TimeSpan timeout)
    {
        if (_stream is null) return 0;
        _client!.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        var bytes = buffer.Length * 2;
        if (_raw.Length < bytes) _raw = new byte[bytes];
        var filled = 0;
        if (_hasCarry)
        {
            _raw[0] = _carry;
            filled = 1;
            _hasCarry = false;
        }
        try
        {
            filled += _stream.Read(_raw, filled, bytes - filled);
        }
        catch (IOException)
        {
            return 0;
        }
        var samples = filled / 2;
        for (var i = 0; i < samples; i++) buffer[i] = new Complex32(Lut[_raw[2 * i]], Lut[_raw[2 * i + 1]]);
        if ((filled & 1) == 1)
        {
            _carry = _raw[filled - 1];
            _hasCarry = true;
        }
        return samples;
    }

    public int Write(ReadOnlySpan<Complex32> buffer, TimeSpan timeout) => throw new NotSupportedException("rtl_tcp is receive-only.");

    private void Send(byte command, uint value)
    {
        if (_stream is null) return;
        Span<byte> cmd = stackalloc byte[5];
        cmd[0] = command;
        BinaryPrimitives.WriteUInt32BigEndian(cmd[1..], value);
        _stream.Write(cmd);
    }

    public void Dispose() => Stop();
}
