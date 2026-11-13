using Medical.Core;

namespace Medical.Emr.Entities;

// Persistence model. Kept separate from the immutable domain records so the schema can evolve independently.

public sealed class PatientEntity
{
    public string Id { get; set; } = "";
    public string Mrn { get; set; } = "";
    public string? Nik { get; set; }
    public string Given { get; set; } = "";
    public string Family { get; set; } = "";
    public Gender Gender { get; set; }
    public DateOnly BirthDate { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public double? WeightKg { get; set; }
    public double? HeightCm { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<EncounterEntity> Encounters { get; set; } = [];
    public List<VitalsEntity> Vitals { get; set; } = [];
    public List<ObservationEntity> Observations { get; set; } = [];
    public List<ConditionEntity> Conditions { get; set; } = [];
    public List<MedicationEntity> Medications { get; set; } = [];
    public List<AllergyEntity> Allergies { get; set; } = [];
}

public sealed class EncounterEntity
{
    public string Id { get; set; } = "";
    public string PatientId { get; set; } = "";
    public EncounterClass Class { get; set; }
    public EncounterStatus Status { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string? Practitioner { get; set; }
    public string? Location { get; set; }
    public string? ReasonSystem { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonDisplay { get; set; }
}

public sealed class VitalsEntity
{
    public long Id { get; set; }
    public string PatientId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public double RespiratoryRate { get; set; }
    public double SpO2 { get; set; }
    public bool OnSupplementalOxygen { get; set; }
    public double SystolicBp { get; set; }
    public double DiastolicBp { get; set; }
    public double HeartRate { get; set; }
    public double TemperatureC { get; set; }
    public Consciousness Consciousness { get; set; }
}

public sealed class ObservationEntity
{
    public string Id { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string? EncounterId { get; set; }
    public string System { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Display { get; set; }
    public double? Value { get; set; }
    public string? Unit { get; set; }
    public string? ValueText { get; set; }
    public double? ReferenceLow { get; set; }
    public double? ReferenceHigh { get; set; }
    public ResultFlag Flag { get; set; }
    public ObservationStatus Status { get; set; }
    public DateTimeOffset Effective { get; set; }
}

public sealed class ConditionEntity
{
    public string Id { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string System { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Display { get; set; }
    public ConditionStatus Status { get; set; }
    public DateOnly? Onset { get; set; }
}

public sealed class MedicationEntity
{
    public string Id { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string System { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Display { get; set; }
    public string Generic { get; set; } = "";
    public double DoseMg { get; set; }
    public string Route { get; set; } = "oral";
    public double FrequencyPerDay { get; set; }
    public DateOnly Start { get; set; }
    public string? Instructions { get; set; }
}

public sealed class AllergyEntity
{
    public string Id { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string Substance { get; set; } = "";
    public string? Reaction { get; set; }
    public AllergySeverity Severity { get; set; }
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string User { get; set; } = "";
    public AuditAction Action { get; set; }
    public string Resource { get; set; } = "";
    public string? PatientId { get; set; }
    public string? Purpose { get; set; }
    public bool Success { get; set; }
}
