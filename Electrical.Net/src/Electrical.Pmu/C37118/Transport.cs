using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Electrical.Pmu.C37118;

/// <summary>Source of synchrophasor frames served by a <see cref="PmuServer"/> (implemented by <see cref="PmuSimulator"/>).</summary>
public interface IPmuDataSource
{
    /// <summary>CFG-2: the measurements being transmitted.</summary>
    C37ConfigFrame Configuration { get; }
    /// <summary>CFG-1 (capabilities); defaults to CFG-2 re-typed.</summary>
    C37ConfigFrame Capabilities => Configuration with { Type = C37FrameType.Config1 };
    /// <summary>CFG-3; defaults to CFG-2 re-typed (variable-length names, PHSCALE).</summary>
    C37ConfigFrame ExtendedConfiguration => Configuration with { Type = C37FrameType.Config3 };
    /// <summary>Text of the header frame.</summary>
    string HeaderText { get; }
    /// <summary>Builds the data frame for reporting instant <paramref name="timestamp"/> (UTC).</summary>
    C37DataFrame CreateFrame(DateTimeOffset timestamp);
}

/// <summary>Options of a <see cref="PmuServer"/>.</summary>
public sealed record PmuServerOptions
{
    /// <summary>
    /// True: frames are paced at DATA_RATE on the <see cref="TimeProvider"/> with timestamps on the reporting grid.
    /// False: frames are written back-to-back (flow-controlled by TCP) with timestamps advancing from <see cref="SimulatedStart"/> —
    /// deterministic, for tests and replays.
    /// </summary>
    public bool Paced { get; init; } = true;
    /// <summary>First timestamp of an unpaced stream (default 2026-01-01T00:00:00Z).</summary>
    public DateTimeOffset SimulatedStart { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    /// <summary>Clock for paced streaming and frame timestamps.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// PMU side of IEEE C37.118.2 over TCP (spontaneous-data mode, default port 4712). Each connection answers command frames
/// (send CFG-1/2/3, send header, data on/off) and, while data is on, streams data frames from an <see cref="IPmuDataSource"/>.
/// </summary>
public sealed class PmuServer : IAsyncDisposable
{
    private readonly IPmuDataSource _source;
    private readonly TcpListener _listener;
    private readonly List<Task> _clients = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private long _framesSent, _commands;

    /// <param name="port">TCP port (0 = ephemeral, read <see cref="Port"/> after <see cref="Start"/>).</param>
    public PmuServer(IPmuDataSource source, IPAddress? address = null, int port = 4712, PmuServerOptions? options = null)
    {
        _source = source;
        Options = options ?? new PmuServerOptions();
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
    }

    public PmuServerOptions Options { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>Data frames sent over all connections.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);
    /// <summary>Command frames received over all connections.</summary>
    public long CommandsReceived => Interlocked.Read(ref _commands);

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _listener.Start();
        _accept = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        Task[] all;
        lock (_gate) all = _accept is null ? [.. _clients] : [.. _clients, _accept];
        try { await Task.WhenAll(all).ConfigureAwait(false); } catch { /* closing */ }
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            var t = ServeAsync(c, ct);
            lock (_gate) { _clients.RemoveAll(x => x.IsCompleted); _clients.Add(t); }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken serverCt)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var ct = connCts.Token;
        var write = new SemaphoreSlim(1, 1);
        CancellationTokenSource? dataCts = null;
        Task? dataTask = null;
        async Task Send(byte[] frame, CancellationToken token)
        {
            await write.WaitAsync(token).ConfigureAwait(false);
            try { await stream.WriteAsync(frame, token).ConfigureAwait(false); }
            finally { write.Release(); }
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var raw = await C37Codec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (raw is null) break;
                if (!C37Codec.IsValid(raw) || C37Codec.PeekType(raw) != C37FrameType.Command) continue;
                var cmd = C37Codec.DecodeCommand(raw);
                Interlocked.Increment(ref _commands);
                var cfg = _source.Configuration;
                if (cmd.IdCode != cfg.IdCode) continue; // not addressed to this stream
                var now = C37Time.From(Options.Paced ? Options.Time.GetUtcNow() : Options.SimulatedStart, cfg.TimeBase);
                switch (cmd.Command)
                {
                    case C37Command.SendConfig1: await Send(C37Codec.Encode(_source.Capabilities with { Time = now }), ct).ConfigureAwait(false); break;
                    case C37Command.SendConfig2: await Send(C37Codec.Encode(cfg with { Time = now }), ct).ConfigureAwait(false); break;
                    case C37Command.SendConfig3: await Send(C37Codec.Encode(_source.ExtendedConfiguration with { Time = now }), ct).ConfigureAwait(false); break;
                    case C37Command.SendHeader:
                        await Send(C37Codec.Encode(new C37HeaderFrame(cfg.IdCode, now, _source.HeaderText) { Version = cfg.Version }), ct).ConfigureAwait(false);
                        break;
                    case C37Command.DataOn when dataTask is null || dataTask.IsCompleted:
                        dataCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var stop = dataCts.Token;
                        dataTask = Task.Run(() => StreamAsync(Send, stop, ct), CancellationToken.None); // never run the stream inline on the command loop
                        break;
                    case C37Command.DataOff:
                        if (dataCts is not null)
                        {
                            await dataCts.CancelAsync().ConfigureAwait(false);
                            try { await dataTask!.ConfigureAwait(false); } catch (OperationCanceledException) { }
                            dataCts.Dispose();
                            dataCts = null; dataTask = null;
                        }
                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException or InvalidDataException or ObjectDisposedException or SocketException)
        {
            // client gone or server stopping
        }
        finally
        {
            await connCts.CancelAsync().ConfigureAwait(false);
            if (dataTask is not null) try { await dataTask.ConfigureAwait(false); } catch { /* cancelled */ }
            dataCts?.Dispose();
        }
    }

    // `stop` ends the stream between frames (data off); writes only observe the connection token `io`, because cancelling a pending
    // socket write would leave a partial frame on the wire.
    private async Task StreamAsync(Func<byte[], CancellationToken, Task> send, CancellationToken stop, CancellationToken io)
    {
        var cfg = _source.Configuration;
        double interval = cfg.ReportingIntervalS;
        var step = TimeSpan.FromTicks((long)Math.Round(interval * TimeSpan.TicksPerSecond));
        try
        {
            if (!Options.Paced)
            {
                var t = Options.SimulatedStart;
                while (!stop.IsCancellationRequested)
                {
                    await send(C37Codec.Encode(_source.CreateFrame(t), cfg), io).ConfigureAwait(false);
                    Interlocked.Increment(ref _framesSent);
                    t += step;
                }
                return;
            }
            using var timer = new PeriodicTimer(step, Options.Time);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                var now = Options.Time.GetUtcNow();
                long ticks = now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
                var aligned = DateTimeOffset.UnixEpoch.AddTicks(ticks - ticks % step.Ticks);
                await send(C37Codec.Encode(_source.CreateFrame(aligned), cfg), io).ConfigureAwait(false);
                Interlocked.Increment(ref _framesSent);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
    }
}

/// <summary>
/// PDC side of IEEE C37.118.2 over TCP: connects to a PMU, requests the configuration, turns data on/off and reads decoded data frames.
/// Reads are sequential (one consumer); frames of other types that arrive while waiting for a reply are skipped.
/// </summary>
public sealed class PdcClient : IAsyncDisposable, IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <param name="idCode">IDCODE of the PMU/PDC stream to command.</param>
    public PdcClient(ushort idCode) => IdCode = idCode;

    public ushort IdCode { get; }
    /// <summary>Timeout of a configuration/header request (default 5 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Configuration used to decode data frames (set by <see cref="RequestConfigurationAsync"/>, or assign it).</summary>
    public C37ConfigFrame? Configuration { get; set; }
    /// <summary>Frames discarded because of a bad CHK or frame size.</summary>
    public long InvalidFrames { get; private set; }
    public bool IsConnected => _tcp?.Connected == true;

    public async Task ConnectAsync(string host, int port = 4712, CancellationToken ct = default)
    {
        Dispose();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    /// <summary>Sends a command frame (time stamped with the current UTC time).</summary>
    public async Task SendCommandAsync(C37Command command, CancellationToken ct = default)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected.");
        var frame = C37Codec.Encode(new C37CommandFrame(IdCode, C37Time.From(DateTimeOffset.UtcNow), command));
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await s.WriteAsync(frame, ct).ConfigureAwait(false); }
        finally { _write.Release(); }
    }

