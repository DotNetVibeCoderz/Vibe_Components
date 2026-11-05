# Architecture

```
                        Agritech.Core  (geo, data, units, farm model, DI, events)
        ┌───────────┬──────────┬────────┴──────┬──────────┬───────────┬──────────┐
     Crops        Soil     Weather            GIS        IoT     Genomics   Livestock/Fisheries/
        │           │          │               │          │                  SupplyChain/Marketplace
        └─────┬─────┴────┬─────┘        ┌──────┴──────┐   │
              AI    Simulation       Drones      Robotics │
              │                                           │
              └──────────────── DigitalTwin ──────────────┘

   Native (Rust C ABI + SIMD fallback) ◄── RemoteSensing        Visualization ◄── Visualization.Blazor
```

## Design rules

- **Dependency-light core.** Libraries only depend on `Microsoft.Extensions.DependencyInjection.Abstractions`, with MQTTnet in IoT and the ASP.NET Components package in Visualization.Blazor.
  Linear algebra, statistics, PNG and SVG rendering, GeoJSON and projections are implemented in-house.
- **Performance and memory.** Spans and `Vector<T>` are used in hot loops, `stackalloc` for small buffers, frozen dictionaries for catalogs, the Rust kernels run multi-threaded,
  and `Parallel.For` handles scenario and mosaic workloads. Libraries are marked `IsAotCompatible`, use JSON via `JsonNode`/`JsonDocument` with no reflection serialization, and use `LibraryImport`.
- **Deterministic synthetic data.** All demo generators are seeded and stable across processes, so tests and screenshots are reproducible.
- **Science-traceable.** Each model names its source (FAO-56, FAO-33, Saxton–Rawls, Magarey, Emerson, NRC THI, VanRaden, Coulter…), and the tests check published examples.
- **Bilingual by design.** Crop and pathogen names, agent tools, gallery and docs are available in English and Bahasa Indonesia.

## Repository

| Path | Contents |
|---|---|
| `src/` | Library packages |
| `native/agritech-native` | Rust crate |
| `tools/Agritech.Cli` | dotnet tool |
| `templates/Agritech.Templates` | dotnet new pack |
| `gallery/` | Shared scenarios + Blazor + Avalonia |
| `tests/Agritech.Tests` | xUnit suite (~90 tests) |
| `build/` | `build-native.ps1`, `pack.ps1`, `publish-nuget.ps1` |

## Extending with supporting libraries

Agritech.Net deliberately keeps its own core light, but it combines well with:
`Gravicode.Science.*` (GravicodeScience: NumPy/Pandas/scikit-learn style), `Gravicode.RLNet` (reinforcement learning for irrigation/fertigation policies),
`ThreeNet` (3D farm digital twin), HF.Net (Hugging Face models), MediaPipeNet, IoTComNet, Biotech.Net, TorchSharp, OpenCvSharp and ML.NET.
Implement `IPlantDiseaseClassifier`, `IWeatherProvider`, `ISensorTransport` or `IChatModel` to plug them in.
