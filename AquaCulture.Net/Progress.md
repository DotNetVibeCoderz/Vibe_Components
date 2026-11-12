# AquaCulture.Net: progress tracking

Status legend: ✅ done · 🟡 partial · ⏳ planned

| Area | Status | Notes |
|---|---|---|
| Solution scaffold (.NET 10, slnx, shared props) | ✅ | 30 projects, Release build with 0 warnings |
| AquaCulture.Core | ✅ | 10 species, farm/pond/cycle model, SIMD stats, numerics, geo, deterministic RNG, DI, event bus |
| AquaCulture.WaterQuality | ✅ | 15 parameters, NH₃, DO saturation (verified against APHA tables), WQI + CCME, alerts with hysteresis and DO trend, dosed recommendations |
| AquaCulture.Growth | ✅ | TGC / VBGF / Gompertz, environmental modifiers, mortality, feed calibrated to species FCR, harvest optimum |
| AquaCulture.Feeding | ✅ | FCR, plans, schedules, least-cost feed, Pearson square, auto-feeder |
| AquaCulture.Disease | ✅ | 15 diseases, Bayesian diagnosis, environmental risk |
| AquaCulture.StockAssessment | ✅ | Schaefer/Fox, dynamic fit + Kobe, B-H/Ricker, YPR, length-based methods; ELEFAN ⏳ |
| AquaCulture.Population | ✅ | Age-structured model, HCRs, predation, Leslie, predator–prey |
| AquaCulture.Environment | ✅ | Open-Meteo, BMKG, NASA POWER, Open-Meteo Marine (all live-tested, parsers tested on fixtures), synthetic weather, impact predictor, climate risk |
| AquaCulture.Hydrodynamic | ✅ | 2-D transport on Rust kernel, flows, sediment/plume, oxygen budget, RAS mass balance |
| AquaCulture.IoT | ✅ | MQTT, REST, Modbus RTU/TCP + RTU serial client, LoRaWAN (LPP, TTN, ChirpStack v4), OPC-UA mapping, edge analytics, simulator; ESP32 firmware sample |
| AquaCulture.GIS | ✅ | GeoJSON, pond layout, IDW, D8 drainage, water sources, NTS + ProjNet |
| AquaCulture.GIS.Gdal (v1.1) | ✅ | GeoTIFF I/O via GDAL.Net 3.13 with WGS-84 warp; NDWI, MNDWI, NDCI chlorophyll-a, turbidity, trophic state |
| AquaCulture.Data (v1.1) | ✅ | EF Core 10 / SQLite store (farms, cycles + logs, readings, decisions), recording transport, Parquet (ParquetSharp) / CSV export |
| AquaCulture.IoT.OpcUa (v1.1) | ✅ | OPC UA reader/writer (OPC Foundation 1.5.378) and pond simulator; integration-tested client ↔ server |
| AquaCulture.IoT.IoTCom (v1.1, preview) | ✅ | IoTCom.Net 0.22 preview: Modbus transport (in-memory server test), SenML JSON/CBOR, NMEA MTW/DPT/DBT + drift, AIS zone watch, Sparkplug B (tested against a local broker and host); v1.2: LoRaWAN network server, LwM2M gateway, M-Bus energy (all tested against IoTCom.Net simulators) |
| AquaCulture.AI | ✅ | GBT growth, logistic mortality, disease/harvest outlook, Q-learning feed policy, ML.NET, ONNX, AquaAgent (live-tested: Azure OpenAI gpt-5-mini, Anthropic claude-sonnet-5-5) |
| AquaCulture.ComputerVision | 🟡 | YOLO/ONNX decode + NMS, model-free counting/sizing/screening, OpenCvSharp interop; trained disease models ⏳ v1.2 |
| AquaCulture.Forecasting | ✅ | Holt, Holt–Winters, AR, auto; dawn-DO forecast; Monte-Carlo harvest |
| AquaCulture.DigitalTwin | ✅ | Pond/farm twins, explained decisions, actuator commands, what-if |
| AquaCulture.Analytics | ✅ | Cycle KPIs, cost breakdown, benchmarks, farm summary |
| AquaCulture.Reporting | ✅ | XLSX/PDF/DOCX/CSV/JSON/HTML/MD (no third-party libs), GAP/CBIB/ASC/BAP |
| AquaCulture.Visualization / Blazor | ✅ | SVG/PNG charts; 6 domain components incl. Three.js 3D pond |
| AquaCulture.Native (Rust) | ✅ | 7 Rust tests; bit-identical managed fallback; CI builds 5 RIDs |
| Tests | ✅ | 124 xUnit tests (reference values, native/managed parity, determinism, file-format validity, every gallery scenario) |
| Gallery: Blazor | ✅ | 17 demos, EN/ID, light/dark, screenshots in docs/images |
| Gallery: Avalonia | ✅ | 17 demos, ThreeNet 3D pond (interactive + offscreen), headless screenshot mode |
| CLI (`aqua`) | ✅ | 21 commands (v1.1: bmkg, history, marine, export, opcua) |
| Templates | ✅ | 4 templates, verified to build from the packed feed |
| NuGet icon & packages | ✅ | 26 packages (v1.1, IoT.IoTCom as preview) + symbols; icon generated with Rodin (Nano Banana 2) |
| Docs EN / ID | ✅ | 10 guides each (v1.1: data and connectors) + README / README.id |
| Monorepo move (Vibe_Components/AquaCulture.Net) + CI workflow | ✅ | `.github/workflows/aquaculture-net.yml`, tag `aquaculture-net-v*` publishes with `NUGET_API_KEY` |
| NuGet publish | ✅ | 1.0.0: all 22 packages on nuget.org · 1.1.0: 25 packages + IoT.IoTCom 1.1.0-preview.1 pushed by tag `aquaculture-net-v1.1.0` (CI run 43148528947) |

