# Medical.Net

**Comprehensive Medical Platform for .NET 10** — FHIR R4, HL7 v2, DICOM, SNOMED CT, ICD-10, LOINC and RxNorm in one toolkit, with clinical decision support, imaging AI, an LLM clinical assistant and Rust kernels for the hot paths.

Made by **Gravicode Studios**, led by **Kang Fadhil**. · [Bahasa Indonesia](README.id.md) · [Documentation](docs/en/README.md) · [Dokumentasi](docs/id/README.md)

![Medical.Net Gallery](docs/images/web-home.png)

> Medical.Net is a software toolkit, not a certified medical device. Clinical scores, rules and AI outputs support — never replace — the judgement of a qualified clinician. All demo data is synthetic.

## Packages

| Package | What it gives you | Built on |
|---|---|---|
| `Medical.Core` | Patient, encounter, vitals, observation, condition, medication and allergy model; codings; UCUM units; audit trail; event bus; `services.AddMedical(...)`; deterministic synthetic patients | — |
| `Medical.Fhir` | Domain ↔ FHIR R4 mapping, transaction and **International Patient Summary** bundles, JSON round-trip, FHIRPath; FHIR repository with real search (`_include`/`_revinclude`, `_sort`, date prefixes, modifiers), `_history`, If-Match; **profile validation** against R4 core and IG packages; **SMART on FHIR** client; **SATUSEHAT** visit workflow, KFA, retry/backoff | Firely .NET SDK 6.8 |
| `Medical.Hl7` | HL7 v2 parse/build (ADT^A01, ORU^R01, **ORM^O01, OML^O21, SIU^S12, MDM^T02**, ACK), segment-level **conformance profiles**, HL7 v2 → domain → FHIR, MLLP server and client | NHapi 3.2 |
| `Medical.Dicom` | Metadata, VOI window/level rendering, HU statistics, **JPEG / JPEG-LS / JPEG 2000 / RLE codecs**, **volumes with MPR / MIP / MinIP** (Rust kernel), **isosurfaces → STL / OBJ**, **DICOM SEG + TID 1500 SR**, **12-lead ECG waveform**, PS3.15 de-identification, synthetic CT phantoms, DICOMweb, C-ECHO / C-STORE SCU + SCP | fo-dicom 5.2, fo-dicom.Codecs |
| `Medical.Terminology` | ICD-10 (WHO 2010 / INA-CBG), SNOMED CT, LOINC, RxNorm — EN/ID search, subsumption, value-set expansion, SNOMED→ICD-10, check digits, RF2 / LOINC / ICD-10-CM / RxNorm loaders | — |
| `Medical.Clinical` | NEWS2, qSOFA, SIRS, CURB-65, CHA₂DS₂-VASc, HAS-BLED, GCS, APGAR, MELD, **SOFA, APACHE II, PEWS, MEOWS, Wells, PERC, Child-Pugh**, **Pooled Cohort Equations / Framingham**, **WHO growth standards (0–19 y)**, CKD-EPI 2021 eGFR, Cockcroft-Gault, BMI (WHO / Asia-Pacific), BSA, dosing, **renal / hepatic dosing from FDA labels**, lab interpretation | — |
| `Medical.Rules` | Forward-chaining decision support: early warning, Sepsis-3, critical labs, hypoglycaemia, hyperkalaemia, drug interactions, allergy conflicts, renal dosing, duplicate therapy; editable JSON workflows (triage, dengue warning signs, referral); **pluggable drug-interaction sources** (FDA-label contraindications, CSV, DrugBank XML, composite) | NRules, Microsoft RulesEngine |
| `Medical.AI` | ML.NET risk models (48-h deterioration, type 2 diabetes) with per-feature explanations; tool-grounded clinical assistant on **Claude** (Anthropic SDK), any OpenAI-compatible endpoint (Hugging Face, Azure OpenAI, DeepSeek, Ollama) or offline rules; CT haemorrhage screening and **lung-nodule detection (Lung-RADS)** with results as **DICOM SEG + SR**; MONAI service client (incl. the `lung_nodule_ct_detection` model-zoo bundle); **clinical NLP** (EN / ID entities, negation, ICD-10 suggestions); **guideline answers with citations** (BM25 + embeddings); **federated learning** with secure aggregation and differential privacy; **model cards** and **drift monitoring** | ML.NET, Anthropic SDK |
| `Medical.HuggingFace` | Hugging Face models in-process: biomedical NER (`d4data/biomedical-ner-all`) and multilingual sentence embeddings for guideline retrieval | HF.Net (GraviTransformers) |
| `Medical.Signals` | Synthetic ECG/PPG, filters, Pan-Tompkins R peaks, HRV, rhythm screening (AF, ectopy); **12-lead ECG**: simulator, median beats, PR / QRS / QT, four QTc formulas, axis, ST and STEMI criteria (UDMI 2018), **SCP-ECG** read/write | `Medical.Native` |
| `Medical.Privacy` | Clinical-note de-identification (HIPAA Safe Harbor + Indonesian NIK/phone/address), pseudonyms, date shifting, masking, consent registry (UU PDP) | — |
| `Medical.Bpjs` | **BPJS Kesehatan**: VClaim and PCare clients (HMAC signature, AES + LZ-String responses), E-Klaim web service (encrypted), INA-CBG claim builder with pre-grouping coding checks | — |
| `Medical.Emr` | EF Core EMR store on SQL Server / SQLite / in-memory, **durable versioned FHIR repository**, Redis cache, persistent audit | EF Core 10 |
| `Medical.AspNetCore` | FHIR REST server (`MapMedicalFhir`: search, `_history`, vread, `$validate`, `$everything`, transactions, ETags), **SMART on FHIR authorization** (`MapSmartAuthorization`), clinical JSON API, hosted MLLP listener with conformance checks | ASP.NET Core |
| `Medical.Visualization` | SVG ECG strips, NEWS2 observation charts, trends, gauges, risk-driver bars, histograms, **WHO growth charts**, score bars, XY charts | — |
| `Medical.Native` | Rust kernels (window/level, pixel statistics, QRS detection, **MPR / slab projections**) with a bit-identical managed fallback | Rust |
| `Medical.Hospital` | **Multi-tenant HIS core**: shared-schema EF Core model (SQLite / SQL Server) with tenant query filters and write guards; registration (NIK de-duplication, MRN, daily queue numbers), SOAP + ICD-10, vitals with NEWS2, admission / transfer / discharge; pharmacy review (interactions, allergies, renal dosing) with FEFO dispensing; lab critical values with read-back and HL7 OML / ORU; radiology worklist and reports; billing (Umum / BPJS / insurance) with INA-CBG claim data; dashboard (BOR, ALOS, queues); real-time NEWS2 `MonitoringEngine` | EF Core 10 |
| `Medical.Sync` | **Offline-first sync**: hybrid logical clocks, per-type conflict policies (append-only, last-writer-wins, server-wins, read-only), server-sequence pull cursors, SQLite client store, HTTP / in-process transports | Microsoft.Data.Sqlite |
| `Medical.Devices` *(preview)* | **Bedside devices**: HL7 v2 / MLLP patient monitors, MQTT + SenML sensors and infusion pumps, monitor and pump simulators, a gateway into the NEWS2 engine. `2.0.0-preview` because IoTCom.Net is prerelease | IoTCom.Net 0.22 (preview) |
| `Medical.Cli` | `medical` .NET tool | — |
| `Medical.Templates` | `dotnet new` templates | — |

