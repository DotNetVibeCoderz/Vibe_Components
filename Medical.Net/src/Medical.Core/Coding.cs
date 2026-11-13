namespace Medical.Core;

/// <summary>Canonical code-system URIs (FHIR-compatible).</summary>
public static class CodeSystems
{
    public const string Snomed = "http://snomed.info/sct";
    public const string Loinc = "http://loinc.org";
    public const string Icd10 = "http://hl7.org/fhir/sid/icd-10";
    public const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";
    public const string Ucum = "http://unitsofmeasure.org";
    public const string Atc = "http://www.whocc.no/atc";
    public const string Kfa = "http://sys-ids.kemkes.go.id/kfa";
    public const string Nik = "https://fhir.kemkes.go.id/id/nik";

    /// <summary>Short display name for a code-system URI (SNOMED CT, LOINC, ICD-10…).</summary>
    public static string ShortName(string system) => system switch
    {
        Snomed => "SNOMED CT",
        Loinc => "LOINC",
        Icd10 => "ICD-10",
        Icd10Cm => "ICD-10-CM",
        RxNorm => "RxNorm",
        Ucum => "UCUM",
        Atc => "ATC",
        Kfa => "KFA",
        _ => system,
    };
}

/// <summary>A code from a terminology (system + code + optional display), equivalent to FHIR <c>Coding</c>.</summary>
public readonly record struct ClinicalCode(string System, string Code, string? Display = null)
{
    public static ClinicalCode Snomed(string code, string? display = null) => new(CodeSystems.Snomed, code, display);
    public static ClinicalCode Loinc(string code, string? display = null) => new(CodeSystems.Loinc, code, display);
    public static ClinicalCode Icd10(string code, string? display = null) => new(CodeSystems.Icd10, code, display);
    public static ClinicalCode RxNorm(string code, string? display = null) => new(CodeSystems.RxNorm, code, display);

    /// <summary>True when system and code match (display is ignored).</summary>
    public bool Matches(ClinicalCode other) => System == other.System && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Display is null ? $"{CodeSystems.ShortName(System)} {Code}" : $"{CodeSystems.ShortName(System)} {Code} — {Display}";
}

/// <summary>Well-known LOINC codes used across Medical.Net (vital signs and common labs).</summary>
public static class Loinc
{
    public static readonly ClinicalCode VitalSignsPanel = ClinicalCode.Loinc("85353-1", "Vital signs, weight, height, head circumference, oxygen saturation and BMI panel");
    public static readonly ClinicalCode HeartRate = ClinicalCode.Loinc("8867-4", "Heart rate");
    public static readonly ClinicalCode RespiratoryRate = ClinicalCode.Loinc("9279-1", "Respiratory rate");
    public static readonly ClinicalCode BloodPressurePanel = ClinicalCode.Loinc("85354-9", "Blood pressure panel with all children optional");
    public static readonly ClinicalCode SystolicBp = ClinicalCode.Loinc("8480-6", "Systolic blood pressure");
    public static readonly ClinicalCode DiastolicBp = ClinicalCode.Loinc("8462-4", "Diastolic blood pressure");
    public static readonly ClinicalCode BodyTemperature = ClinicalCode.Loinc("8310-5", "Body temperature");
    public static readonly ClinicalCode OxygenSaturation = ClinicalCode.Loinc("59408-5", "Oxygen saturation in Arterial blood by Pulse oximetry");
    public static readonly ClinicalCode BodyWeight = ClinicalCode.Loinc("29463-7", "Body weight");
    public static readonly ClinicalCode BodyHeight = ClinicalCode.Loinc("8302-2", "Body height");
    public static readonly ClinicalCode Bmi = ClinicalCode.Loinc("39156-5", "Body mass index (BMI) [Ratio]");
    public static readonly ClinicalCode Glucose = ClinicalCode.Loinc("2345-7", "Glucose [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode HbA1c = ClinicalCode.Loinc("4548-4", "Hemoglobin A1c/Hemoglobin.total in Blood");
    public static readonly ClinicalCode Creatinine = ClinicalCode.Loinc("2160-0", "Creatinine [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode Hemoglobin = ClinicalCode.Loinc("718-7", "Hemoglobin [Mass/volume] in Blood");
    public static readonly ClinicalCode Wbc = ClinicalCode.Loinc("6690-2", "Leukocytes [#/volume] in Blood by Automated count");
    public static readonly ClinicalCode Platelets = ClinicalCode.Loinc("777-3", "Platelets [#/volume] in Blood by Automated count");
    public static readonly ClinicalCode Potassium = ClinicalCode.Loinc("2823-3", "Potassium [Moles/volume] in Serum or Plasma");
    public static readonly ClinicalCode Sodium = ClinicalCode.Loinc("2951-2", "Sodium [Moles/volume] in Serum or Plasma");
    public static readonly ClinicalCode Lactate = ClinicalCode.Loinc("2524-7", "Lactate [Moles/volume] in Serum or Plasma");
    public static readonly ClinicalCode Bilirubin = ClinicalCode.Loinc("1975-2", "Bilirubin.total [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode Inr = ClinicalCode.Loinc("6301-6", "INR in Platelet poor plasma by Coagulation assay");
    public static readonly ClinicalCode Albumin = ClinicalCode.Loinc("1751-7", "Albumin [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode Cholesterol = ClinicalCode.Loinc("2093-3", "Cholesterol [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode Ldl = ClinicalCode.Loinc("13457-7", "Cholesterol in LDL [Mass/volume] in Serum or Plasma by calculation");
    public static readonly ClinicalCode Hdl = ClinicalCode.Loinc("2085-9", "Cholesterol in HDL [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode Triglycerides = ClinicalCode.Loinc("2571-8", "Triglyceride [Mass/volume] in Serum or Plasma");
    public static readonly ClinicalCode Alt = ClinicalCode.Loinc("1742-6", "Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma");
    public static readonly ClinicalCode Crp = ClinicalCode.Loinc("1988-5", "C reactive protein [Mass/volume] in Serum or Plasma");
}
