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
# web shots: chrome --headless=new --force-prefers-reduced-motion --window-size=1440,1000 --screenshot=<file> http://localhost:5290/demo/<slug>
dotnet run --project tools/Medical.Cli -- help
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
