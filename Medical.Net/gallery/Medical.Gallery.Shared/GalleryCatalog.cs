using Medical.Core;

namespace Medical.Gallery.Shared;

public sealed record DemoGroup(string Key, Localized Name);

/// <summary>One gallery use case: navigation metadata, the package it shows and a copy-paste code sample.</summary>
public sealed record Demo(string Slug, string Group, Localized Title, Localized Summary, string Module, string Code);

public static class GalleryCatalog
{
    public static Localized Credit { get; } = new(MedicalInfo.Credit, MedicalInfo.CreditId);
    public static Localized Disclaimer { get; } = new(MedicalInfo.Disclaimer, MedicalInfo.DisclaimerId);

    public static IReadOnlyList<DemoGroup> Groups { get; } =
    [
        new("clinical", new("Bedside", "Di samping pasien")),
        new("imaging", new("Imaging", "Pencitraan")),
        new("signals", new("Biosignals", "Biosinyal")),
        new("interop", new("Interoperability", "Interoperabilitas")),
        new("ai", new("Clinical AI", "AI klinis")),
        new("data", new("Data & privacy", "Data & privasi")),
    ];

    public static IReadOnlyList<Demo> Demos { get; } =
    [
        new("news2", "clinical", new("Early warning", "Peringatan dini"),
            new("A deteriorating patient on a NEWS2 observation chart: every cell is shaded by the points it scores.",
                "Pasien yang memburuk pada lembar observasi NEWS2: setiap sel diberi warna sesuai poinnya."),
            "Medical.Clinical", """
            var v = new VitalSigns { RespiratoryRate = 24, SpO2 = 93, OnSupplementalOxygen = true,
                                     SystolicBp = 105, HeartRate = 115, TemperatureC = 38.5 };
            ScoreResult news2 = ClinicalScores.News2(v);      // 10 — High
            ScoreResult qsofa = ClinicalScores.QSofa(v);
            string svg = Charts.ObservationChart(record.Vitals, lang: "id");
            """),
        new("cds", "clinical", new("Decision support", "Dukungan keputusan"),
            new("NRules evaluates vitals, labs, medications and allergies; JSON workflows on Microsoft RulesEngine decide triage.",
                "NRules mengevaluasi tanda vital, lab, obat dan alergi; alur JSON di Microsoft RulesEngine menentukan triase."),
            "Medical.Rules", """
            var alerts = new ClinicalDecisionSupport().Evaluate(record);
            // NEWS2.High, Sepsis.Screen, Drug.Interaction, Drug.Allergy, Renal.Metformin…

            var rules = new ConfigurableRules();               // editable JSON workflows
            var triage = await rules.EvaluateAsync("Triage", ClinicalSnapshot.From(record));
            """),
        new("calculators", "clinical", new("Calculators", "Kalkulator"),
            new("CKD-EPI 2021 eGFR, Cockcroft-Gault, BMI with Asia-Pacific cut-offs and weight-based dosing with renal adjustment.",
                "eGFR CKD-EPI 2021, Cockcroft-Gault, IMT dengan batas Asia-Pasifik, dan dosis berbasis berat badan dengan penyesuaian ginjal."),
            "Medical.Clinical", """
            EgfrResult egfr = Calculators.EgfrCkdEpi2021(creatinineMgDl: 1.4, age: 67, Gender.Female);
            double crcl = Calculators.CockcroftGault(1.4, 67, weightKg: 58, Gender.Female);
            BmiResult bmi = Calculators.Bmi(58, 155, BmiStandard.AsiaPacific);
            DoseRecommendation dose = DoseCalculator.Calculate("amoxicillin", 58, crcl);
            """),
        new("dicom", "imaging", new("DICOM viewer", "Penampil DICOM"),
            new("fo-dicom reads a CT series; window/level runs on the Rust kernel; Hounsfield statistics and histogram per slice.",
                "fo-dicom membaca seri CT; window/level berjalan di kernel Rust; statistik dan histogram Hounsfield per irisan."),
            "Medical.Dicom", """
            var series = DicomPhantom.CreateSeries(PhantomKind.Head, slices: 24);
            RenderedImage img = DicomImaging.Render(series[12].Dataset, WindowPreset.Brain);
            byte[] png = img.ToPng();
            PixelStats hu = DicomImaging.Statistics(series[12].Dataset);
            DicomFile anon = new DicomDeidentifier(secret).Deidentify(series[12], out _);
            """),
        new("hemorrhage", "imaging", new("Haemorrhage screen", "Skrining perdarahan"),
            new("Explainable triage of non-contrast head CT: hyperdense voxels are thresholded, de-noised and measured in millilitres.",
                "Triase CT kepala tanpa kontras yang dapat dijelaskan: voksel hiperdens diambang, dibersihkan, dan diukur dalam mililiter."),
            "Medical.AI", """
            HemorrhageReport r = HemorrhageScreener.Analyze(series);
            // r.VolumeMl, r.Findings (slice, area, mean HU), r.Impression["id"]
            byte[] overlay = HemorrhageScreener.Overlay(series[14]);

            // or a MONAI / PyTorch model behind services/medical-ai-service
            var report = await new ImagingAiClient(http, "http://localhost:8000").HemorrhageAsync(series);
            """),
        new("ecg", "signals", new("ECG & rhythm", "EKG & irama"),
            new("Pan-Tompkins QRS detection in Rust, heart-rate variability and rhythm screening for atrial fibrillation and ectopy.",
                "Deteksi QRS Pan-Tompkins di Rust, variabilitas denyut jantung, dan skrining fibrilasi atrium serta ektopi."),
            "Medical.Signals", """
            Signal ecg = EcgSimulator.Generate(seconds: 30, heartRate: 88, Rhythm.AtrialFibrillation);
            EcgAnalysis a = EcgAnalyzer.Analyze(ecg);
            // a.RPeaks, a.HeartRateBpm, a.SdnnMs, a.RmssdMs, a.Rhythm, a.Backend == Rust
            string strip = Charts.EcgStrip(ecg.Samples, ecg.SampleRate, a.RPeaks);
            """),
        new("fhir", "interop", new("FHIR patient summary", "Ringkasan pasien FHIR"),
            new("Firely SDK builds an International Patient Summary document bundle that validates and round-trips as FHIR R4 JSON.",
                "Firely SDK menyusun dokumen International Patient Summary yang valid dan bolak-balik sebagai JSON FHIR R4."),
            "Medical.Fhir", """
            var mapper = new FhirMapper();
            Bundle ips = mapper.ToPatientSummary(record);
            OperationOutcome issues = FhirJson.Validate(ips);
            string json = FhirJson.Serialize(ips);

            var store = new InMemoryFhirStore();                 // or app.MapMedicalFhir()
            store.Process(mapper.ToTransaction(record));
            """),
        new("hl7", "interop", new("HL7 v2 feed", "Umpan HL7 v2"),
            new("A lab ORU^R01 is built and parsed with NHapi, acknowledged, and mapped to FHIR Observations ready for SATUSEHAT.",
                "ORU^R01 laboratorium dibuat dan diurai dengan NHapi, diberi ACK, dan dipetakan ke Observation FHIR untuk SATUSEHAT."),
            "Medical.Hl7", """
            ORU_R01 oru = Hl7V2.BuildResults(patient, labs, new("LIS", "LAB"), new("EMR", "RS"));
            Hl7Message msg = Hl7V2.Parse(Hl7V2.Encode(oru));
            string ack = Hl7V2.BuildAck(msg);                  // MSA|AA|<control id>
            Bundle tx = Hl7Mapping.ToFhirTransaction(msg);

            await using var server = new MllpServer(new(IPAddress.Any, 2575), MllpServer.AcceptAll);
            """),
        new("terminology", "interop", new("Terminology", "Terminologi"),
            new("Search ICD-10, SNOMED CT, LOINC and RxNorm in English or Bahasa Indonesia, walk the SNOMED hierarchy and map to ICD-10.",
                "Cari ICD-10, SNOMED CT, LOINC dan RxNorm dalam bahasa Inggris atau Indonesia, telusuri hierarki SNOMED, petakan ke ICD-10."),
            "Medical.Terminology", """
            var t = TerminologyService.Default;
            var hits = t.Search("gagal jantung");                 // SNOMED 84114007, ICD-10 I50.x
            bool isA = t.Subsumes("73211009", "44054006");       // diabetes ⊒ type 2 diabetes
            ConceptMapping? map = t.SnomedToIcd10("44054006");   // → E11.9
            bool ok = CodeValidators.IsValidLoinc("2345-7");     // check digit
            """),
        new("assistant", "ai", new("Clinical assistant", "Asisten klinis"),
            new("Ask about the patient in English or Indonesian. Claude (or any OpenAI-compatible model, or offline rules) answers through validated tools.",
                "Tanyakan tentang pasien dalam bahasa Inggris atau Indonesia. Claude (atau model lain, atau aturan offline) menjawab melalui alat tervalidasi."),
            "Medical.AI", """
            IClinicalLlm llm = LlmOptions.FromEnvironment().Create();   // ANTHROPIC_API_KEY → Claude
            var assistant = new ClinicalAssistant(llm) { Patient = record };
            AssistantReply reply = await assistant.AskAsync("Bagaimana kondisi pasien ini?");
            // reply.ToolCalls: get_patient_summary, calculate_egfr, check_drug_safety…
            """),
        new("risk", "ai", new("Risk models", "Model risiko"),
            new("ML.NET logistic models for 48-hour deterioration and undiagnosed type 2 diabetes, with the drivers behind every prediction.",
                "Model logistik ML.NET untuk perburukan 48 jam dan DM tipe 2 yang belum terdiagnosis, dengan faktor penentu tiap prediksi."),
            "Medical.AI", """
            var model = new DeteriorationRiskModel();
            RiskModelMetrics m = model.Train();                 // AUC on a held-out split
            RiskPrediction p = model.Predict(record);
            foreach (var d in p.Drivers) Console.WriteLine($"{d.Feature}: {d.Contribution:+0.00}");
            """),
        new("privacy", "data", new("De-identification", "De-identifikasi"),
            new("Remove names, NIK, MRN, phone numbers, addresses and dates from Indonesian and English notes — redact or pseudonymise.",
                "Hapus nama, NIK, nomor RM, telepon, alamat dan tanggal dari catatan berbahasa Indonesia dan Inggris — sensor atau pseudonim."),
            "Medical.Privacy", """
            var deid = new TextDeidentifier(secret);
            DeidentifiedText r = deid.Deidentify(note, ReplacementStyle.Pseudonyms, subjectId);
            string nik = Masking.Nik("3201234567890001");        // 3201********0001
            bool ok = new ConsentRegistry(audit).IsPermitted(id, ConsentPurpose.Research);
            """),
        new("emr", "data", new("EMR store", "Penyimpanan EMR"),
            new("Entity Framework Core on SQLite or SQL Server with a Redis cache; every read is written to the audit trail.",
                "Entity Framework Core di SQLite atau SQL Server dengan cache Redis; setiap pembacaan dicatat di jejak audit."),
            "Medical.Emr", """
            services.AddMedical(m => m.AddEmr(o => {
                o.Provider = EmrProvider.SqlServer;              // or Sqlite / InMemory
                o.ConnectionString = config["ConnectionStrings:Emr"]!;
                o.Redis = "localhost:6379";                      // distributed cache
            }));
            var record = await repository.GetRecordAsync("pat-00007", purpose: "treatment");
            """),
    ];

    public static Demo Get(string slug) => Demos.FirstOrDefault(d => d.Slug == slug) ?? Demos[0];
}
