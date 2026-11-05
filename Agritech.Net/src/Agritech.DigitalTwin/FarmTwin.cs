using System.Collections.Concurrent;
using Agritech.AI;
using Agritech.Core.Farming;
using Agritech.Core.Hosting;
using Agritech.Crops;
using Agritech.IoT;
using Agritech.Soil;
using Agritech.Weather;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.DigitalTwin;

public enum DecisionType { Irrigate, PauseIrrigation, Fertilize, ScoutDisease, ProtectFromHeat, Harvest, CheckDevice }

/// <summary>An actionable, explained recommendation produced by the twin.</summary>
public sealed record Decision(Guid FieldId, string FieldName, DecisionType Type, string Title, string Rationale, double Confidence, DateTimeOffset CreatedAt) : IAgriEvent
{
    public DateTimeOffset Timestamp => CreatedAt;
}

/// <summary>Live state of one field.</summary>
public sealed class FieldState(Field field, CropDefinition? crop, SoilSample soil)
{
    public Field Field { get; } = field;
    public CropDefinition? Crop { get; } = crop;
    public SoilSample Soil { get; set; } = soil;
    public CropLifecycle? Lifecycle { get; set; } = crop is not null && field.PlantingDate is { } pd ? new CropLifecycle(crop, pd) : null;
    public ConcurrentDictionary<SensorKind, SensorReading> Latest { get; } = new();
    public double? Ndvi { get; set; }
    public DateTimeOffset? LastUpdate { get; set; }
    public List<SensorAlert> Alerts { get; } = [];

    public double? SoilMoisture => Latest.TryGetValue(SensorKind.SoilMoisture, out var r) ? r.Value : null;
    public double? AirTemp => Latest.TryGetValue(SensorKind.AirTemperature, out var r) ? r.Value : null;
    public double? Humidity => Latest.TryGetValue(SensorKind.RelativeHumidity, out var r) ? r.Value : null;
    public double? LeafWetness => Latest.TryGetValue(SensorKind.LeafWetness, out var r) ? r.Value : null;
}

public sealed record FieldSnapshot(Guid FieldId, string Name, string? Crop, double AreaHa, int? Dap, string? Stage, double? Progress,
    double? SoilMoisture, double? AirTemp, double? Humidity, double? Ndvi, int ActiveAlerts, string Health);

public sealed record FarmSnapshot(string Farm, DateTimeOffset Time, IReadOnlyList<FieldSnapshot> Fields, IReadOnlyList<Decision> Decisions, long ReadingsProcessed);

