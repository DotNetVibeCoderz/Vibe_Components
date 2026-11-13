using System.Globalization;
using Medical.Core;

namespace Medical.Clinical;

/// <summary>Vasopressor / inotrope support for the SOFA cardiovascular component (doses in µg/kg/min).</summary>
public sealed record Vasopressors(double Dopamine = 0, double Dobutamine = 0, double Epinephrine = 0, double Norepinephrine = 0)
{
    public static Vasopressors None { get; } = new();
}

/// <summary>Inputs of the SOFA score; leave a component null when it was not measured (it then scores 0 and is listed as missing).</summary>
public sealed record SofaInputs
{
    public double? PaO2FiO2 { get; init; }
    public bool RespiratorySupport { get; init; }
    /// <summary>Platelets ×10³/µL.</summary>
    public double? Platelets { get; init; }
    public double? BilirubinMgDl { get; init; }
    public double? MeanArterialPressure { get; init; }
    public Vasopressors Vasopressors { get; init; } = Vasopressors.None;
    public int? Gcs { get; init; }
    public double? CreatinineMgDl { get; init; }
    /// <summary>Urine output mL/day.</summary>
    public double? UrineOutputMlPerDay { get; init; }
}

/// <summary>Inputs of APACHE II (worst values in the first 24 h).</summary>
public sealed record ApacheIIInputs
{
    public required double TemperatureC { get; init; }
    public required double MeanArterialPressure { get; init; }
    public required double HeartRate { get; init; }
    public required double RespiratoryRate { get; init; }
    public required double FiO2 { get; init; }
    /// <summary>PaO₂ mmHg (used when FiO₂ &lt; 0.5).</summary>
    public double? PaO2 { get; init; }
    /// <summary>A–a gradient mmHg (used when FiO₂ ≥ 0.5).</summary>
    public double? AaGradient { get; init; }
    public required double ArterialPh { get; init; }
    public required double Sodium { get; init; }
    public required double Potassium { get; init; }
    public required double CreatinineMgDl { get; init; }
    public bool AcuteRenalFailure { get; init; }
    public required double Hematocrit { get; init; }
    /// <summary>White cells ×10³/µL.</summary>
    public required double Wbc { get; init; }
    public required int Gcs { get; init; }
    public required int Age { get; init; }
    /// <summary>History of severe organ insufficiency or immunocompromise.</summary>
    public bool ChronicHealth { get; init; }
    public ApacheAdmission Admission { get; init; } = ApacheAdmission.NonOperative;
}

public enum ApacheAdmission { NonOperative, EmergencyPostoperative, ElectivePostoperative }

/// <summary>PEWS behaviour / cardiovascular / respiratory levels (0 normal … 3 most abnormal), as described in Monaghan 2005.</summary>
public sealed record PewsInputs(int Behaviour, int Cardiovascular, int Respiratory, bool QuarterHourlyNebulisersOrPostOpVomiting = false);

public enum Avpu { Alert, Voice, Pain, Unresponsive }

public static partial class ClinicalScores
{
    // ---------------------------------------------------------------- SOFA

