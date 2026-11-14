using System.Globalization;
using System.Numerics;
using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.StateEstimation;

namespace Electrical.DigitalTwin;

/// <summary>
/// Live digital twin of a <see cref="PowerNetwork"/>. Each cycle it takes the measurements ingested since the previous cycle (directly or
/// mapped from SCADA tags), runs WLS state estimation with largest-normalized-residual bad-data removal, derives branch flows, the system
/// load level and KPIs from the estimated voltages, compares the load with a forecast, integrates asset thermal ageing, explains limit
/// violations and publishes <see cref="TwinStateUpdated"/>, <see cref="TwinAlarmRaised"/>, <see cref="TwinAlarmCleared"/> and
/// <see cref="TwinBadMeasurement"/> on the <see cref="IGridEventBus"/>. Thread-safe.
/// </summary>
/// <example><code>
/// var twin = new GridTwin(TestCases.Case14(), bus: eventBus);
/// twin.IngestTags(tagDatabase);          // RTU tags BUS4.VM, BR3.PF, ...
/// TwinState s = twin.Update(DateTimeOffset.UtcNow);
/// foreach (var a in s.Alarms) Console.WriteLine(a.Message);
/// var w = twin.WhatIf(new OpenBranch(6), new ScaleLoad(1.1));
/// </code></example>
public sealed class GridTwin
{
    private readonly object _gate = new();
    private readonly PowerNetwork _model;
    private readonly bool[] _normalInService;
    private readonly IGridEventBus? _bus;
    private readonly TimeProvider _time;
    private readonly List<Measurement> _pending = [];
    private readonly List<TwinState> _history = [];
    private readonly AssetHealthTracker _health;
    private readonly double[] _modelLoadP, _modelLoadQ;
    private readonly bool[] _hasGen;
    private HashSet<string> _activeAlarms = [];
    private int _step;
    private DateTimeOffset? _lastTimestamp;
    private TimeSeries? _forecast;

    /// <param name="model">Network model (cloned; unrated branches are rated from the base case when <see cref="GridTwinOptions.AutoRateUnratedBranches"/>).</param>
    public GridTwin(PowerNetwork model, GridTwinOptions? options = null, IGridEventBus? bus = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Options = options ?? GridTwinOptions.Default;
        _model = model.Clone();
        if (Options.AutoRateUnratedBranches) BranchRatings.AssignMissing(_model, Options.AutoRatingMargin);
        _normalInService = _model.Branches.Select(b => b.InService).ToArray();
        _bus = bus;
        _time = time ?? TimeProvider.System;
        _health = new AssetHealthTracker(Options.Transformer);
        int n = _model.BusCount;
        _modelLoadP = new double[n]; _modelLoadQ = new double[n]; _hasGen = new bool[n];
        foreach (var l in _model.Loads.Where(l => l.InService)) { int i = _model.IndexOf(l.Bus); _modelLoadP[i] += l.P; _modelLoadQ[i] += l.Q; }
        foreach (var g in _model.Generators.Where(g => g.InService)) _hasGen[_model.IndexOf(g.Bus)] = true;
    }

    public GridTwinOptions Options { get; }

    /// <summary>The twin's network model (current topology, model loads). Do not modify while the twin is running.</summary>
    public PowerNetwork Model => _model;

    /// <summary>Latest estimated state (null before the first <see cref="Update"/>).</summary>
    public TwinState? Current { get { lock (_gate) return _history.Count == 0 ? null : _history[^1]; } }

    /// <summary>Estimated states, oldest first (bounded by <see cref="GridTwinOptions.HistoryCapacity"/>).</summary>
    public IReadOnlyList<TwinState> History { get { lock (_gate) return _history.ToArray(); } }

    /// <summary>Thermal ageing / health of every rated branch.</summary>
    public IReadOnlyList<AssetHealth> AssetHealth { get { lock (_gate) return _health.Assets; } }

    /// <summary>Number of measurements waiting for the next <see cref="Update"/>.</summary>
    public int PendingMeasurements { get { lock (_gate) return _pending.Count; } }

    /// <summary>System-load forecast [MW] used for deviation alarms.</summary>
    public TimeSeries? Forecast { get { lock (_gate) return _forecast; } }