## Log
- **2026-11-12**: v1.0.0 implemented end-to-end. Fixed during verification: edge Hampel filter locking out after a level shift (all values now enter the window),
  DO-trend alert using end points instead of a regression, dawn-DO forecast extrapolating the dusk slope linearly (now damped) and assuming aerators off in the twin,
  shrimp respiration about 2× literature, simulated FCR too low (feed now derived from gain × species FCR), Monte-Carlo harvest spread too narrow (per-run TGC),
  doubled `runtimes` path in the native package, 3-D pond water hidden under the ground plane.
- **2026-11-12**: web-twin / web-twin-dark screenshots showed an empty 3D pond: the screenshot script passed `--disable-gpu`, which disables WebGL in headless Chrome.
  Removed the flag and shortened the virtual-time budget to 12 s (longer budgets let the Blazor circuit drop and the reconnect overlay grey the page).
  `DigitalTwinViewer` now shows a "needs WebGL" message instead of an empty box when the 3D context cannot be created.
- **2026-11-12**: v1.1 "Data and connectors" implemented: `AquaCulture.Data`, `AquaCulture.IoT.OpcUa`, `AquaCulture.GIS.Gdal`, BMKG / NASA POWER / marine clients,
  Modbus RTU serial client, ChirpStack v4 parser, 5 CLI commands, data-connectors guide (EN/ID), 14 new tests. Parquet export moved from Parquet.Net to
  ParquetSharp because Parquet.Net's managed `Parquet.dll` shadowed GDAL's native `parquet.dll` (BadImageFormatException on Windows). The `aqua` tool package
  drops ONNX Runtime's iOS/Android bundles (233 → 118 MB, below the 1.0.0 size of 167 MB). `pack.ps1` reads the version from `Directory.Build.props`. Version 1.1.0.
- **2026-11-12**: IoTCom.Net is on nuget.org (id `IoTCom.Net`, preview only; the search index did not list it, so it was first thought missing). Added
  `AquaCulture.IoT.IoTCom`, packed as `-preview.1` so a stable package never depends on a prerelease; pinned System.Formats.Cbor 10.0.15 (advisory on 10.0.12).
