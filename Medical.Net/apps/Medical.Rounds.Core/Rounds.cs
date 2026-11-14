using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Medical.Clinical;
using Medical.Core;
using Medical.Sync;
using Medical.Sync.Rounds;

namespace Medical.Rounds;

/// <summary>Device settings (persisted by the app shell).</summary>
public sealed record RoundsSettings(string ServerUrl, string Tenant, string Ward, string Nurse, string DeviceKey, string DeviceId)
{
    public static RoundsSettings Demo => new("http://localhost:5300", "rs-nusantara", "MELATI", "ns.dewi", "demo-device-key", $"tablet-{Environment.MachineName.ToLowerInvariant()}");
}

/// <summary>One row of the ward list.</summary>
public sealed record WardRow(RoundsPatient Patient, int? News2, DateTime? News2At, int OpenTasks, bool PendingSync)
{
    public string Risk => News2 switch { >= 7 => "high", >= 5 => "medium", null => "unknown", _ => "low" };
}

/// <summary>A patient's chart on the device: vitals (newest first), notes and tasks.</summary>
public sealed record PatientChart(RoundsPatient Patient, IReadOnlyList<RoundsVitals> Vitals, IReadOnlyList<RoundsNote> Notes, IReadOnlyList<RoundsTask> Tasks);

/// <summary>Values typed by the nurse.</summary>
public sealed record VitalsInput(double RespiratoryRate, double SpO2, bool OnOxygen, double SystolicBp, double DiastolicBp, double HeartRate, double TemperatureC, Consciousness Avpu = Consciousness.Alert);

/// <summary>
/// The ward-round app's model, independent of MAUI. Everything is written to the local SQLite store first, so the app
/// works without a network; <see cref="SyncAsync"/> exchanges changes with the HIS when it can (pull is scoped to the
/// device's ward). NEWS2 is computed on the device for immediate escalation advice and recomputed by the server.
/// </summary>
public sealed class RoundsApp : INotifyPropertyChanged, IDisposable
{
    private readonly SyncClient _client;
    private readonly SqliteSyncStore _store;
    private readonly TimeProvider _clock;
    private bool _online = true;
    private string? _lastError;

