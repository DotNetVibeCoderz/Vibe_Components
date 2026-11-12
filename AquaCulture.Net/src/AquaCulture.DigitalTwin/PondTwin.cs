using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;
using AquaCulture.Disease;
using AquaCulture.Environment;
using AquaCulture.Feeding;
using AquaCulture.Forecasting;
using AquaCulture.Growth;
using AquaCulture.Hydrodynamic;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

namespace AquaCulture.DigitalTwin;

public enum DecisionKind { Aeration, Feeding, WaterExchange, Treatment, Harvest, Weather, Maintenance, Info }

/// <summary>A decision the twin proposes, with its reasoning and optional actuator command.</summary>
public sealed record TwinDecision(DateTime Timestamp, string PondId, DecisionKind Kind, AlertSeverity Severity, Text2 Title, Text2 Rationale,
    ActuatorCommand? Command = null) : IAquaEvent;

/// <summary>Snapshot of the digital pond.</summary>
public sealed record TwinState
{
    public required string PondId { get; init; }
    public DateTime Timestamp { get; init; }
    public int DayOfCulture { get; init; }
    public double AbwG { get; init; }
    public double Population { get; init; }
    public double BiomassKg { get; init; }
    public double DensityKgM3 { get; init; }
    public WaterSample Water { get; init; } = new();
    public double Wqi { get; init; }
    public WaterQualityClass WaterClass { get; init; }
    public double FeedPlannedTodayKg { get; init; }
    public double FeedGivenTodayKg { get; init; }
    public string? TopDisease { get; init; }
    public double TopDiseaseRisk { get; init; }
    public ImpactSeverity WeatherImpact { get; init; }
    public double? ForecastDawnDo { get; init; }
    public bool AeratorOn { get; init; }
    public IReadOnlyList<WaterAlert> ActiveAlerts { get; init; } = [];
    public int OnlineDevices { get; init; }
}

/// <summary>Result of a what-if scenario.</summary>
public sealed record WhatIfResult(string Scenario, GrowthTrajectory Baseline, GrowthTrajectory Scenario_, double BiomassDeltaKg, double FcrDelta, double SurvivalDeltaPct);

/// <summary>
/// Real-time digital pond: fuses sensor streams, culture records, growth (soft-sensor between samplings), water quality, feeding,
/// disease risk and weather into one state and proposes explained decisions. Thread-safe for concurrent ingestion.
/// </summary>
public sealed class PondTwin
{
    private readonly object _gate = new();
    private readonly Dictionary<WaterParameter, TimeSeries> _history = [];
    private readonly AlertEngine _alerts;
    private readonly IAquaEventBus? _bus;
    private readonly List<TwinDecision> _log = [];
    private WaterSample _water;
    private double _abw, _population;
    private DateOnly _lastGrowthDay;
    private readonly Dictionary<DateOnly, double> _feedGiven = [];
    private double? _lastFeederKg;

    public PondTwin(Pond pond, SpeciesProfile species, DateOnly stockingDate, double abwG, double population, IAquaEventBus? bus = null)
    {
        Pond = pond; Species = species; StockingDate = stockingDate;
        _abw = abwG; _population = population; _bus = bus;
        _alerts = AlertEngine.ForSpecies(species, bus);
        _water = new WaterSample { PondId = pond.Id };
        _lastGrowthDay = DateOnly.FromDateTime(DateTime.Today);
    }

    /// <summary>Creates a twin from an active culture cycle.</summary>
    public static PondTwin FromCycle(Pond pond, CultureCycle cycle, IAquaEventBus? bus = null) =>
        new(pond, cycle.Species, cycle.StockingDate, cycle.LatestAbwG, cycle.EstimatedPopulation, bus) { Cycle = cycle };

