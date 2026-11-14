using System.Net.Sockets;
using System.Threading.Channels;

namespace Electrical.SCADA.Dnp3;

/// <summary>Values decoded from response objects: static points by index, events in received order, and time objects.</summary>
public sealed class Dnp3MeasurementSet
{
    public Dictionary<ushort, Dnp3Point> BinaryInputs { get; } = [];
    public Dictionary<ushort, Dnp3Point> AnalogInputs { get; } = [];
    public Dictionary<ushort, Dnp3Point> BinaryOutputs { get; } = [];
    public Dictionary<ushort, Dnp3Point> AnalogOutputs { get; } = [];
    public List<Dnp3Event> Events { get; } = [];
    /// <summary>Last g50v1 time received.</summary>
    public DateTimeOffset? OutstationTime { get; private set; }
    /// <summary>Last g52 time delay [ms].</summary>
    public int? TimeDelayMs { get; private set; }

    /// <summary>Decodes response object headers. Events update the static tables too (latest value wins).</summary>
    public static Dnp3MeasurementSet From(IEnumerable<Dnp3ObjectHeader> headers, DateTimeOffset receivedAt)
    {
        var m = new Dnp3MeasurementSet();
        foreach (var h in headers) m.Add(h, receivedAt);
        return m;
    }

    private void Add(Dnp3ObjectHeader h, DateTimeOffset now)
    {
        foreach (var it in h.Items)
        {
            ushort i = (ushort)it.Index;
            switch (h.Group, h.Variation)
            {
                case (1, 1): BinaryInputs[i] = new(Dnp3PointType.BinaryInput, i, it.Data[0], Dnp3Flags.Online, now, Dnp3PointClass.Class0); break;
                case (1, 2): BinaryInputs[i] = Bin(Dnp3PointType.BinaryInput, i, it.Data[0], now); break;
                case (10, 1): BinaryOutputs[i] = new(Dnp3PointType.BinaryOutput, i, it.Data[0], Dnp3Flags.Online, now, Dnp3PointClass.Class0); break;
                case (10, 2): BinaryOutputs[i] = Bin(Dnp3PointType.BinaryOutput, i, it.Data[0], now); break;
                case (30, _):
                {
                    var (v, f, _) = Dnp3Objects.ReadAnalog(30, h.Variation, it.Data);
                    AnalogInputs[i] = new(Dnp3PointType.AnalogInput, i, v, f, now, Dnp3PointClass.Class0);
                    break;
                }
                case (40, _):
                {
                    var (v, f, _) = Dnp3Objects.ReadAnalog(40, h.Variation, it.Data);
                    AnalogOutputs[i] = new(Dnp3PointType.AnalogOutput, i, v, f, now, Dnp3PointClass.Class0);
                    break;
                }
                case (2, 1 or 2):
                {
                    bool st = (it.Data[0] & 0x80) != 0;
                    var t = h.Variation == 2 ? Dnp3Objects.ReadTime(it.Data.AsSpan(1)) : now;
                    var f = (Dnp3Flags)(it.Data[0] & 0x7F);
                    Events.Add(new Dnp3Event(0, Dnp3PointType.BinaryInput, i, st ? 1 : 0, f, t, Dnp3PointClass.Class0));
                    BinaryInputs[i] = new(Dnp3PointType.BinaryInput, i, st ? 1 : 0, f, t, Dnp3PointClass.Class0);
                    break;
                }
                case (32, _):
                {
                    var (v, f, t) = Dnp3Objects.ReadAnalog(32, h.Variation, it.Data);
                    Events.Add(new Dnp3Event(0, Dnp3PointType.AnalogInput, i, v, f, t ?? now, Dnp3PointClass.Class0));
                    AnalogInputs[i] = new(Dnp3PointType.AnalogInput, i, v, f, t ?? now, Dnp3PointClass.Class0);
                    break;
                }
                case (50, 1): OutstationTime = Dnp3Objects.ReadTime(it.Data); break;
                case (52, _): TimeDelayMs = it.Data[0] | (it.Data[1] << 8); break;
            }
        }
    }

