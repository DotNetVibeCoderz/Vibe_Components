using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;
using AquaCulture.Environment;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

namespace AquaCulture.DigitalTwin;

/// <summary>
/// Farm-level twin: routes sensor readings to pond twins, tracks devices, evaluates every pond and (optionally) sends actuator
/// commands such as <c>aerator_on</c> back through the transport.
/// </summary>
public sealed class FarmTwin : IAsyncDisposable
{
    private readonly Dictionary<string, PondTwin> _ponds = new(StringComparer.Ordinal);
    private readonly ISensorTransport? _transport;
    private readonly IAquaEventBus? _bus;

    public FarmTwin(Farm farm, ISensorTransport? transport = null, IAquaEventBus? bus = null)
    {
        Farm = farm; _transport = transport; _bus = bus;
        foreach (var c in farm.ActiveCycles) Add(PondTwin.FromCycle(farm.Pond(c.PondId), c, bus));
        if (_transport is not null) _transport.ReadingReceived += OnReading;
    }

    public Farm Farm { get; }
    public DeviceRegistry Devices { get; } = new();
    public EdgeAnalytics Edge { get; } = new();
    public IReadOnlyCollection<PondTwin> Ponds => _ponds.Values;
    /// <summary>Send actuator commands from decisions automatically.</summary>
    public bool AutoActuate { get; set; }
    public int ReadingsIngested { get; private set; }
    public int OutliersRejected { get; private set; }

    public PondTwin Add(PondTwin twin) { _ponds[twin.Pond.Id] = twin; return twin; }
    public PondTwin this[string pondId] => _ponds[pondId];

    public IReadOnlyList<WeatherObservation>? WeatherForecast
    {
        set { foreach (var p in _ponds.Values) p.WeatherForecast = value; }
    }

    public Task StartAsync(CancellationToken ct = default) => _transport?.ConnectAsync(ct) ?? Task.CompletedTask;

    private void OnReading(SensorReading r) => Ingest(r);

    /// <summary>Filters (edge analytics) and routes a reading.</summary>
    public void Ingest(SensorReading r)
    {
        Devices.Touch(r);
        var v = Edge.Process(r);
        if (v.IsOutlier) { OutliersRejected++; return; }
        if (_ponds.TryGetValue(r.PondId, out var twin)) { twin.Ingest(v.Reading); ReadingsIngested++; }
    }

    /// <summary>Evaluates all ponds; sends commands when <see cref="AutoActuate"/> is on.</summary>
    public async Task<IReadOnlyList<TwinDecision>> EvaluateAsync(DateTime now, CancellationToken ct = default)
    {
        var all = new List<TwinDecision>();
        foreach (var p in _ponds.Values) all.AddRange(p.Evaluate(now));
        if (AutoActuate && _transport is not null)
            foreach (var cmd in all.Where(d => d.Command is not null && d.Severity >= AlertSeverity.Warning).Select(d => d.Command!).DistinctBy(c => (c.DeviceId, c.Action)))
                await _transport.SendCommandAsync(cmd, ct).ConfigureAwait(false);
        return all;
    }

    public IReadOnlyList<TwinState> States(DateTime now) =>
        _ponds.Values.Select(p => p.State(now, Devices.ForPond(p.Pond.Id).Count(d => d.IsOnline(now, TimeSpan.FromMinutes(30))))).ToList();

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null) { _transport.ReadingReceived -= OnReading; await _transport.DisposeAsync().ConfigureAwait(false); }
    }
}

public static class DigitalTwinBuilderExtensions
{
    public static AquaBuilder AddDigitalTwin(this AquaBuilder b) => b.TryAddModule("DigitalTwin", _ => { });
}
