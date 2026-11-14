# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Electrical.Net is a "Comprehensive Electrical Engineering & Power System Framework for .NET". It will live as a subfolder of the
`DotNetVibeCoderz/Vibe_Components` monorepo (`C:\experiment\VibeCoding\Vibe_Components\Electrical.Net`), following the same layout as its
sibling `Agritech.Net`. Requirements are in `Electrical.Net.txt` (Indonesian) and `solution-design.md`. The roadmap is in `PLAN.md` and status in
`Progress.md`; update `Progress.md` when you finish work.

## Commands (run from this folder)

```bash
dotnet build Electrical.Net.slnx
dotnet test tests/Electrical.Tests                                              # all tests (xUnit)
dotnet test tests/Electrical.Tests --filter "FullyQualifiedName~PowerFlowTests"            # one class
dotnet test tests/Electrical.Tests --filter "FullyQualifiedName~PowerFlowTests.Case14_MatchesMatpower"  # one test
cd native/electrical-native && cargo test --release                             # Rust kernels
pwsh build/build-native.ps1        # cargo build + copy into src/Electrical.Native/runtimes/<rid>/native
pwsh build/pack.ps1                # all NuGet packages → artifacts/packages
dotnet run --project gallery/Electrical.Gallery.Web --urls http://localhost:5290
dotnet run --project gallery/Electrical.Gallery.Desktop -- --screenshot docs/images [en|id]
dotnet run --project tools/Electrical.Cli -- help
```

A running gallery server locks DLLs and makes `dotnet build` fail with MSB3027, so stop it first.

## Architecture

- `src/Electrical.Core` is the base for everything:
  - `Numerics`: generic (`double`/`Complex`) `DenseMatrix<T>`/`DenseLU<T>`, CSR `SparseMatrix<T>` built with `SparseBuilder<T>`, and
    `SparseLU<T>` (minimum-degree ordering + IKJ elimination; pass `lu.Ordering` back to `Factorize` to reuse the ordering).
  - `Network`: the bus-branch model `PowerNetwork` (`Bus`, `Load` with `ZipModel`, `Generator` incl. machine data and `PolynomialCost`, `Branch` π-model
    in pu on the system base, MATPOWER conventions: non-zero `Tap` = transformer, `B` = total charging). `NetworkBuilder` takes physical units
    (kV, Ω, km, nameplate MVA/uk%) and `LineLibrary` has standard conductor types. `AdmittanceMatrix.Build(net, Sequence)` gives Y-bus and per-branch
    admittances (zero-sequence handles transformer winding connections). `Topology` (islands, radial check), `Matpower` (parse/write), `TestCases`
    (`Case9` Anderson–Fouad point, `Case14`, `Case33BW`, deterministic `SyntheticGrid(n)`).
  - `Units`: `PerUnitBase`, `ElectricalFormulas`, `Phasor`, `ThreePhase`, `SymmetricalComponents`.
  - `Data`: `TimeSeries` (regular step, SIMD via `TensorPrimitives`), `Stats`.
  - `Hosting`: `services.AddElectrical(b => b.AddX())`; each module adds `AddX(this ElectricalBuilder)` that calls `TryAddModule` once. `IGridEventBus`.
- `src/Electrical.PowerFlow`: `PowerFlowSolver.Solve(net, options)` (sparse Newton–Raphson with Q limits and ZIP loads, Gauss–Seidel, fast-decoupled XB,
  DC, backward/forward sweep). Results in MW/Mvar/pu/degrees (`PowerFlowResult`, `ApplyTo` for warm starts). `DcPowerFlow.Ptdf/Lodf`, `DcModel`,
  `ContingencyAnalysis.RunN1` (parallel).
- Module dependencies are declared in each `src/Electrical.*/*.csproj`; keep them acyclic (Core → PowerFlow → analysis modules → SmartGrid/Simulation →
  DigitalTwin/AI/Cloud).
- `Electrical.Distribution` (v1.1) has its own phase-frame model `DistributionNetwork` (buses with phase sets, line codes in Ω/km with 3×3/2×2/1×1
  matrices, three-phase transformers, regulators, wye/delta loads) solved by `UnbalancedPowerFlow` (Kersting ladder sweep for radial feeders,
  current injection for meshed). `LineConstants` implements modified Carson + Kron. `OpenDssReader` maps a .dss subset; `ToBalanced()` reduces
  to a Core `PowerNetwork`. Validation references: IEEE 4/13-node published tables and Kersting examples (cited in `tests/.../Distribution`).