    /// <summary>
    /// Sequential Organ Failure Assessment (Vincent et al., Intensive Care Med 1996). A rise of ≥ 2 points from baseline
    /// identifies organ dysfunction in sepsis (Sepsis-3, Singer et al., JAMA 2016).
    /// </summary>
    public static ScoreResult Sofa(SofaInputs x)
    {
        var c = new List<ScoreComponent>();
        var missing = new List<string>();
        int Add(string name, string value, int pts) { c.Add(new(name, value, pts)); return pts; }

        if (x.PaO2FiO2 is { } pf)
            Add("Respiration PaO₂/FiO₂", $"{pf:0}{(x.RespiratorySupport ? " (ventilated)" : "")}",
                pf < 100 && x.RespiratorySupport ? 4 : pf < 200 && x.RespiratorySupport ? 3 : pf < 300 ? 2 : pf < 400 ? 1 : 0);
        else missing.Add("PaO₂/FiO₂");
        if (x.Platelets is { } plt) Add("Coagulation platelets ×10³/µL", $"{plt:0}", plt < 20 ? 4 : plt < 50 ? 3 : plt < 100 ? 2 : plt < 150 ? 1 : 0);
        else missing.Add("platelets");
        if (x.BilirubinMgDl is { } bili) Add("Liver bilirubin mg/dL", F(bili), bili >= 12 ? 4 : bili >= 6 ? 3 : bili >= 2 ? 2 : bili >= 1.2 ? 1 : 0);
        else missing.Add("bilirubin");
        var v = x.Vasopressors;
        int cv = v.Dopamine > 15 || v.Epinephrine > 0.1 || v.Norepinephrine > 0.1 ? 4
            : v.Dopamine > 5 || v.Epinephrine > 0 || v.Norepinephrine > 0 ? 3
            : v.Dopamine > 0 || v.Dobutamine > 0 ? 2
            : x.MeanArterialPressure is { } map && map < 70 ? 1 : 0;
        if (x.MeanArterialPressure is null && cv == 0) missing.Add("MAP");
        Add("Cardiovascular", v == Vasopressors.None ? $"MAP {x.MeanArterialPressure?.ToString("0", CultureInfo.InvariantCulture) ?? "?"}" : "vasopressors", cv);
        if (x.Gcs is { } gcs) Add("CNS Glasgow Coma Scale", $"{gcs}", gcs < 6 ? 4 : gcs <= 9 ? 3 : gcs <= 12 ? 2 : gcs <= 14 ? 1 : 0);
        else missing.Add("GCS");
        int renal = 0;
        if (x.CreatinineMgDl is { } cr) renal = cr >= 5 ? 4 : cr >= 3.5 ? 3 : cr >= 2 ? 2 : cr >= 1.2 ? 1 : 0;
        if (x.UrineOutputMlPerDay is { } uo) renal = Math.Max(renal, uo < 200 ? 4 : uo < 500 ? 3 : 0);
        if (x.CreatinineMgDl is null && x.UrineOutputMlPerDay is null) missing.Add("creatinine / urine output");
        Add("Renal creatinine / urine output", $"{(x.CreatinineMgDl is { } cr2 ? F(cr2) + " mg/dL" : "")}{(x.UrineOutputMlPerDay is { } u ? $" {u:0} mL/d" : "")}".Trim(), renal);

        int s = c.Sum(i => i.Points);
        string miss = missing.Count == 0 ? "" : $" Not measured: {string.Join(", ", missing)}.";
        string missId = missing.Count == 0 ? "" : $" Tidak diukur: {string.Join(", ", missing)}.";
        var (risk, text) = s switch
        {
            < 2 => (RiskLevel.Low, new Localized($"SOFA {s}: no significant organ dysfunction.{miss}", $"SOFA {s}: tidak ada disfungsi organ bermakna.{missId}")),
            < 7 => (RiskLevel.Medium, new Localized($"SOFA {s}: organ dysfunction — with suspected infection this meets Sepsis-3 (in-hospital mortality ≈ 10 % or more).{miss}",
                                                    $"SOFA {s}: disfungsi organ — dengan dugaan infeksi memenuhi Sepsis-3 (mortalitas rumah sakit ≈ 10 % atau lebih).{missId}")),
            < 12 => (RiskLevel.High, new Localized($"SOFA {s}: severe multi-organ dysfunction — ICU care.{miss}", $"SOFA {s}: disfungsi multiorgan berat — perawatan ICU.{missId}")),
            _ => (RiskLevel.Critical, new Localized($"SOFA {s}: very high mortality risk (> 50 %).{miss}", $"SOFA {s}: risiko kematian sangat tinggi (> 50 %).{missId}")),
        };
        return new ScoreResult("SOFA", s, 24, risk, text, c);
    }

    // ---------------------------------------------------------------- APACHE II

