namespace Medical.Core;

/// <summary>Administrative gender (FHIR value set <c>administrative-gender</c>).</summary>
public enum Gender { Unknown, Male, Female, Other }

/// <summary>AVPU consciousness scale used by NEWS2 (with "C" for new confusion).</summary>
public enum Consciousness { Alert, Confusion, Voice, Pain, Unresponsive }

/// <summary>A patient's name.</summary>
public sealed record HumanName(string Given, string Family, string? Prefix = null)
{
    public string Full => string.Join(' ', new[] { Prefix, Given, Family }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public override string ToString() => Full;
}

/// <summary>Demographic record of a patient (the subject of all clinical data).</summary>
public sealed record Patient
{
    public required string Id { get; init; }
    /// <summary>Medical record number assigned by the facility.</summary>
    public required string Mrn { get; init; }
    public required HumanName Name { get; init; }
    public Gender Gender { get; init; }
    public DateOnly BirthDate { get; init; }
    /// <summary>Indonesian national identity number (NIK, 16 digits) — used by SATUSEHAT.</summary>
    public string? Nik { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }

    public int AgeAt(DateOnly date)
    {
        int age = date.Year - BirthDate.Year;
        if (date < BirthDate.AddYears(age)) age--;
        return Math.Max(0, age);
    }

    public int Age => AgeAt(DateOnly.FromDateTime(DateTime.UtcNow));
}

public enum EncounterClass { Ambulatory, Emergency, Inpatient, Virtual, Home }

public enum EncounterStatus { Planned, Arrived, InProgress, Finished, Cancelled }

/// <summary>An interaction between patient and provider (visit, admission, teleconsult).</summary>
public sealed record Encounter
{
    public required string Id { get; init; }
    public required string PatientId { get; init; }
    public EncounterClass Class { get; init; } = EncounterClass.Ambulatory;
    public EncounterStatus Status { get; init; } = EncounterStatus.InProgress;
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? Practitioner { get; init; }
    public string? Location { get; init; }
    public ClinicalCode? Reason { get; init; }
}

/// <summary>One set of bedside vital signs (inputs for NEWS2, qSOFA, MEWS…).</summary>
public sealed record VitalSigns
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Breaths per minute.</summary>
    public double RespiratoryRate { get; init; }
    /// <summary>Peripheral oxygen saturation, %.</summary>
    public double SpO2 { get; init; }
    public bool OnSupplementalOxygen { get; init; }
    /// <summary>Systolic blood pressure, mmHg.</summary>
    public double SystolicBp { get; init; }
    /// <summary>Diastolic blood pressure, mmHg.</summary>
    public double DiastolicBp { get; init; }
    /// <summary>Beats per minute.</summary>
    public double HeartRate { get; init; }
    /// <summary>Body temperature, °C.</summary>
    public double TemperatureC { get; init; }
    public Consciousness Consciousness { get; init; } = Consciousness.Alert;

    public double MeanArterialPressure => (SystolicBp + 2 * DiastolicBp) / 3;
}

public enum ResultFlag { Normal, Low, High, CriticalLow, CriticalHigh, Abnormal }

public enum ObservationStatus { Registered, Preliminary, Final, Amended, Cancelled }

/// <summary>A single coded measurement or lab result.</summary>
public sealed record ClinicalObservation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public string? EncounterId { get; init; }
    public required ClinicalCode Code { get; init; }
    public double? Value { get; init; }
    public string? Unit { get; init; }
    public string? ValueText { get; init; }
    public double? ReferenceLow { get; init; }
    public double? ReferenceHigh { get; init; }
    public ResultFlag Flag { get; init; }
    public ObservationStatus Status { get; init; } = ObservationStatus.Final;
    public DateTimeOffset Effective { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString() => $"{Code.Display ?? Code.Code}: {(Value is { } v ? $"{v:0.##} {Ucum.Display(Unit ?? "")}" : ValueText)} [{Flag}]";
}

public enum ConditionStatus { Active, Recurrence, Remission, Resolved }

/// <summary>A problem/diagnosis on the patient's problem list.</summary>
public sealed record Condition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public required ClinicalCode Code { get; init; }
    public ConditionStatus Status { get; init; } = ConditionStatus.Active;
    public DateOnly? Onset { get; init; }
}

/// <summary>A medication order / prescription.</summary>
public sealed record MedicationOrder
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public required ClinicalCode Medication { get; init; }
    /// <summary>Lower-case generic (INN) name used for interaction checks, e.g. "warfarin".</summary>
    public required string Generic { get; init; }
    public double DoseMg { get; init; }
    public string Route { get; init; } = "oral";
    /// <summary>Administrations per day.</summary>
    public double FrequencyPerDay { get; init; } = 1;
    public DateOnly Start { get; init; }
    public string? Instructions { get; init; }
    public double DailyDoseMg => DoseMg * FrequencyPerDay;
}

public enum AllergySeverity { Mild, Moderate, Severe }

/// <summary>Allergy or intolerance (substance is a lower-case generic name or class, e.g. "penicillin").</summary>
public sealed record Allergy
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string PatientId { get; init; }
    public required string Substance { get; init; }
    public string? Reaction { get; init; }
    public AllergySeverity Severity { get; init; } = AllergySeverity.Moderate;
}

/// <summary>Everything Medical.Net knows about one patient at a point in time (input for rules, AI and FHIR export).</summary>
public sealed record PatientRecord
{
    public required Patient Patient { get; init; }
    public IReadOnlyList<Encounter> Encounters { get; init; } = [];
    public IReadOnlyList<VitalSigns> Vitals { get; init; } = [];
    public IReadOnlyList<ClinicalObservation> Observations { get; init; } = [];
    public IReadOnlyList<Condition> Conditions { get; init; } = [];
    public IReadOnlyList<MedicationOrder> Medications { get; init; } = [];
    public IReadOnlyList<Allergy> Allergies { get; init; } = [];
    public double? WeightKg { get; init; }
    public double? HeightCm { get; init; }

    public VitalSigns? LatestVitals => Vitals.Count == 0 ? null : Vitals.MaxBy(v => v.Timestamp);

    /// <summary>Most recent observation with the given code, if any.</summary>
    public ClinicalObservation? Latest(ClinicalCode code) =>
        Observations.Where(o => o.Code.Matches(code)).MaxBy(o => o.Effective);

    public bool HasCondition(string icd10Prefix) =>
        Conditions.Any(c => c.Status is ConditionStatus.Active or ConditionStatus.Recurrence &&
                            c.Code.System is CodeSystems.Icd10 or CodeSystems.Icd10Cm &&
                            c.Code.Code.StartsWith(icd10Prefix, StringComparison.OrdinalIgnoreCase));
}
