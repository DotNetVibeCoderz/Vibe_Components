using Medical.Core;
using Medical.Terminology;

namespace Medical.Rules;

/// <summary>
/// Curated, illustrative drug-safety knowledge (interactions and allergy cross-reactivity) keyed on generic names and
/// RxNorm class tags from <see cref="TerminologyService"/>. Not a substitute for a licensed drug-interaction database.
/// </summary>
public sealed class DrugKnowledge
{
    private readonly TerminologyService _terms;

    public DrugKnowledge(TerminologyService? terms = null) => _terms = terms ?? TerminologyService.Default;

    public static DrugKnowledge Default { get; } = new();

    public IReadOnlyList<DrugInteraction> Interactions { get; } =
    [
        new("anticoagulant", "antiplatelet", InteractionSeverity.Major, new("Additive bleeding risk (anticoagulant + antiplatelet).", "Risiko perdarahan meningkat (antikoagulan + antiplatelet).")),
        new("anticoagulant", "nsaid", InteractionSeverity.Major, new("NSAIDs increase gastrointestinal bleeding risk with anticoagulants.", "OAINS meningkatkan risiko perdarahan saluran cerna dengan antikoagulan.")),
        new("anticoagulant", "anticoagulant", InteractionSeverity.Major, new("Duplicate anticoagulation — high bleeding risk.", "Antikoagulan ganda — risiko perdarahan tinggi.")),
        new("warfarin", "metronidazole", InteractionSeverity.Major, new("Metronidazole inhibits warfarin metabolism (CYP2C9) — INR rises; monitor INR / reduce dose.", "Metronidazol menghambat metabolisme warfarin — INR naik; pantau INR / turunkan dosis.")),
        new("warfarin", "fluconazole", InteractionSeverity.Major, new("Fluconazole raises warfarin levels (CYP2C9) — INR rises.", "Flukonazol meningkatkan kadar warfarin — INR naik.")),
        new("warfarin", "ciprofloxacin", InteractionSeverity.Moderate, new("Ciprofloxacin may potentiate warfarin — monitor INR.", "Siprofloksasin dapat memperkuat efek warfarin — pantau INR.")),
        new("warfarin", "macrolide", InteractionSeverity.Moderate, new("Macrolides may potentiate warfarin — monitor INR.", "Makrolida dapat memperkuat efek warfarin — pantau INR.")),
        new("anticoagulant", "cyp-inducer", InteractionSeverity.Major, new("Enzyme inducer lowers anticoagulant levels — risk of thrombosis.", "Penginduksi enzim menurunkan kadar antikoagulan — risiko trombosis.")),
        new("cyp3a4-substrate", "cyp3a4-inhibitor", InteractionSeverity.Contraindicated, new("Strong CYP3A4 inhibitor with simvastatin / colchicine — rhabdomyolysis or toxicity. Avoid combination.", "Penghambat kuat CYP3A4 dengan simvastatin / kolkisin — rabdomiolisis atau toksisitas. Hindari kombinasi.")),
        new("acei", "k-sparing", InteractionSeverity.Major, new("ACE inhibitor + potassium-sparing diuretic — hyperkalaemia.", "Penghambat ACE + diuretik hemat kalium — hiperkalemia.")),
        new("arb", "k-sparing", InteractionSeverity.Major, new("ARB + potassium-sparing diuretic — hyperkalaemia.", "ARB + diuretik hemat kalium — hiperkalemia.")),
        new("acei", "potassium", InteractionSeverity.Moderate, new("ACE inhibitor + potassium supplement — monitor potassium.", "Penghambat ACE + suplemen kalium — pantau kalium.")),
        new("acei", "arb", InteractionSeverity.Major, new("Dual RAAS blockade — hyperkalaemia, hypotension and AKI without added benefit.", "Blokade RAAS ganda — hiperkalemia, hipotensi dan AKI tanpa manfaat tambahan.")),
        new("acei", "nsaid", InteractionSeverity.Moderate, new("NSAID with ACE inhibitor reduces renal perfusion (risk of AKI, 'triple whammy' with a diuretic).", "OAINS dengan penghambat ACE menurunkan perfusi ginjal (risiko AKI).")),
        new("arb", "nsaid", InteractionSeverity.Moderate, new("NSAID with ARB reduces renal perfusion (risk of AKI).", "OAINS dengan ARB menurunkan perfusi ginjal (risiko AKI).")),
        new("digitalis", "loop-diuretic", InteractionSeverity.Moderate, new("Diuretic-induced hypokalaemia increases digoxin toxicity — monitor K⁺.", "Hipokalemia akibat diuretik meningkatkan toksisitas digoksin — pantau K⁺.")),
        new("digitalis", "macrolide", InteractionSeverity.Moderate, new("Macrolides raise digoxin levels.", "Makrolida meningkatkan kadar digoksin.")),
        new("serotonergic", "serotonergic", InteractionSeverity.Major, new("Two serotonergic drugs — risk of serotonin syndrome.", "Dua obat serotonergik — risiko sindrom serotonin.")),
        new("qt", "qt", InteractionSeverity.Moderate, new("Two QT-prolonging drugs — risk of torsades de pointes; check ECG and electrolytes.", "Dua obat pemanjang QT — risiko torsades de pointes; periksa EKG dan elektrolit.")),
        new("clopidogrel", "omeprazole", InteractionSeverity.Moderate, new("Omeprazole reduces clopidogrel activation (CYP2C19) — prefer pantoprazole.", "Omeprazol menurunkan aktivasi klopidogrel — pilih pantoprazol.")),
        new("sulfonylurea", "fluconazole", InteractionSeverity.Moderate, new("Fluconazole increases sulfonylurea levels — hypoglycaemia.", "Flukonazol meningkatkan kadar sulfonilurea — hipoglikemia.")),
        new("opioid", "benzodiazepine", InteractionSeverity.Major, new("Opioid + benzodiazepine — profound sedation and respiratory depression.", "Opioid + benzodiazepin — sedasi berat dan depresi napas.")),
    ];

