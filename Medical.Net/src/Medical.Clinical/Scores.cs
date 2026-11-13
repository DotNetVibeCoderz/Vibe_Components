using Medical.Core;

namespace Medical.Clinical;

public enum RiskLevel { Low, LowMedium, Medium, High, Critical }

/// <summary>One scored item of a clinical score (e.g. "Respiratory rate 26 /min → 3").</summary>
public sealed record ScoreComponent(string Parameter, string Value, int Points)
{
    /// <summary>The published (possibly fractional) points when <see cref="Points"/> uses scaled units, e.g. Wells PE.</summary>
    public double? ExactPoints { get; init; }
}

/// <summary>Result of a clinical score with its bilingual interpretation.</summary>
public sealed record ScoreResult(string Name, int Score, int MaxScore, RiskLevel Risk, Localized Interpretation, IReadOnlyList<ScoreComponent> Components)
{
    /// <summary>The published (possibly fractional) score when <see cref="Score"/> uses scaled units, e.g. Wells PE (half points).</summary>
    public double? ExactScore { get; init; }

    public override string ToString() => $"{Name} {(ExactScore is { } e ? e.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : Score.ToString(System.Globalization.CultureInfo.InvariantCulture))}/{MaxScore} ({Risk}) — {Interpretation.En}";
}

/// <summary>Validated bedside scores. All thresholds follow the original publications cited on each method.</summary>
public static partial class ClinicalScores
{
    /// <summary>
    /// National Early Warning Score 2 (Royal College of Physicians, 2017).
    /// Use <paramref name="hypercapnicScale"/> (SpO₂ scale 2) only for patients with confirmed hypercapnic respiratory failure.
    /// </summary>
    public static ScoreResult News2(VitalSigns v, bool hypercapnicScale = false)
    {
        var c = new List<ScoreComponent>(7);
        int rr = v.RespiratoryRate switch { <= 8 => 3, <= 11 => 1, <= 20 => 0, <= 24 => 2, _ => 3 };
        c.Add(new("Respiratory rate", $"{v.RespiratoryRate:0} /min", rr));

        int spo2 = hypercapnicScale
            ? v.SpO2 switch
            {
                <= 83 => 3,
                <= 85 => 2,
                <= 87 => 1,
                <= 92 => 0,
                _ when !v.OnSupplementalOxygen => 0,
                <= 94 => 1,
                <= 96 => 2,
                _ => 3,
            }
            : v.SpO2 switch { <= 91 => 3, <= 93 => 2, <= 95 => 1, _ => 0 };
        c.Add(new(hypercapnicScale ? "SpO₂ (scale 2)" : "SpO₂ (scale 1)", $"{v.SpO2:0} %", spo2));

        int air = v.OnSupplementalOxygen ? 2 : 0;
        c.Add(new("Air or oxygen", v.OnSupplementalOxygen ? "Oxygen" : "Air", air));

        int sbp = v.SystolicBp switch { <= 90 => 3, <= 100 => 2, <= 110 => 1, <= 219 => 0, _ => 3 };
        c.Add(new("Systolic BP", $"{v.SystolicBp:0} mmHg", sbp));

        int hr = v.HeartRate switch { <= 40 => 3, <= 50 => 1, <= 90 => 0, <= 110 => 1, <= 130 => 2, _ => 3 };
        c.Add(new("Pulse", $"{v.HeartRate:0} /min", hr));

        int cons = v.Consciousness == Consciousness.Alert ? 0 : 3;
        c.Add(new("Consciousness", v.Consciousness.ToString(), cons));

        int temp = v.TemperatureC switch { <= 35.0 => 3, <= 36.0 => 1, <= 38.0 => 0, <= 39.0 => 1, _ => 2 };
        c.Add(new("Temperature", $"{v.TemperatureC:0.0} °C", temp));

        int total = c.Sum(x => x.Points);
        bool redScore = c.Any(x => x.Points == 3);
        var (risk, text) = total switch
        {
            >= 7 => (RiskLevel.High, new Localized(
                "High clinical risk — emergency assessment by a critical-care team; continuous monitoring.",
                "Risiko klinis tinggi — penilaian darurat oleh tim perawatan kritis; pemantauan kontinu.")),
            >= 5 => (RiskLevel.Medium, new Localized(
                "Medium clinical risk — urgent review by a clinician with competence in acute illness; monitor at least hourly.",
                "Risiko klinis sedang — evaluasi segera oleh klinisi yang kompeten menangani penyakit akut; pantau minimal tiap jam.")),
            _ when redScore => (RiskLevel.LowMedium, new Localized(
                "Low–medium risk (a single parameter scored 3) — urgent ward-based response; monitor at least hourly.",
                "Risiko rendah–sedang (satu parameter bernilai 3) — respons segera di bangsal; pantau minimal tiap jam.")),
            >= 1 => (RiskLevel.Low, new Localized(
                "Low clinical risk — ward-based response; monitor at least every 4–6 hours.",
                "Risiko klinis rendah — respons di bangsal; pantau minimal tiap 4–6 jam.")),
            _ => (RiskLevel.Low, new Localized(
                "Score 0 — routine monitoring at least every 12 hours.",
                "Skor 0 — pemantauan rutin minimal tiap 12 jam.")),
        };
        return new ScoreResult("NEWS2", total, 20, risk, text, c);
    }

