# Modul

## Medical.Fhir — FHIR R4 (Firely SDK)

```csharp
var m = new FhirMapper(new FhirMappingOptions { MrnSystem = "http://rs-contoh.id/mrn", Organization = "RSUD Contoh" });
Hl7.Fhir.Model.Patient fp = m.ToFhir(record.Patient);         // identifier RM + NIK, nama, telepon, alamat
var vital = m.ToFhir(record.LatestVitals!, record.Patient.Id);  // profil vital-signs; TD sebagai panel dengan komponen
Bundle ips = m.ToPatientSummary(record);                       // Composition + masalah, obat, alergi, hasil lab, tanda vital
Bundle tx  = m.ToTransaction(record);
OperationOutcome oo = FhirJson.Validate(ips);
IReadOnlyList<string> nama = FhirJson.Select(fp, "Patient.name.given");

var store = new InMemoryFhirStore();                           // CRUD berversi, pencarian, transaksi dengan rollback
store.Process(tx);
```

**SATUSEHAT** (Kemenkes RI): `SatuSehatOptions.FromEnvironment()` membaca `SATUSEHAT_CLIENT_ID`, `SATUSEHAT_CLIENT_SECRET`, `SATUSEHAT_ORG_ID`, `SATUSEHAT_ENV`. `SatuSehatClient` menyimpan token OAuth2, mencari pasien berdasarkan NIK (`SearchPatientByNikAsync`), membuat/membaca resource, dan menyiapkan Encounter dengan identifier `sys-ids.kemkes.go.id` yang diwajibkan.

## Medical.Hl7 — HL7 v2 (NHapi) dan MLLP

```csharp
Hl7Message msg = Hl7V2.Parse(er7);
Patient p = Hl7Mapping.ToPatient(msg);                 // PID (identifier MR + NI)
var lab = Hl7Mapping.ToObservations(msg, p.Id);       // OBX → observasi LOINC dengan rentang/flag
Bundle tx = Hl7Mapping.ToFhirTransaction(msg);
string ack = Hl7V2.BuildAck(msg);                     // AA / AE / AR

await using var server = new MllpServer(new IPEndPoint(IPAddress.Any, 2575), MllpServer.AcceptAll);
server.Start();
```

## Medical.Dicom — pencitraan (fo-dicom)

```csharp
DicomFile f = DicomImaging.Open("IM0001.dcm");
RenderedImage img = DicomImaging.Render(f.Dataset, WindowPreset.Brain);   // kernel Rust
PixelStats hu = DicomImaging.Statistics(f.Dataset, roi: (100, 100, 40, 40));
DicomFile anon = new DicomDeidentifier(rahasia).Deidentify(f, out var laporan);
var series = DicomPhantom.CreateSeries(PhantomKind.Head, slices: 24);   // CT sintetis dengan perdarahan
var (ok, rtt) = await DicomScu.EchoAsync("pacs", 104, calledAe: "ORTHANC");
using var scp = StoreScp.StartServer(11112, inbox);
```

Transfer syntax terkompresi harus di-transcode dengan codec fo-dicom sebelum dirender.

## Medical.Terminology

```csharp
var t = TerminologyService.Default;
t.Search("gagal jantung");                          // SNOMED 84114007, ICD-10 I50.0, I50.9 …
t.Subsumes("73211009", "44054006");                // true: diabetes ⊒ DM tipe 2
t.SnomedToIcd10("44054006");                       // E11.9
t.Validate(CodeSystems.Loinc, "2345-7");           // digit pemeriksa
t.Drug("amoksisilin");                             // RxNorm 723 beserta tag golongan
CodeValidators.TryParseNik(nik, out var lahir, out var perempuan);
```

Data bawaan adalah subset terkurasi (ICD-10 WHO 2010 sesuai INA-CBG dengan tampilan bahasa Indonesia; sampel kecil SNOMED; LOINC dan bahan aktif RxNorm yang sering dipakai). Muat rilis lengkap sesuai lisensi Anda dengan `ReleaseLoaders.LoadSnomedRf2`, `LoadLoincCsv`, `LoadIcd10CmOrderFile`, `LoadRxNormRrf`, lalu `service.Register(codeSystem)`.

## Medical.Clinical

`ClinicalScores`: NEWS2 (skala 1 dan 2), qSOFA, SIRS, CURB-65, CHA₂DS₂-VASc, HAS-BLED, GCS, APGAR, MELD. `Calculators`: eGFR CKD-EPI 2021, Cockcroft-Gault, IMT (WHO / Asia-Pasifik), LPT, BB ideal, anion gap, kalsium terkoreksi, cairan Holliday-Segar, MAP, indeks syok, HPL Naegele. `DoseCalculator`: dosis per berat badan dengan batas maksimum dan penyesuaian ginjal. `LabInterpreter`: rentang rujukan, batas per jenis kelamin, nilai kritis, konversi satuan.

## Medical.Rules

```csharp
var peringatan = new ClinicalDecisionSupport(audit: auditLog).Evaluate(record);
var triase = await new ConfigurableRules().EvaluateAsync("Triage", ClinicalSnapshot.From(record));
var dbd = await new ConfigurableRules().EvaluateAsync("DengueWarningSigns", ClinicalSnapshot.From(record));
```

Basis pengetahuan obat (`DrugKnowledge`) bersifat ilustratif — gunakan basis data interaksi berlisensi untuk produksi.

## Medical.Signals, Privacy, Emr, Visualization

```csharp
EcgAnalysis a = EcgAnalyzer.Analyze(EcgSimulator.Generate(30, 88, Rhythm.AtrialFibrillation));
var deid = new TextDeidentifier(rahasia).Deidentify(catatan, ReplacementStyle.Pseudonyms, "pat-1");
Masking.Nik("3201234567890001");                               // 3201********0001
var r = await repo.GetRecordAsync("pat-00007", purpose: "treatment");   // EF Core + cache, tercatat di audit
string svg = Charts.ObservationChart(record.Vitals, lang: "id");
```
