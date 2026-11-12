using System.Globalization;
using System.IO.Ports;
using AquaCulture.Core;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Hosting;
using IoTCom.Net;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Protocols.Sparkplug;
using IoTCom.Net.Serialization.SenML;
using IoTCom.Net.Transport.Serial;

namespace AquaCulture.IoT.IoTCom;

// ─────────────────────────────── Modbus (TCP / RTU / ASCII / serial) ───────────────────────────────

/// <summary>A Modbus probe or PLC slave: its unit id, register map and optional aerator coil.</summary>
public sealed record IoTComProbe(byte Unit, string DeviceId, string PondId, IReadOnlyList<ModbusRegisterMap> Map, ushort? AeratorCoil = null, bool InputRegisters = false);

/// <summary>
/// <see cref="ISensorTransport"/> over the IoTCom.Net Modbus client (TCP, RTU, ASCII; TCP, serial or in-memory transports, retries and reconnect).
/// Polls every probe on an interval; <c>aerator_on</c>/<c>aerator_off</c> commands write the probe's aerator coil.
/// </summary>
public sealed class IoTComModbusTransport : ISensorTransport
{
    private readonly Action<ModbusClientOptions> _configure;
    private readonly IReadOnlyList<IoTComProbe> _probes;
    private ModbusClient? _client;
    private CancellationTokenSource? _loop;
    private Task? _task;

