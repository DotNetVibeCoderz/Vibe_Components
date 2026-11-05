# Getting started

## Requirements

- .NET 10 SDK
- Optional: Rust 1.80+ to rebuild the native kernels (prebuilt `win-x64` binaries ship in `Agritech.Native`; other platforms use the managed SIMD fallback automatically)

## Install

Pick only the packages you need. Each package brings its dependencies:

```bash
dotnet add package Agritech.AI              # Core + Crops + Soil + Weather + agent
dotnet add package Agritech.RemoteSensing   # Core + Native
dotnet add package Agritech.DigitalTwin     # IoT + AI + everything a twin needs
dotnet add package Agritech.Visualization.Blazor
```

## Your first program

```csharp
using Agritech.AI;
using Agritech.Core.Geo;
using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

var location = new GeoCoordinate(-6.3045, 107.3012);
var soil = new SoilSample { Ph = 5.4, OrganicCarbonPct = 1.2, AvailablePMgKg = 9, SandPct = 22, SiltPct = 38, ClayPct = 40 };

// Which crop suits this site?
var best = new CropRecommender().Recommend(new SiteConditions(MeanTempC: 27, SeasonalRainfallMm: 1600, SoilPh: soil.Ph), top: 3);

// Soil health and a fertilizer schedule
var health = SoilHealth.Assess(soil);
var plan = FertilizerAdvisor.Advise(CropCatalog.Default.Get("rice"), soil, areaHa: 2, targetYieldTonHa: 6);

// Weather (live, free, no key) and reference evapotranspiration
var forecast = await new OpenMeteoWeatherProvider().GetForecastAsync(location, days: 7);
var et0 = Evapotranspiration.Compute(forecast, new WeatherStation(location.Latitude, 20));
```

## Dependency injection

```csharp
builder.Services.AddAgritech(b => b
    .AddCrops(catalog => catalog.Register(myLocalVariety))
    .AddSoil()
    .AddWeather(offline: false)          // IWeatherProvider = Open-Meteo
    .AddRemoteSensing()                  // ISceneCatalog = STAC (Earth Search)
    .AddIoT(sp => new MqttSensorTransport(new MqttOptions("broker.local")))
    .AddAI()                             // IChatModel from AGRITECH_LLM_* or offline
    .AddDigitalTwin(farm));
```

Every module registers once (the builder deduplicates), and `AgritechModuleRegistry` lists the loaded modules.

## Offline and deterministic data

The demos and tests run without network access:

- `SyntheticWeatherProvider(seed)`: stochastic weather generator (Markov wet/dry days, gamma rainfall, seasonal temperature) that is deterministic across processes.
- `SyntheticScene.Create(bounds, ...)`: realistic multispectral field imagery with stress patches.
- `RgbImage.SyntheticRowCrop(...)`: drone photo of crop rows with weeds.
- `YieldForecaster.SyntheticDataset`, `BreedingAdvisor.SyntheticPopulation`, `PriceIntelligence.SyntheticSeries`.

## Units and conventions

- Coordinates: WGS-84 decimal degrees (`GeoCoordinate(lat, lon)`). Areas are in m² and ha.
- Water: mm. Nutrients: kg/ha as N, P₂O₅ and K₂O. Yield: t/ha. Temperature: °C.
- Rasters: `float` reflectance 0–1; `NaN` means no data.
- Days after planting are abbreviated DAP (HST in Indonesian).

Next: [module guide](modules.md).
