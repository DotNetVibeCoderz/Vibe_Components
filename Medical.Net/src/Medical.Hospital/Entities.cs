using Medical.Core;

namespace Medical.Hospital;

/// <summary>Everything a tenant owns carries its tenant id; the context filters and stamps it.</summary>
public abstract class TenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string TenantId { get; set; } = "";
}

/// <summary>Hospital class under Indonesian regulations (RS kelas A–D) or a primary-care clinic.</summary>
public enum FacilityClass { A, B, C, D, Puskesmas, Klinik }

/// <summary>A hospital (tenant). Data of one tenant is never visible to another.</summary>
public sealed class Tenant
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public FacilityClass Class { get; set; } = FacilityClass.C;
    /// <summary>SATUSEHAT organisation id, when registered.</summary>
    public string? SatuSehatOrgId { get; set; }
    /// <summary>INA-CBG tariff class code (e.g. "CP" for class C private).</summary>
    public string KodeTarif { get; set; } = "CP";
    public bool Active { get; set; } = true;
}

public enum DepartmentKind { Outpatient, Emergency, Ward, Icu, Laboratory, Radiology, Pharmacy, Cashier }

public sealed class Department : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameId { get; set; } = "";
    public DepartmentKind Kind { get; set; }
    /// <summary>Queue prefix for outpatient clinics (A, B, C…).</summary>
    public string? QueuePrefix { get; set; }
}

/// <summary>Room class (JKN): kelas 1–3, VIP or ICU.</summary>
public enum BedClass { Vip, Kelas1, Kelas2, Kelas3, Icu }

public enum BedStatus { Free, Occupied, Cleaning, Blocked }

public sealed class Bed : TenantEntity
{
    public string WardId { get; set; } = "";
    public string Code { get; set; } = "";
    public BedClass Class { get; set; } = BedClass.Kelas3;
    public BedStatus Status { get; set; } = BedStatus.Free;
    public string? VisitId { get; set; }
    public string? PatientId { get; set; }
    /// <summary>Bedside monitor bound to this bed (device id), for live vital signs.</summary>
    public string? MonitorId { get; set; }
}

public enum StaffRole { Admin, Registration, Doctor, Nurse, Pharmacist, LabAnalyst, Radiographer, Radiologist, Cashier, Coder }

public sealed class Staff : TenantEntity
{
    public string UserName { get; set; } = "";
    public string Name { get; set; } = "";
    public StaffRole Role { get; set; }
    public string? DepartmentId { get; set; }
    public string? Specialty { get; set; }
    /// <summary>Practice licence (SIP) for clinicians.</summary>
    public string? Licence { get; set; }
    public string? Nik { get; set; }
}

public enum PayerType { Umum, Bpjs, Insurance }

public sealed class HisPatient : TenantEntity
{
    public string Mrn { get; set; } = "";
    public string Given { get; set; } = "";
    public string Family { get; set; } = "";
    public Gender Gender { get; set; }
    public DateOnly BirthDate { get; set; }
    public string? Nik { get; set; }
    public string? BpjsNumber { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Allergies { get; set; }
    public DateTime RegisteredAt { get; set; }
    public string FullName => $"{Given} {Family}".Trim();
}

public enum VisitKind { Outpatient, Emergency, Inpatient }

public enum VisitStatus { Registered, Waiting, InConsultation, Done, Admitted, Discharged, Cancelled }

public enum DischargeType { Home, Referred, AgainstAdvice, Died, Transferred }

/// <summary>A visit / encounter: outpatient consultation, emergency attendance or inpatient stay.</summary>
public sealed class Visit : TenantEntity
{
    public string PatientId { get; set; } = "";
    public VisitKind Kind { get; set; }
    public VisitStatus Status { get; set; } = VisitStatus.Registered;
    public string DepartmentId { get; set; } = "";
    public string? DoctorId { get; set; }
    public PayerType Payer { get; set; }
    /// <summary>BPJS SEP number for JKN visits.</summary>
    public string? SepNumber { get; set; }
    public string? QueueNumber { get; set; }
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public string? ChiefComplaint { get; set; }
    public string? Subjective { get; set; }
    public string? Objective { get; set; }
    public string? Assessment { get; set; }
    public string? Plan { get; set; }
    public string? BedId { get; set; }
    public BedClass? BedClass { get; set; }
    public DischargeType? Discharge { get; set; }
    public string? ParentVisitId { get; set; }
}

public sealed class Diagnosis : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string Icd10 { get; set; } = "";
    public string Display { get; set; } = "";
    public bool Primary { get; set; }
}

