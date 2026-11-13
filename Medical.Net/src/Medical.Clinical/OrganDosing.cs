using System.Globalization;
using Medical.Core;

namespace Medical.Clinical;

/// <summary>Which kidney-function estimate a label's dosing table is written in.</summary>
public enum RenalMeasure
{
    /// <summary>Cockcroft-Gault creatinine clearance, mL/min (most labels).</summary>
    CrCl,
    /// <summary>eGFR, mL/min/1.73 m² (e.g. metformin).</summary>
    Egfr,
}

/// <summary>Dosing action of a band.</summary>
public enum DoseAction { NoChange, Reduce, ExtendInterval, Avoid, Contraindicated }

/// <summary>One band of a renal dosing table: applies when <c>From ≤ value &lt; To</c>.</summary>
public sealed record RenalDoseBand(double From, double To, DoseAction Action, Localized Advice);

/// <summary>A drug's renal dosing table for one indication / regimen, with its source.</summary>
public sealed record RenalDosingTable(string Generic, string Regimen, RenalMeasure Measure, IReadOnlyList<RenalDoseBand> Bands, string Source, Localized? Dialysis = null);

/// <summary>Hepatic guidance by Child-Pugh class.</summary>
public sealed record HepaticDosing(string Generic, DoseAction A, DoseAction B, DoseAction C, Localized Advice, string Source);

/// <summary>The renal / hepatic advice for a patient.</summary>
public sealed record OrganDoseAdvice(string Generic, string Regimen, DoseAction Action, Localized Advice, string Basis, string Source)
{
    public override string ToString() => $"{Generic} ({Regimen}): {Action} — {Advice.En} [{Basis}; {Source}]";
}

/// <summary>
/// Renal and hepatic dose adjustment from the US prescribing information (FDA labels) of common high-risk drugs, plus
/// the apixaban dose-reduction rule. Tables are keyed by the measure the label uses (Cockcroft-Gault CrCl or eGFR).
/// Add local tables with <see cref="AddRenal"/> / <see cref="AddHepatic"/>; always confirm against the current label
/// and local formulary.
/// </summary>
public sealed class OrganDosing
{
    private readonly List<RenalDosingTable> _renal = [];
    private readonly Dictionary<string, HepaticDosing> _hepatic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The built-in tables.</summary>
    public static OrganDosing Default { get; } = CreateDefault();

    public IReadOnlyList<RenalDosingTable> RenalTables => _renal;
    public IReadOnlyCollection<HepaticDosing> HepaticTables => _hepatic.Values;

    public OrganDosing AddRenal(RenalDosingTable t)
    {
        var sorted = t.Bands.OrderBy(b => b.From).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (Math.Abs(sorted[i].From - sorted[i - 1].To) > 1e-9) throw new ArgumentException($"{t.Generic}: renal bands must be contiguous.", nameof(t));
        _renal.Add(t with { Bands = sorted });
        return this;
    }

    public OrganDosing AddHepatic(HepaticDosing h) { _hepatic[h.Generic] = h; return this; }

    /// <summary>Regimens with a renal table for a drug.</summary>
    public IEnumerable<string> Regimens(string generic) => _renal.Where(t => Eq(t.Generic, generic)).Select(t => t.Regimen);