    /// <summary>quick SOFA (Sepsis-3, Seymour et al. JAMA 2016). ≥2 identifies infected patients at risk of poor outcome.</summary>
    public static ScoreResult QSofa(double respiratoryRate, double systolicBp, bool alteredMentation)
    {
        var c = new List<ScoreComponent>
        {
            new("Respiratory rate ≥ 22", $"{respiratoryRate:0} /min", respiratoryRate >= 22 ? 1 : 0),
            new("Systolic BP ≤ 100", $"{systolicBp:0} mmHg", systolicBp <= 100 ? 1 : 0),
            new("Altered mentation (GCS < 15)", alteredMentation ? "Yes" : "No", alteredMentation ? 1 : 0),
        };
        int s = c.Sum(x => x.Points);
        return s >= 2
            ? new ScoreResult("qSOFA", s, 3, RiskLevel.High, new("Positive — high risk of poor outcome if infection is suspected; assess for organ dysfunction (full SOFA, lactate).",
                "Positif — risiko luaran buruk tinggi bila dicurigai infeksi; nilai disfungsi organ (SOFA lengkap, laktat)."), c)
            : new ScoreResult("qSOFA", s, 3, RiskLevel.Low, new("Negative — continue to monitor; qSOFA is not a diagnostic test for sepsis.",
                "Negatif — lanjutkan pemantauan; qSOFA bukan tes diagnostik sepsis."), c);
    }

    public static ScoreResult QSofa(VitalSigns v) => QSofa(v.RespiratoryRate, v.SystolicBp, v.Consciousness != Consciousness.Alert);

    /// <summary>SIRS criteria (ACCP/SCCM 1992). <paramref name="wbc"/> in ×10³/µL; <paramref name="paCo2"/> in mmHg (optional).</summary>
    public static ScoreResult Sirs(VitalSigns v, double? wbc, double? paCo2 = null, bool bands = false)
    {
        var c = new List<ScoreComponent>
        {
            new("Temperature > 38 or < 36 °C", $"{v.TemperatureC:0.0} °C", v.TemperatureC is > 38 or < 36 ? 1 : 0),
            new("Heart rate > 90", $"{v.HeartRate:0} /min", v.HeartRate > 90 ? 1 : 0),
            new("RR > 20 or PaCO₂ < 32", $"{v.RespiratoryRate:0} /min", v.RespiratoryRate > 20 || paCo2 < 32 ? 1 : 0),
            new("WBC > 12 or < 4 ×10³/µL or > 10 % bands", wbc is { } w ? $"{w:0.0}" : "n/a", wbc is > 12 or < 4 || bands ? 1 : 0),
        };
        int s = c.Sum(x => x.Points);
        return new ScoreResult("SIRS", s, 4, s >= 2 ? RiskLevel.Medium : RiskLevel.Low,
            s >= 2 ? new("SIRS criteria met (≥ 2).", "Kriteria SIRS terpenuhi (≥ 2).") : new("SIRS criteria not met.", "Kriteria SIRS tidak terpenuhi."), c);
    }

