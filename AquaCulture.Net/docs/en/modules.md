# Modules

## AquaCulture.Core
`SpeciesCatalog` (10 tropical species with tolerance bands, TGC, von Bertalanffy, length–weight, feeding tables, prices), `Farm`, `Pond`
(`CultureSystem`: earthen, concrete, lined, biofloc, floating net cage, RAS, hatchery, aquaponic, raceway), `CultureCycle` (sampling, feed,
mortality, harvest, expense logs), `Stats`, `TimeSeries`, `Numerics` (Nelder–Mead, Brent, golden section, RK4, linear solve), `GeoPolygon`,
`DeterministicRandom`, `services.AddAquaCulture(...)`, `IAquaEventBus`.

## AquaCulture.WaterQuality
- `WaterChemistry`: un-ionised ammonia (Emerson et al. 1975, Bower & Bidwell salinity correction), DO saturation (Benson & Krause, APHA 4500-O, altitude),
  free CO₂, bicarbonate and lime doses, salt for nitrite (Cl⁻:NO₂-N 20:1), molasses for TAN, exchange fraction.
- `WaterQualityIndex.Assess`: per-parameter suitability 0–100 against species bands, weighted WQI capped when a key parameter is critical; `Ccme` index.
- `AlertEngine`: threshold rules with hysteresis, a DO-decline trend rule, bus publishing.
- `RecommendationEngine`: prioritised, quantified actions (aeration sizing at ≈1.65 kW/t, feed cuts, exchange m³, NaHCO₃/lime kg, salt, molasses, sludge).

## AquaCulture.Growth
`GrowthModels` (TGC, SGR, ADG, von Bertalanffy, Gompertz, logistic), `GrowthModifiers`, `GrowthSimulator` (daily step: TGC × temperature × DO × NH₃ × salinity
× crowding × ration; stress mortality; feed = gain × species FCR worsened by stress, capped by appetite), `HarvestPredictor` (at weight, margin optimum,
size-graded shrimp price, biomass confidence interval, TGC calibration).

### Mechanistic growth (v1.3)
- `Bioenergetics`: Wisconsin model (Fish Bioenergetics 4.0 structure). Consumption and respiration follow Kitchell temperature curves, and
  egestion, excretion and SDA close the energy budget. `FitP` finds the proportion of maximum ration that reproduces observed growth.
  The tilapia and whiteleg-shrimp parameter sets are **indicative**: calibrate `p` and `ACT` with your samplings.
- `DebModel`: standard Dynamic Energy Budget for the growth phase (reserve, structure, κ-rule, Arrhenius temperature). Parameters come
  from the Add-my-Pet collection. At constant food it reproduces von Bertalanffy growth with L∞ = f·L_m and r_B = k_M·g / (3(f + g)).
- `ShrimpMoultModel`: size- and temperature-dependent intermoult period, Drach stages A–D3, and a stepwise individual weight. It also
  computes the pond moulting share and feeding factor after a synchronising event (water exchange, moon phase), the moment farmers cut
  feed and add minerals.

## AquaCulture.Feeding
`Fcr`, `FeedingCalculator.Plan`, `FeedCatalog`, `FeedOptimizer` (adjusted FCR, cost per kg gain, ranking, Pearson square, cycle cost analysis), `AutoFeedingAdvisor`.

## AquaCulture.Disease
`DiseaseKnowledgeBase` (15 diseases × signs with P(sign|disease), risk factors, treatment, prevention, WOAH-notifiable flag), `DiagnosisEngine.Diagnose`
(naive Bayes + "none of these" hypothesis), `Risk` / `RiskProfile` (noisy-OR of factor scores).

## AquaCulture.StockAssessment
`SurplusProduction` (Schaefer/Fox equilibrium, dynamic Schaefer fit with Kobe status), `StockRecruitment`, `YieldPerRecruit.Analyze` (Fmax, F0.1, F40 %),
`LengthBased` (Pauly M, Then M, Powell–Wetherall, catch curve, exploitation rate).

## AquaCulture.Population
`AgeStructuredModel` (recruitment models incl. steepness, CV, seasonality, migration, Holling-II predator, `HarvestPolicy` constant F or hockey stick),
`LeslieMatrix.Dominant`, `PredatorPrey.Simulate`.

## AquaCulture.Environment
`WeatherObservation`, `DailyWeather`, `SyntheticWeather`, `OpenMeteoClient`, `WeatherImpactPredictor` (water temperature lag model, dilution, acid runoff,
photosynthesis index, stratification, storms), `ClimateRiskAnalysis` (heatwave, heavy rain, flood, drought, storm, overcast; likelihood × impact by system).

## AquaCulture.Hydrodynamic
`Grid2D`, `FlowFields` (uniform, river, paddle wheels, tidal), `ScalarTransport` (CFL-stable explicit solver on the Rust kernel), `Sediment`
(Stokes velocity, plume, waste load), `PondOxygenBudget` (photosynthesis, plankton/animal respiration, SOD, wind reaeration, aerator AOTR; set-point or schedule),
`RasMassBalance`.

