using System.Globalization;
using Medical.Clinical;
using Medical.Core;
using Medical.Terminology;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

/// <summary>Events raised by hospital services on the <see cref="IMedicalEventBus"/> (dashboards, notifications, integration).</summary>
public abstract record HospitalEvent(string TenantId, DateTime At);
public sealed record PatientRegistered(string TenantId, DateTime At, string PatientId, string Mrn) : HospitalEvent(TenantId, At);
public sealed record VisitStarted(string TenantId, DateTime At, string VisitId, string DepartmentId, string? QueueNumber) : HospitalEvent(TenantId, At);
public sealed record PatientAdmitted(string TenantId, DateTime At, string VisitId, string PatientId, string BedId) : HospitalEvent(TenantId, At);
public sealed record PatientDischarged(string TenantId, DateTime At, string VisitId, string PatientId, DischargeType Type) : HospitalEvent(TenantId, At);
public sealed record CriticalLabResult(string TenantId, DateTime At, string ResultId, string PatientId, string Test, double? Value, ResultFlag Flag) : HospitalEvent(TenantId, At);
public sealed record VitalsCharted(string TenantId, DateTime At, string PatientId, int News2, VitalsSource Source) : HospitalEvent(TenantId, At);

/// <summary>Shared plumbing of the hospital services.</summary>
public abstract class HospitalServiceBase(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null)
{
    protected HospitalDbContext Db { get; } = db;
    protected TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    protected IMedicalEventBus? Bus { get; } = bus;
    protected DateTime Now => Clock.GetUtcNow().UtcDateTime;
    protected string TenantId => Db.Tenant.TenantId;
    protected void Publish<T>(T e) => Bus?.Publish(e);

    protected async Task<T> Find<T>(string id, CancellationToken ct) where T : TenantEntity =>
        await Db.Set<T>().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new KeyNotFoundException($"{typeof(T).Name} '{id}' not found.");

    protected async Task<decimal> PriceAsync(string code, decimal fallback, CancellationToken ct) =>
        (await Db.Tariffs.FirstOrDefaultAsync(t => t.Code == code, ct))?.Price ?? fallback;

    protected async Task ChargeAsync(string visitId, string code, string description, ChargeCategory category, decimal quantity, decimal? unitPrice, decimal fallback, CancellationToken ct)
    {
        decimal price = unitPrice ?? await PriceAsync(code, fallback, ct);
        Db.Charges.Add(new Charge { VisitId = visitId, Code = code, Description = description, Category = category, Quantity = quantity, UnitPrice = price, At = Now });
    }
}

/// <summary>New-patient form (registration desk).</summary>
public sealed record NewPatient(string Given, string Family, Gender Gender, DateOnly BirthDate, string? Nik = null, string? BpjsNumber = null,
    string? Phone = null, string? Address = null, string? City = null, string? Allergies = null);

/// <summary>Registration and queues: patient master index per tenant (NIK de-duplication, MRN), visits and queue numbers.</summary>
public sealed class RegistrationService(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null) : HospitalServiceBase(db, clock, bus)
{
    /// <summary>Registers a patient, or returns the existing one when the NIK is already known in this hospital.</summary>
    public async Task<HisPatient> RegisterAsync(NewPatient p, CancellationToken ct = default)
    {
        if (p.Nik is { } nik && (nik.Length != 16 || !nik.All(char.IsDigit))) throw new ArgumentException("NIK must be 16 digits.", nameof(p));
        if (p.BpjsNumber is { } card && (card.Length != 13 || !card.All(char.IsDigit))) throw new ArgumentException("BPJS card number must be 13 digits.", nameof(p));
        if (p.Nik is not null && await Db.Patients.FirstOrDefaultAsync(x => x.Nik == p.Nik, ct) is { } existing) return existing;
        long n = await Db.NextAsync("mrn", ct);
        var patient = new HisPatient
        {
            Mrn = n.ToString("D6", CultureInfo.InvariantCulture), Given = p.Given.Trim(), Family = p.Family.Trim(), Gender = p.Gender, BirthDate = p.BirthDate,
            Nik = p.Nik, BpjsNumber = p.BpjsNumber, Phone = p.Phone, Address = p.Address, City = p.City, Allergies = p.Allergies, RegisteredAt = Now,
        };
        Db.Patients.Add(patient);
        Db.AuditEvent(AuditAction.Create, "Patient", patient.Id);
        await Db.SaveChangesAsync(ct);
        Publish(new PatientRegistered(TenantId, Now, patient.Id, patient.Mrn));
        return patient;
    }

