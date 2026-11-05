# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Agritech.Net is "The Ultimate Open-Source Agriculture Technology Framework for .NET". It lives as a subfolder of the `DotNetVibeCoderz/Vibe_Components` monorepo.
Requirements are in `Agritech.Net.txt` (Indonesian) and `solution-design.md`. The roadmap is in `PLAN.md` and status in `Progress.md`; update `Progress.md` when you finish work.

## Commands (run from this folder)

```bash
dotnet build Agritech.Net.slnx
dotnet test tests/Agritech.Tests                                   # all tests (xUnit, VSTest)
dotnet test tests/Agritech.Tests --filter "FullyQualifiedName~SoilTests"            # one class
dotnet test tests/Agritech.Tests --filter "FullyQualifiedName~SoilTests.UsdaTexture" # one test
cd native/agritech-native && cargo test --release                  # Rust kernels
pwsh build/build-native.ps1        # cargo build + copy into src/Agritech.Native/runtimes/<rid>/native
pwsh build/pack.ps1                # all NuGet packages → artifacts/packages
dotnet run --project gallery/Agritech.Gallery.Web --urls http://localhost:5280
dotnet run --project gallery/Agritech.Gallery.Desktop -- --screenshot docs/images [en|id]   # headless Avalonia screenshots
dotnet run --project tools/Agritech.Cli -- help
```

A running gallery server locks DLLs and makes `dotnet build` fail with MSB3027, so stop it first.
Web screenshots are taken with headless Chrome (`--screenshot`, `--blink-settings=preferredColorScheme=1` for light mode) into `docs/images`.

## Architecture

- `src/Agritech.Core` is the base for everything: geo types (`GeoCoordinate`, `GeoPolygon`, `GeoMath`), `Stats`/`Matrix`/`TimeSeries`, `Farm`/`Field`, `IAgriEventBus`,
  and `services.AddAgritech(b => b.AddX())`. Each module adds an `AddX(this AgritechBuilder)` extension that calls `TryAddModule` once.
- Dependency layering: Crops/Soil/Weather → AI and Simulation; GIS → Drones and Robotics; Native → RemoteSensing; IoT + AI + agronomy → DigitalTwin.
  Visualization is UI-agnostic (PNG encoder, SVG charts, isometric farm); Visualization.Blazor wraps it in components.
- `Agritech.Native` P/Invokes the Rust crate `native/agritech-native` (C ABI `agt_*`) through `LibraryImport`, with a custom resolver that searches the app folder and
  `runtimes/<rid>/native`. `AgriKernels` falls back to managed `Vector<T>` code with identical semantics, and division by zero gives NaN (no-data).
- `Agritech.AI.Agent.FarmAgent` sends the LLM through tool calling (`AgronomyTools`) so answers stay grounded. `LlmOptions.FromEnvironment()` reads `AGRITECH_LLM_PROVIDER`,
  `AGRITECH_LLM_ENDPOINT`, `AGRITECH_LLM_API_KEY` and `AGRITECH_LLM_MODEL`. Without them, `RuleBasedChatModel` routes by bilingual keywords (offline).
- The gallery's computation lives in `gallery/Agritech.Gallery.Shared` (`GalleryCatalog` + `Scenarios.cs`). Both the Blazor (`Components/Demos/*View.razor`) and Avalonia
  (`Views.cs`, code-first UI in `Ui.cs`) apps render the same scenario results. SVG made for the web uses CSS variables. `SvgTheme.Resolve` swaps in literal colors
  for Avalonia's Svg.Skia.

## Conventions

- Libraries must stay dependency-light and AOT-compatible (`IsAotCompatible`). Use `JsonNode`/`JsonDocument` instead of reflection serialization, and cast to `JsonNode` when adding to a `JsonArray`.
- Synthetic data generators must be deterministic across processes. Never seed with `HashCode.Combine`, which is randomized per process; a test pins the output.
- Units: mm water, kg/ha nutrients as N/P₂O₅/K₂O, t/ha yield, °C, WGS-84 degrees; rasters are `float` 0–1 with NaN as no-data.
- Bilingual: user-facing text (gallery, agent tools, docs) in English and Bahasa Indonesia; docs are mirrored in `docs/en` and `docs/id`.
- Credit "Made by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (`AgritechInfo`) must appear in docs and apps.
- Versioning uses `VersionPrefix` in `Directory.Build.props`. CI adds `VersionSuffix=ci.N`, and a tag `agritech-net-vX.Y.Z` publishes to NuGet through
  `.github/workflows/agritech-net.yml` (monorepo root) with the `NUGET_API_KEY` secret.
- Never commit secrets. Local keys live outside the repo (`..\PackageCredentials.txt`, `testkey.txt`, `HFToken.txt`).

## Supporting libraries

Use these for future work instead of reimplementing: GravicodeScience (`Gravicode.Science.*`), `Gravicode.RLNet`, `ThreeNet`, HF.Net, MediaPipeNet, IoTComNet,
Biotech.Net (DotNetVibeCoderz org), TorchSharp, OpenCvSharp, ML.NET, and GDAL.Net / OpenSceneGraph.Net from this monorepo.
