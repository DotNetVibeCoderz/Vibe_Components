using Medical.Clinical;
using Medical.Core;
using NRules.Fluent.Dsl;

namespace Medical.Rules.Definitions;

// NRules fluent rules. Each rule matches facts produced by FactBuilder and inserts ClinicalAlert facts.

[Name("NEWS2.High"), Priority(100), Description("NEWS2 ≥ 7: emergency response")]
public sealed class News2HighRule : Rule
{
    public override void Define()
    {
        VitalsFact v = null!;
        When().Match(() => v, x => x.News2.Score >= 7);
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(v.PatientId, AlertSeverity.Critical, AlertCategory.EarlyWarning, "NEWS2.High",
            new("NEWS2 high — emergency response", "NEWS2 tinggi — respons darurat"), v.News2.Interpretation, $"NEWS2={v.News2.Score}")));
    }
}

[Name("NEWS2.Medium"), Priority(90), Description("NEWS2 5–6 or any single red score")]
public sealed class News2MediumRule : Rule
{
    public override void Define()
    {
        VitalsFact v = null!;
        When().Match(() => v, x => (x.News2.Score >= 5 && x.News2.Score < 7) || (x.News2.Score < 5 && x.HasRedScore));
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(v.PatientId, AlertSeverity.Warning, AlertCategory.EarlyWarning, "NEWS2.Medium",
            new("NEWS2 elevated — urgent clinical review", "NEWS2 meningkat — evaluasi klinis segera"), v.News2.Interpretation, $"NEWS2={v.News2.Score}")));
    }
}

[Name("Sepsis.Screen"), Priority(95), Description("Sepsis-3: suspected infection with qSOFA ≥ 2 or lactate > 2")]
public sealed class SepsisScreenRule : Rule
{
    public override void Define()
    {
        PatientFact p = null!;
        VitalsFact v = null!;
        When()
            .Match(() => p, x => x.SuspectedInfection)
            .Match(() => v, x => x.PatientId == p.PatientId && x.QSofa.Score >= 2);
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(p.PatientId, AlertSeverity.Critical, AlertCategory.Sepsis, "Sepsis.Screen",
            new("Possible sepsis — start Sepsis Six / Hour-1 bundle", "Kemungkinan sepsis — mulai bundel 1 jam"),
            new("Suspected infection with qSOFA ≥ 2: take blood cultures, measure lactate, give broad-spectrum antibiotics and fluids within 1 hour; assess SOFA.",
                "Curiga infeksi dengan qSOFA ≥ 2: kultur darah, periksa laktat, beri antibiotik spektrum luas dan cairan dalam 1 jam; nilai SOFA."),
            $"qSOFA={v.QSofa.Score}")));
    }
}

[Name("Sepsis.Lactate"), Priority(94), Description("Suspected infection with lactate > 2 mmol/L")]
public sealed class SepsisLactateRule : Rule
{
    public override void Define()
    {
        PatientFact p = null!;
        LabFact l = null!;
        When()
            .Match(() => p, x => x.SuspectedInfection)
            .Match(() => l, x => x.PatientId == p.PatientId && x.Code == "2524-7" && x.Value > 2.0);
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(p.PatientId, l.Value >= 4 ? AlertSeverity.Critical : AlertSeverity.Warning, AlertCategory.Sepsis, "Sepsis.Lactate",
            new("Hyperlactataemia with suspected infection", "Hiperlaktatemia dengan curiga infeksi"),
            new($"Lactate {l.Value:0.0} mmol/L — tissue hypoperfusion; reassess within 2 h (≥ 4 mmol/L suggests septic shock if hypotensive).",
                $"Laktat {l.Value:0.0} mmol/L — hipoperfusi jaringan; evaluasi ulang dalam 2 jam."),
            $"lactate={l.Value:0.0}")));
    }
}

[Name("Lab.Critical"), Priority(80), Description("Critical (panic) laboratory values")]
public sealed class CriticalLabRule : Rule
{
    public override void Define()
    {
        LabFact l = null!;
        When().Match(() => l, x => x.Flag == ResultFlag.CriticalHigh || x.Flag == ResultFlag.CriticalLow);
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(l.PatientId, AlertSeverity.Critical, AlertCategory.Laboratory, "Lab.Critical",
            new($"Critical value: {l.Observation.Code.Display}", $"Nilai kritis: {l.Observation.Code.Display}"), l.Message, $"{l.Code}={l.Value:0.##}")));
    }
}