    /// <summary>
    /// APACHE II (Knaus et al., Crit Care Med 1985): acute physiology (12 variables, worst in 24 h) + age + chronic health.
    /// <paramref name="diagnosticWeight"/> is the published diagnostic-category weight; with it the predicted hospital
    /// mortality is ln(R/(1−R)) = −3.517 + 0.146·score + 0.603·(emergency surgery) + weight.
    /// </summary>
    public static ScoreResult ApacheII(ApacheIIInputs x, double? diagnosticWeight = null)
    {
        var c = new List<ScoreComponent>();
        void Add(string n, double v, int p, string fmt = "0.#") => c.Add(new(n, v.ToString(fmt, CultureInfo.InvariantCulture), p));
        double t = x.TemperatureC;
        Add("Temperature °C", t, t >= 41 ? 4 : t >= 39 ? 3 : t >= 38.5 ? 1 : t >= 36 ? 0 : t >= 34 ? 1 : t >= 32 ? 2 : t >= 30 ? 3 : 4);
        double map = x.MeanArterialPressure;
        Add("Mean arterial pressure", map, map >= 160 ? 4 : map >= 130 ? 3 : map >= 110 ? 2 : map >= 70 ? 0 : map >= 50 ? 2 : 4);
        double hr = x.HeartRate;
        Add("Heart rate", hr, hr >= 180 ? 4 : hr >= 140 ? 3 : hr >= 110 ? 2 : hr >= 70 ? 0 : hr >= 55 ? 2 : hr >= 40 ? 3 : 4);
        double rr = x.RespiratoryRate;
        Add("Respiratory rate", rr, rr >= 50 ? 4 : rr >= 35 ? 3 : rr >= 25 ? 1 : rr >= 12 ? 0 : rr >= 10 ? 1 : rr >= 6 ? 2 : 4);
        if (x.FiO2 >= 0.5)
        {
            double aa = x.AaGradient ?? throw new ArgumentException("A–a gradient is required when FiO₂ ≥ 0.5.");
            Add("A–a gradient (FiO₂ ≥ 0.5)", aa, aa >= 500 ? 4 : aa >= 350 ? 3 : aa >= 200 ? 2 : 0);
        }
        else
        {
            double po2 = x.PaO2 ?? throw new ArgumentException("PaO₂ is required when FiO₂ < 0.5.");
            Add("PaO₂ (FiO₂ < 0.5)", po2, po2 > 70 ? 0 : po2 >= 61 ? 1 : po2 >= 55 ? 3 : 4);
        }
        double ph = x.ArterialPh;
        Add("Arterial pH", ph, ph >= 7.7 ? 4 : ph >= 7.6 ? 3 : ph >= 7.5 ? 1 : ph >= 7.33 ? 0 : ph >= 7.25 ? 2 : ph >= 7.15 ? 3 : 4, "0.00");
        double na = x.Sodium;
        Add("Sodium", na, na >= 180 ? 4 : na >= 160 ? 3 : na >= 155 ? 2 : na >= 150 ? 1 : na >= 130 ? 0 : na >= 120 ? 2 : na >= 111 ? 3 : 4);
        double k = x.Potassium;
        Add("Potassium", k, k >= 7 ? 4 : k >= 6 ? 3 : k >= 5.5 ? 1 : k >= 3.5 ? 0 : k >= 3 ? 1 : k >= 2.5 ? 2 : 4);
        double cr = x.CreatinineMgDl;
        int crPts = cr >= 3.5 ? 4 : cr >= 2 ? 3 : cr >= 1.5 ? 2 : cr >= 0.6 ? 0 : 2;
        Add(x.AcuteRenalFailure ? "Creatinine mg/dL (×2, acute renal failure)" : "Creatinine mg/dL", cr, x.AcuteRenalFailure ? crPts * 2 : crPts);
        double hct = x.Hematocrit;
        Add("Haematocrit %", hct, hct >= 60 ? 4 : hct >= 50 ? 2 : hct >= 46 ? 1 : hct >= 30 ? 0 : hct >= 20 ? 2 : 4);
        double wbc = x.Wbc;
        Add("White cells ×10³/µL", wbc, wbc >= 40 ? 4 : wbc >= 20 ? 2 : wbc >= 15 ? 1 : wbc >= 3 ? 0 : wbc >= 1 ? 2 : 4);
        Add("Glasgow Coma Scale (15 − GCS)", x.Gcs, 15 - Math.Clamp(x.Gcs, 3, 15), "0");
        int age = x.Age;
        Add("Age", age, age >= 75 ? 6 : age >= 65 ? 5 : age >= 55 ? 3 : age >= 45 ? 2 : 0, "0");
        int chronic = !x.ChronicHealth ? 0 : x.Admission == ApacheAdmission.ElectivePostoperative ? 2 : 5;
        c.Add(new("Chronic health", x.ChronicHealth ? x.Admission.ToString() : "No", chronic));

        int s = c.Sum(i => i.Points);
        string mortality;
        if (diagnosticWeight is { } w)
        {
            double logit = -3.517 + 0.146 * s + (x.Admission == ApacheAdmission.EmergencyPostoperative ? 0.603 : 0) + w;
            mortality = $"{100 / (1 + Math.Exp(-logit)):0.#} %";
        }
        else mortality = s switch { <= 4 => "≈ 4 %", <= 9 => "≈ 8 %", <= 14 => "≈ 15 %", <= 19 => "≈ 25 %", <= 24 => "≈ 40 %", <= 29 => "≈ 55 %", <= 34 => "≈ 75 %", _ => "≈ 85 %" };
        var risk = s switch { < 10 => RiskLevel.Low, < 20 => RiskLevel.Medium, < 30 => RiskLevel.High, _ => RiskLevel.Critical };
        return new ScoreResult("APACHE II", s, 71, risk,
            new($"APACHE II {s}: predicted hospital mortality {mortality}{(diagnosticWeight is null ? " (non-operative band average; pass the diagnostic weight for the full equation)" : "")}.",
                $"APACHE II {s}: prediksi mortalitas rumah sakit {mortality}{(diagnosticWeight is null ? " (rerata kelompok non-operatif; isi bobot diagnosis untuk persamaan lengkap)" : "")}."), c);
    }