    public RoundsSettings Settings { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public RoundsApp(RoundsSettings settings, string databasePath, ISyncTransport? transport = null, TimeProvider? clock = null)
    {
        Settings = settings;
        _clock = clock ?? TimeProvider.System;
        _store = databasePath == ":memory:" ? new SqliteSyncStore() : SqliteSyncStore.Open(databasePath);
        _client = new SyncClient(_store, transport ?? HttpTransport(settings), settings.DeviceId, new HybridLogicalClock(_clock));
    }

    /// <summary>HTTP transport to <c>/api/{tenant}/sync</c> with the device key, pulling only this ward.</summary>
    public static ISyncTransport HttpTransport(RoundsSettings s, HttpMessageHandler? inner = null)
    {
        var http = new HttpClient(new WardScopeHandler(s.Ward) { InnerHandler = inner ?? new HttpClientHandler() }) { Timeout = TimeSpan.FromSeconds(15) };
        // Per-device tokens ("mdn_…", enrolled in HIS → Devices) are bound to one ward; anything else is the legacy shared key.
        http.DefaultRequestHeaders.Add(s.DeviceKey.StartsWith("mdn_", StringComparison.Ordinal) ? "X-Device-Token" : "X-Device-Key", s.DeviceKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return new HttpSyncTransport(http, $"{s.ServerUrl.TrimEnd('/')}/api/{s.Tenant}/sync");
    }

    private sealed class WardScopeHandler(string ward) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { } u && u.AbsolutePath.EndsWith("/pull", StringComparison.Ordinal))
                request.RequestUri = new UriBuilder(u) { Query = $"ward={Uri.EscapeDataString(ward)}" }.Uri;
            return base.SendAsync(request, ct);
        }
    }

    public bool Online { get => _online; private set => Set(ref _online, value); }
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }
    public int PendingChanges => _store.DirtyCount;
    public DateTimeOffset? LastSync => _client.LastSync;

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        var r = await _client.SyncAsync(ct).ConfigureAwait(false);
        Online = r.Online;
        LastError = r.Online ? (r.Conflicts.Count > 0 ? $"{r.Conflicts.Count} conflict(s): {r.Conflicts[0].Reason}" : null) : r.Error;
        Changed(nameof(PendingChanges));
        Changed(nameof(LastSync));
        return r;
    }

    /// <summary>The ward list, sickest first (latest NEWS2 from the device or the server).</summary>
    public IReadOnlyList<WardRow> Ward()
    {
        var patients = _client.All<RoundsPatient>(RoundsTypes.Patient);
        var vitals = _client.All<RoundsVitals>(RoundsTypes.Vitals);
        var tasks = _client.All<RoundsTask>(RoundsTypes.Task);
        var dirty = _store.Dirty().Select(d => d.Data?["visitId"]?.GetValue<string>()).OfType<string>().ToHashSet();
        return patients.Select(p =>
        {
            var mine = vitals.Where(v => v.VisitId == p.Id).OrderByDescending(v => v.Time).FirstOrDefault();
            bool deviceNewer = mine is not null && (p.News2At is null || mine.Time > p.News2At);
            return new WardRow(p, deviceNewer ? mine!.News2 : p.News2, deviceNewer ? mine!.Time : p.News2At,
                tasks.Count(t => t.VisitId == p.Id && !t.Done), dirty.Contains(p.Id));
        }).OrderByDescending(r => r.News2 ?? -1).ThenBy(r => r.Patient.Bed).ToList();
    }

    public PatientChart? Chart(string visitId)
    {
        var p = _client.Get<RoundsPatient>(RoundsTypes.Patient, visitId);
        if (p is null) return null;
        return new PatientChart(p,
            _client.All<RoundsVitals>(RoundsTypes.Vitals).Where(v => v.VisitId == visitId).OrderByDescending(v => v.Time).ToList(),
            _client.All<RoundsNote>(RoundsTypes.Note).Where(n => n.VisitId == visitId).OrderByDescending(n => n.Time).ToList(),
            _client.All<RoundsTask>(RoundsTypes.Task).Where(t => t.VisitId == visitId).OrderBy(t => t.Done).ThenBy(t => t.Due).ToList());
    }

    /// <summary>NEWS2 and the clinical response for values being typed (before saving).</summary>
    public static ScoreResult Preview(VitalsInput v) => ClinicalScores.News2(ToVitalSigns(v, DateTimeOffset.UtcNow));

    public RoundsVitals RecordVitals(string visitId, VitalsInput v)
    {
        var p = _client.Get<RoundsPatient>(RoundsTypes.Patient, visitId) ?? throw new KeyNotFoundException(visitId);
        var now = _clock.GetUtcNow();
        var news = ClinicalScores.News2(ToVitalSigns(v, now));
        var row = new RoundsVitals(Guid.NewGuid().ToString("n"), p.PatientId, visitId, now.UtcDateTime, v.RespiratoryRate, v.SpO2, v.OnOxygen, v.SystolicBp,
            v.DiastolicBp, v.HeartRate, v.TemperatureC, v.Avpu.ToString(), news.Score, Settings.Nurse);
        _client.Save(RoundsTypes.Vitals, row.Id, row);
        Changed(nameof(PendingChanges));
        return row;
    }

    public RoundsNote AddNote(string visitId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Empty note.", nameof(text));
        var p = _client.Get<RoundsPatient>(RoundsTypes.Patient, visitId) ?? throw new KeyNotFoundException(visitId);
        var n = new RoundsNote(Guid.NewGuid().ToString("n"), p.PatientId, visitId, _clock.GetUtcNow().UtcDateTime, Settings.Nurse, text.Trim());
        _client.Save(RoundsTypes.Note, n.Id, n);
        Changed(nameof(PendingChanges));
        return n;
    }

    public RoundsTask CompleteTask(string taskId, bool done = true)
    {
        var t = _client.Get<RoundsTask>(RoundsTypes.Task, taskId) ?? throw new KeyNotFoundException(taskId);
        var updated = t with { Done = done, DoneBy = done ? Settings.Nurse : null, DoneAt = done ? _clock.GetUtcNow().UtcDateTime : null };
        _client.Save(RoundsTypes.Task, taskId, updated);
        Changed(nameof(PendingChanges));
        return updated;
    }

    private static VitalSigns ToVitalSigns(VitalsInput v, DateTimeOffset at) => new()
    {
        Timestamp = at, RespiratoryRate = v.RespiratoryRate, SpO2 = v.SpO2, OnSupplementalOxygen = v.OnOxygen, SystolicBp = v.SystolicBp,
        DiastolicBp = v.DiastolicBp, HeartRate = v.HeartRate, TemperatureC = v.TemperatureC, Consciousness = v.Avpu,
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name!);
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _store.Dispose();
}
