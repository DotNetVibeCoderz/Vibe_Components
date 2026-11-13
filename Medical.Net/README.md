# Medical.Net

**Comprehensive Medical Platform for .NET 10** — FHIR R4, HL7 v2, DICOM, SNOMED CT, ICD-10, LOINC and RxNorm in one toolkit, with clinical decision support, imaging AI, an LLM clinical assistant and Rust kernels for the hot paths.

Made by **Gravicode Studios**, led by **Kang Fadhil**. · [Bahasa Indonesia](README.id.md) · [Documentation](docs/en/README.md) · [Dokumentasi](docs/id/README.md)

![Medical.Net Gallery](docs/images/web-home.png)

> Medical.Net is a software toolkit, not a certified medical device. Clinical scores, rules and AI outputs support — never replace — the judgement of a qualified clinician. All demo data is synthetic.

## Packages

| Package | What it gives you | Built on |
|---|---|---|
| `Medical.Core` | Patient, encounter, vitals, observation, condition, medication and allergy model; codings; UCUM units; audit trail; event bus; `services.AddMedical(...)`; deterministic synthetic patients | — |
| `Medical.Fhir` | Domain ↔ FHIR R4 mapping, transaction and **International Patient Summary** bundles, JSON round-trip, validation, FHIRPath, in-memory FHIR store, **SATUSEHAT** client | Firely .NET SDK 6.8 |
| `Medical.Hl7` | HL7 v2 parse/build (ADT^A01, ORU^R01, ACK), HL7 v2 → domain → FHIR, MLLP server and client | NHapi 3.2 |
| `Medical.Dicom` | Metadata, VOI window/level rendering to PNG, HU statistics, PS3.15 de-identification with stable pseudonyms, synthetic CT phantoms, DICOMweb (QIDO/WADO/STOW), C-ECHO / C-STORE SCU + SCP | fo-dicom 5.2 |
| `Medical.Terminology` | ICD-10 (WHO 2010 / INA-CBG), SNOMED CT, LOINC, RxNorm — EN/ID search, subsumption, value-set expansion, SNOMED→ICD-10, check digits, RF2 / LOINC / ICD-10-CM / RxNorm loaders | — |
| `Medical.Clinical` | NEWS2, qSOFA, SIRS, CURB-65, CHA₂DS₂-VASc, HAS-BLED, GCS, APGAR, MELD, CKD-EPI 2021 eGFR, Cockcroft-Gault, BMI (WHO / Asia-Pacific), BSA, dosing, lab interpretation | — |
| `Medical.Rules` | Forward-chaining decision support: early warning, Sepsis-3, critical labs, hypoglycaemia, hyperkalaemia, drug interactions, allergy conflicts, renal dosing, duplicate therapy; editable JSON workflows (triage, dengue warning signs, referral) | NRules, Microsoft RulesEngine |
| `Medical.AI` | ML.NET risk models (48-h deterioration, type 2 diabetes) with per-feature explanations; tool-grounded clinical assistant on **Claude** (Anthropic SDK), any OpenAI-compatible endpoint (Hugging Face, Azure OpenAI, DeepSeek, Ollama) or offline rules; CT haemorrhage screening; MONAI service client | ML.NET, Anthropic SDK |
| `Medical.Signals` | Synthetic ECG/PPG, filters, Pan-Tompkins R peaks, HRV, rhythm screening (AF, ectopy) | `Medical.Native` |
| `Medical.Privacy` | Clinical-note de-identification (HIPAA Safe Harbor + Indonesian NIK/phone/address), pseudonyms, date shifting, masking, consent registry (UU PDP) | — |
| `Medical.Emr` | EF Core EMR store on SQL Server / SQLite / in-memory, Redis cache, persistent audit | EF Core 10 |
| `Medical.AspNetCore` | FHIR REST facade (`MapMedicalFhir`), clinical JSON API (`MapMedicalApi`), hosted MLLP listener | ASP.NET Core |
| `Medical.Visualization` | SVG ECG strips, NEWS2 observation charts, trends, gauges, risk-driver bars, histograms | — |
| `Medical.Native` | Rust kernels (window/level, pixel statistics, QRS detection) with a bit-identical managed fallback | Rust |
| `Medical.Cli` | `medical` .NET tool | — |
| `Medical.Templates` | `dotnet new` templates | — |

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
app.MapMedicalFhir();   // /fhir/metadata, /fhir/Patient?name=…, POST /fhir (transaction), $everything
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

dotnet new install Medical.Templates
dotnet new medical-fhir-api -n MyHospital.Api      # also: medical-console, medical-hl7-gateway, medical-blazor, medical-avalonia
```

## Gallery

Every capability runs live in the **Medical.Net Gallery**, in Blazor (web) and Avalonia (desktop), in English and Bahasa Indonesia:

| Web (Blazor) | Desktop (Avalonia) |
|---|---|
| ![](docs/images/web-news2.png) | ![](docs/images/desktop-dicom.png) |
| ![](docs/images/web-cds.png) | ![](docs/images/desktop-ecg.png) |
| ![](docs/images/web-hemorrhage.png) | ![](docs/images/desktop-assistant.png) |

```bash
dotnet run --project gallery/Medical.Gallery.Web --urls http://localhost:5290
dotnet run --project gallery/Medical.Gallery.Desktop
```

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
