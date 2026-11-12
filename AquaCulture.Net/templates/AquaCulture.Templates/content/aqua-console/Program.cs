// AquaCulture.Net pond advisor — water quality, feeding, growth/harvest forecast and the AquaAgent.
// Made by Gravicode Studios, led by Kang Fadhil.
using AquaCulture.AI.Agent;
using AquaCulture.Core.Species;
using AquaCulture.Feeding;
using AquaCulture.Forecasting;
using AquaCulture.Growth;
using AquaCulture.WaterQuality;

var species = SpeciesCatalog.Get("SPECIES_ID");
double abw = species.MarketWeightG * 0.4, population = species.Group == SpeciesGroup.Shrimp ? 200_000 : 10_000;
var sample = new WaterSample { TemperatureC = 29.5, DissolvedOxygen = 4.4, Ph = 8.1, Ammonia = 0.9, SalinityPpt = species.Habitat == Habitat.Freshwater ? 0.3 : 18 };

// 1. Water quality and actions
var wq = WaterQualityIndex.Assess(sample, species);
Console.WriteLine($"{species.Name.En}: WQI {wq.Wqi:0} ({wq.Class}), NH3 {wq.UnionizedAmmonia:0.000} mg/L");
foreach (var a in RecommendationEngine.Recommend(wq, new PondContext(VolumeM3: 2600, AreaM2: 2000, BiomassKg: abw * population / 1000)))
    Console.WriteLine($"  {a.Priority}. {a.Action.En}");

// 2. Today's feeding plan
var plan = FeedingCalculator.Plan(species, abw, population, sample.TemperatureC!.Value, sample.DissolvedOxygen, wq.UnionizedAmmonia);
Console.WriteLine($"Feed {plan.DailyKg:0.0} kg/day in {plan.MealsPerDay} meals ({plan.Feed?.Name})");

// 3. Harvest forecast (Monte-Carlo, Rust kernel when available)
var forecast = HarvestForecaster.Forecast(species, DateOnly.FromDateTime(DateTime.Today), abw, population, species.MarketWeightG, Enumerable.Repeat(29.0, 200).ToArray());
Console.WriteLine($"Market size in {forecast.DayP50} days (P10–P90 {forecast.DayP10}–{forecast.DayP90})");

// 4. Ask the agent (set AQUA_LLM_PROVIDER/ENDPOINT/API_KEY/MODEL for an LLM; offline otherwise)
var agent = new AquaAgent(new AquaContext { Species = species, AbwG = abw, Population = (long)population, Water = sample });
Console.WriteLine(agent.Ask(args.Length > 0 ? string.Join(' ', args) : "Berapa pakan hari ini?"));