## AquaCulture.Data, IoT.OpcUa, GIS.Gdal (v1.1)
`AquaStore` (EF Core / SQLite: farms, cycles and logs, readings, decisions; `SeriesAsync` → `TimeSeries`, retention), `RecordingTransport`,
`TimeSeriesExport` (CSV, Parquet via ParquetSharp); `OpcUaReader` and `PondOpcUaSimulator` (OPC Foundation stack); IoT.IoTCom (preview): `IoTComModbusTransport`, `AquaSenML`, `NmeaCageDecoder`, `CageZoneWatch` (AIS), `SparkplugPondPublisher`; `GdalRaster` (GeoTIFF via GDAL.Net) and
`WaterColour` (NDWI, MNDWI, NDCI → chlorophyll-a after Mishra & Mishra 2012, turbidity after Dogliotti et al. 2015, trophic state).
Environment adds `BmkgClient`, `NasaPowerClient`, `MarineForecastClient`; IoT adds `ModbusRtuClient` (serial) and `ChirpStack`. See [Data and connectors](data-connectors.md).

## AquaCulture.IoT, GIS, DigitalTwin
See [IoT and digital twin](iot-twin.md). GIS: `GeoJson`, `FarmMapping` (pond blocks, features, IDW), `WaterSourceAnalysis`, `DrainageAnalysis` (D8),
`NtsInterop` (UTM via ProjNet, buffers and unions via NetTopologySuite).

## AquaCulture.AI, Forecasting
See [AI and the AquaAgent](ai-agent.md). Forecasting: `HoltLinear`, `HoltWinters`, `Autoregressive`, `AutoForecaster`, `DoCrashForecaster`, `HarvestForecaster`.

## AquaCulture.ComputerVision
`RgbImage`, `ImageOps` (resize, letterbox, CHW tensor, HSV), `YoloDecoder` (v5 and v8 layouts, class-wise NMS), `OnnxDetector`, `AnimalAnalyzer`
(segmentation, components, moments, length → weight, screening), `SyntheticAquaImage`, `OpenCvInterop` (add an OpenCvSharp runtime package).

### Model hub and fish length (v1.2)

```csharp
// Hugging Face Hub: licence check, cached download (ETag, offline fallback), ONNX detector
var hub = new ModelHub();                                   // HF_TOKEN for gated repos, HF_ENDPOINT for a mirror
var info = await hub.GetModelInfoAsync("your-org/shrimp-yolo-onnx");
using var detector = await hub.LoadDetectorAsync("your-org/shrimp-yolo-onnx", "best.onnx", ["shrimp"]);   // refuses AGPL/NC unless acceptLicense: true

// AquaCulture.ComputerVision.MediaPipe: tap a fish, get its length and weight (MediaPipe MagicTouch segmenter)
using var measurer = new FishMeasurer(SpeciesCatalog.Nila, cmPerPixel: FishMeasurer.ScaleFromReference(600, 30));   // 30 cm board = 600 px
var m = measurer.Measure("sampling.jpg", x: 0.45f, y: 0.52f);       // LengthPx (principal axis), LengthCm, WeightG, AngleDeg
var (median, frames) = measurer.MeasureVideo(videoFrames);           // follows the fish, median of plausible frames
```

- Public Hub models for fish and shrimp are few. At the time of writing they ship as PyTorch `.pt` files under AGPL-3.0 or are gated,
  so none is bundled or downloaded by default.
- Train your own detector with `notebooks/train-yolo-aquaculture.ipynb` (Ultralytics → ONNX), publish it to a Hub repository, and load it
  with `ModelHub`.

## AquaCulture.Analytics
`KpiCalculator.Calculate` (FCR, SR, ABW, ADG, SGR, biomass, productivity, cost breakdown by bilingual keywords, profit, break-even, ROI, B/C, Water Health Index,
benchmarks), `Summarize` for a farm.

## AquaCulture.Reporting
`ReportDocument` (headings, paragraphs, bullets, key-values, tables, images) → `ReportWriters.Xlsx/Pdf/Docx/Csv/Json/Html/Markdown`;
`ComplianceAudit` for GAP, CBIB, ASC and BAP; `Reports.Cycle` and `Reports.Compliance`.

## AquaCulture.Visualization and AquaCulture.Blazor
`ColorRamp` (oxygen, thermal, depth, waste, score, terrain), `PngEncoder`, `SvgCharts` (line, bars, gauge, sparkline, heatmap, map). Blazor components:
`PondMap`, `WaterChart`, `FeedScheduler`, `SensorGrid`, `GISViewer`, `DigitalTwinViewer`, `Gauge`, `KpiCard`, `SvgMarkup`
(include `_content/AquaCulture.Blazor/aquaculture.css`).