/// <summary>Where a set of vital signs came from.</summary>
public enum VitalsSource { Manual, Device, MobileApp }

public sealed class VitalsEntry : TenantEntity
{
    public string PatientId { get; set; } = "";
    public string? VisitId { get; set; }
    public DateTime Time { get; set; }
    public double RespiratoryRate { get; set; }
    public double SpO2 { get; set; }
    public bool OnOxygen { get; set; }
    public double SystolicBp { get; set; }
    public double DiastolicBp { get; set; }
    public double HeartRate { get; set; }
    public double TemperatureC { get; set; }
    public Consciousness Consciousness { get; set; }
    public int News2 { get; set; }
    public VitalsSource Source { get; set; }
    public string? DeviceId { get; set; }
    /// <summary>Device readings are charted as pending until a nurse validates them.</summary>
    public bool Validated { get; set; } = true;
    public string? RecordedBy { get; set; }

    public VitalSigns ToVitalSigns() => new()
    {
        Timestamp = new DateTimeOffset(DateTime.SpecifyKind(Time, DateTimeKind.Utc)), RespiratoryRate = RespiratoryRate, SpO2 = SpO2, OnSupplementalOxygen = OnOxygen,
        SystolicBp = SystolicBp, DiastolicBp = DiastolicBp, HeartRate = HeartRate, TemperatureC = TemperatureC, Consciousness = Consciousness,
    };
}

public enum OrderState { Ordered, Collected, Scheduled, InProgress, Resulted, Verified, Reported, Cancelled }

public sealed class LabOrder : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string PlacerNumber { get; set; } = "";
    public string? SpecimenBarcode { get; set; }
    public OrderPriority Priority { get; set; }
    public OrderState State { get; set; }
    public string? OrderedBy { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTime? CollectedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
    public List<LabResult> Results { get; set; } = [];
}

public sealed class LabResult : TenantEntity
{
    public string LabOrderId { get; set; } = "";
    public string Loinc { get; set; } = "";
    public string Display { get; set; } = "";
    public double? Value { get; set; }
    public string? Unit { get; set; }
    public string? Text { get; set; }
    public double? RefLow { get; set; }
    public double? RefHigh { get; set; }
    public ResultFlag Flag { get; set; }
    public bool Critical { get; set; }
    public DateTime? ResultedAt { get; set; }
    /// <summary>When the critical value was communicated to a clinician (read-back), as accreditation standards require.</summary>
    public DateTime? CriticalNotifiedAt { get; set; }
    public string? CriticalNotifiedTo { get; set; }
}

public sealed class RadiologyOrder : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string Procedure { get; set; } = "";
    public string ProcedureCode { get; set; } = "";
    public string Modality { get; set; } = "CR";
    public string AccessionNumber { get; set; } = "";
    public OrderPriority Priority { get; set; }
    public OrderState State { get; set; }
    public string? ClinicalInfo { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? PerformedAt { get; set; }
    public string? StudyInstanceUid { get; set; }
    public string? Findings { get; set; }
    public string? Impression { get; set; }
    public string? Radiologist { get; set; }
    public DateTime? ReportedAt { get; set; }
}

public enum PrescriptionState { Ordered, Verified, Dispensed, Rejected, Cancelled }

