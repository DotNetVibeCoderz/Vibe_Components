using Medical.AI.Assistant;
using Medical.AI.Guidelines;
using Medical.AI.Learning;
using Medical.AI.Nlp;
using Medical.Clinical;
using Medical.Core;
using Medical.Rules;
using Medical.Visualization;

namespace Medical.Gallery.Shared;

public sealed record IcuView(ScoreResult Sofa, string SofaBars, ScoreResult Apache, ScoreResult Pews, ScoreResult Meows, ScoreResult WellsPe, ScoreResult Perc, double Severity);

public sealed record CvView(CvRiskResult Pce, CvRiskResult Framingham, string ByAge, CvRiskInputs Inputs);

public sealed record GrowthVisit(int Months, double LengthCm, double WeightKg, GrowthResult LengthForAge, GrowthResult WeightForAge, GrowthResult WeightForLength);

public sealed record GrowthView(string? Chart, IReadOnlyList<GrowthVisit> Visits, GrowthIndicator Indicator, string? Error);

public sealed record DrugSafetyView(IReadOnlyList<string> Medications, IReadOnlyList<InteractionFinding> Interactions, double Creatinine, double CrCl, double Egfr,
    IReadOnlyList<OrganDoseAdvice> Renal, OrganDoseAdvice Apixaban, string ChildPugh, IReadOnlyList<OrganDoseAdvice> Hepatic);

public sealed record TextSegment(string Text, ClinicalEntity? Entity);

public sealed record NlpView(string Note, IReadOnlyList<TextSegment> Segments, IReadOnlyList<ClinicalEntity> Entities, IReadOnlyList<CodingSuggestion> Codes);

public sealed record GuidelineView(string Question, IReadOnlyList<RetrievedPassage> Passages, GuidelineAnswer Answer, int Chunks);

public sealed record FederatedView(FederatedResult Result, BinaryMetrics Federated, BinaryMetrics Pooled, IReadOnlyList<(string Site, int Rows, double Prevalence)> Sites,
    string LossChart, string PrivacyChart, ModelCard Card, DriftReport Drift, double Noise, double Shift);

public static partial class Scenarios
{
    // ------------------------------------------------------------------ ICU & obstetric scores

    /// <summary>One septic patient whose physiology worsens with <paramref name="severity"/> (0 well … 1 moribund).</summary>
    public static IcuView Icu(double severity = 0.6)
    {
        double s = Math.Clamp(severity, 0, 1);
        double L(double a, double b) => a + (b - a) * s;
        var sofa = ClinicalScores.Sofa(new SofaInputs
        {
            PaO2FiO2 = L(420, 90), RespiratorySupport = s > 0.45, Platelets = L(260, 35), BilirubinMgDl = L(0.8, 7),
            MeanArterialPressure = L(85, 58), Vasopressors = s > 0.55 ? new Vasopressors(Norepinephrine: L(-0.1, 0.25)) : Vasopressors.None,
            Gcs = (int)Math.Round(L(15, 7)), CreatinineMgDl = L(0.9, 4.2), UrineOutputMlPerDay = L(1500, 300),
        });
        var bars = Charts.ScoreBars(sofa.Components.Select(c => (c.Parameter.Split(' ')[0], (double)c.Points, 4.0)).ToList());
        var apache = ClinicalScores.ApacheII(new ApacheIIInputs
        {
            TemperatureC = L(37, 39.8), MeanArterialPressure = L(88, 55), HeartRate = L(82, 138), RespiratoryRate = L(16, 34), FiO2 = s > 0.45 ? 0.6 : 0.3,
            PaO2 = L(95, 58), AaGradient = L(150, 380), ArterialPh = L(7.4, 7.18), Sodium = L(140, 128), Potassium = L(4.2, 5.8), CreatinineMgDl = L(0.9, 4.2),
            AcuteRenalFailure = s > 0.6, Hematocrit = L(40, 27), Wbc = L(9, 24), Gcs = (int)Math.Round(L(15, 7)), Age = 67,
        }, diagnosticWeight: 0.113);   // sepsis (non-operative) weight from Knaus 1985
        var pews = ClinicalScores.Pews(new PewsInputs((int)Math.Round(L(0, 3)), (int)Math.Round(L(0, 2.6)), (int)Math.Round(L(0, 2.8)), s > 0.7));
        var meows = ClinicalScores.Meows(L(16, 32), L(98, 92), L(36.8, 38.6), L(118, 86), L(74, 52), L(84, 128), s > 0.8 ? Avpu.Voice : Avpu.Alert);
        var wells = ClinicalScores.WellsPe(s > 0.5, s > 0.7, L(84, 124), true, false, false, s > 0.3);
        var perc = ClinicalScores.Perc(67, L(84, 124), L(98, 92), false, false, true, false, false);
        return new IcuView(sofa, bars, apache, pews, meows, wells, perc, s);
    }

