# Medical.Net — Progress

Made by Gravicode Studios, led by Kang Fadhil. Status as of 2026-11-14. Roadmap: [PLAN.md](PLAN.md).

## v1.0.0 — published ✅
16 packages on nuget.org (tag `medical-net-v1.0.0`).

## v1.1.0 — published ✅
17 packages on nuget.org (tag `medical-net-v1.1.0`, adds `Medical.Bpjs`). Interoperability depth: FHIR server, SMART on FHIR, SATUSEHAT workflow, HL7 orders and conformance, BPJS.

## v1.2.0 — published ✅
17 packages on nuget.org (tag `medical-net-v1.2.0`). Imaging and signals:

| Area | Status | Verified by |
|---|---|---|
| Compressed transfer syntaxes (`fo-dicom.Codecs`: RLE, JPEG Lossless, JPEG-LS, JPEG 2000, HT-J2K; lossy JPEG-LS / J2K) with on-demand decoding | ✅ | lossless syntaxes bit-exact, including through a Part 10 file; lossy syntaxes within their bounds |
| `DicomVolume` (IPP-sorted, LPS geometry), MPR / MIP / MinIP / AvgIP / oblique on the Rust `mdn_slab_i16` kernel | ✅ | Rust and managed outputs bit-identical (all planes and modes); MIP ≥ slice ≥ MinIP; axial MPR reproduces the slices; reformat proportions correct |
| Isosurfaces (surface nets), STL / OBJ, Taubin smoothing | ✅ | meshes watertight and consistently wound; lesion mesh volume matches the analytic ellipsoid (3.45 mL) |
| DICOM SEG + TID 1500 SR (haemorrhage, lung nodules), with readers | ✅ | **highdicom** parses both: frames map onto the source CT instances; measurement groups carry finding, finding site, segment and UCUM units |
| Lung-nodule detection (classical, Lung-RADS v2022), Python twin, MONAI `lung_nodule_ct_detection` in the service | ✅ | C# and Python agree exactly; the MONAI RetinaNet (published LUNA16 weights) finds the phantom nodule (score 0.65, within 4 mm, 4B) |
| 12-lead ECG: VCG simulator, analyzer (PR, QRS, QT, four QTc formulas, axis, ST at J / J+60, UDMI 2018 STEMI criteria, QTc categories) | ✅ | ground truth across rates, STEMI territories with reciprocal leads, sex/age V2–V3 thresholds, AF, long QT |
| QT accuracy against cardiologists (PhysioNet QTDB, 101 records) | ✅ | held-out half: mean −4.9 ms, median \|error\| 18 ms, 70 % within 30 ms (SD 65 ms, a few failed delineations) |
| DICOM 12-lead ECG Waveform; SCP-ECG (raw and default-Huffman) | ✅ | **pydicom** decodes the waveform; an **independent SCP-ECG reader** decodes both SCP files; CRC tamper detection |
| Charts: 12-lead printout, median beats with fiducials | ✅ | rendered and reviewed |
| CLI: `dicom transcode / mpr / mip / mesh / ai`, `ecg12 [analyze]` | ✅ | run end to end on phantom data |
| Gallery: 5 new demos (MPR & MIP, 3D volume with ThreeNet / three.js, compression, imaging AI → SEG / SR, 12-lead ECG), 22 in total, EN / ID | ✅ | 59 screenshots reviewed (desktop EN / ID, web) |
| Docs: `imaging-signals.md` (EN / ID), README / README.id, tools, deployment, modules, service README, CLAUDE.md; `tools/validation` scripts | ✅ | |
| Tests | ✅ 157/157 | `dotnet test`; Release build with 0 warnings |

Bugs and pitfalls found and fixed during v1.2:
- **CharLS probe:** a 2 × 2 image is too small for CharLS's output buffer, so the codec-availability check reported no codecs.
- **MPR border:** round-off made the last row and column count as outside the volume; both kernels now use a tolerance.
- **Slab sampling:** slab projections now sample voxel layers along acquisition axes, so interpolation can't hide a maximum.
- **ECG on 2-lead data:** onset/offset collapsed because velocity is near zero at R; one artefact could hide every beat.
- **T end on real data:** baseline wander, the next P wave and weak T waves misled T-end detection.
- **Huffman decoding:** the SCP-ECG escape codes were decoded wrongly; found through the independent reader cross-check.
- **Screenshots:** virtual time in headless Chrome drops the Blazor circuit, which is why the 3D page has a still mode.

## v1.3.0 — published ✅
18 packages on nuget.org (tag `medical-net-v1.3.0`, adds `Medical.HuggingFace`). Clinical intelligence:

