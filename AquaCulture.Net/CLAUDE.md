# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

AquaCulture.Net is "The Ultimate Open Source Aquaculture Platform for .NET". Requirements are in `AquaCulture.Net.txt` (Indonesian) and
`solution-design.md`; the roadmap is in `PLAN.md` and status in `Progress.md` — update `Progress.md` when you finish work.
Its home is the `AquaCulture.Net/` folder of the `DotNetVibeCoderz/Vibe_Components` monorepo (local clone `C:\experiment\VibeCoding\Vibe_Components`).

## Commands (run from this folder)

```bash
dotnet build AquaCulture.Net.slnx
dotnet test tests/AquaCulture.Tests                                               # 102 xUnit tests
dotnet test tests/AquaCulture.Tests --filter "FullyQualifiedName~WaterQualityTests"                 # one class
dotnet test tests/AquaCulture.Tests --filter "FullyQualifiedName~WaterQualityTests.Do_saturation_matches_reference_tables"  # one test
cd native/aqua-native && cargo test --release                                     # Rust kernels
pwsh build/build-native.ps1          # cargo build + copy into src/AquaCulture.Native/runtimes/<rid>/native
pwsh build/pack.ps1                  # all NuGet packages → artifacts/packages (runs tests first; -SkipTests, -SkipNative)
dotnet run --project gallery/AquaCulture.Gallery.Web --urls http://localhost:5390
dotnet run --project gallery/AquaCulture.Gallery.Desktop
dotnet run --project gallery/AquaCulture.Gallery.Desktop -- --screenshot docs/images [en|id]   # headless Avalonia + ThreeNet offscreen
pwsh build/screenshots-web.ps1       # headless Chrome screenshots of the running web gallery (twin page needs a long virtual-time budget)
dotnet run --project tools/AquaCulture.Cli -- help
```

A running gallery server locks DLLs and makes builds fail with MSB3027; stop `AquaCulture.Gallery.Web` first.

## Architecture

- `src/AquaCulture.Core` is the base: `SpeciesCatalog` (10 species with tolerance bands, TGC, VBGF, length–weight, feeding tables), `Farm`/`Pond`/`CultureCycle`,
  `Stats`, `TimeSeries`, `Numerics`, geo types, `DeterministicRandom`, `services.AddAquaCulture(b => b.AddX())` (each module's `AddX` calls `TryAddModule` once), `IAquaEventBus`.
- Layering: Native → WaterQuality → Growth → Feeding → Analytics → Reporting; Environment + WaterQuality → Hydrodynamic → Forecasting → AI;
  IoT + everything → DigitalTwin → Blazor. GIS, StockAssessment, Population, ComputerVision depend only on Core.
- `AquaCulture.Native` P/Invokes the Rust crate `native/aqua-native` (C ABI `aq_*`) via `LibraryImport` with a resolver for the app folder and
  `runtimes/<rid>/native`. `AquaKernels` has a managed fallback with identical formulas; the Rust PRNG is bit-identical to `DeterministicRandom` (tests check parity).
- `GrowthSimulator` drives growth by TGC × modifiers; feed is derived from gain × species FCR worsened by stress (keeps simulated FCR realistic) — do not go back to
  table-only feeding. `PondOxygenBudget` respiration constants are calibrated to literature (shrimp ≈ 600, fish ≈ 300 mg O₂/kg/h at 30 °C).
- `PondTwin` forecasts dawn DO assuming installed aerators run at the species DO optimum; `DoCrashForecaster` damps the evening slope (τ = 5 h).
- `AquaCulture.AI.Agent.AquaAgent` grounds every answer in tool calls (`AquaTools`). `LlmOptions.FromEnvironment()` reads `AQUA_LLM_PROVIDER`
  (azure|openai|anthropic|deepseek|hf|ollama|custom), `AQUA_LLM_ENDPOINT`, `AQUA_LLM_API_KEY`, `AQUA_LLM_MODEL`, `AQUA_LLM_WORKSPACE` (Anthropic org keys);
  without them `RuleBasedChatModel` routes by bilingual keywords offline.
- The gallery's computation lives in `gallery/AquaCulture.Gallery.Shared` (`GalleryCatalog`, `DemoFarm`, `*Demo.Run`); the Blazor views
  (`Components/Demos/*View.razor`) and the Avalonia views (`Views.cs`, code-first kit in `Ui.cs`, ThreeNet scene in `Twin3D.cs`) only render results.
  `GalleryScenarioTests` runs every scenario. SVG uses `var(--aq-*, #fallback)`; `SvgTheme.Resolve` swaps in literals for Svg.Skia.
- The Blazor 3D pond is `src/AquaCulture.Blazor/wwwroot/aquaTwin.js` with a vendored Three.js r170 (`wwwroot/three/`).

## Conventions

- Libraries stay dependency-light and AOT-compatible (`IsAotCompatible`), except AI (ML.NET), ComputerVision (ONNX/OpenCvSharp), GIS (NTS) and Blazor.
  Reporting writes OOXML/PDF by hand — keep it free of third-party packages. Use `JsonNode`/`JsonDocument`, not reflection serialization.
- The namespace `AquaCulture.Environment` shadows `System.Environment` inside `AquaCulture.*` — write `System.Environment` explicitly.
- Synthetic data must be deterministic (`DeterministicRandom`, never `HashCode`). Format numbers with `CultureInfo.InvariantCulture`.
- Units: °C, mg/L, ppt, TAN/nitrite as N, g per animal, kg biomass/feed, m²/m³, days, IDR/kg.
- Bilingual: user-facing text is `Text2(En, Id)`; docs are mirrored in `docs/en` and `docs/id`.
- Credit "Made by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (`AquaInfo`) must appear in docs and apps.
- Versioning uses `VersionPrefix` in `Directory.Build.props`; CI adds `VersionSuffix=ci.N`; a tag `aquaculture-net-vX.Y.Z` publishes to NuGet via
  `.github/workflows/aquaculture-net.yml` (monorepo root) with the `NUGET_API_KEY` secret.
- In this environment bash heredocs that contain single quotes fail; write files with the Write tool or a Python script file instead.
- Never commit secrets. Local keys live outside the repo: `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt` (NuGet), `testkey.txt` (LLM keys), `HFToken.txt`.

## Supporting libraries

For future work prefer the author's libraries: GravicodeScience, Gravicode.RLNet, Visual.Net, ThreeNet (desktop 3D, already used), HF.Net, MediaPipeNet,
IoTComNet (DotNetVibeCoderz org), GDAL.Net from the monorepo, plus TorchSharp, OpenCvSharp, ML.NET. MCP servers `rodin` and `blender` (`.mcp.json`) generate 2D/3D assets.
