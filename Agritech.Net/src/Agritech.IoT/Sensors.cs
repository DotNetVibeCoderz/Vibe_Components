using System.Globalization;
using System.Text.Json;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.IoT;

public enum SensorKind
{
    AirTemperature, RelativeHumidity, Rainfall, WindSpeed, SolarRadiation, Par, LeafWetness, Barometer,
    SoilMoisture, SoilTemperature, SoilEc, SoilPh, SoilNitrogen,
    WaterLevel, WaterTemperature, DissolvedOxygen, WaterPh, Ammonia, Turbidity,
    Co2, Illuminance, BatteryVoltage, Digital, Analog, Gps, Custom
}

/// <summary>A single sensor measurement.</summary>
public sealed record SensorReading(string DeviceId, SensorKind Kind, double Value, string Unit, DateTimeOffset Timestamp, int Channel = 0,
    GeoCoordinate? Location = null) : IAgriEvent
{
    public string Key => $"{DeviceId}/{Kind}/{Channel}";

    public static string DefaultUnit(SensorKind k) => k switch
    {
        SensorKind.AirTemperature or SensorKind.SoilTemperature or SensorKind.WaterTemperature => "°C",
        SensorKind.RelativeHumidity => "%",
        SensorKind.SoilMoisture => "%VWC",
        SensorKind.Rainfall => "mm",
        SensorKind.WindSpeed => "m/s",
        SensorKind.SolarRadiation => "W/m²",
        SensorKind.Par => "µmol/m²/s",
        SensorKind.LeafWetness => "%",
        SensorKind.Barometer => "hPa",
        SensorKind.SoilEc => "dS/m",
        SensorKind.SoilPh or SensorKind.WaterPh => "pH",
        SensorKind.SoilNitrogen => "mg/kg",
        SensorKind.WaterLevel => "cm",
        SensorKind.DissolvedOxygen => "mg/L",
        SensorKind.Ammonia => "mg/L",
        SensorKind.Turbidity => "NTU",
        SensorKind.Co2 => "ppm",
        SensorKind.Illuminance => "lux",
        SensorKind.BatteryVoltage => "V",
        _ => "",
    };

    /// <summary>Compact JSON payload <c>{"v":..,"u":"..","ts":"..","ch":..}</c> used on MQTT topics.</summary>
    public string ToJson() => string.Create(CultureInfo.InvariantCulture,
        $"{{\"v\":{Value},\"u\":{JsonSerializer.Serialize(Unit, IoTJsonContext.Default.String)},\"ts\":\"{Timestamp:O}\",\"ch\":{Channel}}}");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class IoTJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Bidirectional sensor message transport (MQTT, LoRaWAN network server, in-memory…).</summary>
public interface ISensorTransport : IAsyncDisposable
{
    event Action<SensorReading>? ReadingReceived;
    Task ConnectAsync(CancellationToken ct = default);
    Task PublishAsync(SensorReading reading, CancellationToken ct = default);
}

/// <summary>In-process loopback transport — ideal for tests, simulations and single-process edge apps.</summary>
public sealed class InMemoryTransport : ISensorTransport
{
    public event Action<SensorReading>? ReadingReceived;
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishAsync(SensorReading reading, CancellationToken ct = default)
    {
        ReadingReceived?.Invoke(reading);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Simulated field sensor network producing physically plausible diurnal signals.</summary>
public sealed class SimulatedSensorNetwork(ISensorTransport transport, int seed = 3, double initialSoilMoisture = 30, double dryingRatePerHour = 0.12)
{
    private readonly Random _rng = new(seed);
    private readonly List<(string Device, SensorKind Kind, double Base, double Amp, double Noise)> _sensors = [];
    private double _soilMoisture = initialSoilMoisture;

    public SimulatedSensorNetwork AddWeatherStation(string deviceId)
    {
        _sensors.Add((deviceId, SensorKind.AirTemperature, 27, 4.5, 0.4));
        _sensors.Add((deviceId, SensorKind.RelativeHumidity, 78, -14, 2));
        _sensors.Add((deviceId, SensorKind.SolarRadiation, 0, 900, 40));
        _sensors.Add((deviceId, SensorKind.WindSpeed, 1.8, 1.2, 0.4));
        _sensors.Add((deviceId, SensorKind.LeafWetness, 30, -30, 5));
        return this;
    }

    public SimulatedSensorNetwork AddSoilProbe(string deviceId)
    {
        _sensors.Add((deviceId, SensorKind.SoilMoisture, double.NaN, 0, 0.3));
        _sensors.Add((deviceId, SensorKind.SoilTemperature, 26, 2, 0.2));
        _sensors.Add((deviceId, SensorKind.SoilEc, 0.8, 0.05, 0.02));
        return this;
    }

    public SimulatedSensorNetwork AddPondProbe(string deviceId)
    {
        _sensors.Add((deviceId, SensorKind.DissolvedOxygen, 5.5, 2.0, 0.2));
        _sensors.Add((deviceId, SensorKind.WaterTemperature, 29, 1.5, 0.1));
        _sensors.Add((deviceId, SensorKind.WaterPh, 7.6, 0.4, 0.05));
        _sensors.Add((deviceId, SensorKind.Ammonia, 0.4, 0.1, 0.05));
        return this;
    }

    /// <summary>Emits one reading per sensor for the given instant.</summary>
    public async Task<int> TickAsync(DateTimeOffset now, double rainMm = 0, CancellationToken ct = default)
    {
        double hour = now.TimeOfDay.TotalHours;
        double diurnal = Math.Sin((hour - 9) / 24 * 2 * Math.PI); // peak ≈ 15:00
        double sun = Math.Max(0, Math.Sin((hour - 6) / 12 * Math.PI));
        _soilMoisture = Math.Clamp(_soilMoisture - dryingRatePerHour * sun + rainMm * 0.6, 12, 48);
        int n = 0;
        foreach (var s in _sensors)
        {
            double v = s.Kind switch
            {
                SensorKind.SolarRadiation => s.Amp * sun,
                SensorKind.SoilMoisture => _soilMoisture,
                _ => s.Base + s.Amp * diurnal,
            } + _rng.NextDouble() * 2 * s.Noise - s.Noise;
            if (s.Kind is SensorKind.RelativeHumidity or SensorKind.LeafWetness) v = Math.Clamp(v, 0, 100);
            if (s.Kind is SensorKind.SolarRadiation or SensorKind.WindSpeed or SensorKind.Ammonia) v = Math.Max(0, v);
            await transport.PublishAsync(new SensorReading(s.Device, s.Kind, Math.Round(v, 2), SensorReading.DefaultUnit(s.Kind), now), ct).ConfigureAwait(false);
            n++;
        }
        return n;
    }
}
