using Medical.Core;

namespace Medical.Clinical;

/// <summary>Weight-based dosing rule for one drug (all doses in mg).</summary>
public sealed record DosingRule(
    string Generic,
    double MgPerKgPerDose,
    double DosesPerDay,
    double MaxSingleDoseMg,
    double MaxDailyMg,
    Localized Note,
    double? RenalAdjustBelowCrCl = null,
    double RenalDoseFactor = 1.0);

public sealed record DoseRecommendation(string Generic, double SingleDoseMg, double DosesPerDay, double DailyDoseMg, bool Capped, bool RenalAdjusted, Localized Note)
{
    public override string ToString() => $"{Generic}: {SingleDoseMg:0.#} mg × {DosesPerDay:0}/day = {DailyDoseMg:0.#} mg/day{(Capped ? " (capped)" : "")}{(RenalAdjusted ? " (renal)" : "")}";
}

/// <summary>
/// Weight-based dose calculator with single-dose and daily caps and optional renal adjustment (Cockcroft-Gault CrCl).
/// The built-in table is illustrative — always verify against the local formulary.
/// </summary>
public static class DoseCalculator
{
    public static IReadOnlyDictionary<string, DosingRule> Rules { get; } = new Dictionary<string, DosingRule>(StringComparer.OrdinalIgnoreCase)
    {
        ["paracetamol"] = new("paracetamol", 15, 4, 1000, 4000, new("Every 6 h; max 75 mg/kg/day.", "Tiap 6 jam; maks 75 mg/kgBB/hari.")),
        ["ibuprofen"] = new("ibuprofen", 10, 3, 400, 1200, new("Every 8 h with food; avoid in dehydration / renal impairment.", "Tiap 8 jam bersama makanan; hindari pada dehidrasi / gangguan ginjal."), 30, 0),
        ["amoxicillin"] = new("amoxicillin", 25, 2, 1000, 3000, new("Every 12 h (high-dose 45 mg/kg/day regimen).", "Tiap 12 jam (regimen dosis tinggi 45 mg/kgBB/hari)."), 30, 0.5),
        ["ceftriaxone"] = new("ceftriaxone", 50, 1, 2000, 4000, new("Once daily IV/IM.", "Sekali sehari IV/IM.")),
        ["gentamicin"] = new("gentamicin", 5, 1, 560, 560, new("Once-daily dosing; monitor trough levels.", "Dosis sekali sehari; pantau kadar trough."), 60, 0.5),
        ["ondansetron"] = new("ondansetron", 0.15, 3, 8, 24, new("Every 8 h.", "Tiap 8 jam.")),
        ["morphine"] = new("morphine", 0.1, 6, 10, 60, new("IV every 4 h titrated to effect; monitor respiration.", "IV tiap 4 jam dititrasi; pantau pernapasan."), 30, 0.5),
    };

    /// <summary>Calculates a dose for <paramref name="generic"/> given body weight and (optionally) creatinine clearance in mL/min.</summary>
    public static DoseRecommendation Calculate(string generic, double weightKg, double? crClMlMin = null)
    {
        Guard.Positive(weightKg, nameof(weightKg));
        if (!Rules.TryGetValue(generic, out var rule)) throw new KeyNotFoundException($"No dosing rule for '{generic}'.");
        double single = rule.MgPerKgPerDose * weightKg;
        bool capped = false, renal = false;
        if (single > rule.MaxSingleDoseMg) { single = rule.MaxSingleDoseMg; capped = true; }
        if (single * rule.DosesPerDay > rule.MaxDailyMg) { single = rule.MaxDailyMg / rule.DosesPerDay; capped = true; }
        if (rule.RenalAdjustBelowCrCl is { } limit && crClMlMin < limit)
        {
            renal = true;
            single *= rule.RenalDoseFactor;
        }
        single = Math.Round(single, single < 10 ? 2 : 0);
        var note = renal && rule.RenalDoseFactor == 0
            ? new Localized($"Avoid: CrCl {crClMlMin:0} mL/min is below {rule.RenalAdjustBelowCrCl:0}. {rule.Note.En}", $"Hindari: CrCl {crClMlMin:0} mL/menit di bawah {rule.RenalAdjustBelowCrCl:0}. {rule.Note.Id}")
            : rule.Note;
        return new DoseRecommendation(rule.Generic, single, rule.DosesPerDay, Math.Round(single * rule.DosesPerDay, 2), capped, renal, note);
    }
}
