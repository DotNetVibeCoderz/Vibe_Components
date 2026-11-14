# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Medical.Net is a "Comprehensive Medical Platform for .NET" (.NET 10 + Rust). It lives as a subfolder of the `DotNetVibeCoderz/Vibe_Components` monorepo.
The requirements are in `Medical.Net.txt` (Indonesian) and `solution-design.md`. The roadmap is `PLAN.md` and status is `Progress.md`; update `Progress.md` when you finish work.

## Commands (run from this folder)

```bash
dotnet build Medical.Net.slnx
dotnet test tests/Medical.Tests                                              # all tests (xUnit)
dotnet test tests/Medical.Tests --filter "FullyQualifiedName~ClinicalScoreTests"            # one class
dotnet test tests/Medical.Tests --filter "FullyQualifiedName~ClinicalScoreTests.News2RcpWorkedExample"
cd native/medical-native && cargo test --release                             # Rust kernels
pwsh build/build-native.ps1        # cargo build + copy into src/Medical.Native/runtimes/<rid>/native
pwsh build/pack.ps1 [-SkipNative] [-SkipTests]   # all NuGet packages → artifacts/packages
dotnet run --project gallery/Medical.Gallery.Web --urls http://localhost:5290   # /?lang=id
dotnet run --project gallery/Medical.Gallery.Desktop -- --screenshot docs/images [en|id]
# web shots: chrome --headless=new --force-prefers-reduced-motion --blink-settings=preferredColorScheme=1 --window-size=1440,1000 --screenshot=<file> http://localhost:5290/demo/<slug>
dotnet run --project tools/Medical.Cli -- help
ASPNETCORE_ENVIRONMENT=Development dotnet run --project apps/Medical.His --urls http://localhost:5300   # HIS (static assets need Development or a publish)
# HIS shots (fresh profile each time, so no old cookie): chrome --headless=new --user-data-dir=<new temp dir> --blink-settings=preferredColorScheme=1 --window-size=1440,1000 --screenshot=docs/images/his-<page>[-en].png "http://localhost:5300/account/demo?tenant=rs-nusantara&user=admin&lang=en|id&returnUrl=/rs-nusantara/<page>"
dotnet build Medical.Rounds.slnx   # MAUI ward rounds (maui-windows workload; -p:IncludeAndroid=true + maui-android for Android)
# Rounds shots, HIS running: apps/Medical.Rounds/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Medical.Rounds.exe --capture docs/images [en|id]
cd services/medical-ai-service && uvicorn app:app --port 8000                # optional imaging AI
```

Windows PowerShell 5.1 can run the build scripts (`powershell -File build\pack.ps1`). A running gallery locks DLLs and `dotnet build` then fails with MSB3027, so stop it first.
To verify templates, install the packed `Medical.Templates` into a custom hive (`--debug:custom-hive`) with a `nuget.config` that points at `artifacts/packages`.

## Architecture

- `src/Medical.Core` is the base: immutable domain records gathered in `PatientRecord`, `ClinicalCode`/`CodeSystems`, `Ucum`, `Localized(En, Id)`, `IAuditLog`, `IMedicalEventBus`,
  `Pseudonymizer`, `PngEncoder`, `SyntheticData`, and `services.AddMedical(b => b.AddX())`. Each module adds `AddX(this MedicalBuilder)`, which calls `TryAddModule` once.