    /// <summary>
    /// Renal advice. Pass the estimate the label uses: <paramref name="crClMlMin"/> (Cockcroft-Gault, see
    /// <see cref="Calculators.CockcroftGault"/>) and / or <paramref name="egfr"/>. With <paramref name="onDialysis"/> the
    /// dialysis note is returned when the label has one.
    /// </summary>
    public IReadOnlyList<OrganDoseAdvice> Renal(string generic, double? crClMlMin = null, double? egfr = null, bool onDialysis = false, string? regimen = null)
    {
        var res = new List<OrganDoseAdvice>();
        foreach (var t in _renal.Where(t => Eq(t.Generic, generic) && (regimen is null || Eq(t.Regimen, regimen))))
        {
            double? v = t.Measure == RenalMeasure.CrCl ? crClMlMin : egfr;
            string unit = t.Measure == RenalMeasure.CrCl ? "CrCl" : "eGFR";
            if (onDialysis && t.Dialysis is { } d)
            {
                res.Add(new(t.Generic, t.Regimen, DoseAction.Reduce, d, "dialysis", t.Source));
                continue;
            }
            if (v is not { } x) { res.Add(new(t.Generic, t.Regimen, DoseAction.NoChange, new($"{unit} needed for this table.", $"{unit} diperlukan untuk tabel ini."), $"{unit} missing", t.Source)); continue; }
            var band = t.Bands.FirstOrDefault(b => x >= b.From && x < b.To) ?? (x < t.Bands[0].From ? t.Bands[0] : t.Bands[^1]);
            res.Add(new(t.Generic, t.Regimen, band.Action, band.Advice, string.Create(CultureInfo.InvariantCulture, $"{unit} {x:0}"), t.Source));
        }
        return res;
    }

    /// <summary>Hepatic advice by Child-Pugh class ("A", "B" or "C"; see <see cref="ClinicalScores.ChildPugh"/>).</summary>
    public OrganDoseAdvice? Hepatic(string generic, string childPughClass)
    {
        if (!_hepatic.TryGetValue(generic, out var h)) return null;
        var action = childPughClass.ToUpperInvariant() switch
        {
            "A" => h.A, "B" => h.B, "C" => h.C,
            _ => throw new ArgumentException("Child-Pugh class must be A, B or C.", nameof(childPughClass)),
        };
        return new(h.Generic, "hepatic", action, h.Advice, $"Child-Pugh {childPughClass.ToUpperInvariant()}", h.Source);
    }

