# Agritech.Net: development roadmap / peta jalan pengembangan

> Made by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

## v1.0: Foundation (current) ✅
- Core + 17 domain modules from `solution-design.md` (Crops, Soil, Weather, GIS, RemoteSensing, Drones, IoT, AI, Simulation, Genomics, Livestock, Fisheries, Robotics, SupplyChain, Marketplace, Visualization, DigitalTwin)
- Rust native kernels with SIMD fallback
- AI farm agent (`Ask(...)`) with OpenAI / Azure OpenAI / Hugging Face / Ollama / offline
- Gallery (Blazor + Avalonia), CLI tool, 4 project templates, bilingual docs, NuGet packaging and CI

## v1.1: Data connectors
- Cloud-Optimized GeoTIFF reader (Sentinel-2/Landsat COG windows from STAC assets), integrating with GDAL.Net from this monorepo
- BMKG (Indonesia) weather and NASA POWER providers
- ChirpStack webhook support; Modbus/RS-485 soil-sensor adapter (IoTComNet)
- Persistent twin state (SQLite/EF Core) and time-series export (Parquet/CSV)

## v1.2: AI depth
- ONNX/TorchSharp `IPlantDiseaseClassifier` with PlantVillage-trained weights; HF.Net model hub integration
- Gradient-boosted and LSTM yield forecasters (GravicodeScience / ML.NET)
- Reinforcement-learning irrigation and fertigation policies (Gravicode.RLNet) trained on `CropSimulator`
- Agent memory, multi-turn conversations, voice (Indonesian ASR/TTS) for farmers

## v1.3: Simulation fidelity
- Calibration toolkit (parameter estimation against field trials)
- Multi-layer N and P dynamics, paddy ponding and puddling for rice, perennial crops (oil palm, coffee, cacao)
- Pest–crop coupling, CO₂ × temperature interactions, ensemble GCM downscaling

## v2.0: Future roadmap (from solution design)
- **Agricultural Foundation Models**: fine-tuned LLM/VLM for agronomy in Bahasa Indonesia
- **Carbon credit tracking**: soil-carbon MRV, methane from AWD rice, verifiable registries
- **ESG agriculture**: sustainability scoring, EUDR deforestation-free traceability for palm oil, coffee and cacao
- **Autonomous drone swarms**: multi-UAV task allocation and collision-free coverage
- **3D farm digital twin**: ThreeNet / OpenSceneGraph.Net rendering of terrain, canopy and machinery

## Platform
- Native binaries for win-x64, linux-x64/arm64 and osx-x64/arm64 via CI (done in workflow)
- NativeAOT samples for edge gateways (Raspberry Pi, Jetson)
- Benchmarks project (BenchmarkDotNet) and performance budgets in CI