    // ------------------------------------------------------------------ cardiovascular risk

    public static CvView CvRisk(int age = 58, bool female = false, double sbp = 142, double tc = 220, double hdl = 42, bool treated = false, bool smoker = true, bool diabetes = false)
    {
        var x = new CvRiskInputs(age, female ? Gender.Female : Gender.Male, tc, hdl, sbp, treated, smoker, diabetes);
        var pce = Clinical.CardiovascularRisk.Ascvd(x);
        var frs = Clinical.CardiovascularRisk.Framingham(x);
        var ages = Enumerable.Range(40, 35).ToList();   // 40–74 (both equations)
        var chart = Charts.XyChart(
        [
            new XySeries("PCE", ages.Select(a => ((double)a, Clinical.CardiovascularRisk.Ascvd(x with { Age = a }).TenYearRiskPercent)).ToList(), SvgTheme.Teal),
            new XySeries("Framingham", ages.Select(a => ((double)a, Clinical.CardiovascularRisk.Framingham(x with { Age = a }).TenYearRiskPercent)).ToList(), SvgTheme.Amber),
            new XySeries("7.5 %", [(40, 7.5), (74, 7.5)], SvgTheme.Muted, Dashed: true),
            new XySeries("20 %", [(40, 20), (74, 20)], SvgTheme.Red, Dashed: true),
        ], 40, 74, 0, 60, "age (years)", "10-year risk %", [(age, $"{age} y")], width: 900, height: 280);
        return new CvView(pce, frs, chart, x);
    }

    // ------------------------------------------------------------------ WHO growth

    private static readonly Lazy<Task<(WhoGrowthStandards? Who, string? Error)>> Who = new(async () =>
    {
        try { return (await WhoGrowthStandards.DownloadAsync(Path.Combine(Path.GetTempPath(), "mednet-who-growth")), null); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return (null, e.Message); }
    });

    /// <summary>Siti, a girl whose length falters after weaning — the typical course of stunting.</summary>
    public static readonly IReadOnlyList<(int Months, double Length, double Weight)> SitiVisits =
    [
        (0, 49.0, 3.1), (3, 59.5, 5.6), (6, 64.6, 6.9), (9, 68.0, 7.5), (12, 70.6, 8.0), (15, 72.6, 8.4), (18, 74.3, 8.7), (24, 77.5, 9.4),
    ];

    public static async Task<GrowthView> Growth(GrowthIndicator indicator = GrowthIndicator.LengthHeightForAge, string lang = "en")
    {
        var (who, error) = await Who.Value;
        if (who is null) return new GrowthView(null, [], indicator, error);
        var visits = SitiVisits.Select(v =>
        {
            double days = v.Months * 30.4375;
            return new GrowthVisit(v.Months, v.Length, v.Weight,
                who.Assess(GrowthIndicator.LengthHeightForAge, Gender.Female, days, v.Length, v.Length, LengthMeasure.Recumbent),
                who.Assess(GrowthIndicator.WeightForAge, Gender.Female, days, v.Weight),
                who.Assess(GrowthIndicator.WeightForLengthHeight, Gender.Female, days, v.Weight, v.Length, LengthMeasure.Recumbent));
        }).ToList();
        var points = SitiVisits.Select(v => (v.Months * 30.4375, indicator == GrowthIndicator.WeightForAge ? v.Weight : v.Length)).ToList();
        var chart = Charts.GrowthChart(who, indicator, Gender.Female, 0, 731, points, lang);
        return new GrowthView(chart, visits, indicator, null);
    }

