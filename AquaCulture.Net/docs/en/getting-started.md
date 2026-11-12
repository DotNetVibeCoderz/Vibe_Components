# Getting started

Requirements: .NET 10 SDK. The native Rust kernels ship inside `AquaCulture.Native` for win-x64, linux-x64/arm64 and osx-x64/arm64;
on other platforms the managed fallback runs automatically.

## Install

```bash
dotnet add package AquaCulture.WaterQuality    # brings AquaCulture.Core and AquaCulture.Native
dotnet add package AquaCulture.Growth
dotnet add package AquaCulture.Feeding
dotnet add package AquaCulture.AI              # predictive models and the AquaAgent
```

## 1. Check the water

```csharp
using AquaCulture.Core.Species;
using AquaCulture.WaterQuality;

var species = SpeciesCatalog.Get("vaname");                    // or "Udang vaname", "Litopenaeus vannamei"
var sample = new WaterSample { TemperatureC = 30.5, DissolvedOxygen = 3.8, Ph = 8.4, Ammonia = 1.6, SalinityPpt = 18, Alkalinity = 95 };
var a = WaterQualityIndex.Assess(sample, species);
Console.WriteLine($"WQI {a.Wqi} {a.Class}, NH3 {a.UnionizedAmmonia} mg/L, DO {a.DoSaturationPct} % saturation");
foreach (var issue in a.Issues) Console.WriteLine(issue.En);

foreach (var r in RecommendationEngine.Recommend(a, new PondContext(VolumeM3: 2600, AreaM2: 2000, BiomassKg: 3000, AerationKw: 4.4)))
    Console.WriteLine($"{r.Priority}. {r.Action.En} ({r.Quantity} {r.Unit}) — {r.Rationale.En}");
```

Every user-facing text is a `Text2` with `.En` and `.Id` (Bahasa Indonesia).

## 2. Watch alerts on a stream

```csharp
var alerts = AlertEngine.ForSpecies(species);
foreach (var reading in readings)                     // WaterSample snapshots from sensors
    foreach (var alert in alerts.Evaluate(reading))
        Console.WriteLine($"{alert.Severity}: {alert.Message.En}{(alert.Cleared ? " (cleared)" : "")}");
```

Rules have hysteresis, so a noisy probe near a limit raises one alert, and a trend rule warns when DO falls faster than 0.6 mg/L per hour.

## 3. Plan today's feeding

```csharp
using AquaCulture.Feeding;
var plan = FeedingCalculator.Plan(species, abwG: 12, population: 220_000, tempC: 30, dawnDo: 4.2, nh3: a.UnionizedAmmonia);
Console.WriteLine($"{plan.DailyKg} kg/day, {plan.MealsPerDay} meals, {plan.Feed?.Name}");
foreach (var m in plan.Meals) Console.WriteLine($"{m.Time} {m.Kg} kg");
```

## 4. Forecast growth and harvest

```csharp
using AquaCulture.Growth;
using AquaCulture.Forecasting;

var sim = new GrowthSimulator(species);
var start = new StockState(species, DateOnly.FromDateTime(DateTime.Today), AbwG: 1, Count: 250_000, VolumeM3: 2600);
var trajectory = sim.Simulate(start, 140, new DailyEnvironment(TemperatureC: 29, DissolvedOxygen: 5, Nh3: 0.03, SalinityPpt: 18));
var best = HarvestPredictor.Optimal(trajectory, areaM2: 2000, new HarvestEconomics(FeedPricePerKg: 17_500, DailyFixedCost: 200_000));
var mc = HarvestForecaster.Forecast(species, DateOnly.FromDateTime(DateTime.Today), 1, 250_000, 20, Enumerable.Repeat(29.0, 160).ToArray());
Console.WriteLine($"Best harvest DOC {best.DayOfCulture}, P50 {mc.DayP50} days (P10–P90 {mc.DayP10}–{mc.DayP90})");
```

## 5. Dependency injection

```csharp
services.AddAquaCulture(b => b.AddWaterQuality().AddGrowth().AddFeeding().AddIoT().AddDigitalTwin());
```

`IAquaEventBus` carries `WaterAlert`, `SensorReading` and `TwinDecision` events between modules.

## Next

- [Modules](modules.md) for the full API surface
- [IoT and digital twin](iot-twin.md) to connect real sensors
- [AI and the AquaAgent](ai-agent.md) to add an LLM assistant