    public async Task<IReadOnlyList<HisPatient>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        string q = query.Trim().ToLowerInvariant();
        return await Db.Patients.Where(p => p.Mrn == q || p.Nik == q || p.BpjsNumber == q || (p.Given + " " + p.Family).ToLower().Contains(q))
            .OrderBy(p => p.Family).Take(limit).ToListAsync(ct);
    }

    /// <summary>Opens a visit. Outpatient clinics issue a daily queue number (prefix + sequence, e.g. "B-007").</summary>
    public async Task<Visit> StartVisitAsync(string patientId, string departmentId, VisitKind kind, PayerType payer, string? sepNumber = null,
        string? doctorId = null, string? complaint = null, CancellationToken ct = default)
    {
        var patient = await Find<HisPatient>(patientId, ct);
        var dept = await Find<Department>(departmentId, ct);
        if (payer == PayerType.Bpjs && patient.BpjsNumber is null) throw new InvalidOperationException("A BPJS visit needs the patient's card number.");
        if (await Db.Visits.AnyAsync(v => v.PatientId == patientId && v.DepartmentId == departmentId && (v.Status == VisitStatus.Waiting || v.Status == VisitStatus.InConsultation), ct))
            throw new InvalidOperationException("The patient already has an open visit in this department.");
        string? queue = null;
        if (dept.QueuePrefix is { } prefix)
        {
            long n = await Db.NextAsync($"queue:{dept.Id}:{Now:yyyyMMdd}", ct);
            queue = $"{prefix}-{n:D3}";
        }
        var visit = new Visit
        {
            PatientId = patientId, DepartmentId = departmentId, Kind = kind, Payer = payer, SepNumber = sepNumber, DoctorId = doctorId,
            Status = VisitStatus.Waiting, QueueNumber = queue, Start = Now, ChiefComplaint = complaint,
        };
        Db.Visits.Add(visit);
        await ChargeAsync(visit.Id, kind == VisitKind.Emergency ? "REG-IGD" : "REG", kind == VisitKind.Emergency ? "Pendaftaran IGD" : "Pendaftaran", ChargeCategory.Registration, 1, null, 50_000, ct);
        Db.AuditEvent(AuditAction.Create, "Visit", patientId, dept.Code);
        await Db.SaveChangesAsync(ct);
        Publish(new VisitStarted(TenantId, Now, visit.Id, departmentId, queue));
        return visit;
    }

    /// <summary>Today's queue of a clinic in call order.</summary>
    public async Task<IReadOnlyList<(Visit Visit, HisPatient Patient)>> QueueAsync(string departmentId, CancellationToken ct = default)
    {
        var today = Now.Date;
        var rows = await Db.Visits.Where(v => v.DepartmentId == departmentId && v.Start >= today && (v.Status == VisitStatus.Waiting || v.Status == VisitStatus.InConsultation))
            .Join(Db.Patients, v => v.PatientId, p => p.Id, (v, p) => new { v, p }).ToListAsync(ct);
        return rows.OrderByDescending(r => r.v.Status == VisitStatus.InConsultation).ThenBy(r => r.v.Kind == VisitKind.Emergency ? 0 : 1).ThenBy(r => r.v.Start)
            .Select(r => (r.v, r.p)).ToList();
    }
}