[Name("Lab.Hypoglycaemia"), Priority(85), Description("Glucose < 70 mg/dL")]
public sealed class HypoglycaemiaRule : Rule
{
    public override void Define()
    {
        LabFact l = null!;
        When().Match(() => l, x => (x.Code == "2345-7" || x.Code == "2339-0") && x.Value < 70);
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(l.PatientId, l.Value < 54 ? AlertSeverity.Critical : AlertSeverity.Warning, AlertCategory.Laboratory, "Lab.Hypoglycaemia",
            new("Hypoglycaemia", "Hipoglikemia"),
            new($"Glucose {l.Value:0} mg/dL — give 15–20 g fast-acting carbohydrate (or IV dextrose) and recheck in 15 min.",
                $"Glukosa {l.Value:0} mg/dL — beri 15–20 g karbohidrat cepat (atau dekstrosa IV) dan cek ulang 15 menit."),
            $"glucose={l.Value:0}")));
    }
}

[Name("Lab.Hyperkalaemia.RAAS"), Priority(75), Description("Potassium > 5.0 on ACEi/ARB/K-sparing")]
public sealed class HyperkalaemiaRaasRule : Rule
{
    public override void Define()
    {
        LabFact l = null!;
        MedicationFact m = null!;
        When()
            .Match(() => l, x => x.Code == "2823-3" && x.Value > 5.0)
            .Match(() => m, x => x.PatientId == l.PatientId && (x.Is("acei") || x.Is("arb") || x.Is("k-sparing") || x.Is("potassium")));
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(l.PatientId, l.Value >= 6 ? AlertSeverity.Critical : AlertSeverity.Warning, AlertCategory.Laboratory, "Lab.Hyperkalaemia.RAAS",
            new($"Hyperkalaemia on {m.Generic}", $"Hiperkalemia dengan {m.Generic}"),
            new($"K⁺ {l.Value:0.0} mmol/L while taking {m.Generic} — review dose, repeat K⁺, obtain ECG.", $"K⁺ {l.Value:0.0} mmol/L saat memakai {m.Generic} — tinjau dosis, ulang K⁺, rekam EKG."),
            $"K={l.Value:0.0};{m.Generic}")));
    }
}

[Name("Drug.Interaction"), Priority(70), Description("Drug-drug interactions from DrugKnowledge")]
public sealed class DrugInteractionRule : Rule
{
    public override void Define()
    {
        MedicationFact a = null!;
        MedicationFact b = null!;
        When()
            .Match(() => a)
            .Match(() => b, x => x.PatientId == a.PatientId && string.CompareOrdinal(x.Generic, a.Generic) > 0 && DrugKnowledge.Default.Interaction(a.Generic, x.Generic) != null);
        Then().Do(ctx => Raise(ctx, a, b));
    }

    private static void Raise(NRules.RuleModel.IContext ctx, MedicationFact a, MedicationFact b)
    {
        var i = DrugKnowledge.Default.Interaction(a.Generic, b.Generic)!;
        ctx.TryInsert(new ClinicalAlert(a.PatientId, DrugKnowledge.ToAlert(i.Severity), AlertCategory.DrugInteraction, "Drug.Interaction",
            new($"{i.Severity} interaction: {a.Generic} + {b.Generic}", $"Interaksi {Sev(i.Severity)}: {a.Generic} + {b.Generic}"), i.Effect, $"{a.Generic}+{b.Generic}"));
    }

    internal static string Sev(InteractionSeverity s) => s switch { InteractionSeverity.Contraindicated => "kontraindikasi", InteractionSeverity.Major => "mayor", _ => "moderat" };
}

[Name("Drug.Duplicate"), Priority(65), Description("Two drugs of the same therapeutic class")]
public sealed class DuplicateTherapyRule : Rule
{
    public override void Define()
    {
        MedicationFact a = null!;
        MedicationFact b = null!;
        When()
            .Match(() => a)
            .Match(() => b, x => x.PatientId == a.PatientId && string.CompareOrdinal(x.Generic, a.Generic) > 0 && DrugKnowledge.Default.DuplicateClass(a.Generic, x.Generic) != null);
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(a.PatientId, AlertSeverity.Warning, AlertCategory.DuplicateTherapy, "Drug.Duplicate",
            new($"Duplicate therapy: {a.Generic} + {b.Generic}", $"Terapi ganda: {a.Generic} + {b.Generic}"),
            new($"Both drugs belong to the '{DrugKnowledge.Default.DuplicateClass(a.Generic, b.Generic)}' class.", $"Kedua obat termasuk golongan '{DrugKnowledge.Default.DuplicateClass(a.Generic, b.Generic)}'."),
            $"{a.Generic}+{b.Generic}")));
    }
}