    // ------------------------------------------------------------------ drug safety & organ dosing

    public static readonly IReadOnlyList<string> SafetyMedications = ["rivaroxaban", "clopidogrel", "omeprazole", "simvastatin", "clarithromycin", "metformin", "gabapentin"];

    /// <summary>An 80-year-old woman (55 kg) on seven drugs; creatinine and liver class drive the dosing advice.</summary>
    public static async Task<DrugSafetyView> DrugSafety(double creatinine = 1.6, string childPugh = "A")
    {
        var source = CompositeInteractionSource.Default();
        var interactions = await source.CheckAsync(SafetyMedications);
        double crcl = Clinical.Calculators.CockcroftGault(creatinine, 80, 55, Gender.Female);
        double egfr = Clinical.Calculators.EgfrCkdEpi2021(creatinine, 80, Gender.Female).Egfr;
        var renal = SafetyMedications.SelectMany(d => OrganDosing.Default.Renal(d, crcl, egfr, regimen: d == "rivaroxaban" ? "AF stroke prevention" : null)).ToList();
        var hepatic = SafetyMedications.Append("apixaban").Select(d => OrganDosing.Default.Hepatic(d, childPugh)).OfType<OrganDoseAdvice>().ToList();
        return new DrugSafetyView(SafetyMedications, interactions, creatinine, crcl, egfr, renal, OrganDosing.ApixabanAf(80, 55, creatinine), childPugh, hepatic);
    }

    // ------------------------------------------------------------------ clinical NLP

    public static readonly IReadOnlyDictionary<string, string> SampleNotes = new Dictionary<string, string>
    {
        ["id"] = "Pasien perempuan 54 tahun datang dengan demam 4 hari, nyeri perut dan muntah. Riwayat darah tinggi dan kencing manis, rutin minum metformin. Tidak ada sesak napas, tidak ada batuk. Trombosit turun; kesan demam berdarah. Riwayat keluarga stroke.",
        ["en"] = "62-year-old man admitted with shortness of breath and fever for 3 days. Known COPD and atrial fibrillation on warfarin. Denies chest pain but reports cough with sputum. Chest X-ray consistent with pneumonia. No evidence of heart failure. Family history of diabetes.",
    };

    private static readonly Lazy<TerminologyEntityRecognizer> Recognizer = new(() => new TerminologyEntityRecognizer());

    public static NlpView Nlp(string note)
    {
        var coder = new Icd10Coder(Recognizer.Value);
        var codes = coder.Suggest(note);
        var ents = coder.LastEntities;
        var segs = new List<TextSegment>();
        int at = 0;
        foreach (var e in ents.OrderBy(e => e.Start))
        {
            if (e.Start > at) segs.Add(new(note[at..e.Start], null));
            segs.Add(new(note[e.Start..e.End], e));
            at = e.End;
        }
        if (at < note.Length) segs.Add(new(note[at..], null));
        return new NlpView(note, segs, ents, codes);
    }

    // ------------------------------------------------------------------ guidelines

    private static readonly Lazy<GuidelineRetriever> GuidelineIndex = new(() => new GuidelineRetriever(GuidelineCorpus.Sample()));

    public static readonly IReadOnlyList<string> GuidelineQuestions =
    [
        "Berapa lama pengobatan TB paru sensitif obat?",
        "What fluid rate for dengue with warning signs?",
        "Obat lini pertama diabetes melitus tipe 2 dan batas eGFR metformin?",
        "First-line vasopressor and fluid volume in septic shock?",
        "Pengobatan malaria vivaks tanpa komplikasi?",
    ];

