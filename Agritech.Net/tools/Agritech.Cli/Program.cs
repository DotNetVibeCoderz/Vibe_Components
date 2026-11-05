using System.Globalization;
using Agritech.AI;
using Agritech.AI.Agent;
using Agritech.Core;
using Agritech.Core.Geo;
using Agritech.Crops;
using Agritech.Drones;
using Agritech.GIS;
using Agritech.IoT;
using Agritech.Livestock;
using Agritech.Native;
using Agritech.RemoteSensing;
using Agritech.Simulation;
using Agritech.Soil;
using Agritech.Visualization;
using Agritech.Weather;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var cli = new Args(args);
try
{
    return await (cli.Command switch
    {
        "info" or "" => Info(),
        "crops" => Crops(cli),
        "soil" => Soil(cli),
        "et0" => Et0(cli),
        "weather" => WeatherAsync(cli),
        "ndvi" => Ndvi(cli),
        "mission" => Mission(cli),
        "simulate" => Simulate(cli),
        "ask" => AskAsync(cli),
        "lpp" => Lpp(cli),
        "thi" => Thi(cli),
        "help" or "--help" or "-h" => Help(),
        _ => Fail($"Unknown command '{cli.Command}'. Run 'agritech help'."),
    });
}
catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or HttpRequestException or InvalidOperationException)
{
    return await Fail(ex.Message);
}

static Task<int> Fail(string msg)
{
    Console.Error.WriteLine($"error: {msg}");
    return Task.FromResult(1);
}

static Task<int> Help()
{
    Console.WriteLine($"""
        {AgritechInfo.Name} CLI {AgritechInfo.Version} — {AgritechInfo.Tagline}
        {AgritechInfo.CreditEn}

        Usage: agritech <command> [options]

          info                                   Framework, modules and native kernel status
          crops list                             Crop catalog
          crops recommend --temp 26 --rain 1400 --ph 5.6 [--elev 300] [--irrigated]
          soil --ph 5.4 --oc 1.2 --n 0.12 --p 9 --k 0.28 --sand 22 --silt 38 --clay 40 [--crop rice --area 2 --target 6.5]
          et0 --lat -6.3 --tmin 23 --tmax 32 [--rh 80 --wind 2 --rs 20 --elev 18 --date 2026-09-14]
          weather --lat -6.3 --lon 107.3 [--days 7] [--offline]
          ndvi [--seed 7] [--out ndvi.png]        Synthetic Sentinel-2 field → NDVI PNG + stats
          mission --lat -6.3 --lon 107.3 --width 300 --height 200 [--alt 80] [--field field.geojson] [--out field.plan]
          simulate --crop maize [--planting 2026-04-01] [--irrigated] [--n 120] [--years 10]
          ask "Berapa dosis pupuk terbaik minggu ini?" [--crop rice --dap 20 --area 2]
          lpp 03670110056700FF                   Decode a Cayenne LPP (LoRaWAN) payload
          thi --temp 33 --rh 70                  Livestock temperature-humidity index

        LLM for 'ask': set AGRITECH_LLM_PROVIDER (openai|azure|hf|ollama), AGRITECH_LLM_ENDPOINT, AGRITECH_LLM_API_KEY, AGRITECH_LLM_MODEL.
        """);
    return Task.FromResult(0);
}

static Task<int> Info()
{
    Console.WriteLine($"{AgritechInfo.Name} {AgritechInfo.Version} — {AgritechInfo.Tagline}");
    Console.WriteLine(AgritechInfo.CreditEn + " / " + AgritechInfo.CreditId);
    Console.WriteLine($"Modules: {string.Join(", ", AgritechInfo.Modules)}");
    Console.WriteLine($"Native kernels (Rust): {(AgriKernels.IsNativeAvailable ? "available" : "not found — using managed SIMD")}; active backend: {AgriKernels.ActiveBackend}");
    Console.WriteLine($"Crops in catalog: {CropCatalog.Default.All.Count}");
    Console.WriteLine($"LLM: {(LlmOptions.FromEnvironment() is { } o ? $"{o.Provider}:{o.Model}" : "not configured (agent runs offline)")}");
    return Task.FromResult(0);
}

