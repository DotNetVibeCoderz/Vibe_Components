using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using AquaCulture.Core.Hosting;
using IoTCom.Net;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;

namespace AquaCulture.IoT.IoTCom;

// ─────────────────────────────── LoRaWAN: own light network server (Semtech UDP) ───────────────────────────────

/// <summary>A LoRaWAN pond node: ABP or OTAA credentials and the pond it measures.</summary>
public sealed record LoRaPondNode(string DevEui, string PondId, string Name, string? AppKey = null, string? DevAddr = null, string? NwkSKey = null, string? AppSKey = null)
{
    internal LoRaWanDeviceRegistration ToRegistration()
    {
        var eui = Eui64.Parse(DevEui);
        if (AppKey is { Length: > 0 }) return LoRaWanDeviceRegistration.Otaa(eui, LoRaWanKeys.Parse(AppKey), Name, null);
        if (DevAddr is null || NwkSKey is null || AppSKey is null) throw new ArgumentException($"Node {DevEui}: give AppKey (OTAA) or DevAddr + NwkSKey + AppSKey (ABP).");
        return LoRaWanDeviceRegistration.Abp(eui, global::IoTCom.Net.Protocols.LoRaWan.DevAddr.Parse(DevAddr), LoRaWanSessionKeys.FromHex(NwkSKey, AppSKey), Name);
    }
}

/// <summary>
/// A farm-local LoRaWAN network server (IoTCom.Net) that gateways reach over the Semtech UDP packet-forwarder protocol (port 1700) — no
/// ChirpStack or TTN needed for a farm without internet. Uplinks are decrypted and decoded with the 12-byte pond codec or Cayenne LPP; gateway
/// RSSI is reported as <see cref="SensorKind.Rssi"/>. Aerator commands are queued as Class A downlinks (FPort 10, byte 1 = on, 0 = off).
/// </summary>
public sealed class LoRaWanPondNetwork : ISensorTransport
{
    private readonly LoRaWanNetworkServer _server;
    private readonly Dictionary<ulong, LoRaPondNode> _nodes;

    public const byte CommandPort = 10;

    public LoRaWanPondNetwork(IEnumerable<LoRaPondNode> nodes, Action<LoRaWanNetworkServerOptions>? configure = null)
    {
        _nodes = nodes.ToDictionary(n => Eui64.Parse(n.DevEui).Value);
        _server = LoRaWanNetworkServer.Create(o =>
        {
            o.Region = LoRaRegion.AS923Group2;   // Indonesia: AS923-2 (920–923 MHz)
            o.Name = "AquaCulture.Net LoRaWAN";
            configure?.Invoke(o);
        });
        foreach (var n in _nodes.Values) _server.AddDevice(n.ToRegistration());
        _server.UplinkReceived += (_, u) => OnUplink(u);
        _server.FrameRejected += (_, why) => FrameRejected?.Invoke(why);
    }

    /// <summary>Listens on UDP <paramref name="port"/> (1700 is the Semtech default).</summary>
    public static LoRaWanPondNetwork Udp(IEnumerable<LoRaPondNode> nodes, int port = 1700) =>
        new(nodes, o => o.UseUdp(port, IPAddress.Any));

    public event Action<SensorReading>? ReadingReceived;
    public event Action<string>? FrameRejected;
    public bool IsConnected { get; private set; }
    public EndPoint? LocalEndPoint => _server.LocalEndPoint;
    public IReadOnlyCollection<LoRaWanDeviceSession> Sessions => _server.Devices;
    public long Uplinks { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _server.StartAsync(ct).ConfigureAwait(false);
        IsConnected = true;
    }

    private void OnUplink(LoRaWanUplink u)
    {
        if (u.Payload is not { Length: > 0 } payload || u.FPort is null or 0) return;
        if (!_nodes.TryGetValue(u.Device.DevEui.Value, out var node)) return;
        Uplinks++;
        var ts = u.Time.UtcDateTime;
        var readings = payload.Length == 12
            ? LoRaPayload.DecodePond(payload, node.Name, node.PondId, ts)
            : LoRaPayload.CayenneToReadings(LoRaPayload.DecodeCayenne(payload), node.Name, node.PondId, ts);
        foreach (var r in readings) ReadingReceived?.Invoke(r);
        if (u.Gateways is { Count: > 0 } g)
            ReadingReceived?.Invoke(new SensorReading(node.Name, node.PondId, SensorKind.Rssi, g.Max(x => x.Rssi), ts, "dBm"));
    }

    /// <summary>Queues an aerator downlink; it is sent in the node's next receive window.</summary>
    public Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default)
    {
        var node = _nodes.Values.FirstOrDefault(n => n.Name == command.DeviceId || n.DevEui.Equals(command.DeviceId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Unknown LoRaWAN node '{command.DeviceId}'.");
        if (command.Action is not ("aerator_on" or "aerator_off")) throw new NotSupportedException($"Action '{command.Action}' is not mapped to a downlink.");
        _server.EnqueueDownlink(Eui64.Parse(node.DevEui), CommandPort, [(byte)(command.Action == "aerator_on" ? 1 : 0)], false);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected) await _server.StopAsync(CancellationToken.None).ConfigureAwait(false);
        IsConnected = false;
    }
}