    /// <summary>Raised after each update (in addition to the event bus).</summary>
    public event Action<TwinState>? Updated;

    /// <summary>Sets the system-load forecast [MW] compared with the estimated load each cycle.</summary>
    public void SetForecast(TimeSeries? forecastMw) { lock (_gate) _forecast = forecastMw; }

    public void Ingest(Measurement measurement) { lock (_gate) _pending.Add(measurement); }

    public void Ingest(IEnumerable<Measurement> measurements) { lock (_gate) _pending.AddRange(measurements); }

    /// <summary>
    /// Maps the Good-quality tags of a SCADA database to measurements (see <see cref="TagMeasurementMapper"/>), applies breaker statuses
    /// (BRk.CLOSED) to the model topology and queues the measurements. Returns the number of measurements queued.
    /// </summary>
    public int IngestTags(TagDatabase tags, string prefix = "")
    {
        var status = new Dictionary<int, bool>();
        lock (_gate)
        {
            var ms = TagMeasurementMapper.ToMeasurements(tags, _model, Options.Sigmas, prefix, status);
            foreach (var (k, closed) in status) _model.Branches[k].InService = closed;
            _pending.AddRange(ms);
            return ms.Count;
        }
    }

    /// <summary>Opens/closes a branch in the twin model (e.g. from a switching order).</summary>
    public void SetBranchStatus(int branchIndex, bool closed) { lock (_gate) _model.Branches[branchIndex].InService = closed; }

    /// <summary>Branches out of service compared with the topology the twin was created with.</summary>
    public IReadOnlyList<int> OpenBranches()
    {
        lock (_gate) return Enumerable.Range(0, _model.Branches.Count).Where(k => _normalInService[k] && !_model.Branches[k].InService).ToList();
    }

    /// <summary>Runs one estimation cycle on the queued measurements (which are then cleared).</summary>
    public TwinState Update(DateTimeOffset? timestamp = null)
    {
        TwinState state;
        List<IGridEvent> events = [];
        lock (_gate)
        {
            var ts = timestamp ?? _time.GetUtcNow();
            var meas = _pending.ToList();
            _pending.Clear();
            state = Estimate(meas, ts, events);
            _history.Add(state);
            if (_history.Count > Options.HistoryCapacity) _history.RemoveRange(0, _history.Count - Options.HistoryCapacity);
            _lastTimestamp = ts;
        }
        foreach (var e in events) PublishEvent(e);
        _bus?.Publish(new TwinStateUpdated(state, state.Timestamp));
        Updated?.Invoke(state);
        return state;
    }

    private void PublishEvent(IGridEvent e)
    {
        if (_bus is null) return;
        switch (e)
        {
            case TwinAlarmRaised r: _bus.Publish(r); break;
            case TwinAlarmCleared c: _bus.Publish(c); break;
            case TwinBadMeasurement b: _bus.Publish(b); break;
        }
    }

