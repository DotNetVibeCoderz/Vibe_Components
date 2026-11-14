using System.Collections.Concurrent;
using Medical.Clinical;
using Medical.Core;

namespace Medical.Hospital.Monitoring;

/// <summary>A parameter reported by a bedside device.</summary>
public enum VitalParameter { RespiratoryRate, SpO2, SystolicBp, DiastolicBp, HeartRate, TemperatureC, OnOxygen, Consciousness }

/// <summary>One value from a device (monitor, pulse oximeter, infusion pump…).</summary>
public sealed record DeviceReading(string DeviceId, VitalParameter Parameter, double Value, DateTimeOffset Time);

/// <summary>A source of device readings (HL7 / MLLP monitors, MQTT / SenML sensors, simulators — see Medical.Devices).</summary>
public interface IDeviceFeed
{
    string Name { get; }
    IAsyncEnumerable<DeviceReading> ReadAllAsync(CancellationToken ct = default);
}

/// <summary>NEWS2 clinical response (RCP 2017): low 0–4, low-medium for a single parameter scoring 3, medium 5–6, high ≥ 7.</summary>
public enum EscalationLevel { None, Low, LowMedium, Medium, High }

/// <summary>Live state of one bed.</summary>
public sealed record BedSnapshot(string DeviceId, string? BedCode, string? PatientId, string? PatientName, string? VisitId,
    IReadOnlyDictionary<VitalParameter, (double Value, DateTimeOffset Time)> Latest, VitalSigns? Vitals, ScoreResult? News2, EscalationLevel Level,
    IReadOnlyList<VitalParameter> Missing, DateTimeOffset Updated, int Rejected);

/// <summary>An early-warning alert raised (or cleared) for a bed.</summary>
public sealed record EarlyWarningAlert(string DeviceId, string? BedCode, string? PatientId, string? PatientName, int Score, EscalationLevel Level, bool Raised, Localized Message, DateTimeOffset At);

public sealed record MonitoringOptions
{
    /// <summary>How long a reading stays usable for NEWS2 (temperature is measured less often).</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TemperatureMaxAge { get; init; } = TimeSpan.FromMinutes(60);
    /// <summary>Consecutive evaluations at a level before the alert is raised (suppresses one-off artefacts).</summary>
    public int ConfirmEvaluations { get; init; } = 2;
    /// <summary>Consecutive evaluations below the level before the alert clears (hysteresis).</summary>
    public int ClearEvaluations { get; init; } = 3;
    /// <summary>How often a validated-pending set is charted to the record.</summary>
    public TimeSpan ChartEvery { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Median filter length for heart rate, respiratory rate and SpO2.</summary>
    public int MedianWindow { get; init; } = 3;
}

/// <summary>
/// Real-time NEWS2 from bedside devices. Each reading passes a plausibility check, short median filtering of HR / RR /
/// SpO2, then NEWS2 is recomputed from the freshest complete set. Escalation follows the RCP thresholds; alerts are
/// raised only after <see cref="MonitoringOptions.ConfirmEvaluations"/> consistent evaluations and cleared with hysteresis,
/// so a probe falling off does not page the team. Consciousness and supplemental oxygen are set by the nurse.
/// </summary>
public sealed class MonitoringEngine(MonitoringOptions? options = null, TimeProvider? clock = null)
{
    private sealed class BedState
    {
        public string? BedCode, PatientId, PatientName, VisitId;
        public readonly Dictionary<VitalParameter, (double Value, DateTimeOffset Time)> Latest = [];
        public readonly Dictionary<VitalParameter, Queue<double>> Window = [];
        public EscalationLevel Alerted = EscalationLevel.None;
        public EscalationLevel Pending = EscalationLevel.None;
        public int PendingCount, BelowCount, Rejected;
        public DateTimeOffset LastCharted = DateTimeOffset.MinValue;
        public BedSnapshot? Snapshot;
        public readonly Lock Gate = new();
    }

    private readonly MonitoringOptions _o = options ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, BedState> _beds = new();