// ─────────────────────────────── LwM2M (OMA, CoAP) ───────────────────────────────

/// <summary>
/// LwM2M server for NB-IoT / LTE-M pond sensors that speak IPSO smart objects. Registered devices are observed on
/// 3303 Temperature, 3304 Humidity, 3300 Generic Sensor (the Application Type resource 5750 names the parameter: "DO", "pH", "salinity"…),
/// 3301 Illuminance and 3315 Barometer; aerators are 3306 Actuation (5850 On/Off). <paramref name="pondOf"/> maps an endpoint name to its pond
/// (default: the part before the first <c>'.'</c>, so <c>"P-01.probe-3"</c> → pond P-01, device probe-3).
/// </summary>
public sealed class Lwm2mPondGateway(Action<Lwm2mServerOptions> configure, Func<string, (string Pond, string Device)>? pondOf = null) : ISensorTransport
{
    public const ushort Temperature = 3303, Humidity = 3304, GenericSensor = 3300, Illuminance = 3301, Barometer = 3315, Actuation = 3306;
    public const ushort SensorValue = 5700, ApplicationType = 5750, OnOff = 5850;
    private const ushort TextPlain = 0;

    private readonly ConcurrentDictionary<string, List<Lwm2mObservation>> _observations = new();
    private readonly Func<string, (string Pond, string Device)> _pondOf = pondOf ?? DefaultPondOf;
    private Lwm2mServer? _server;

    public static Lwm2mPondGateway Udp(int port = 5683, Func<string, (string Pond, string Device)>? pondOf = null) =>
        new(o => o.UseUdp(port, IPAddress.Any), pondOf);

    public static (string Pond, string Device) DefaultPondOf(string endpoint)
    {
        int i = endpoint.IndexOf('.');
        return i > 0 ? (endpoint[..i], endpoint[(i + 1)..]) : ("", endpoint);
    }

