# Architecture

```text
                         ┌────────────── AquaCulture.Core ──────────────┐
                         │ species · farm/pond/cycle · stats · geo · DI │
                         └──────────────────────────────────────────────┘
   Native (Rust) ─► WaterQuality ─► Growth ─► Feeding ─► Analytics ─► Reporting
         │              │   │                    ▲            ▲
         │              │   └► Disease           │            └─ Visualization
         └► Hydrodynamic ◄── Environment         │
                 │                               │
                 └► Forecasting ─► AI (models, RL, ML.NET/ONNX, AquaAgent)
   IoT ─────────────────────────────┐
   GIS (NTS, ProjNet)               ▼
   StockAssessment · Population   DigitalTwin ─► Blazor components (Three.js 3D)
   ComputerVision (ONNX, OpenCvSharp)
```

- **Core** has no domain logic beyond the species database and the farm model; everything else depends on it.
- **Native** exposes Rust kernels through `LibraryImport` with a resolver that searches the app folder and `runtimes/<rid>/native`;
  `AquaKernels` falls back to managed code with the same formulas (tests check that both agree).
- **WaterQuality → Growth → Feeding → Analytics** is the production chain. Growth uses water-quality modifiers; feeding uses growth
  modifiers; analytics computes KPIs from culture-cycle records.
- **Hydrodynamic** combines environment (wind, solar, rain) with water chemistry for the pond oxygen budget and the 2-D transport model.
- **Forecasting** builds on Growth (Monte-Carlo harvest) and Hydrodynamic (dawn DO).
- **DigitalTwin** fuses IoT readings with everything above and publishes `TwinDecision` events with optional actuator commands.
- **Gallery** (`gallery/AquaCulture.Gallery.Shared`) holds all scenario computation; the Blazor and Avalonia apps only render results.

## Units

°C, mg/L (= g/m³), ppt salinity, mg/L CaCO₃ alkalinity, TAN and nitrite as N, g for individual weight, kg for biomass and feed,
m² / m³ for ponds, days for culture time, IDR/kg for prices (any currency works). DO forecasting uses hours.

## Determinism

All synthetic data (weather, sensors, images, Monte-Carlo) uses `DeterministicRandom` (xoshiro256** seeded by SplitMix64 or FNV-1a),
which is identical across processes and between the Rust kernels and managed code. Never seed with `HashCode`, which is randomised per process.

## Performance

- `Stats` sums with `Vector<T>`; spans everywhere on hot paths.
- The 2-D transport step and Monte-Carlo growth run in Rust (`opt-level = 3`, LTO); an 80 × 64 grid for three simulated hours (13 000 steps) takes about one second.
- Reporting writes OOXML and PDF directly — no reflection, no third-party dependencies, AOT-friendly.
- Libraries are `IsAotCompatible` except AI (ML.NET), ComputerVision (ONNX/OpenCV), GIS (NTS) and Blazor.
