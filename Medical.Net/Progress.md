# Medical.Net — Progress

Made by Gravicode Studios, led by Kang Fadhil. Status as of 2026-11-14. Roadmap: [PLAN.md](PLAN.md).

## v1.0.0 — published ✅
16 packages on nuget.org (tag `medical-net-v1.0.0`).

## v1.1.0 — published ✅
17 packages on nuget.org (tag `medical-net-v1.1.0`, adds `Medical.Bpjs`). Interoperability depth: FHIR server, SMART on FHIR, SATUSEHAT workflow, HL7 orders and conformance, BPJS.

## v1.2.0 — Imaging and signals: complete, not yet published

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

## To publish
1. Commit `Medical.Net/` and push to `main`.
2. Tag `medical-net-v1.2.0` and push the tag. The workflow publishes the 17 packages with the `NUGET_API_KEY` secret.

## Known limitations
- No public trained weights exist for intracranial haemorrhage. The MONAI path takes user weights (`MEDNET_MONAI_WEIGHTS`); the classical screener is a triage aid.
- The lung-nodule detector misses juxtapleural and ground-glass nodules. The MONAI bundle needs a GPU for practical speed.
- 3D shows isosurfaces; direct volume ray-casting is not implemented.
- SCP-ECG: reference-beat subtraction (Sections 4/5) and bimodal compression are rejected; only the default Huffman table is supported.
- QT measurement has heavy-tailed errors on some recordings. Always over-read automated intervals.
- Embedded terminology is a curated subset; `DrugKnowledge` is illustrative; risk models are trained on synthetic cohorts.
