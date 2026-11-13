# Modules

## Medical.Fhir — FHIR R4 (Firely SDK)

```csharp
var m = new FhirMapper(new FhirMappingOptions { MrnSystem = "http://rs-contoh.id/mrn", Organization = "RSUD Contoh" });
Hl7.Fhir.Model.Patient fp = m.ToFhir(record.Patient);         // MRN + NIK identifiers, name, telecom, address
var vitals = m.ToFhir(record.LatestVitals!, record.Patient.Id); // vital-signs profiles; BP as panel with components
Bundle ips = m.ToPatientSummary(record);                       // Composition + problems, medications, allergies, results, vitals
Bundle tx  = m.ToTransaction(record);
OperationOutcome oo = FhirJson.Validate(ips);
IReadOnlyList<string> given = FhirJson.Select(fp, "Patient.name.given");

var store = new InMemoryFhirStore();                           // versioned CRUD, search, transactions with rollback
store.Process(tx);
Bundle labs = store.Search("Observation", [new("patient", record.Patient.Id), new("category", "laboratory")]);
```

**SATUSEHAT** (Kemenkes RI): `SatuSehatOptions.FromEnvironment()` reads `SATUSEHAT_CLIENT_ID`, `SATUSEHAT_CLIENT_SECRET`, `SATUSEHAT_ORG_ID`, `SATUSEHAT_ENV`. `SatuSehatClient` caches the OAuth2 token, finds patients by NIK (`SearchPatientByNikAsync`), creates/reads resources and prepares encounters with the required `sys-ids.kemkes.go.id` identifiers.

## Medical.Hl7 — HL7 v2 (NHapi) and MLLP

```csharp
Hl7Message msg = Hl7V2.Parse(er7);                     // any version NHapi supports (2.3, 2.5.1)
Patient p = Hl7Mapping.ToPatient(msg);                 // PID (MR + NI identifiers)
var labs = Hl7Mapping.ToObservations(msg, p.Id);      // OBX → LOINC observations with ranges/flags
Bundle tx = Hl7Mapping.ToFhirTransaction(msg);
string ack = Hl7V2.BuildAck(msg);                     // AA / AE / AR

var oru = Hl7V2.BuildResults(p, labs, new("LIS", "LAB"), new("EMR", "RS"));
await using var server = new MllpServer(new IPEndPoint(IPAddress.Any, 2575), MllpServer.AcceptAll);
server.Start();
await using var client = new MllpClient("localhost", 2575);
string reply = await client.SendAsync(Hl7V2.Encode(oru));
```

## Medical.Dicom — imaging (fo-dicom)

```csharp
DicomFile f = DicomImaging.Open("IM0001.dcm");
DicomImageInfo info = DicomImaging.Describe(f.Dataset);
RenderedImage img = DicomImaging.Render(f.Dataset, WindowPreset.Brain);   // Rust kernel
File.WriteAllBytes("slice.png", img.ToPng());
PixelStats hu = DicomImaging.Statistics(f.Dataset, roi: (100, 100, 40, 40));
DicomFile anon = new DicomDeidentifier(secret).Deidentify(f, out var report);   // PS3.15 + stable pseudonym

var series = DicomPhantom.CreateSeries(PhantomKind.Head, slices: 24);   // synthetic CT with a haemorrhage
var (ok, rtt) = await DicomScu.EchoAsync("pacs", 104, calledAe: "ORTHANC");
using var scp = StoreScp.StartServer(11112, inbox);                       // C-STORE / C-ECHO receiver
var studies = await new DicomWebClient(http, "https://pacs/dicom-web").SearchStudiesAsync(new Dictionary<string, string> { ["PatientID"] = "MRN1" });
```

Compressed transfer syntaxes must be transcoded with fo-dicom codecs before rendering.

## Medical.Terminology

```csharp
var t = TerminologyService.Default;                 // embedded curated subsets
t.Search("gagal jantung");                          // SNOMED 84114007, ICD-10 I50.0, I50.9 …
t.Subsumes("73211009", "44054006");                // true: diabetes ⊒ type 2 diabetes
t.Expand("73211009");                              // value set << 73211009
t.SnomedToIcd10("44054006");                       // E11.9
t.Validate(CodeSystems.Loinc, "2345-7");           // check digit
t.Drug("parasetamol");                             // RxNorm 161 (acetaminophen) with class tags
CodeValidators.TryParseNik(nik, out var dob, out var female);
```

The embedded data is a curated subset (ICD-10 WHO 2010 as used by INA-CBG with Indonesian displays; a small SNOMED sample; frequent LOINC and RxNorm ingredients). Load full releases you are licensed for: `ReleaseLoaders.LoadSnomedRf2(desc, rel)`, `LoadLoincCsv`, `LoadIcd10CmOrderFile`, `LoadRxNormRrf`, then `service.Register(codeSystem)`.

