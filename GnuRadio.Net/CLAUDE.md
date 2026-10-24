# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

GnuRadio.Net: a .NET 10 SDR/DSP framework modelled on GNU Radio (independent managed implementation, not a binding to
GNU Radio's C++). The brief is `GnuRadio.Net.txt` (Bahasa Indonesia) and the feature list `features.md`; `PLAN.md` is the
roadmap and `Progress.md` the development log — update both when finishing a feature.

Non-negotiables from the brief:
- Docs in **English and Bahasa Indonesia** (`docs/en`, `docs/id`, `README.md`, `README.id.md`); keep them in sync.
- Attribution "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" in docs and apps (also in assembly metadata via `Directory.Build.props`).
- Gallery UI work uses the project-local `frontend-design` skill; the visual language is the "instrument chassis"
  (light anodised panels, dark screens, amber/cyan traces, Chakra Petch / Instrument Sans / IBM Plex Mono), tokens in
  `src/GnuRadio.Net.Blazor/wwwroot/gnuradio.css` and `src/GnuRadio.Net.Avalonia/GrTheme.cs`.
- This folder lives in the `Vibe_Components` monorepo. CI is `../.github/workflows/gnuradio-net.yml`: it builds the Rust
  kernels for win-x64/linux-x64/osx-arm64, then builds, tests and packs on every change under `GnuRadio.Net/`, and publishes
  to NuGet (repository secret `NUGET_API_KEY`) when a tag `gnuradio-net-vX.Y.Z` is pushed or the workflow is started
  manually with "publish". Publishing is public and permanent: only tag or dispatch when the user asks. Never commit
  credentials files.

## Commands

```bash
dotnet build GnuRadio.Net.slnx
dotnet test tests/GnuRadio.Net.Tests                                   # all tests (~30 s; demos run real-time)
dotnet test tests/GnuRadio.Net.Tests --filter "FullyQualifiedName~FecTests"            # one class
dotnet test tests/GnuRadio.Net.Tests --filter "FullyQualifiedName~OfdmTimingStaysExact" # one test
dotnet run -c Release --project tests/GnuRadio.Net.Benchmarks -- --filter *KernelBenchmarks* --job short
pwsh native/build.ps1                     # Rust kernels → runtimes/<rid>/native (optional; needs cargo)
cd native/gnuradio-native && cargo test --release
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/GnuRadio.Net.Gallery.Web --urls http://localhost:5180
dotnet run --project samples/GnuRadio.Net.Gallery.Desktop [-- --screenshots docs/images]   # headless PNGs
pwsh scripts/pack.ps1                     # local tests + pack into artifacts/packages (no publishing)
```

The web gallery must run in Development (or be published) for static assets of referenced projects to be served.
`dotnet run file.cs` with `#:project <csproj>` is handy for quick experiments against the libraries.

## Architecture (big picture)

- `src/GnuRadio.Net/Runtime`: `Block` (ports declared with `AddInput<T>/AddOutput<T>`, `GeneralWork(WorkContext)`),
  bases in `BlockBases.cs`, `StreamBuffer<T>` (ring with a **mirror region** so spans are always contiguous —
  `MinimumInputItems`/`OutputMultiple` must stay below half the buffer), `Scheduler` (thread-per-block; a block is done
  when inputs are drained-and-finished, all readers detached, or work returns `Done`), `Flowgraph` (connect, flatten
  `HierBlock`s, validate, start/stop). Tags propagate by `RelativeRate`. Messages are `Pmt`; handlers run on the
  receiving block's thread.
- Module folders mirror GNU Radio modules: `Blocks`, `Filter`, `Fft`, `Analog`, `Digital`, `Fec`, `Synchronization`,
  `Channels`, `Network`, `Sinks` (UI-agnostic visual sinks with version counters), `Extensions` (LoRa, radar,
  satellites, inspector, ML), `Kernels` (`Volk` SIMD; `NativeKernels` P/Invoke into Rust, routed only for FIR/FFT).
- Conventions: items are `unmanaged` (`Complex32` = GNU Radio `gr_complex`); block `Name`s are GNU Radio ids
  (`fir_filter_ccf`); soft bits are LLRs with **positive = bit 0**; constellation symbol bits are MSB first; 8PSK is
  rotated π/8 (GNU Radio orientation).
- Other packages: `GnuRadio.Net.ZeroMQ` (NetMQ, gr-zeromq wire format), `.Hardware` (SoapySDR, rtl_tcp,
  `SimulatedSdrDevice`), `.Grc` (`GrcDocument` → `GrcImporter` using `GrcBlockLibrary` mappings, `GrcExpression` Python
  subset, `GrcCodeGenerator`), `.Blazor` (Razor components + `wwwroot/gnuradio.js` canvas renderer; data as float bytes),
  `.Avalonia` (code-only controls on `GrDisplay`).
- Gallery: demos are UI-agnostic in `samples/GnuRadio.Net.Gallery.Shared` (`Demo` subclasses declare parameters,
  `DemoView` records and readouts; `DemoCatalog` lists them). The Web (`Components/Panels/DemoViewHost.razor`) and
  Desktop (`MainWindow.cs`) apps map views to components. New demos only need a class + catalog entry;
  `GalleryDemoTests` starts every demo headlessly.

## Gotchas learned

- Block field initializers run before `Block`'s constructor: declare ports in the constructor, not in initializers.
- Sources that emit idle data (framers) fill buffers seconds ahead — give such flowgraphs small `BufferBytes`.
- Receivers that search for frames must not discard part of a sync preamble when a frame is incomplete (see OFDM fix).
- `Volk.UseNative` is process-wide; tests touching it are in a non-parallel collection.
