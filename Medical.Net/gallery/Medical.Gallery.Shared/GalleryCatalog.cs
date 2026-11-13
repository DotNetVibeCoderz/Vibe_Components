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
        new("national", new("Indonesia: SATUSEHAT & BPJS", "Indonesia: SATUSEHAT & BPJS")),
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
        new("mpr", "imaging", new("MPR & MIP", "MPR & MIP"),
            new("Axial, coronal and sagittal reformats with correct proportions, plus maximum / minimum / average intensity slabs — one Rust kernel, bit-identical managed fallback.",
                "Rekonstruksi aksial, koronal dan sagital dengan proporsi benar, ditambah slab intensitas maksimum / minimum / rata-rata — satu kernel Rust, fallback managed identik."),
            "Medical.Dicom · Medical.Native", """
            DicomVolume v = DicomVolume.FromSeries(files);          // sorted by Image Position (Patient); JPEG/J2K decoded
            VolumeImage coronal = VolumeRendering.Mpr(v, VolumePlane.Coronal, position: 0.5);
            VolumeImage mip = VolumeRendering.Project(v, VolumePlane.Axial, SlabMode.Max, 0.5, thicknessMm: 30);
            VolumeImage minip = VolumeRendering.Project(v, VolumePlane.Coronal, SlabMode.Min);   // airways
            VolumeImage oblique = VolumeRendering.Oblique(v, u: new(1, 0.3, 0), v: new(0, 0, -1));
            byte[] png = mip.ToPng(WindowPreset.Bone);               // mip.Backend == Rust
            """),
        new("volume3d", "imaging", new("3D volume", "Volume 3D"),
            new("Surface-nets isosurfaces of the skull and the segmented haemorrhage, rendered with ThreeNet (desktop) and three.js (web); export to STL for 3D printing.",
                "Isosurface surface-nets tengkorak dan perdarahan tersegmentasi, dirender dengan ThreeNet (desktop) dan three.js (web); ekspor STL untuk cetak 3D."),
            "Medical.Dicom · ThreeNet", """
            SurfaceMesh skull = IsoSurface.Extract(volume, isoValue: 300, downsample: 2);       // bone
            SurfaceMesh bleed = IsoSurface.ExtractMask(volume, HemorrhageScreener.SegmentVolume(volume));
            double ml = bleed.VolumeMl;                          // divergence theorem on the closed mesh
            File.WriteAllBytes("skull.stl", skull.ToStl());
            // ThreeNet: scene.CreateGeometry(vertices, skull.Indices) → ThreeNetView + OrbitController
            """),
        new("codecs", "imaging", new("Compression", "Kompresi"),
            new("JPEG-LS, JPEG 2000, HT-JPEG 2000, JPEG Lossless and RLE through the fo-dicom native codecs — lossless syntaxes decode bit-exactly, lossy ones within bounds.",
                "JPEG-LS, JPEG 2000, HT-JPEG 2000, JPEG Lossless dan RLE melalui codec native fo-dicom — sintaks lossless kembali identik, lossy dalam batas."),
            "Medical.Dicom · fo-dicom.Codecs", """
            DicomFile jls = DicomCodecs.Transcode(file, DicomCompression.JpegLsLossless);
            DicomFile j2k = DicomCodecs.Transcode(file, DicomCompression.Jpeg2000, quality: 20);   // 20:1 target
            short[] pixels = DicomImaging.FrameI16(jls.Dataset);    // compressed frames decode on demand
            RenderedImage img = DicomImaging.Render(j2k.Dataset, WindowPreset.Brain);
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
        new("ecg12", "signals", new("12-lead ECG", "EKG 12 sadapan"),
            new("Median beats, PR / QRS / QT with four QTc formulas, axis, ST at J and the UDMI STEMI criteria by territory; DICOM waveform and SCP-ECG in and out.",
                "Denyut median, PR / QRS / QT dengan empat rumus QTc, aksis, ST pada titik J dan kriteria STEMI UDMI per wilayah; masuk-keluar waveform DICOM dan SCP-ECG."),
            "Medical.Signals · Medical.Dicom", """
            var (ecg, _) = Ecg12Simulator.Generate(new Ecg12Options { StPattern = StPattern.InferiorStemi });
            Ecg12Analysis a = Ecg12Analyzer.Analyze(ecg, new EcgPatient(Gender.Male, 58));
            // a.QtMs, a.QtcFridericiaMs, a.QrsAxisDeg, a.StemiTerritories = ["Inferior"], a.StDepressionLeads = [I, aVL]
            DicomFile dcm = DicomEcg.Create(ecg, patient, a);                // 12-lead ECG Waveform Storage
            byte[] scp = ScpEcg.Write(ecg, patient.Mrn);                      // SCP-ECG, default Huffman
            string svg = Charts.Ecg12Grid(ecg, highlight: a.Leads.Where(l => l.StJMv >= 0.1).Select(l => l.Lead));
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
        new("fhir-server", "interop", new("FHIR server", "Server FHIR"),
            new("A versioned FHIR R4 repository: chained search with _include / _revinclude, _history and vread, If-Match, profile validation against the R4 spec and SMART on FHIR scopes.",
                "Repositori FHIR R4 berversi: pencarian dengan _include / _revinclude, _history dan vread, If-Match, validasi profil terhadap spesifikasi R4, dan scope SMART on FHIR."),
            "Medical.Fhir · Medical.AspNetCore", """
            services.AddMedical(m => m.AddMedicalApi()
                .AddEmr(o => o.PersistFhir = true)           // EF Core: versions + search index
                .AddSmartOnFhir(o => o.Clients.Add(new("ward-app", ["https://app/cb"], "launch/patient patient/*.rs"))));
            app.MapMedicalFhir();                            // /fhir/{type}?…, /_history, $validate, transaction
            app.MapSmartAuthorization();                     // .well-known/smart-configuration, /auth/*

            Bundle labs = repo.Search("Observation", [new("patient", "pat-00003"), new("_include", "Observation:patient")]);
            OperationOutcome oo = new FhirProfileValidator().Validate(obs, "http://hl7.org/fhir/StructureDefinition/heartrate");
            """),
        new("hl7-orders", "interop", new("HL7 orders & scheduling", "Order & jadwal HL7"),
            new("ORM^O01 / OML^O21 orders, SIU^S12 appointments and MDM^T02 documents, checked by segment-level conformance profiles and mapped to FHIR.",
                "Order ORM^O01 / OML^O21, janji temu SIU^S12 dan dokumen MDM^T02, diperiksa profil kesesuaian tingkat segmen dan dipetakan ke FHIR."),
            "Medical.Hl7", """
            ORM_O01 orm = Hl7V2.BuildOrder(patient, orders, new("EMR", "RSUD"), new("LIS", "LAB"));
            SIU_S12 siu = Hl7V2.BuildAppointment(patient, appointment, new("SIMRS", "RSUD"), new("EMR", "RSUD"));
            MDM_T02 mdm = Hl7V2.BuildDocument(patient, cppt, new("EMR", "RSUD"), new("DMS", "RSUD"), encounter);

            ConformanceResult r = new Hl7ConformanceValidator().Validate(Hl7V2.Parse(Hl7V2.Encode(orm)));
            // r.IsValid, r.Issues: "Error PID-3: Required field missing", "Error ORC-1: 'ZZ' not in table"
            Bundle tx = Hl7Mapping.ToFhirTransaction(msg);   // → ServiceRequest / Appointment / DocumentReference
            """),
        new("satusehat", "national", new("SATUSEHAT visit", "Kunjungan SATUSEHAT"),
            new("One outpatient visit as a SATUSEHAT transaction: Encounter → Condition → Observation → Medication (KFA) → MedicationRequest, linked by urn:uuid, posted with token refresh and retry/backoff.",
                "Satu kunjungan rawat jalan sebagai transaksi SATUSEHAT: Encounter → Condition → Observation → Medication (KFA) → MedicationRequest, ditautkan urn:uuid, dikirim dengan pembaruan token dan retry/backoff."),
            "Medical.Fhir", """
            var kfa = new KfaCatalog();
            await new KfaClient(http, satuSehat).ResolveIntoAsync(kfa, record.Medications.Select(m => m.Generic));

            SatuSehatSubmission s = SatuSehatWorkflow.BuildOutpatientVisit(record, context, kfa);
            // s.Warnings: medications without a KFA code are skipped, never sent uncoded
            Bundle result = await satuSehat.PostTransactionAsync(s.Bundle);   // 429/5xx → RetryHandler
            """),
        new("bpjs", "national", new("BPJS claim (INA-CBG)", "Klaim BPJS (INA-CBG)"),
            new("Build E-Klaim claim data from the record, check INA-CBG coding rules before grouping, and sign VClaim / PCare requests with HMAC-SHA256 + LZ-String responses.",
                "Susun data klaim E-Klaim dari rekam medis, periksa aturan koding INA-CBG sebelum grouping, dan tandatangani permintaan VClaim / PCare dengan HMAC-SHA256 + respons LZ-String."),
            "Medical.Bpjs", """
            InaCbgClaim claim = InaCbgClaimBuilder.FromRecord(record, nomorSep, nomorKartu, tariff, coderNik, ["87.44"]);
            IReadOnlyList<ClaimIssue> issues = InaCbgClaimBuilder.Validate(claim);   // SEP, ICD-10 rules, LOS, tariff

            var eklaim = new EKlaimClient(http, "http://eklaim/E-Klaim/ws.php", hexKey);
            await eklaim.NewClaimAsync(claim); await eklaim.SetClaimDataAsync(claim);
            JsonNode cbg = await eklaim.GrouperAsync(claim.NomorSep);                 // → CBG code + tariff

            var vclaim = new VClaimClient(http, BpjsCredentials.FromEnvironment());
            JsonNode peserta = await vclaim.PesertaByKartuAsync("0001234567890", today);
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
        new("imaging-ai", "ai", new("Imaging AI → SEG / SR", "AI pencitraan → SEG / SR"),
            new("Haemorrhage and lung-nodule results stored as DICOM Segmentation + TID 1500 Measurement Report (validated with highdicom); the MONAI lung_nodule_ct_detection bundle plugs into the imaging service.",
                "Hasil perdarahan dan nodul paru disimpan sebagai DICOM Segmentation + Measurement Report TID 1500 (divalidasi dengan highdicom); bundle MONAI lung_nodule_ct_detection terpasang di layanan pencitraan."),
            "Medical.AI · Medical.Dicom", """
            LungNoduleReport r = LungNoduleDetector.Detect(volume);          // Lung-RADS v2022 category per nodule
            ImagingAiOutput o = ImagingAiReports.LungNodules(volume, r)!;
            await o.Segmentation!.SaveAsync("seg.dcm");                     // Segmentation IOD, BINARY
            await o.Report.SaveAsync("sr.dcm");                              // TID 1500 → TID 1411 groups
            SegmentationResult back = DicomSegmentation.Read(DicomFile.Open("seg.dcm"), volume);
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
