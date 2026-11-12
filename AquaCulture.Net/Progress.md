# AquaCulture.Net: progress tracking

Status legend: ✅ done · 🟡 partial · ⏳ planned

| Area | Status | Notes |
|---|---|---|
| Solution scaffold (.NET 10, slnx, shared props) | ✅ | 26 projects, Release build with 0 warnings |
| AquaCulture.Core | ✅ | 10 species, farm/pond/cycle model, SIMD stats, numerics, geo, deterministic RNG, DI, event bus |
| AquaCulture.WaterQuality | ✅ | 15 parameters, NH₃, DO saturation (verified against APHA tables), WQI + CCME, alerts with hysteresis and DO trend, dosed recommendations |
| AquaCulture.Growth | ✅ | TGC / VBGF / Gompertz, environmental modifiers, mortality, feed calibrated to species FCR, harvest optimum |
| AquaCulture.Feeding | ✅ | FCR, plans, schedules, least-cost feed, Pearson square, auto-feeder |
| AquaCulture.Disease | ✅ | 15 diseases, Bayesian diagnosis, environmental risk |
| AquaCulture.StockAssessment | ✅ | Schaefer/Fox, dynamic fit + Kobe, B-H/Ricker, YPR, length-based methods; ELEFAN ⏳ |
| AquaCulture.Population | ✅ | Age-structured model, HCRs, predation, Leslie, predator–prey |
| AquaCulture.Environment | ✅ | Open-Meteo (live-tested), synthetic weather, impact predictor, climate risk |
| AquaCulture.Hydrodynamic | ✅ | 2-D transport on Rust kernel, flows, sediment/plume, oxygen budget, RAS mass balance |
| AquaCulture.IoT | ✅ | MQTT, REST, Modbus RTU/TCP, LoRaWAN (LPP, TTN), OPC-UA mapping, edge analytics, simulator; ESP32 firmware sample |
| AquaCulture.GIS | ✅ | GeoJSON, pond layout, IDW, D8 drainage, water sources, NTS + ProjNet |
| AquaCulture.AI | ✅ | GBT growth, logistic mortality, disease/harvest outlook, Q-learning feed policy, ML.NET, ONNX, AquaAgent (live-tested: Azure OpenAI gpt-5-mini, Anthropic claude-sonnet-5-5) |
| AquaCulture.ComputerVision | 🟡 | YOLO/ONNX decode + NMS, model-free counting/sizing/screening, OpenCvSharp interop; trained disease models ⏳ v1.2 |
| AquaCulture.Forecasting | ✅ | Holt, Holt–Winters, AR, auto; dawn-DO forecast; Monte-Carlo harvest |
| AquaCulture.DigitalTwin | ✅ | Pond/farm twins, explained decisions, actuator commands, what-if |
| AquaCulture.Analytics | ✅ | Cycle KPIs, cost breakdown, benchmarks, farm summary |
| AquaCulture.Reporting | ✅ | XLSX/PDF/DOCX/CSV/JSON/HTML/MD (no third-party libs), GAP/CBIB/ASC/BAP |
| AquaCulture.Visualization / Blazor | ✅ | SVG/PNG charts; 6 domain components incl. Three.js 3D pond |
| AquaCulture.Native (Rust) | ✅ | 7 Rust tests; bit-identical managed fallback; CI builds 5 RIDs |
| Tests | ✅ | 102 xUnit tests (reference values, native/managed parity, determinism, file-format validity, every gallery scenario) |
| Gallery: Blazor | ✅ | 17 demos, EN/ID, light/dark, screenshots in docs/images |
| Gallery: Avalonia | ✅ | 17 demos, ThreeNet 3D pond (interactive + offscreen), headless screenshot mode |
| CLI (`aqua`) | ✅ | 16 commands |
| Templates | ✅ | 4 templates, verified to build from the packed feed |
| NuGet icon & packages | ✅ | 22 packages + symbols; icon generated with Rodin (Nano Banana 2) |
| Docs EN / ID | ✅ | 9 guides each + README / README.id |
| Monorepo move (Vibe_Components/AquaCulture.Net) + CI workflow | ✅ | `.github/workflows/aquaculture-net.yml`, tag `aquaculture-net-v*` publishes with `NUGET_API_KEY` |
| NuGet publish | ⏳ | waiting for the release tag |

## Log
- **2026-11-12**: v1.0.0 implemented end-to-end. Fixed during verification: edge Hampel filter locking out after a level shift (all values now enter the window),
  DO-trend alert using end points instead of a regression, dawn-DO forecast extrapolating the dusk slope linearly (now damped) and assuming aerators off in the twin,
  shrimp respiration about 2× literature, simulated FCR too low (feed now derived from gain × species FCR), Monte-Carlo harvest spread too narrow (per-run TGC),
  doubled `runtimes` path in the native package, 3-D pond water hidden under the ground plane.
- **2026-11-12**: web-twin / web-twin-dark screenshots showed an empty 3D pond: the screenshot script passed `--disable-gpu`, which disables WebGL in headless Chrome.
  Removed the flag and shortened the virtual-time budget to 12 s (longer budgets let the Blazor circuit drop and the reconnect overlay grey the page).
  `DigitalTwinViewer` now shows a "needs WebGL" message instead of an empty box when the 3D context cannot be created.