    public Pond Pond { get; }
    public SpeciesProfile Species { get; }
    public DateOnly StockingDate { get; }
    public CultureCycle? Cycle { get; init; }
    public IReadOnlyList<WeatherObservation>? WeatherForecast { get; set; }
    public bool AeratorOn { get; private set; }
    public string? AeratorDeviceId { get; set; }
    public string? FeederDeviceId { get; set; }
    public IReadOnlyList<TwinDecision> DecisionLog { get { lock (_gate) return _log.ToList(); } }
    public WaterSample Water { get { lock (_gate) return _water; } }
    public double AbwG { get { lock (_gate) return _abw; } }

    public TimeSeries History(WaterParameter p) { lock (_gate) return _history.TryGetValue(p, out var ts) ? new TimeSeries(ts.Points, ts.Name, ts.Unit) : new TimeSeries(); }

    private static WaterParameter? ToParameter(SensorKind k) => (int)k <= (int)SensorKind.Turbidity ? (WaterParameter)(int)k : null;

    /// <summary>Ingests one sensor reading.</summary>
    public void Ingest(SensorReading r)
    {
        IReadOnlyList<WaterAlert> changes = [];
        lock (_gate)
        {
            if (ToParameter(r.Kind) is { } p)
            {
                _water = _water.With(p, r.Value) with { Timestamp = r.Timestamp, PondId = Pond.Id };
                if (!_history.TryGetValue(p, out var ts)) _history[p] = ts = new TimeSeries(p.ToString(), WaterParameters.Info[p].Unit);
                ts.Add(r.Timestamp, r.Value);
                if (ts.Count > 5000) _history[p] = new TimeSeries(ts.Points.Skip(1000), ts.Name, ts.Unit);
                changes = _alerts.Evaluate(_water);
            }
            else if (r.Kind == SensorKind.FeederRemaining)
            {
                var day = DateOnly.FromDateTime(r.Timestamp);
                if (_lastFeederKg is { } prev && r.Value < prev) _feedGiven[day] = _feedGiven.GetValueOrDefault(day) + (prev - r.Value);
                _lastFeederKg = r.Value;
            }
            else if (r.Kind == SensorKind.AeratorCurrent) AeratorOn = r.Value > 0.5;
        }
        foreach (var a in changes.Where(a => !a.Cleared))
            Record(new TwinDecision(a.Timestamp, Pond.Id, a.Parameter == WaterParameter.DissolvedOxygen ? DecisionKind.Aeration : DecisionKind.Info, a.Severity,
                new($"Alert: {a.Message.En}", $"Peringatan: {a.Message.Id}"), new("Threshold crossed on live sensor data", "Ambang terlampaui pada data sensor langsung"),
                a.Parameter == WaterParameter.DissolvedOxygen && AeratorDeviceId is not null ? new ActuatorCommand(AeratorDeviceId, "aerator_on", 1, a.Timestamp) : null));
    }

    public void Ingest(IEnumerable<SensorReading> readings) { foreach (var r in readings) Ingest(r); }

    /// <summary>Updates the stock from a sampling (ground truth for the growth soft-sensor).</summary>
    public void ApplySampling(double abwG, double? population = null)
    {
        lock (_gate) { _abw = abwG; if (population is { } p) _population = p; }
    }

    /// <summary>Records feed given (manual feeding).</summary>
    public void RecordFeed(DateOnly day, double kg) { lock (_gate) _feedGiven[day] = _feedGiven.GetValueOrDefault(day) + kg; }

    /// <summary>Advances the growth soft-sensor to <paramref name="today"/> using the mean observed conditions.</summary>
    public void AdvanceGrowth(DateOnly today)
    {
        lock (_gate)
        {
            int days = today.DayNumber - _lastGrowthDay.DayNumber;
            if (days <= 0) return;
            var sim = new GrowthSimulator(Species);
            double t = Mean(WaterParameter.Temperature, 24) ?? _water.TemperatureC ?? 28;
            double dox = MinRecent(WaterParameter.DissolvedOxygen, 24) ?? _water.DissolvedOxygen ?? Species.DoOptimal;
            double nh3 = Nh3() ?? 0;
            var traj = sim.Simulate(new StockState(Species, _lastGrowthDay, _abw, _population, Pond.VolumeM3), days, new DailyEnvironment(t, dox, nh3, _water.SalinityPpt ?? double.NaN));
            _abw = traj.Final.AbwG; _population = traj.Final.Survivors; _lastGrowthDay = today;
        }
    }