## What's new in 2.0

- **Hospital information system core** (`Medical.Hospital`): one database for many hospitals, with every row tenant-stamped, filtered and guarded against cross-tenant writes. It covers registration and queues, outpatient and inpatient care, pharmacy, laboratory, radiology, billing with BPJS INA-CBG claim data, and dashboards.
- **Real-time monitoring** (`Medical.Devices`, preview): HL7 / MLLP monitors and MQTT / SenML sensors and pumps (IoTCom.Net) feed a NEWS2 engine with plausibility and median filtering, RCP escalation, confirmed alerts with hysteresis, and 15-minute charting for nurse validation.
- **Offline-first sync** (`Medical.Sync`): hybrid logical clocks, append-only clinical data, last-writer-wins tasks, a read-only census, and pull cursors by server sequence so late offline pushes are never skipped.
- **Reference apps**: a Blazor Server HIS for two fictional hospitals (`apps/Medical.His`) and a MAUI ward-round app for Windows and Android (`apps/Medical.Rounds`).

See [Hospital platform](docs/en/platform.md).

## What's new in 1.3

- **Scores**: SOFA, APACHE II (with predicted mortality), PEWS, MEOWS, Wells PE / DVT, PERC, Child-Pugh; ASCVD Pooled Cohort Equations and Framingham, cross-checked against an independent implementation.
- **WHO growth**: the Child Growth Standards and 2007 Reference with WHO's own algorithms. The engine matches all 2,101 z-scores of WHO's validation survey; the tables are fetched from WHO at runtime.
- **Drug safety**: pluggable interaction sources (FDA-label contraindications, CSV exports, licensed DrugBank XML) and renal / hepatic dosing from the labels.
- **Clinical NLP**: English / Indonesian entities with negation and family history, ICD-10 suggestions, and Hugging Face NER through HF.Net (`Medical.HuggingFace`).
- **Guidelines**: hybrid retrieval over Kemenkes PNPK / WHO texts and LLM answers with validated citations, with an extractive answer offline.
- **Federated learning**: secure aggregation, differential privacy with an ε budget, model cards by subgroup and drift monitoring.