    // ---------------------------------------------------------------- PEWS

    /// <summary>
    /// Brighton Paediatric Early Warning Score (Monaghan, Paediatr Nurs 2005): behaviour, cardiovascular and respiratory
    /// 0–3 each, +2 for quarter-hourly nebulisers or persistent post-operative vomiting.
    /// </summary>
    public static ScoreResult Pews(PewsInputs x)
    {
        foreach (var v in new[] { x.Behaviour, x.Cardiovascular, x.Respiratory })
            if (v is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(x), "Each PEWS component is 0–3.");
        string[] beh = ["Playing / appropriate", "Sleeping", "Irritable", "Lethargic / confused / reduced response to pain"];
        string[] cvs = ["Pink, CRT 1–2 s", "Pale or CRT 3 s", "Grey or CRT 4 s or HR +20 above normal", "Grey and mottled or CRT ≥ 5 s or HR +30 or bradycardia"];
        string[] resp = ["Normal, no recession", "RR +10 or accessory muscles or FiO₂ ≥ 30 % / 3 L/min", "RR +20 or recession / tracheal tug or FiO₂ ≥ 40 % / 6 L/min", "RR 5 below normal with recession / grunting or FiO₂ ≥ 50 % / 8 L/min"];
        var c = new List<ScoreComponent>
        {
            new("Behaviour", beh[x.Behaviour], x.Behaviour),
            new("Cardiovascular", cvs[x.Cardiovascular], x.Cardiovascular),
            new("Respiratory", resp[x.Respiratory], x.Respiratory),
            new("Nebulisers ¼-hourly / post-op vomiting", Yn(x.QuarterHourlyNebulisersOrPostOpVomiting), x.QuarterHourlyNebulisersOrPostOpVomiting ? 2 : 0),
        };
        int s = c.Sum(i => i.Points);
        bool anyThree = x.Behaviour == 3 || x.Cardiovascular == 3 || x.Respiratory == 3;
        var (risk, text) = (s, anyThree) switch
        {
            (>= 5, _) or (_, true) => (RiskLevel.High, new Localized($"PEWS {s}: urgent medical review (score ≥ 5 or a 3 in any category); consider the outreach / resuscitation team.",
                                                                     $"PEWS {s}: evaluasi medis segera (skor ≥ 5 atau nilai 3 di satu kategori); pertimbangkan tim tanggap cepat.")),
            (4, _) => (RiskLevel.Medium, new Localized("PEWS 4: medical review and increased observations.", "PEWS 4: evaluasi dokter dan tingkatkan observasi.")),
            (3, _) => (RiskLevel.LowMedium, new Localized("PEWS 3: inform the nurse in charge; observe more often.", "PEWS 3: laporkan ke perawat penanggung jawab; observasi lebih sering.")),
            _ => (RiskLevel.Low, new Localized($"PEWS {s}: continue routine observations.", $"PEWS {s}: lanjutkan observasi rutin.")),
        };
        return new ScoreResult("PEWS", s, 13, risk, text, c);
    }

