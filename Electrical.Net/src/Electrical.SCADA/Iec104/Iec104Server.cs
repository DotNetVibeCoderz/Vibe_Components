using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Electrical.SCADA.Iec104;

/// <summary>
/// Binding of an information object address to a <see cref="TagDatabase"/> tag. Monitor points (M_SP/M_DP/M_ME_NA/NB/NC) report the
/// tag value; normalized values are tag / <paramref name="FullScale"/>. Command points (C_SC/C_DC/C_SE) write the tag
/// (single: 0/1, double: 0 = off, 1 = on, set-point: value). <paramref name="Group"/> 1–16 assigns the point to a group interrogation.
/// </summary>
public sealed record Iec104Point(uint Ioa, string Tag, TypeId Type, double FullScale = 1, int Group = 0, bool SelectBeforeOperate = false)
{
    public bool IsCommand => !Asdu.IsMonitor(Type);
}

/// <summary>IOA ↔ tag map served by an <see cref="Iec104Server"/> (and used by <see cref="Iec104Client.ApplyTo"/>).</summary>
public sealed class Iec104PointMap
{
    private readonly Dictionary<uint, Iec104Point> _byIoa = [];
    private readonly List<Iec104Point> _order = [];

    public Iec104Point Add(Iec104Point p)
    {
        if (!_byIoa.TryAdd(p.Ioa, p)) throw new ArgumentException($"IOA {p.Ioa} already mapped.");
        Asdu.ElementSize(p.Type);
        _order.Add(p);
        return p;
    }

    public Iec104Point Add(uint ioa, string tag, TypeId type, double fullScale = 1, int group = 0, bool selectBeforeOperate = false) =>
        Add(new Iec104Point(ioa, tag, type, fullScale, group, selectBeforeOperate));

