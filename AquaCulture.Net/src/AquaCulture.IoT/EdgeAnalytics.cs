using AquaCulture.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AquaCulture.IoT;

/// <summary>Result of edge filtering for one reading.</summary>
public sealed record EdgeVerdict(SensorReading Reading, bool IsOutlier, bool IsStuck, double Smoothed, double? Corrected);

/// <summary>
/// Edge analytics for gateways: Hampel outlier filter (median ± k·MAD), stuck-sensor detection, exponential smoothing and
/// two-point calibration. Keeps a small ring buffer per (device, kind).
/// </summary>
public sealed class EdgeAnalytics(int window = 11, double hampelK = 3, int stuckCount = 12)
{
    private readonly Dictionary<(string, SensorKind), Queue<double>> _buffers = [];
    private readonly Dictionary<(string, SensorKind), double> _ema = [];
    private readonly Dictionary<(string, SensorKind), (double Gain, double Offset)> _calibration = [];

    public double SmoothingAlpha { get; init; } = 0.3;

    /// <summary>Two-point calibration: maps measured readings (m1, m2) to reference values (r1, r2).</summary>
    public void Calibrate(string deviceId, SensorKind kind, double measured1, double reference1, double measured2, double reference2)
    {
        double gain = (reference2 - reference1) / (measured2 - measured1);
        _calibration[(deviceId, kind)] = (gain, reference1 - gain * measured1);
    }

    public EdgeVerdict Process(SensorReading r)
    {
        var key = (r.DeviceId, r.Kind);
        double v = r.Value;
        double? corrected = null;
        if (_calibration.TryGetValue(key, out var cal)) { v = cal.Gain * v + cal.Offset; corrected = v; }
        if (!_buffers.TryGetValue(key, out var q)) _buffers[key] = q = new Queue<double>();
        bool outlier = false, stuck = false;
        if (q.Count >= 5)
        {
            var arr = q.ToArray(); Array.Sort(arr);
            double med = arr[arr.Length / 2];
            var dev = arr.Select(x => Math.Abs(x - med)).ToArray(); Array.Sort(dev);
            double mad = 1.4826 * dev[dev.Length / 2];
            // a minimum tolerance (5 % of the level) keeps very quiet signals from flagging normal noise
            double tol = Math.Max(hampelK * mad, Math.Max(0.05 * Math.Abs(med), 1e-6));
            outlier = Math.Abs(v - med) > tol;
        }
        if (q.Count >= stuckCount - 1 && q.TakeLast(stuckCount - 1).All(x => x == v)) stuck = true;
        // every value enters the window, so a genuine level shift is accepted after a few samples instead of locking the filter out
        q.Enqueue(v);
        while (q.Count > Math.Max(window, stuckCount)) q.Dequeue();
        double ema = _ema.TryGetValue(key, out var e) && !outlier ? e + SmoothingAlpha * (v - e) : _ema.GetValueOrDefault(key, v);
        _ema[key] = ema;
        return new EdgeVerdict(r with { Value = v }, outlier, stuck, ema, corrected);
    }
}

/// <summary>Maps OPC-UA node ids (from a PLC/SCADA) to sensors. Supply the actual read through <see cref="IOpcUaReader"/>.</summary>
public sealed record OpcUaNodeMap(string NodeId, string DeviceId, string PondId, SensorKind Kind, double Scale = 1, double Offset = 0);

/// <summary>
/// Abstraction over an OPC-UA client session (e.g. the OPC Foundation .NET Standard stack); keeps this package dependency-free.
/// </summary>
public interface IOpcUaReader
{
    Task<IReadOnlyDictionary<string, double>> ReadAsync(IEnumerable<string> nodeIds, CancellationToken ct = default);
}

/// <summary>Polls mapped OPC-UA nodes and converts values into readings.</summary>
public sealed class OpcUaSensorPoller(IOpcUaReader reader, IReadOnlyList<OpcUaNodeMap> map)
{
    public async Task<IReadOnlyList<SensorReading>> PollAsync(CancellationToken ct = default)
    {
        var values = await reader.ReadAsync(map.Select(m => m.NodeId), ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return map.Where(m => values.ContainsKey(m.NodeId))
                  .Select(m => new SensorReading(m.DeviceId, m.PondId, m.Kind, values[m.NodeId] * m.Scale + m.Offset, now, SensorReading.DefaultUnit(m.Kind))).ToList();
    }
}

public static class IoTBuilderExtensions
{
    /// <summary>Registers the device registry and edge analytics.</summary>
    public static AquaBuilder AddIoT(this AquaBuilder b) => b.TryAddModule("IoT", s =>
    {
        s.TryAddSingleton<DeviceRegistry>();
        s.TryAddSingleton(_ => new EdgeAnalytics());
    });
}