    // ---------------------------------------------------------------- MEOWS

    /// <summary>
    /// Modified Early Obstetric Warning Score with red / yellow triggers (Singh et al., Anaesthesia 2012). One red or
    /// two yellow triggers call for urgent obstetric review. Score = 2 per red + 1 per yellow trigger.
    /// </summary>
    public static ScoreResult Meows(double respiratoryRate, double spo2, double temperatureC, double systolicBp, double diastolicBp, double heartRate, Avpu consciousness)
    {
        var c = new List<ScoreComponent>();
        int reds = 0, yellows = 0;
        void Trig(string name, string value, bool red, bool yellow)
        {
            c.Add(new(name, value, red ? 2 : yellow ? 1 : 0));
            if (red) reds++; else if (yellow) yellows++;
        }
        Trig("Respiratory rate", $"{respiratoryRate:0}", respiratoryRate < 10 || respiratoryRate > 30, respiratoryRate is >= 21 and <= 30);
        Trig("SpO₂ %", $"{spo2:0}", spo2 < 95, false);
        Trig("Temperature °C", F(temperatureC), temperatureC < 35 || temperatureC > 38, temperatureC is >= 35 and < 36);
        Trig("Systolic BP", $"{systolicBp:0}", systolicBp < 90 || systolicBp > 160, systolicBp is >= 150 and <= 160 or >= 90 and < 100);
        Trig("Diastolic BP", $"{diastolicBp:0}", diastolicBp > 100, diastolicBp is >= 90 and <= 100);
        Trig("Heart rate", $"{heartRate:0}", heartRate < 40 || heartRate > 120, heartRate is > 100 and <= 120 or >= 40 and < 50);
        Trig("Consciousness (AVPU)", consciousness.ToString(), consciousness is Avpu.Pain or Avpu.Unresponsive, consciousness == Avpu.Voice);
        int s = c.Sum(i => i.Points);
        bool urgent = reds >= 1 || yellows >= 2;
        return new ScoreResult("MEOWS", s, 14, urgent ? (reds >= 2 ? RiskLevel.Critical : RiskLevel.High) : yellows == 1 ? RiskLevel.LowMedium : RiskLevel.Low,
            urgent
                ? new Localized($"{reds} red / {yellows} yellow trigger(s): urgent obstetric review.", $"{reds} pemicu merah / {yellows} kuning: evaluasi obstetri segera.")
                : new Localized($"{yellows} yellow trigger(s): continue observations.", $"{yellows} pemicu kuning: lanjutkan observasi."), c);
    }

    // ---------------------------------------------------------------- Wells / PERC

