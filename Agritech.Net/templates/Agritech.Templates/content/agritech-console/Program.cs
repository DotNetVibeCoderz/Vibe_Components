using Agritech.AI;
using Agritech.AI.Agent;
using Agritech.Core;
using Agritech.Core.Geo;
using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"{AgritechInfo.Name} {AgritechInfo.Version} — {AgritechInfo.CreditEn}\n");

// 1. Describe the farm
var location = new GeoCoordinate(-6.3045, 107.3012);            // Karawang, West Java
var soil = new SoilSample { Ph = 5.4, OrganicCarbonPct = 1.2, TotalNitrogenPct = 0.12, AvailablePMgKg = 9, ExchangeableKCmolKg = 0.28, SandPct = 22, SiltPct = 38, ClayPct = 40 };
var crop = CropCatalog.Default.Get("CROP_ID");
var planting = DateOnly.FromDateTime(DateTime.Today).AddDays(-20);

// 2. Soil health & fertilizer plan
var health = SoilHealth.Assess(soil);
Console.WriteLine($"Soil health {health.Score}/100 ({health.Grade}), texture {soil.Texture}");
foreach (var rec in health.Recommendations) Console.WriteLine($"  → {rec}");

var advice = FertilizerAdvisor.Advise(crop, soil, areaHa: 1.5);
Console.WriteLine($"\nFertilizer plan for {crop.Name} (target {advice.TargetYieldTonHa} t/ha):");
foreach (var app in advice.Schedule)
    Console.WriteLine($"  {app.Stage} ({app.FromDap}-{app.ToDap} DAP): " + string.Join(", ", app.Products.Select(p => $"{p.Product.Name} {p.KgPerHa} kg/ha")));

// 3. Weather & irrigation (offline synthetic weather; swap for new OpenMeteoWeatherProvider() to use live data)
IWeatherProvider weather = new SyntheticWeatherProvider();
var history = await weather.GetHistoricalAsync(location, planting, DateOnly.FromDateTime(DateTime.Today).AddDays(-1));
var forecast = await weather.GetForecastAsync(location, 7);
var irrigation = new IrrigationAdvisor(crop, soil.Hydraulics(), new WeatherStation(location.Latitude, 20))
    .Recommend(planting, history, forecast, areaHa: 1.5);
Console.WriteLine($"\nIrrigation: {(irrigation.IrrigateNow ? $"irrigate {irrigation.GrossDepthMm} mm ({irrigation.VolumeM3} m³)" : "not needed today")} — {irrigation.Reason}");

// 4. Ask the AI farm agent (offline by default; set AGRITECH_LLM_* environment variables to use an LLM)
var agent = new FarmAgent(new FarmContext { CropId = crop.Id, PlantingDate = planting, AreaHa = 1.5, Soil = soil, Location = location, Weather = weather });
Console.WriteLine("\n" + agent.Ask("Berapa dosis pupuk terbaik minggu ini?"));
