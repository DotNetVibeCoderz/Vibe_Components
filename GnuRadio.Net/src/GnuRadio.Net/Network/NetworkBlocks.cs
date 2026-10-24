using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GnuRadio.Network;

/// <summary>Whether a TCP block listens for a peer or connects to one.</summary>
public enum TcpMode
{
    Client,
    Server,
}

/// <summary>
/// Sends the raw item bytes over TCP (GNU Radio's <c>network.tcp_sink</c>). Interoperates with GNU Radio
/// flowgraphs, <c>nc</c>, and any program reading raw samples. In server mode items are dropped while no
/// client is connected so the flowgraph never stalls.
/// </summary>
public sealed class TcpSink<T> : SinkBlock<T> where T : unmanaged
{
    private TcpListener? _listener;
    private Socket? _socket;

    public TcpSink(string host, int port, TcpMode mode = TcpMode.Client, int vectorLength = 1) : base("tcp_sink", vectorLength)
    {
        Host = host;
        Port = port;
        Mode = mode;
    }

    public string Host { get; }
    public int Port { get; }
    public TcpMode Mode { get; }
    public bool IsConnected => _socket?.Connected == true;

    public override void Start()
    {
        if (Mode == TcpMode.Server)
        {
            _listener = new TcpListener(IPAddress.Parse(Host == "localhost" ? "127.0.0.1" : Host), Port);
            _listener.Start();
        }
        else
        {
            TryConnect();
        }
    }

    private void TryConnect()
    {
        try
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            socket.Connect(Host, Port);
            _socket = socket;
        }
        catch (SocketException)
        {
            _socket = null;
        }
    }

    public override void Stop()
    {
        _socket?.Dispose();
        _socket = null;
        _listener?.Stop();
        _listener = null;
    }

    protected override bool Consume(ReadOnlySpan<T> input)
    {
        if (_socket is null)
        {
            if (_listener is { } l && l.Pending()) _socket = l.AcceptSocket();
            else if (Mode == TcpMode.Client) TryConnect();
            if (_socket is null) return true;
        }
        try
        {
            var bytes = MemoryMarshal.AsBytes(input);
            while (!bytes.IsEmpty)
            {
                var sent = _socket.Send(bytes);
                bytes = bytes[sent..];
            }
        }
        catch (SocketException)
        {
            _socket.Dispose();
            _socket = null;
        }
        return true;
    }
}

/// <summary>Receives raw item bytes over TCP (GNU Radio's <c>blocks.tcp_server_source</c>/<c>tcp_source</c>).</summary>
public sealed class TcpSource<T> : SourceBlock<T> where T : unmanaged
{
    private TcpListener? _listener;
    private Socket? _socket;
    private byte[] _carry = new byte[64];
    private int _carryCount;

    public TcpSource(string host, int port, TcpMode mode = TcpMode.Server, int vectorLength = 1) : base("tcp_source", vectorLength)
    {
        Host = host;
        Port = port;
        Mode = mode;
    }

    public string Host { get; }
    public int Port { get; }
    public TcpMode Mode { get; }

    /// <summary>When false (default), the source keeps waiting after the peer disconnects.</summary>
    public bool StopOnDisconnect { get; set; }

    public override void Start()
    {
        if (Mode == TcpMode.Server)
        {
            _listener = new TcpListener(IPAddress.Parse(Host == "localhost" ? "127.0.0.1" : Host), Port);
            _listener.Start();
        }
    }

    public override void Stop()
    {
        _socket?.Dispose();
        _socket = null;
        _listener?.Stop();
        _listener = null;
    }

    protected override int Generate(Span<T> output)
    {
        if (_socket is null)
        {
            try
            {
                if (_listener is { } l)
                {
                    if (!l.Pending())
                    {
                        Thread.Sleep(20);
                        return 0;
                    }
                    _socket = l.AcceptSocket();
                }
                else
                {
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    s.Connect(Host, Port);
                    _socket = s;
                }
                _socket.ReceiveTimeout = 50;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
                return 0;
            }
        }

        var bytes = MemoryMarshal.AsBytes(output);
        var itemBytes = bytes.Length / (output.Length / VectorLength);
        if (_carry.Length < itemBytes) Array.Resize(ref _carry, itemBytes);
        _carry.AsSpan(0, _carryCount).CopyTo(bytes);
        var filled = _carryCount;
        _carryCount = 0;
        try
        {
            var n = _socket.Receive(bytes[filled..]);
            if (n == 0)
            {
                _socket.Dispose();
                _socket = null;
                return StopOnDisconnect ? -1 : 0;
            }
            filled += n;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
        }
        var items = filled / itemBytes;
        _carryCount = filled - items * itemBytes;
        bytes.Slice(items * itemBytes, _carryCount).CopyTo(_carry);
        return items;
    }
}