    public IReadOnlyList<AllergyCrossReactivity> CrossReactivity { get; } =
    [
        new("penicillin", "penicillin", InteractionSeverity.Contraindicated, new("Documented penicillin allergy — do not give penicillins.", "Riwayat alergi penisilin — jangan berikan golongan penisilin.")),
        new("penicillin", "cephalosporin", InteractionSeverity.Moderate, new("Penicillin allergy: low (~1–2 %) cross-reactivity with cephalosporins — use with caution, avoid if anaphylaxis.", "Alergi penisilin: reaktivitas silang rendah (~1–2 %) dengan sefalosporin — hati-hati, hindari bila riwayat anafilaksis.")),
        new("penicillin", "carbapenem", InteractionSeverity.Moderate, new("Penicillin allergy: very low (<1 %) cross-reactivity with carbapenems.", "Alergi penisilin: reaktivitas silang sangat rendah (<1 %) dengan karbapenem.")),
        new("sulfonamide", "sulfonamide", InteractionSeverity.Contraindicated, new("Sulfonamide allergy — avoid sulfonamide antibiotics.", "Alergi sulfa — hindari antibiotik sulfonamida.")),
        new("nsaid", "nsaid", InteractionSeverity.Contraindicated, new("NSAID hypersensitivity — avoid NSAIDs including aspirin.", "Hipersensitivitas OAINS — hindari OAINS termasuk aspirin.")),
        new("aspirin", "nsaid", InteractionSeverity.Major, new("Aspirin hypersensitivity — cross-reactive with other NSAIDs.", "Hipersensitivitas aspirin — reaksi silang dengan OAINS lain.")),
    ];

    /// <summary>Generic name plus RxNorm class tags, e.g. "warfarin" → {warfarin, anticoagulant, vka}.</summary>
    public IReadOnlySet<string> Classes(string generic)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { generic.ToLowerInvariant() };
        if (_terms.Drug(generic) is { } c)
        {
            set.Add(c.Display.ToLowerInvariant());
            foreach (var t in c.Tags) set.Add(t);
        }
        return set;
    }

    /// <summary>The most severe interaction between two drugs, if any.</summary>
    public DrugInteraction? Interaction(string drugA, string drugB)
    {
        if (string.Equals(drugA, drugB, StringComparison.OrdinalIgnoreCase)) return null;
        var a = Classes(drugA);
        var b = Classes(drugB);
        return Interactions.Where(i => (a.Contains(i.A) && b.Contains(i.B)) || (a.Contains(i.B) && b.Contains(i.A)))
            .OrderByDescending(i => i.Severity).FirstOrDefault();
    }

    /// <summary>The most severe allergy conflict between an allergy substance and a drug, if any.</summary>
    public AllergyCrossReactivity? AllergyConflict(string allergySubstance, string drug)
    {
        var allergen = Classes(allergySubstance);
        var d = Classes(drug);
        return CrossReactivity.Where(x => allergen.Contains(x.Allergen) && d.Contains(x.DrugClass)).OrderByDescending(x => x.Severity).FirstOrDefault();
    }

    /// <summary>Therapeutic class shared by two different drugs (duplicate therapy), if any.</summary>
    public string? DuplicateClass(string drugA, string drugB)
    {
        if (string.Equals(drugA, drugB, StringComparison.OrdinalIgnoreCase)) return null;
        string[] duplicateClasses = ["nsaid", "statin", "acei", "arb", "ppi", "ssri", "betablocker", "sulfonylurea", "benzodiazepine"];
        var a = Classes(drugA);
        var b = Classes(drugB);
        return duplicateClasses.FirstOrDefault(c => a.Contains(c) && b.Contains(c) && !(c == "nsaid" && (a.Contains("antiplatelet") || b.Contains("antiplatelet"))));
    }

    public static AlertSeverity ToAlert(InteractionSeverity s) => s == InteractionSeverity.Moderate ? AlertSeverity.Warning : AlertSeverity.Critical;
}