    /// <summary>CURB-65 pneumonia severity (Lim et al. Thorax 2003). <paramref name="ureaMmolL"/> = blood urea in mmol/L.</summary>
    public static ScoreResult Curb65(bool confusion, double ureaMmolL, double respiratoryRate, double systolicBp, double diastolicBp, int age)
    {
        var c = new List<ScoreComponent>
        {
            new("Confusion", confusion ? "Yes" : "No", confusion ? 1 : 0),
            new("Urea > 7 mmol/L", $"{ureaMmolL:0.0}", ureaMmolL > 7 ? 1 : 0),
            new("RR ≥ 30", $"{respiratoryRate:0}", respiratoryRate >= 30 ? 1 : 0),
            new("SBP < 90 or DBP ≤ 60", $"{systolicBp:0}/{diastolicBp:0}", systolicBp < 90 || diastolicBp <= 60 ? 1 : 0),
            new("Age ≥ 65", $"{age}", age >= 65 ? 1 : 0),
        };
        int s = c.Sum(x => x.Points);
        var (risk, text) = s switch
        {
            <= 1 => (RiskLevel.Low, new Localized("Low severity (30-day mortality < 3 %) — consider home treatment.", "Derajat ringan (mortalitas 30 hari < 3 %) — pertimbangkan rawat jalan.")),
            2 => (RiskLevel.Medium, new Localized("Moderate severity (~9 %) — consider short inpatient stay or supervised outpatient care.", "Derajat sedang (~9 %) — pertimbangkan rawat inap singkat.")),
            _ => (RiskLevel.High, new Localized("High severity (15–40 %) — hospital admission; assess for ICU if 4–5.", "Derajat berat (15–40 %) — rawat inap; pertimbangkan ICU bila skor 4–5.")),
        };
        return new ScoreResult("CURB-65", s, 5, risk, text, c);
    }

    /// <summary>CHA₂DS₂-VASc stroke risk in atrial fibrillation (Lip et al. Chest 2010).</summary>
    public static ScoreResult Cha2Ds2Vasc(int age, Gender gender, bool chf, bool hypertension, bool diabetes, bool strokeOrTia, bool vascularDisease)
    {
        var c = new List<ScoreComponent>
        {
            new("Congestive heart failure", Yn(chf), chf ? 1 : 0),
            new("Hypertension", Yn(hypertension), hypertension ? 1 : 0),
            new("Age ≥ 75", $"{age}", age >= 75 ? 2 : 0),
            new("Diabetes", Yn(diabetes), diabetes ? 1 : 0),
            new("Stroke / TIA / thromboembolism", Yn(strokeOrTia), strokeOrTia ? 2 : 0),
            new("Vascular disease", Yn(vascularDisease), vascularDisease ? 1 : 0),
            new("Age 65–74", $"{age}", age is >= 65 and < 75 ? 1 : 0),
            new("Sex category (female)", gender.ToString(), gender == Gender.Female ? 1 : 0),
        };
        int s = c.Sum(x => x.Points);
        int nonSex = s - (gender == Gender.Female ? 1 : 0);
        var (risk, text) = nonSex switch
        {
            0 => (RiskLevel.Low, new Localized("Low risk — anticoagulation generally not recommended.", "Risiko rendah — antikoagulan umumnya tidak direkomendasikan.")),
            1 => (RiskLevel.Medium, new Localized("Intermediate risk — consider oral anticoagulation.", "Risiko menengah — pertimbangkan antikoagulan oral.")),
            _ => (RiskLevel.High, new Localized("High risk — oral anticoagulation recommended unless contraindicated (check HAS-BLED).", "Risiko tinggi — antikoagulan oral direkomendasikan kecuali kontraindikasi (cek HAS-BLED).")),
        };
        return new ScoreResult("CHA₂DS₂-VASc", s, 9, risk, text, c);
    }

    /// <summary>HAS-BLED major-bleeding risk on anticoagulation (Pisters et al. Chest 2010).</summary>
    public static ScoreResult HasBled(bool uncontrolledHypertension, bool abnormalRenal, bool abnormalLiver, bool stroke, bool bleedingHistory, bool labileInr, bool elderly, bool drugs, bool alcohol)
    {
        bool[] items = [uncontrolledHypertension, abnormalRenal, abnormalLiver, stroke, bleedingHistory, labileInr, elderly, drugs, alcohol];
        string[] names = ["Hypertension (SBP > 160)", "Abnormal renal function", "Abnormal liver function", "Stroke", "Bleeding history", "Labile INR", "Elderly (> 65)", "Antiplatelet / NSAID", "Alcohol ≥ 8 drinks/week"];
        var c = items.Select((b, i) => new ScoreComponent(names[i], Yn(b), b ? 1 : 0)).ToList();
        int s = c.Sum(x => x.Points);
        return new ScoreResult("HAS-BLED", s, 9, s >= 3 ? RiskLevel.High : s == 2 ? RiskLevel.Medium : RiskLevel.Low,
            s >= 3 ? new("High bleeding risk — address modifiable factors and review regularly (not a reason by itself to withhold anticoagulation).",
                         "Risiko perdarahan tinggi — atasi faktor yang dapat dimodifikasi dan evaluasi berkala.")
                   : new("Low-to-moderate bleeding risk.", "Risiko perdarahan rendah-sedang."), c);
    }