    private TwinState Estimate(List<Measurement> meas, DateTimeOffset ts, List<IGridEvent> events)
    {
        int step = _step++;
        int n = _model.BusCount;
        var ids = _model.Buses.Select(b => b.Id).ToArray();
        BadDataResult? bd = null;
        string? failure = null;
        if (meas.Count == 0) failure = "no measurements";
        else
        {
            try
            {
                bd = BadDataDetector.IdentifyAndRemove(_model, meas, Options.BadDataThreshold, Options.MaxBadDataRemovals, Options.Estimation);
                if (!bd.Final.Observable) failure = "network not observable with the available measurements";
                else if (!bd.Final.Converged) failure = "state estimator did not converge";
            }
            catch (Exception e) when (e is InvalidOperationException or ArithmeticException or ArgumentException or KeyNotFoundException)
            {
                failure = e.Message;
            }
        }

        if (failure is not null || bd is null)
        {
            var fa = new ExplainedAlarm(TwinAlarmKind.EstimatorFailure, "state estimator", meas.Count, 0, TwinAlarmSeverity.High,
                $"State estimation failed at cycle {step}: {failure}.", $"Estimasi keadaan gagal pada siklus {step}: {failure}.", ["estimator-failure"]);
            var alarmsF = new List<ExplainedAlarm> { fa };
            DiffAlarms(alarmsF, ts, events);
            return new TwinState
            {
                Step = step, Timestamp = ts, Converged = false, Observable = bd?.Final.Observable ?? false, Iterations = bd?.Final.Iterations ?? 0,
                ObjectiveJ = bd?.Final.ObjectiveJ ?? double.NaN, BusIds = ids, Vm = new double[n], VaDeg = new double[n], Buses = [], Branches = [],
                EstimatedLoadMw = double.NaN, LoadScale = double.NaN, MeasurementCount = meas.Count, RemovedMeasurements = bd?.Removed ?? [],
                Alarms = alarmsF, Kpis = GridKpis.Failed,
            };
        }

        var res = bd.Final;
        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = Complex.FromPolarCoordinates(res.Vm[i], res.VaDeg[i] * Scalar.Deg2Rad);
        double baseMva = _model.BaseMva;
        var sInj = AdmittanceMatrix.Build(_model).Injections(v);

        // load level from buses without generation: −ΣP_inj / ΣP_model
        double pMeas = 0, pModel = 0;
        for (int i = 0; i < n; i++)
            if (!_hasGen[i] && _modelLoadP[i] > 0) { pMeas += -sInj[i].Real * baseMva; pModel += _modelLoadP[i]; }
        double scale = pModel > 0 ? Math.Clamp(pMeas / pModel, 0, 10) : 1;

        var buses = new BusResult[n];
        for (int i = 0; i < n; i++)
        {
            var b = _model.Buses[i];
            double pl = _modelLoadP[i] * scale, ql = _modelLoadQ[i] * scale;
            var s = sInj[i] * baseMva;
            double pg = _hasGen[i] ? s.Real + pl : 0, qg = _hasGen[i] ? s.Imaginary + ql : 0;
            buses[i] = new BusResult(b.Id, b.Name, res.Vm[i], res.VaDeg[i], pg, qg, _hasGen[i] ? pl : -s.Real, _hasGen[i] ? ql : -s.Imaginary, b.Type);
        }
        var branches = new BranchResult[_model.Branches.Count];
        for (int k = 0; k < _model.Branches.Count; k++)
        {
            var br = _model.Branches[k];
            if (!br.InService) { branches[k] = new BranchResult(k, br.From, br.To, br.Name, 0, 0, 0, 0, br.RateA); continue; }
            int f = _model.IndexOf(br.From), t = _model.IndexOf(br.To);
            var a = BranchAdmittance.Of(br);
            var sF = v[f] * Complex.Conjugate(a.Yff * v[f] + a.Yft * v[t]) * baseMva;
            var sT = v[t] * Complex.Conjugate(a.Ytf * v[f] + a.Ytt * v[t]) * baseMva;
            branches[k] = new BranchResult(k, br.From, br.To, br.Name, sF.Real, sF.Imaginary, sT.Real, sT.Imaginary, br.RateA);
        }

        double estLoad = _modelLoadP.Sum() * scale;
        double? fc = ForecastAt(ts);
        double? dev = fc is { } f0 && f0 > 1e-9 ? 100 * (estLoad - f0) / f0 : null;
        var open = Enumerable.Range(0, _model.Branches.Count).Where(k => _normalInService[k] && !_model.Branches[k].InService).ToList();
        var alarms = AlarmExplainer.Explain(_model, buses, branches, Options, new AlarmContext(open, dev, scale)).ToList();
        foreach (var (m, r) in bd.Removed)
        {
            string what = Describe(m);
            alarms.Add(new ExplainedAlarm(TwinAlarmKind.BadData, what, r, Options.BadDataThreshold, TwinAlarmSeverity.Warning,
                $"Measurement {what} = {m.Value.ToString("G5", CultureInfo.InvariantCulture)} rejected as bad data (normalized residual {r.ToString("F1", CultureInfo.InvariantCulture)} > {Options.BadDataThreshold.ToString("F1", CultureInfo.InvariantCulture)}); check the transducer or RTU channel",
                $"Pengukuran {what} = {m.Value.ToString("G5", CultureInfo.InvariantCulture)} ditolak sebagai data buruk (residu ternormalisasi {r.ToString("F1", CultureInfo.InvariantCulture)} > {Options.BadDataThreshold.ToString("F1", CultureInfo.InvariantCulture)}); periksa transduser atau kanal RTU",
                ["bad-data"]));
            events.Add(new TwinBadMeasurement(m, r, ts));
        }
        double hours = _lastTimestamp is { } last && ts > last ? (ts - last).TotalHours : 0;
        _health.Update(_model, branches, hours, Options.AmbientTemperatureC);
        DiffAlarms(alarms, ts, events);

        return new TwinState
        {
            Step = step, Timestamp = ts, Converged = true, Observable = true, Iterations = res.Iterations, ObjectiveJ = res.ObjectiveJ,
            BusIds = ids, Vm = res.Vm, VaDeg = res.VaDeg, Buses = buses, Branches = branches, EstimatedLoadMw = estLoad, LoadScale = scale,
            ForecastLoadMw = fc, ForecastDeviationPercent = dev, MeasurementCount = meas.Count, RemovedMeasurements = bd.Removed, Alarms = alarms,
            Kpis = GridKpis.From(buses, branches, true, Options.VoltageLowPu, Options.VoltageHighPu, Options.OverloadPercent),
        };
    }

