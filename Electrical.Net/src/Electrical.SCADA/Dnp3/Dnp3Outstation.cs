using System.Net;
using System.Net.Sockets;

namespace Electrical.SCADA.Dnp3;

/// <summary>Configuration of a <see cref="Dnp3Outstation"/>.</summary>
public sealed record Dnp3OutstationOptions
{
    /// <summary>Link address of the outstation.</summary>
    public ushort LocalAddress { get; init; } = 1024;
    /// <summary>Static binary input variation for class 0 / g1v0 (1 = packed, 2 = with flags).</summary>
    public byte StaticBinaryVariation { get; init; } = 2;
    /// <summary>Static analog input variation for class 0 / g30v0 (1 = int32 with flag, 2 = int16, 5 = float with flag).</summary>
    public byte StaticAnalogVariation { get; init; } = 5;
    /// <summary>Static analog output status variation (g40: 1 = int32, 2 = int16, 3 = float).</summary>
    public byte StaticAnalogOutputVariation { get; init; } = 3;
    /// <summary>Binary input event variation (g2: 1 = without time, 2 = with absolute time).</summary>
    public byte EventBinaryVariation { get; init; } = 2;
    /// <summary>Analog input event variation (g32: 1 = int32, 3 = int32 + time, 5 = float, 7 = float + time).</summary>
    public byte EventAnalogVariation { get; init; } = 7;
    /// <summary>Maximum application fragment size of responses [octets]; larger responses are split into confirmed fragments.</summary>
    public int MaxFragmentSize { get; init; } = 2048;
    /// <summary>Maximum time between SELECT and OPERATE.</summary>
    public TimeSpan SelectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum number of controls in one request (status TOO_MANY_OBJS beyond).</summary>
    public int MaxControlsPerRequest { get; init; } = 16;
    /// <summary>Accept ENABLE_UNSOLICITED and report events spontaneously.</summary>
    public bool AllowUnsolicited { get; init; } = true;
    /// <summary>Set IIN1.4 NEED_TIME until the master writes g50v1.</summary>
    public bool NeedTimeAtStartup { get; init; }
    /// <summary>
    /// Executes a CROB on binary output <c>index</c>; null = default behaviour (latch/pulse/trip-close sets the binary output status).
    /// </summary>
    public Func<ushort, Crob, Dnp3CommandStatus>? BinaryCommandHandler { get; init; }
    /// <summary>Executes an analog output command on <c>index</c> [engineering units]; null = store it in the analog output status.</summary>
    public Func<ushort, double, Dnp3CommandStatus>? AnalogCommandHandler { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>Executed control (raised by <see cref="Dnp3Outstation.Commanded"/>).</summary>
public sealed record Dnp3CommandEvent(Dnp3PointType Type, ushort Index, Crob? Crob, double? AnalogValue, Dnp3CommandStatus Status, bool Selected);

/// <summary>
/// IEEE 1815 (DNP3) outstation over TCP (default port 20000) serving a <see cref="Dnp3Database"/>: class 0 integrity polls, class 1–3
/// event polls with application confirmation (events are removed only when confirmed), static/event reads by group, select-before-operate
/// and direct operate for CROB (g12v1) and analog output (g41v1–3) with echo, time write (g50v1), IIN1.7 restart clearing (g80v1),
/// delay measurement (g52v2) and unsolicited responses. Responses larger than <see cref="Dnp3OutstationOptions.MaxFragmentSize"/> are
/// sent as multi-fragment responses, each fragment confirmed before the next.
/// </summary>
public sealed class Dnp3Outstation : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly List<Task> _sessions = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private TimeSpan _clockOffset;
    private Dnp3Iin _deviceIin = Dnp3Iin.DeviceRestart;

    public Dnp3Outstation(Dnp3Database database, Dnp3OutstationOptions? options = null, IPAddress? address = null, int port = 20000)
    {
        Database = database;
        Options = options ?? new Dnp3OutstationOptions();
        if (Options.NeedTimeAtStartup) _deviceIin |= Dnp3Iin.NeedTime;
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
    }

    public Dnp3Database Database { get; }
    public Dnp3OutstationOptions Options { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>Outstation clock (system time plus the offset set by g50v1 writes).</summary>
    public DateTimeOffset Now => Options.Time.GetUtcNow() + _clockOffset;
    /// <summary>Device-level IIN bits (restart, need time).</summary>
    public Dnp3Iin DeviceIin { get { lock (_gate) return _deviceIin; } }
    /// <summary>Raised after a control has been executed (or rejected) by OPERATE / DIRECT_OPERATE.</summary>
    public event Action<Dnp3CommandEvent>? Commanded;

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
        lock (_gate) all = _accept is null ? [.. _sessions] : [.. _sessions, _accept];
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
            var t = new Session(this, c).RunAsync(ct);
            lock (_gate) { _sessions.RemoveAll(x => x.IsCompleted); _sessions.Add(t); }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record OutFragment(byte[] Objects, List<long> EventIds, Dnp3Iin Iin);

    private sealed class Session(Dnp3Outstation os, TcpClient client)
    {
        private readonly Lock _state = new();
        private Dnp3Channel _ch = null!;
        private ushort _master;
        // solicited confirm / multi-fragment state
        private int _pendingSeq = -1;
        private List<long> _pendingEvents = [];
        private readonly Queue<OutFragment> _remaining = new();
        // select-before-operate
        private int _selSeq = -1;
        private byte[]? _selObjects;
        private DateTimeOffset _selTime;
        // unsolicited
        private readonly HashSet<Dnp3PointClass> _unsClasses = [];
        private int _unsSeq;
        private int _unsPendingSeq = -1;
        private List<long> _unsPendingEvents = [];
        private readonly SemaphoreSlim _unsSignal = new(0, int.MaxValue);

        private Dnp3OutstationOptions O => os.Options;
        private Dnp3Database Db => os.Database;

        public async Task RunAsync(CancellationToken serverCt)
        {
            using var _ = client;
            client.NoDelay = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            var ct = cts.Token;
            _ch = new Dnp3Channel(client.GetStream(), isMaster: false, O.LocalAddress);
            void OnEvent(Dnp3Event e) => _unsSignal.Release();
            Db.EventAdded += OnEvent;
            var uns = UnsolicitedLoopAsync(ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var r = await _ch.ReceiveAsync(ct).ConfigureAwait(false);
                    if (r is null) break;
                    _master = r.Value.Source;
                    Dnp3Fragment req;
                    try { req = Dnp3Fragment.Decode(r.Value.Fragment); }
                    catch (InvalidDataException) { continue; }
                    await HandleAsync(req, r.Value.Fragment, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException or InvalidDataException or ObjectDisposedException or SocketException)
            {
                // master gone, framing error or stopping
            }
            finally
            {
                Db.EventAdded -= OnEvent;
                await cts.CancelAsync().ConfigureAwait(false);
                try { await uns.ConfigureAwait(false); } catch { /* cancelled */ }
            }
        }

        private Task SendAsync(Dnp3Fragment f, CancellationToken ct) => _ch.SendAsync(f.Encode(), _master, ct);

        private async Task HandleAsync(Dnp3Fragment req, byte[] raw, CancellationToken ct)
        {
            byte seq = req.Control.Seq;
            if (req.Function == Dnp3Function.Confirm)
            {
                await OnConfirmAsync(req.Control, ct).ConfigureAwait(false);
                return;
            }
            // a new request cancels any outstanding solicited confirm / remaining fragments (events stay buffered)
            lock (_state) { _pendingSeq = -1; _pendingEvents = []; _remaining.Clear(); }

            List<OutFragment> frags;
            switch (req.Function)
            {
                case Dnp3Function.Read: frags = Read(req.Objects); break;
                case Dnp3Function.Write: frags = [Null(Write(req.Objects))]; break;
                case Dnp3Function.Select or Dnp3Function.Operate or Dnp3Function.DirectOperate or Dnp3Function.DirectOperateNoAck:
                    var (echo, iin) = Control(req.Function, seq, req.Objects, raw.AsSpan(2).ToArray());
                    if (req.Function == Dnp3Function.DirectOperateNoAck) return;
                    frags = [new OutFragment(echo, [], iin)];
                    break;
                case Dnp3Function.EnableUnsolicited or Dnp3Function.DisableUnsolicited:
                    frags = [Null(Unsolicited(req.Function == Dnp3Function.EnableUnsolicited, req.Objects))];
                    if (req.Function == Dnp3Function.EnableUnsolicited) _unsSignal.Release();
                    break;
                case Dnp3Function.DelayMeasure:
                    frags = [new OutFragment(new Dnp3ObjectWriter().Count(52, 2, [[0, 0]]).ToArray(), [], Dnp3Iin.None)];
                    break;
                case Dnp3Function.RecordCurrentTime: frags = [Null(Dnp3Iin.None)]; break;
                default: frags = [Null(Dnp3Iin.NoFunctionCodeSupport)]; break;
            }
            await SendResponseAsync(frags, seq, ct).ConfigureAwait(false);
        }

        private static OutFragment Null(Dnp3Iin iin) => new([], [], iin);

        private Dnp3Iin Iin(Dnp3Iin extra) => os.DeviceIin | Db.EventIin() | extra;

        private async Task SendResponseAsync(List<OutFragment> frags, byte seq, CancellationToken ct)
        {
            var first = frags[0];
            bool fin = frags.Count == 1;
            bool con = !fin || first.EventIds.Count > 0;
            lock (_state)
            {
                for (int i = 1; i < frags.Count; i++) _remaining.Enqueue(frags[i]);
                if (con) { _pendingSeq = seq; _pendingEvents = first.EventIds; }
            }
            var ac = new Dnp3AppControl(true, fin, con, false, seq);
            await SendAsync(new Dnp3Fragment(ac, Dnp3Function.Response, Iin(first.Iin), first.Objects), ct).ConfigureAwait(false);
        }

        private async Task OnConfirmAsync(Dnp3AppControl c, CancellationToken ct)
        {
            if (c.Uns)
            {
                lock (_state)
                {
                    if (c.Seq != _unsPendingSeq) return;
                    Db.Acknowledge(_unsPendingEvents);
                    _unsPendingSeq = -1; _unsPendingEvents = [];
                    _unsSeq = (_unsSeq + 1) & 0x0F;
                }
                _unsSignal.Release(); // more events may be waiting
                return;
            }
            Dnp3Fragment toSend;
            lock (_state)
            {
                if (c.Seq != _pendingSeq) return;
                Db.Acknowledge(_pendingEvents);
                if (!_remaining.TryDequeue(out var next)) { _pendingSeq = -1; _pendingEvents = []; return; }
                byte seq = (byte)((_pendingSeq + 1) & 0x0F);
                bool fin = _remaining.Count == 0;
                bool con = !fin || next.EventIds.Count > 0;
                _pendingSeq = con ? seq : -1;
                _pendingEvents = next.EventIds;
                toSend = new Dnp3Fragment(new Dnp3AppControl(false, fin, con, false, seq), Dnp3Function.Response, next.Iin, next.Objects);
            }
            await SendAsync(toSend with { Iin = Iin(toSend.Iin) }, ct).ConfigureAwait(false);
        }

        // ------------------------------- READ -------------------------------

        private sealed class Builder(int maxObjects)
        {
            public readonly List<OutFragment> Fragments = [];
            private Dnp3ObjectWriter _w = new();
            private List<long> _ids = [];
            public Dnp3Iin Iin;

            public void Add(byte[] block, IEnumerable<long>? ids = null)
            {
                if (_w.Length > 0 && _w.Length + block.Length > maxObjects) Flush();
                _w.Raw(block);
                if (ids is not null) _ids.AddRange(ids);
            }

            public void Flush()
            {
                if (_w.Length == 0 && Fragments.Count > 0) return;
                Fragments.Add(new OutFragment(_w.ToArray(), _ids, Dnp3Iin.None));
                _w = new(); _ids = [];
            }

            public List<OutFragment> Finish()
            {
                if (_w.Length > 0 || Fragments.Count == 0) Flush();
                Fragments[0] = Fragments[0] with { Iin = Iin };
                return Fragments;
            }
        }

        private int ChunkFor(int objectSize) => Math.Max(1, (O.MaxFragmentSize - 4 - 8) / Math.Max(1, objectSize));

        private List<OutFragment> Read(byte[] objects)
        {
            var b = new Builder(O.MaxFragmentSize - 4);
            List<Dnp3ObjectHeader> headers;
            try { headers = Dnp3ObjectParser.Parse(objects, withData: false); }
            catch (NotSupportedException) { b.Iin |= Dnp3Iin.ObjectUnknown; return b.Finish(); }
            catch (InvalidDataException) { b.Iin |= Dnp3Iin.ParameterError; return b.Finish(); }
            foreach (var h in headers)
            {
                uint? start = h.Qualifier is Dnp3Qualifier.Range8 or Dnp3Qualifier.Range16 ? h.Start : null;
                uint? stop = start is null ? null : h.Stop;
                int limit = h.Qualifier is Dnp3Qualifier.Count8 or Dnp3Qualifier.Count16 ? h.Count : int.MaxValue;
                switch (h.Group, h.Variation)
                {
                    case (60, 1):
                        Static(b, Dnp3PointType.BinaryInput, O.StaticBinaryVariation, null, null);
                        Static(b, Dnp3PointType.BinaryOutput, 2, null, null);
                        Static(b, Dnp3PointType.AnalogInput, O.StaticAnalogVariation, null, null);
                        Static(b, Dnp3PointType.AnalogOutput, O.StaticAnalogOutputVariation, null, null);
                        break;
                    case (60, 2 or 3 or 4): Events(b, [(Dnp3PointClass)(h.Variation - 1)], limit, null, 0); break;
                    case (1, 0 or 1 or 2): Static(b, Dnp3PointType.BinaryInput, h.Variation == 0 ? O.StaticBinaryVariation : h.Variation, start, stop); break;
                    case (10, 0 or 1 or 2): Static(b, Dnp3PointType.BinaryOutput, h.Variation == 0 ? (byte)2 : h.Variation, start, stop); break;
                    case (30, 0 or 1 or 2 or 5): Static(b, Dnp3PointType.AnalogInput, h.Variation == 0 ? O.StaticAnalogVariation : h.Variation, start, stop); break;
                    case (40, 0 or 1 or 2 or 3): Static(b, Dnp3PointType.AnalogOutput, h.Variation == 0 ? O.StaticAnalogOutputVariation : h.Variation, start, stop); break;
                    case (2, 0 or 1 or 2): Events(b, [Dnp3PointClass.Class1, Dnp3PointClass.Class2, Dnp3PointClass.Class3], limit, Dnp3PointType.BinaryInput, h.Variation); break;
                    case (32, 0 or 1 or 3 or 5 or 7): Events(b, [Dnp3PointClass.Class1, Dnp3PointClass.Class2, Dnp3PointClass.Class3], limit, Dnp3PointType.AnalogInput, h.Variation); break;
                    case (50, 1): b.Add(new Dnp3ObjectWriter().Count(50, 1, [Dnp3Objects.Time(os.Now)]).ToArray()); break;
                    default: b.Iin |= Dnp3Iin.ObjectUnknown; break;
                }
            }
            return b.Finish();
        }

        private void Static(Builder b, Dnp3PointType type, byte variation, uint? start, uint? stop)
        {
            byte group = type switch { Dnp3PointType.BinaryInput => 1, Dnp3PointType.BinaryOutput => 10, Dnp3PointType.AnalogInput => 30, _ => 40 };
            var pts = Db.Points(type).Where(p => start is null || (p.Index >= start && p.Index <= stop)).ToList();
            if (start is not null && pts.Count != (int)(stop!.Value - start.Value + 1)) b.Iin |= Dnp3Iin.ParameterError;
            int size = Dnp3ObjectParser.DataSize(group, variation) is int s && s > 0 ? s : 1;
            int chunk = ChunkFor(size);
            // contiguous runs → range headers
            int i = 0;
            while (i < pts.Count)
            {
                int j = i;
                while (j + 1 < pts.Count && pts[j + 1].Index == pts[j].Index + 1 && j + 1 - i < chunk) j++;
                var run = pts.GetRange(i, j - i + 1);
                var w = new Dnp3ObjectWriter();
                if (variation == 1 && group is 1 or 10) w.PackedRange(group, 1, run[0].Index, run.Select(p => p.State).ToArray());
                else if (group is 1 or 10) w.Range(group, 2, run[0].Index, run.Select(p => Dnp3Objects.Binary(p.State, p.Flags)).ToArray());
                else w.Range(group, variation, run[0].Index, run.Select(p => Dnp3Objects.Analog(group, variation, p.Value, p.Flags)).ToArray());
                b.Add(w.ToArray());
                i = j + 1;
            }
        }

        private void Events(Builder b, Dnp3PointClass[] classes, int limit, Dnp3PointType? type, byte variation)
        {
            var evs = Db.PeekEvents(classes, limit, type);
            // binary and analog events go into separate headers; consecutive runs keep the overall order
            int i = 0;
            while (i < evs.Count)
            {
                var t = evs[i].Type;
                int j = i;
                while (j + 1 < evs.Count && evs[j + 1].Type == t && j + 1 - i < ChunkFor(13)) j++;
                var run = evs.GetRange(i, j - i + 1);
                var w = new Dnp3ObjectWriter();
                if (t == Dnp3PointType.BinaryInput)
                {
                    byte v = variation == 0 ? O.EventBinaryVariation : variation;
                    w.Prefixed(2, v, run.Select(e => ((uint)e.Index, v == 2 ? Dnp3Objects.BinaryWithTime(e.Value != 0, e.Flags, e.Time) : Dnp3Objects.Binary(e.Value != 0, e.Flags))).ToArray(), forceWide: true);
                }
                else
                {
                    byte v = variation == 0 ? O.EventAnalogVariation : variation;
                    w.Prefixed(32, v, run.Select(e => ((uint)e.Index, Dnp3Objects.Analog(32, v, e.Value, e.Flags, e.Time))).ToArray(), forceWide: true);
                }
                b.Add(w.ToArray(), run.Select(e => e.Id));
                i = j + 1;
            }
        }

        // ------------------------------- WRITE -------------------------------

        private Dnp3Iin Write(byte[] objects)
        {
            List<Dnp3ObjectHeader> headers;
            try { headers = Dnp3ObjectParser.Parse(objects, withData: true); }
            catch (NotSupportedException) { return Dnp3Iin.ObjectUnknown; }
            catch (InvalidDataException) { return Dnp3Iin.ParameterError; }
            var iin = Dnp3Iin.None;
            foreach (var h in headers)
            {
                if (h is { Group: 50, Variation: 1, Items.Count: 1 })
                {
                    var t = Dnp3Objects.ReadTime(h.Items[0].Data);
                    os._clockOffset = t - O.Time.GetUtcNow();
                    lock (os._gate) os._deviceIin &= ~Dnp3Iin.NeedTime;
                }
                else if (h is { Group: 80, Variation: 1 })
                {
                    foreach (var it in h.Items)
                    {
                        if (it.Index == 7 && it.Data[0] == 0) { lock (os._gate) os._deviceIin &= ~Dnp3Iin.DeviceRestart; }
                        else iin |= Dnp3Iin.ParameterError;
                    }
                }
                else iin |= Dnp3Iin.ObjectUnknown;
            }
            return iin;
        }

        // ------------------------------- CONTROLS -------------------------------

        private (byte[] Echo, Dnp3Iin Iin) Control(Dnp3Function fc, byte seq, byte[] objects, byte[] rawObjects)
        {
            List<Dnp3ObjectHeader> headers;
            try { headers = Dnp3ObjectParser.Parse(objects, withData: true); }
            catch (NotSupportedException) { return (objects, Dnp3Iin.ObjectUnknown); }
            catch (InvalidDataException) { return (objects, Dnp3Iin.ParameterError); }

            bool select = fc == Dnp3Function.Select;
            bool operate = fc == Dnp3Function.Operate;
            bool selectionValid = false;
            if (operate)
            {
                lock (_state)
                {
                    selectionValid = _selObjects is not null && seq == ((_selSeq + 1) & 0x0F) &&
                        rawObjects.AsSpan().SequenceEqual(_selObjects) && os.Now - _selTime <= O.SelectTimeout;
                    _selObjects = null; _selSeq = -1;
                }
            }
            var iin = Dnp3Iin.None;
            int controls = 0;
            var echo = new Dnp3ObjectWriter();
            bool anyGood = false;
            foreach (var h in headers)
            {
                bool isCrob = h is { Group: 12, Variation: 1 };
                bool isAo = h.Group == 41 && h.Variation is 1 or 2 or 3;
                if (!isCrob && !isAo) { iin |= Dnp3Iin.ObjectUnknown; continue; }
                var items = new List<(uint, byte[])>();
                foreach (var it in h.Items)
                {
                    controls++;
                    Dnp3CommandStatus st;
                    ushort idx = (ushort)it.Index;
                    if (isCrob)
                    {
                        var crob = Crob.Decode(it.Data);
                        st = controls > O.MaxControlsPerRequest ? Dnp3CommandStatus.TooManyObjects
                            : !Db.Contains(Dnp3PointType.BinaryOutput, idx) ? Dnp3CommandStatus.NotSupported
                            : crob.TargetState is null && O.BinaryCommandHandler is null ? Dnp3CommandStatus.NotSupported
                            : operate && !selectionValid ? Dnp3CommandStatus.NoSelect
                            : Dnp3CommandStatus.Success;
                        if (st == Dnp3CommandStatus.Success && !select) st = ExecuteBinary(idx, crob, operate);
                        items.Add((it.Index, (crob with { Status = st }).Encode()));
                    }
                    else
                    {
                        var (value, _) = Dnp3Objects.ReadAnalogOutput(h.Variation, it.Data);
                        st = controls > O.MaxControlsPerRequest ? Dnp3CommandStatus.TooManyObjects
                            : !Db.Contains(Dnp3PointType.AnalogOutput, idx) ? Dnp3CommandStatus.NotSupported
                            : operate && !selectionValid ? Dnp3CommandStatus.NoSelect
                            : Dnp3CommandStatus.Success;
                        if (st == Dnp3CommandStatus.Success && !select) st = ExecuteAnalog(idx, value, operate);
                        items.Add((it.Index, Dnp3Objects.AnalogOutput(h.Variation, value, st)));
                    }
                    anyGood |= st == Dnp3CommandStatus.Success;
                }
                if (h.Qualifier is Dnp3Qualifier.Prefix8Count8 or Dnp3Qualifier.Prefix16Count16)
                    echo.Prefixed(h.Group, h.Variation, items, forceWide: h.Qualifier == Dnp3Qualifier.Prefix16Count16);
                else echo.Range(h.Group, h.Variation, h.Start, items.Select(x => x.Item2).ToArray());
            }
            if (select && anyGood)
                lock (_state) { _selSeq = seq; _selObjects = rawObjects; _selTime = os.Now; }
            return (echo.ToArray(), iin);
        }

        private Dnp3CommandStatus ExecuteBinary(ushort idx, Crob crob, bool selected)
        {
            Dnp3CommandStatus st;
            if (O.BinaryCommandHandler is { } h) st = h(idx, crob);
            else { Db.UpdateBinary(Dnp3PointType.BinaryOutput, idx, crob.TargetState!.Value); st = Dnp3CommandStatus.Success; }
            os.Commanded?.Invoke(new Dnp3CommandEvent(Dnp3PointType.BinaryOutput, idx, crob, null, st, selected));
            return st;
        }

        private Dnp3CommandStatus ExecuteAnalog(ushort idx, double value, bool selected)
        {
            Dnp3CommandStatus st;
            if (O.AnalogCommandHandler is { } h) st = h(idx, value);
            else { Db.Update(Dnp3PointType.AnalogOutput, idx, value); st = Dnp3CommandStatus.Success; }
            os.Commanded?.Invoke(new Dnp3CommandEvent(Dnp3PointType.AnalogOutput, idx, null, value, st, selected));
            return st;
        }

        // ------------------------------- UNSOLICITED -------------------------------

        private Dnp3Iin Unsolicited(bool enable, byte[] objects)
        {
            if (!O.AllowUnsolicited) return Dnp3Iin.NoFunctionCodeSupport;
            List<Dnp3ObjectHeader> headers;
            try { headers = Dnp3ObjectParser.Parse(objects, withData: false); }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException) { return Dnp3Iin.ParameterError; }
            var iin = Dnp3Iin.None;
            lock (_state)
            {
                foreach (var h in headers)
                {
                    if (h.Group == 60 && h.Variation is 2 or 3 or 4)
                    {
                        var c = (Dnp3PointClass)(h.Variation - 1);
                        if (enable) _unsClasses.Add(c); else _unsClasses.Remove(c);
                    }
                    else iin |= Dnp3Iin.ObjectUnknown;
                }
            }
            return iin;
        }

        private async Task UnsolicitedLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _unsSignal.WaitAsync(ct).ConfigureAwait(false);
                    Dnp3Fragment? f = null;
                    lock (_state)
                    {
                        if (_unsClasses.Count == 0 || _unsPendingSeq >= 0) continue;
                        var b = new Builder(O.MaxFragmentSize - 4);
                        Events(b, [.. _unsClasses], int.MaxValue, null, 0);
                        var frag = b.Finish()[0];
                        if (frag.EventIds.Count == 0) continue;
                        _unsPendingSeq = _unsSeq;
                        _unsPendingEvents = frag.EventIds;
                        f = new Dnp3Fragment(Dnp3AppControl.Single((byte)_unsSeq, con: true, uns: true), Dnp3Function.UnsolicitedResponse, Dnp3Iin.None, frag.Objects);
                    }
                    await SendAsync(f with { Iin = Iin(Dnp3Iin.None) }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
        }
    }
}
