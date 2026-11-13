# Architecture

```
                      Medical.Core  (domain model, codings, UCUM, audit, events, DI, synthetic data, PNG)
         ┌───────────────┬──────────┴──────┬───────────────┬──────────────┐
  Medical.Clinical  Medical.Terminology  Medical.Fhir   Medical.Privacy   Medical.Emr
         │               │                  │  (Firely)                    (EF Core, Redis)
         └──── Medical.Rules (NRules, RulesEngine)   Medical.Hl7 (NHapi, MLLP) ──┐
                                                                                 │
  Medical.Native (Rust) ── Medical.Dicom (fo-dicom) ── Medical.Signals           │
                     └────────────── Medical.AI (ML.NET, Anthropic SDK) ─────────┤
  Medical.Visualization (SVG)          Medical.AspNetCore (FHIR REST, API, MLLP host)
```

## Domain model

`Medical.Core` defines immutable records — `Patient`, `Encounter`, `VitalSigns`, `ClinicalObservation`, `Condition`, `MedicationOrder`, `Allergy` — gathered in `PatientRecord`. Codes are `ClinicalCode(System, Code, Display)` with FHIR system URIs (`CodeSystems.Snomed`, `.Loinc`, `.Icd10`, `.RxNorm`, `.Nik`). Units are UCUM (`Ucum.Convert`). User-facing text is `Localized(En, Id)`.

The FHIR model is an interchange format, not the internal model: `FhirMapper` converts both ways, so rules, AI and scores stay independent of any FHIR version.

## Cross-cutting services

- `services.AddMedical(b => b.AddX())` — every module adds an `AddX(this MedicalBuilder)` extension that registers once (`TryAddModule`).
- `IMedicalEventBus` — in-process pub/sub (the MLLP server publishes `Hl7Message`, decision support publishes `ClinicalAlert`).
- `IAuditLog` — every read, query, export, de-identification and alert is recorded (`InMemoryAuditLog`, or `EfAuditLog` in the EMR database).
- `Pseudonymizer` — keyed HMAC-SHA256 pseudonyms and per-subject date shifts shared by DICOM and text de-identification.

## Decision support pipeline

`FactBuilder` turns a `PatientRecord` into facts (`PatientFact`, `VitalsFact` with NEWS2/qSOFA, `LabFact` with interpretation, `MedicationFact` with RxNorm class tags, `AllergyFact`). The NRules rule set (`Medical.Rules.Definitions`) is compiled once into a Rete network; each evaluation creates a session, fires, and collects `ClinicalAlert` facts. Configurable policies (triage, dengue warning signs, referral) are Microsoft RulesEngine JSON workflows over `ClinicalSnapshot`, editable without recompiling.

## Native kernels

Hot loops (window/level on every pixel, HU statistics, ECG QRS detection) run in Rust behind a C ABI. `MedicalKernels` loads the library through a custom resolver and falls back to C# implementations with identical arithmetic; a test asserts both backends produce identical output.

## Design rules

- Libraries are deterministic: synthetic data uses `SeededRandom` (SplitMix64) seeded by FNV-1a hashes — never `GetHashCode`.
- Everything a clinician reads is bilingual (English / Bahasa Indonesia).
- AI never invents numbers: the assistant answers through validated tools; risk models expose their drivers.
- No secrets in code: keys come from environment variables (`ANTHROPIC_API_KEY`, `SATUSEHAT_CLIENT_SECRET`, …).
- Inside `namespace Medical.*`, fully qualified Firely names must start with `global::Hl7.Fhir…` because `Medical.Hl7` shadows the `Hl7` root.