- FHIR is an interchange format: `FhirMapper` maps domain ↔ Firely POCOs (build codings with `FhirMapper.Cc`, because Firely's 3-argument `CodeableConcept` constructor sets `text`, not `Coding.display`).
  Storage goes through `IFhirRepository`. `FhirRepositoryBase` (Medical.Fhir/Search) implements search, includes, history, If-Match and transactions on top of a few storage primitives
  (`Current`, `Version`, `HistoryVersions`, `CurrentOfType`, `Append`, `Atomic`). The primitives are implemented by `InMemoryFhirStore` and by `EfFhirRepository` in Medical.Emr (JSON versions plus the `FhirSearchIndex` table).
  Search parameters per type are listed in `FhirSearchIndex.ParametersFor`/`Extract`; add a parameter in both places.
  `MapMedicalFhir()` and `MapSmartAuthorization()` live in Medical.AspNetCore; SMART scope checks run in `FhirEndpoints.Guard`.
- `FhirProfileValidator` needs the R4 core `specification.zip`, which `buildTransitive/Medical.Fhir.targets` copies into consuming apps. The first validation takes about 10 s.
- HL7 v2: `Hl7V2.Build*` (ADT, ORU, ORM, OML, SIU, MDM via NHapi `Terser` paths; group names come from `<Message>.Names`) and `Hl7WorkflowMapping` back to the domain.
  Conformance profiles are in `Medical.Hl7/Conformance/profiles.json` (embedded).
- v1.2 imaging: `DicomCodecs` (fo-dicom.Codecs; `DicomImaging.FrameBytes` decompresses on demand), `DicomVolume` (LPS geometry) → `VolumeRendering`
  (MPR / slab on the Rust `mdn_slab_i16` kernel; its managed twin in `Medical.Native/VolumeKernels.cs` must stay bit-identical — parity test), `IsoSurface`
  (surface nets; `SurfaceMesh.Smooth` is display-only), `DicomSegmentation` + `MeasurementReport` (SEG / TID 1500 SR — validate changes with highdicom,
  `tools/validation`). `Medical.AI`: `LungNoduleDetector`, `ImagingAiReports`; the Python twin is `services/medical-ai-service/lung.py`.
- v1.2 signals: `Ecg12Simulator` (VCG dipoles; truth uses the tangent 2σ convention), `Ecg12Analyzer` (`Measure` core shared by `Analyze` and
  `MeasureIntervals`; area-curve T end with a 96 ms window chosen on half of QTDB — report held-out numbers only), `ScpEcg`, `DicomEcg` (Medical.Dicom).
- Gallery 3D: desktop `Volume3D.cs` (ThreeNet; offscreen when `Program.Headless`), web `wwwroot/medVolume.js` (three.js r170 vendored in `wwwroot/three`,
  meshes from `/mesh/{name}.bin`; screenshot at `/demo/volume3d?still=1` because virtual time drops the Blazor circuit).
- v1.3 clinical intelligence: scores in `Medical.Clinical/CriticalCareScores.cs` (Wells PE uses half-point units in `Score`; `ExactScore` holds the published value)
  and `CardiovascularRisk.cs` (coefficients checked against CVrisk). `WhoGrowthStandards` follows WHO anthro / anthroplus exactly (restricted z beyond ±3 SD,
  0.7 cm at 731 days, interpolation); the WHO tables are downloaded at runtime from pinned commits — never embed them (licence). `OrganDosing` holds label dosing
  bands. `Medical.Rules/InteractionSources.cs`: `IDrugInteractionSource` + label / CSV / DrugBank / composite sources.
  `Medical.AI`: `Nlp/ClinicalNlp.cs` (terminology recognizer, NegEx-style `Context`, `Icd10Coder`), `Guidelines/GuidelineRag.cs` (BM25 + optional
  `ITextEmbedder`, RRF, citation validation, extractive fallback; the sample corpus is our own summaries), `Learning/*` (FedAvg + pairwise masks + DP, `ModelEvaluation`,
  `ModelCard`, `DriftMonitor`). `Medical.HuggingFace` adapts HF.Net (`TransformerModel.FindEntities` / `Embed` → `NdArray.ToArray()`); entity spans are widened to words.
  Opt-in tests: `MEDNET_HF_TESTS=1` (downloads models), `MEDNET_LLM_TESTS=1` + `ANTHROPIC_API_KEY` (real Claude); the WHO tests download the tables and skip offline.
- `Medical.Bpjs`: VClaim/PCare (`BpjsCrypto`: HMAC signature, AES-256-CBC + `LzString` responses), E-Klaim (`EKlaimCrypto`), and `InaCbgClaimBuilder` plus coding checks. Test vectors come from reference implementations (lz-string 1.5.0 JS, Python).
- Decision support: `FactBuilder` turns a record into facts. The NRules rules live in `Medical.Rules/ClinicalRules.cs` (namespace `Medical.Rules.Definitions`) and are compiled once.
  JSON workflows (Microsoft RulesEngine) are in `Medical.Rules/Workflows/*.json` and evaluate over `ClinicalSnapshot` (`input1.X`). Drug classes come from RxNorm tags in `Medical.Terminology/Data/rxnorm.tsv`.
- Terminology data is embedded TSV (`src/Medical.Terminology/Data`). LOINC codes carry Luhn check digits and SCTIDs carry Verhoeff check digits, and a test validates every embedded code, so keep them valid.
- `Medical.Native` P/Invokes the Rust crate (`mdn_*`) with a custom resolver. `MedicalKernels` has managed twins with identical f64 arithmetic, and a parity test enforces it.
- The AI assistant never computes numbers itself; it calls `ClinicalTools`. Providers: `AnthropicClinicalLlm` (Anthropic SDK, `claude-opus-5-5`, adaptive thinking,
  `fallbacks: "default"`, and the `anthropic-workspace-id` header via `ANTHROPIC_WORKSPACE_ID`), `OpenAICompatibleLlm` (HF/Azure/DeepSeek/Ollama), and `OfflineClinicalLlm`.
  `LlmOptions.FromEnvironment()` chooses among them.
- The gallery's computation lives in `gallery/Medical.Gallery.Shared` (`GalleryCatalog` + `Scenarios`). Blazor (`Components/Demos/*Demo.razor`) and Avalonia (`Views.cs`, code-first `Ui.cs`)
  render the same results. SVG from `Medical.Visualization` uses `var(--token, #fallback)`, and `SvgTheme.Resolve` swaps in literal colours for Svg.Skia.

- v2.0 platform (docs: `docs/en/platform.md`):
  - `Medical.Hospital`: `HospitalDbContext` is shared-schema multi-tenancy. Every `TenantEntity` gets a `TenantId` query filter, and `SaveChanges` stamps new rows and throws
    `TenantViolationException` on cross-tenant writes; `NextAsync(key)` is the per-tenant counter (MRN, queues, accession, invoices). Services are plain classes over a
    context (`RegistrationService`, `ClinicalService`, `PharmacyService`, `LabService`, `RadiologyService`, `BillingService`, `DashboardService`), with no DI extension.
    `ITenantContext` comes from the caller (`FixedTenantContext`, `HisSession`). `Monitoring.cs` has `MonitoringEngine` (plausibility, median, NEWS2, RCP levels, 2-evaluation
    confirmation, 3-evaluation clear, `ChartDue` every 15 min, no alerts for unbound devices). `RoundsSync.cs` is the outbox (`SyncLog`) plus `HospitalSyncStore`.
    `HospitalSeeder` creates the schema with `EnsureCreated` (no migrations) and two fictional tenants, `rs-nusantara` and `rs-bahari`.
  - `Medical.Sync`: `HybridLogicalClock`, `ConflictPolicy` (AppendOnly / LastWriterWins / ServerWins / ReadOnly) and `SyncServer`. Pull cursors are the server `Seq`, never the HLC,
    so late offline pushes are not skipped. The package also holds `SqliteSyncStore` / `SyncClient` and the ward-round contracts in `Rounds.cs` (`RoundsTypes.Policies`).
  - `Medical.Devices` is a preview (`<VersionSuffix>preview</VersionSuffix>`) because IoTCom.Net 0.22.0-preview.1 is prerelease. `pack.ps1` and CI pass `-p:VersionPrefix`
    (not `Version`), so the suffix survives. Keep the `System.Formats.Cbor` 10.0.15 pin (CVE-2026-118747; IoTCom brings 10.0.12).
  - `apps/Medical.His` (Blazor Server, not packed):
    - Demo cookie sign-in via `/account/demo`, to be replaced by OpenID Connect. Config: `His:Provider=SqlServer` + `ConnectionStrings:His`, `His:DeviceKeys:{tenant}`,
      `His:SimulateDevices`, `His:MllpPort`, `His:TickSeconds`.
    - The sync API is `/api/{tenant}/sync/push|pull?ward=`. `MonitoringHub` simulates monitors at occupied ICU beds, and `HisSession.Can` maps roles to modules.
    - Run it in Development (or published): static web assets don't resolve when the `bin` exe runs in Production.
  - `apps/Medical.Rounds(.Core)` (MAUI, not packed):
    - Not in `Medical.Net.slnx` (CI has no MAUI workloads); build with `Medical.Rounds.slnx`. The csproj clears the `TargetFramework` that `Directory.Build.props` sets
      and uses platform `TargetFrameworks`; Android only with `-p:IncludeAndroid=true`.
    - `RoundsApp` (Core) holds all logic so tests run without MAUI. The emulator default server is `http://10.0.2.2:5300`.
  - Tests (`V20*Tests.cs`) need no opt-in: SQLite in memory, an in-process MLLP feed, and `WebApplicationFactory` with `His:SimulateDevices=false`.
    The existing opt-ins remain (`MEDNET_HF_TESTS`, `MEDNET_LLM_TESTS`, `MEDNET_QTDB_SPLIT`).

## Conventions

- Inside `namespace Medical.*`, write Firely types as `global::Hl7.Fhir.Model.X`, because `Medical.Hl7` shadows the `Hl7` root. The HL7 static API is `Hl7V2`.
  Alias name clashes (`Patient`, `Encounter`, `AuditEvent`, `Task`) where both namespaces are imported.
- Synthetic data must stay deterministic: use `SeededRandom` (SplitMix64) with FNV-1a seeds and never `GetHashCode`. Tests pin values.
- Clinical thresholds must cite their source in XML docs. Verify reference values independently (e.g. in Python) before writing test expectations.
- Bilingual: all user-facing text (scores, alerts, gallery, CLI with `--lang id`, docs mirrored in `docs/en` and `docs/id`).
- The credit "Made by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (`MedicalInfo`) and the disclaimer appear in docs and apps.
- Gallery design tokens: paper `#F3F6F7`, ink `#14233A`, ECG rose `#E39C97`, teal `#0F6E6E`, with amber/red only for risk. Fonts: IBM Plex Sans Condensed (display), Atkinson Hyperlegible (body), IBM Plex Mono.
- Versioning uses `VersionPrefix` in `Directory.Build.props`. CI is `.github/workflows/medical-net.yml` at the monorepo root, and a tag `medical-net-vX.Y.Z` publishes with the `NUGET_API_KEY` secret.
- In Git Bash heredocs, a backslash-n escape inside Python string literals can turn into a real newline. Write multi-line patch scripts to a file instead.
- Never commit secrets. Local keys live outside the repo (`..\PackageCredentials.txt`, `testkey.txt`, `HFToken.txt`); pass them through environment variables.