    private double? Mean(WaterParameter p, double hours) =>
        _history.TryGetValue(p, out var ts) && ts.Count > 0 ? ts.Points.Where(x => (ts.Last!.Value.Time - x.Time).TotalHours <= hours).Average(x => x.Value) : null;

    private double? MinRecent(WaterParameter p, double hours) =>
        _history.TryGetValue(p, out var ts) && ts.Count > 0 ? ts.Points.Where(x => (ts.Last!.Value.Time - x.Time).TotalHours <= hours).Min(x => x.Value) : null;

    private double? Nh3() => _water.Ammonia is { } tan && _water.TemperatureC is { } t && _water.Ph is { } ph ? WaterChemistry.UnionizedAmmonia(tan, t, ph, _water.SalinityPpt ?? 0) : null;

    private PondConditions Conditions(DateTime now)
    {
        double? swing = _history.TryGetValue(WaterParameter.Temperature, out var ts) && ts.Count > 2
            ? ts.Points.Where(x => (now - x.Time).TotalHours <= 24).Select(x => x.Value).DefaultIfEmpty().Max() - ts.Points.Where(x => (now - x.Time).TotalHours <= 24).Select(x => x.Value).DefaultIfEmpty().Min() : null;
        return new PondConditions
        {
            TemperatureC = _water.TemperatureC, TemperatureSwing24h = swing, DawnDo = MinRecent(WaterParameter.DissolvedOxygen, 24) ?? _water.DissolvedOxygen,
            Nh3 = Nh3(), SalinityPpt = _water.SalinityPpt, DensityRatio = _abw * _population / 1000 / Pond.VolumeM3 / Species.MaxBiomassKgM3,
            OrpMv = _water.OrpMv, Bod = _water.Bod, DayOfCulture = DateOnly.FromDateTime(now).DayNumber - StockingDate.DayNumber,
        };
    }

    /// <summary>Current state of the digital pond.</summary>
    public TwinState State(DateTime now, int onlineDevices = 0)
    {
        lock (_gate)
        {
            var a = WaterQualityIndex.Assess(_water, Species);
            var plan = FeedingCalculator.Plan(Species, _abw, _population, _water.TemperatureC ?? 28, MinRecent(WaterParameter.DissolvedOxygen, 24), Nh3());
            var risk = DiagnosisEngine.RiskProfile(Species, Conditions(now)).FirstOrDefault();
            var wi = WeatherForecast is { Count: > 0 } w ? WeatherImpactPredictor.Predict(w.Where(x => x.Time >= now).Take(48).DefaultIfEmpty(w[^1]).ToList(),
                new PondExposure(Pond.DepthM, _water.SalinityPpt ?? 0, Pond.System, false, _water.TemperatureC ?? double.NaN)).Overall : ImpactSeverity.Low;
            double? dawn = null;
            if (_history.TryGetValue(WaterParameter.DissolvedOxygen, out var dts) && dts.Count >= 3)
                dawn = DoCrashForecaster.Forecast(dts, Species, OxygenParams(), WeatherForecast, now, Aeration).PredictedMin;
            return new TwinState
            {
                PondId = Pond.Id, Timestamp = now, DayOfCulture = DateOnly.FromDateTime(now).DayNumber - StockingDate.DayNumber,
                AbwG = Math.Round(_abw, 2), Population = Math.Round(_population), BiomassKg = Math.Round(_abw * _population / 1000, 1),
                DensityKgM3 = Math.Round(_abw * _population / 1000 / Pond.VolumeM3, 3), Water = _water, Wqi = a.Wqi, WaterClass = a.Class,
                FeedPlannedTodayKg = plan.DailyKg, FeedGivenTodayKg = Math.Round(_feedGiven.GetValueOrDefault(DateOnly.FromDateTime(now)), 2),
                TopDisease = risk?.Disease.Id, TopDiseaseRisk = risk?.Risk ?? 0, WeatherImpact = wi, ForecastDawnDo = dawn, AeratorOn = AeratorOn,
                ActiveAlerts = _alerts.Active.Where(x => x.PondId == Pond.Id).ToList(), OnlineDevices = onlineDevices,
            };
        }
    }

