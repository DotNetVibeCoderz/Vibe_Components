using System.Threading.Channels;

namespace Electrical.SCADA.Iec104;

/// <summary>IEC 60870-5-104 §9.6 protocol parameters (defaults from the standard).</summary>
public sealed record Iec104Parameters
{
    /// <summary>Maximum unacknowledged I-frames sent (k).</summary>
    public int K { get; init; } = 12;
    /// <summary>Acknowledge after receiving w I-frames at the latest (w ≤ 2k/3).</summary>
    public int W { get; init; } = 8;
    /// <summary>t0: connection establishment timeout.</summary>
    public TimeSpan T0 { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>t1: timeout for the acknowledgement of a sent I-frame or TESTFR/STARTDT/STOPDT act (connection closed on expiry).</summary>
    public TimeSpan T1 { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>t2: acknowledge received I-frames with an S-frame when no data is sent for t2 (t2 &lt; t1).</summary>
    public TimeSpan T2 { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>t3: send TESTFR act after t3 of idle line.</summary>
    public TimeSpan T3 { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// One IEC 104 TCP connection: APCI framing, STARTDT/STOPDT/TESTFR handling, send/receive sequence numbers with k/w windows and
/// acknowledgement, and timers t1–t3. Shared by <see cref="Iec104Server"/> (controlled station) and <see cref="Iec104Client"/>
/// (controlling station). Received ASDUs are delivered through <see cref="Received"/>. Protocol errors (sequence error, t1 expiry,
/// unacknowledged window overflow) close the connection, as the standard requires.
/// </summary>
public sealed class Iec104Connection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);
    private readonly Channel<Asdu> _received = Channel.CreateUnbounded<Asdu>();
    private readonly Queue<(ushort Seq, DateTimeOffset Time)> _unacked = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeProvider _time;
    private readonly bool _controlling;
    private TaskCompletionSource<bool>? _startCon, _stopCon, _testCon;
    private ushort _vs, _vr, _ackRx;
    private int _rxSinceAck;
    private DateTimeOffset? _firstUnackedRx, _testSent;
    private DateTimeOffset _lastRx;
    private Task? _reader, _timers;

    internal Iec104Connection(Stream stream, Iec104Parameters parameters, bool controlling, TimeProvider? time = null)
    {
        _stream = stream;
        Parameters = parameters;
        _controlling = controlling;
        _time = time ?? TimeProvider.System;
        _lastRx = _time.GetUtcNow();
    }

    public Iec104Parameters Parameters { get; }
    /// <summary>STARTDT confirmed (controlling) / received (controlled): I-frames with process data may flow.</summary>
    public bool DataTransferActive { get; private set; }
    /// <summary>Received ASDUs (completes when the connection closes).</summary>
    public ChannelReader<Asdu> Received => _received.Reader;
    /// <summary>Completes when the connection is closed; <see cref="CloseReason"/> tells why.</summary>
    public Task Completion => _closed.Task;
    public string? CloseReason { get; private set; }
    public long IFramesSent { get; private set; }
    public long IFramesReceived { get; private set; }
    public long SFramesSent { get; private set; }
    public long TestFramesSent { get; private set; }
    public long TestFramesReceived { get; private set; }
    /// <summary>Unacknowledged sent I-frames.</summary>
    public int Unacknowledged { get { lock (_gate) return _unacked.Count; } }
    /// <summary>Raised (controlled station) when STARTDT act has been confirmed.</summary>
    internal event Action? Started;

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Start()
    {
        _reader = ReadLoopAsync(_cts.Token);
        _timers = TimerLoopAsync(_cts.Token);
    }

    // ------------------------------------------------------------ sending

    /// <summary>Sends an ASDU as an I-frame; waits while k frames are unacknowledged.</summary>
    public async Task SendAsync(Asdu asdu, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        while (true)
        {
            lock (_gate) if (_unacked.Count < Parameters.K) break;
            await _ackSignal.WaitAsync(token).ConfigureAwait(false);
        }
        await _write.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] frame;
            lock (_gate)
            {
                frame = Iec104Codec.Encode(Apdu.I(_vs, _vr, asdu));
                _unacked.Enqueue((_vs, _time.GetUtcNow()));
                _vs = (ushort)((_vs + 1) & 0x7FFF);
                _rxSinceAck = 0; _firstUnackedRx = null; // N(R) piggy-backs the acknowledgement
            }
            await _stream.WriteAsync(frame, token).ConfigureAwait(false);
            IFramesSent++;
        }
        finally { _write.Release(); }
    }

    private async Task SendRawAsync(byte[] frame, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(frame, ct).ConfigureAwait(false); }
        finally { _write.Release(); }
    }

