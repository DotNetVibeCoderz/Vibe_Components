using System.Runtime.InteropServices;
using NetMQ;
using NetMQ.Sockets;

namespace GnuRadio.ZeroMQ;

/// <summary>
/// Shared plumbing for the ZeroMQ stream sinks. Each work call is sent as one ZeroMQ message containing the raw
/// item bytes, which is exactly what GNU Radio's <c>zeromq.*_sink</c> blocks emit with <c>pass_tags=False</c>.
/// </summary>
public abstract class ZmqStreamSink<T> : SinkBlock<T> where T : unmanaged
{
    private NetMQSocket? _socket;

    protected ZmqStreamSink(string name, string address, bool bind, int highWaterMark, int vectorLength)
        : base(name, vectorLength)
    {
        Address = address;
        Bind = bind;
        HighWaterMark = highWaterMark;
    }

    public string Address { get; }
    public bool Bind { get; }
    public int HighWaterMark { get; }
    public long MessagesSent { get; private set; }
    public long MessagesDropped { get; private set; }

    /// <summary>
    /// How long a send may wait for a peer before the message is dropped. PUB never waits (it drops by design);
    /// PUSH waits, giving back-pressure like GNU Radio's push_sink.
    /// </summary>
    public TimeSpan BlockingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    protected abstract NetMQSocket CreateSocket();

    protected virtual bool Blocking => true;

    public override void Start()
    {
        _socket = CreateSocket();
        _socket.Options.SendHighWatermark = HighWaterMark;
        // Give queued messages a moment to leave when the flowgraph stops.
        _socket.Options.Linger = TimeSpan.FromSeconds(1);
        if (Bind) _socket.Bind(Address);
        else _socket.Connect(Address);
    }

    public override void Stop()
    {
        _socket?.Dispose();
        _socket = null;
    }

    protected override bool Consume(ReadOnlySpan<T> input)
    {
        var bytes = MemoryMarshal.AsBytes(input).ToArray();
        var sent = Blocking ? _socket!.TrySendFrame(BlockingTimeout, bytes) : _socket!.TrySendFrame(bytes);
        if (sent) MessagesSent++;
        else MessagesDropped++;
        return true;
    }
}

/// <summary>Shared plumbing for the ZeroMQ stream sources (GNU Radio's <c>zeromq.*_source</c>).</summary>
public abstract class ZmqStreamSource<T> : SourceBlock<T> where T : unmanaged
{
    private NetMQSocket? _socket;
    private byte[] _pending = [];
    private int _pendingOffset;
    private readonly byte[] _carry;
    private int _carryCount;

    protected ZmqStreamSource(string name, string address, bool bind, int vectorLength) : base(name, vectorLength)
    {
        Address = address;
        Bind = bind;
        _carry = new byte[Marshal.SizeOf<T>() * vectorLength];
    }

    public string Address { get; }
    public bool Bind { get; }

    /// <summary>How long one work call waits for a message before yielding.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(50);

    protected abstract NetMQSocket CreateSocket();

    protected virtual void Configure(NetMQSocket socket)
    {
    }

    public override void Start()
    {
        _socket = CreateSocket();
        _socket.Options.Linger = TimeSpan.Zero;
        if (Bind) _socket.Bind(Address);
        else _socket.Connect(Address);
        Configure(_socket);
    }

    public override void Stop()
    {
        _socket?.Dispose();
        _socket = null;
    }

    protected override int Generate(Span<T> output)
    {
        var bytes = MemoryMarshal.AsBytes(output);
        var itemBytes = _carry.Length;
        _carry.AsSpan(0, _carryCount).CopyTo(bytes);
        var filled = _carryCount;
        _carryCount = 0;
        while (filled < bytes.Length)
        {
            if (_pendingOffset < _pending.Length)
            {
                var n = Math.Min(bytes.Length - filled, _pending.Length - _pendingOffset);
                _pending.AsSpan(_pendingOffset, n).CopyTo(bytes[filled..]);
                _pendingOffset += n;
                filled += n;
                continue;
            }
            if (filled >= itemBytes) break;
            if (!_socket!.TryReceiveFrameBytes(Timeout, out var frame)) break;
            _pending = frame;
            _pendingOffset = 0;
        }
        var items = filled / itemBytes;
        _carryCount = filled - items * itemBytes;
        bytes.Slice(items * itemBytes, _carryCount).CopyTo(_carry);
        return items;
    }
}

