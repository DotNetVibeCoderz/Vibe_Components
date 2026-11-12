# AquaCulture.Net: progress tracking

Status legend: ✅ done · 🟡 partial · ⏳ planned

| Area | Status | Notes |
|---|---|---|
| Solution scaffold (.NET 10, slnx, shared props) | ✅ | 32 projects, Release build with 0 warnings |
| AquaCulture.Core | ✅ | 10 species, farm/pond/cycle model, SIMD stats, numerics, geo, deterministic RNG, DI, event bus |
| AquaCulture.WaterQuality | ✅ | 15 parameters, NH₃, DO saturation (verified against APHA tables), WQI + CCME, alerts with hysteresis and DO trend, dosed recommendations |
| AquaCulture.Growth | ✅ | v1.3: Wisconsin bioenergetics, DEB, shrimp moult cycle · TGC / VBGF / Gompertz, environmental modifiers, mortality, feed calibrated to species FCR, harvest optimum |
| AquaCulture.Feeding | ✅ | FCR, plans, schedules, least-cost feed, Pearson square, auto-feeder |
| AquaCulture.Disease | ✅ | 15 diseases, Bayesian diagnosis, environmental risk |
| AquaCulture.StockAssessment | ✅ | Schaefer/Fox, dynamic fit + Kobe, B-H/Ricker, YPR, length-based methods; v1.3: ELEFAN, CMSY, Bayesian surplus production |
| AquaCulture.Population | ✅ | Age-structured model, HCRs, predation, Leslie, predator–prey |
| AquaCulture.Environment | ✅ | Open-Meteo, BMKG, NASA POWER, Open-Meteo Marine (all live-tested, parsers tested on fixtures), synthetic weather, impact predictor, climate risk |
| AquaCulture.Hydrodynamic | ✅ | v1.3: biofloc N dynamics, RAS components, 1-D reservoir stratification + umbalan, coastal tidal plumes · 2-D transport on Rust kernel, flows, sediment/plume, oxygen budget, RAS mass balance |
| AquaCulture.IoT | ✅ | MQTT, REST, Modbus RTU/TCP + RTU serial client, LoRaWAN (LPP, TTN, ChirpStack v4), OPC-UA mapping, edge analytics, simulator; ESP32 firmware sample |
| AquaCulture.GIS | ✅ | GeoJSON, pond layout, IDW, D8 drainage, water sources, NTS + ProjNet |
| AquaCulture.GIS.Gdal (v1.1) | ✅ | GeoTIFF I/O via GDAL.Net 3.13 with WGS-84 warp; NDWI, MNDWI, NDCI chlorophyll-a, turbidity, trophic state |
| AquaCulture.Data (v1.1) | ✅ | EF Core 10 / SQLite store (farms, cycles + logs, readings, decisions), recording transport, Parquet (ParquetSharp) / CSV export |
| AquaCulture.IoT.OpcUa (v1.1) | ✅ | OPC UA reader/writer (OPC Foundation 1.5.378) and pond simulator; integration-tested client ↔ server |
| AquaCulture.IoT.IoTCom (v1.1, preview) | ✅ | IoTCom.Net 0.22 preview: Modbus transport (in-memory server test), SenML JSON/CBOR, NMEA MTW/DPT/DBT + drift, AIS zone watch, Sparkplug B (tested against a local broker and host); v1.2: LoRaWAN network server, LwM2M gateway, M-Bus energy (all tested against IoTCom.Net simulators) |
| AquaCulture.AI.Torch (v1.2) | ✅ | LSTM DO forecaster on TorchSharp 0.107 (tests use TorchSharp-cpu) |
| AquaCulture.AI | ✅ | GBT growth, logistic mortality, disease/harvest outlook, Q-learning feed policy, ML.NET, ONNX, v1.2: agent memory / multi-pond / voice, SAC aeration/feeding environments (RLNet), `GbtDoForecaster` (ML.NET FastTree, skill vs persistence/diurnal), `FarmGrowthLearner`; AquaAgent (live-tested: Azure OpenAI gpt-5-mini, Anthropic claude-sonnet-5-5) |
| AquaCulture.ComputerVision.MediaPipe (v1.2) | ✅ | `FishMeasurer` on MediaPipeNet MagicTouch: length within 5 % on synthetic tilted fish, video tracking |
| AquaCulture.ComputerVision | 🟡 | YOLO/ONNX decode + NMS, model-free counting/sizing/screening, OpenCvSharp interop; `ModelHub` (v1.2); trained disease / counting models ⏳ need a licensed dataset |
| AquaCulture.Forecasting | ✅ | Holt, Holt–Winters, AR, auto; dawn-DO forecast; Monte-Carlo harvest |
| AquaCulture.DigitalTwin | ✅ | Pond/farm twins, explained decisions, actuator commands, what-if |
| AquaCulture.Analytics | ✅ | Cycle KPIs, cost breakdown, benchmarks, farm summary |
| AquaCulture.Reporting | ✅ | XLSX/PDF/DOCX/CSV/JSON/HTML/MD (no third-party libs), GAP/CBIB/ASC/BAP |
| AquaCulture.Visualization / Blazor | ✅ | SVG/PNG charts; 6 domain components incl. Three.js 3D pond |
| AquaCulture.Native (Rust) | ✅ | 7 Rust tests; bit-identical managed fallback; CI builds 5 RIDs |
| Tests | ✅ | 175 xUnit tests (reference values, native/managed parity, determinism, file-format validity, every gallery scenario) |
| Gallery: Blazor | ✅ | 20 demos (v1.2: connectors, chlorophyll, learning), EN/ID, light/dark, screenshots in docs/images |
| Gallery: Avalonia | ✅ | 20 demos, ThreeNet 3D pond (interactive + offscreen), headless screenshot mode |
| CLI (`aqua`) | ✅ | 21 commands (v1.1: bmkg, history, marine, export, opcua) |
| Templates | ✅ | 5 templates (v2.0: aqua-cloud), verified to build from the packed feed |
| v2.0 Enterprise | ✅ | Traceability (GS1/EPCIS 2.0), Esg, Hatchery, Operations (inventory, purchasing, payroll), FarmCopilot, AquaTenants + aqua-cloud template |
| NuGet icon & packages | ✅ | 26 packages (v1.1, IoT.IoTCom as preview) + symbols; icon generated with Rodin (Nano Banana 2) |
| Docs EN / ID | ✅ | 10 guides each (v1.1: data and connectors) + README / README.id |
| Monorepo move (Vibe_Components/AquaCulture.Net) + CI workflow | ✅ | `.github/workflows/aquaculture-net.yml`, tag `aquaculture-net-v*` publishes with `NUGET_API_KEY` |
| NuGet publish | ✅ | 1.0.0: all 22 packages on nuget.org · 1.1.0: 25 packages + IoT.IoTCom 1.1.0-preview.1 pushed by tag `aquaculture-net-v1.1.0` (CI run 43148528947) · 1.2.0: 27 packages + IoT.IoTCom 1.2.0-preview.1 pushed by tag `aquaculture-net-v1.2.0` (CI run 43157251298) · 1.3.0: 27 packages + IoT.IoTCom 1.3.0-preview.1 pushed by tag `aquaculture-net-v1.3.0` (CI run 43192930499) |

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
- **2026-11-12**: v1.2 RL: `PondOxygenBudget.StepHour` extracted (aerator power fraction; `Simulate` unchanged, all reference tests pass).
  First feeding environment was degenerate (feed above appetite had no cost); it now wastes uneaten feed (cost, TAN, lower dawn DO).
  SAC aeration ≈ tuned set point (no critical-DO hours); SAC feeding +14 % profit over the adjusted feeding table.