| Area | Status | Verified by |
|---|---|---|
| SOFA, APACHE II (+ predicted mortality), PEWS, MEOWS, Wells PE / DVT, PERC, Child-Pugh | ✅ | hand-computed cases per component; boundary rules (ventilation for SOFA 3–4, Wells "exactly 4 = unlikely") |
| ASCVD Pooled Cohort Equations, Framingham general CVD | ✅ | guideline examples (2.1 / 3.0 / 6.1 %), CVrisk R values (7.95 / 5.08 / FRS 13.53 %); for white men the guideline table prints 5.3 % but its equation gives 5.38 % |
| WHO growth standards 0–5 y and 2007 reference 5–19 y; growth-chart SVG | ✅ | **all 2,101 z-scores of WHO's `survey_who2007_z.csv` match exactly**; anthro package test values (−6.09, −4.81 / −5.02 with the 9-month rule, −2.55); tables downloaded at runtime, not redistributed |
| Drug-interaction sources: built-in, FDA-label contraindications, CSV, DrugBank XML (streamed), composite | ✅ | adapters, severity mapping, failing-source isolation |
| Renal / hepatic dosing from labels (metformin, gabapentin, rivaroxaban, dabigatran, enoxaparin, levofloxacin, ciprofloxacin; apixaban rule) | ✅ | band edges (e.g. rivaroxaban CrCl 50 → 15 mg, 51 → 20 mg) |
| Clinical NLP (EN / ID terminology recognizer, NegEx-style negation + family history), ICD-10 suggestions; HF.Net NER (`Medical.HuggingFace`) | ✅ | Indonesian and English notes; **real `d4data/biomedical-ner-all` run** finds fever / cough / pneumonia / amoxicillin (after word-level aggregation) |
| Guideline RAG: BM25 + embeddings (RRF), LLM answers with validated citations, extractive fallback | ✅ | retrieval across EN / ID; **real Claude answer** to an Indonesian TB question cites the WHO source; **real multilingual embeddings** rank the WHO passage first |
| Federated learning (FedAvg, secure aggregation, DP with RDP ε), model cards, drift (PSI, KS, AUC / ECE alerts) | ✅ | federated AUC within 0.01 of pooled; masked = plain weights; ε falls as σ rises; drift flags a shifted cohort and not a fresh one |
| CLI: `score`, `cvrisk`, `growth`, `renal`, `interactions --sources`, `nlp`, `guideline ask`, `federated`, `drift` | ✅ | run end to end |
| Gallery: 7 new demos (29 in total), web + desktop, EN / ID | ✅ | screenshots reviewed (desktop EN / ID, web light mode) |
| Docs: `clinical-intelligence.md` (EN / ID), README / README.id, tools, PACKAGE, CLAUDE.md | ✅ | |
| Tests | ✅ 194/194 | `dotnet test`; Release build with 0 warnings; 18 packages packed; all 5 templates build against the 1.3.0 packages |

Bugs and pitfalls found and fixed during v1.3:
- **Growth rules:** WHO Anthro ignores a standing measurement below 9 months and stops the 0–5 standards at 60 months; both are matched.
- **DrugBank reader:** `ReadElementContentAsString` already advances the reader, so the next `Read()` skipped the sibling element and every description was lost.
- **Sub-word entities:** the HF model returned "am" + "oxici"; spans are now widened to whole words and merged.
- **Bilingual retrieval:** glossary translations outranked direct matches until they were given half weight.
- **HF.Net embeddings** return an `NdArray`; it is converted with `ToArray()`.
- **pack.ps1** had a hard-coded default version; it now reads `VersionPrefix`.

## v2.0.0 — Hospital platform: complete, not yet published

