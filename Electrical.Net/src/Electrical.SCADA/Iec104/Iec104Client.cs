using System.Net.Sockets;

namespace Electrical.SCADA.Iec104;

/// <summary>Latest value of an information object received by an <see cref="Iec104Client"/>.</summary>
public sealed record Iec104Value(TypeId Type, InformationObject Object, CauseOfTransmission Cause, DateTimeOffset Received);

/// <summary>Result of a command: positive/negative ActCon and whether ActTerm was received.</summary>
public sealed record Iec104CommandResult(bool Confirmed, bool Terminated, CauseOfTransmission Cause)
{
    public bool Success => Confirmed;
}

/// <summary>
/// IEC 60870-5-104 controlling station (client): connects (t0), STARTDT/STOPDT/TESTFR, general/group interrogation collecting the
/// objects between ActCon and ActTerm, clock synchronisation, single/double/set-point commands with optional select-before-execute,
/// and spontaneous data. Every monitor-direction object updates <see cref="Values"/> and raises <see cref="AsduReceived"/>.
/// One request is outstanding at a time.
/// </summary>
public sealed class Iec104Client : IAsyncDisposable
{
    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Iec104Value> _values = [];
    private TcpClient? _tcp;
    private Iec104Connection? _conn;
    private Task? _reader;
    private Pending? _pending;

