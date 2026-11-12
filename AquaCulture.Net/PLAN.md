# AquaCulture.Net: development roadmap / peta jalan pengembangan

> Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

The roadmap in `solution-design.md` grouped modules into v1.0–v5.0. v1.0.0 already ships every module from that design as a working first version,
so the versions below deepen them rather than introduce them.

## v1.0: Foundation (current) ✅
- Core, WaterQuality, Growth, Feeding, Analytics (design v1.0)
- AI, IoT, GIS, Reporting (design v2.0) — first versions
- DigitalTwin, ComputerVision (design v3.0) — first versions
- Hydrodynamic, StockAssessment (design v4.0) — first versions; plus Population, Environment, Forecasting, Visualization, Blazor
- Rust kernels with managed fallback; gallery (Blazor + Avalonia/ThreeNet, 17 use cases); CLI; 4 templates; bilingual docs; NuGet packaging and CI

## v1.1: Data and connectors
- Persistence: EF Core / SQLite store for farms, cycles, readings and decisions; time-series export (Parquet/CSV)
- BMKG weather and marine forecasts; NASA POWER history for climate risk
- ChirpStack webhook; Modbus RTU over serial (System.IO.Ports); OPC-UA client package (OPC Foundation stack); IoTComNet adapters
- GDAL.Net raster I/O (GeoTIFF DEMs, Sentinel-2 water-colour / chlorophyll for reservoirs and KJA zones)

## v1.2: AI depth
- Trained ONNX models: shrimp disease (WSSV/AHPND/black gill) and fish counting/sizing (YOLO) with datasets and training notebooks
- HF.Net model hub integration; MediaPipeNet for fish pose/length estimation from video
- LSTM / gradient-boosted DO and growth forecasters on farm data (GravicodeScience, ML.NET, TorchSharp)
- Gravicode.RLNet policies for aeration scheduling and feeding with continuous actions
- AquaAgent memory, multi-pond context, voice (Indonesian ASR/TTS) for farmers

## v1.3: Science fidelity
- Bioenergetic growth model (Wisconsin-style) and DEB option; stage-specific shrimp moult cycle
- Biofloc C:N dynamics, nitrification kinetics, sludge model; RAS component models (drum filter, MBBR, degassing)
- 3-D stratified reservoir model for KJA upwelling (umbalan) risk; tidal coastal transport with real bathymetry
- Stock assessment: ELEFAN (TropFishR-style), catch-only methods (CMSY), Bayesian surplus production

## v2.0: Enterprise edition (from the design)
- Farm ERP (inventory, purchasing, payroll), hatchery management (broodstock, spawning, larval rearing)
- Traceability (lot to pond, GS1 EPCIS), ESG reporting (energy, carbon, water footprint, mangrove)
- AI Copilot across farms; multi-tenant cloud deployment

## v5.0: Full aquaculture operating system
- Edge OS image for gateways, offline-first sync, marketplace for sensors, feeds and buyers

## Platform
- NativeAOT samples for edge gateways (Raspberry Pi 4/5)
- BenchmarkDotNet project and performance budgets in CI
- MAUI/mobile client for field sampling
