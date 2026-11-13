# Medical.Net — Progress

Made by Gravicode Studios, led by Kang Fadhil. Status as of 2026-11-13. Roadmap: [PLAN.md](PLAN.md).

## v1.0.0 — complete, not yet published

| Area | Status | Verified by |
|---|---|---|
| Rust kernels (`native/medical-native`) | ✅ | `cargo test` (5); Rust/managed parity test |
| 15 libraries in `src/` | ✅ | Release build, 0 warnings |
| Tests (`tests/Medical.Tests`) | ✅ 94/94 | `dotnet test` |
| CLI `medical` | ✅ | every command run end-to-end |
| Templates (5) | ✅ | installed from the packed `.nupkg` into an isolated hive; each one built (console and Blazor also run) |
| NuGet packaging (16 packages, icon, readme) | ✅ | `build/pack.ps1`; package IDs free on nuget.org |
| Live LLM | ✅ | Claude `claude-opus-5-5` (Anthropic SDK 13.8) — tool calls, Indonesian answer |
| Imaging AI service (`services/medical-ai-service`) | ✅ | FastAPI classical pipeline matches C# screener exactly (3.47 mL, slices 13–16); client fallback when offline |
| Gallery web (Blazor, 13 demos, EN/ID, light/dark) | ✅ | all pages HTTP 200; screenshots reviewed |
| Gallery desktop (Avalonia, 13 demos, EN/ID) | ✅ | headless screenshots reviewed |
| Docs EN + ID, README, screenshots (32) | ✅ | `docs/en`, `docs/id`, `docs/images` |
| Monorepo move + CI (`.github/workflows/medical-net.yml`) | ✅ | Release build + tests in `Vibe_Components/Medical.Net` |

## To publish
1. Commit `Medical.Net/`, `.github/workflows/medical-net.yml` and the README row, then push to `main` (CI builds the kernels for 5 RIDs, tests and packs).
2. Tag `medical-net-v1.0.0` and push the tag — the workflow publishes with the `NUGET_API_KEY` secret.

## Known limitations
- Embedded terminology is a curated subset; load licensed full releases (SNOMED CT RF2, LOINC, ICD-10-CM, RxNorm) with `ReleaseLoaders`.
- `DrugKnowledge` and dosing tables are illustrative; integrate a licensed drug database for production.
- Risk models are trained on synthetic cohorts; the MONAI path needs trained weights (`MEDNET_MONAI_WEIGHTS`).
- `InMemoryFhirStore` is for development, tests and edge gateways.
