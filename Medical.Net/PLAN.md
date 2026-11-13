# Medical.Net — Roadmap

Made by Gravicode Studios, led by Kang Fadhil. Status details: [Progress.md](Progress.md).

## v1.0 — Foundation ✅
- Core domain model, codings, UCUM, audit, event bus, DI, deterministic synthetic data
- FHIR R4 (Firely): mapping, IPS documents, transactions, validation, FHIRPath, in-memory store, SATUSEHAT client
- HL7 v2 (NHapi): ADT/ORU/ACK, HL7→FHIR, MLLP server/client
- DICOM (fo-dicom): metadata, window/level, HU statistics, de-identification, phantoms, DICOMweb, C-ECHO/C-STORE SCU+SCP
- Terminology: ICD-10 / SNOMED CT / LOINC / RxNorm subsets, bilingual search, subsumption, maps, release loaders
- Clinical scores and calculators; NRules decision support; RulesEngine JSON workflows
- Signals (ECG/HRV), privacy (de-identification, consent), EMR (EF Core + Redis), ASP.NET Core FHIR/API/MLLP
- AI: ML.NET risk models, Claude / OpenAI-compatible / offline clinical assistant, CT haemorrhage screen, MONAI service
- Rust kernels with managed fallback; SVG visualization
- CLI, 5 templates, Blazor + Avalonia gallery, bilingual docs, CI and NuGet publishing

## v1.1 — Interoperability depth ✅
- FHIR: persistence-backed FHIR server (EF Core), `_include`/`_revinclude`, `_history`, profile validation against IG packages (Firely `Hl7.Fhir.Specification`), SMART on FHIR launch
- SATUSEHAT: full Encounter→Condition→Observation→MedicationRequest workflow helpers, KFA drug codes, retry/backoff
- HL7 v2: ORM/OML orders, SIU scheduling, MDM documents, segment-level conformance profiles
- BPJS: INA-CBG grouping inputs, VClaim/PCare clients
- Delivered in 1.1.0 — see [docs/en/interoperability.md](docs/en/interoperability.md). Carried forward: SATUSEHAT inpatient / ED workflows and ServiceRequest-Specimen-DiagnosticReport lab flow; HL7 v2.3 / 2.4 conformance profiles; E-Klaim iDRG (when published by Kemenkes)

## v1.2 — Imaging and signals
- Transcoding of compressed transfer syntaxes (JPEG 2000 / JPEG-LS) in the pipeline
- MPR and MIP rendering in Rust; 3D volume viewer with ThreeNet
- MONAI: published weights for haemorrhage and lung-nodule detection, DICOM SEG / SR output
- 12-lead ECG (SCP-ECG, DICOM waveform), QT/QTc, ST deviation

## v1.3 — Clinical intelligence
- Full SOFA, APACHE II, PEWS, MEOWS (obstetric), Wells, PERC, Framingham / ASCVD, WHO growth charts
- Licensed drug-interaction database adapters; renal/hepatic dosing tables
- Retrieval-augmented guideline answers (Kemenkes PNPK, WHO) with citations; Hugging Face clinical NLP (NER, ICD coding suggestions) via HF.Net
- Federated / privacy-preserving model training; model cards and drift monitoring

## v2.0 — Platform
- Multi-tenant hospital information system reference app (registration, outpatient, inpatient, pharmacy, lab, radiology, billing)
- IoT bedside devices via IoTComNet (vital-sign monitors, pumps); real-time NEWS2 dashboard
- Mobile companion (MAUI) for ward rounds; offline-first sync
