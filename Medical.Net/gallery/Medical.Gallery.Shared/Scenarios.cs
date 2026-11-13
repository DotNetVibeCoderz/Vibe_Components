using System.Globalization;
using FellowOakDicom;
using Medical.AI;
using Medical.AI.Assistant;
using Medical.Clinical;
using Medical.Core;
using Medical.Dicom;
using Medical.Fhir;
using Medical.Hl7;
using Medical.Native;
using Medical.Privacy;
using Medical.Rules;
using Medical.Signals;
using Medical.Terminology;
using Medical.Visualization;
using Medical.Emr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Medical.Gallery.Shared;

public sealed record PatientCard(string Name, string Mrn, string Sex, int Age, string Location, IReadOnlyList<string> Problems);

public sealed record NewsView(PatientCard Patient, ScoreResult News2, ScoreResult QSofa, string ObservationChart, string Trend, string Gauge, double Deterioration);

public sealed record CdsView(PatientCard Patient, IReadOnlyList<ClinicalAlert> Alerts, IReadOnlyList<WorkflowRuleResult> Triage, IReadOnlyList<string> Medications, IReadOnlyList<string> Allergies, IReadOnlyList<string> Rules);

public sealed record CalcView(EgfrResult Egfr, double CrCl, BmiResult BmiWho, BmiResult BmiAsia, double Bsa, DoseRecommendation Dose, double FluidsMlH, double Map);

public sealed record DicomView(byte[] Png, DicomImageInfo Info, PixelStats Stats, string Histogram, WindowPreset Window, int Slice, int Slices, KernelBackend Backend, double RenderMs);

public sealed record HemorrhageView(byte[] Overlay, HemorrhageReport Report, int Slice, int Slices);

public sealed record EcgView(string Strip, EcgAnalysis Analysis, Rhythm Simulated, double HeartRate);

public sealed record FhirView(string Json, int Entries, int Issues, IReadOnlyList<(string Section, int Items)> Sections, IReadOnlyList<string> FhirPath);

public sealed record Hl7View(string Er7, string Ack, string FhirJson, IReadOnlyList<ClinicalObservation> Observations, string Patient);

public sealed record TermView(IReadOnlyList<SearchHit> Hits, Concept? Selected, IReadOnlyList<Concept> Ancestors, ConceptMapping? Map, CodeValidation Loinc, CodeValidation Sctid);

public sealed record RiskView(RiskPrediction Deterioration, RiskModelMetrics Metrics, string Drivers, RiskPrediction Diabetes, string DiabetesDrivers, RiskModelMetrics DiabetesMetrics);

public sealed record PrivacyView(string Original, string Redacted, IReadOnlyList<PhiSpan> Findings, IReadOnlyDictionary<PhiType, int> Counts, IReadOnlyList<(string Label, string Value)> Masks);

public sealed record EmrView(int Patients, IReadOnlyList<Patient> Search, IReadOnlyList<AuditEvent> Audit, string Provider);

