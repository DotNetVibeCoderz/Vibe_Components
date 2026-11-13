using Medical.Core;

namespace Medical.Rules;

public enum AlertSeverity { Info, Warning, Critical }

public enum AlertCategory { EarlyWarning, Sepsis, Laboratory, DrugInteraction, Allergy, Dosing, DuplicateTherapy, Renal }

/// <summary>A decision-support alert raised by a rule, with bilingual title/detail and the evidence that triggered it.</summary>
public sealed record ClinicalAlert(
    string PatientId,
    AlertSeverity Severity,
    AlertCategory Category,
    string Rule,
    Localized Title,
    Localized Detail,
    string Evidence)
{
    /// <summary>Stable key used to de-duplicate alerts raised by different paths.</summary>
    public string Key => $"{PatientId}|{Rule}|{Evidence}";

    public override string ToString() => $"[{Severity}] {Title.En} — {Detail.En} ({Evidence})";
}

/// <summary>Interaction severity used by the drug knowledge base.</summary>
public enum InteractionSeverity { Moderate, Major, Contraindicated }

/// <summary>One drug–drug interaction rule; sides match a generic name or an RxNorm class tag.</summary>
public sealed record DrugInteraction(string A, string B, InteractionSeverity Severity, Localized Effect);

/// <summary>Allergy cross-reactivity rule: an allergy to <paramref name="Allergen"/> (generic or class) conflicts with drugs of <paramref name="DrugClass"/>.</summary>
public sealed record AllergyCrossReactivity(string Allergen, string DrugClass, InteractionSeverity Severity, Localized Effect);