    /// <summary>
    /// Wells score for pulmonary embolism (Wells et al., Thromb Haemost 2000); two-tier: &gt; 4 = PE likely. The published
    /// score has half points: <see cref="ScoreResult.ExactScore"/> holds it, while <c>Score</c> / <c>Points</c> are in half-point units.
    /// </summary>
    public static ScoreResult WellsPe(bool dvtSigns, bool peMostLikely, double heartRate, bool immobilisationOrRecentSurgery, bool previousDvtOrPe, bool haemoptysis, bool malignancy)
    {
        (string Name, string Value, bool Hit, double Weight)[] items =
        [
            ("Clinical signs of DVT", Yn(dvtSigns), dvtSigns, 3), ("PE is the most likely diagnosis", Yn(peMostLikely), peMostLikely, 3),
            ("Heart rate > 100", $"{heartRate:0}", heartRate > 100, 1.5), ("Immobilisation ≥ 3 d or surgery in 4 wk", Yn(immobilisationOrRecentSurgery), immobilisationOrRecentSurgery, 1.5),
            ("Previous DVT / PE", Yn(previousDvtOrPe), previousDvtOrPe, 1.5), ("Haemoptysis", Yn(haemoptysis), haemoptysis, 1), ("Malignancy", Yn(malignancy), malignancy, 1),
        ];
        var c = items.Select(i => new ScoreComponent(i.Name, i.Value, i.Hit ? (int)(i.Weight * 2) : 0) { ExactPoints = i.Hit ? i.Weight : 0 }).ToList();
        double s = items.Where(i => i.Hit).Sum(i => i.Weight);
        bool likely = s > 4;
        return new ScoreResult("Wells PE", (int)Math.Round(s * 2), 25, likely ? RiskLevel.High : RiskLevel.Low,
            likely ? new Localized($"Wells {s:0.#} — PE likely: CT pulmonary angiography (D-dimer does not exclude).", $"Wells {s:0.#} — kemungkinan EP: CT angiografi paru (D-dimer tidak menyingkirkan).")
                   : new Localized($"Wells {s:0.#} — PE unlikely: D-dimer; if negative PE is excluded. Apply PERC first in low pretest probability.",
                                   $"Wells {s:0.#} — EP tidak mungkin: periksa D-dimer; bila negatif EP tersingkir. Terapkan PERC lebih dulu pada probabilitas rendah."), c)
        { ExactScore = s };
    }

    /// <summary>Wells score for deep-vein thrombosis (Wells et al., NEJM 2003): ≥ 2 = DVT likely.</summary>
    public static ScoreResult WellsDvt(bool activeCancer, bool paralysisOrCast, bool bedriddenOrSurgery, bool tendernessAlongVeins, bool entireLegSwollen,
        bool calfSwellingOver3Cm, bool pittingOedema, bool collateralVeins, bool previousDvt, bool alternativeAsLikely)
    {
        (string, bool, int)[] items =
        [
            ("Active cancer", activeCancer, 1), ("Paralysis / recent plaster cast", paralysisOrCast, 1), ("Bedridden ≥ 3 d or major surgery in 12 wk", bedriddenOrSurgery, 1),
            ("Tenderness along deep veins", tendernessAlongVeins, 1), ("Entire leg swollen", entireLegSwollen, 1), ("Calf swelling > 3 cm", calfSwellingOver3Cm, 1),
            ("Pitting oedema (symptomatic leg)", pittingOedema, 1), ("Collateral superficial veins", collateralVeins, 1), ("Previous DVT", previousDvt, 1),
            ("Alternative diagnosis at least as likely", alternativeAsLikely, -2),
        ];
        var c = items.Select(i => new ScoreComponent(i.Item1, Yn(i.Item2), i.Item2 ? i.Item3 : 0)).ToList();
        int s = c.Sum(i => i.Points);
        return new ScoreResult("Wells DVT", s, 9, s >= 2 ? RiskLevel.High : RiskLevel.Low,
            s >= 2 ? new Localized($"Wells {s} — DVT likely: proximal leg ultrasound.", $"Wells {s} — kemungkinan TVD: USG vena proksimal.")
                   : new Localized($"Wells {s} — DVT unlikely: D-dimer; if negative DVT is excluded.", $"Wells {s} — TVD tidak mungkin: D-dimer; bila negatif TVD tersingkir."), c);
    }

