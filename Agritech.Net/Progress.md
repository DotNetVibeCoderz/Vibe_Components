# Agritech.Net: progress tracking

Status legend: ✅ done · 🟡 partial · ⏳ planned

| Area | Status | Notes |
|---|---|---|
| Solution scaffold (.NET 10, slnx, shared props) | ✅ | 26 projects, 0 warnings |
| Agritech.Core | ✅ | Geo, data, units, farm model, DI, event bus |
| Agritech.Crops | ✅ | 16 crops, phenology, yield, FAO suitability |
| Agritech.Soil | ✅ | Texture, Saxton–Rawls, health index, fertilizer, ledger |
| Agritech.Weather | ✅ | FAO-56 ET0, Open-Meteo (live-tested), synthetic generator, SSP |
| Agritech.GIS | ✅ | UTM, GeoJSON, geohash, quadtree, geometry |
| Agritech.RemoteSensing | ✅ | Indices, zonal, zones, phenometrics, STAC search; COG reading ⏳ v1.1 |
| Agritech.Drones | ✅ | Missions + QGC export, weed detection, lesions, orthomosaic (direct georef.) |
| Agritech.IoT | ✅ | MQTT (MQTTnet 5), Cayenne LPP, TTN, edge analytics |
| Agritech.AI | ✅ | Forecaster, disease/pest, irrigation, fertilizer, LLM agent (live-tested on Azure OpenAI gpt-5-mini) |
| Agritech.Simulation | ✅ | Daily crop model + Monte-Carlo scenarios |
| Agritech.Genomics | ✅ | Sequences, alignment, SNP QC, GRM, rrBLUP, pedigree, crosses |
| Agritech.Livestock | ✅ | THI, vitals, reproduction calendar |
| Agritech.Fisheries | ✅ | Water quality, TGC growth, feeding |
| Agritech.Robotics | ✅ | A*, coverage, pure pursuit, harvest TSP, greenhouse robot |
| Agritech.SupplyChain | ✅ | Hash-chained traceability, recall, GS1, shelf life, FEFO |
| Agritech.Marketplace | ✅ | Order book with transport cost, price intelligence |
| Agritech.DigitalTwin | ✅ | Live twin with explained decisions |
| Agritech.Visualization (+ Blazor) | ✅ | Color ramps, PNG, SVG charts, isometric farm, components |
| Agritech.Native (Rust) | ✅ | 6 Rust tests; CI builds 5 RIDs |
| Tests | ✅ | 86 xUnit tests passing (FAO-56 examples, known vectors, determinism) |
| Gallery: Blazor | ✅ | 13 demos, EN/ID, light/dark, screenshots in docs/images |
| Gallery: Avalonia | ✅ | 13 demos, headless screenshot mode |
| CLI (`agritech`) | ✅ | 11 commands |
| Templates | ✅ | 4 templates, all verified to build from the packed feed |
| NuGet icon & packages | ✅ | 22 packages + symbols; icon generated with Rodin (Nano Banana 2) |
| Docs EN / ID | ✅ | 9 guides each + README / README.id |
| Monorepo move (Vibe_Components/Agritech.Net) + CI workflow | ✅ | `.github/workflows/agritech-net.yml`, tag `agritech-net-v*` publishes with `NUGET_API_KEY` |

## Log
- **2026-11-05**: v1.0.0 implemented end-to-end. Fixed during verification: static-init order bug in `CropCatalog` (stages null), trailing-MA phase lag in phenometrics,
  process-randomised `HashCode` seeding in the weather generator (now pinned by a test), twin lifecycles not caught up to the current date, doubled `runtimes` path in the native package.