/// <summary>Computes every gallery demo. Results are deterministic so screenshots and docs stay reproducible.</summary>
public static class Scenarios
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Lazy<IReadOnlyList<DicomFile>> HeadCt = new(() => DicomPhantom.CreateSeries(PhantomKind.Head, 24, 256));
    private static readonly Lazy<IReadOnlyList<DicomFile>> ChestCt = new(() => DicomPhantom.CreateSeries(PhantomKind.Chest, 16, 256));
    private static readonly Lazy<DeteriorationRiskModel> DetModel = new(() => { var m = new DeteriorationRiskModel(); m.Train(); return m; });
    private static readonly Lazy<DiabetesRiskModel> DmModel = new(() => { var m = new DiabetesRiskModel(); m.Train(); return m; });
    private static readonly DateOnly Today = new(2026, 11, 13);

    public static PatientCard Card(PatientRecord r) => new(
        r.Patient.Name.Full, r.Patient.Mrn, r.Patient.Gender.ToString(), r.Patient.AgeAt(Today),
        r.Encounters.FirstOrDefault()?.Location ?? "—", r.Conditions.Select(c => $"{c.Code.Code} · {c.Code.Display}").ToArray());

    // ---------------------------------------------------------------- home

    /// <summary>The home hero: 10 s of ECG on millimetre paper with R peaks detected by the Rust kernel.</summary>
    public static (string Svg, EcgAnalysis Analysis) HeroEcg(double seconds = 10, double mmPx = 4.2)
    {
        var sig = EcgSimulator.Generate(seconds + 2, 74, Rhythm.NormalSinus, noise: 0.012, seed: 26);
        var a = EcgAnalyzer.Analyze(sig);
        return (Charts.EcgStrip(sig.Samples, sig.SampleRate, a.RPeaks, seconds, mmPx, "Live ECG"), a);
    }

    // ---------------------------------------------------------------- clinical

    public static NewsView News(double deterioration = 0.85, string lang = "en", int patient = 3)
    {
        var start = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.FromHours(7));
        var vitals = SyntheticData.VitalsSeries(12, deterioration, start, 60, 9);
        var rec = SyntheticData.Record(patient, deterioration) with { Vitals = vitals };
        var last = vitals[^1];
        var news = ClinicalScores.News2(last);
        var trend = Charts.Trend(
        [
            new Series(lang == "id" ? "Nadi" : "Pulse", vitals.Select(v => (v.Timestamp, v.HeartRate)).ToArray(), SvgTheme.Red),
            new Series(lang == "id" ? "Napas ×4" : "Resp ×4", vitals.Select(v => (v.Timestamp, v.RespiratoryRate * 4)).ToArray(), SvgTheme.Teal),
            new Series("SBP", vitals.Select(v => (v.Timestamp, v.SystolicBp)).ToArray(), SvgTheme.Ink),
        ], 40, 160, (51, 90), lang == "id" ? "per menit · mmHg" : "per min · mmHg", 660, 230);
        var gauge = Charts.Gauge(news.Score, 20, [(4, SvgTheme.Teal), (6, SvgTheme.Amber), (20, SvgTheme.Red)], "NEWS2");
        return new NewsView(Card(rec), news, ClinicalScores.QSofa(last), Charts.ObservationChart(vitals, lang), trend, gauge, deterioration);
    }

    public static async Task<CdsView> Cds(bool riskyMedications = true, double deterioration = 0.9)
    {
        var rec = SyntheticData.Record(3, deterioration);
        if (riskyMedications)
            rec = rec with
            {
                Medications =
                [
                    .. rec.Medications,
                    Med(rec, "11289", "warfarin", 5, 1), Med(rec, "5640", "ibuprofen", 400, 3), Med(rec, "723", "amoxicillin", 1000, 3), Med(rec, "6922", "metronidazole", 500, 3),
                ],
                Allergies = [new Allergy { PatientId = rec.Patient.Id, Substance = "penicillin", Reaction = "Urticaria", Severity = AllergySeverity.Moderate }],
            };
        var alerts = new ClinicalDecisionSupport().Evaluate(rec, Today);
        var triage = await new ConfigurableRules().EvaluateAsync("Triage", ClinicalSnapshot.From(rec, Today));
        return new CdsView(Card(rec), alerts, triage, rec.Medications.Select(m => $"{m.Generic} {m.DoseMg:0} mg × {m.FrequencyPerDay:0}/d").ToArray(),
            rec.Allergies.Select(a => $"{a.Substance} ({a.Reaction})").ToArray(), ClinicalDecisionSupport.RuleNames);
    }

    private static MedicationOrder Med(PatientRecord r, string rxcui, string generic, double dose, double freq) =>
        new() { PatientId = r.Patient.Id, Medication = ClinicalCode.RxNorm(rxcui, generic), Generic = generic, DoseMg = dose, FrequencyPerDay = freq, Start = Today };

    public static CalcView Calculators(double creatinine = 1.4, int age = 67, Gender sex = Gender.Female, double weight = 58, double height = 155, string drug = "amoxicillin")
    {
        double crcl = Clinical.Calculators.CockcroftGault(creatinine, age, weight, sex);
        return new CalcView(
            Clinical.Calculators.EgfrCkdEpi2021(creatinine, age, sex), crcl,
            Clinical.Calculators.Bmi(weight, height), Clinical.Calculators.Bmi(weight, height, BmiStandard.AsiaPacific),
            Clinical.Calculators.BsaMosteller(weight, height), DoseCalculator.Calculate(drug, weight, crcl),
            Clinical.Calculators.MaintenanceFluidMlPerHour(weight), Clinical.Calculators.MeanArterialPressure(118, 72));
    }

    // ---------------------------------------------------------------- imaging

    public static DicomView Dicom(int slice = 14, string window = "Brain", bool chest = false)
    {
        var series = chest ? ChestCt.Value : HeadCt.Value;
        slice = Math.Clamp(slice, 0, series.Count - 1);
        var ds = series[slice].Dataset;
        var preset = WindowPreset.Find(window) ?? (chest ? WindowPreset.Lung : WindowPreset.Brain);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var img = DicomImaging.Render(ds, preset);
        double ms = sw.Elapsed.TotalMilliseconds;
        var hist = DicomImaging.Histogram(ds, -1100, 1300, 96);
        var svg = Charts.Histogram(hist, -1100, 1300, [(preset.Center - preset.Width / 2, "L"), (preset.Center + preset.Width / 2, "W")], 420, 150);
        return new DicomView(img.ToPng(), DicomImaging.Describe(ds), DicomImaging.Statistics(ds), svg, preset, slice, series.Count, MedicalKernels.ActiveBackend, Math.Round(ms, 2));
    }

    public static HemorrhageView Hemorrhage(int slice = 14)
    {
        var series = HeadCt.Value;
        slice = Math.Clamp(slice, 0, series.Count - 1);
        return new HemorrhageView(HemorrhageScreener.Overlay(series[slice]), HemorrhageScreener.Analyze(series), slice, series.Count);
    }

    // ---------------------------------------------------------------- signals

    public static EcgView Ecg(Rhythm rhythm = Rhythm.AtrialFibrillation, double heartRate = 88)
    {
        var sim = rhythm is Rhythm.SinusTachycardia or Rhythm.SinusBradycardia ? Rhythm.NormalSinus : rhythm;
        var sig = EcgSimulator.Generate(30, heartRate, sim, seed: 5);
        var a = EcgAnalyzer.Analyze(sig);
        return new EcgView(Charts.EcgStrip(sig.Samples, sig.SampleRate, a.RPeaks, 10, 4.2), a, rhythm, heartRate);
    }

    // ---------------------------------------------------------------- interoperability

    public static FhirView Fhir(int patient = 4)
    {
        var rec = SyntheticData.Record(patient, 0.6);
        var m = new FhirMapper();
        var ips = m.ToPatientSummary(rec, new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(7)));
        var comp = (global::Hl7.Fhir.Model.Composition)ips.Entry[0].Resource!;
        var patientRes = m.ToFhir(rec.Patient);
        return new FhirView(FhirJson.Serialize(ips), ips.Entry.Count, FhirJson.Validate(ips).Issue.Count,
            comp.Section.Select(s => (s.Title ?? "", s.Entry.Count)).ToArray(),
            [
                $"Patient.name.given → {string.Join(", ", FhirJson.Select(patientRes, "Patient.name.given"))}",
                $"Patient.identifier.where(system='{CodeSystems.Nik}').value → {string.Join(", ", FhirJson.Select(patientRes, $"Patient.identifier.where(system='{CodeSystems.Nik}').value"))}",
                $"Bundle.entry.resource.ofType(Observation).count() → {FhirJson.Select(ips, "Bundle.entry.resource.ofType(Observation).count()")[0]}",
            ]);
    }

    public static Hl7View Hl7(int patient = 5)
    {
        var rec = SyntheticData.Record(patient, 0.7);
        var oru = Hl7V2.BuildResults(rec.Patient, rec.Observations.Take(6).ToArray(), new("LIS", "LAB-RSUD"), new("EMR", "RSUD"), "ORD-2026-0042", "MSG00042",
            new DateTimeOffset(2026, 11, 13, 8, 30, 0, TimeSpan.FromHours(7)));
        var er7 = Hl7V2.Encode(oru);
        var msg = Hl7V2.Parse(er7);
        var p = Hl7Mapping.ToPatient(msg);
        return new Hl7View(er7.Replace("\r", "\n").TrimEnd(), Hl7V2.BuildAck(msg, at: new DateTimeOffset(2026, 11, 13, 8, 30, 1, TimeSpan.FromHours(7))).Replace("\r", "\n").TrimEnd(),
            FhirJson.Serialize(Hl7Mapping.ToFhirTransaction(msg)), Hl7Mapping.ToObservations(msg, p.Id), $"{p.Name} · {p.Mrn} · NIK {Masking.Nik(p.Nik ?? "")}");
    }

    public static TermView Terminology(string query = "gagal jantung", string? system = null)
    {
        var t = TerminologyService.Default;
        var hits = t.Search(query, system, 12);
        var selected = hits.Where(h => h.Concept.System == CodeSystems.Snomed).Select(h => h.Concept).FirstOrDefault() ?? (hits.Count > 0 ? hits[0].Concept : null);
        var ancestors = selected is not null && selected.System == CodeSystems.Snomed ? t.Snomed.Ancestors(selected.Code) : [];
        var map = selected is not null && selected.System == CodeSystems.Snomed ? t.SnomedToIcd10(selected.Code) : null;
        return new TermView(hits, selected, ancestors, map, t.Validate(CodeSystems.Loinc, "2345-7"), t.Validate(CodeSystems.Snomed, "44054006"));
    }

    // ---------------------------------------------------------------- AI

    public static RiskView Risk(double deterioration = 0.7, double bmi = 29, int age = 52, bool family = true)
    {
        var det = DetModel.Value;
        var p = det.Predict(SyntheticData.Record(7, deterioration), Today);
        var dm = DmModel.Value;
        var dp = dm.Predict([age, bmi, bmi * 3.4, 105 + age * 0.45 + (bmi - 22) * 0.9, 0, 1, family ? 1 : 0, 0]);
        return new RiskView(p, det.Metrics!, Charts.DivergingBars(p.Drivers.Select(d => (d.Feature, d.Contribution)).ToArray(), 560),
            dp, Charts.DivergingBars(dp.Drivers.Select(d => (d.Feature, d.Contribution)).ToArray(), 560), dm.Metrics!);
    }

    public static readonly string[] SampleQuestions =
    [
        "Bagaimana kondisi pasien ini?",
        "Is it safe to add ibuprofen for this patient on warfarin?",
        "Hitung eGFR pasien ini",
        "Paracetamol dose for a 18 kg child",
        "Kode ICD-10 untuk demam berdarah",
    ];

    /// <summary>Assistant for the gallery: Claude when ANTHROPIC_API_KEY (or MEDNET_LLM_*) is set, otherwise offline.</summary>
    public static ClinicalAssistant Assistant(int patient = 7)
    {
        var rec = SyntheticData.Record(patient, 0.85) with
        {
            Medications = [.. SyntheticData.Record(patient, 0.85).Medications, new MedicationOrder { PatientId = $"pat-{patient:00000}", Medication = ClinicalCode.RxNorm("11289", "warfarin"), Generic = "warfarin", DoseMg = 5, Start = Today }],
        };
        return new ClinicalAssistant(LlmOptions.FromEnvironment().Create()) { Patient = rec };
    }

    // ---------------------------------------------------------------- data

    public const string SampleNote =
        "Tn. Budi Santoso, 67 tahun, NIK 3201234505590001, No. RM: MRN100245, alamat Jl. Merdeka No. 12 RT 03/RW 05 Bandung, " +
        "HP 0812-3456-7890, email budi.s@example.com. Masuk IGD 12 November 2026 pukul 21.40 dengan sesak dan demam 3 hari. " +
        "Riwayat DM tipe 2 dan hipertensi. Budi tinggal bersama ayahnya yang berusia 92 tahun. Kontrol poli 2026-11-20.";

    public static PrivacyView Privacy(string? text = null, bool pseudonyms = false)
    {
        var deid = new TextDeidentifier("gallery-demo-secret-key-2026");
        var src = text ?? SampleNote;
        var r = deid.Deidentify(src, pseudonyms ? ReplacementStyle.Pseudonyms : ReplacementStyle.Tags, "pat-00001");
        return new PrivacyView(src, r.Text, r.Findings, r.Counts,
        [
            ("NIK", Masking.Nik("3201234505590001")), ("Phone", Masking.Phone("+62 812 3456 7890")),
            ("Email", Masking.Email("budi.s@example.com")), ("Name", Masking.Name(new HumanName("Budi", "Santoso"))),
        ]);
    }

    public static async Task<EmrView> Emr(string search = "S")
    {
        var services = new ServiceCollection();
        services.AddMedical(m => m.AddEmr(o => { o.Provider = EmrProvider.Sqlite; o.ConnectionString = "Data Source=gallery-emr;Mode=Memory;Cache=Shared"; }));
        await using var sp = services.BuildServiceProvider();
        // A shared in-memory SQLite database lives as long as one connection stays open.
        await using var keepAlive = await sp.GetRequiredService<IDbContextFactory<MedicalDbContext>>().CreateDbContextAsync();
        await keepAlive.Database.OpenConnectionAsync();
        await keepAlive.Database.EnsureCreatedAsync();
        using var scope = sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<PatientRepository>();
        repo.User = "dr.ratna";
        await repo.SeedSyntheticAsync(40);
        var found = await repo.SearchAsync(search, 8);
        foreach (var p in found.Take(3)) await repo.GetRecordAsync(p.Id, "treatment");
        var audit = sp.GetRequiredService<IAuditLog>();
        return new EmrView(await repo.CountAsync(), found, audit.Query().TakeLast(8).ToArray(), "SQLite (in-memory) + distributed cache");
    }

    public static string Fmt(double v, string format = "0.#") => v.ToString(format, Inv);

    public static Localized RiskName(RiskLevel r) => r switch
    {
        RiskLevel.LowMedium => new("Low–medium", "Rendah–sedang"),
        RiskLevel.Medium => new("Medium", "Sedang"),
        RiskLevel.High => new("High", "Tinggi"),
        RiskLevel.Critical => new("Critical", "Kritis"),
        _ => new("Low", "Rendah"),
    };

    public static Localized RhythmName(Rhythm r) => r switch
    {
        Rhythm.AtrialFibrillation => new("Atrial fibrillation", "Fibrilasi atrium"),
        Rhythm.Ectopy => new("Ectopic beats", "Denyut ektopik"),
        Rhythm.SinusTachycardia => new("Sinus tachycardia", "Takikardia sinus"),
        Rhythm.SinusBradycardia => new("Sinus bradycardia", "Bradikardia sinus"),
        _ => new("Normal sinus rhythm", "Irama sinus normal"),
    };
}