- **2026-11-12**: v1.2 vision: HF.Net's GraviHub depends on Parquet.Net, whose `Parquet.dll` breaks GDAL on Windows (same clash as in v1.1),
  so `ModelHub` is a small HTTP client instead. MediaPipeNet 1.0.0 pulls SixLabors.ImageSharp 3.1.12 (advisories NU1902/NU1903); the patched
  3.2.0 needs a commercial licence for Release builds, so it is not pinned. Both need fixes upstream. No permissively licensed fish/shrimp
  ONNX model exists on the Hub yet (AGPL `.pt` or gated), so trained models stay pending.
- **2026-11-12**: v1.2 "AI depth" complete (except trained detector weights, which need a licensed dataset): IoTCom gateways, learned DO
  forecasters (GBT, LSTM), RLNet aeration/feeding, agent memory/multi-pond/voice, model hub + MediaPipe fish length, 3 gallery demos.
  Version 1.2.0; 27 packages (new: AI.Torch, ComputerVision.MediaPipe).
- **2026-11-12**: v1.3 "Science fidelity" complete. Bioenergetics/DEB/moult; biofloc (Ebeling stoichiometry, start-up nitrite peak) and RAS
  components; layered reservoir model with umbalan detection (a 1-D vertical column: full 3-D circulation deferred); tidal plumes over
  bathymetry; ELEFAN, CMSY, Bayesian surplus production. Fixed during verification: a sign error in the wind-mixing energy (columns
  were always fully mixed), convective mixing of equal-density layers, cage respiration spread over the whole column, low-wind latent
  heat (surface reached 38 °C), MCMC step adaptation (0.6 % acceptance). Version 1.3.0.
- **2026-11-12**: v2.0 "Enterprise edition" complete: GS1/EPCIS 2.0 traceability with trace-back and recall, ESG accounting (feed, energy,
  mangrove conversion and restoration, water, N/P), shrimp hatchery (broodstock, larval rearing, PL quality, EPCIS seed lots), farm ERP
  (FEFO inventory feeding cycles per lot, purchasing, Indonesian payroll), multi-farm copilot, database-per-tenant cloud API template.
  Fixed during verification: `LarvalBatch.StageOn` treated day 0 as "not found" because `(0, N, 0)` equals the default tuple.
  Version 2.0.0; 31 packages.
