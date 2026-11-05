using System.Collections.Concurrent;
using Agritech.Core.Data;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;
using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

namespace Agritech.Simulation;

public sealed record ScenarioDefinition(string Name, Management Management, ClimateScenario? Climate = null, int ClimateYear = 2050);

public sealed record ScenarioSummary(string Name, int Runs, double MeanYield, double StdYield, double P10Yield, double P90Yield,
    double MeanIrrigationMm, double MeanNLeached, double MeanWaterProductivity, IReadOnlyList<SimulationResult> Results);

/// <summary>
/// Runs management × climate scenarios across many stochastic weather years in parallel (Monte-Carlo, like an APSIM factorial).
/// </summary>
public sealed class ScenarioRunner(CropDefinition crop, SoilSample soil, GeoCoordinate location, double elevationM = 100)
{
    public WeatherStation Station => new(location.Latitude, elevationM);

    public IReadOnlyList<ScenarioSummary> Run(IEnumerable<ScenarioDefinition> scenarios, int years = 20, int seed = 1,
        Func<int, DateOnly, DateOnly, IReadOnlyList<DailyWeather>>? weatherFactory = null)
    {
        weatherFactory ??= (yr, from, to) => new SyntheticWeatherProvider(seed + yr).Generate(location, from, to);
        var defs = scenarios.ToList();
        var results = new ConcurrentDictionary<(int Scenario, int Year), SimulationResult>();
        var sim = new CropSimulator(crop, soil, Station);
        Parallel.For(0, defs.Count * years, k =>
        {
            int s = k / years, y = k % years;
            var def = defs[s];
            var start = def.Management.PlantingDate;
            var wx = weatherFactory(y, start, start.AddDays(crop.SeasonLengthDays + 90));
            var mgmt = def.Management;
            if (def.Climate is { } cs)
            {
                var delta = ClimateProjection.Delta(cs, def.ClimateYear);
                wx = ClimateProjection.Apply(wx, delta);
                mgmt = mgmt with { Co2Ppm = delta.Co2Ppm };
            }
            results[(s, y)] = sim.Run(mgmt, wx);
        });
        return defs.Select((d, s) =>
        {
            var rs = Enumerable.Range(0, years).Select(y => results[(s, y)]).ToList();
            var yields = rs.Select(r => r.YieldTonHa).ToArray();
            return new ScenarioSummary(d.Name, years, Math.Round(Stats.Mean(yields), 3), Math.Round(Stats.StdDev(yields), 3),
                Math.Round(Stats.Percentile(yields, 10), 3), Math.Round(Stats.Percentile(yields, 90), 3),
                Math.Round(rs.Average(r => r.TotalIrrigationMm), 1), Math.Round(rs.Average(r => r.NLeachedKgHa), 2),
                Math.Round(rs.Average(r => r.WaterProductivityKgM3), 3), rs);
        }).ToList();
    }
}

public static class SimulationBuilderExtensions
{
    public static AgritechBuilder AddSimulation(this AgritechBuilder builder)
    {
        builder.TryAddModule("Simulation");
        return builder;
    }
}