    /// <summary>Raised after every accepted reading.</summary>
    public event Action<BedSnapshot>? Updated;
    /// <summary>Raised when an alert is confirmed or cleared.</summary>
    public event Action<EarlyWarningAlert>? Alert;
    /// <summary>Raised when a complete set is due for charting (every <see cref="MonitoringOptions.ChartEvery"/>).</summary>
    public event Action<BedSnapshot>? ChartDue;

    public IReadOnlyList<BedSnapshot> Snapshots => _beds.Values.Select(b => b.Snapshot).OfType<BedSnapshot>().OrderBy(s => s.BedCode ?? s.DeviceId).ToList();

    /// <summary>Associates a device with a bed and its current patient (called on admission / transfer).</summary>
    public void Bind(string deviceId, string? bedCode, string? patientId, string? patientName, string? visitId)
    {
        var b = _beds.GetOrAdd(deviceId, _ => new BedState());
        lock (b.Gate)
        {
            if (b.PatientId != patientId) { b.Latest.Clear(); b.Window.Clear(); b.Alerted = b.Pending = EscalationLevel.None; b.PendingCount = b.BelowCount = 0; }
            (b.BedCode, b.PatientId, b.PatientName, b.VisitId) = (bedCode, patientId, patientName, visitId);
        }
    }

    /// <summary>Nurse-entered parameters the monitor cannot measure.</summary>
    public void SetManual(string deviceId, Consciousness consciousness, bool onOxygen)
    {
        var now = _clock.GetUtcNow();
        Ingest(new DeviceReading(deviceId, VitalParameter.Consciousness, (int)consciousness, now));
        Ingest(new DeviceReading(deviceId, VitalParameter.OnOxygen, onOxygen ? 1 : 0, now));
    }

    public static bool Plausible(VitalParameter p, double v) => p switch
    {
        VitalParameter.HeartRate => v is >= 20 and <= 300,
        VitalParameter.RespiratoryRate => v is >= 2 and <= 80,
        VitalParameter.SpO2 => v is >= 50 and <= 100,
        VitalParameter.SystolicBp => v is >= 40 and <= 300,
        VitalParameter.DiastolicBp => v is >= 15 and <= 200,
        VitalParameter.TemperatureC => v is >= 30 and <= 44,
        VitalParameter.OnOxygen => v is 0 or 1,
        VitalParameter.Consciousness => v is >= 0 and <= 4,
        _ => false,
    };

    public BedSnapshot? Ingest(DeviceReading r)
    {
        var b = _beds.GetOrAdd(r.DeviceId, _ => new BedState());
        BedSnapshot snap;
        EarlyWarningAlert? alert = null;
        bool chart = false;
        lock (b.Gate)
        {
            if (!Plausible(r.Parameter, r.Value)) { b.Rejected++; if (b.Snapshot is not null) b.Snapshot = b.Snapshot with { Rejected = b.Rejected }; return b.Snapshot; }
            double value = r.Value;
            if (r.Parameter is VitalParameter.HeartRate or VitalParameter.RespiratoryRate or VitalParameter.SpO2 && _o.MedianWindow > 1)
            {
                if (!b.Window.TryGetValue(r.Parameter, out var q)) b.Window[r.Parameter] = q = new Queue<double>();
                q.Enqueue(value);
                while (q.Count > _o.MedianWindow) q.Dequeue();
                var sorted = q.Order().ToArray();
                value = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            }
            b.Latest[r.Parameter] = (value, r.Time);
            snap = Evaluate(r.DeviceId, b, r.Time, out alert);
            b.Snapshot = snap;
            if (snap.Vitals is not null && r.Time - b.LastCharted >= _o.ChartEvery) { b.LastCharted = r.Time; chart = true; }
        }
        Updated?.Invoke(snap);
        if (alert is not null) Alert?.Invoke(alert);
        if (chart) ChartDue?.Invoke(snap);
        return snap;
    }