    private void DiffAlarms(List<ExplainedAlarm> alarms, DateTimeOffset ts, List<IGridEvent> events)
    {
        var now = alarms.Select(a => a.Key).ToHashSet();
        foreach (var a in alarms) if (!_activeAlarms.Contains(a.Key)) events.Add(new TwinAlarmRaised(a, ts));
        var prev = _history.Count > 0 ? _history[^1].Alarms : [];
        foreach (var a in prev) if (!now.Contains(a.Key)) events.Add(new TwinAlarmCleared(a, ts));
        _activeAlarms = now;
    }

    private double? ForecastAt(DateTimeOffset ts)
    {
        if (_forecast is not { Count: > 0 } f) return null;
        double x = (ts - f.Start) / f.Step;
        int i = (int)Math.Round(x);
        return i >= 0 && i < f.Count ? f[i] : null;
    }

    private string Describe(Measurement m)
    {
        if (!m.IsFlow) return $"{Short(m.Type)}@bus {m.Location}";
        var br = _model.Branches[m.Location];
        return $"{Short(m.Type)}@branch {br.From}-{br.To} ({m.End.ToString().ToLowerInvariant()} end)";
    }

    private static string Short(MeasurementType t) => t switch
    {
        MeasurementType.VoltageMagnitude => "V",
        MeasurementType.VoltageAngle => "θ",
        MeasurementType.ActiveInjection => "P_inj",
        MeasurementType.ReactiveInjection => "Q_inj",
        MeasurementType.ActiveFlow => "P_flow",
        _ => "Q_flow",
    };

    /// <summary>
    /// Snapshot of the network at the current estimated state: model loads scaled by the estimated load level and voltages from the estimate
    /// (warm start). Use it as the base case for studies.
    /// </summary>
    public PowerNetwork OperatingSnapshot()
    {
        lock (_gate)
        {
            var net = _model.Clone();
            var cur = _history.Count == 0 ? null : _history[^1];
            if (cur is { Converged: true })
            {
                foreach (var l in net.Loads) { l.P *= cur.LoadScale; l.Q *= cur.LoadScale; }
                for (int i = 0; i < net.BusCount; i++) { net.Buses[i].Vm = cur.Vm[i]; net.Buses[i].VaDeg = cur.VaDeg[i]; }
            }
            return net;
        }
    }

    /// <summary>What-if study on <see cref="OperatingSnapshot"/>: clone, apply the actions, solve and compare KPIs.</summary>
    public WhatIfResult WhatIf(params WhatIfAction[] actions)
    {
        var snap = OperatingSnapshot();
        var cur = Current;
        // outages relative to the snapshot are attributed by the simulator; keep forecast deviation and load level as context
        return WhatIfSimulator.Run(snap, actions, Options, new AlarmContext(null, cur?.ForecastDeviationPercent, cur?.LoadScale is { } s && !double.IsNaN(s) ? s : 1));
    }