## Medical.Clinical

`ClinicalScores`: NEWS2 (scale 1 and 2), qSOFA, SIRS, CURB-65, CHA₂DS₂-VASc, HAS-BLED, GCS, APGAR, MELD. `Calculators`: CKD-EPI 2021 eGFR, Cockcroft-Gault, BMI (WHO / Asia-Pacific), BSA, IBW, anion gap, corrected calcium, Holliday-Segar fluids, MAP, shock index, Naegele due date. `DoseCalculator`: weight-based dosing with caps and renal adjustment. `LabInterpreter`: reference ranges, sex-specific limits, critical values, unit conversion.

## Medical.Rules

```csharp
var alerts = new ClinicalDecisionSupport(audit: auditLog, bus: eventBus).Evaluate(record);
var rules = new ConfigurableRules();                                 // embedded JSON workflows
var triage = await rules.EvaluateAsync("Triage", ClinicalSnapshot.From(record));
var custom = new ConfigurableRules(ConfigurableRules.Parse(File.ReadAllText("my-workflows.json")));
```

Rules: `NEWS2.High`, `NEWS2.Medium`, `Sepsis.Screen`, `Sepsis.Lactate`, `Lab.Critical`, `Lab.Hypoglycaemia`, `Lab.Hyperkalaemia.RAAS`, `Drug.Interaction`, `Drug.Duplicate`, `Drug.Allergy`, `Renal.Metformin`, `Renal.Nsaid`, `Bleeding.ElderlyAnticoagulant`. The drug knowledge base (`DrugKnowledge`) is illustrative — integrate a licensed interaction database for production.

## Medical.Signals

```csharp
Signal ecg = EcgSimulator.Generate(30, 88, Rhythm.AtrialFibrillation);
EcgAnalysis a = EcgAnalyzer.Analyze(ecg);   // R peaks (Rust), HR, SDNN, RMSSD, pNN50, RR CV, rhythm
```

## Medical.Privacy

```csharp
var deid = new TextDeidentifier(secret, knownNames: ["Siti", "Budi"]);
deid.Deidentify(note);                                              // [NAME], [NIK], [DATE] …
deid.Deidentify(note, ReplacementStyle.Pseudonyms, "pat-1");        // stable tokens, shifted dates
Masking.Nik("3201234567890001");                                    // 3201********0001
new ConsentRegistry(audit).IsPermitted(id, ConsentPurpose.SatuSehatExchange);
```

## Medical.Emr

```csharp
services.AddMedical(m => m.AddEmr(o => { o.Provider = EmrProvider.SqlServer; o.ConnectionString = cs; o.Redis = "redis:6379"; }));
await app.Services.EnsureEmrDatabaseAsync();      // use EF migrations in production
var repo = scope.ServiceProvider.GetRequiredService<PatientRepository>();
await repo.SaveAsync(record);
var r = await repo.GetRecordAsync("pat-00007", purpose: "treatment");   // cached, audited
```

## Medical.Visualization

`Charts.EcgStrip`, `ObservationChart` (NEWS2), `Trend`, `DivergingBars`, `Histogram`, `Gauge` return SVG strings that use CSS variables (`--ink`, `--teal`, `--ecg-major`…). Use `SvgTheme.Resolve(svg)` for renderers without CSS (Svg.Skia, PDFs).

## Medical.Bpjs — BPJS Kesehatan

```csharp
var vclaim = new VClaimClient(http, BpjsCredentials.FromEnvironment());
JsonNode peserta = await vclaim.PesertaByKartuAsync("0001234567890", DateOnly.FromDateTime(DateTime.Today));

InaCbgClaim claim = InaCbgClaimBuilder.FromRecord(record, nomorSep, nomorKartu, tariff, coderNik, ["87.44"]);
IReadOnlyList<ClaimIssue> issues = InaCbgClaimBuilder.Validate(claim);      // before the INA-CBG grouper
var eklaim = new EKlaimClient(http, "http://eklaim/E-Klaim/ws.php", hexKey);
await eklaim.NewClaimAsync(claim); await eklaim.SetClaimDataAsync(claim);
JsonNode cbg = await eklaim.GrouperAsync(claim.NomorSep);
```

v1.1 additions to Medical.Fhir (FHIR repository, search, history, profile validation, SMART, SATUSEHAT workflow, KFA) and Medical.Hl7 (ORM/OML/SIU/MDM, conformance) are documented in [Interoperability](interoperability.md).