    /// <summary>
    /// Apixaban for non-valvular AF (Eliquis label): 5 mg twice daily, or 2.5 mg twice daily when at least two of age
    /// ≥ 80 y, weight ≤ 60 kg and serum creatinine ≥ 1.5 mg/dL.
    /// </summary>
    public static OrganDoseAdvice ApixabanAf(int age, double weightKg, double creatinineMgDl)
    {
        var hits = new List<string>();
        if (age >= 80) hits.Add("age ≥ 80");
        if (weightKg <= 60) hits.Add("weight ≤ 60 kg");
        if (creatinineMgDl >= 1.5) hits.Add("creatinine ≥ 1.5 mg/dL");
        bool reduce = hits.Count >= 2;
        return new("apixaban", "AF stroke prevention", reduce ? DoseAction.Reduce : DoseAction.NoChange,
            reduce ? new("2.5 mg twice daily (≥ 2 dose-reduction criteria).", "2,5 mg dua kali sehari (≥ 2 kriteria penurunan dosis).")
                   : new("5 mg twice daily.", "5 mg dua kali sehari."),
            hits.Count == 0 ? "no reduction criteria" : string.Join(", ", hits), "Eliquis (apixaban) US prescribing information §2.2");
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static RenalDoseBand B(double from, double to, DoseAction a, string en, string id) => new(from, to, a, new(en, id));

    private static OrganDosing CreateDefault()
    {
        const double Inf = double.PositiveInfinity;
        var d = new OrganDosing();
        d.AddRenal(new("metformin", "type 2 diabetes", RenalMeasure.Egfr,
        [
            B(0, 30, DoseAction.Contraindicated, "Contraindicated (eGFR < 30).", "Kontraindikasi (eGFR < 30)."),
            B(30, 45, DoseAction.Reduce, "Do not start; if already taking, assess benefit / risk (commonly reduce, e.g. ≤ 1000 mg/day).", "Jangan memulai; bila sudah memakai, nilai manfaat / risiko (umumnya turunkan, mis. ≤ 1000 mg/hari)."),
            B(45, Inf, DoseAction.NoChange, "No renal adjustment; check eGFR at least yearly.", "Tanpa penyesuaian ginjal; periksa eGFR minimal tiap tahun."),
        ], "Metformin US prescribing information (FDA 2016 labelling change)"));
        d.AddRenal(new("gabapentin", "epilepsy / postherpetic neuralgia", RenalMeasure.CrCl,
        [
            B(0, 15, DoseAction.Reduce, "100–300 mg/day once daily (scale down further in proportion to CrCl).", "100–300 mg/hari sekali sehari (turunkan lagi sebanding CrCl)."),
            B(15, 30, DoseAction.Reduce, "200–700 mg/day once daily.", "200–700 mg/hari sekali sehari."),
            B(30, 60, DoseAction.Reduce, "400–1400 mg/day in two divided doses.", "400–1400 mg/hari dibagi dua dosis."),
            B(60, Inf, DoseAction.NoChange, "900–3600 mg/day in three divided doses.", "900–3600 mg/hari dibagi tiga dosis."),
        ], "Neurontin (gabapentin) US prescribing information, Table 1",
            new("Haemodialysis: maintenance dose by CrCl plus a supplemental 125–350 mg after each 4-h session.", "Hemodialisis: dosis rumatan sesuai CrCl ditambah 125–350 mg setelah tiap sesi 4 jam.")));
        d.AddRenal(new("rivaroxaban", "AF stroke prevention", RenalMeasure.CrCl,
        [
            B(0, 15, DoseAction.Avoid, "Avoid (CrCl < 15 mL/min).", "Hindari (CrCl < 15 mL/menit)."),
            B(15, 50.000001, DoseAction.Reduce, "15 mg once daily with the evening meal.", "15 mg sekali sehari bersama makan malam."),
            B(50.000001, Inf, DoseAction.NoChange, "20 mg once daily with the evening meal.", "20 mg sekali sehari bersama makan malam."),
        ], "Xarelto (rivaroxaban) US prescribing information §2.2"));
        d.AddRenal(new("rivaroxaban", "VTE treatment", RenalMeasure.CrCl,
        [
            B(0, 15, DoseAction.Avoid, "Avoid (CrCl < 15 mL/min).", "Hindari (CrCl < 15 mL/menit)."),
            B(15, Inf, DoseAction.NoChange, "15 mg twice daily for 21 days, then 20 mg once daily, with food.", "15 mg dua kali sehari selama 21 hari, lalu 20 mg sekali sehari, bersama makanan."),
        ], "Xarelto (rivaroxaban) US prescribing information §2.2"));
        d.AddRenal(new("dabigatran", "AF stroke prevention", RenalMeasure.CrCl,
        [
            B(0, 15, DoseAction.Avoid, "Not recommended (CrCl < 15 mL/min or on dialysis).", "Tidak dianjurkan (CrCl < 15 mL/menit atau dialisis)."),
            B(15, 30.000001, DoseAction.Reduce, "75 mg twice daily.", "75 mg dua kali sehari."),
            B(30.000001, Inf, DoseAction.NoChange, "150 mg twice daily.", "150 mg dua kali sehari."),
        ], "Pradaxa (dabigatran) US prescribing information §2.3"));
        d.AddRenal(new("enoxaparin", "VTE prophylaxis", RenalMeasure.CrCl,
        [
            B(0, 30, DoseAction.Reduce, "30 mg SC once daily.", "30 mg SK sekali sehari."),
            B(30, Inf, DoseAction.NoChange, "40 mg SC once daily (abdominal surgery / medical patients).", "40 mg SK sekali sehari (bedah abdomen / pasien medis)."),
        ], "Lovenox (enoxaparin) US prescribing information §2.3"));
        d.AddRenal(new("enoxaparin", "VTE treatment", RenalMeasure.CrCl,
        [
            B(0, 30, DoseAction.ExtendInterval, "1 mg/kg SC once daily; consider anti-Xa monitoring.", "1 mg/kgBB SK sekali sehari; pertimbangkan pemantauan anti-Xa."),
            B(30, Inf, DoseAction.NoChange, "1 mg/kg SC every 12 h (or 1.5 mg/kg once daily).", "1 mg/kgBB SK tiap 12 jam (atau 1,5 mg/kgBB sekali sehari)."),
        ], "Lovenox (enoxaparin) US prescribing information §2.3"));
        d.AddRenal(new("levofloxacin", "750 mg regimen", RenalMeasure.CrCl,
        [
            B(0, 10, DoseAction.ExtendInterval, "750 mg once, then 500 mg every 48 h (no data below 10 mL/min outside dialysis; follow the label).", "750 mg sekali, lalu 500 mg tiap 48 jam (ikuti label di bawah 10 mL/menit)."),
            B(10, 20, DoseAction.ExtendInterval, "750 mg once, then 500 mg every 48 h.", "750 mg sekali, lalu 500 mg tiap 48 jam."),
            B(20, 50, DoseAction.ExtendInterval, "750 mg every 48 h.", "750 mg tiap 48 jam."),
            B(50, Inf, DoseAction.NoChange, "750 mg every 24 h.", "750 mg tiap 24 jam."),
        ], "Levaquin (levofloxacin) US prescribing information, Table 2",
            new("Haemodialysis / CAPD: 750 mg once, then 500 mg every 48 h; no supplemental dose.", "Hemodialisis / CAPD: 750 mg sekali, lalu 500 mg tiap 48 jam; tanpa dosis tambahan.")));
        d.AddRenal(new("ciprofloxacin", "oral", RenalMeasure.CrCl,
        [
            B(0, 30, DoseAction.ExtendInterval, "250–500 mg every 18 h.", "250–500 mg tiap 18 jam."),
            B(30, 50.000001, DoseAction.ExtendInterval, "250–500 mg every 12 h.", "250–500 mg tiap 12 jam."),
            B(50.000001, Inf, DoseAction.NoChange, "Usual dose.", "Dosis lazim."),
        ], "Cipro (ciprofloxacin) tablets US prescribing information, Table 10",
            new("Haemodialysis / peritoneal dialysis: 250–500 mg every 24 h, after dialysis.", "Hemodialisis / dialisis peritoneal: 250–500 mg tiap 24 jam, setelah dialisis.")));

        d.AddHepatic(new("rivaroxaban", DoseAction.NoChange, DoseAction.Avoid, DoseAction.Avoid,
            new("Avoid in Child-Pugh B or C, or any hepatic disease with coagulopathy.", "Hindari pada Child-Pugh B atau C, atau penyakit hati dengan koagulopati."), "Xarelto US prescribing information §8.7"));
        d.AddHepatic(new("apixaban", DoseAction.NoChange, DoseAction.NoChange, DoseAction.Avoid,
            new("No adjustment in Child-Pugh A; limited experience in B (use with caution); not recommended in C.", "Tanpa penyesuaian pada Child-Pugh A; pengalaman terbatas pada B (hati-hati); tidak dianjurkan pada C."), "Eliquis US prescribing information §8.7"));
        d.AddHepatic(new("dabigatran", DoseAction.NoChange, DoseAction.NoChange, DoseAction.Avoid,
            new("No adjustment in mild-moderate impairment; patients with raised liver enzymes were excluded from trials; avoid in severe disease.", "Tanpa penyesuaian pada gangguan ringan-sedang; hindari pada penyakit berat."), "Pradaxa US prescribing information §8"));
        d.AddHepatic(new("metformin", DoseAction.Avoid, DoseAction.Avoid, DoseAction.Avoid,
            new("Not recommended in hepatic impairment (lactic acidosis risk).", "Tidak dianjurkan pada gangguan hati (risiko asidosis laktat)."), "Metformin US prescribing information §5.1"));
        d.AddHepatic(new("simvastatin", DoseAction.NoChange, DoseAction.Contraindicated, DoseAction.Contraindicated,
            new("Contraindicated in active liver disease / decompensated cirrhosis.", "Kontraindikasi pada penyakit hati aktif / sirosis dekompensasi."), "Zocor (simvastatin) US prescribing information §4"));
        d.AddHepatic(new("paracetamol", DoseAction.NoChange, DoseAction.Reduce, DoseAction.Reduce,
            new("Chronic liver disease: limit to 2 g/day and avoid in acute liver failure.", "Penyakit hati kronik: batasi 2 g/hari; hindari pada gagal hati akut."), "Common hepatology practice (e.g. AASLD guidance); not a label table"));
        return d;
    }
}