static Task<int> Crops(Args a)
{
    if (a.Positional(1) == "list")
    {
        Console.WriteLine($"{"Id",-10} {"Name",-20} {"Nama",-16} {"Season",7} {"Ymax t/ha",10}");
        foreach (var c in CropCatalog.Default.All.OrderBy(c => c.Category).ThenBy(c => c.Id))
            Console.WriteLine($"{c.Id,-10} {c.Name,-20} {c.NameId,-16} {c.SeasonLengthDays,5} d {c.PotentialYieldTonHa,10:0.#}");
        return Task.FromResult(0);
    }
    var site = new SiteConditions(a.D("temp", 26.5), a.D("rain", 1400), a.D("ph", 6), a.D("elev", 100), a.Flag("irrigated"));
    Console.WriteLine($"Site: {site.MeanTempC} °C, {site.SeasonalRainfallMm} mm, pH {site.SoilPh}, {site.ElevationM} m{(site.IrrigationAvailable ? ", irrigated" : "")}");
    int i = 1;
    foreach (var s in new CropRecommender().Recommend(site, (int)a.D("top", 8)))
        Console.WriteLine($"{i++,2}. {s.Crop.Name,-20} {s.Class,-3} {s.Score * 100,5:0}%  {string.Join("; ", s.LimitingFactors)}");
    return Task.FromResult(0);
}

static Task<int> Soil(Args a)
{
    var s = new SoilSample
    {
        Ph = a.D("ph", 6.5), OrganicCarbonPct = a.D("oc", 1.5), TotalNitrogenPct = a.D("n", 0.15), AvailablePMgKg = a.D("p", 15),
        ExchangeableKCmolKg = a.D("k", 0.4), CecCmolKg = a.D("cec", 15), EcDsM = a.D("ec", 0.5), SandPct = a.D("sand", 40), SiltPct = a.D("silt", 40),
        ClayPct = a.D("clay", 20), BulkDensity = a.D("bd", 1.3),
    };
    var r = SoilHealth.Assess(s);
    var h = s.Hydraulics();
    Console.WriteLine($"Soil health: {r.Score}/100 — {r.Grade}   Texture: {s.Texture}   FC {h.FieldCapacity:P1}  WP {h.WiltingPoint:P1}  AWC {h.AvailableWaterMmPerM:0} mm/m");
    foreach (var i in r.Indicators) Console.WriteLine($"  {i.Indicator,-16} {i.Value,8:0.###}  {i.Interpretation,-16} score {i.Score * 100,3:0}");
    foreach (var rec in r.Recommendations) Console.WriteLine($"  → {rec}");
    if (a.S("crop") is { } cropId)
    {
        var adv = FertilizerAdvisor.Advise(CropCatalog.Default.Get(cropId), s, a.D("area", 1), a.Has("target") ? a.D("target", 0) : null);
        Console.WriteLine($"\nFertilizer for {adv.Crop.Name}, target {adv.TargetYieldTonHa} t/ha on {adv.AreaHa} ha (N {adv.Need.N}, P2O5 {adv.Need.P2O5}, K2O {adv.Need.K2O} kg/ha):");
        foreach (var app in adv.Schedule)
            Console.WriteLine($"  {app.Stage,-26} {app.FromDap,3}-{app.ToDap,-3} DAP  " + string.Join(", ", app.Products.Select(p => $"{p.Product.Name} {p.KgPerHa} kg/ha ({p.TotalKg} kg)")));
        Console.WriteLine($"  Estimated cost: Rp {adv.Plan.TotalCost:N0}");
    }
    return Task.FromResult(0);
}

static Task<int> Et0(Args a)
{
    var date = a.S("date") is { } ds ? DateOnly.Parse(ds, CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Today);
    var w = new DailyWeather(date, a.D("tmin", 23), a.D("tmax", 32), 0, a.Has("rh") ? a.D("rh", 0) : null, a.Has("wind") ? a.D("wind", 0) : null, a.Has("rs") ? a.D("rs", 0) : null);
    var st = new WeatherStation(a.D("lat", -6.3), a.D("elev", 50));
    Console.WriteLine($"ET0 Penman-Monteith (FAO-56): {Evapotranspiration.PenmanMonteith(w, st):0.00} mm/day");
    Console.WriteLine($"ET0 Hargreaves:               {Evapotranspiration.Hargreaves(w.TminC, w.TmaxC, st.LatitudeDeg, w.DayOfYear):0.00} mm/day");
    Console.WriteLine($"Ra {Evapotranspiration.ExtraterrestrialRadiation(st.LatitudeDeg, w.DayOfYear):0.0} MJ/m²/day, daylight {Evapotranspiration.DaylightHours(st.LatitudeDeg, w.DayOfYear):0.0} h");
    return Task.FromResult(0);
}