See [Clinical intelligence](docs/en/clinical-intelligence.md).

## What's new in 1.2

- **Imaging**: JPEG / JPEG-LS / JPEG 2000 / HT-J2K / RLE codecs; `DicomVolume` with MPR, MIP, MinIP and oblique slabs on a Rust kernel; surface-nets isosurfaces → STL; a 3D viewer with ThreeNet (desktop) and three.js (web).
- **Imaging AI**: lung-nodule detection with Lung-RADS v2022; haemorrhage and nodule results as **DICOM SEG + TID 1500 SR** (validated with highdicom); the MONAI `lung_nodule_ct_detection` bundle in the imaging service.
- **12-lead ECG**: QT/QTc (Bazett, Fridericia, Framingham, Hodges), axis, ST at J, STEMI criteria (UDMI 2018); DICOM 12-lead waveform and SCP-ECG; QT validated against the PhysioNet QT Database.

See [Imaging and signals](docs/en/imaging-signals.md).

## What's new in 1.1

- **FHIR server**: durable EF Core repository, `_include`/`_revinclude`, `_history`/vread, If-Match, date/token/reference/string search with modifiers, `$validate` against R4 core and IG packages, **SMART on FHIR** (PKCE, backend services, patient compartments).
- **SATUSEHAT**: one-call outpatient visit bundle (Encounter → Condition → Observation → Medication/KFA → MedicationRequest), KFA lookup, retry/backoff and token refresh.
- **HL7 v2**: ORM/OML orders, SIU scheduling, MDM documents, segment-level conformance profiles.
- **BPJS** (`Medical.Bpjs`): VClaim, PCare, E-Klaim and INA-CBG claim checks.

See [Interoperability](docs/en/interoperability.md).

## Quick start

```bash
dotnet add package Medical.Rules
dotnet add package Medical.Fhir
```

```csharp
using Medical.Clinical; using Medical.Core; using Medical.Fhir; using Medical.Rules;

var record = SyntheticData.Record(3, deterioration: 0.8);          // or load from your EMR / FHIR server / HL7 feed
Console.WriteLine(ClinicalScores.News2(record.LatestVitals!));     // NEWS2 14/20 (High) — …
foreach (var alert in new ClinicalDecisionSupport().Evaluate(record))
    Console.WriteLine(alert);                                      // [Critical] Possible sepsis — start Sepsis Six …
var ips = new FhirMapper().ToPatientSummary(record);               // FHIR R4 International Patient Summary
File.WriteAllText("ips.json", FhirJson.Serialize(ips));
```

