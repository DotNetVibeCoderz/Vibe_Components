# Medical.Net — Progress

Made by Gravicode Studios, led by Kang Fadhil. Status as of 2026-11-13. Roadmap: [PLAN.md](PLAN.md).

## v1.0.0 — published ✅
16 packages on nuget.org (tag `medical-net-v1.0.0`). `Medical.Cli` was installed from nuget.org and runs.

## v1.1.0 — Interoperability depth: complete, not yet published

| Area | Status | Verified by |
|---|---|---|
| FHIR repository: `FhirRepositoryBase` with search (token/reference/string/date, prefixes, `:not` `:missing` `:exact` `:contains` `:Type`), `_include`/`_revinclude`, `_sort`, paging, `_summary=count` | ✅ | `FhirServerTests` run on the in-memory **and** SQLite repositories |
| Versioning: `_history`, vread, `If-Match` (412), 410 Gone, atomic transactions with `urn:uuid` rewrite and `ifNoneExist` | ✅ | same, plus HTTP tests via TestServer |
| Durable `EfFhirRepository` (Medical.Emr; JSON versions + search index; DB transaction) | ✅ | restart test (new provider, same DB); template `--persist` restarted with no duplicate seeding and no failed-DDL log |
| Profile validation (`FhirProfileValidator`, R4 core + IG packages, `$validate`, 422 on write) | ✅ | heart-rate profile errors; all mapped vital signs conform (SpO₂ now carries LOINC 2708-6); `specification.zip` reaches consuming apps (fixed `NuGetPackageRoot` without a trailing slash) |
| SMART on FHIR (discovery, authorize + PKCE S256, token, client_credentials, v1/v2 scopes, patient compartment) | ✅ | `SmartTests` end-to-end; template `--smart` probed with curl (401 → token → 200/403) |
| SATUSEHAT workflow (Encounter → Condition → Observation → Medication/KFA → MedicationRequest), `KfaClient`/`KfaCatalog`, `RetryHandler`, 401 refresh | ✅ | `SatuSehatTests` (bundle linkage, processable, every coding has a display, backoff/idempotency, token refresh, KFA parsing) |
| HL7 v2 ORM^O01, OML^O21, SIU^S12, MDM^T02 + `Hl7WorkflowMapping` + FHIR | ✅ | round-trip tests |
| HL7 conformance profiles (6 built-in), `/api/hl7/conformance`, MLLP `EnforceConformance` | ✅ | built messages conform; broken ones are flagged; MDM always emits PV1 |
| `Medical.Bpjs`: VClaim, PCare, E-Klaim, INA-CBG claim builder + coding checks, LZ-String | ✅ | lz-string 1.5.0 vectors, HMAC signature reference, E-Klaim tamper detection, client mocks |
| CLI: `fhir validate --profile`, `hl7 sample --type …`, `hl7 conform`, `satusehat bundle`, `bpjs claim` | ✅ | run end-to-end; also from the packed tool |
| Template `medical-fhir-api --smart --persist` (+ version 1.1.0 for all templates) | ✅ | all 5 templates + the smart/persist variant built from the local feed |
| Gallery: 4 new demos (FHIR server, HL7 orders & scheduling, SATUSEHAT visit, BPJS claim), 17 in total, web + desktop, EN/ID | ✅ | screenshots reviewed; all 44 images refreshed for v1.1.0 |
| Docs: `interoperability.md` (EN/ID), README/README.id, modules, tools, deployment, CLAUDE.md | ✅ | |
| Tests | ✅ 125/125 | `dotnet test -c Release` (in `build/pack.ps1`) |
| Packages (17, incl. new `Medical.Bpjs`) | ✅ | `build/pack.ps1 -Version 1.1.0` |

Bugs found and fixed during v1.1:
- `FhirMapper` codings had no `display`: Firely's 3-argument `CodeableConcept` constructor sets `text`. Fixed with `FhirMapper.Cc`.
- Conditional-create `urn:uuid` remap.
- The `clinical-status` search parameter was missing.
- E-Klaim birth-date format.
- The SEP number is alphanumeric.

## To publish
1. Commit `Medical.Net/` and push to `main` (CI builds the kernels for 5 RIDs, tests and packs).
2. Tag `medical-net-v1.1.0` and push the tag. The workflow publishes the 17 packages with the `NUGET_API_KEY` secret.

## Known limitations
- Embedded terminology is a curated subset; load licensed full releases (SNOMED CT RF2, LOINC, ICD-10-CM, RxNorm) with `ReleaseLoaders`.
- `DrugKnowledge` and dosing tables are illustrative; integrate a licensed drug database for production.
- Risk models are trained on synthetic cohorts; the MONAI path needs trained weights (`MEDNET_MONAI_WEIGHTS`).
- The built-in SMART authorization server auto-approves (development); production needs an identity provider in front of `/auth/authorize`.
- The BPJS and SATUSEHAT clients are verified against mocks and reference crypto vectors, not against live BPJS / Kemenkes sandboxes, which need facility credentials. KFA codes in the gallery are illustrative.
- `EfFhirRepository` pre-filters token/reference criteria in SQL and evaluates the rest in memory. For very large stores, use a dedicated FHIR server.
