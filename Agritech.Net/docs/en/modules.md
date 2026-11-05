# Module guide

Each section lists the main types and a short example. All APIs are XML-documented, so IntelliSense shows the details.

## Agritech.Core
`GeoCoordinate`, `GeoPolygon` (area, perimeter, centroid, contains, local projection), `GeoMath` (haversine, bearing, destination),
`TimeSeries<T>`, `Stats` (SIMD sum/mean, percentile, OLS, Otsu), `Matrix` (Gaussian elimination), `Units`, `Farm`/`Field`,
`IAgriEventBus`, `AddAgritech(...)`, `AgritechInfo`.

```csharp
var block = GeoPolygon.Rectangle(new(-6.3, 107.3), 200, 100);   // 2.00 ha
double d = GeoMath.Distance(new(-6.2088, 106.8456), new(-6.9175, 107.6191)); // ≈ 117 km
```

## Agritech.Crops
`CropCatalog` (rice, maize, wheat, soybean, cassava, potato, tomato, chili, shallot, sugarcane, oil palm, coffee, cacao, peanut, mung bean, banana; Indonesian names supported),
`Phenology` (GDD with cutoff, FAO-56 Kc curve, stages), `CropLifecycle`, `YieldEstimator` (FAO-33 Ky, biomass × HI, NDVI), `CropRecommender` (fuzzy FAO S1/S2/S3/N with limiting factors).

```csharp
var maize = CropCatalog.Default.Get("jagung");
double y = YieldEstimator.WaterLimited(maize, actualEt: 400, maxEt: 500); // 7.5 t/ha
```

## Agritech.Soil
`SoilSample`, `SoilTexture.Classify` (USDA), `SoilTexture.Hydraulics` (Saxton & Rawls 2006: FC, WP, saturation, Ksat),
`SoilHealth.Assess` (0–100 index with indicator scores and recommendations), `LimeRequirementTonHa`, `FertilizerCalculator`,
`FertilizerProducts` (Urea, ZA, SP-36, TSP, DAP, KCl, NPK Phonska), `NutrientLedger`.

## Agritech.Weather
`DailyWeather`, `Evapotranspiration` (Penman–Monteith, Hargreaves, Ra, daylight, Rs from temperature), `OpenMeteoWeatherProvider`,
`SyntheticWeatherProvider`, `ClimateProjection` (SSP1-2.6 … SSP5-8.5 delta change + CO₂), `AgroClimate` (dry spells, heat days, rainy-season onset, aridity).

## Agritech.GIS
`Projections` (UTM ↔ WGS-84 with sub-mm round trip, Web Mercator, slippy tiles), `GeoJson` (read/write), `Geohash`, `QuadTree<T>`
(bounding box, radius, nearest), `Geometry` (Douglas–Peucker, convex hull, scanlines).

## Agritech.RemoteSensing
`SensorCatalog` (band tables, DN → reflectance including the Sentinel-2 baseline offset), `RasterBand`, `MultispectralImage`, `GeoTransform`,
`VegetationIndices` (NDVI, EVI, SAVI, NDWI, NDMI, NDRE, GNDVI, NBR, MSAVI2), `RasterAnalysis` (classification, zonal stats, management zones, change),
`Phenometrics` (start, peak and end of season, integral), `StacSceneCatalog` (Sentinel-2 L2A and Landsat C2 L2 COG assets), `SyntheticScene`.

```csharp
var scenes = await new StacSceneCatalog().SearchAsync(field.Bounds, new(2026, 6, 1), new(2026, 8, 31), maxCloudPct: 20);
var ndvi = VegetationIndices.Ndvi(image);
var zones = RasterAnalysis.ManagementZones(ndvi, 3);
```

## Agritech.Drones
`CameraSpec` (GSD, footprint, altitude for target GSD), `DroneSpec`, `MissionPlanner.PlanSurvey/PlanSpray` → `MissionPlan.ToQGroundControlPlan()`,
`WeedDetector` (ExG + Otsu + row detection + connected components), `IPlantDiseaseClassifier` / `LesionSeverityAnalyzer`, `OrthomosaicBuilder`.