A FHIR server with an HL7 listener in a few lines:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMedical(m => m.AddMedicalApi().AddMllpListener(o => o.Port = 2575));
var app = builder.Build();
app.MapMedicalFhir();   // /fhir/metadata, /fhir/Observation?patient=…&_include=Observation:patient, _history, $validate, POST /fhir (transaction)
app.MapMedicalApi();    // /api/scores/news2, /api/terminology/search, /api/hl7/to-fhir, …
app.Run();
```

## Tools

```bash
dotnet tool install -g Medical.Cli
medical news2 --rr 24 --spo2 93 --o2 --sbp 105 --hr 115 --temp 38.5 --lang id
medical code search "gagal jantung"
medical dicom phantom --out head-ct && medical dicom hemorrhage head-ct
medical ask "Bagaimana kondisi pasien ini?" --patient 7      # Claude when ANTHROPIC_API_KEY is set
medical hl7 sample --type orm --out orm.hl7 && medical hl7 conform orm.hl7
medical dicom mip ct/ --plane coronal --out mip.png && medical dicom ai ct/ --out-dir results
medical ecg12 --pattern inferior --out ecg.svg
medical satusehat bundle --index 3 && medical bpjs claim --index 7
medical score sofa --pf 150 --ventilated --plt 80 --gcs 13 && medical growth --sex f --age-months 24 --weight 9 --height 77.5
medical nlp "demam berdarah, tidak ada sesak napas" && medical guideline ask "Berapa lama pengobatan TB paru?"
medical his seed --db his.db && medical his dashboard --db his.db --tenant rs-nusantara
medical monitor --minutes 120 && medical sync demo

dotnet new install Medical.Templates
dotnet new medical-fhir-api -n MyHospital.Api --smart --persist   # also: medical-console, medical-hl7-gateway, medical-blazor, medical-avalonia
```

## Gallery

Thirty-two use cases run live in the **Medical.Net Gallery**, in Blazor (web) and Avalonia (desktop), in English and Bahasa Indonesia:

| Web (Blazor) | Desktop (Avalonia) |
|---|---|
| ![](docs/images/web-news2.png) | ![](docs/images/desktop-dicom.png) |
| ![](docs/images/web-cds.png) | ![](docs/images/desktop-ecg.png) |
| ![](docs/images/web-hemorrhage.png) | ![](docs/images/desktop-assistant.png) |
| ![](docs/images/web-fhir-server.png) | ![](docs/images/desktop-bpjs.png) |
| ![](docs/images/web-satusehat.png) | ![](docs/images/desktop-hl7-orders.png) |
| ![](docs/images/web-volume3d.png) | ![](docs/images/desktop-ecg12.png) |
| ![](docs/images/web-mpr.png) | ![](docs/images/desktop-imaging-ai.png) |
| ![](docs/images/web-icu-scores.png) | ![](docs/images/desktop-growth.png) |
| ![](docs/images/web-clinical-nlp.png) | ![](docs/images/desktop-federated.png) |
| ![](docs/images/web-live-monitoring.png) | ![](docs/images/desktop-offline-sync.png) |

```bash
dotnet run --project gallery/Medical.Gallery.Web --urls http://localhost:5290
dotnet run --project gallery/Medical.Gallery.Desktop
```

## Hospital apps

The HIS reference app runs two fictional hospitals — RS Medika Nusantara (Jakarta) and RSUD Kota Bahari (Makassar) — with simulated ICU monitors:

| | |
|---|---|
| ![](docs/images/his-dashboard-en.png) | ![](docs/images/his-monitoring-en.png) |
| ![](docs/images/his-pharmacy.png) | ![](docs/images/his-billing.png) |
| ![](docs/images/rounds-ward.png) | ![](docs/images/rounds-vitals.png) |

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project apps/Medical.His --urls http://localhost:5300   # demo sign-in at /login
dotnet build Medical.Rounds.slnx                                    # MAUI ward rounds (needs the maui-windows workload)
dotnet run --project apps/Medical.Rounds -f net10.0-windows10.0.19041.0
```

The demo sign-in has no passwords; replace it with OpenID Connect and set `His:DeviceKeys:{tenant}` before any real use. See [Hospital platform](docs/en/platform.md).

## Build from source

```bash
pwsh build/build-native.ps1          # Rust kernels → src/Medical.Native/runtimes/<rid>/native (optional; managed fallback otherwise)
dotnet build Medical.Net.slnx
dotnet test tests/Medical.Tests
pwsh build/pack.ps1                  # NuGet packages → artifacts/packages
```

Imaging AI service (MONAI / PyTorch, optional): `services/medical-ai-service` — see its README.

Documentation: [English](docs/en/README.md) · [Bahasa Indonesia](docs/id/README.md) · Roadmap: [PLAN.md](PLAN.md) · Status: [Progress.md](Progress.md)

---
© 2026 Gravicode Studios. Made by Gravicode Studios, led by Kang Fadhil. MIT licence.