/// <summary>PUB sink: broadcasts samples to any number of subscribers (GNU Radio's <c>zeromq.pub_sink</c>). Binds by default.</summary>
public sealed class ZmqPubSink<T>(string address, int highWaterMark = 1000, int vectorLength = 1, bool bind = true)
    : ZmqStreamSink<T>("zmq_pub_sink", address, bind, highWaterMark, vectorLength) where T : unmanaged
{
    protected override NetMQSocket CreateSocket() => new PublisherSocket();

    protected override bool Blocking => false;
}

/// <summary>SUB source: receives samples from a PUB sink (GNU Radio's <c>zeromq.sub_source</c>). Connects by default.</summary>
public sealed class ZmqSubSource<T>(string address, int vectorLength = 1, bool bind = false)
    : ZmqStreamSource<T>("zmq_sub_source", address, bind, vectorLength) where T : unmanaged
{
    protected override NetMQSocket CreateSocket() => new SubscriberSocket();

    protected override void Configure(NetMQSocket socket) => ((SubscriberSocket)socket).SubscribeToAnyTopic();
}

/// <summary>PUSH sink: load-balances samples over PULL peers, with back-pressure (GNU Radio's <c>zeromq.push_sink</c>).</summary>
public sealed class ZmqPushSink<T>(string address, int highWaterMark = 1000, int vectorLength = 1, bool bind = true)
    : ZmqStreamSink<T>("zmq_push_sink", address, bind, highWaterMark, vectorLength) where T : unmanaged
{
    protected override NetMQSocket CreateSocket() => new PushSocket();
}

/// <summary>PULL source (GNU Radio's <c>zeromq.pull_source</c>).</summary>
public sealed class ZmqPullSource<T>(string address, int vectorLength = 1, bool bind = false)
    : ZmqStreamSource<T>("zmq_pull_source", address, bind, vectorLength) where T : unmanaged
{
    protected override NetMQSocket CreateSocket() => new PullSocket();
}

/// <summary>
/// Publishes every message received on <c>in</c>, serialised with <see cref="PmtSerializer"/>
/// (GNU Radio's <c>zeromq.pub_msg_sink</c>). Use it to send PDUs to GNU Radio flowgraphs.
/// </summary>
public sealed class ZmqPubMessageSink : MessageBlock
{
    private PublisherSocket? _socket;

    public ZmqPubMessageSink(string address, bool bind = true) : base("zmq_pub_msg_sink")
    {
        Address = address;
        Bind = bind;
        RegisterMessageInput("in", m => _socket?.TrySendFrame(PmtSerializer.Serialize(m)));
    }

    public string Address { get; }
    public bool Bind { get; }

    public override void Start()
    {
        _socket = new PublisherSocket();
        _socket.Options.Linger = TimeSpan.Zero;
        if (Bind) _socket.Bind(Address);
        else _socket.Connect(Address);
    }

    public override void Stop()
    {
        _socket?.Dispose();
        _socket = null;
    }
}

/// <summary>
/// Receives serialised PMTs and publishes them on <c>out</c> (GNU Radio's <c>zeromq.sub_msg_source</c>).
/// A background thread polls the socket so the block works without stream ports.
/// </summary>
public sealed class ZmqSubMessageSource : MessageBlock
{
    private SubscriberSocket? _socket;
    private Thread? _thread;
    private volatile bool _running;

    public ZmqSubMessageSource(string address, bool bind = false) : base("zmq_sub_msg_source")
    {
        Address = address;
        Bind = bind;
        RegisterMessageOutput("out");
    }

    public string Address { get; }
    public bool Bind { get; }

    public override void Start()
    {
        _socket = new SubscriberSocket();
        _socket.Options.Linger = TimeSpan.Zero;
        if (Bind) _socket.Bind(Address);
        else _socket.Connect(Address);
        _socket.SubscribeToAnyTopic();
        _running = true;
        _thread = new Thread(Poll) { IsBackground = true, Name = $"zmq:{Alias}" };
        _thread.Start();
    }

    private void Poll()
    {
        while (_running)
        {
            if (_socket!.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(50), out var frame))
            {
                try
                {
                    PublishMessage("out", PmtSerializer.Deserialize(frame));
                }
                catch (InvalidDataException)
                {
                    // Not a PMT: forward the raw bytes as a PDU so nothing is silently lost.
                    PublishMessage("out", Pmt.Pdu(frame));
                }
            }
        }
    }

    public override void Stop()
    {
        _running = false;
        _thread?.Join();
        _socket?.Dispose();
        _socket = null;
    }
}
