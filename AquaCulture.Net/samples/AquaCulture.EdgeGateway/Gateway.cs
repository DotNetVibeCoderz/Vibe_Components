using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Forecasting;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

namespace AquaCulture.EdgeGateway;

/// <summary>Settings the cloud can push (<c>PUT /t/{tenant}/sync/{gateway}/config</c>); the gateway keeps the last copy on disk.</summary>
public sealed record GatewaySettings(double DoSetPoint = 4.5, double DoHysteresis = 1.0, string Species = "vaname")
{
    public GatewaySettings Apply(GatewayConfig cfg) => this with
    {
        DoSetPoint = cfg.Number("doSetPoint") ?? DoSetPoint,
        DoHysteresis = cfg.Number("doHysteresis") ?? DoHysteresis,
        Species = cfg.Values["species"]?.GetValue<string>() ?? Species,
    };
}

/// <summary>What a tick did, for logs and the self-test.</summary>
public sealed record TickReport(int Readings, int Rejected, IReadOnlyList<WaterAlert> Alerts, IReadOnlyList<ActuatorCommand> Commands);

/// <summary>
/// The gateway's local brain. Every tick: read sensors → edge filter (outliers, stuck probes, calibration) → outbox →
/// water-quality alerts → aerator control (DO set point with hysteresis, switched on early when the dawn-DO forecast
/// falls below the species' critical DO). Works with no network; the cloud only receives the outbox and pushes settings.
/// </summary>
public sealed class GatewayCore(ISensorTransport transport, EdgeOutbox outbox, IReadOnlyDictionary<string, string> aeratorByPond)
{
    private readonly EdgeAnalytics _edge = new();
    private readonly Dictionary<string, AlertEngine> _alerts = [];
    private readonly Dictionary<string, TimeSeries> _do = [];
    private readonly Dictionary<string, Dictionary<SensorKind, double>> _latest = [];
    private readonly Dictionary<string, bool> _aeratorOn = [];

    public GatewaySettings Settings { get; set; } = new();
    public SpeciesProfile Species => SpeciesCatalog.Get(Settings.Species);
    public IReadOnlyDictionary<string, bool> AeratorState => _aeratorOn;

    public async Task<TickReport> TickAsync(IReadOnlyList<SensorReading> raw, DateTime now, CancellationToken ct = default)
    {
        var accepted = new List<SensorReading>(raw.Count);
        int rejected = 0;
        foreach (var r in raw)
        {
            var v = _edge.Process(r);
            if (v.IsOutlier || v.IsStuck) { rejected++; accepted.Add(r with { Quality = 0 }); continue; }   // kept for audit, marked bad
            var clean = v.Corrected is { } c ? r with { Value = c } : r;
            accepted.Add(clean);
            (_latest.TryGetValue(r.PondId, out var l) ? l : _latest[r.PondId] = [])[r.Kind] = clean.Value;
            if (r.Kind == SensorKind.DissolvedOxygen)
            {
                var ts = _do.TryGetValue(r.PondId, out var s) ? s : _do[r.PondId] = new TimeSeries("DO", "mg/L");
                ts.Add(r.Timestamp, clean.Value);
                if (ts.Count > 400) _do[r.PondId] = ts.Between(now.AddHours(-6), now);   // keep the last 6 hours
            }
        }
        outbox.Append(accepted);

        var alerts = new List<WaterAlert>();
        var commands = new List<ActuatorCommand>();
        foreach (var (pond, latest) in _latest)
        {
            double? V(SensorKind k) => latest.TryGetValue(k, out var x) ? x : null;
            var sample = new WaterSample
            {
                Timestamp = now, PondId = pond, TemperatureC = V(SensorKind.Temperature), DissolvedOxygen = V(SensorKind.DissolvedOxygen), Ph = V(SensorKind.Ph),
                SalinityPpt = V(SensorKind.Salinity), Ammonia = V(SensorKind.Ammonia), OrpMv = V(SensorKind.Orp), TurbidityNtu = V(SensorKind.Turbidity),
            };
            var engine = _alerts.TryGetValue(pond, out var e) ? e : _alerts[pond] = AlertEngine.ForSpecies(Species);
            alerts.AddRange(engine.Evaluate(sample));

            if (!aeratorByPond.TryGetValue(pond, out var aerator) || sample.DissolvedOxygen is not { } dox) continue;
            bool on = _aeratorOn.GetValueOrDefault(pond);
            bool forecastLow = false;
            if (!on && _do.TryGetValue(pond, out var series) && series.Count >= 3 && (now.Hour >= 18 || now.Hour < 6))
                forecastLow = DoCrashForecaster.Forecast(series, Species, now: now).PredictedMin < Species.DoCritical + 0.5;
            bool want = on ? dox < Settings.DoSetPoint + Settings.DoHysteresis : dox < Settings.DoSetPoint || forecastLow;
            if (want == on) continue;
            _aeratorOn[pond] = want;
            var cmd = new ActuatorCommand(aerator, want ? "aerator_on" : "aerator_off", want ? 1 : 0, now);
            commands.Add(cmd);
            await transport.SendCommandAsync(cmd, ct).ConfigureAwait(false);
        }
        return new TickReport(accepted.Count, rejected, alerts, commands);
    }
}