    /// <summary>Requests CFG-1, CFG-2 (default) or CFG-3 and stores CFG-2/CFG-3 as <see cref="Configuration"/>.</summary>
    public async Task<C37ConfigFrame> RequestConfigurationAsync(C37FrameType type = C37FrameType.Config2, CancellationToken ct = default)
    {
        var cmd = type switch
        {
            C37FrameType.Config1 => C37Command.SendConfig1,
            C37FrameType.Config2 => C37Command.SendConfig2,
            C37FrameType.Config3 => C37Command.SendConfig3,
            _ => throw new ArgumentException("type must be a configuration frame type."),
        };
        await SendCommandAsync(cmd, ct).ConfigureAwait(false);
        var raw = await WaitForAsync(type, ct).ConfigureAwait(false);
        var cfg = C37Codec.DecodeConfig(raw);
        if (type != C37FrameType.Config1) Configuration = cfg;
        return cfg;
    }

    /// <summary>Requests the header frame text.</summary>
    public async Task<string> RequestHeaderAsync(CancellationToken ct = default)
    {
        await SendCommandAsync(C37Command.SendHeader, ct).ConfigureAwait(false);
        return C37Codec.DecodeHeader(await WaitForAsync(C37FrameType.Header, ct).ConfigureAwait(false)).Text;
    }