    private sealed class Pending(TypeId type, uint ioa)
    {
        public TypeId Type { get; } = type;
        public uint Ioa { get; } = ioa;
        public TaskCompletionSource<Asdu> Con { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Asdu> Term { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(TypeId, InformationObject)> Collected { get; } = [];
        public CauseOfTransmission? CollectCause { get; init; }
    }

    public Iec104Client(Iec104Parameters? parameters = null, ushort commonAddress = 1, byte originatorAddress = 0)
    {
        Parameters = parameters ?? new Iec104Parameters();
        CommonAddress = commonAddress;
        OriginatorAddress = originatorAddress;
    }

    public Iec104Parameters Parameters { get; }
    public ushort CommonAddress { get; }
    public byte OriginatorAddress { get; }
    /// <summary>Timeout for ActCon/ActTerm (default 10 s).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public Iec104Connection Connection => _conn ?? throw new InvalidOperationException("Not connected.");
    public bool IsConnected => _conn is { Completion.IsCompleted: false };
    /// <summary>Raised on the receive task for every monitor-direction ASDU (interrogated, spontaneous, end of initialization …).</summary>
    public event Action<Asdu>? AsduReceived;
    /// <summary>Number of M_EI_NA_1 (end of initialization) received.</summary>
    public int EndOfInitializationCount { get; private set; }

    /// <summary>Latest value per IOA.</summary>
    public IReadOnlyDictionary<uint, Iec104Value> Values { get { lock (_gate) return new Dictionary<uint, Iec104Value>(_values); } }

    /// <summary>Opens the TCP connection within t0 and starts the APCI state machine (call <see cref="StartDataTransferAsync"/> next).</summary>
    public async Task ConnectAsync(string host, int port = 2404, CancellationToken ct = default)
    {
        await DisposeAsync().ConfigureAwait(false);
        _tcp = new TcpClient { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(Parameters.T0);
            try { await _tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("t0 expired while connecting."); }
        }
        _conn = new Iec104Connection(_tcp.GetStream(), Parameters, controlling: true);
        _conn.Start();
        _reader = ReadLoopAsync(_conn);
    }

    public Task StartDataTransferAsync(CancellationToken ct = default) => Connection.StartDataTransferAsync(ct);
    public Task StopDataTransferAsync(CancellationToken ct = default) => Connection.StopDataTransferAsync(ct);
    public Task TestAsync(CancellationToken ct = default) => Connection.TestAsync(ct);

    private async Task ReadLoopAsync(Iec104Connection conn)
    {
        try
        {
            await foreach (var a in conn.Received.ReadAllAsync().ConfigureAwait(false))
            {
                Pending? p;
                lock (_gate) p = _pending;
                if (Asdu.IsMonitor(a.Type) || a.Type == TypeId.M_EI_NA_1)
                {
                    if (a.Type == TypeId.M_EI_NA_1) EndOfInitializationCount++;
                    else
                    {
                        var now = DateTimeOffset.UtcNow;
                        lock (_gate) foreach (var o in a.Objects) _values[o.Address] = new Iec104Value(a.Type, o, a.Cause, now);
                        if (p?.CollectCause is { } cc && a.Cause == cc) lock (_gate) foreach (var o in a.Objects) p.Collected.Add((a.Type, o));
                    }
                    AsduReceived?.Invoke(a);
                    continue;
                }
                if (p is null || a.Type != p.Type || (a.Objects.Count > 0 && a.Objects[0].Address != p.Ioa)) continue;
                switch (a.Cause)
                {
                    case CauseOfTransmission.ActivationConfirmation or CauseOfTransmission.DeactivationConfirmation:
                        p.Con.TrySetResult(a);
                        if (a.Negative) p.Term.TrySetResult(a);
                        break;
                    case CauseOfTransmission.ActivationTermination: p.Term.TrySetResult(a); break;
                    case CauseOfTransmission.UnknownTypeId or CauseOfTransmission.UnknownCause or CauseOfTransmission.UnknownCommonAddress
                        or CauseOfTransmission.UnknownObjectAddress:
                        p.Con.TrySetResult(a); p.Term.TrySetResult(a);
                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
        finally
        {
            Pending? p;
            lock (_gate) p = _pending;
            p?.Con.TrySetException(new IOException("IEC 104 connection closed."));
            p?.Term.TrySetException(new IOException("IEC 104 connection closed."));
        }
    }

    private async Task<(Asdu Con, Asdu? Term, List<(TypeId, InformationObject)> Data)> ExecuteAsync(Asdu request, bool waitTerm,
        CauseOfTransmission? collect, CancellationToken ct)
    {
        var conn = Connection;
        await _op.WaitAsync(ct).ConfigureAwait(false);
        var p = new Pending(request.Type, request.Objects[0].Address) { CollectCause = collect };
        lock (_gate) _pending = p;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            await conn.SendAsync(request, cts.Token).ConfigureAwait(false);
            try
            {
                var con = await p.Con.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                Asdu? term = null;
                if (waitTerm && !con.Negative) term = await p.Term.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                List<(TypeId, InformationObject)> data;
                lock (_gate) data = [.. p.Collected];
                return (con, term, data);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No confirmation of {request.Type} within {RequestTimeout.TotalMilliseconds} ms.");
            }
        }
        finally
        {
            lock (_gate) _pending = null;
            _op.Release();
        }
    }

    /// <summary>
    /// General (QOI 20) or group (QOI 21–36) interrogation: returns the objects received with COT = QOI between ActCon and ActTerm.
    /// Throws <see cref="InvalidOperationException"/> on a negative ActCon.
    /// </summary>
    public async Task<IReadOnlyList<(TypeId Type, InformationObject Object)>> InterrogateAsync(byte qoi = 20, CancellationToken ct = default)
    {
        var req = new Asdu(TypeId.C_IC_NA_1, CauseOfTransmission.Activation, CommonAddress, [new InformationObject(0, qoi)], OriginatorAddress: OriginatorAddress);
        var (con, _, data) = await ExecuteAsync(req, waitTerm: true, collect: (CauseOfTransmission)qoi, ct).ConfigureAwait(false);
        if (con.Negative) throw new InvalidOperationException($"Interrogation rejected ({con.Cause}).");
        return data;
    }

    public Task<IReadOnlyList<(TypeId Type, InformationObject Object)>> GeneralInterrogationAsync(CancellationToken ct = default) => InterrogateAsync(20, ct);

    /// <summary>Clock synchronisation (C_CS_NA_1 act); returns the station time from the ActCon.</summary>
    public async Task<DateTimeOffset?> ClockSyncAsync(DateTimeOffset time, CancellationToken ct = default)
    {
        var req = new Asdu(TypeId.C_CS_NA_1, CauseOfTransmission.Activation, CommonAddress, [new InformationObject(0, 0, Time: time)], OriginatorAddress: OriginatorAddress);
        var (con, _, _) = await ExecuteAsync(req, waitTerm: false, null, ct).ConfigureAwait(false);
        if (con.Negative) throw new InvalidOperationException($"Clock synchronisation rejected ({con.Cause}).");
        return con.Objects.Count > 0 ? con.Objects[0].Time : null;
    }

    /// <summary>Single command C_SC_NA_1 (<paramref name="value"/> = SCS); select then execute when <paramref name="selectBeforeOperate"/>.</summary>
    public Task<Iec104CommandResult> SingleCommandAsync(uint ioa, bool value, bool selectBeforeOperate = false, byte qualifier = 0, CancellationToken ct = default) =>
        CommandAsync(TypeId.C_SC_NA_1, ioa, value ? 1 : 0, selectBeforeOperate, qualifier, ct);

    /// <summary>Double command C_DC_NA_1 (on → DCS 2, off → DCS 1).</summary>
    public Task<Iec104CommandResult> DoubleCommandAsync(uint ioa, bool on, bool selectBeforeOperate = false, byte qualifier = 0, CancellationToken ct = default) =>
        CommandAsync(TypeId.C_DC_NA_1, ioa, on ? 2 : 1, selectBeforeOperate, qualifier, ct);

    /// <summary>Set-point command, short floating point (C_SE_NC_1) [engineering units of the mapped tag].</summary>
    public Task<Iec104CommandResult> SetpointAsync(uint ioa, float value, bool selectBeforeOperate = false, CancellationToken ct = default) =>
        CommandAsync(TypeId.C_SE_NC_1, ioa, value, selectBeforeOperate, 0, ct);

    private async Task<Iec104CommandResult> CommandAsync(TypeId type, uint ioa, double value, bool sbo, byte qu, CancellationToken ct)
    {
        if (sbo)
        {
            var sel = new Asdu(type, CauseOfTransmission.Activation, CommonAddress, [new InformationObject(ioa, value, Select: true, Qualifier: qu)], OriginatorAddress: OriginatorAddress);
            var (sc, _, _) = await ExecuteAsync(sel, waitTerm: false, null, ct).ConfigureAwait(false);
            if (sc.Negative) return new Iec104CommandResult(false, false, sc.Cause);
        }
        var exe = new Asdu(type, CauseOfTransmission.Activation, CommonAddress, [new InformationObject(ioa, value, Qualifier: qu)], OriginatorAddress: OriginatorAddress);
        var (con, term, _) = await ExecuteAsync(exe, waitTerm: true, null, ct).ConfigureAwait(false);
        return new Iec104CommandResult(!con.Negative && con.Cause == CauseOfTransmission.ActivationConfirmation, term is { Negative: false }, con.Cause);
    }

    /// <summary>Writes received values of mapped monitor points into a tag database (IOA → tag through <paramref name="map"/>).</summary>
    /// <returns>Number of tags updated.</returns>
    public int ApplyTo(TagDatabase tags, Iec104PointMap map)
    {
        int n = 0;
        foreach (var (ioa, v) in Values)
        {
            if (!map.TryGet(ioa, out var p) || p!.IsCommand || !tags.TryGet(p.Tag, out _)) continue;
            var q = v.Object.Quality;
            var tq = q.HasFlag(Iec104Quality.Invalid) ? TagQuality.Bad : q != Iec104Quality.Good ? TagQuality.Uncertain : TagQuality.Good;
            tags.UpdateValue(p.Tag, Iec104PointMap.FromWire(p, v.Object.Value), v.Object.Time ?? v.Received, tq);
            n++;
        }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null) await _conn.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null) try { await _reader.ConfigureAwait(false); } catch { /* closed */ }
        _tcp?.Dispose();
        _conn = null; _reader = null; _tcp = null;
    }
}