    public IReadOnlyList<Iec104Point> Points => _order;
    public IEnumerable<Iec104Point> Monitor => _order.Where(p => !p.IsCommand);
    public IEnumerable<Iec104Point> Commands => _order.Where(p => p.IsCommand);
    public bool TryGet(uint ioa, out Iec104Point? p) => _byIoa.TryGetValue(ioa, out p);
    public IEnumerable<Iec104Point> ForTag(string tag) => _order.Where(p => string.Equals(p.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Encodes a tag value as the information value of a monitor point.</summary>
    internal static double ToWire(Iec104Point p, double value) => p.Type switch
    {
        TypeId.M_SP_NA_1 or TypeId.M_SP_TB_1 => value != 0 ? 1 : 0,
        TypeId.M_DP_NA_1 or TypeId.M_DP_TB_1 => value != 0 ? 2 : 1,
        TypeId.M_ME_NA_1 or TypeId.M_ME_TD_1 => value / p.FullScale,
        _ => value,
    };

    /// <summary>Decodes an information value of a monitor point into engineering units.</summary>
    public static double FromWire(Iec104Point p, double wire) => p.Type switch
    {
        TypeId.M_DP_NA_1 or TypeId.M_DP_TB_1 => wire == 2 ? 1 : 0,
        TypeId.M_ME_NA_1 or TypeId.M_ME_TD_1 => wire * p.FullScale,
        _ => wire,
    };

    internal static Iec104Quality Quality(TagQuality q) => q switch
    {
        TagQuality.Good => Iec104Quality.Good,
        TagQuality.Uncertain => Iec104Quality.NotTopical,
        TagQuality.CommFailure => Iec104Quality.NotTopical | Iec104Quality.Invalid,
        _ => Iec104Quality.Invalid,
    };

    /// <summary>Time-tagged counterpart of a monitor type (used for spontaneous transmission).</summary>
    internal static TypeId WithTime(TypeId t) => t switch
    {
        TypeId.M_SP_NA_1 => TypeId.M_SP_TB_1,
        TypeId.M_DP_NA_1 => TypeId.M_DP_TB_1,
        TypeId.M_ME_NA_1 => TypeId.M_ME_TD_1,
        TypeId.M_ME_NB_1 => TypeId.M_ME_TE_1,
        TypeId.M_ME_NC_1 => TypeId.M_ME_TF_1,
        _ => t,
    };

    internal static TypeId WithoutTime(TypeId t) => t switch
    {
        TypeId.M_SP_TB_1 => TypeId.M_SP_NA_1,
        TypeId.M_DP_TB_1 => TypeId.M_DP_NA_1,
        TypeId.M_ME_TD_1 => TypeId.M_ME_NA_1,
        TypeId.M_ME_TE_1 => TypeId.M_ME_NB_1,
        TypeId.M_ME_TF_1 => TypeId.M_ME_NC_1,
        _ => t,
    };
}

/// <summary>Command delivered to <see cref="Iec104ServerOptions.CommandHandler"/>.</summary>
/// <param name="Value">Single: 0/1; double: 1 off / 2 on (DCS); set-point: value.</param>
public sealed record Iec104Command(Iec104Point Point, double Value, byte Qualifier, DateTimeOffset? Time);

/// <summary>Settings of an <see cref="Iec104Server"/>.</summary>
public sealed record Iec104ServerOptions
{
    /// <summary>Common address of ASDU (station address).</summary>
    public ushort CommonAddress { get; init; } = 1;
    public Iec104Parameters Parameters { get; init; } = new();
    /// <summary>Maximum time between select and execute.</summary>
    public TimeSpan SelectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Send M_EI_NA_1 (end of initialization) after the first STARTDT of a connection.</summary>
    public bool SendEndOfInitialization { get; init; } = true;
    /// <summary>Use time-tagged types (M_*_TB/TD/TE/TF) for spontaneous transmission.</summary>
    public bool SpontaneousWithTime { get; init; } = true;
    /// <summary>Maximum information objects per interrogation ASDU.</summary>
    public int MaxObjectsPerAsdu { get; init; } = 20;
    /// <summary>
    /// Executes a command; return false for a negative confirmation. Null = write the mapped tag (single 0/1, double 1 → 0 / 2 → 1,
    /// set-point value).
    /// </summary>
    public Func<Iec104Command, bool>? CommandHandler { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// IEC 60870-5-104 controlled station (server, default port 2404) backed by a <see cref="TagDatabase"/>: general and group interrogation
/// (C_IC_NA_1 → ActCon, data with COT 20 + n, ActTerm), clock synchronisation (C_CS_NA_1), single/double/set-point commands with
/// select/execute and deactivation, negative confirmations with COT 44–47, spontaneous transmission (COT 3) of tag changes to every
/// started connection, and end of initialization.
/// </summary>
public sealed class Iec104Server : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly List<Session> _sessions = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private TimeSpan _clockOffset;

    public Iec104Server(TagDatabase tags, Iec104PointMap points, Iec104ServerOptions? options = null, IPAddress? address = null, int port = 2404)
    {
        Tags = tags;
        Points = points;
        Options = options ?? new Iec104ServerOptions();
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
    }

    public TagDatabase Tags { get; }
    public Iec104PointMap Points { get; }
    public Iec104ServerOptions Options { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>Station clock (system time plus the offset set by C_CS_NA_1).</summary>
    public DateTimeOffset Now => Options.Time.GetUtcNow() + _clockOffset;
    /// <summary>Open connections.</summary>
    public int ConnectionCount { get { lock (_gate) return _sessions.Count(s => !s.Connection.Completion.IsCompleted); } }
    /// <summary>Raised after a command has been executed (or rejected).</summary>
    public event Action<Iec104Command, bool>? Commanded;

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _listener.Start();
        Tags.Changed += OnTagChanged;
        _accept = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        Tags.Changed -= OnTagChanged;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        Session[] all;
        lock (_gate) all = [.. _sessions];
        foreach (var s in all) await s.Connection.DisposeAsync().ConfigureAwait(false);
        try { await Task.WhenAll(all.Select(s => s.Run).Append(_accept ?? Task.CompletedTask)).ConfigureAwait(false); } catch { /* closing */ }
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
            c.NoDelay = true;
            var conn = new Iec104Connection(c.GetStream(), Options.Parameters, controlling: false, Options.Time);
            var s = new Session(this, conn, c);
            lock (_gate) { _sessions.RemoveAll(x => x.Run.IsCompleted); _sessions.Add(s); }
            s.Begin(ct);
        }
    }

    private void OnTagChanged(TagValueChanged e)
    {
        var pts = Points.ForTag(e.Tag).Where(p => !p.IsCommand).ToList();
        if (pts.Count == 0) return;
        Session[] all;
        lock (_gate) all = [.. _sessions];
        foreach (var p in pts)
        {
            var type = Options.SpontaneousWithTime ? Iec104PointMap.WithTime(p.Type) : p.Type;
            var obj = new InformationObject(p.Ioa, Iec104PointMap.ToWire(p, e.Value), Iec104PointMap.Quality(e.Quality), e.Timestamp);
            var asdu = new Asdu(type, CauseOfTransmission.Spontaneous, Options.CommonAddress, [obj]);
            foreach (var s in all) if (s.Connection.DataTransferActive) s.Enqueue(asdu);
        }
    }

    private InformationObject Snapshot(Iec104Point p)
    {
        var t = Tags.Get(p.Tag);
        double v = double.IsNaN(t.Value) ? 0 : t.Value;
        return new InformationObject(p.Ioa, Iec104PointMap.ToWire(p, v), Iec104PointMap.Quality(t.Quality), t.Timestamp);
    }

    private sealed class Session(Iec104Server server, Iec104Connection conn, TcpClient client)
    {
        private readonly Channel<Asdu> _out = Channel.CreateUnbounded<Asdu>();
        private readonly Lock _state = new();
        private (uint Ioa, TypeId Type, double Value, DateTimeOffset Time)? _selection;
        private bool _initSent;

        public Iec104Connection Connection { get; } = conn;
        public Task Run { get; private set; } = Task.CompletedTask;
        private Iec104ServerOptions O => server.Options;

        public void Enqueue(Asdu a) => _out.Writer.TryWrite(a);

        public void Begin(CancellationToken ct)
        {
            Connection.Started += () =>
            {
                lock (_state)
                {
                    if (_initSent || !O.SendEndOfInitialization) return;
                    _initSent = true;
                }
                Enqueue(new Asdu(TypeId.M_EI_NA_1, CauseOfTransmission.Initialized, O.CommonAddress, [new InformationObject(0, 0)]));
            };
            Connection.Start();
            Run = Task.WhenAll(ReceiveAsync(ct), SendLoopAsync(ct));
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var a in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await Connection.SendAsync(a, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        }

        private async Task ReceiveAsync(CancellationToken ct)
        {
            using var _ = client;
            try
            {
                await foreach (var a in Connection.Received.ReadAllAsync(ct).ConfigureAwait(false)) Handle(a);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
            finally
            {
                _out.Writer.TryComplete();
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void Reply(Asdu req, CauseOfTransmission cot, bool negative = false, IReadOnlyList<InformationObject>? objects = null) =>
            Enqueue(req with { Cause = cot, Negative = negative, Objects = objects ?? req.Objects });

        private void Handle(Asdu a)
        {
            if (!Enum.IsDefined(a.Type) || Asdu.IsMonitor(a.Type)) { Reply(a, CauseOfTransmission.UnknownTypeId, true); return; }
            if (a.CommonAddress != O.CommonAddress && a.CommonAddress != 0xFFFF) { Reply(a, CauseOfTransmission.UnknownCommonAddress, true); return; }
            switch (a.Type)
            {
                case TypeId.C_IC_NA_1: Interrogation(a); break;
                case TypeId.C_CS_NA_1: ClockSync(a); break;
                case TypeId.C_SC_NA_1 or TypeId.C_SC_TA_1 or TypeId.C_DC_NA_1 or TypeId.C_DC_TA_1 or TypeId.C_SE_NC_1: Command(a); break;
                default: Reply(a, CauseOfTransmission.UnknownTypeId, true); break;
            }
        }

        private void Interrogation(Asdu a)
        {
            if (a.Cause != CauseOfTransmission.Activation) { Reply(a, CauseOfTransmission.UnknownCause, true); return; }
            int qoi = a.Objects.Count > 0 ? (int)a.Objects[0].Value : 20;
            if (qoi < 20 || qoi > 36) { Reply(a, CauseOfTransmission.ActivationConfirmation, true); return; }
            Reply(a, CauseOfTransmission.ActivationConfirmation);
            var cot = (CauseOfTransmission)qoi; // 20 = station, 21–36 = group 1–16
            var pts = server.Points.Monitor.Where(p => qoi == 20 || p.Group == qoi - 20).ToList();
            foreach (var byType in pts.GroupBy(p => Iec104PointMap.WithoutTime(p.Type)))
                foreach (var chunk in byType.Chunk(O.MaxObjectsPerAsdu))
                {
                    var objs = chunk.Select(p => server.Snapshot(p) with { Time = null }).ToArray();
                    Enqueue(new Asdu(byType.Key, cot, O.CommonAddress, objs));
                }
            Reply(a, CauseOfTransmission.ActivationTermination);
        }

        private void ClockSync(Asdu a)
        {
            if (a.Cause != CauseOfTransmission.Activation || a.Objects.Count == 0 || a.Objects[0].Time is null)
            {
                Reply(a, CauseOfTransmission.ActivationConfirmation, true);
                return;
            }
            server._clockOffset = a.Objects[0].Time!.Value - O.Time.GetUtcNow();
            Reply(a, CauseOfTransmission.ActivationConfirmation, objects: [a.Objects[0] with { Time = server.Now }]);
        }

        private void Command(Asdu a)
        {
            var o = a.Objects.Count == 1 ? a.Objects[0] : null;
            if (o is null) { Reply(a, CauseOfTransmission.ActivationConfirmation, true); return; }
            if (!server.Points.TryGet(o.Address, out var p) || NoTime(p!.Type) != NoTime(a.Type))
            {
                Reply(a, CauseOfTransmission.UnknownObjectAddress, true);
                return;
            }
            if (a.Cause == CauseOfTransmission.Deactivation)
            {
                lock (_state) _selection = null;
                Reply(a, CauseOfTransmission.DeactivationConfirmation);
                return;
            }
            if (a.Cause != CauseOfTransmission.Activation) { Reply(a, CauseOfTransmission.UnknownCause, true); return; }
            if (o.Select)
            {
                lock (_state) _selection = (o.Address, a.Type, o.Value, server.Now);
                Reply(a, CauseOfTransmission.ActivationConfirmation);
                return;
            }
            bool selected;
            lock (_state)
            {
                selected = _selection is { } s && s.Ioa == o.Address && s.Type == a.Type && s.Value == o.Value && server.Now - s.Time <= O.SelectTimeout;
                _selection = null;
            }
            if (p.SelectBeforeOperate && !selected) { Reply(a, CauseOfTransmission.ActivationConfirmation, true); return; }
            var cmd = new Iec104Command(p, o.Value, o.Qualifier, o.Time);
            bool ok;
            try { ok = O.CommandHandler?.Invoke(cmd) ?? DefaultExecute(cmd); }
            catch (Exception) { ok = false; }
            server.Commanded?.Invoke(cmd, ok);
            Reply(a, CauseOfTransmission.ActivationConfirmation, !ok);
            if (ok) Reply(a, CauseOfTransmission.ActivationTermination);
        }

        private static TypeId NoTime(TypeId t) => t switch { TypeId.C_SC_TA_1 => TypeId.C_SC_NA_1, TypeId.C_DC_TA_1 => TypeId.C_DC_NA_1, _ => t };

        private bool DefaultExecute(Iec104Command c)
        {
            double v = NoTime(c.Point.Type) switch
            {
                TypeId.C_SC_NA_1 => c.Value != 0 ? 1 : 0,
                TypeId.C_DC_NA_1 => c.Value switch { 2 => 1, 1 => 0, _ => double.NaN },
                _ => c.Value,
            };
            if (double.IsNaN(v)) return false;
            server.Tags.UpdateValue(c.Point.Tag, v, server.Now);
            return true;
        }
    }
}