    /// <summary>Glasgow Coma Scale (Teasdale &amp; Jennett 1974): eye 1–4, verbal 1–5, motor 1–6.</summary>
    public static ScoreResult Gcs(int eye, int verbal, int motor)
    {
        Guard.InRange(eye, 1, 4, nameof(eye)); Guard.InRange(verbal, 1, 5, nameof(verbal)); Guard.InRange(motor, 1, 6, nameof(motor));
        int s = eye + verbal + motor;
        var c = new List<ScoreComponent> { new("Eye (E)", $"{eye}", eye), new("Verbal (V)", $"{verbal}", verbal), new("Motor (M)", $"{motor}", motor) };
        var (risk, text) = s switch
        {
            <= 8 => (RiskLevel.Critical, new Localized("Severe brain injury (GCS ≤ 8) — protect the airway.", "Cedera otak berat (GCS ≤ 8) — amankan jalan napas.")),
            <= 12 => (RiskLevel.High, new Localized("Moderate brain injury (9–12).", "Cedera otak sedang (9–12).")),
            < 15 => (RiskLevel.Medium, new Localized("Mild brain injury (13–14).", "Cedera otak ringan (13–14).")),
            _ => (RiskLevel.Low, new Localized("Fully conscious (15).", "Sadar penuh (15).")),
        };
        return new ScoreResult("GCS", s, 15, risk, text, c);
    }

    /// <summary>APGAR newborn score (each item 0–2): appearance, pulse, grimace, activity, respiration.</summary>
    public static ScoreResult Apgar(int appearance, int pulse, int grimace, int activity, int respiration)
    {
        int[] v = [appearance, pulse, grimace, activity, respiration];
        foreach (var x in v) Guard.InRange(x, 0, 2, "apgar item");
        string[] n = ["Appearance", "Pulse", "Grimace", "Activity", "Respiration"];
        var c = v.Select((x, i) => new ScoreComponent(n[i], $"{x}", x)).ToList();
        int s = v.Sum();
        var (risk, text) = s switch
        {
            >= 7 => (RiskLevel.Low, new Localized("Normal (7–10).", "Normal (7–10).")),
            >= 4 => (RiskLevel.Medium, new Localized("Moderately abnormal (4–6) — may need stimulation / airway support.", "Agak abnormal (4–6) — mungkin perlu stimulasi / bantuan jalan napas.")),
            _ => (RiskLevel.Critical, new Localized("Low (0–3) — immediate resuscitation.", "Rendah (0–3) — resusitasi segera.")),
        };
        return new ScoreResult("APGAR", s, 10, risk, text, c);
    }

    /// <summary>
    /// MELD (UNOS, pre-2016) — creatinine and bilirubin in mg/dL. Lab values below 1 are set to 1; creatinine is capped at 4
    /// (and set to 4 when dialysed twice in the past week).
    /// </summary>
    public static ScoreResult Meld(double creatinineMgDl, double bilirubinMgDl, double inr, bool dialysis = false)
    {
        double cr = dialysis ? 4 : Math.Clamp(creatinineMgDl, 1, 4);
        double bili = Math.Max(1, bilirubinMgDl), i = Math.Max(1, inr);
        double meld = 10 * (0.957 * Math.Log(cr) + 0.378 * Math.Log(bili) + 1.120 * Math.Log(i) + 0.643);
        int s = (int)Math.Round(Math.Clamp(meld, 6, 40), MidpointRounding.AwayFromZero);
        var c = new List<ScoreComponent> { new("Creatinine", $"{creatinineMgDl:0.00} mg/dL", 0), new("Bilirubin", $"{bilirubinMgDl:0.0} mg/dL", 0), new("INR", $"{inr:0.0}", 0) };
        var (risk, text) = s switch
        {
            < 10 => (RiskLevel.Low, new Localized("Estimated 90-day mortality ≈ 1.9 %.", "Perkiraan mortalitas 90 hari ≈ 1,9 %.")),
            < 20 => (RiskLevel.Medium, new Localized("Estimated 90-day mortality ≈ 6 %.", "Perkiraan mortalitas 90 hari ≈ 6 %.")),
            < 30 => (RiskLevel.High, new Localized("Estimated 90-day mortality ≈ 19.6 %.", "Perkiraan mortalitas 90 hari ≈ 19,6 %.")),
            < 40 => (RiskLevel.Critical, new Localized("Estimated 90-day mortality ≈ 52.6 %.", "Perkiraan mortalitas 90 hari ≈ 52,6 %.")),
            _ => (RiskLevel.Critical, new Localized("Estimated 90-day mortality ≈ 71.3 %.", "Perkiraan mortalitas 90 hari ≈ 71,3 %.")),
        };
        return new ScoreResult("MELD", s, 40, risk, text, c);
    }

    private static string Yn(bool b) => b ? "Yes" : "No";
}