static async Task<int> WeatherAsync(Args a)
{
    var loc = new GeoCoordinate(a.D("lat", -6.3), a.D("lon", 107.3));
    IWeatherProvider p = a.Flag("offline") ? new SyntheticWeatherProvider() : new OpenMeteoWeatherProvider();
    var days = await p.GetForecastAsync(loc, (int)a.D("days", 7));
    var st = new WeatherStation(loc.Latitude, a.D("elev", 50));
    Console.WriteLine($"{p.Name} forecast for {loc} (UTM {Projections.ToUtm(loc)}):");
    foreach (var d in days)
        Console.WriteLine($"  {d.Date:ddd dd MMM}  {d.TminC,5:0.0}–{d.TmaxC,4:0.0} °C  rain {d.RainMm,5:0.0} mm  RH {d.RhMeanPct,3:0}%  ET0 {(d.Et0Mm ?? Evapotranspiration.PenmanMonteith(d, st)),4:0.0} mm");
    return 0;
}

static Task<int> Ndvi(Args a)
{
    var field = GeoPolygon.Rectangle(new GeoCoordinate(a.D("lat", -6.3045), a.D("lon", 107.3012)), a.D("width", 300), a.D("height", 220));
    var img = SyntheticScene.Create(field.Bounds, a.D("res", 10), (int)a.D("seed", 7));
    var ndvi = VegetationIndices.Ndvi(img);
    var z = RasterAnalysis.Zonal(ndvi, field, "field");
    Console.WriteLine($"Scene {img.SceneId}: {ndvi.Width}×{ndvi.Height} px, backend {AgriKernels.ActiveBackend}");
    Console.WriteLine($"NDVI mean {z.Stats.Mean:0.000} ± {z.Stats.StdDev:0.000} (min {z.Stats.Min:0.00}, max {z.Stats.Max:0.00}), {z.PixelCount} px");
    foreach (var kv in z.HealthShare.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key,-16} {kv.Value,6:0.0}%");
    var outFile = a.S("out") ?? "ndvi.png";
    File.WriteAllBytes(outFile, PngEncoder.FromRaster(ndvi.Data, ndvi.Width, ndvi.Height, ColorMap.Ndvi, 4));
    Console.WriteLine($"Saved {Path.GetFullPath(outFile)}");
    return Task.FromResult(0);
}

static Task<int> Mission(Args a)
{
    var center = new GeoCoordinate(a.D("lat", -6.3045), a.D("lon", 107.3012));
    GeoPolygon field = a.S("field") is { } path
        ? GeoJson.ReadFeatures(File.ReadAllText(path)).Select(f => f.Geometry).OfType<PolygonGeometry>().First().Polygon
        : GeoPolygon.Rectangle(center, a.D("width", 300), a.D("height", 200));
    var plan = MissionPlanner.PlanSurvey(field, CameraSpec.DjiMavic3M, a.D("alt", 80), a.D("front", 0.75), a.D("side", 0.7));
    Console.WriteLine($"Field {field.AreaHectares:0.00} ha · GSD {plan.GsdCm} cm/px · {plan.Waypoints.Count} waypoints · {plan.LineSpacingM} m spacing");
    Console.WriteLine($"Distance {plan.TotalDistanceM / 1000:0.00} km · {plan.FlightTimeMin} min · {plan.EstimatedPhotos} photos · {plan.BatteriesRequired} battery(ies) · heading {plan.HeadingDeg}°");
    var outFile = a.S("out") ?? "mission.plan";
    File.WriteAllText(outFile, plan.ToQGroundControlPlan(field.Centroid));
    Console.WriteLine($"QGroundControl plan saved to {Path.GetFullPath(outFile)}");
    return Task.FromResult(0);
}

