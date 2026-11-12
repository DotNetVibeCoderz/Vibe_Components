using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaCulture.Core.Hosting;

namespace AquaCulture.IoT;

public enum DeviceKind { Esp32, Esp8266, Arduino, RaspberryPi, IndustrialPlc, Gateway, Simulated }

public enum IoTProtocol { Mqtt, LoRaWan, Rest, ModbusRtu, ModbusTcp, OpcUa }

/// <summary>Measured quantity. Mirrors the 15 water-quality parameters plus farm signals.</summary>
public enum SensorKind
{
    Temperature, Ph, DissolvedOxygen, Orp, Salinity, Tds, Tss, Alkalinity, Ammonia, Nitrite, Nitrate, Phosphate, Bod, Cod, Turbidity,
    WaterLevel, AirTemperature, Humidity, Rainfall, WindSpeed, SolarRadiation, AeratorCurrent, FeederRemaining, Battery, Rssi,
    /// <summary>Electrical power (kW), for example of an aerator from an energy meter.</summary>
    Power,
    /// <summary>Cumulative electrical energy (kWh).</summary>
    Energy,
}

/// <summary>A sensor reading.</summary>
public sealed record SensorReading(string DeviceId, string PondId, SensorKind Kind, double Value, DateTime Timestamp, string Unit = "", byte Quality = 100) : IAquaEvent
{
    public static string DefaultUnit(SensorKind k) => k switch
    {
        SensorKind.Temperature or SensorKind.AirTemperature => "°C",
        SensorKind.DissolvedOxygen or SensorKind.Tds or SensorKind.Tss or SensorKind.Ammonia or SensorKind.Nitrite or SensorKind.Nitrate
            or SensorKind.Phosphate or SensorKind.Bod or SensorKind.Cod => "mg/L",
        SensorKind.Alkalinity => "mg/L CaCO3",
        SensorKind.Orp => "mV",
        SensorKind.Salinity => "ppt",
        SensorKind.Turbidity => "NTU",
        SensorKind.WaterLevel => "cm",
        SensorKind.Humidity => "%",
        SensorKind.Rainfall => "mm",
        SensorKind.WindSpeed => "m/s",
        SensorKind.SolarRadiation => "W/m2",
        SensorKind.AeratorCurrent => "A",
        SensorKind.FeederRemaining => "kg",
        SensorKind.Battery => "V",
        SensorKind.Rssi => "dBm",
        SensorKind.Power => "kW",
        SensorKind.Energy => "kWh",
        _ => "",
    };

    /// <summary>Compact JSON <c>{"device":…,"pond":…,"kind":…,"v":…,"u":…,"ts":…}</c>.</summary>
    public string ToJson() => new JsonObject
    {
        ["device"] = DeviceId, ["pond"] = PondId, ["kind"] = Kind.ToString(), ["v"] = Value, ["u"] = Unit,
        ["ts"] = Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    }.ToJsonString();