    private async Task SendAckAsync(CancellationToken ct)
    {
        byte[] f;
        lock (_gate)
        {
            if (_rxSinceAck == 0) return;
            f = Iec104Codec.Encode(Apdu.S(_vr));
            _rxSinceAck = 0; _firstUnackedRx = null;
        }
        await SendRawAsync(f, ct).ConfigureAwait(false);
        SFramesSent++;
    }

    /// <summary>Controlling station: STARTDT act, waits for STARTDT con (t1).</summary>
    public async Task StartDataTransferAsync(CancellationToken ct = default)
    {
        var tcs = _startCon = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.StartDtAct)), ct).ConfigureAwait(false);
        await WaitConAsync(tcs.Task, "STARTDT", ct).ConfigureAwait(false);
        DataTransferActive = true;
    }

    /// <summary>Controlling station: acknowledges pending I-frames, STOPDT act, waits for STOPDT con (t1).</summary>
    public async Task StopDataTransferAsync(CancellationToken ct = default)
    {
        await SendAckAsync(ct).ConfigureAwait(false);
        var tcs = _stopCon = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.StopDtAct)), ct).ConfigureAwait(false);
        await WaitConAsync(tcs.Task, "STOPDT", ct).ConfigureAwait(false);
        DataTransferActive = false;
    }

    /// <summary>Sends TESTFR act and waits for TESTFR con (t1).</summary>
    public async Task TestAsync(CancellationToken ct = default)
    {
        var tcs = _testCon = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _testSent = _time.GetUtcNow();
        await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.TestFrAct)), ct).ConfigureAwait(false);
        TestFramesSent++;
        await WaitConAsync(tcs.Task, "TESTFR", ct).ConfigureAwait(false);
    }

    private async Task WaitConAsync(Task con, string what, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var delay = Task.Delay(Parameters.T1, _time, linked.Token);
        var done = await Task.WhenAny(con, delay, Completion).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (done == con) { await linked.CancelAsync().ConfigureAwait(false); return; }
        if (done == delay) { await CloseAsync($"t1 expired waiting for {what} con").ConfigureAwait(false); throw new TimeoutException($"No {what} con within t1."); }
        throw new IOException($"Connection closed: {CloseReason}");
    }

    // ------------------------------------------------------------ receiving

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var head = new byte[2];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
                if (head[0] != Iec104Codec.StartByte) { await CloseAsync("start byte 0x68 expected").ConfigureAwait(false); return; }
                var frame = new byte[2 + head[1]];
                head.CopyTo(frame, 0);
                await _stream.ReadExactlyAsync(frame.AsMemory(2), ct).ConfigureAwait(false);
                Apdu apdu;
                try { apdu = Iec104Codec.Decode(frame); }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException) { await CloseAsync($"invalid APDU: {e.Message}").ConfigureAwait(false); return; }
                lock (_gate) _lastRx = _time.GetUtcNow();
                if (!await HandleAsync(apdu, ct).ConfigureAwait(false)) return;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException)
        {
            await CloseAsync(e is OperationCanceledException ? "closed" : "peer closed the connection").ConfigureAwait(false);
        }
    }

    private async Task<bool> HandleAsync(Apdu a, CancellationToken ct)
    {
        switch (a.Format)
        {
            case ApciFormat.U:
                switch (a.Function)
                {
                    case UFunction.StartDtAct:
                        await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.StartDtCon)), ct).ConfigureAwait(false);
                        DataTransferActive = true;
                        Started?.Invoke();
                        break;
                    case UFunction.StopDtAct:
                        await SendAckAsync(ct).ConfigureAwait(false);
                        DataTransferActive = false;
                        await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.StopDtCon)), ct).ConfigureAwait(false);
                        break;
                    case UFunction.TestFrAct:
                        TestFramesReceived++;
                        await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.TestFrCon)), ct).ConfigureAwait(false);
                        break;
                    case UFunction.StartDtCon: _startCon?.TrySetResult(true); break;
                    case UFunction.StopDtCon: _stopCon?.TrySetResult(true); break;
                    case UFunction.TestFrCon:
                        lock (_gate) _testSent = null;
                        _testCon?.TrySetResult(true);
                        break;
                }
                return true;
            case ApciFormat.S:
                return await AcknowledgedAsync(a.ReceiveSequence).ConfigureAwait(false);
            default:
                bool ackNow;
                if (a.SendSequence != _vr)
                {
                    await CloseAsync($"sequence error: expected N(S)={_vr}, got {a.SendSequence}").ConfigureAwait(false);
                    return false;
                }
                if (!await AcknowledgedAsync(a.ReceiveSequence).ConfigureAwait(false)) return false;
                lock (_gate)
                {
                    _vr = (ushort)((_vr + 1) & 0x7FFF);
                    _rxSinceAck++;
                    _firstUnackedRx ??= _time.GetUtcNow();
                    ackNow = _rxSinceAck >= Parameters.W;
                }
                IFramesReceived++;
                if (ackNow) await SendAckAsync(ct).ConfigureAwait(false);
                if (a.Asdu is not null) await _received.Writer.WriteAsync(a.Asdu, ct).ConfigureAwait(false);
                return true;
        }
    }

    /// <summary>Processes a received N(R): releases acknowledged I-frames; an N(R) outside the window is a protocol error.</summary>
    private async Task<bool> AcknowledgedAsync(ushort nr)
    {
        bool ok;
        lock (_gate)
        {
            int outstanding = (_vs - _ackRx) & 0x7FFF;
            int acked = (nr - _ackRx) & 0x7FFF;
            ok = acked <= outstanding;
            if (ok)
            {
                for (int i = 0; i < acked && _unacked.Count > 0; i++) _unacked.Dequeue();
                _ackRx = nr;
            }
        }
        if (!ok) { await CloseAsync($"N(R)={nr} acknowledges frames never sent").ConfigureAwait(false); return false; }
        _ackSignal.Release();
        return true;
    }

    // ------------------------------------------------------------ timers

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(Math.Min(Parameters.T2.TotalMilliseconds, Parameters.T3.TotalMilliseconds) / 10, 5, 200));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tick, _time, ct).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                bool t1Data, t1Test, t2, t3;
                lock (_gate)
                {
                    t1Data = _unacked.Count > 0 && now - _unacked.Peek().Time >= Parameters.T1;
                    t1Test = _testSent is { } ts && now - ts >= Parameters.T1;
                    t2 = _firstUnackedRx is { } fr && now - fr >= Parameters.T2;
                    t3 = _testSent is null && now - _lastRx >= Parameters.T3;
                }
                if (t1Data) { await CloseAsync("t1 expired: I-frames not acknowledged").ConfigureAwait(false); return; }
                if (t1Test) { await CloseAsync("t1 expired: no TESTFR con").ConfigureAwait(false); return; }
                if (t2) await SendAckAsync(ct).ConfigureAwait(false);
                if (t3)
                {
                    lock (_gate) _testSent = now;
                    await SendRawAsync(Iec104Codec.Encode(Apdu.U(UFunction.TestFrAct)), ct).ConfigureAwait(false);
                    TestFramesSent++;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    // ------------------------------------------------------------ closing

    internal async Task CloseAsync(string reason)
    {
        lock (_gate)
        {
            if (CloseReason is not null) return;
            CloseReason = reason;
        }
        DataTransferActive = false;
        _received.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _stream.Dispose(); } catch { /* already closed */ }
        _closed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed").ConfigureAwait(false);
        foreach (var t in new[] { _reader, _timers })
            if (t is not null) try { await t.ConfigureAwait(false); } catch { /* closing */ }
    }

    /// <summary>Not used by the controlled station.</summary>
    internal bool IsControlling => _controlling;
}