    public event Action<SensorReading>? ReadingReceived;
    public event Action<string, Exception>? DeviceFailed;
    public bool IsConnected => _server is not null;
    public EndPoint? LocalEndPoint => _server?.LocalEndPoint;
    public IReadOnlyCollection<Lwm2mRegistration> Registrations => _server?.Registrations ?? [];

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_server is not null) return;
        _server = Lwm2mServer.Create(o => { o.AllowWrites(); o.Name = "AquaCulture.Net LwM2M"; configure(o); });
        _server.Registered += r => _ = ObserveAsync(r);
        _server.Deregistered += (r, reason) => _ = ForgetAsync(r.Endpoint);
        await _server.StartAsync(ct).ConfigureAwait(false);
    }

    private async Task ObserveAsync(Lwm2mRegistration reg)
    {
        var server = _server!;
        var (pond, device) = _pondOf(reg.Endpoint);
        var list = _observations.GetOrAdd(reg.Endpoint, _ => []);
        try
        {
            foreach (var path in reg.Objects.Where(p => p.InstanceId is not null && p.ObjectId is Temperature or Humidity or GenericSensor or Illuminance or Barometer))
            {
                SensorKind? kind = path.ObjectId switch
                {
                    Temperature => SensorKind.Temperature, Humidity => SensorKind.Humidity, Illuminance => SensorKind.SolarRadiation,
                    Barometer => null, _ => null,
                };
                if (path.ObjectId == GenericSensor)
                {
                    var app = await server.ReadValueAsync(reg.Endpoint, new Lwm2mPath(GenericSensor, path.InstanceId, ApplicationType, null)).ConfigureAwait(false);
                    string? name = app switch { string s => s, byte[] b => System.Text.Encoding.UTF8.GetString(b), _ => null };
                    if (name is not null && SensorReading.TryParseKind(name.Trim(), out var k)) kind = k; else continue;
                }
                if (kind is not { } kk) continue;
                var valuePath = new Lwm2mPath(path.ObjectId, path.InstanceId, SensorValue, null);
                // text/plain (content-format 0): objects without a typed definition (3300) would otherwise arrive as opaque bytes
                var obs = await server.ObserveAsync(reg.Endpoint, valuePath, values => Emit(values, pond, device, kk), TextPlain).ConfigureAwait(false);
                lock (list) list.Add(obs);
                Emit(obs.Initial, pond, device, kk);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { DeviceFailed?.Invoke(reg.Endpoint, e); }
    }

    private void Emit(IReadOnlyList<Lwm2mValue> values, string pond, string device, SensorKind kind)
    {
        foreach (var v in values)
        {
            double? x = v.Value switch
            {
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                byte[] b when double.TryParse(System.Text.Encoding.UTF8.GetString(b), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                bool => null,
                IConvertible c and not string => c.ToDouble(CultureInfo.InvariantCulture),
                _ => null,
            };
            if (x is { } value)
                ReadingReceived?.Invoke(new SensorReading(device, pond, kind, value, DateTime.UtcNow, SensorReading.DefaultUnit(kind)));
        }
    }

    private async Task ForgetAsync(string endpoint)
    {
        if (!_observations.TryRemove(endpoint, out var list)) return;
        foreach (var o in list) await o.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary><c>aerator_on</c>/<c>aerator_off</c> write 3306/0/5850 on the endpoint named by <see cref="ActuatorCommand.DeviceId"/>.</summary>
    public async Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default)
    {
        var server = _server ?? throw new InvalidOperationException("Call ConnectAsync first.");
        if (command.Action is not ("aerator_on" or "aerator_off")) throw new NotSupportedException($"Action '{command.Action}' is not mapped to LwM2M.");
        await server.WriteAsync(command.DeviceId, new Lwm2mPath(Actuation, 0, OnOff, null), command.Action == "aerator_on", ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ep in _observations.Keys.ToList()) await ForgetAsync(ep).ConfigureAwait(false);
        if (_server is not null) await _server.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _server = null;
    }
}

// ─────────────────────────────── M-Bus energy meters ───────────────────────────────

/// <summary>An M-Bus (EN 13757) electricity meter on an aerator circuit.</summary>
public sealed record MBusAeratorMeter(byte Address, string DeviceId, string PondId);

/// <summary>
/// Reads M-Bus electricity meters on aerator circuits (wired M-Bus over a level converter on a serial port, or M-Bus over TCP). Instantaneous
/// power becomes <see cref="SensorKind.Power"/> (kW), the energy register <see cref="SensorKind.Energy"/> (kWh) and phase current <see cref="SensorKind.AeratorCurrent"/> (A). Combine with harvest data
/// for kWh per kg of shrimp, a key cost and ESG figure of intensive ponds.
/// </summary>
public sealed class MBusEnergyReader(Action<MBusMasterOptions> configure, IReadOnlyList<MBusAeratorMeter> meters) : IAsyncDisposable
{
    private MBusMaster? _master;

    /// <summary>M-Bus over a serial level converter: 2400 baud, 8 data bits, even parity, one stop bit.</summary>
    public static MBusEnergyReader Serial(string portName, IReadOnlyList<MBusAeratorMeter> meters, int baudRate = 2400) =>
        new(o => o.UseSerial(portName, baudRate, System.IO.Ports.Parity.Even, System.IO.Ports.StopBits.One, 8), meters);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _master ??= MBusMaster.Create(configure);
        await _master.ConnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads every meter once (a meter that does not answer is skipped).</summary>
    public async Task<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken ct = default)
    {
        var master = _master ?? throw new InvalidOperationException("Call ConnectAsync first.");
        var list = new List<SensorReading>();
        foreach (var m in meters)
        {
            MBusTelegram t;
            try { t = await master.ReadAsync(m.Address, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is IoTComException or TimeoutException) { continue; }
            list.AddRange(ToReadings(t, m, DateTime.UtcNow));
        }
        return list;
    }

    /// <summary>Converts a telegram: current-value (storage 0, tariff 0) energy and power records, scaled to kWh and kW.</summary>
    public static IReadOnlyList<SensorReading> ToReadings(MBusTelegram telegram, MBusAeratorMeter meter, DateTime utc)
    {
        var list = new List<SensorReading>();
        foreach (var r in telegram.Records)
        {
            if (r.Value is not { } v || r.StorageNumber != 0 || r.Tariff != 0 || r.Function != MBusFunction.Instantaneous) continue;
            string unit = r.Unit ?? "", q = r.Quantity ?? "";
            double? kwh = unit switch { "Wh" => v / 1000, "kWh" => v, "MWh" => v * 1000, "J" => v / 3.6e6, "kJ" => v / 3600, "MJ" => v / 3.6, _ => null };
            double? kw = unit switch { "W" => v / 1000, "kW" => v, "MW" => v * 1000, "J/h" => v / 3.6e6, "kJ/h" => v / 3600, _ => null };
            if (q.Contains("Power", StringComparison.OrdinalIgnoreCase) && kw is { } p && list.All(x => x.Kind != SensorKind.Power))
                list.Add(new SensorReading(meter.DeviceId, meter.PondId, SensorKind.Power, Math.Round(p, 4), utc, "kW"));
            else if (q.Contains("Energy", StringComparison.OrdinalIgnoreCase) && kwh is { } e && list.All(x => x.Kind != SensorKind.Energy))
                list.Add(new SensorReading(meter.DeviceId, meter.PondId, SensorKind.Energy, Math.Round(e, 4), utc, "kWh"));
            else if (q.Equals("Current", StringComparison.OrdinalIgnoreCase) && unit == "A" && list.All(x => x.Kind != SensorKind.AeratorCurrent))
                list.Add(new SensorReading(meter.DeviceId, meter.PondId, SensorKind.AeratorCurrent, v, utc, "A"));
        }
        return list;
    }

    /// <summary>Aeration energy intensity: kWh used between two energy readings per kg of biomass produced.</summary>
    public static double KwhPerKg(double energyStartKwh, double energyEndKwh, double harvestKg) =>
        harvestKg <= 0 ? double.NaN : (energyEndKwh - energyStartKwh) / harvestKg;

    public async ValueTask DisposeAsync()
    {
        if (_master is not null) await _master.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