    /// <summary>
    /// Pulmonary Embolism Rule-out Criteria (Kline et al., J Thromb Haemost 2004). Valid only when the clinician's gestalt
    /// pretest probability is low (&lt; 15 %); all eight negative = PE can be ruled out without D-dimer.
    /// </summary>
    public static ScoreResult Perc(int age, double heartRate, double spo2, bool unilateralLegSwelling, bool haemoptysis, bool recentSurgeryOrTrauma, bool previousDvtOrPe, bool hormoneUse)
    {
        var c = new List<ScoreComponent>
        {
            new("Age ≥ 50", $"{age}", age >= 50 ? 1 : 0), new("Heart rate ≥ 100", $"{heartRate:0}", heartRate >= 100 ? 1 : 0),
            new("SpO₂ < 95 %", $"{spo2:0}", spo2 < 95 ? 1 : 0), new("Unilateral leg swelling", Yn(unilateralLegSwelling), unilateralLegSwelling ? 1 : 0),
            new("Haemoptysis", Yn(haemoptysis), haemoptysis ? 1 : 0), new("Surgery / trauma ≤ 4 wk", Yn(recentSurgeryOrTrauma), recentSurgeryOrTrauma ? 1 : 0),
            new("Previous DVT / PE", Yn(previousDvtOrPe), previousDvtOrPe ? 1 : 0), new("Hormone use", Yn(hormoneUse), hormoneUse ? 1 : 0),
        };
        int s = c.Sum(i => i.Points);
        return new ScoreResult("PERC", s, 8, s == 0 ? RiskLevel.Low : RiskLevel.Medium,
            s == 0 ? new Localized("PERC negative — with low gestalt probability, PE is ruled out (no D-dimer needed).", "PERC negatif — dengan probabilitas klinis rendah, EP tersingkir (tanpa D-dimer).")
                   : new Localized($"PERC positive ({s} criteria) — cannot rule out; proceed with Wells / D-dimer.", $"PERC positif ({s} kriteria) — belum dapat disingkirkan; lanjutkan Wells / D-dimer."), c);
    }

    // ---------------------------------------------------------------- Child-Pugh

    /// <summary>Child-Turcotte-Pugh classification of cirrhosis severity (Pugh et al., Br J Surg 1973). Class A 5–6, B 7–9, C 10–15.</summary>
    public static ScoreResult ChildPugh(double bilirubinMgDl, double albuminGDl, double inr, int ascites, int encephalopathy)
    {
        if (ascites is < 0 or > 2 || encephalopathy is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(ascites), "Ascites and encephalopathy: 0 none, 1 mild / grade 1–2, 2 moderate-severe / grade 3–4.");
        var c = new List<ScoreComponent>
        {
            new("Bilirubin mg/dL", F(bilirubinMgDl), bilirubinMgDl < 2 ? 1 : bilirubinMgDl <= 3 ? 2 : 3),
            new("Albumin g/dL", F(albuminGDl), albuminGDl > 3.5 ? 1 : albuminGDl >= 2.8 ? 2 : 3),
            new("INR", F(inr), inr < 1.7 ? 1 : inr <= 2.3 ? 2 : 3),
            new("Ascites", new[] { "None", "Mild", "Moderate–severe" }[ascites], ascites + 1),
            new("Encephalopathy", new[] { "None", "Grade 1–2", "Grade 3–4" }[encephalopathy], encephalopathy + 1),
        };
        int s = c.Sum(i => i.Points);
        string cls = s <= 6 ? "A" : s <= 9 ? "B" : "C";
        return new ScoreResult("Child-Pugh", s, 15, cls == "A" ? RiskLevel.Low : cls == "B" ? RiskLevel.Medium : RiskLevel.High,
            new($"Child-Pugh class {cls} ({s}) — {(cls == "A" ? "well-compensated" : cls == "B" ? "significant functional compromise" : "decompensated")}; adjust hepatically cleared drugs.",
                $"Child-Pugh kelas {cls} ({s}) — {(cls == "A" ? "terkompensasi baik" : cls == "B" ? "gangguan fungsi bermakna" : "dekompensasi")}; sesuaikan obat yang dimetabolisme hati."), c);
    }

    /// <summary>Child-Pugh class letter (A / B / C) from a <see cref="ChildPugh"/> score.</summary>
    public static string ChildPughClass(int score) => score <= 6 ? "A" : score <= 9 ? "B" : "C";

    private static string F(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture);
}