    public static async Task<GuidelineView> Guidelines(string question, string lang = "en", IClinicalLlm? llm = null)
    {
        var passages = GuidelineIndex.Value.Search(question, 4);
        var answer = await new GuidelineAnswerer(GuidelineIndex.Value, llm).AskAsync(question, lang);
        return new GuidelineView(question, passages, answer, GuidelineCorpus.Sample().Chunks.Count);
    }

    // ------------------------------------------------------------------ federated learning & drift

    private static readonly Lazy<(string[] Names, FederatedSite[] Sites, List<(double[] X, bool Y)> Test, LogisticModel Pooled)> Federation = new(() =>
    {
        var (names, sites, test) = SyntheticFederation.Create();
        return (names, sites, test, FederatedLogisticRegression.TrainCentralised(names, sites.SelectMany(SyntheticFederation.Rows).ToList()));
    });

    private static readonly Lazy<string> PrivacyCurve = new(() =>
    {
        var f = Federation.Value;
        double[] sigmas = [0.25, 0.5, 1, 1.5, 2, 3, 4];
        var auc = sigmas.Select(s => (s, FederatedLogisticRegression.Train(f.Names, f.Sites, new FederatedOptions { ClipNorm = 0.5, NoiseMultiplier = s }).Model.Evaluate(f.Test).Auc)).ToList();
        double pooled = Federation.Value.Pooled.Evaluate(Federation.Value.Test).Auc;
        return Charts.XyChart(
        [
            new XySeries("AUC", auc.Select(p => (p.s, p.Auc)).ToList(), SvgTheme.Teal, Markers: true),
            new XySeries("pooled", [(0.25, pooled), (4, pooled)], SvgTheme.Muted, Dashed: true),
        ], 0.25, 4, 0.5, 0.85, "noise multiplier σ", "test AUC",
        new double[] { 0.5, 1, 2, 4 }.Select(s => (s, $"ε {FederatedResult.EpsilonFor(30, s):0}")).ToList());
    });

    public static FederatedView Federated(double noise = 0, double shift = 0.8, bool secure = true)
    {
        var f = Federation.Value;
        var result = FederatedLogisticRegression.Train(f.Names, f.Sites, new FederatedOptions
        {
            SecureAggregation = secure, ClipNorm = noise > 0 ? 0.5 : null, NoiseMultiplier = noise,
        });
        var loss = Charts.XyChart([new XySeries(noise > 0 ? $"σ {noise:0.##}" : "FedAvg", result.History.Select(h => ((double)h.Round, h.MeanLoss)).ToList(), SvgTheme.Teal)],
            1, result.History.Count, 0.4, 0.7, "round", "mean site loss", yFormat: "0.00");
        var p = f.Test.Select(r => result.Model.Predict(r.X)).ToList();
        var card = ModelCard.Create("Deterioration risk (federated)", "Flag adult inpatients at risk of deterioration within 48 h for clinician review.", p, f.Test.Select(r => r.Y).ToList(),
            new Dictionary<string, IReadOnlyList<string>> { ["age"] = f.Test.Select(r => r.X[1] >= 65 ? "≥ 65" : "< 65").ToList(), ["lactate"] = f.Test.Select(r => r.X[0] >= 2 ? "≥ 2" : "< 2").ToList() })
            with { Features = f.Names, Date = Today, TrainingData = "Synthetic cohorts at three simulated sites; rows never pooled.", Limitations = ["Synthetic data — retrain and validate on local data."] };
        var monitor = new DriftMonitor(f.Names, f.Test.Select(r => r.X).ToList(), p, f.Test.Select(r => r.Y).ToList());
        var current = SyntheticFederation.Cohort(1500, 77, shift);
        var drift = monitor.Check(current.Select(r => r.X).ToList(), current.Select(r => result.Model.Predict(r.X)).ToList(), current.Select(r => r.Y).ToList());
        var sites = f.Sites.Select(s => (s.Name, s.Count, Math.Round(SyntheticFederation.Rows(s).Count(r => r.Y) / (double)s.Count, 3))).ToList();
        return new FederatedView(result, result.Model.Evaluate(f.Test), f.Pooled.Evaluate(f.Test), sites, loss, PrivacyCurve.Value, card, drift, noise, shift);
    }
}