| Area | Status | Verified by |
|---|---|---|
| `Medical.Hospital`: multi-tenant HIS core (shared schema, query filters, cross-tenant write guard, per-tenant counters) | ✅ | tenant isolation test (same NIK in two hospitals, foreign write throws), separate MRN sequences |
| Registration and queues, outpatient SOAP + ICD-10, admission / transfer / discharge with room charges and discharge summary | ✅ | NIK de-duplication, daily queue numbers, JKN no-VIP rule, 3-day stay billed 3 days |
| Pharmacy review (interactions, allergy, renal from latest creatinine, stock) and FEFO dispensing with audited override | ✅ | expired batch untouched, earliest expiry used first, Stop findings block dispensing |
| Laboratory (critical values, read-back before verification, HL7 OML^O21 out, ORU^R01 in by placer number), radiology worklist and reports | ✅ | critical potassium event, verification blocked until read-back, ORU import matched |
| Billing: Umum / BPJS / insurance invoices, payments, INA-CBG claim data with pre-grouping checks; dashboard (BOR, ALOS, queues, NEWS2 watch list); FHIR IPS from the HIS record | ✅ | outpatient + inpatient bills, claim tariff breakdown, IPS bundle from a seeded patient |
| Real-time NEWS2 engine: plausibility + median filter, RCP escalation, confirmed alerts with hysteresis, no alerts without a bound patient, 15-minute charting | ✅ | escalation / clearing sequence, motion artefact suppressed, stale data reported |
| `Medical.Devices` (2.0.0-preview): HL7 / MLLP monitors and MQTT / SenML sensors and pumps on IoTCom.Net, simulators, gateway | ✅ | **IoTCom.Net simulators over real MLLP**: sepsis bed alerts, stable bed does not; **real MQTT broker**: pump near-empty alarm and oximeter readings |
| `Medical.Sync`: hybrid logical clock, AppendOnly / LastWriterWins / ServerWins / ReadOnly, SQLite client, cursors by server sequence | ✅ | clock skew, offline edits converge, read-only census restored, late offline vitals delivered, 1,234-record paging |
| `apps/Medical.His` reference app (Blazor Server): 9 modules, roles, live monitoring wall, sync and FHIR APIs | ✅ | in-process tests: sign-in required, cross-tenant API 403, ward-scoped pulls (missing / unknown ward → 400), device key, local-only redirects; 12 screenshots reviewed |
| `apps/Medical.Rounds` (MAUI, Windows; Android with `-p:IncludeAndroid=true`) + `Medical.Rounds.Core` | ✅ | tablet syncs with the HIS over HTTP; offline vitals survive an app restart; Windows build and self-captured screenshots (EN / ID) |
| CLI: `his seed / dashboard / patient`, `monitor`, `sync demo`; gallery: 3 demos (32 in total), web + desktop, EN / ID | ✅ | run end to end; screenshots reviewed |
| Docs: `platform.md` (EN / ID), README / README.id, tools, PACKAGE, PLAN, CLAUDE.md | ✅ | |
| Tests | ✅ 213/213 | `dotnet test`; Release build with 0 warnings; 21 packages packed (Medical.Devices as 2.0.0-preview, no stable package depends on a prerelease); all 5 templates build against 2.0.0 |

Bugs and pitfalls found and fixed during v2.0:
- **Static assets:** the Blazor app run from `bin` in Production served empty CSS / JS (static web assets resolve only in Development or after publish) — screenshots run in Development.
- **Security review of the HIS app:** unscoped pulls for missing / unknown wards, a demo device key accepted outside demo mode, the password-less login active with demo mode off, an anonymous wards endpoint, a `//host` open redirect and no HTTPS / HSTS — all fixed and covered by tests.
- **MAUI:** the repo-wide `TargetFramework=net10.0` broke the MAUI build (cleared in the project); the `maui-android` workload was required even for Windows; render-to-bitmap capture skipped native text boxes (PrintWindow capture instead).
- **IoTCom.Net:** `Hl7Message.Get("MSH-3")` returns empty (use `Sender`); `System.Formats.Cbor` 10.0.12 (CVE-2026-118747) pinned to 10.0.15.
- **Packaging:** `-p:Version` overrode the preview suffix of `Medical.Devices`; CI and `pack.ps1` now pass `-p:VersionPrefix`.
- **Gallery:** blocking the Avalonia UI thread on async scenarios deadlocked the desktop screenshots; a fixed delay made the MQTT test flaky (readiness probe instead).

## To publish
1. Commit `Medical.Net/` and push to `main`.
2. Tag `medical-net-v2.0.0` and push the tag. The workflow publishes the 21 packages (new: `Medical.Hospital`, `Medical.Sync`, and `Medical.Devices` as 2.0.0-preview) with the `NUGET_API_KEY` secret.

## Known limitations
- No public trained weights exist for intracranial haemorrhage. The MONAI path takes user weights (`MEDNET_MONAI_WEIGHTS`); the classical screener is a triage aid.
- The lung-nodule detector misses juxtapleural and ground-glass nodules. The MONAI bundle needs a GPU for practical speed.
- 3D shows isosurfaces; direct volume ray-casting is not implemented.
- SCP-ECG: reference-beat subtraction (Sections 4/5) and bimodal compression are rejected; only the default Huffman table is supported.
- QT measurement has heavy-tailed errors on some recordings. Always over-read automated intervals.
- Embedded terminology is a curated subset; `DrugKnowledge` is illustrative; risk models are trained on synthetic cohorts.
- Licensed interaction databases (FDB, Medi-Span, Lexicomp) need an `IDrugInteractionSource` written against the vendor SDK; DrugBank XML has no severity grades, so they are inferred from the text.
- The guideline sample corpus is short summaries; load the official PNPK / WHO documents. The ICD-10 coder suggests codes from the curated subset only.
- Federated learning covers logistic models; deep-model federation and a real key-agreement protocol for the masks are not included.
- The HIS reference app signs in demo users without passwords (switch off with `His:DemoLogin=false` and add OpenID Connect); monitors and pumps in the app are simulated unless `His:MllpPort` receives real devices; NEWS2 from monitors assumes Alert and room air until a nurse sets them.
- `Medical.Devices` stays preview until IoTCom.Net is stable; device integrations are not certified medical-device software.
- The MAUI app is built and screenshotted on Windows; Android builds need the Android SDK and are not part of CI.