    /// <summary>Planned aeration for forecasts: installed aerators run whenever DO drops below the species optimum.</summary>
    private Func<DateTime, double, bool>? Aeration => Pond.AerationKw > 0 ? PondOxygenBudget.SetPoint(Species.DoOptimal) : null;

    private OxygenBudgetParameters OxygenParams() => new()
    {
        VolumeM3 = Pond.VolumeM3, DepthM = Pond.DepthM, BiomassKg = _abw * _population / 1000, MeanWeightG = _abw, Group = Species.Group,
        SalinityPpt = _water.SalinityPpt ?? 0, AeratorKw = Pond.AerationKw,
    };

    /// <summary>Evaluates the twin and returns explained decisions (also published on the bus and logged).</summary>
    public IReadOnlyList<TwinDecision> Evaluate(DateTime now)
    {
        var list = new List<TwinDecision>();
        WaterSample water; double abw, pop;
        lock (_gate) { water = _water; abw = _abw; pop = _population; }
        var s = Species;

        // 1. water quality → prioritised actions
        var a = WaterQualityIndex.Assess(water, s);
        foreach (var r in RecommendationEngine.Recommend(a, new PondContext(Pond.VolumeM3, Pond.AreaM2, abw * pop / 1000, Pond.AerationKw, Pond.System)).Where(r => r.Priority <= 2))
        {
            var kind = r.Category switch
            {
                RecommendationCategory.Aeration => DecisionKind.Aeration, RecommendationCategory.Feeding => DecisionKind.Feeding,
                RecommendationCategory.WaterExchange => DecisionKind.WaterExchange, RecommendationCategory.Chemical or RecommendationCategory.Biological => DecisionKind.Treatment,
                RecommendationCategory.Sludge => DecisionKind.Maintenance, _ => DecisionKind.Info,
            };
            var cmd = kind == DecisionKind.Aeration && AeratorDeviceId is not null && !AeratorOn ? new ActuatorCommand(AeratorDeviceId, "aerator_on", 1, now) : null;
            list.Add(new TwinDecision(now, Pond.Id, kind, r.Priority == 1 ? AlertSeverity.Critical : AlertSeverity.Warning, r.Action, r.Rationale, cmd));
        }

        // 2. overnight DO forecast
        var doSeries = History(WaterParameter.DissolvedOxygen);
        if (doSeries.Count >= 3 && (now.Hour >= 17 || now.Hour < 5))
        {
            var f = DoCrashForecaster.Forecast(doSeries, s, OxygenParams(), WeatherForecast, now, Aeration);
            if (f.ProbabilityBelowCritical > 0.15)
                list.Add(new TwinDecision(now, Pond.Id, DecisionKind.Aeration, f.ProbabilityBelowCritical > 0.5 ? AlertSeverity.Critical : AlertSeverity.Warning,
                    new($"Dawn DO forecast {f.PredictedMin:0.0} mg/L", $"Prakiraan DO subuh {f.PredictedMin:0.0} mg/L"), f.Advice,
                    AeratorDeviceId is not null && !AeratorOn ? new ActuatorCommand(AeratorDeviceId, "aerator_on", 1, now) : null));
        }

        // 3. feeding plan for the next meal
        var plan = FeedingCalculator.Plan(s, abw, pop, water.TemperatureC ?? 28, water.DissolvedOxygen, a.UnionizedAmmonia);
        var next = plan.Meals.FirstOrDefault(m => m.Time.ToTimeSpan() > now.TimeOfDay) ?? plan.Meals[0];
        list.Add(new TwinDecision(now, Pond.Id, DecisionKind.Feeding, AlertSeverity.Info,
            new($"Next meal {next.Time:HH\\:mm}: {next.Kg:0.0} kg ({plan.DailyKg:0.0} kg/day)", $"Pakan berikutnya {next.Time:HH\\:mm}: {next.Kg:0.0} kg ({plan.DailyKg:0.0} kg/hari)"),
            plan.Adjustments.Count > 0 ? new(string.Join("; ", plan.Adjustments.Select(x => x.En)), string.Join("; ", plan.Adjustments.Select(x => x.Id)))
                                       : new($"{plan.RatePercentBw:0.00} % of {abw * pop / 1000:N0} kg biomass", $"{plan.RatePercentBw:0.00} % dari biomassa {abw * pop / 1000:N0} kg"),
            FeederDeviceId is not null ? new ActuatorCommand(FeederDeviceId, "feed", Math.Round(next.Kg * 1000), now) : null));

        // 4. disease risk
        PondConditions cond; lock (_gate) cond = Conditions(now);
        var top = DiagnosisEngine.RiskProfile(s, cond).FirstOrDefault();
        if (top is { Risk: > 0.35 })
            list.Add(new TwinDecision(now, Pond.Id, DecisionKind.Treatment, top.Risk > 0.6 ? AlertSeverity.Warning : AlertSeverity.Info,
                new($"Elevated {top.Disease.Name.En} risk ({top.Risk:P0})", $"Risiko {top.Disease.Name.Id} meningkat ({top.Risk:P0})"), top.Disease.Prevention));

        // 5. weather
        if (WeatherForecast is { Count: > 0 } w)
        {
            var window = w.Where(x => x.Time >= now).Take(48).ToList();
            if (window.Count > 0)
                foreach (var i in WeatherImpactPredictor.Predict(window, new PondExposure(Pond.DepthM, water.SalinityPpt ?? 0, Pond.System, false, water.TemperatureC ?? double.NaN)).Impacts.Where(i => i.Severity >= ImpactSeverity.Moderate))
                    list.Add(new TwinDecision(now, Pond.Id, DecisionKind.Weather, i.Severity >= ImpactSeverity.High ? AlertSeverity.Warning : AlertSeverity.Info, i.Effect, i.Advice));
        }

        // 6. harvest readiness
        if (abw >= s.MarketWeightG * 0.95)
            list.Add(new TwinDecision(now, Pond.Id, DecisionKind.Harvest, AlertSeverity.Info,
                new($"Market size reached ({abw:0.#} g): plan harvest", $"Ukuran pasar tercapai ({abw:0.#} g): rencanakan panen"),
                new($"Standing biomass ≈ {abw * pop / 1000:N0} kg", $"Biomassa ≈ {abw * pop / 1000:N0} kg")));

        foreach (var d in list) Record(d);
        return list;
    }