## Agritech.IoT
`SensorReading`, `ISensorTransport` (`InMemoryTransport`, `MqttSensorTransport`), `CayenneLpp` (decode/encode), `TtnWebhook`,
`EdgeProcessor` (rolling windows, threshold rules, z-score anomalies, deadband), `SimulatedSensorNetwork`.

MQTT topic schema: `agritech/{farm}/{device}/{kind}[/{channel}]` with payload `{"v":28.4,"u":"%VWC","ts":"…"}`.

## Agritech.AI
`YieldForecaster` / `RidgeRegression` (with cross-validation), `PathogenModel` + `DiseaseRiskModel` (rice blast, late blight, anthracnose, downy mildew, black pod),
`PestModel` (fall armyworm, brown planthopper, fruit fly, coffee berry borer), `IrrigationAdvisor` (FAO-56 water balance),
`FertilizerAdvisor` (split schedule), `FarmAgent` (see [AI agent](ai-agent.md)).

## Agritech.Simulation
`CropSimulator.Run(Management, weather)` covers thermal-time phenology, LAI growth and senescence, Beer's law with CO₂-adjusted RUE, a 5-layer tipping-bucket soil
(SCS runoff, drainage, root extraction), root-front growth, mineral N (mineralisation, leaching, critical-N dilution) and a stress-adjusted harvest index.
`ScenarioRunner` runs management × climate scenarios across stochastic years in parallel.

```csharp
var r = new ScenarioRunner(crop, soil, location).Run(
[
    new("Rainfed", mgmt),
    new("Irrigated", mgmt with { Irrigation = IrrigationStrategy.AutoAtRaw }),
    new("SSP5-8.5 2050", mgmt, ClimateScenario.Ssp585, 2050),
], years: 20);
```

## Agritech.Genomics
`NucleotideSequence` (GC, reverse complement, transcription, translation, ORFs, k-mers, Tm, motifs), `Fasta`, `Alignment` (Needleman–Wunsch, Smith–Waterman),
`GenotypeMatrix` (MAF, heterozygosity, HWE χ², QC filter, VanRaden GRM), `BreedingAdvisor` (breeder's equation, rrBLUP GEBV, pedigree A-matrix and inbreeding, cross planning).

## Agritech.Livestock
`Animal`, `Herd` (vital-sign alerts, reproduction calendar), `SpeciesProfile`, `LivestockMath` (THI, heat stress, milk loss, Schoorl weight, DMI, activity-based estrus).

## Agritech.Fisheries
`AquaSpecies` (tilapia, catfish, pangasius, vannamei shrimp, milkfish, gourami), `WaterQuality` (NH₃ fraction, DO saturation, assessment),
`Pond`, `FeedOptimizer` (feeding table with temperature, DO and ammonia adjustment; TGC growth; days to target; stocking).

## Agritech.Robotics
`OccupancyGrid`, `PathPlanner` (A*, smoothing, field coverage with headlands), `TractorModel` (bicycle kinematics), `PurePursuitController`,
`HarvestPlanner` (ripeness, NN + 2-opt), `GreenhouseRobot` (battery-aware state machine).

## Agritech.SupplyChain
`TraceabilityLedger` (harvest, split, merge, SHA-256 hash chain, trace back/forward, recall, verify), `Gs1` (GTIN/SSCC), `ShelfLife` (Q10), `Inventory` (FEFO).

## Agritech.Marketplace
`OrderBook` (price-time priority, transport cost per km), `PriceIntelligence` (MA, volatility, Holt forecast, seasonal index, fair farm-gate price).

## Agritech.DigitalTwin
`FarmTwin` binds devices to fields, ingests readings through the edge processor, advances phenology and `Evaluate()`s explained `Decision`s
(irrigate, pause irrigation, fertilize, scout disease, heat protection, harvest, check device). `Snapshot()` gives a UI-ready state.

## Agritech.Visualization / .Blazor
`ColorMap` (NDVI, water, change, heat), `PngEncoder` (raster → PNG, no dependencies), `SvgCharts` (line, area, bars, sparkline, gauge), `IsometricFarm` (2.5D farm).
Blazor: `AgriLineChart`, `AgriBarChart`, `AgriGauge`, `RasterImage`, `ColorRampLegend`, `IsometricFarmView`, `KpiCard`, `SvgMarkup`. Charts use CSS variables
`--agt-ink`, `--agt-muted` and `--agt-grid` for theming.