    public IoTComModbusTransport(Action<ModbusClientOptions> configure, IReadOnlyList<IoTComProbe> probes, TimeSpan? interval = null)
    {
        _configure = configure; _probes = probes; Interval = interval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Modbus TCP (port 502 by default) to a PLC or gateway.</summary>
    public static IoTComModbusTransport Tcp(string host, IReadOnlyList<IoTComProbe> probes, int port = 502, TimeSpan? interval = null) =>
        new(o => o.UseTcp(host, port).WithTimeout(TimeSpan.FromSeconds(2)).WithRetries(2), probes, interval);

    /// <summary>Modbus RTU over a serial RS-485 line (8 data bits).</summary>
    public static IoTComModbusTransport Serial(string portName, IReadOnlyList<IoTComProbe> probes, int baudRate = 9600, Parity parity = Parity.None,
        StopBits stopBits = StopBits.One, TimeSpan? interval = null) =>
        new(o => o.UseSerial(portName, baudRate, parity, stopBits, 8).UseRtuFraming().WithTimeout(TimeSpan.FromSeconds(1)).WithRetries(2), probes, interval);

    public TimeSpan Interval { get; }
    public event Action<SensorReading>? ReadingReceived;
    /// <summary>Raised when a probe fails to answer (the poll continues with the next probe).</summary>
    public event Action<IoTComProbe, Exception>? ProbeFailed;
    public bool IsConnected => _client is { State: EndpointState.Connected };

    /// <summary>Connects and starts polling every <see cref="Interval"/>.</summary>
    public Task ConnectAsync(CancellationToken ct = default) => ConnectAsync(true, ct);

    /// <summary>Connects; with <paramref name="startPolling"/> false, poll manually with <see cref="PollOnceAsync"/>.</summary>
    public async Task ConnectAsync(bool startPolling, CancellationToken ct = default)
    {
        _client ??= ModbusClient.Create(_configure);
        await _client.ConnectAsync(ct).ConfigureAwait(false);
        if (!startPolling || _task is not null) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _task = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Interval);
            do { await PollOnceAsync(token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
    }

    /// <summary>Reads every probe once, raises <see cref="ReadingReceived"/> and returns the readings.</summary>
    public async Task<IReadOnlyList<SensorReading>> PollOnceAsync(CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("Call ConnectAsync first.");
        var all = new List<SensorReading>();
        foreach (var p in _probes)
        {
            try
            {
                ushort start = p.Map.Min(m => m.Address), end = (ushort)p.Map.Max(m => m.Address + m.Registers);
                var regs = p.InputRegisters
                    ? await client.ReadInputRegistersAsync(start, (ushort)(end - start), p.Unit, ct).ConfigureAwait(false)
                    : await client.ReadHoldingRegistersAsync(start, (ushort)(end - start), p.Unit, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                foreach (var (kind, value) in Modbus.Decode(regs, start, p.Map))
                {
                    var r = new SensorReading(p.DeviceId, p.PondId, kind, value, now, SensorReading.DefaultUnit(kind));
                    all.Add(r);
                    ReadingReceived?.Invoke(r);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ProbeFailed?.Invoke(p, e);
            }
        }
        return all;
    }

    public async Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("Call ConnectAsync first.");
        var p = _probes.FirstOrDefault(x => x.DeviceId == command.DeviceId) ?? throw new ArgumentException($"Unknown device '{command.DeviceId}'.");
        if (command.Action is not ("aerator_on" or "aerator_off")) throw new NotSupportedException($"Action '{command.Action}' is not mapped to Modbus.");
        if (p.AeratorCoil is not { } coil) throw new InvalidOperationException($"Device '{p.DeviceId}' has no aerator coil.");
        await client.WriteSingleCoilAsync(coil, command.Action == "aerator_on", p.Unit, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null)
        {
            await _loop.CancelAsync().ConfigureAwait(false);
            try { if (_task is not null) await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _loop.Dispose();
        }
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
    }
}

// ─────────────────────────────── SenML (RFC 8428) ───────────────────────────────

/// <summary>
/// Sensor readings ↔ SenML (RFC 8428) JSON or CBOR. Record names are <c>{pond}/{device}/{kind}</c>, units use the SenML registry where one exists
/// (<c>Cel</c>, <c>%RH</c>, <c>mm</c>, <c>m/s</c>, <c>W/m2</c>, <c>A</c>, <c>V</c>, <c>dBm</c>…).
/// </summary>
public static class AquaSenML
{
    public const string JsonContentType = SenMLCodec.JsonContentType;
    public const string CborContentType = SenMLCodec.CborContentType;

    private static readonly Dictionary<string, string> ToSenMLUnit = new()
    {
        ["°C"] = "Cel", ["%"] = "%RH", ["W/m2"] = "W/m2", ["m/s"] = "m/s", ["mm"] = "mm", ["A"] = "A", ["V"] = "V", ["dBm"] = "dBm", ["mV"] = "mV", ["kg"] = "kg",
    };
    private static readonly Dictionary<string, string> FromSenMLUnit = ToSenMLUnit.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static IReadOnlyList<SenMLRecord> ToRecords(IEnumerable<SensorReading> readings) => readings.Select(r => new SenMLRecord
    {
        Name = $"{r.PondId}/{r.DeviceId}/{r.Kind}",
        Unit = ToSenMLUnit.TryGetValue(r.Unit, out var u) ? u : r.Unit,
        Value = r.Value,
        Time = (r.Timestamp.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds,
    }).ToList();

    public static byte[] Encode(IEnumerable<SensorReading> readings, bool cbor = false)
    {
        var pack = ToRecords(readings);
        return cbor ? SenMLCodec.ToCbor(pack) : SenMLCodec.ToJson(pack);
    }

    /// <summary>
    /// Decodes a SenML pack (base names, base times and relative times are resolved). Records whose last name segment is not a known
    /// parameter are skipped; <paramref name="defaultPond"/> and <paramref name="defaultDevice"/> fill names with fewer segments (for example <c>"temp"</c>).
    /// </summary>
    public static IReadOnlyList<SensorReading> Decode(ReadOnlyMemory<byte> payload, bool cbor = false, string defaultPond = "", string defaultDevice = "senml")
    {
        var pack = cbor ? SenMLCodec.ParseCbor(payload) : SenMLCodec.ParseJson(payload.Span);
        var list = new List<SensorReading>();
        foreach (var r in SenMLCodec.Resolve(pack, DateTimeOffset.UtcNow))
        {
            if (r.Value is not { } v) { if (r.BoolValue is { } b) v = b ? 1 : 0; else continue; }
            // "pond/device/kind" (this library) or URN style "urn:dev:<device>:<kind>" (RFC 8428 examples): ponds only come from path names
            bool path = r.Name.Contains('/');
            var parts = r.Name.Split(path ? '/' : ':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !SensorReading.TryParseKind(parts[^1], out var kind)) continue;
            string device = parts.Length >= 2 ? parts[^2] : defaultDevice, pond = path && parts.Length >= 3 ? parts[^3] : defaultPond;
            string unit = r.Unit is { Length: > 0 } u ? FromSenMLUnit.GetValueOrDefault(u, u) : SensorReading.DefaultUnit(kind);
            list.Add(new SensorReading(device, pond, kind, v, r.Time.UtcDateTime, unit));
        }
        return list;
    }
}

// ─────────────────────────────── NMEA 0183 and AIS ───────────────────────────────

/// <summary>
/// Decodes NMEA 0183 from echo sounders, thermometers and GNSS on floating net cages and feed barges:
/// <c>MTW</c> water temperature, <c>DPT</c>/<c>DBT</c> depth (as <see cref="SensorKind.WaterLevel"/> in cm), and <c>GGA</c>/<c>RMC</c>/<c>GLL</c> position.
/// With an anchor position, <see cref="DriftM"/> and <see cref="IsDrifting"/> detect a broken mooring.
/// </summary>
public sealed class NmeaCageDecoder(string deviceId, string pondId, GeoCoordinate? anchor = null, double driftAlarmM = 30)
{
    public GeoCoordinate? Position { get; private set; }
    public DateTime? PositionTime { get; private set; }
    public GeoCoordinate? Anchor { get; } = anchor;
    public double DriftAlarmM { get; } = driftAlarmM;
    public int Rejected { get; private set; }

    /// <summary>Distance from the anchor in metres (null without an anchor or a fix).</summary>
    public double? DriftM => Anchor is { } a && Position is { } p ? GeoMath.Haversine(a, p) : null;
    public bool IsDrifting => DriftM > DriftAlarmM;

    /// <summary>Feeds one sentence; returns the readings it produced (empty for position-only or unsupported sentences).</summary>
    public IReadOnlyList<SensorReading> Feed(string line, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        if (!NmeaSentence.TryParse(line.Trim(), out var s, true) || s is null) { Rejected++; return []; }
        switch (NmeaParser.Decode(s))
        {
            case GgaMessage { Latitude: { } la, Longitude: { } lo }: Fix(la, lo, now); return [];
            case RmcMessage { Active: true, Latitude: { } la, Longitude: { } lo } r: Fix(la, lo, r.Timestamp?.UtcDateTime ?? now); return [];
            case GllMessage { Active: true, Latitude: { } la, Longitude: { } lo }: Fix(la, lo, now); return [];
        }
        double F(int i) => i < s.Fields.Count && double.TryParse(s.Fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        switch (s.Type)
        {
            case "MTW" when !double.IsNaN(F(0)):
                return [new SensorReading(deviceId, pondId, SensorKind.Temperature, F(0), now, "°C")];
            case "DPT" when !double.IsNaN(F(0)):
                double off = double.IsNaN(F(1)) ? 0 : F(1);
                return [new SensorReading(deviceId, pondId, SensorKind.WaterLevel, Math.Round((F(0) + off) * 100, 1), now, "cm")];
            case "DBT" when !double.IsNaN(F(2)):
                return [new SensorReading(deviceId, pondId, SensorKind.WaterLevel, Math.Round(F(2) * 100, 1), now, "cm")];
            default:
                return [];
        }
    }

    private void Fix(double lat, double lon, DateTime t) { Position = new GeoCoordinate(lat, lon); PositionTime = t; }
}

/// <summary>A vessel close to or inside a cage zone.</summary>
public sealed record VesselAlert(uint Mmsi, string Name, double DistanceM, double? SpeedKnots, bool Inside, Text2 Message);

/// <summary>
/// Watches AIS traffic (via IoTCom.Net's AIS decoder and tracker) around a floating-net-cage (KJA) zone: vessels inside the zone, or within
/// <paramref name="bufferM"/> and moving faster than <paramref name="minSpeedKnots"/>, raise an alert (wake and collision risk to nets and moorings).
/// </summary>
public sealed class CageZoneWatch(GeoPolygon zone, double bufferM = 300, double minSpeedKnots = 2)
{
    private readonly AisDecoder _decoder = new();
    private readonly AisTracker _tracker = new();

    public GeoPolygon Zone { get; } = zone;
    public IReadOnlyCollection<AisVessel> Vessels => _tracker.Vessels;

    /// <summary>Feeds one <c>!AIVDM</c>/<c>!AIVDO</c> sentence; returns an alert when it updates a vessel position that is a risk.</summary>
    public VesselAlert? Feed(string line, DateTimeOffset? time = null)
    {
        var msg = _decoder.Feed(line.Trim());
        if (msg is null) return null;
        _tracker.Apply(msg, time ?? DateTimeOffset.UtcNow);
        var v = _tracker.Vessels.FirstOrDefault(x => x.Mmsi == msg.Mmsi);
        return v is null ? null : Assess(v);
    }

    public VesselAlert? Assess(AisVessel v)
    {
        if (v.Latitude is not { } la || v.Longitude is not { } lo) return null;
        var p = new GeoCoordinate(la, lo);
        bool inside = Zone.Contains(p);
        double d = inside ? 0 : DistanceToZone(p);
        double? sog = v.Speed;
        if (!inside && (d > bufferM || (sog ?? 0) < minSpeedKnots)) return null;
        string name = string.IsNullOrWhiteSpace(v.Name) ? v.Mmsi.ToString(CultureInfo.InvariantCulture) : v.Name.Trim();
        string speed = sog is { } s ? s.ToString("0.0", CultureInfo.InvariantCulture) : "?";
        var msg = inside
            ? new Text2($"Vessel {name} is inside the cage zone ({speed} kn): check nets and moorings", $"Kapal {name} masuk zona KJA ({speed} knot): periksa jaring dan tambat")
            : new Text2($"Vessel {name} {d:0} m from the cage zone at {speed} kn: wake risk", $"Kapal {name} berjarak {d:0} m dari zona KJA dengan {speed} knot: risiko gelombang kapal");
        return new VesselAlert(v.Mmsi, name, d, sog, inside, msg);
    }

    /// <summary>Shortest distance (m) from a point to the zone boundary.</summary>
    public double DistanceToZone(GeoCoordinate p)
    {
        double best = double.MaxValue;
        var ring = Zone.Ring;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = GeoMath.ToLocal(p, ring[i]); var b = GeoMath.ToLocal(p, ring[(i + 1) % ring.Count]);
            double dx = b.East - a.East, dy = b.North - a.North, len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Clamp(-(a.East * dx + a.North * dy) / len2, 0, 1);
            double ex = a.East + t * dx, ey = a.North + t * dy;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }
}

// ─────────────────────────────── Sparkplug B ───────────────────────────────

/// <summary>
/// Publishes ponds as Sparkplug B devices of one edge node (Ignition, HiveMQ, Cirrus Link and other SCADA hosts discover them from the birth
/// certificates). Each pond has a Double metric per parameter and a writable <c>Aerator</c> Boolean; host writes arrive as <see cref="CommandReceived"/>.
/// </summary>
public sealed class SparkplugPondPublisher : IAsyncDisposable
{
    private readonly SparkplugEdgeNode _node;

    public static readonly SensorKind[] DefaultKinds =
        [SensorKind.Temperature, SensorKind.DissolvedOxygen, SensorKind.Ph, SensorKind.Salinity, SensorKind.Ammonia, SensorKind.Orp, SensorKind.Turbidity, SensorKind.WaterLevel];

    public SparkplugPondPublisher(string group, string edgeNode, IEnumerable<string> pondIds, Action<MqttEndpointOptions> mqtt, IEnumerable<SensorKind>? kinds = null)
    {
        Group = group; EdgeNode = edgeNode;
        Kinds = (kinds ?? DefaultKinds).ToArray();
        _node = SparkplugEdgeNode.Create(o => { o.Group = group; o.EdgeNode = edgeNode; o.Mqtt = mqtt; o.AcceptWrites = true; });
        _node.Metric("Platform/Version", SparkplugDataType.String, AquaInfo.Version);
        foreach (var pond in pondIds)
        {
            var d = _node.Device(pond);
            foreach (var k in Kinds) d.Metric(k.ToString(), SparkplugDataType.Double, 0.0);
            d.Metric("Aerator", SparkplugDataType.Boolean, false, writable: true);
        }
        _node.CommandReceived += (_, c) =>
        {
            if (c.Metric == "Aerator" && c.Value is bool on && c.Device is { Length: > 0 })
                CommandReceived?.Invoke(new ActuatorCommand(c.Device, on ? "aerator_on" : "aerator_off", on ? 1 : 0, DateTime.UtcNow));
        };
    }

    public string Group { get; }
    public string EdgeNode { get; }
    public IReadOnlyList<SensorKind> Kinds { get; }
    /// <summary>Aerator writes from the SCADA host, as actuator commands for the pond (device id = pond id).</summary>
    public event Action<ActuatorCommand>? CommandReceived;

    public Task StartAsync(CancellationToken ct = default) => _node.StartAsync(ct);

    /// <summary>Publishes readings as DDATA (one message per pond); kinds without a metric are ignored.</summary>
    public async Task PublishAsync(IEnumerable<SensorReading> readings, CancellationToken ct = default)
    {
        foreach (var g in readings.Where(r => Kinds.Contains(r.Kind)).GroupBy(r => r.PondId))
        {
            if (!_node.Devices.TryGetValue(g.Key, out var d)) continue;
            var values = g.GroupBy(r => r.Kind).ToDictionary(k => k.Key.ToString(), k => (object?)k.OrderBy(r => r.Timestamp).Last().Value);
            await d.SetAsync(values, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reports an aerator state change (for example after the twin switched it).</summary>
    public Task SetAeratorAsync(string pondId, bool on, CancellationToken ct = default) =>
        _node.Devices.TryGetValue(pondId, out var d) ? d.SetAsync("Aerator", on, ct) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try { await _node.StopAsync().ConfigureAwait(false); } catch (Exception e) when (e is IoTComException or InvalidOperationException) { }
        await _node.DisposeAsync().ConfigureAwait(false);
    }
}

public static class IoTComBuilderExtensions
{
    /// <summary>Marks the IoTCom.Net adapters as registered (they are created per connection, so nothing is added to the container).</summary>
    public static AquaBuilder AddIoTCom(this AquaBuilder b) => b.TryAddModule("IoT.IoTCom", _ => { });
}
