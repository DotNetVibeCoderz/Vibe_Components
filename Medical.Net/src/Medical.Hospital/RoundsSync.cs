using System.Text.Json;
using System.Text.Json.Nodes;
using Medical.Clinical;
using Medical.Core;
using Medical.Sync;
using Medical.Sync.Rounds;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

/// <summary>
/// The hospital side of ward-round sync. The HIS publishes the ward census and tasks into a per-tenant sync log
/// (outbox); devices pull by ward (scope). Pushed vital signs, notes and task completions are applied to the clinical
/// record (vitals with NEWS2, nursing notes, ward tasks) as well as stored in the log.
/// </summary>
public static class RoundsSync
{
    private static readonly HybridLogicalClock Clock = new();

    /// <summary>Publishes (or refreshes) a patient on the ward list of the bed's ward.</summary>
    public static async Task PublishPatientAsync(HospitalDbContext db, Visit stay, CancellationToken ct = default)
    {
        if (stay.Kind != VisitKind.Inpatient || stay.BedId is null) return;
        var p = await db.Patients.FirstAsync(x => x.Id == stay.PatientId, ct);
        var bed = await db.Beds.FirstAsync(b => b.Id == stay.BedId, ct);
        var ward = await db.Departments.FirstAsync(d => d.Id == bed.WardId, ct);
        var dx = await db.Diagnoses.Where(d => d.VisitId == stay.Id).OrderByDescending(d => d.Primary).FirstOrDefaultAsync(ct);
        var last = (await db.Vitals.Where(v => v.PatientId == stay.PatientId).ToListAsync(ct)).OrderByDescending(v => v.Time).FirstOrDefault();
        var age = new Patient { Id = "", Mrn = "", Name = new("", ""), BirthDate = p.BirthDate }.AgeAt(DateOnly.FromDateTime(DateTime.UtcNow));
        var row = new RoundsPatient(stay.Id, p.Id, p.Mrn, p.FullName, age, p.Gender == Gender.Female ? "P" : "L", ward.NameId, bed.Code,
            dx is null ? null : $"{dx.Icd10} {dx.Display}", p.Allergies, stay.Start, last?.News2, last?.Time, stay.Payer.ToString());
        Put(db, RoundsTypes.Patient, stay.Id, row, ward.Id);
        await db.SaveChangesAsync(ct);
    }

    public static async Task RemovePatientAsync(HospitalDbContext db, Visit stay, string wardId, CancellationToken ct = default)
    {
        Put(db, RoundsTypes.Patient, stay.Id, (object?)null, wardId, deleted: true);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Creates a ward task and publishes it to the ward's devices.</summary>
    public static async Task<WardTask> AddTaskAsync(HospitalDbContext db, Visit stay, string title, DateTime due, CancellationToken ct = default)
    {
        var bed = await db.Beds.FirstAsync(b => b.Id == stay.BedId, ct);
        var t = new WardTask { PatientId = stay.PatientId, VisitId = stay.Id, WardId = bed.WardId, Title = title, Due = due, State = WardTaskState.Open };
        db.Tasks.Add(t);
        Put(db, RoundsTypes.Task, t.Id, new RoundsTask(t.Id, t.PatientId, stay.Id, title, due, false, null, null), bed.WardId);
        await db.SaveChangesAsync(ct);
        return t;
    }

    internal static void Put<T>(HospitalDbContext db, string type, string id, T? value, string? scope, bool deleted = false, string node = "server", long? hlc = null)
    {
        foreach (var old in db.SyncLog.Local.Where(s => s.Type == type && s.RecordId == id && s.Latest).Concat(
                     db.SyncLog.Where(s => s.Type == type && s.RecordId == id && s.Latest).ToList()).Distinct())
            old.Latest = false;
        db.SyncLog.Add(new SyncLogEntry
        {
            Type = type, RecordId = id, Hlc = hlc ?? Clock.Now(), NodeId = node, Scope = scope, Deleted = deleted, Latest = true,
            Data = value is null ? null : JsonSerializer.Serialize(value, SyncJson.Options),
        });
    }

    /// <summary>A sync server for one tenant over its database.</summary>
    public static SyncServer Server(HospitalDbContext db, string? deviceWardId = null) => new(new HospitalSyncStore(db, deviceWardId), RoundsTypes.Policies);
}

/// <summary>
/// <see cref="ISyncServerStore"/> over the hospital database (tenant-filtered; scope = ward id). With
/// <paramref name="deviceWardId"/> (an enrolled device), writes are accepted only for admissions on that ward, and vitals /
/// notes must name the admission's own patient.
/// </summary>
public sealed class HospitalSyncStore(HospitalDbContext db, string? deviceWardId = null) : ISyncServerStore
{
    public SyncChange? Get(string type, string id)
    {
        var e = db.SyncLog.Where(s => s.Type == type && s.RecordId == id && s.Latest).OrderByDescending(s => s.Seq).FirstOrDefault();
        return e is null ? null : ToChange(e);
    }