static Task<int> Simulate(Args a)
{
    var crop = CropCatalog.Default.Get(a.S("crop") ?? "maize");
    var planting = a.S("planting") is { } p ? DateOnly.Parse(p, CultureInfo.InvariantCulture) : new DateOnly(DateTime.Today.Year, 4, 1);
    var loc = new GeoCoordinate(a.D("lat", -6.3), a.D("lon", 107.3));
    double n = a.D("n", 120);
    var mgmt = new Management
    {
        PlantingDate = planting, Irrigation = a.Flag("irrigated") ? IrrigationStrategy.AutoAtRaw : IrrigationStrategy.Rainfed,
        NitrogenApplications = [(0, n * 0.3), (25, n * 0.4), (45, n * 0.3)],
    };
    var soil = new SoilSample { SandPct = a.D("sand", 30), SiltPct = a.D("silt", 40), ClayPct = a.D("clay", 30), OrganicCarbonPct = a.D("oc", 1.3) };
    var summary = new ScenarioRunner(crop, soil, loc, a.D("elev", 50)).Run(
    [
        new ScenarioDefinition("baseline", mgmt),
        new ScenarioDefinition("SSP2-4.5 2050", mgmt, ClimateScenario.Ssp245, 2050),
        new ScenarioDefinition("SSP5-8.5 2050", mgmt, ClimateScenario.Ssp585, 2050),
    ], (int)a.D("years", 10));
    Console.WriteLine($"{crop.Name} planted {planting:yyyy-MM-dd}, {mgmt.Irrigation}, N {n} kg/ha, {summary[0].Runs} stochastic years:");
    foreach (var s in summary)
        Console.WriteLine($"  {s.Name,-16} yield {s.MeanYield,6:0.00} ± {s.StdYield:0.00} t/ha  (P10 {s.P10Yield:0.0}, P90 {s.P90Yield:0.0})  irrigation {s.MeanIrrigationMm,5:0} mm  N leached {s.MeanNLeached:0.0} kg/ha");
    return Task.FromResult(0);
}

static async Task<int> AskAsync(Args a)
{
    var q = a.Positional(1) ?? throw new ArgumentException("Provide a question, e.g. agritech ask \"When should I irrigate?\"");
    var agent = new FarmAgent(new FarmContext
    {
        CropId = a.S("crop") ?? "rice", AreaHa = a.D("area", 1), PlantingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-(int)a.D("dap", 20)),
        Location = new GeoCoordinate(a.D("lat", -6.3045), a.D("lon", 107.3012)),
        Weather = a.Flag("live-weather") ? new OpenMeteoWeatherProvider() : new SyntheticWeatherProvider(),
    });
    var ans = await agent.AskAsync(q);
    Console.WriteLine(ans.Text);
    Console.WriteLine($"\n[{ans.Model} · tools: {string.Join(", ", ans.ToolsUsed.Distinct())}]");
    return 0;
}

static Task<int> Lpp(Args a)
{
    var hex = a.Positional(1) ?? throw new ArgumentException("Provide a hex payload.");
    foreach (var v in CayenneLpp.Decode(Convert.FromHexString(hex.Replace(" ", ""))))
        Console.WriteLine($"ch {v.Channel,3}  {v.Name,-20} {string.Join(", ", v.Values.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture)))}");
    return Task.FromResult(0);
}

static Task<int> Thi(Args a)
{
    double thi = LivestockMath.Thi(a.D("temp", 30), a.D("rh", 70));
    Console.WriteLine($"THI {thi:0.0} → {LivestockMath.HeatStress(thi)}; estimated milk loss {LivestockMath.MilkLossKg(thi):0.0} kg/cow/day");
    return Task.FromResult(0);
}

/// <summary>Minimal GNU-style argument parser: positional words, --key value, --flag.</summary>
internal sealed class Args
{
    private readonly List<string> _pos = [];
    private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                string key = args[i][2..];
                bool hasValue = i + 1 < args.Length && (!args[i + 1].StartsWith("--", StringComparison.Ordinal) || double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                _opts[key] = hasValue ? args[++i] : null;
            }
            else _pos.Add(args[i]);
        }
    }

    public string Command => _pos.Count > 0 ? _pos[0].ToLowerInvariant() : "";
    public string? Positional(int i) => i < _pos.Count ? _pos[i] : null;
    public bool Has(string k) => _opts.ContainsKey(k);
    public bool Flag(string k) => _opts.ContainsKey(k);
    public string? S(string k) => _opts.TryGetValue(k, out var v) ? v : null;

    public double D(string k, double def) => _opts.TryGetValue(k, out var v) && v is not null
        ? double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException($"--{k} expects a number, got '{v}'.")
        : def;
}