    /// <summary>Estimated system load [MW] of the converged states as a regular time series (step taken from the first two states).</summary>
    public TimeSeries EstimatedLoadSeries()
    {
        var h = History.Where(s => s.Converged).ToList();
        if (h.Count == 0) return new TimeSeries(DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1), [], "Estimated load", "MW");
        var step = h.Count > 1 ? h[1].Timestamp - h[0].Timestamp : TimeSpan.FromHours(1);
        if (step <= TimeSpan.Zero) step = TimeSpan.FromHours(1);
        return new TimeSeries(h[0].Timestamp, step, h.Select(s => s.EstimatedLoadMw).ToArray(), "Estimated load", "MW");
    }

    /// <summary>Forecasts the next <paramref name="horizon"/> samples of the estimated system load (needs ≥ 2 days of history for Holt-Winters).</summary>
    public TimeSeries ForecastLoad(int horizon, LoadForecastMethod method = LoadForecastMethod.HoltWinters) =>
        LoadForecast.Forecast(EstimatedLoadSeries(), horizon, method);

    /// <summary>Bilingual one-paragraph status summary of the latest state.</summary>
    public string Summary(string language = "en")
    {
        var s = Current;
        bool id = language == "id";
        if (s is null) return id ? "Digital twin belum menerima pengukuran." : "The digital twin has not received measurements yet.";
        var inv = CultureInfo.InvariantCulture;
        string fc = s.ForecastDeviationPercent is { } d
            ? (id ? $", deviasi terhadap prakiraan {d.ToString("+0.0;-0.0", inv)}%" : $", deviation from forecast {d.ToString("+0.0;-0.0", inv)}%")
            : "";
        var worst = AssetHealth.OrderBy(a => a.HealthIndex).FirstOrDefault();
        string asset = worst is null ? "" : id
            ? $" Aset paling tertekan: {worst.Name} ({worst.LoadingPercent.ToString("0", inv)}% beban, F_AA {worst.AgingFactor.ToString("0.00", inv)}, kesehatan {worst.HealthIndex.ToString("0", inv)} {worst.StatusId})."
            : $" Most stressed asset: {worst.Name} ({worst.LoadingPercent.ToString("0", inv)}% load, F_AA {worst.AgingFactor.ToString("0.00", inv)}, health {worst.HealthIndex.ToString("0", inv)} {worst.Status}).";
        if (!s.Converged)
            return (id ? $"Siklus {s.Step}: estimasi gagal. " : $"Cycle {s.Step}: estimation failed. ") + string.Join(" ", s.Alarms.Select(a => a.Localized(language)));
        string head = id
            ? $"Siklus {s.Step} ({s.Timestamp:yyyy-MM-dd HH:mm}): estimasi konvergen, {s.MeasurementCount} pengukuran, {s.RemovedMeasurements.Count} data buruk dibuang, beban sistem {s.EstimatedLoadMw.ToString("0.##", inv)} MW{fc}, rugi-rugi {s.Kpis.LossesMw.ToString("0.###", inv)} MW, Vmin {s.Kpis.MinVoltagePu.ToString("0.000", inv)} pu (bus {s.Kpis.MinVoltageBus})."
            : $"Cycle {s.Step} ({s.Timestamp:yyyy-MM-dd HH:mm}): estimate converged, {s.MeasurementCount} measurements, {s.RemovedMeasurements.Count} bad data removed, system load {s.EstimatedLoadMw.ToString("0.##", inv)} MW{fc}, losses {s.Kpis.LossesMw.ToString("0.###", inv)} MW, Vmin {s.Kpis.MinVoltagePu.ToString("0.000", inv)} pu (bus {s.Kpis.MinVoltageBus}).";
        string alarms = s.Alarms.Count == 0
            ? (id ? " Tidak ada alarm." : " No alarms.")
            : (id ? $" {s.Alarms.Count} alarm: " : $" {s.Alarms.Count} alarm(s): ") + string.Join("; ", s.Alarms.Take(8).Select(a => a.Localized(language))) + ".";
        return head + alarms + asset;
    }
}