    public static bool TryParseKind(string key, out SensorKind kind)
    {
        if (Enum.TryParse(key, true, out kind)) return true;
        switch (key.ToLowerInvariant())
        {
            case "temp" or "temp_c" or "t" or "suhu" or "water_temp": kind = SensorKind.Temperature; return true;
            case "do" or "oxygen" or "do_mg_l" or "oksigen": kind = SensorKind.DissolvedOxygen; return true;
            case "ph": kind = SensorKind.Ph; return true;
            case "orp": kind = SensorKind.Orp; return true;
            case "sal" or "salinity_ppt" or "salinitas": kind = SensorKind.Salinity; return true;
            case "tan" or "nh3" or "nh4" or "amonia": kind = SensorKind.Ammonia; return true;
            case "no2" or "nitrit": kind = SensorKind.Nitrite; return true;
            case "no3" or "nitrat": kind = SensorKind.Nitrate; return true;
            case "po4" or "fosfat": kind = SensorKind.Phosphate; return true;
            case "ntu" or "turb" or "kekeruhan": kind = SensorKind.Turbidity; return true;
            case "level" or "water_level" or "tinggi_air": kind = SensorKind.WaterLevel; return true;
            case "air_temp" or "tair": kind = SensorKind.AirTemperature; return true;
            case "rh" or "hum": kind = SensorKind.Humidity; return true;
            case "rain": kind = SensorKind.Rainfall; return true;
            case "wind": kind = SensorKind.WindSpeed; return true;
            case "solar" or "rad": kind = SensorKind.SolarRadiation; return true;
            case "bat" or "vbat": kind = SensorKind.Battery; return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a multi-value JSON telemetry document as sent by ESP32/ESP8266/Raspberry Pi nodes:
    /// <c>{"device":"esp32-k01","pond":"K-01","ts":"…","temp":29.1,"do":5.2,"ph":7.8}</c>. Unknown keys are ignored.
    /// </summary>
    public static IReadOnlyList<SensorReading> ParseTelemetry(string json, string? defaultDevice = null, string? defaultPond = null)
    {
        var list = new List<SensorReading>();
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string device = r.TryGetProperty("device", out var d) ? d.GetString() ?? "" : defaultDevice ?? "";
        string pond = r.TryGetProperty("pond", out var p) ? p.GetString() ?? "" : defaultPond ?? "";
        var ts = r.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.String && t.TryGetDateTime(out var dt) ? dt.ToUniversalTime()
            : r.TryGetProperty("ts", out var tn) && tn.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(tn.GetInt64()).UtcDateTime : DateTime.UtcNow;
        if (r.TryGetProperty("kind", out var kEl) && r.TryGetProperty("v", out var vEl) && TryParseKind(kEl.GetString() ?? "", out var kk))
        {
            list.Add(new SensorReading(device, pond, kk, vEl.GetDouble(), ts, r.TryGetProperty("u", out var u) ? u.GetString() ?? DefaultUnit(kk) : DefaultUnit(kk)));
            return list;
        }
        foreach (var prop in r.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Number && TryParseKind(prop.Name, out var kind))
                list.Add(new SensorReading(device, pond, kind, prop.Value.GetDouble(), ts, DefaultUnit(kind)));
        return list;
    }
}

/// <summary>A registered field device.</summary>
public sealed record IoTDevice(string Id, DeviceKind Kind, IoTProtocol Protocol, string PondId, IReadOnlyList<SensorKind> Sensors, string Firmware = "")
{
    public DateTime LastSeen { get; set; }
    public double? BatteryV { get; set; }
    public bool IsOnline(DateTime now, TimeSpan timeout) => now - LastSeen <= timeout;
}

/// <summary>Thread-safe device registry that tracks last-seen times from incoming readings.</summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, IoTDevice> _devices = new();

    public IoTDevice Register(IoTDevice d) => _devices[d.Id] = d;
    public IReadOnlyCollection<IoTDevice> All => (IReadOnlyCollection<IoTDevice>)_devices.Values;
    public IoTDevice? Find(string id) => _devices.TryGetValue(id, out var d) ? d : null;
    public IEnumerable<IoTDevice> ForPond(string pondId) => _devices.Values.Where(d => d.PondId == pondId);

    public void Touch(SensorReading r)
    {
        if (!_devices.TryGetValue(r.DeviceId, out var d)) return;
        if (r.Timestamp > d.LastSeen) d.LastSeen = r.Timestamp;
        if (r.Kind == SensorKind.Battery) d.BatteryV = r.Value;
    }

    public IEnumerable<IoTDevice> Offline(DateTime now, TimeSpan timeout) => _devices.Values.Where(d => !d.IsOnline(now, timeout));
}

/// <summary>Command to an actuator (aerator, feeder, pump, valve).</summary>
public sealed record ActuatorCommand(string DeviceId, string Action, double Value = 0, DateTime? Timestamp = null)
{
    public string ToJson() => new JsonObject { ["device"] = DeviceId, ["action"] = Action, ["value"] = Value,
        ["ts"] = (Timestamp ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture) }.ToJsonString();
}

/// <summary>Source of sensor readings and sink for actuator commands.</summary>
public interface ISensorTransport : IAsyncDisposable
{
    event Action<SensorReading>? ReadingReceived;
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default);
}