    private void Record(TwinDecision d)
    {
        lock (_gate) { _log.Add(d); if (_log.Count > 2000) _log.RemoveRange(0, 500); }
        _bus?.Publish(d);
    }

    /// <summary>What-if: compares the baseline with modified conditions for <paramref name="days"/> days.</summary>
    public WhatIfResult WhatIf(string scenario, int days, Func<DailyEnvironment, DailyEnvironment> modify)
    {
        double abw, pop; WaterSample w;
        lock (_gate) { abw = _abw; pop = _population; w = _water; }
        var sim = new GrowthSimulator(Species);
        var start = new StockState(Species, DateOnly.FromDateTime(DateTime.Today), abw, pop, Pond.VolumeM3);
        var baseEnv = new DailyEnvironment(w.TemperatureC ?? 28, w.DissolvedOxygen ?? Species.DoOptimal, Nh3() ?? 0, w.SalinityPpt ?? double.NaN);
        var b = sim.Simulate(start, days, baseEnv);
        var sc = sim.Simulate(start, days, modify(baseEnv));
        return new WhatIfResult(scenario, b, sc, Math.Round(sc.Final.BiomassKg - b.Final.BiomassKg, 1), Math.Round(sc.Final.Fcr - b.Final.Fcr, 3),
            Math.Round(100 * (sc.SurvivalRate - b.SurvivalRate), 2));
    }
}
