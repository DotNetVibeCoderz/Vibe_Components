using System.Collections.Concurrent;
using Agritech.Core.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.IoT;

public enum AlertSeverity { Info, Warning, Critical }

public sealed record SensorAlert(string Key, string Message, AlertSeverity Severity, double Value, DateTimeOffset Timestamp) : IAgriEvent;

/// <summary>Threshold rule for a sensor kind (optionally a specific device).</summary>
public sealed record ThresholdRule(SensorKind Kind, double? Min, double? Max, AlertSeverity Severity, string Message, string? DeviceId = null);

/// <summary>Rolling statistics over a fixed-size window (O(1) update).</summary>
public sealed class RollingWindow(int capacity)
{
    private readonly double[] _buf = new double[capacity];
    private int _count, _head;
    private double _sum, _sumSq;

    public int Count => _count;
    public double Mean => _count == 0 ? double.NaN : _sum / _count;
    public double StdDev => _count < 2 ? 0 : Math.Sqrt(Math.Max(0, (_sumSq - _sum * _sum / _count) / (_count - 1)));
    public double Last { get; private set; } = double.NaN;

    public void Add(double v)
    {
        if (_count == capacity)
        {
            double old = _buf[_head];
            _sum -= old; _sumSq -= old * old;
        }
        else _count++;
        _buf[_head] = v;
        _head = (_head + 1) % capacity;
        _sum += v; _sumSq += v * v;
        Last = v;
    }
}

/// <summary>
/// Edge analytics pipeline: per-sensor rolling statistics, threshold rules, z-score anomaly detection and
/// report-by-exception deadband filtering (cuts uplink bandwidth on LoRa/cellular links).
/// </summary>
public sealed class EdgeProcessor
{
    private readonly ConcurrentDictionary<string, RollingWindow> _windows = new();
    private readonly ConcurrentDictionary<string, double> _lastForwarded = new();
    private readonly List<ThresholdRule> _rules = [];
    private readonly IAgriEventBus? _bus;

    public EdgeProcessor(IAgriEventBus? bus = null, int windowSize = 60, double anomalyZ = 4, double deadband = 0)
    {
        _bus = bus; WindowSize = windowSize; AnomalyZ = anomalyZ; Deadband = deadband;
    }

    public int WindowSize { get; }
    public double AnomalyZ { get; }
    /// <summary>Minimum absolute change required to forward a reading upstream (0 = forward everything).</summary>
    public double Deadband { get; }
    public event Action<SensorAlert>? Alert;
    public event Action<SensorReading>? Forward;
    public long Received { get; private set; }
    public long Forwarded { get; private set; }

    public EdgeProcessor AddRule(ThresholdRule rule) { _rules.Add(rule); return this; }

    /// <summary>Default agronomic rules (heat stress, dry soil, low dissolved oxygen, high ammonia…).</summary>
    public EdgeProcessor AddDefaultAgronomicRules() => this
        .AddRule(new(SensorKind.AirTemperature, null, 35, AlertSeverity.Warning, "Heat stress risk (air > 35 °C)"))
        .AddRule(new(SensorKind.SoilMoisture, 18, null, AlertSeverity.Warning, "Soil is drying — irrigation advised"))
        .AddRule(new(SensorKind.SoilMoisture, null, 45, AlertSeverity.Info, "Soil near saturation — pause irrigation"))
        .AddRule(new(SensorKind.DissolvedOxygen, 3, null, AlertSeverity.Critical, "Low dissolved oxygen — start aerators"))
        .AddRule(new(SensorKind.Ammonia, null, 1.0, AlertSeverity.Warning, "High total ammonia nitrogen"))
        .AddRule(new(SensorKind.WaterPh, 6.5, 9.0, AlertSeverity.Warning, "Pond pH out of range"))
        .AddRule(new(SensorKind.BatteryVoltage, 3.4, null, AlertSeverity.Info, "Device battery low"));

    public RollingWindow? Window(string key) => _windows.TryGetValue(key, out var w) ? w : null;
    public IReadOnlyDictionary<string, RollingWindow> Windows => _windows;

    public void Process(SensorReading r)
    {
        Received++;
        var w = _windows.GetOrAdd(r.Key, _ => new RollingWindow(WindowSize));
        if (w.Count >= 10)
        {
            double z = w.StdDev == 0 ? 0 : (r.Value - w.Mean) / w.StdDev;
            if (Math.Abs(z) >= AnomalyZ)
                Raise(new SensorAlert(r.Key, $"Anomalous {r.Kind} value {r.Value} {r.Unit} (z = {z:F1})", AlertSeverity.Warning, r.Value, r.Timestamp));
        }
        w.Add(r.Value);
        foreach (var rule in _rules)
        {
            if (rule.Kind != r.Kind || (rule.DeviceId is not null && rule.DeviceId != r.DeviceId)) continue;
            if ((rule.Min is { } min && r.Value < min) || (rule.Max is { } max && r.Value > max))
                Raise(new SensorAlert(r.Key, rule.Message, rule.Severity, r.Value, r.Timestamp));
        }
        if (Deadband <= 0 || !_lastForwarded.TryGetValue(r.Key, out var last) || Math.Abs(r.Value - last) >= Deadband)
        {
            _lastForwarded[r.Key] = r.Value;
            Forwarded++;
            Forward?.Invoke(r);
            _bus?.Publish(r);
        }
    }

    private void Raise(SensorAlert a)
    {
        Alert?.Invoke(a);
        _bus?.Publish(a);
    }
}

public static class IoTBuilderExtensions
{
    public static AgritechBuilder AddIoT(this AgritechBuilder builder, Func<IServiceProvider, ISensorTransport>? transport = null)
    {
        builder.TryAddModule("IoT");
        if (transport is not null) builder.Services.TryAddSingleton(transport);
        else builder.Services.TryAddSingleton<ISensorTransport, InMemoryTransport>();
        builder.Services.TryAddSingleton(sp => new EdgeProcessor(sp.GetService(typeof(IAgriEventBus)) as IAgriEventBus).AddDefaultAgronomicRules());
        return builder;
    }
}