    private static Dnp3Point Bin(Dnp3PointType t, ushort i, byte flags, DateTimeOffset now) =>
        new(t, i, (flags & 0x80) != 0 ? 1 : 0, (Dnp3Flags)(flags & 0x7F), now, Dnp3PointClass.Class0);

    /// <summary>Merges another set (newer values overwrite, events are appended).</summary>
    public void Merge(Dnp3MeasurementSet other)
    {
        foreach (var (k, v) in other.BinaryInputs) BinaryInputs[k] = v;
        foreach (var (k, v) in other.AnalogInputs) AnalogInputs[k] = v;
        foreach (var (k, v) in other.BinaryOutputs) BinaryOutputs[k] = v;
        foreach (var (k, v) in other.AnalogOutputs) AnalogOutputs[k] = v;
        Events.AddRange(other.Events);
        OutstationTime = other.OutstationTime ?? OutstationTime;
        TimeDelayMs = other.TimeDelayMs ?? TimeDelayMs;
    }
}

/// <summary>A complete (possibly multi-fragment) response.</summary>
public sealed record Dnp3Response(Dnp3Iin Iin, IReadOnlyList<Dnp3ObjectHeader> Objects, Dnp3MeasurementSet Measurements, int Fragments);

/// <summary>
/// Minimal DNP3 master over TCP: one outstanding request at a time, automatic application confirms (solicited and unsolicited),
/// multi-fragment response assembly, integrity/event polls, SBO and direct operate, time write, restart-IIN clearing, and
/// unsolicited enable/disable. All received values are also merged into <see cref="Values"/>.
/// </summary>
public sealed class Dnp3Master : IAsyncDisposable
{
    private readonly SemaphoreSlim _request = new(1, 1);
    private Channel<Dnp3Fragment> _responses = Channel.CreateUnbounded<Dnp3Fragment>();
    private readonly Lock _gate = new();
    private TcpClient? _tcp;
    private Dnp3Channel? _ch;
    private CancellationTokenSource? _cts;
    private Task? _reader;
    private int _seq = -1;

    public Dnp3Master(ushort localAddress = 1, ushort outstationAddress = 1024)
    {
        LocalAddress = localAddress;
        OutstationAddress = outstationAddress;
    }

    public ushort LocalAddress { get; }
    public ushort OutstationAddress { get; }
    /// <summary>Response timeout per fragment (default 5 s).</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Accumulated values of all responses.</summary>
    public Dnp3MeasurementSet Values { get; } = new();
    /// <summary>IIN of the last response.</summary>
    public Dnp3Iin LastIin { get; private set; }
    /// <summary>Raised (on the receive task) for each unsolicited response, after it has been confirmed.</summary>
    public event Action<Dnp3Response>? UnsolicitedReceived;

    public async Task ConnectAsync(string host, int port = 20000, CancellationToken ct = default)
    {
        await DisposeAsync().ConfigureAwait(false);
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _ch = new Dnp3Channel(_tcp.GetStream(), isMaster: true, LocalAddress);
        _responses = Channel.CreateUnbounded<Dnp3Fragment>();
        _cts = new CancellationTokenSource();
        _reader = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await _ch!.ReceiveAsync(ct).ConfigureAwait(false);
                if (r is null) break;
                Dnp3Fragment f;
                try { f = Dnp3Fragment.Decode(r.Value.Fragment); } catch (InvalidDataException) { continue; }
                if (f.Function == Dnp3Function.UnsolicitedResponse)
                {
                    if (f.Control.Con) await ConfirmAsync(f.Control.Seq, uns: true, ct).ConfigureAwait(false);
                    var objs = Parse(f.Objects);
                    var m = Dnp3MeasurementSet.From(objs, DateTimeOffset.UtcNow);
                    lock (_gate) Values.Merge(m);
                    UnsolicitedReceived?.Invoke(new Dnp3Response(f.Iin, objs, m, 1));
                }
                else if (f.Function == Dnp3Function.Response) await _responses.Writer.WriteAsync(f, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException or SocketException) { }
        finally { _responses.Writer.TryComplete(); }
    }

    private static List<Dnp3ObjectHeader> Parse(byte[] objects)
    {
        try { return Dnp3ObjectParser.Parse(objects, withData: true); }
        catch (NotSupportedException) { return []; }
    }

