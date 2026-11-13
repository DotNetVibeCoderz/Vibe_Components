using System.Text.Json;
using Medical.Core;
using Medical.Emr.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Medical.Emr;

/// <summary>
/// Patient-record repository over <see cref="MedicalDbContext"/>. Reads go through an optional distributed cache
/// (Redis in production, memory in development); every access is written to the audit trail.
/// </summary>
public sealed class PatientRepository(MedicalDbContext db, IAuditLog audit, IDistributedCache? cache = null, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DistributedCacheEntryOptions CacheEntry = new() { SlidingExpiration = TimeSpan.FromMinutes(10) };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string User { get; set; } = "system";

    private static string CacheKey(string id) => $"mednet:record:{id}";

    /// <summary>Inserts or replaces a complete patient record.</summary>
    public async Task SaveAsync(PatientRecord record, CancellationToken ct = default)
    {
        var existing = await db.Patients.Include(p => p.Encounters).Include(p => p.Vitals).Include(p => p.Observations)
            .Include(p => p.Conditions).Include(p => p.Medications).Include(p => p.Allergies)
            .FirstOrDefaultAsync(p => p.Id == record.Patient.Id, ct).ConfigureAwait(false);
        if (existing is not null) db.Patients.Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Patients.Add(EmrMapping.ToEntity(record, _clock.GetUtcNow()));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (cache is not null) await cache.RemoveAsync(CacheKey(record.Patient.Id), ct).ConfigureAwait(false);
        audit.Record(new AuditEvent(_clock.GetUtcNow(), User, existing is null ? AuditAction.Create : AuditAction.Update, "PatientRecord", record.Patient.Id));
    }

    /// <summary>Loads the full record (cache first).</summary>
    public async Task<PatientRecord?> GetRecordAsync(string patientId, string? purpose = null, CancellationToken ct = default)
    {
        PatientRecord? record = null;
        if (cache is not null && await cache.GetStringAsync(CacheKey(patientId), ct).ConfigureAwait(false) is { } cached)
            record = JsonSerializer.Deserialize<PatientRecord>(cached, Json);
        if (record is null)
        {
            var e = await db.Patients.AsNoTracking().AsSplitQuery()
                .Include(p => p.Encounters).Include(p => p.Vitals).Include(p => p.Observations)
                .Include(p => p.Conditions).Include(p => p.Medications).Include(p => p.Allergies)
                .FirstOrDefaultAsync(p => p.Id == patientId, ct).ConfigureAwait(false);
            if (e is not null)
            {
                record = EmrMapping.ToRecord(e);
                if (cache is not null) await cache.SetStringAsync(CacheKey(patientId), JsonSerializer.Serialize(record, Json), CacheEntry, ct).ConfigureAwait(false);
            }
        }
        audit.Record(new AuditEvent(_clock.GetUtcNow(), User, AuditAction.Read, "PatientRecord", patientId, purpose, record is not null));
        return record;
    }

    /// <summary>Search by MRN, NIK or name prefix (family or given).</summary>
    public async Task<IReadOnlyList<Patient>> SearchAsync(string text, int take = 25, CancellationToken ct = default)
    {
        text = text.Trim();
        var q = db.Patients.AsNoTracking().Where(p => p.Mrn == text || p.Nik == text || p.Family.StartsWith(text) || p.Given.StartsWith(text));
        var rows = await q.OrderBy(p => p.Family).ThenBy(p => p.Given).Take(take).ToListAsync(ct).ConfigureAwait(false);
        audit.Record(new AuditEvent(_clock.GetUtcNow(), User, AuditAction.Query, "Patient", null, $"search:{rows.Count}"));
        return rows.Select(EmrMapping.ToDomain).ToArray();
    }

    public Task<int> CountAsync(CancellationToken ct = default) => db.Patients.CountAsync(ct);

    /// <summary>Appends one vital-signs set and invalidates the cached record.</summary>
    public async Task AddVitalsAsync(string patientId, VitalSigns v, CancellationToken ct = default)
    {
        db.Vitals.Add(EmrMapping.ToEntity(patientId, v));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (cache is not null) await cache.RemoveAsync(CacheKey(patientId), ct).ConfigureAwait(false);
        audit.Record(new AuditEvent(_clock.GetUtcNow(), User, AuditAction.Create, "VitalSigns", patientId));
    }

    /// <summary>Appends observations (e.g. from an HL7 ORU feed).</summary>
    public async Task AddObservationsAsync(IEnumerable<ClinicalObservation> observations, CancellationToken ct = default)
    {
        var list = observations.ToList();
        db.Observations.AddRange(list.Select(EmrMapping.ToEntity));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var pid in list.Select(o => o.PatientId).Distinct())
        {
            if (cache is not null) await cache.RemoveAsync(CacheKey(pid), ct).ConfigureAwait(false);
            audit.Record(new AuditEvent(_clock.GetUtcNow(), User, AuditAction.Create, "Observation", pid));
        }
    }

    /// <summary>Vital-sign history between two instants, oldest first.</summary>
    public async Task<IReadOnlyList<VitalSigns>> VitalsAsync(string patientId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var rows = await db.Vitals.AsNoTracking().Where(v => v.PatientId == patientId && v.Timestamp >= from && v.Timestamp <= to)
            .OrderBy(v => v.Timestamp).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(EmrMapping.ToDomain).ToArray();
    }

    /// <summary>Seeds <paramref name="count"/> deterministic synthetic patients (no-op for ids that already exist).</summary>
    public async Task<int> SeedSyntheticAsync(int count, CancellationToken ct = default)
    {
        int added = 0;
        for (int i = 1; i <= count; i++)
        {
            string id = $"pat-{i:00000}";
            if (await db.Patients.AnyAsync(p => p.Id == id, ct).ConfigureAwait(false)) continue;
            db.Patients.Add(EmrMapping.ToEntity(SyntheticData.Record(i, i % 7 == 0 ? 0.85 : i % 3 == 0 ? 0.45 : 0.1), _clock.GetUtcNow()));
            added++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return added;
    }
}

/// <summary><see cref="IAuditLog"/> persisted in the EMR database.</summary>
public sealed class EfAuditLog(IDbContextFactory<MedicalDbContext> factory) : IAuditLog
{
    public void Record(AuditEvent e)
    {
        using var db = factory.CreateDbContext();
        db.AuditEvents.Add(new AuditEntity
        {
            Timestamp = e.Timestamp, User = e.User, Action = e.Action, Resource = e.Resource, PatientId = e.PatientId, Purpose = e.Purpose, Success = e.Success,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<AuditEvent> Query(string? patientId = null, DateTimeOffset? since = null)
    {
        using var db = factory.CreateDbContext();
        var q = db.AuditEvents.AsNoTracking().AsQueryable();
        if (patientId is not null) q = q.Where(a => a.PatientId == patientId);
        if (since is { } s) q = q.Where(a => a.Timestamp >= s);
        return q.OrderBy(a => a.Id).AsEnumerable()
            .Select(a => new AuditEvent(a.Timestamp, a.User, a.Action, a.Resource, a.PatientId, a.Purpose, a.Success)).ToArray();
    }
}