/// <summary>
/// Sends items as UDP datagrams of <see cref="PayloadSize"/> bytes (GNU Radio's <c>network.udp_sink</c>
/// without header). Datagrams are only sent when full.
/// </summary>
public sealed class UdpSink<T> : SinkBlock<T> where T : unmanaged
{
    private UdpClient? _client;
    private byte[] _pending = [];
    private int _pendingCount;

    public UdpSink(string host, int port, int payloadSize = 1472, int vectorLength = 1) : base("udp_sink", vectorLength)
    {
        Host = host;
        Port = port;
        PayloadSize = payloadSize;
    }

    public string Host { get; }
    public int Port { get; }
    public int PayloadSize { get; }

    public override void Start()
    {
        _client = new UdpClient();
        _client.Connect(Host, Port);
        _pending = new byte[PayloadSize];
        _pendingCount = 0;
    }

    public override void Stop()
    {
        _client?.Dispose();
        _client = null;
    }

    protected override bool Consume(ReadOnlySpan<T> input)
    {
        var bytes = MemoryMarshal.AsBytes(input);
        while (!bytes.IsEmpty)
        {
            var n = Math.Min(bytes.Length, PayloadSize - _pendingCount);
            bytes[..n].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += n;
            bytes = bytes[n..];
            if (_pendingCount == PayloadSize)
            {
                try
                {
                    _client!.Send(_pending, PayloadSize);
                }
                catch (SocketException)
                {
                    // UDP is best effort; an unreachable peer must not stop the flowgraph.
                }
                _pendingCount = 0;
            }
        }
        return true;
    }
}

/// <summary>Receives UDP datagrams and outputs their bytes as items (GNU Radio's <c>network.udp_source</c>).</summary>
public sealed class UdpSource<T> : SourceBlock<T> where T : unmanaged
{
    private UdpClient? _client;
    private byte[] _leftover = [];
    private int _leftoverOffset;
    private byte[] _carry = new byte[64];
    private int _carryCount;

    public UdpSource(int port, string bindAddress = "0.0.0.0", int vectorLength = 1) : base("udp_source", vectorLength)
    {
        Port = port;
        BindAddress = bindAddress;
    }

    public int Port { get; }
    public string BindAddress { get; }

    public override void Start()
    {
        _client = new UdpClient(new IPEndPoint(IPAddress.Parse(BindAddress), Port));
        _client.Client.ReceiveTimeout = 50;
    }

    public override void Stop()
    {
        _client?.Dispose();
        _client = null;
    }

    protected override int Generate(Span<T> output)
    {
        var bytes = MemoryMarshal.AsBytes(output);
        var itemBytes = bytes.Length / (output.Length / VectorLength);
        if (_carry.Length < itemBytes) Array.Resize(ref _carry, itemBytes);
        _carry.AsSpan(0, _carryCount).CopyTo(bytes);
        var filled = _carryCount;
        _carryCount = 0;
        while (filled < bytes.Length)
        {
            if (_leftoverOffset < _leftover.Length)
            {
                var n = Math.Min(bytes.Length - filled, _leftover.Length - _leftoverOffset);
                _leftover.AsSpan(_leftoverOffset, n).CopyTo(bytes[filled..]);
                _leftoverOffset += n;
                filled += n;
                continue;
            }
            // Return what we have rather than waiting for the window to fill: keeps latency low.
            if (filled >= itemBytes) break;
            try
            {
                IPEndPoint? remote = null;
                _leftover = _client!.Receive(ref remote);
                _leftoverOffset = 0;
            }
            catch (SocketException)
            {
                break;
            }
        }
        var items = filled / itemBytes;
        _carryCount = filled - items * itemBytes;
        bytes.Slice(items * itemBytes, _carryCount).CopyTo(_carry);
        return items;
    }
}