public sealed class Prescription : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string? PrescriberId { get; set; }
    public PrescriptionState State { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTime? DispensedAt { get; set; }
    /// <summary>Pharmacist review findings (interactions, allergies, dosing), one per line.</summary>
    public string? Review { get; set; }
    public List<PrescriptionItem> Items { get; set; } = [];
}

public sealed class PrescriptionItem : TenantEntity
{
    public string PrescriptionId { get; set; } = "";
    public string Generic { get; set; } = "";
    public double DoseMg { get; set; }
    public double FrequencyPerDay { get; set; } = 1;
    public int Days { get; set; } = 1;
    public string Route { get; set; } = "oral";
    public int Quantity { get; set; }
    public string? Instructions { get; set; }
}

/// <summary>A batch of a drug in stock (first-expiry-first-out dispensing).</summary>
public sealed class StockBatch : TenantEntity
{
    public string Generic { get; set; } = "";
    public string Batch { get; set; } = "";
    public DateOnly Expiry { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public int ReorderLevel { get; set; } = 50;
}

public enum ChargeCategory { Registration, Consultation, Room, Nursing, Drug, Laboratory, Radiology, Procedure, Administration }

public sealed class Tariff : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public ChargeCategory Category { get; set; }
    public decimal Price { get; set; }
}

public sealed class Charge : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public ChargeCategory Category { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public DateTime At { get; set; }
    public string? InvoiceId { get; set; }
    public decimal Amount => Quantity * UnitPrice;
}

public enum InvoiceState { Draft, Issued, Paid, Void }

public sealed class Invoice : TenantEntity
{
    public string VisitId { get; set; } = "";
    public string Number { get; set; } = "";
    public PayerType Payer { get; set; }
    public decimal Total { get; set; }
    /// <summary>Amount claimed from BPJS / the insurer.</summary>
    public decimal PayerAmount { get; set; }
    /// <summary>Amount the patient pays (cost sharing, room upgrade, or everything for Umum).</summary>
    public decimal PatientAmount { get; set; }
    public decimal Paid { get; set; }
    public InvoiceState State { get; set; }
    public DateTime IssuedAt { get; set; }
}

public sealed class Payment : TenantEntity
{
    public string InvoiceId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Method { get; set; } = "cash";
    public DateTime At { get; set; }
    public string? Cashier { get; set; }
}

public enum WardTaskState { Open, Done, Cancelled }

/// <summary>A nursing / ward-round task (synchronised to the mobile app).</summary>
public sealed class WardTask : TenantEntity
{
    public string PatientId { get; set; } = "";
    public string? VisitId { get; set; }
    public string WardId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Due { get; set; }
    public WardTaskState State { get; set; }
    public string? DoneBy { get; set; }
    public DateTime? DoneAt { get; set; }
}

public sealed class NursingNote : TenantEntity
{
    public string PatientId { get; set; } = "";
    public string? VisitId { get; set; }
    public DateTime Time { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Per-tenant counters for MRNs, queue numbers, invoices and accession numbers.</summary>
public sealed class Counter : TenantEntity
{
    public string Key { get; set; } = "";
    public long Value { get; set; }
}

/// <summary>Sync outbox / log: one row per record version exchanged with offline clients.</summary>
public sealed class SyncLogEntry
{
    public long Seq { get; set; }
    public string TenantId { get; set; } = "";
    public string Type { get; set; } = "";
    public string RecordId { get; set; } = "";
    public long Hlc { get; set; }
    public string NodeId { get; set; } = "";
    public string? Data { get; set; }
    public bool Deleted { get; set; }
    /// <summary>Partition clients subscribe to (e.g. the ward id).</summary>
    public string? Scope { get; set; }
    public bool Latest { get; set; } = true;
}

/// <summary>Audit trail entry per tenant.</summary>
public sealed class HisAudit
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public DateTime At { get; set; }
    public string User { get; set; } = "";
    public AuditAction Action { get; set; }
    public string Resource { get; set; } = "";
    public string? PatientId { get; set; }
    public string? Detail { get; set; }
}
