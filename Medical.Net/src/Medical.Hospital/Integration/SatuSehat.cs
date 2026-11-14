using Medical.Fhir;
using Medical.Fhir.SatuSehat;
using Microsoft.EntityFrameworkCore;
using F = global::Hl7.Fhir.Model;

namespace Medical.Hospital.Integration;

/// <summary>Facility identities the publisher cannot derive from the HIS data.</summary>
public sealed record SatuSehatPublisherOptions
{
    /// <summary>SATUSEHAT <c>Location</c> id per HIS department code (registered in the SATUSEHAT portal).</summary>
    public IReadOnlyDictionary<string, string> LocationIds { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Location used when a department has no entry in <see cref="LocationIds"/>.</summary>
    public string? DefaultLocationId { get; init; }
    /// <summary>Practitioner IHS numbers per staff user name; otherwise the practitioner is looked up by the staff NIK.</summary>
    public IReadOnlyDictionary<string, string> PractitionerIhs { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>KFA codes for the dispensed drugs (drugs without one are skipped with a warning).</summary>
    public KfaCatalog? Kfa { get; init; }
    /// <summary>A submission is no longer retried after this many failed attempts.</summary>
    public int MaxAttempts { get; init; } = 5;
}

/// <summary>Outcome of a <see cref="SatuSehatPublisher.RetryPendingAsync"/> run.</summary>
public sealed record SatuSehatRunResult(int Sent, int Failed);

/// <summary>
/// Sends finished visits (outpatient / emergency visits that are <see cref="VisitStatus.Done"/> and discharged inpatient stays)
/// to SATUSEHAT. The visit is narrowed out of <see cref="HisRecords.ToPatientRecordAsync"/> (its encounter, its diagnoses,
/// vital signs charted during it and the drugs dispensed for it), turned into one transaction bundle by
/// <see cref="SatuSehatWorkflow.BuildOutpatientVisit"/> and posted with <see cref="SatuSehatClient.PostTransactionAsync"/>.
/// Every attempt is recorded in a <see cref="SatuSehatSubmissionRecord"/> outbox row; failures are retried by
/// <see cref="RetryPendingAsync"/>. The patient IHS number is found by NIK and the practitioner by the staff NIK
/// (<c>Patient?identifier=https://fhir.kemkes.go.id/id/nik|…</c>, SATUSEHAT FHIR R4 playbook).
/// </summary>
public sealed class SatuSehatPublisher(HospitalDbContext db, SatuSehatClient client, SatuSehatPublisherOptions? options = null, TimeProvider? clock = null)
{
    private readonly SatuSehatPublisherOptions _options = options ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private sealed class SubmissionException(string message) : Exception(message);

    private static bool Finished(Visit v) => v.End is not null && v.Status is VisitStatus.Done or VisitStatus.Discharged;

    public Task<SatuSehatSubmissionRecord?> SubmissionAsync(string visitId, CancellationToken ct = default) =>
        db.Set<SatuSehatSubmissionRecord>().FirstOrDefaultAsync(s => s.VisitId == visitId, ct);

    /// <summary>Queues a finished visit (idempotent: one outbox row per visit).</summary>
    public async Task<SatuSehatSubmissionRecord> EnqueueAsync(string visitId, CancellationToken ct = default)
    {
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == visitId, ct) ?? throw new KeyNotFoundException($"Visit '{visitId}' not found.");
        if (!Finished(v)) throw new InvalidOperationException("Only finished visits (done or discharged) are sent to SATUSEHAT.");
        if (await SubmissionAsync(visitId, ct) is { } existing) return existing;
        var row = new SatuSehatSubmissionRecord { VisitId = v.Id, PatientId = v.PatientId, CreatedAt = Now };
        db.Set<SatuSehatSubmissionRecord>().Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Queues every visit finished since <paramref name="sinceUtc"/> that has no submission yet; returns how many were queued.</summary>
    public async Task<int> EnqueueFinishedVisitsAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        var known = (await db.Set<SatuSehatSubmissionRecord>().Select(s => s.VisitId).ToListAsync(ct)).ToHashSet();
        var visits = await db.Visits.Where(v => v.End != null && v.End >= sinceUtc && (v.Status == VisitStatus.Done || v.Status == VisitStatus.Discharged)).ToListAsync(ct);
        int n = 0;
        foreach (var v in visits.Where(v => !known.Contains(v.Id)))
        {
            db.Set<SatuSehatSubmissionRecord>().Add(new SatuSehatSubmissionRecord { VisitId = v.Id, PatientId = v.PatientId, CreatedAt = Now });
            n++;
        }
        await db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>Queues the visit and sends it now (on discharge / when the outpatient visit is finished).</summary>
    public async Task<SatuSehatSubmissionRecord> PublishVisitAsync(string visitId, CancellationToken ct = default)
    {
        var row = await EnqueueAsync(visitId, ct);
        if (row.Status != SatuSehatSubmissionStatus.Sent) await AttemptAsync(row, ct);
        return row;
    }

    /// <summary>Sends pending and failed submissions (oldest first) that have not reached <see cref="SatuSehatPublisherOptions.MaxAttempts"/>.</summary>
    public async Task<SatuSehatRunResult> RetryPendingAsync(int limit = 50, CancellationToken ct = default)
    {
        var rows = (await db.Set<SatuSehatSubmissionRecord>()
                .Where(s => s.Status != SatuSehatSubmissionStatus.Sent && s.Attempts < _options.MaxAttempts).ToListAsync(ct))
            .OrderBy(s => s.CreatedAt).Take(limit).ToList();
        int sent = 0, failed = 0;
        foreach (var row in rows)
        {
            await AttemptAsync(row, ct);
            if (row.Status == SatuSehatSubmissionStatus.Sent) sent++; else failed++;
        }
        return new SatuSehatRunResult(sent, failed);
    }

    private async Task AttemptAsync(SatuSehatSubmissionRecord row, CancellationToken ct)
    {
        row.Attempts++;
        row.LastAttemptAt = Now;
        try
        {
            var visit = await db.Visits.FirstOrDefaultAsync(v => v.Id == row.VisitId, ct) ?? throw new SubmissionException($"Visit '{row.VisitId}' no longer exists.");
            var patient = await db.Patients.FirstAsync(p => p.Id == visit.PatientId, ct);
            var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == visit.DepartmentId, ct);
            var doctor = visit.DoctorId is { } did ? await db.Staff.FirstOrDefaultAsync(s => s.Id == did || s.UserName == did, ct) : null;
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == db.Tenant.TenantId, ct);
            string org = tenant?.SatuSehatOrgId is { Length: > 0 } o ? o : client.Options.OrganizationId;
            if (org.Length == 0) throw new SubmissionException("No SATUSEHAT organization id (tenant SatuSehatOrgId or His:SatuSehat:OrganizationId).");
            string location = (dept is not null && _options.LocationIds.TryGetValue(dept.Code, out var loc) ? loc : _options.DefaultLocationId)
                              ?? throw new SubmissionException($"No SATUSEHAT Location id for department '{dept?.Code}'.");

            row.PatientIhs ??= await PatientIhsAsync(patient, ct);
            string practitioner = await PractitionerIhsAsync(doctor, ct);

            var record = await VisitRecordAsync(visit, ct);
            var ctx = new SatuSehatContext
            {
                PatientIhs = row.PatientIhs, PatientName = patient.FullName, PractitionerIhs = practitioner, PractitionerName = doctor?.Name ?? practitioner,
                LocationId = location, LocationName = dept?.NameId ?? location, OrganizationId = org,
            };
            var submission = SatuSehatWorkflow.BuildOutpatientVisit(record, ctx, _options.Kfa);
            row.ResourceCount = submission.ResourceCount;
            row.Warnings = submission.Warnings.Count == 0 ? null : string.Join("\n", submission.Warnings);

            var response = await client.PostTransactionAsync(submission.Bundle, ct);
            row.RawResponse = FhirJson.Serialize(response, false);
            int encIndex = submission.Bundle.Entry.FindIndex(e => e.Resource is F.Encounter);
            if (encIndex >= 0 && encIndex < response.Entry.Count && response.Entry[encIndex].Response?.Location is { } locHeader)
                row.EncounterId = ResourceId(locHeader);
            row.Status = SatuSehatSubmissionStatus.Sent;
            row.SentAt = Now;
            row.LastError = null;
            db.AuditEvent(Medical.Core.AuditAction.Export, "SATUSEHAT/Bundle", patient.Id, $"{visit.Id} → Encounter/{row.EncounterId}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            row.Status = SatuSehatSubmissionStatus.Failed;
            row.LastError = e.Message;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Encounter/abc/_history/1" → "abc".</summary>
    private static string ResourceId(string location)
    {
        var parts = location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.IndexOf(parts, "Encounter");
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : location;
    }

    private async Task<string> PatientIhsAsync(HisPatient patient, CancellationToken ct)
    {
        string nik = patient.Nik ?? throw new SubmissionException("The patient has no NIK, so the SATUSEHAT IHS number cannot be looked up.");
        var found = await client.SearchPatientByNikAsync(nik, ct);
        return found.Entry.Select(e => e.Resource).OfType<F.Patient>().FirstOrDefault()?.Id
               ?? throw new SubmissionException("Patient not found in SATUSEHAT by NIK.");
    }

    private async Task<string> PractitionerIhsAsync(Staff? doctor, CancellationToken ct)
    {
        if (doctor is null) throw new SubmissionException("The visit has no attending doctor.");
        if (_options.PractitionerIhs.TryGetValue(doctor.UserName, out var ihs)) return ihs;
        string nik = doctor.Nik ?? throw new SubmissionException($"No NIK or IHS number for practitioner '{doctor.UserName}'.");
        var found = await client.SearchAsync("Practitioner", "identifier=" + Uri.EscapeDataString($"{SatuSehatSystems.Nik}|{nik}"), ct);
        return found.Entry.Select(e => e.Resource).OfType<F.Practitioner>().FirstOrDefault()?.Id
               ?? throw new SubmissionException($"Practitioner '{doctor.UserName}' not found in SATUSEHAT by NIK.");
    }

    /// <summary>The patient record narrowed to one visit: its encounter, its diagnoses (primary first), vitals charted during it and drugs dispensed for it.</summary>
    private async Task<Medical.Core.PatientRecord> VisitRecordAsync(Visit visit, CancellationToken ct)
    {
        var full = await HisRecords.ToPatientRecordAsync(db, visit.PatientId, ct);
        var enc = full.Encounters.First(e => e.Id == visit.Id);
        var codes = await db.Diagnoses.Where(d => d.VisitId == visit.Id).OrderByDescending(d => d.Primary).Select(d => d.Icd10).ToListAsync(ct);
        var itemIds = (await db.Prescriptions.Where(r => r.VisitId == visit.Id && r.State == PrescriptionState.Dispensed).SelectMany(r => r.Items).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
        return full with
        {
            Encounters = [enc],
            Conditions = codes.Distinct().Select(c => full.Conditions.FirstOrDefault(x => x.Code.Code == c)).OfType<Medical.Core.Condition>().ToList(),
            Vitals = full.Vitals.Where(v => v.Timestamp >= enc.Start && (enc.End is null || v.Timestamp <= enc.End)).ToList(),
            Medications = full.Medications.Where(m => itemIds.Contains(m.Id)).ToList(),
        };
    }
}