    private Task ConfirmAsync(byte seq, bool uns, CancellationToken ct) =>
        _ch!.SendAsync(new Dnp3Fragment(Dnp3AppControl.Single(seq, uns: uns), Dnp3Function.Confirm, Dnp3Iin.None, []).Encode(), OutstationAddress, ct);

    /// <summary>Sends a request and returns the assembled response (null for DIRECT_OPERATE_NR).</summary>
    public async Task<Dnp3Response?> RequestAsync(Dnp3Function function, byte[]? objects = null, CancellationToken ct = default)
    {
        var ch = _ch ?? throw new InvalidOperationException("Not connected.");
        await _request.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte seq = (byte)(Interlocked.Increment(ref _seq) & 0x0F);
            await ch.SendAsync(Dnp3Fragment.Request(seq, function, objects).Encode(), OutstationAddress, ct).ConfigureAwait(false);
            if (function == Dnp3Function.DirectOperateNoAck) return null;
            var all = new List<Dnp3ObjectHeader>();
            var iin = Dnp3Iin.None;
            int expected = seq, count = 0;
            bool started = false;
            while (true)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ResponseTimeout);
                Dnp3Fragment f;
                try { f = await _responses.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"No DNP3 response to {function} within {ResponseTimeout.TotalMilliseconds} ms."); }
                catch (ChannelClosedException) { throw new IOException("DNP3 connection closed."); }
                if (f.Control.Seq != expected || (!started && !f.Control.Fir)) continue; // stale fragment
                started = true;
                count++;
                iin = f.Iin;
                all.AddRange(Parse(f.Objects));
                if (f.Control.Con) await ConfirmAsync(f.Control.Seq, uns: false, ct).ConfigureAwait(false);
                if (f.Control.Fin) break;
                expected = (expected + 1) & 0x0F;
                Interlocked.Exchange(ref _seq, expected); // next request continues after the last fragment sequence
            }
            var m = Dnp3MeasurementSet.From(all, DateTimeOffset.UtcNow);
            lock (_gate) Values.Merge(m);
            LastIin = iin;
            return new Dnp3Response(iin, all, m, count);
        }
        finally { _request.Release(); }
    }

    /// <summary>Integrity poll: READ class 1, 2, 3 then class 0 (g60v2, g60v3, g60v4, g60v1).</summary>
    public async Task<Dnp3Response> IntegrityPollAsync(CancellationToken ct = default) =>
        (await RequestAsync(Dnp3Function.Read, new Dnp3ObjectWriter().All(60, 2).All(60, 3).All(60, 4).All(60, 1).ToArray(), ct).ConfigureAwait(false))!;

    /// <summary>Event poll of the given classes (default 1–3).</summary>
    public async Task<Dnp3Response> EventPollAsync(IEnumerable<Dnp3PointClass>? classes = null, CancellationToken ct = default)
    {
        var w = new Dnp3ObjectWriter();
        foreach (var c in classes ?? [Dnp3PointClass.Class1, Dnp3PointClass.Class2, Dnp3PointClass.Class3]) w.All(60, (byte)(1 + (int)c));
        return (await RequestAsync(Dnp3Function.Read, w.ToArray(), ct).ConfigureAwait(false))!;
    }

    /// <summary>Operates binary output <paramref name="index"/> with a CROB (SELECT then OPERATE when <paramref name="selectBeforeOperate"/>, else DIRECT_OPERATE).</summary>
    public async Task<Dnp3CommandStatus> OperateBinaryAsync(ushort index, Crob crob, bool selectBeforeOperate = true, CancellationToken ct = default)
    {
        byte[] obj = new Dnp3ObjectWriter().Prefixed(12, 1, [(index, crob.Encode())], forceWide: index > 0xFF).ToArray();
        return await OperateAsync(obj, selectBeforeOperate, ct).ConfigureAwait(false);
    }

    /// <summary>Operates analog output <paramref name="index"/> (g41 variation 1 = int32, 2 = int16, 3 = float) [engineering units].</summary>
    public async Task<Dnp3CommandStatus> OperateAnalogAsync(ushort index, double value, byte variation = 3, bool selectBeforeOperate = true, CancellationToken ct = default)
    {
        byte[] obj = new Dnp3ObjectWriter().Prefixed(41, variation, [(index, Dnp3Objects.AnalogOutput(variation, value))], forceWide: index > 0xFF).ToArray();
        return await OperateAsync(obj, selectBeforeOperate, ct).ConfigureAwait(false);
    }

    private async Task<Dnp3CommandStatus> OperateAsync(byte[] obj, bool sbo, CancellationToken ct)
    {
        if (sbo)
        {
            var sel = await RequestAsync(Dnp3Function.Select, obj, ct).ConfigureAwait(false);
            var st = Status(sel!);
            if (st != Dnp3CommandStatus.Success) return st;
            return Status((await RequestAsync(Dnp3Function.Operate, obj, ct).ConfigureAwait(false))!);
        }
        return Status((await RequestAsync(Dnp3Function.DirectOperate, obj, ct).ConfigureAwait(false))!);
    }

    /// <summary>Raw SELECT or OPERATE with arbitrary objects (for tests of the select-before-operate rules).</summary>
    public async Task<Dnp3CommandStatus> SendControlAsync(Dnp3Function function, byte[] objects, CancellationToken ct = default) =>
        Status((await RequestAsync(function, objects, ct).ConfigureAwait(false))!);

    private static Dnp3CommandStatus Status(Dnp3Response r)
    {
        foreach (var h in r.Objects)
            foreach (var it in h.Items)
            {
                var st = h.Group == 12 ? (Dnp3CommandStatus)(it.Data[10] & 0x7F) : (Dnp3CommandStatus)(it.Data[^1] & 0x7F);
                if (st != Dnp3CommandStatus.Success) return st;
            }
        return r.Objects.Count == 0 ? Dnp3CommandStatus.Undefined : Dnp3CommandStatus.Success;
    }

    /// <summary>Writes the outstation clock (g50v1, qualifier 0x07).</summary>
    public Task<Dnp3Response?> WriteTimeAsync(DateTimeOffset time, CancellationToken ct = default) =>
        RequestAsync(Dnp3Function.Write, new Dnp3ObjectWriter().Count(50, 1, [Dnp3Objects.Time(time)]).ToArray(), ct);

    /// <summary>Clears IIN1.7 DEVICE_RESTART (WRITE g80v1, index 7 = 0).</summary>
    public Task<Dnp3Response?> ClearRestartAsync(CancellationToken ct = default) =>
        RequestAsync(Dnp3Function.Write, new Dnp3ObjectWriter().PackedRange(80, 1, 7, [false]).ToArray(), ct);

    public Task<Dnp3Response?> EnableUnsolicitedAsync(IEnumerable<Dnp3PointClass>? classes = null, CancellationToken ct = default) =>
        RequestAsync(Dnp3Function.EnableUnsolicited, ClassHeaders(classes), ct);

    public Task<Dnp3Response?> DisableUnsolicitedAsync(IEnumerable<Dnp3PointClass>? classes = null, CancellationToken ct = default) =>
        RequestAsync(Dnp3Function.DisableUnsolicited, ClassHeaders(classes), ct);

    /// <summary>DELAY_MEASURE; returns the outstation processing delay reported in g52v2 [ms].</summary>
    public async Task<int?> DelayMeasureAsync(CancellationToken ct = default) =>
        (await RequestAsync(Dnp3Function.DelayMeasure, null, ct).ConfigureAwait(false))!.Measurements.TimeDelayMs;

    private static byte[] ClassHeaders(IEnumerable<Dnp3PointClass>? classes)
    {
        var w = new Dnp3ObjectWriter();
        foreach (var c in classes ?? [Dnp3PointClass.Class1, Dnp3PointClass.Class2, Dnp3PointClass.Class3]) w.All(60, (byte)(1 + (int)c));
        return w.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        if (_reader is not null) try { await _reader.ConfigureAwait(false); } catch { /* closed */ }
        _cts?.Dispose();
        _cts = null; _reader = null; _tcp = null; _ch = null;
    }
}