    public long Put(SyncChange change)
    {
        string? scope = null;
        var data = change.Data;
        switch (change.Type)
        {
            case RoundsTypes.Vitals when data is not null:
            {
                var v = data.Deserialize<RoundsVitals>(SyncJson.Options)!;
                scope = Authorise(v.VisitId, v.PatientId);
                var vs = new VitalSigns
                {
                    Timestamp = new DateTimeOffset(DateTime.SpecifyKind(v.Time, DateTimeKind.Utc)), RespiratoryRate = v.RespiratoryRate, SpO2 = v.SpO2, OnSupplementalOxygen = v.OnOxygen,
                    SystolicBp = v.SystolicBp, DiastolicBp = v.DiastolicBp, HeartRate = v.HeartRate, TemperatureC = v.TemperatureC,
                    Consciousness = Enum.TryParse<Consciousness>(v.Avpu, true, out var c) ? c : Consciousness.Alert,
                };
                int news = ClinicalScores.News2(vs).Score;   // recomputed on the server; the device value is advisory
                db.Vitals.Add(new VitalsEntry
                {
                    Id = v.Id, PatientId = v.PatientId, VisitId = v.VisitId, Time = vs.Timestamp.UtcDateTime, RespiratoryRate = vs.RespiratoryRate, SpO2 = vs.SpO2, OnOxygen = vs.OnSupplementalOxygen,
                    SystolicBp = vs.SystolicBp, DiastolicBp = vs.DiastolicBp, HeartRate = vs.HeartRate, TemperatureC = vs.TemperatureC, Consciousness = vs.Consciousness,
                    News2 = news, Source = VitalsSource.MobileApp, DeviceId = change.NodeId, RecordedBy = v.RecordedBy, Validated = true,
                });
                break;
            }
            case RoundsTypes.Note when data is not null:
            {
                var n = data.Deserialize<RoundsNote>(SyncJson.Options)!;
                scope = Authorise(n.VisitId, n.PatientId);
                db.Notes.Add(new NursingNote { Id = n.Id, PatientId = n.PatientId, VisitId = n.VisitId, Time = n.Time, Author = n.Author, Text = n.Text });
                break;
            }
            case RoundsTypes.Task when data is not null:
            {
                var t = data.Deserialize<RoundsTask>(SyncJson.Options)!;
                var task = db.Tasks.FirstOrDefault(x => x.Id == t.Id) ?? throw new InvalidOperationException($"Task {t.Id} not found.");
                if (deviceWardId is not null && task.WardId != deviceWardId) throw new InvalidOperationException("The task belongs to another ward.");
                (task.State, task.DoneBy, task.DoneAt) = (t.Done ? WardTaskState.Done : WardTaskState.Open, t.DoneBy, t.DoneAt);
                scope = task.WardId;
                break;
            }
            case RoundsTypes.Patient:
                throw new InvalidOperationException("The ward list is read-only on devices.");
        }
        RoundsSync.Put(db, change.Type, change.Id, data, scope, change.Deleted, change.NodeId, change.Hlc);
        db.SaveChanges();
        if (change.Type == RoundsTypes.Vitals && data is not null)
        {
            var v = data.Deserialize<RoundsVitals>(SyncJson.Options)!;
            if (db.Visits.FirstOrDefault(x => x.Id == v.VisitId) is { } stay) RoundsSync.PublishPatientAsync(db, stay).GetAwaiter().GetResult();
        }
        return db.SyncLog.Where(s => s.Type == change.Type && s.RecordId == change.Id && s.Latest).Max(s => s.Seq);
    }

    public IReadOnlyList<SyncChange> Since(long seq, int limit, IReadOnlyCollection<string>? types, string? scope)
    {
        var q = db.SyncLog.Where(s => s.Seq > seq && s.Latest);
        if (scope is not null) q = q.Where(s => s.Scope == scope);
        if (types is not null) q = q.Where(s => types.Contains(s.Type));
        return q.OrderBy(s => s.Seq).Take(limit).ToList().Select(ToChange).ToList();
    }

    /// <summary>The ward of an admission, after checking the device may write to it and the patient matches.</summary>
    private string? Authorise(string visitId, string patientId)
    {
        var stay = db.Visits.FirstOrDefault(v => v.Id == visitId) ?? throw new InvalidOperationException($"Admission {visitId} not found.");
        if (stay.PatientId != patientId) throw new InvalidOperationException("The record names a different patient than the admission.");
        string? ward = WardOf(visitId);
        if (deviceWardId is not null && ward != deviceWardId) throw new InvalidOperationException("The admission is on another ward.");
        return ward;
    }

    private string? WardOf(string visitId) =>
        db.Visits.Where(v => v.Id == visitId).Join(db.Beds, v => v.BedId, b => b.Id, (v, b) => b.WardId).FirstOrDefault();

    private static SyncChange ToChange(SyncLogEntry e) =>
        new(e.Type, e.RecordId, e.Hlc, e.NodeId, e.Data is null ? null : JsonNode.Parse(e.Data)!.AsObject(), e.Deleted) { Seq = e.Seq };
}