- `Electrical.Interop` (v1.1) reads/writes PSS/E RAW v33/v35 and reads IEEE CDF; `NetworkFormats.Load/Detect` auto-detects MATPOWER, RAW, CDF and
  Cloud JSON. Balanced QSTS controllers (`OltcController`, `VoltageRegulatorController`, `CapacitorController`) live in `Electrical.Simulation`.
- Newton–Raphson flat start takes its initial angles from a DC power flow (`PowerFlowCase.DcStartAngles`) — keep it; pure flat starts diverge
  or find spurious solutions with large phase shifters.
- `Electrical.DigitalTwin.GridTwin` ingests SCADA tags (`RtuSimulator` naming `BUSn.VM`, `BRk.PF`, …), runs WLS state estimation with bad-data removal
  each cycle and produces explained alarms (EN `Message` / ID `MessageId`). `TwinSimulator` is a deterministic closed loop used by tests, CLI and gallery.
- `Electrical.AI.GridAgent` answers through tool calling (`GridTools`, 10 tools). `LlmOptions.FromEnvironment()` reads `ELECTRICAL_LLM_PROVIDER`
  (openai|azure|hf|ollama|custom), `_ENDPOINT`, `_API_KEY`, `_MODEL`; without them `RuleBasedChatModel` routes EN/ID keywords offline.
  The live LLM test runs only with `ELECTRICAL_LIVE_LLM=1`.
- `Electrical.Cloud` exposes `MapElectricalApi()` (minimal API with the Request Delegate Generator for AOT) over a `StudyRunner` whose study kinds are
  registered in `StudyCatalog` (partial class; `ShortCircuitStudies.cs` adds `shortcircuit`).
- The CLI (`tools/Electrical.Cli`, command `eln`) is split into partial classes: `Program.cs` (core commands), `Analysis.cs`, `Operations.cs`,
  `Intelligence.cs`, each adding a dispatch hook and help text.
- Gallery: computations live in `gallery/Electrical.Gallery.Shared`. Most demos are declarative (`IInteractiveScenario` + `InteractiveScenarios.All` +
  a `GalleryCatalog.Demos` entry) and are rendered by both the Blazor (`InteractiveView.razor`) and Avalonia (`Views.Interactive`) apps.
  Avalonia shows SVG through Svg.Skia after `SvgTheme.Resolve` (CSS variables → literal colors).
- `Electrical.Native` P/Invokes the Rust crate `native/electrical-native` (C ABI `eln_*`) through `LibraryImport`, with a resolver that searches the app
  folder and `runtimes/<rid>/native`. Every native kernel has a managed fallback with identical semantics.

## Conventions

- Namespaces match project names (`Electrical.ShortCircuit`, …). Libraries are dependency-light and AOT-compatible (`IsAotCompatible`): no reflection-based
  JSON (use `JsonNode`/`JsonDocument` or `Utf8JsonWriter`), no `dynamic`.
- Units at the API surface: MW, Mvar, MVA, kV, kA, Ω, pu on the network `BaseMva`, angles in degrees (radians internally), time in seconds, frequency in Hz.
  Document units in XML doc comments.
- Validate algorithms against published reference values (MATPOWER, IEC 60909-4 examples, IEEE 1584, textbook examples) and cite them in tests.
- Synthetic data must be deterministic across processes: seed `Random` with constants, never `HashCode.Combine`.
- Bilingual: user-facing text (gallery, agent tools, CLI help, docs) in English and Bahasa Indonesia; docs are mirrored in `docs/en` and `docs/id`.
- Credit "Made by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (`ElectricalInfo`) must appear
  in docs and apps.
- Versioning uses `VersionPrefix` in `Directory.Build.props`. A tag `electrical-net-vX.Y.Z` in the monorepo publishes to NuGet through
  `.github/workflows/electrical-net.yml` with the `NUGET_API_KEY` secret.
- Never commit secrets. Local keys live outside the repo (`..\PackageCredentials.txt`, `..\testkey.txt`, `..\HFToken.txt`); read them at run time only.

## Supporting libraries

Use these instead of reimplementing when a feature needs them: GravicodeScience, `Gravicode.RLNet`, Visual.Net, ThreeNet, HF.Net, MediaPipeNet,
IoTComNet (DotNetVibeCoderz org), GDAL.Net from the monorepo, TorchSharp, OpenCvSharp, ML.NET.