    private BedSnapshot Evaluate(string deviceId, BedState b, DateTimeOffset now, out EarlyWarningAlert? alert)
    {
        alert = null;
        bool Fresh(VitalParameter p, TimeSpan age) => b.Latest.TryGetValue(p, out var x) && now - x.Time <= age;
        VitalParameter[] required = [VitalParameter.RespiratoryRate, VitalParameter.SpO2, VitalParameter.SystolicBp, VitalParameter.HeartRate, VitalParameter.TemperatureC];
        var missing = required.Where(p => !Fresh(p, p == VitalParameter.TemperatureC ? _o.TemperatureMaxAge : _o.MaxAge)).ToList();
        VitalSigns? vs = null;
        ScoreResult? news = null;
        var level = EscalationLevel.None;
        if (missing.Count == 0)
        {
            double V(VitalParameter p, double d = 0) => b.Latest.TryGetValue(p, out var x) ? x.Value : d;
            vs = new VitalSigns
            {
                Timestamp = now, RespiratoryRate = V(VitalParameter.RespiratoryRate), SpO2 = V(VitalParameter.SpO2), SystolicBp = V(VitalParameter.SystolicBp),
                DiastolicBp = V(VitalParameter.DiastolicBp, double.NaN) is var d && double.IsNaN(d) ? V(VitalParameter.SystolicBp) * 0.6 : d,
                HeartRate = V(VitalParameter.HeartRate), TemperatureC = V(VitalParameter.TemperatureC),
                OnSupplementalOxygen = V(VitalParameter.OnOxygen) >= 0.5, Consciousness = (Consciousness)(int)V(VitalParameter.Consciousness),
            };
            news = ClinicalScores.News2(vs);
            level = news.Score >= 7 ? EscalationLevel.High : news.Score >= 5 ? EscalationLevel.Medium
                : news.Components.Any(c => c.Points == 3) ? EscalationLevel.LowMedium : news.Score >= 1 ? EscalationLevel.Low : EscalationLevel.None;

            // Confirmation and hysteresis for the levels that trigger a clinical response. A device not bound to a
            // patient (empty bed, monitor on standby) never alerts — there is nobody to respond to.
            var target = level >= EscalationLevel.LowMedium && b.PatientId is not null ? level : EscalationLevel.None;
            if (target > b.Alerted)
            {
                b.BelowCount = 0;
                if (target == b.Pending) b.PendingCount++; else (b.Pending, b.PendingCount) = (target, 1);
                if (b.PendingCount >= _o.ConfirmEvaluations)
                {
                    b.Alerted = target;
                    alert = new EarlyWarningAlert(deviceId, b.BedCode, b.PatientId, b.PatientName, news.Score, target, true, Message(target, news.Score), now);
                }
            }
            else if (target < b.Alerted)
            {
                b.PendingCount = 0;
                if (++b.BelowCount >= _o.ClearEvaluations)
                {
                    var was = b.Alerted;
                    b.Alerted = target;
                    b.BelowCount = 0;
                    alert = new EarlyWarningAlert(deviceId, b.BedCode, b.PatientId, b.PatientName, news.Score, target, false,
                        new($"NEWS2 {news.Score}: {was} alert cleared.", $"NEWS2 {news.Score}: peringatan {was} berakhir."), now);
                }
            }
            else { b.PendingCount = 0; b.BelowCount = 0; }
        }
        return new BedSnapshot(deviceId, b.BedCode, b.PatientId, b.PatientName, b.VisitId, new Dictionary<VitalParameter, (double, DateTimeOffset)>(b.Latest),
            vs, news, level, missing, now, b.Rejected);
    }

    public static Localized Message(EscalationLevel level, int score) => level switch
    {
        EscalationLevel.High => new($"NEWS2 {score}: emergency response — continuous monitoring, critical-care outreach team now.",
                                    $"NEWS2 {score}: respons darurat — pemantauan kontinu, panggil tim tanggap cepat sekarang."),
        EscalationLevel.Medium => new($"NEWS2 {score}: urgent review by a clinician within 30 minutes; observations at least hourly.",
                                      $"NEWS2 {score}: evaluasi segera oleh dokter dalam 30 menit; observasi minimal tiap jam."),
        EscalationLevel.LowMedium => new($"NEWS2 {score} with a single parameter scoring 3: urgent ward-based review.",
                                         $"NEWS2 {score} dengan satu parameter bernilai 3: evaluasi segera di bangsal."),
        _ => new($"NEWS2 {score}.", $"NEWS2 {score}."),
    };
}