    public Task StartDataAsync(CancellationToken ct = default) => SendCommandAsync(C37Command.DataOn, ct);
    public Task StopDataAsync(CancellationToken ct = default) => SendCommandAsync(C37Command.DataOff, ct);

    /// <summary>Reads the next data frame (skipping other frame types; configuration frames update <see cref="Configuration"/>).</summary>
    public async Task<C37DataFrame> ReadDataFrameAsync(CancellationToken ct = default)
    {
        var cfg0 = Configuration ?? throw new InvalidOperationException("No configuration: call RequestConfigurationAsync first.");
        while (true)
        {
            var raw = await ReadValidAsync(ct).ConfigureAwait(false);
            var t = C37Codec.PeekType(raw);
            if (t is C37FrameType.Config2 or C37FrameType.Config3) { Configuration = C37Codec.DecodeConfig(raw); continue; }
            if (t != C37FrameType.Data) continue;
            return C37Codec.DecodeData(raw, Configuration ?? cfg0);
        }
    }

    /// <summary>Streams data frames until cancelled or the connection closes.</summary>
    public async IAsyncEnumerable<C37DataFrame> ReadDataFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            C37DataFrame f;
            try { f = await ReadDataFrameAsync(ct).ConfigureAwait(false); }
            catch (EndOfStreamException) { yield break; }
            yield return f;
        }
    }

    private async Task<byte[]> WaitForAsync(C37FrameType type, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            while (true)
            {
                var raw = await ReadValidAsync(cts.Token).ConfigureAwait(false);
                if (C37Codec.PeekType(raw) == type) return raw;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No {type} frame received within {Timeout.TotalMilliseconds} ms.");
        }
    }

    private async Task<byte[]> ReadValidAsync(CancellationToken ct)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected.");
        while (true)
        {
            var raw = await C37Codec.ReadFrameAsync(s, ct).ConfigureAwait(false) ?? throw new EndOfStreamException("PMU closed the connection.");
            if (C37Codec.IsValid(raw)) return raw;
            InvalidFrames++;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Publishes encoded C37.118 frames as UDP datagrams (one frame per datagram, typical port 4713).</summary>
public sealed class PmuUdpPublisher(IPEndPoint target) : IDisposable
{
    private readonly UdpClient _udp = new(target.AddressFamily);

    public IPEndPoint Target { get; } = target;

    public async Task SendAsync(byte[] frame, CancellationToken ct = default) =>
        await _udp.SendAsync(frame, Target, ct).ConfigureAwait(false);

    public void Dispose() => _udp.Dispose();
}

/// <summary>Receives C37.118 frames sent as UDP datagrams; invalid datagrams (bad CHK/size) are dropped.</summary>
public sealed class PdcUdpReceiver : IDisposable
{
    private readonly UdpClient _udp;

    /// <param name="port">Local port (0 = ephemeral, read <see cref="Port"/>).</param>
    public PdcUdpReceiver(int port = 4713, IPAddress? address = null) => _udp = new UdpClient(new IPEndPoint(address ?? IPAddress.Loopback, port));

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>Returns the next valid frame.</summary>
    public async Task<byte[]> ReceiveFrameAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var r = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            if (C37Codec.IsValid(r.Buffer)) return r.Buffer;
        }
    }

    public void Dispose() => _udp.Dispose();
}