[Name("Drug.Allergy"), Priority(99), Description("Prescribed drug conflicts with a documented allergy")]
public sealed class AllergyConflictRule : Rule
{
    public override void Define()
    {
        AllergyFact al = null!;
        MedicationFact m = null!;
        When()
            .Match(() => al)
            .Match(() => m, x => x.PatientId == al.PatientId && DrugKnowledge.Default.AllergyConflict(al.Allergy.Substance, x.Generic) != null);
        Then().Do(ctx => Raise(ctx, al, m));
    }

    private static void Raise(NRules.RuleModel.IContext ctx, AllergyFact al, MedicationFact m)
    {
        var x = DrugKnowledge.Default.AllergyConflict(al.Allergy.Substance, m.Generic)!;
        ctx.TryInsert(new ClinicalAlert(al.PatientId, DrugKnowledge.ToAlert(x.Severity), AlertCategory.Allergy, "Drug.Allergy",
            new($"Allergy conflict: {m.Generic} (allergic to {al.Allergy.Substance})", $"Konflik alergi: {m.Generic} (alergi {al.Allergy.Substance})"),
            x.Effect, $"{al.Allergy.Substance}→{m.Generic}"));
    }
}

[Name("Renal.Metformin"), Priority(60), Description("Metformin with eGFR < 45 (reduce) or < 30 (stop)")]
public sealed class MetforminRenalRule : Rule
{
    public override void Define()
    {
        PatientFact p = null!;
        MedicationFact m = null!;
        When()
            .Match(() => p, x => x.Egfr != null && x.Egfr < 45)
            .Match(() => m, x => x.PatientId == p.PatientId && x.Generic == "metformin");
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(p.PatientId, p.Egfr < 30 ? AlertSeverity.Critical : AlertSeverity.Warning, AlertCategory.Renal, "Renal.Metformin",
            new("Metformin and reduced kidney function", "Metformin dan penurunan fungsi ginjal"),
            p.Egfr < 30
                ? new($"eGFR {p.Egfr:0} — metformin is contraindicated (lactic acidosis risk); stop.", $"eGFR {p.Egfr:0} — metformin kontraindikasi (risiko asidosis laktat); hentikan.")
                : new($"eGFR {p.Egfr:0} — max 1000 mg/day; review the benefit/risk.", $"eGFR {p.Egfr:0} — maks 1000 mg/hari; tinjau manfaat/risiko."),
            $"eGFR={p.Egfr:0}")));
    }
}

[Name("Renal.Nsaid"), Priority(60), Description("NSAID with eGFR < 30 or AKI risk")]
public sealed class NsaidRenalRule : Rule
{
    public override void Define()
    {
        PatientFact p = null!;
        MedicationFact m = null!;
        When()
            .Match(() => p, x => x.Egfr != null && x.Egfr < 30)
            .Match(() => m, x => x.PatientId == p.PatientId && x.Is("nsaid") && !x.Is("antiplatelet"));
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(p.PatientId, AlertSeverity.Critical, AlertCategory.Renal, "Renal.Nsaid",
            new($"Avoid {m.Generic} in severe CKD", $"Hindari {m.Generic} pada PGK berat"),
            new($"eGFR {p.Egfr:0}: NSAIDs can precipitate acute kidney injury.", $"eGFR {p.Egfr:0}: OAINS dapat memicu cedera ginjal akut."), $"eGFR={p.Egfr:0};{m.Generic}")));
    }
}

[Name("Bleeding.ElderlyAnticoagulant"), Priority(40), Description("Anticoagulated patient ≥ 75 years")]
public sealed class ElderlyAnticoagulantRule : Rule
{
    public override void Define()
    {
        PatientFact p = null!;
        MedicationFact m = null!;
        When()
            .Match(() => p, x => x.Age >= 75)
            .Match(() => m, x => x.PatientId == p.PatientId && x.Is("anticoagulant"));
        Then().Do(ctx => ctx.TryInsert(new ClinicalAlert(p.PatientId, AlertSeverity.Info, AlertCategory.Dosing, "Bleeding.ElderlyAnticoagulant",
            new("Anticoagulation in an older adult", "Antikoagulan pada lansia"),
            new($"Age {p.Age} on {m.Generic}: calculate HAS-BLED and review falls risk and renal dosing.", $"Usia {p.Age} memakai {m.Generic}: hitung HAS-BLED, tinjau risiko jatuh dan dosis ginjal."),
            $"age={p.Age};{m.Generic}")));
    }
}