/// <summary>Outpatient consultation and inpatient care: SOAP notes, ICD-10 diagnoses, vital signs, admission, transfer and discharge.</summary>
public sealed class ClinicalService(HospitalDbContext db, TimeProvider? clock = null, IMedicalEventBus? bus = null, TerminologyService? terms = null)
    : HospitalServiceBase(db, clock, bus)
{
    private readonly TerminologyService _terms = terms ?? TerminologyService.Default;

    public async Task<Visit?> CallNextAsync(string departmentId, string doctorId, CancellationToken ct = default)
    {
        var queue = await new RegistrationService(Db, Clock, Bus).QueueAsync(departmentId, ct);
        if (queue.FirstOrDefault(q => q.Visit.Status == VisitStatus.Waiting) is not { Visit: { } next }) return null;
        next.Status = VisitStatus.InConsultation;
        next.DoctorId = doctorId;
        await Db.SaveChangesAsync(ct);
        return next;
    }

    public async Task SaveSoapAsync(string visitId, string? subjective, string? objective, string? assessment, string? plan, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        (v.Subjective, v.Objective, v.Assessment, v.Plan) = (subjective, objective, assessment, plan);
        Db.AuditEvent(AuditAction.Update, "Visit/soap", v.PatientId);
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>Adds an ICD-10 diagnosis (validated against the terminology); the first one becomes primary unless stated.</summary>
    public async Task<Diagnosis> AddDiagnosisAsync(string visitId, string icd10, bool? primary = null, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        string code = icd10.Trim().ToUpperInvariant();
        if (!CodeValidators.IsIcd10Format(code)) throw new ArgumentException($"'{icd10}' is not an ICD-10 code.", nameof(icd10));
        var concept = _terms.Lookup(CodeSystems.Icd10, code);
        bool isPrimary = primary ?? !await Db.Diagnoses.AnyAsync(d => d.VisitId == visitId && d.Primary, ct);
        if (isPrimary) foreach (var d in await Db.Diagnoses.Where(d => d.VisitId == visitId && d.Primary).ToListAsync(ct)) d.Primary = false;
        var dx = new Diagnosis { VisitId = v.Id, Icd10 = code, Display = concept?.Display ?? code, Primary = isPrimary };
        Db.Diagnoses.Add(dx);
        await Db.SaveChangesAsync(ct);
        return dx;
    }

    /// <summary>Charts vital signs with NEWS2. Device readings stay "pending" until validated by a nurse.</summary>
    public async Task<VitalsEntry> RecordVitalsAsync(string patientId, string? visitId, VitalSigns vs, VitalsSource source = VitalsSource.Manual,
        string? deviceId = null, string? recordedBy = null, CancellationToken ct = default)
    {
        var news = ClinicalScores.News2(vs);
        var e = new VitalsEntry
        {
            PatientId = patientId, VisitId = visitId, Time = vs.Timestamp.UtcDateTime, RespiratoryRate = vs.RespiratoryRate, SpO2 = vs.SpO2, OnOxygen = vs.OnSupplementalOxygen,
            SystolicBp = vs.SystolicBp, DiastolicBp = vs.DiastolicBp, HeartRate = vs.HeartRate, TemperatureC = vs.TemperatureC, Consciousness = vs.Consciousness,
            News2 = news.Score, Source = source, DeviceId = deviceId, Validated = source != VitalsSource.Device, RecordedBy = recordedBy ?? Db.Tenant.UserName,
        };
        Db.Vitals.Add(e);
        await Db.SaveChangesAsync(ct);
        if (visitId is not null && await Db.Visits.FirstOrDefaultAsync(v => v.Id == visitId, ct) is { Kind: VisitKind.Inpatient } iv)
            await RoundsSync.PublishPatientAsync(Db, iv, ct);
        Publish(new VitalsCharted(TenantId, Now, patientId, news.Score, source));
        return e;
    }

    public async Task ValidateVitalsAsync(string vitalsId, CancellationToken ct = default)
    {
        var v = await Find<VitalsEntry>(vitalsId, ct);
        v.Validated = true;
        v.RecordedBy = Db.Tenant.UserName;
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>Finishes an outpatient visit and charges the consultation (specialist or general).</summary>
    public async Task FinishVisitAsync(string visitId, CancellationToken ct = default)
    {
        var v = await Find<Visit>(visitId, ct);
        if (!await Db.Diagnoses.AnyAsync(d => d.VisitId == visitId, ct)) throw new InvalidOperationException("Record at least one diagnosis before finishing the visit.");
        var dept = await Find<Department>(v.DepartmentId, ct);
        v.Status = VisitStatus.Done;
        v.End = Now;
        string code = dept.Kind == DepartmentKind.Emergency ? "KONS-IGD" : dept.Code == "POLI-UMUM" ? "KONS-UMUM" : "KONS-SP";
        await ChargeAsync(v.Id, code, $"Konsultasi {dept.NameId}", ChargeCategory.Consultation, 1, null, 100_000, ct);
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>Admits from an outpatient / emergency visit to a free bed of the ward (optionally of a given class or a given bed).</summary>
    public async Task<Visit> AdmitAsync(string fromVisitId, string wardId, BedClass? bedClass = null, string? bedId = null, CancellationToken ct = default)
    {
        var from = await Find<Visit>(fromVisitId, ct);
        if (await Db.Visits.AnyAsync(v => v.PatientId == from.PatientId && v.Kind == VisitKind.Inpatient && v.Status == VisitStatus.Admitted, ct))
            throw new InvalidOperationException("The patient is already admitted.");
        var bed = bedId is not null ? await Find<Bed>(bedId, ct)
            : await Db.Beds.Where(b => b.WardId == wardId && b.Status == BedStatus.Free && (bedClass == null || b.Class == bedClass)).OrderBy(b => b.Code).FirstOrDefaultAsync(ct)
              ?? throw new InvalidOperationException("No free bed of that class in the ward.");
        if (bed.Status != BedStatus.Free) throw new InvalidOperationException($"Bed {bed.Code} is {bed.Status}.");
        if (from.Payer == PayerType.Bpjs && bed.Class == BedClass.Vip) throw new InvalidOperationException("JKN patients are admitted to class 1–3 (VIP is an upgrade the patient pays for).");
        var stay = new Visit
        {
            PatientId = from.PatientId, Kind = VisitKind.Inpatient, Status = VisitStatus.Admitted, DepartmentId = wardId, DoctorId = from.DoctorId,
            Payer = from.Payer, SepNumber = from.SepNumber, Start = Now, BedId = bed.Id, BedClass = bed.Class, ParentVisitId = from.Id, ChiefComplaint = from.ChiefComplaint,
            Assessment = from.Assessment,
        };
        Db.Visits.Add(stay);
        foreach (var d in await Db.Diagnoses.Where(d => d.VisitId == from.Id).ToListAsync(ct))
            Db.Diagnoses.Add(new Diagnosis { VisitId = stay.Id, Icd10 = d.Icd10, Display = d.Display, Primary = d.Primary });
        from.Status = VisitStatus.Admitted;
        from.End = Now;
        (bed.Status, bed.VisitId, bed.PatientId) = (BedStatus.Occupied, stay.Id, from.PatientId);
        Db.AuditEvent(AuditAction.Create, "Visit/admission", from.PatientId, bed.Code);
        await Db.SaveChangesAsync(ct);
        await RoundsSync.PublishPatientAsync(Db, stay, ct);
        Publish(new PatientAdmitted(TenantId, Now, stay.Id, stay.PatientId, bed.Id));
        return stay;
    }

    public async Task TransferAsync(string stayId, string newBedId, CancellationToken ct = default)
    {
        var stay = await Find<Visit>(stayId, ct);
        var to = await Find<Bed>(newBedId, ct);
        if (to.Status != BedStatus.Free) throw new InvalidOperationException($"Bed {to.Code} is {to.Status}.");
        if (stay.BedId is { } oldId && await Db.Beds.FirstOrDefaultAsync(b => b.Id == oldId, ct) is { } old)
        {
            if (old.WardId != to.WardId) await RoundsSync.RemovePatientAsync(Db, stay, old.WardId, ct);
            (old.Status, old.VisitId, old.PatientId) = (BedStatus.Cleaning, null, null);
        }
        (to.Status, to.VisitId, to.PatientId) = (BedStatus.Occupied, stay.Id, stay.PatientId);
        (stay.BedId, stay.DepartmentId) = (to.Id, to.WardId);
        await Db.SaveChangesAsync(ct);
        await RoundsSync.PublishPatientAsync(Db, stay, ct);
    }

    /// <summary>Discharges an inpatient: room and nursing charges per calendar day (minimum one), bed to cleaning, discharge summary.</summary>
    public async Task<ClinicalDocument> DischargeAsync(string stayId, DischargeType type, string? instructions = null, CancellationToken ct = default)
    {
        var stay = await Find<Visit>(stayId, ct);
        if (stay.Status != VisitStatus.Admitted) throw new InvalidOperationException("The visit is not an active admission.");
        if (!await Db.Diagnoses.AnyAsync(d => d.VisitId == stayId, ct)) throw new InvalidOperationException("Record a discharge diagnosis first.");
        stay.Status = VisitStatus.Discharged;
        stay.End = Now;
        stay.Discharge = type;
        int days = Math.Max(1, (stay.End.Value.Date - stay.Start.Date).Days);
        string cls = (stay.BedClass ?? BedClass.Kelas3).ToString().ToUpperInvariant();
        await ChargeAsync(stay.Id, $"KAMAR-{cls}", $"Kamar {cls} × {days} hari", ChargeCategory.Room, days, null, 250_000, ct);
        await ChargeAsync(stay.Id, "PERAWATAN", $"Asuhan keperawatan × {days} hari", ChargeCategory.Nursing, days, null, 100_000, ct);
        if (stay.BedId is { } bedId && await Db.Beds.FirstOrDefaultAsync(b => b.Id == bedId, ct) is { } bed)
        {
            await RoundsSync.RemovePatientAsync(Db, stay, bed.WardId, ct);
            (bed.Status, bed.VisitId, bed.PatientId) = (BedStatus.Cleaning, null, null);
        }
        foreach (var t in await Db.Tasks.Where(t => t.VisitId == stayId && t.State == WardTaskState.Open).ToListAsync(ct)) t.State = WardTaskState.Cancelled;
        Db.AuditEvent(AuditAction.Update, "Visit/discharge", stay.PatientId, type.ToString());
        await Db.SaveChangesAsync(ct);
        Publish(new PatientDischarged(TenantId, Now, stay.Id, stay.PatientId, type));
        return await DischargeSummaryAsync(stay, instructions, ct);
    }

    public async Task MarkBedCleanAsync(string bedId, CancellationToken ct = default)
    {
        var bed = await Find<Bed>(bedId, ct);
        if (bed.Status == BedStatus.Cleaning) bed.Status = BedStatus.Free;
        await Db.SaveChangesAsync(ct);
    }

    private async Task<ClinicalDocument> DischargeSummaryAsync(Visit stay, string? instructions, CancellationToken ct)
    {
        var p = await Find<HisPatient>(stay.PatientId, ct);
        var dx = await Db.Diagnoses.Where(d => d.VisitId == stay.Id).OrderByDescending(d => d.Primary).ToListAsync(ct);
        var meds = await Db.Prescriptions.Where(r => r.VisitId == stay.Id && r.State == PrescriptionState.Dispensed).SelectMany(r => r.Items).ToListAsync(ct);
        var labs = await Db.LabOrders.Where(o => o.VisitId == stay.Id).SelectMany(o => o.Results).Where(r => r.Flag != ResultFlag.Normal).ToListAsync(ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"RINGKASAN PULANG / DISCHARGE SUMMARY — {p.FullName} (RM {p.Mrn})");
        sb.AppendLine($"Dirawat {stay.Start:dd-MM-yyyy} s.d. {stay.End:dd-MM-yyyy} · cara pulang: {stay.Discharge}");
        sb.AppendLine("Diagnosis: " + string.Join("; ", dx.Select(d => $"{d.Icd10} {d.Display}{(d.Primary ? " (utama)" : "")}")));
        if (stay.Assessment is { Length: > 0 } a) sb.AppendLine("Asesmen: " + a);
        if (labs.Count > 0) sb.AppendLine("Lab abnormal: " + string.Join("; ", labs.Select(l => $"{l.Display} {l.Value?.ToString("0.##", CultureInfo.InvariantCulture) ?? l.Text} {l.Unit} [{l.Flag}]")));
        if (meds.Count > 0) sb.AppendLine("Obat: " + string.Join("; ", meds.Select(m => $"{m.Generic} {m.DoseMg:0.#} mg {m.FrequencyPerDay:0}×/hari")));
        if (instructions is { Length: > 0 } i) sb.AppendLine("Instruksi: " + i);
        return new ClinicalDocument
        {
            PatientId = p.Id, EncounterId = stay.Id, Type = ClinicalCode.Loinc("18842-5", "Discharge summary"), Title = "Ringkasan pulang",
            Author = Db.Tenant.UserName, Created = new DateTimeOffset(Now, TimeSpan.Zero), Text = sb.ToString(),
        };
    }
}