/// <summary>
/// Farm digital twin: mirrors each field's crop, soil and micro-climate state from IoT streams, advances crop phenology from
/// daily weather, and continuously produces explained decisions (irrigation, fertilisation, disease scouting, heat protection, harvest).
/// </summary>
public sealed class FarmTwin : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, FieldState> _fields = new();
    private readonly ConcurrentDictionary<string, Guid> _deviceToField = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Decision> _decisions = [];
    private readonly ISensorTransport? _transport;
    private readonly EdgeProcessor _edge;
    private readonly IAgriEventBus _bus;
    private readonly TimeProvider _clock;

    public FarmTwin(Farm farm, ISensorTransport? transport = null, IAgriEventBus? bus = null, TimeProvider? clock = null, CropCatalog? catalog = null)
    {
        Farm = farm;
        _transport = transport;
        _bus = bus ?? new InMemoryEventBus();
        _clock = clock ?? TimeProvider.System;
        _edge = new EdgeProcessor(_bus).AddDefaultAgronomicRules();
        _edge.Alert += OnAlert;
        _edge.Forward += OnReading;
        var cat = catalog ?? CropCatalog.Default;
        foreach (var f in farm.Fields)
            _fields[f.Id] = new FieldState(f, f.CropId is { } c && cat.TryGet(c, out var cd) ? cd : null, new SoilSample());
        if (_transport is not null) _transport.ReadingReceived += _edge.Process;
    }

    public Farm Farm { get; }
    public IAgriEventBus Events => _bus;
    public IReadOnlyList<FieldState> Fields => _fields.Values.ToList();
    public IReadOnlyList<Decision> Decisions { get { lock (_decisions) return _decisions.ToList(); } }
    public long ReadingsProcessed => _edge.Received;

    public Task StartAsync(CancellationToken ct = default) => _transport?.ConnectAsync(ct) ?? Task.CompletedTask;

    /// <summary>Binds a device (by id) to a field so its readings update that field's state.</summary>
    public FarmTwin BindDevice(string deviceId, Field field) { _deviceToField[deviceId] = field.Id; return this; }

    public FieldState State(Field field) => _fields[field.Id];

    /// <summary>Feeds a reading directly (bypassing the transport).</summary>
    public void Ingest(SensorReading r) => _edge.Process(r);

    public void SetSoil(Field field, SoilSample soil) => _fields[field.Id].Soil = soil;
    public void SetNdvi(Field field, double ndvi) => _fields[field.Id].Ndvi = ndvi;

    /// <summary>Advances phenology for all planted fields with a day of weather.</summary>
    public void AdvanceDay(DailyWeather day)
    {
        foreach (var s in _fields.Values) s.Lifecycle?.Advance(day.TminC, day.TmaxC);
    }

    private void OnReading(SensorReading r)
    {
        if (!_deviceToField.TryGetValue(r.DeviceId, out var fid) || !_fields.TryGetValue(fid, out var s)) return;
        s.Latest[r.Kind] = r;
        s.LastUpdate = r.Timestamp;
    }

    private void OnAlert(SensorAlert a)
    {
        var device = a.Key.Split('/')[0];
        if (_deviceToField.TryGetValue(device, out var fid) && _fields.TryGetValue(fid, out var s))
            lock (s.Alerts) { s.Alerts.Add(a); if (s.Alerts.Count > 200) s.Alerts.RemoveRange(0, 100); }
    }

    /// <summary>Evaluates every field and emits new decisions (deduplicated per field/type per day).</summary>
    public IReadOnlyList<Decision> Evaluate()
    {
        var now = _clock.GetUtcNow();
        var created = new List<Decision>();
        foreach (var s in _fields.Values)
        {
            void Add(DecisionType t, string title, string why, double conf)
            {
                lock (_decisions)
                {
                    if (_decisions.Any(d => d.FieldId == s.Field.Id && d.Type == t && (now - d.CreatedAt).TotalHours < 24)) return;
                    var d = new Decision(s.Field.Id, s.Field.Name, t, title, why, Math.Round(conf, 2), now);
                    _decisions.Add(d); created.Add(d); _bus.Publish(d);
                }
            }
            var crop = s.Crop;
            var hyd = s.Soil.Hydraulics();
            if (s.SoilMoisture is { } sm && crop is not null)
            {
                double vwc = sm / 100;
                double raw = hyd.FieldCapacity - crop.DepletionFraction * (hyd.FieldCapacity - hyd.WiltingPoint);
                if (vwc < raw)
                {
                    double deficitMm = (hyd.FieldCapacity - vwc) * crop.MaxRootDepthM * 1000;
                    Add(DecisionType.Irrigate, $"Irrigate {s.Field.Name}: ≈ {deficitMm:F0} mm ({Core.Units.MmToCubicMetresPerHa(deficitMm / 0.75, s.Field.AreaHa):F0} m³)",
                        $"Soil moisture {sm:F1}% VWC is below the stress threshold {raw * 100:F1}% for {crop.Name} (FC {hyd.FieldCapacity * 100:F1}%).", 0.85);
                }
                else if (vwc > hyd.FieldCapacity + 0.03)
                    Add(DecisionType.PauseIrrigation, $"Pause irrigation on {s.Field.Name}", $"Soil moisture {sm:F1}% exceeds field capacity — risk of waterlogging and N leaching.", 0.8);
            }
            if (s.Lifecycle is { } lc && crop is not null)
            {
                var adv = FertilizerAdvisor.Advise(crop, s.Soil, s.Field.AreaHa);
                foreach (var app in adv.Schedule.Where(a => a.IsDue(lc.DaysAfterPlanting)))
                    Add(DecisionType.Fertilize, $"{app.Stage} for {s.Field.Name}",
                        string.Join(", ", app.Products.Select(p => $"{p.Product.Name} {p.KgPerHa:F0} kg/ha ({p.TotalKg:F0} kg)")) + $" at {lc.DaysAfterPlanting} DAP.", 0.75);
                if (lc.IsMature) Add(DecisionType.Harvest, $"Harvest {s.Field.Name}", $"{crop.Name} reached physiological maturity ({lc.CumulativeGdd:F0} GDD).", 0.9);
                if (s.LeafWetness is { } lw && s.AirTemp is { } t)
                    foreach (var p in DiseaseRiskModel.ForCrop(crop))
                    {
                        double wetHours = lw / 100 * 24;
                        if (wetHours >= p.RequiredWetness(t))
                            Add(DecisionType.ScoutDisease, $"Scout {s.Field.Name} for {p.Name}",
                                $"Leaf wetness ≈ {wetHours:F0} h at {t:F1} °C meets infection requirement ({p.RequiredWetness(t):F1} h). {p.Management}", 0.7);
                    }
            }
            if (s.AirTemp is { } at && crop is not null && at >= crop.AbsoluteTempMaxC - 3)
                Add(DecisionType.ProtectFromHeat, $"Heat protection on {s.Field.Name}", $"Air temperature {at:F1} °C approaches the crop's limit ({crop.AbsoluteTempMaxC} °C); irrigate in the early morning, use shade nets if available.", 0.7);
            if (s.LastUpdate is { } lu && (now - lu).TotalHours > 6)
                Add(DecisionType.CheckDevice, $"Check sensors on {s.Field.Name}", $"No data for {(now - lu).TotalHours:F0} h.", 0.6);
        }
        return created;
    }

    public FarmSnapshot Snapshot() => new(Farm.Name, _clock.GetUtcNow(),
        _fields.Values.Select(s =>
        {
            int alerts; lock (s.Alerts) alerts = s.Alerts.Count(a => (_clock.GetUtcNow() - a.Timestamp).TotalHours < 24);
            string health = s.Ndvi is { } n ? n >= 0.6 ? "Healthy" : n >= 0.4 ? "Moderate" : "Stressed" : alerts > 0 ? "Attention" : "OK";
            return new FieldSnapshot(s.Field.Id, s.Field.Name, s.Crop?.Name, Math.Round(s.Field.AreaHa, 2), s.Lifecycle?.DaysAfterPlanting, s.Lifecycle?.Stage?.Name,
                s.Lifecycle is null ? null : Math.Round(s.Lifecycle.Progress, 3), s.SoilMoisture, s.AirTemp, s.Humidity, s.Ndvi, alerts, health);
        }).OrderBy(f => f.Name).ToList(),
        Decisions, ReadingsProcessed);

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null) { _transport.ReadingReceived -= _edge.Process; await _transport.DisposeAsync().ConfigureAwait(false); }
    }
}

public static class DigitalTwinBuilderExtensions
{
    public static AgritechBuilder AddDigitalTwin(this AgritechBuilder builder, Farm farm)
    {
        builder.TryAddModule("DigitalTwin");
        builder.Services.TryAddSingleton(sp => new FarmTwin(farm, sp.GetService(typeof(ISensorTransport)) as ISensorTransport, sp.GetService(typeof(IAgriEventBus)) as IAgriEventBus,
            sp.GetService(typeof(TimeProvider)) as TimeProvider, sp.GetService(typeof(CropCatalog)) as CropCatalog));
        return builder;
    }
}
