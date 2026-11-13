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

## v1.1: Data and connectors ✅ (2026-11-12)
- ✅ Persistence: `AquaCulture.Data`, an EF Core / SQLite store for farms, cycles, readings and decisions, with time-series export (Parquet via ParquetSharp, CSV)
- ✅ BMKG village weather forecasts; NASA POWER history for climate risk. Marine forecasts use Open-Meteo Marine because BMKG's maritime API is not a stable public API
- ✅ ChirpStack v4 webhook parser; Modbus RTU over serial (System.IO.Ports); OPC UA client package `AquaCulture.IoT.OpcUa` (OPC Foundation stack) with a pond simulator
- ✅ IoTCom.Net adapters (`AquaCulture.IoT.IoTCom`, preview like IoTCom.Net itself): Modbus transport, SenML, NMEA 0183 cage sensors and drift, AIS vessel watch, Sparkplug B
- ✅ GDAL.Net raster I/O (`AquaCulture.GIS.Gdal`: GeoTIFF read/write with WGS-84 warp) and Sentinel-2 water colour (NDWI, MNDWI, NDCI chlorophyll-a, turbidity, trophic state)

## v1.2: AI depth ✅ (2026-11-12)
- ✅ More IoTCom.Net adapters: LwM2M server (IPSO objects), farm-local LoRaWAN network server (Semtech UDP, AS923-2), M-Bus aerator energy meters
- ✅ Gallery demos: data & connectors (BMKG, NASA POWER, marine, ChirpStack, SQLite → Parquet), satellite chlorophyll around cages, learned DO forecast & RL aeration
- 🟡 Trained ONNX models: training notebook (`notebooks/train-yolo-aquaculture.ipynb`, YOLO → ONNX → Hub) done; trained weights wait for a licensed, labelled shrimp/fish dataset (none permissive on the Hub yet)
- ✅ Hub integration (`ModelHub`: licence check, cached download, ONNX detector) and MediaPipeNet fish length/weight from photos and video (`FishMeasurer`)
- ✅ Gradient-boosted (ML.NET FastTree) and LSTM (TorchSharp, `AquaCulture.AI.Torch`) DO forecasters with baseline skill; farm-data growth learning
- ✅ Gravicode.RLNet SAC policies for hourly aeration and daily feeding (continuous actions), evaluated against farmer baselines
- ✅ AquaAgent long-term memory (BM25 notes), multi-pond context, bounded history, voice (OpenAI-compatible ASR/TTS, Indonesian)

## v1.3: Science fidelity ✅ (2026-11-12)
- ✅ Bioenergetic growth model (Wisconsin-style) and DEB option; stage-specific shrimp moult cycle
- ✅ Biofloc C:N dynamics, nitrification kinetics, sludge model; RAS component models (drum filter, MBBR, degassing)
- ✅ Stratified reservoir model for KJA upwelling (umbalan) risk — 1-D vertical (layered) column; full 3-D circulation deferred
- ✅ Tidal coastal transport over real bathymetry (raster → grid, depth-scaled tide, plume footprint and receptor exposure)
- ✅ Stock assessment: ELEFAN (TropFishR-style), catch-only methods (CMSY), Bayesian surplus production

## v2.0: Enterprise edition ✅ (2026-11-12)
- ✅ Farm ERP (inventory, purchasing, payroll); ✅ hatchery management (broodstock, spawning, larval rearing, PL quality, EPCIS seed lots)
- ✅ Traceability (lot to pond, GS1 EPCIS 2.0), ESG reporting (energy, carbon, water footprint, mangrove, N/P)
- ✅ AI Copilot across farms; multi-tenant cloud deployment (database per tenant, `aqua-cloud` template with Dockerfile)

## v2.1: Edge and platform ✅ (2026-11-13)
- ✅ NativeAOT edge gateway `aqua-edge` (`samples/AquaCulture.EdgeGateway`): local alerts and aerator control, Modbus TCP or simulated probes,
  self-test; CI publishes it for linux-arm64 (Raspberry Pi 4/5) and linux-x64 and runs the native binary
- ✅ Offline-first sync: `EdgeOutbox` (durable, power-cut safe, capped), `CloudSyncClient` (batches, backoff, config pull with ETag),
  idempotent per-gateway watermarks in `AquaStore` and the `aqua-cloud` sync endpoints
- ✅ .NET MAUI field-sampling app (`samples/AquaCulture.FieldApp`, Android + Windows): instant WQI, dosed actions, ABW/biomass, feeding plan,
  offline journal synced over the same protocol; CI builds Windows and an Android APK
- ✅ BenchmarkDotNet project (`benchmarks/AquaCulture.Benchmarks`, Rust vs managed kernels and the hot models) with time and allocation
  budgets enforced in CI before publishing

## v5.0: Full aquaculture operating system
- 🟡 Edge OS image for gateways: the gateway is one self-contained binary with a systemd unit (documented); a flashable Pi image is not built yet
- ✅ Offline-first sync (v2.1)
- ⏳ Marketplace for sensors, feeds and buyers

## Platform
- ✅ NativeAOT samples for edge gateways (Raspberry Pi 4/5) (v2.1)
- ✅ BenchmarkDotNet project and performance budgets in CI (v2.1)
- ✅ MAUI/mobile client for field sampling (v2.1)
