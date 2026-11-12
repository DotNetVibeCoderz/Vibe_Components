using System.Globalization;
using System.Text;
using AquaCulture.AI.Agent;
using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Data;
using AquaCulture.Disease;
using AquaCulture.Environment;
using AquaCulture.Feeding;
using AquaCulture.Forecasting;
using AquaCulture.Gallery.Shared;
using AquaCulture.Growth;
using AquaCulture.Hydrodynamic;
using AquaCulture.IoT;
using AquaCulture.IoT.OpcUa;
using AquaCulture.Native;
using AquaCulture.Reporting;
using AquaCulture.StockAssessment;
using AquaCulture.WaterQuality;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var opt = Options.Parse(args.Skip(1).ToArray());
string lang = opt.Str("lang", "en");
string T(string en, string id) => lang == "id" ? id : en;

try
{
    switch (cmd)
    {
        case "info": Info(); break;
        case "species": Species(); break;
        case "wq": Wq(); break;
        case "feed": Feed(); break;
        case "grow": Grow(); break;
        case "oxygen": Oxygen(); break;
        case "diagnose": Diagnose(); break;
        case "msy": Msy(); break;
        case "ypr": Ypr(); break;
        case "weather": await Weather(); break;
        case "bmkg": await Bmkg(); break;
        case "history": await History(); break;
        case "marine": await Marine(); break;
        case "export": await Export(); break;
        case "opcua": await OpcUa(); break;
        case "modbus": ModbusCmd(); break;
        case "lora": Lora(); break;
        case "ras": Ras(); break;
        case "audit": Audit(); break;
        case "report": Report(); break;
        case "ask": await Ask(); break;
        case "help" or "--help" or "-h": Help(); break;
        default: Console.Error.WriteLine($"Unknown command '{cmd}'."); Help(); return 1;
    }
    return 0;
}
catch (Exception e) when (e is ArgumentException or KeyNotFoundException or FormatException or InvalidOperationException or HttpRequestException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

void Help() => Console.WriteLine($"""
    aqua — {AquaInfo.Name} {AquaInfo.Version} command-line tool · {AquaInfo.Credit(lang)}

    Usage: aqua <command> [--option value] [--lang id]

      info                                   platform, native kernel and modules
      species [--id vaname]                  species database
      wq --species vaname --temp 30 --do 3.8 --ph 8.4 --tan 1.6 [--sal 18 --no2 0.5 --alk 100 --volume 2600 --biomass 3000]
                                             water-quality index, NH3, issues and dosed actions
      feed --species vaname --abw 12 --pop 200000 [--temp 29 --do 4.5]
                                             daily ration, meal schedule and best feed
      grow --species nila [--abw 5 --pop 10000 --temp 29 --do 5 --target 400 --sims 500]
                                             growth simulation, harvest date (Monte-Carlo) and optimal harvest
      oxygen [--biomass 3000 --volume 2600 --depth 1.3 --kw 4.4 --setpoint 4.5 --chl 120]
                                             36-hour pond oxygen budget and dawn minimum
      diagnose --species vaname --signs white_spots_shell,red_body [--temp 27 --doc 45]
      msy [--csv catch_effort.csv]           Schaefer/Fox surplus production (columns: year,catch,effort)
      ypr --winf 2000 --k 0.3 --t0 -0.2 --m 0.4 --tr 0.5 --tc 1.5
      weather --lat -6.33 --lon 108.32 [--days 3 --depth 1.3 --sal 18 --offline]
                                             Open-Meteo forecast and pond impact
      bmkg --adm4 31.71.03.1001 [--depth 1.3 --sal 0]
                                             BMKG village forecast (Indonesia) and pond impact
      history --lat -6.33 --lon 108.32 [--start 2015-01-01 --end 2024-12-31 --system EarthenPond]
                                             NASA POWER daily history and climate-risk ranking
      marine --lat -5.85 --lon 106.6 [--species kerapu --days 3]
                                             waves, currents and SST for floating net cages
      export --db aqua.db --pond P-01 --out readings.parquet|csv [--from 2026-11-01 --to 2026-11-12 --demo]
                                             export stored readings (--demo fills the db with a simulated day)
      opcua [--port 4841 --ponds P-01,P-02 --seconds 30]
                                             run the OPC UA pond simulator and print what a client reads
      modbus <hex frame> [--unit 1]          decode a Modbus RTU 0x03 response with the typical probe map
      lora <hex payload>                     decode a 12-byte pond payload or Cayenne LPP
      ras --feed 10 --protein 40 --volume 20 RAS mass balance (flow, biofilter, oxygen)
      audit --standard cbib|gap|asc|bap      print the self-assessment checklist
      report --format xlsx|pdf|docx|csv|json|html|md --out file   demo cycle report
      ask "Berapa pakan hari ini?"           AquaAgent (AQUA_LLM_* environment or offline)
    """);

void Info()
{
    Console.WriteLine($"{AquaInfo.Name} {AquaInfo.Version} — {(lang == "id" ? AquaInfo.TaglineId : AquaInfo.Tagline)}");
    Console.WriteLine(AquaInfo.Credit(lang));
    Console.WriteLine($"Native kernels: {AquaKernels.Backend} (version {AquaKernels.NativeVersion?.ToString() ?? "n/a"})");
    Console.WriteLine($"Species: {string.Join(", ", SpeciesCatalog.All.Select(s => s.Id))}");
    Console.WriteLine($"Diseases: {DiseaseKnowledgeBase.Diseases.Count} · water parameters: {WaterParameters.Info.Count}");
    Console.WriteLine($"LLM: {(LlmOptions.FromEnvironment() is { } o ? o.Provider + ":" + o.Model : "offline (rule-based)")}");
}

void Species()
{
    if (opt.Has("id"))
    {
        var s = SpeciesCatalog.Get(opt.Str("id"));
        Console.WriteLine($"{s.Name[lang]} — {s.ScientificName} ({s.Group}, {s.Habitat})");
        Console.WriteLine($"  temperature {s.Temperature.OptLow}–{s.Temperature.OptHigh} °C (tolerance {s.Temperature.Min}–{s.Temperature.Max})");
        Console.WriteLine($"  pH {s.Ph.OptLow}–{s.Ph.OptHigh} · salinity {s.Salinity.OptLow}–{s.Salinity.OptHigh} ppt · DO ≥ {s.DoOptimal} (critical {s.DoCritical}) mg/L");
        Console.WriteLine($"  NH3 ≤ {s.Nh3Max} · NO2-N ≤ {s.NitriteMax} · alkalinity {s.Alkalinity.OptLow}–{s.Alkalinity.OptHigh} mg/L");
        Console.WriteLine($"  TGC {s.Tgc} · market {s.MarketWeightG} g in ~{s.TypicalCycleDays} d · FCR {s.TypicalFcr} · SR {s.TypicalSurvival:P0} · Rp {s.PricePerKg:N0}/kg");
        return;
    }
    Console.WriteLine($"{"id",-8} {"name",-30} {"scientific",-28} {"market g",9} {"cycle d",8} {"FCR",5}");
    foreach (var s in SpeciesCatalog.All) Console.WriteLine($"{s.Id,-8} {s.Name[lang],-30} {s.ScientificName,-28} {s.MarketWeightG,9} {s.TypicalCycleDays,8} {s.TypicalFcr,5}");
}

void Wq()
{
    var s = SpeciesCatalog.Get(opt.Str("species", "vaname"));
    var sample = new WaterSample
    {
        TemperatureC = opt.NumOrNull("temp"), DissolvedOxygen = opt.NumOrNull("do"), Ph = opt.NumOrNull("ph"), Ammonia = opt.NumOrNull("tan"),
        SalinityPpt = opt.NumOrNull("sal"), Nitrite = opt.NumOrNull("no2"), Alkalinity = opt.NumOrNull("alk"), TurbidityNtu = opt.NumOrNull("ntu"), OrpMv = opt.NumOrNull("orp"),
    };
    var a = WaterQualityIndex.Assess(sample, s);
    Console.WriteLine($"WQI {a.Wqi:0} — {WaterQualityAssessment.ClassName(a.Class)[lang]} ({s.Name[lang]})");
    if (a.UnionizedAmmonia is { } nh3) Console.WriteLine($"NH3-N {nh3:0.000} mg/L (≤ {s.Nh3Max})");
    if (a.DoSaturationPct is { } sat) Console.WriteLine($"DO saturation {sat:0} %");
    foreach (var sc in a.Scores) Console.WriteLine($"  {WaterParameters.Info[sc.Parameter].Code,-5} {sc.Value,8:0.###}  score {sc.Score,5:0}  {WaterQualityAssessment.StatusName(sc.Status)[lang]}");
    foreach (var i in a.Issues) Console.WriteLine($"! {i[lang]}");
    Console.WriteLine(T("Actions:", "Tindakan:"));
    foreach (var r in RecommendationEngine.Recommend(a, new PondContext(opt.Num("volume", 2600), opt.Num("area", 2000), opt.Num("biomass", 0), opt.Num("kw", 0))))
        Console.WriteLine($"  {r.Priority}. {r.Action[lang]} — {r.Rationale[lang]}");
}

void Feed()
{
    var s = SpeciesCatalog.Get(opt.Str("species", "vaname"));
    var p = FeedingCalculator.Plan(s, opt.Num("abw", s.MarketWeightG / 2), opt.Num("pop", 100_000), opt.Num("temp", 29), opt.NumOrNull("do"), opt.NumOrNull("nh3"));
    Console.WriteLine($"{T("Daily ration", "Ransum harian")}: {p.DailyKg:0.00} kg ({p.RatePercentBw:0.00} % BW) · {p.Feed?.Name} · Rp {p.DailyCost:N0}");
    foreach (var m in p.Meals) Console.WriteLine($"  {m.Time:HH\\:mm}  {m.Kg:0.00} kg");
    foreach (var a in p.Adjustments) Console.WriteLine($"  * {a[lang]}");
}

void Grow()
{
    var s = SpeciesCatalog.Get(opt.Str("species", "nila"));
    double abw = opt.Num("abw", s.StockingWeightG), pop = opt.Num("pop", 10_000), temp = opt.Num("temp", 29), target = opt.Num("target", s.MarketWeightG);
    var sim = new GrowthSimulator(s);
    var traj = sim.Simulate(new StockState(s, DateOnly.FromDateTime(DateTime.Today), abw, pop, opt.Num("volume", pop * target / 1000 / s.MaxBiomassKgM3 * 1.5)),
        (int)(s.TypicalCycleDays * 1.4), new DailyEnvironment(temp, opt.Num("do", s.DoOptimal), 0));
    var hit = HarvestPredictor.AtWeight(traj, target, opt.Num("area", 1000), new HarvestEconomics(opt.Num("feedprice", 14_000)));
    var mc = HarvestForecaster.Forecast(s, DateOnly.FromDateTime(DateTime.Today), abw, pop, target, Enumerable.Repeat(temp, traj.Days.Count).ToArray(), (int)opt.Num("sims", 500));
    Console.WriteLine($"{s.Name[lang]}: {abw} g → {target} g at {temp} °C");
    Console.WriteLine($"  {T("reaches target", "mencapai target")} DOC {hit.DayOfCulture} ({hit.Date:yyyy-MM-dd}) · SR {hit.SurvivalRate:P0} · {hit.BiomassKg:N0} kg · FCR {hit.Fcr:0.00}");
    Console.WriteLine($"  Monte-Carlo P10/P50/P90: {mc.DayP10}/{mc.DayP50}/{mc.DayP90} {T("days", "hari")} ({mc.Simulations} runs, {mc.Backend} kernel)");
    foreach (var d in traj.Days.Where(d => d.Day % 15 == 0)) Console.WriteLine($"  DOC {d.Day,4}  ABW {d.AbwG,8:0.0} g  biomass {d.BiomassKg,9:N0} kg  feed {d.CumulativeFeedKg,9:N0} kg");
}

void Oxygen()
{
    var p = new OxygenBudgetParameters { VolumeM3 = opt.Num("volume", 2600), DepthM = opt.Num("depth", 1.3), BiomassKg = opt.Num("biomass", 3000), MeanWeightG = opt.Num("abw", 12),
        Group = opt.Str("group", "shrimp") == "fish" ? SpeciesGroup.Fish : SpeciesGroup.Shrimp, ChlorophyllUgL = opt.Num("chl", 120), SalinityPpt = opt.Num("sal", 18), AeratorKw = opt.Num("kw", 4.4) };
    var w = SyntheticWeather.Generate(-6.3, DateTime.Today, 2, (ulong)opt.Num("seed", 12)).Skip(12).Take(36).ToList();
    var off = PondOxygenBudget.Simulate(p, w, opt.Num("initial", 6.5));
    var on = PondOxygenBudget.Simulate(p, w, opt.Num("initial", 6.5), PondOxygenBudget.SetPoint(opt.Num("setpoint", 4.5)));
    Console.WriteLine($"{T("No aeration", "Tanpa aerasi")}: min {off.MinDo:0.00} mg/L at {off.MinTime:HH:mm}");
    Console.WriteLine($"{T("Set-point aeration", "Aerasi set-point")}: min {on.MinDo:0.00} mg/L, {on.AeratorKwh:0} kWh");
    foreach (var (a, b) in off.Hours.Zip(on.Hours).Where((_, i) => i % 3 == 0)) Console.WriteLine($"  {a.Time:HH:mm}  {a.Do,6:0.00}  {b.Do,6:0.00}{(b.AeratorOn ? "  ⚙" : "")}");
}

void Diagnose()
{
    var s = SpeciesCatalog.Get(opt.Str("species", "vaname"));
    var signs = opt.Str("signs", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (signs.Length == 0) { Console.WriteLine(T("Signs:", "Gejala:")); foreach (var x in DiseaseKnowledgeBase.SymptomsFor(s)) Console.WriteLine($"  {x.Id,-24} {x.Name[lang]}"); return; }
    var r = DiagnosisEngine.Diagnose(s, signs, new PondConditions { TemperatureC = opt.NumOrNull("temp"), DayOfCulture = opt.NumOrNull("doc") is { } d ? (int)d : null });
    foreach (var c in r.Candidates.Take(5)) Console.WriteLine($"  {c.Probability,6:P0}  {c.Disease.Name[lang]}{(c.Disease.Notifiable ? " [notifiable]" : "")}");
    Console.WriteLine(r.Advice[lang]);
}

void Msy()
{
    IReadOnlyList<CatchEffort> data = opt.Has("csv")
        ? File.ReadAllLines(opt.Str("csv")).Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Split(',')).Select(p => new CatchEffort(int.Parse(p[0]), double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture))).ToList()
        : StockDemo.Fishery();
    var fit = SurplusProduction.FitDynamic(data);
    var sch = SurplusProduction.Schaefer(data); var fox = SurplusProduction.Fox(data);
    Console.WriteLine($"Dynamic Schaefer: r {fit.R:0.000}, K {fit.K:N0}, MSY {fit.Msy:N0}, Bmsy {fit.Bmsy:N0}, Fmsy {fit.Fmsy:0.000}");
    Console.WriteLine($"  B/Bmsy {fit.BOverBmsy:0.00} · F/Fmsy {fit.FOverFmsy:0.00} → {DynamicSchaeferFit.StatusName(fit.Status)[lang]}");
    Console.WriteLine($"Schaefer (Gulland): MSY {sch.Msy:N0} at E {sch.EffortAtMsy:N0} (R² {sch.RSquared:0.00})");
    Console.WriteLine($"Fox: MSY {fox.Msy:N0} at E {fox.EffortAtMsy:N0} (R² {fox.RSquared:0.00})");
}

void Ypr()
{
    var p = new YprParameters(opt.Num("winf", 2000), opt.Num("k", 0.3), opt.Num("t0", -0.2), opt.Num("m", 0.4), opt.Num("tr", 0.5), opt.Num("tc", 1.5));
    var r = YieldPerRecruit.Analyze(p);
    Console.WriteLine($"Fmax {r.Fmax:0.000} (Y/R {r.YprAtFmax:0.0} g) · F0.1 {r.F01:0.000} (Y/R {r.YprAtF01:0.0} g) · F40% {r.F40:0.000}");
}

async Task Weather()
{
    double lat = opt.Num("lat", -6.333), lon = opt.Num("lon", 108.321);
    int days = (int)opt.Num("days", 3);
    IReadOnlyList<WeatherObservation> w;
    if (opt.Has("offline")) w = SyntheticWeather.Generate(lat, DateTime.Today, days);
    else
    {
        try { w = await new OpenMeteoClient().GetHourlyForecastAsync(lat, lon, days); Console.WriteLine($"Open-Meteo: {w.Count} hourly records"); }
        catch (HttpRequestException e) { Console.WriteLine($"Open-Meteo unavailable ({e.Message}); using synthetic weather."); w = SyntheticWeather.Generate(lat, DateTime.Today, days); }
    }
    var r = WeatherImpactPredictor.Predict(w, new PondExposure(opt.Num("depth", 1.3), opt.Num("sal", 0)));
    Console.WriteLine($"{T("Water", "Air")} {r.WaterTempMinC}–{r.WaterTempMaxC} °C · {T("rain", "hujan")} {r.TotalRainMm} mm · {T("light", "cahaya")} {r.PhotosynthesisIndex:P0} · wind max {r.MaxWindMs} m/s");
    foreach (var i in r.Impacts) Console.WriteLine($"  [{i.Severity}] {i.Effect[lang]} → {i.Advice[lang]}");
    if (r.Impacts.Count == 0) Console.WriteLine(T("  No significant risk.", "  Tidak ada risiko berarti."));
}

async Task Bmkg()
{
    var f = await new BmkgClient().GetForecastAsync(opt.Str("adm4", "31.71.03.1001"));
    Console.WriteLine($"BMKG {f.Location.Village}, {f.Location.District}, {f.Location.Regency} ({f.Location.Latitude:0.###}, {f.Location.Longitude:0.###}) · {f.Hourly.Count} × 3 h");
    foreach (var (t, d) in f.Conditions.Take(8)) Console.WriteLine($"  {t:yyyy-MM-dd HH:mm}  {d[lang]}");
    var hourly = BmkgClient.ToHourly(f.Hourly, f.Location.Latitude);
    var r = WeatherImpactPredictor.Predict(hourly, new PondExposure(opt.Num("depth", 1.3), opt.Num("sal", 0)));
    Console.WriteLine($"{T("Water", "Air")} {r.WaterTempMinC}–{r.WaterTempMaxC} °C · {T("rain", "hujan")} {r.TotalRainMm} mm · wind max {r.MaxWindMs} m/s");
    foreach (var i in r.Impacts) Console.WriteLine($"  [{i.Severity}] {i.Effect[lang]} → {i.Advice[lang]}");
    if (r.Impacts.Count == 0) Console.WriteLine(T("  No significant risk.", "  Tidak ada risiko berarti."));
}

async Task History()
{
    double lat = opt.Num("lat", -6.333), lon = opt.Num("lon", 108.321);
    var end = opt.Has("end") ? DateOnly.Parse(opt.Str("end"), CultureInfo.InvariantCulture) : new DateOnly(DateTime.Today.Year - 1, 12, 31);
    var start = opt.Has("start") ? DateOnly.Parse(opt.Str("start"), CultureInfo.InvariantCulture) : new DateOnly(end.Year - 9, 1, 1);
    var days = await new NasaPowerClient().GetDailyAsync(lat, lon, start, end);
    var system = Enum.Parse<AquaCulture.Core.Farming.CultureSystem>(opt.Str("system", "EarthenPond"), true);
    var risk = ClimateRiskAnalysis.Analyze(days, system);
    double meanT = days.Where(d => !double.IsNaN(d.TmeanC)).Average(d => d.TmeanC);
    double rain = days.Where(d => !double.IsNaN(d.RainMm)).Sum(d => d.RainMm) / Math.Max(1, risk.Years);
    Console.WriteLine($"NASA POWER {start:yyyy-MM-dd} … {end:yyyy-MM-dd}: {days.Count} {T("days", "hari")} · {T("mean", "rata-rata")} {meanT:0.0} °C · {T("rain", "hujan")} {rain:0} mm/{T("year", "tahun")}");
    foreach (var h in risk.Hazards) Console.WriteLine($"  {h.Score,2} {h.Level[lang],-14} {h.Name[lang]} ({h.EventsPerYear:0.#}/{T("yr", "thn")}) → {h.Adaptation[lang]}");
}

async Task Marine()
{
    var sea = await new MarineForecastClient().GetHourlyAsync(opt.Num("lat", -5.85), opt.Num("lon", 106.6), (int)opt.Num("days", 3));
    var r = MarineForecastClient.Assess(sea, SpeciesCatalog.Get(opt.Str("species", "kerapu")));
    Console.WriteLine($"Open-Meteo Marine: {sea.Count} h · {T("waves", "gelombang")} max {r.MaxWaveM:0.0} m · {T("current", "arus")} max {r.MaxCurrentMs:0.00} m/s · SST {r.MeanSstC:0.0} °C");
    foreach (var i in r.Impacts) Console.WriteLine($"  [{i.Severity}] {i.Effect[lang]} → {i.Advice[lang]}");
    if (r.Impacts.Count == 0) Console.WriteLine(T("  Safe for cage work.", "  Aman untuk kegiatan KJA."));
}

async Task Export()
{
    string db = opt.Str("db", "aqua.db"), pond = opt.Str("pond", "P-01"), outPath = opt.Str("out", "readings.parquet");
    using var store = AquaStore.OpenSqlite(db);
    if (opt.Has("demo"))
    {
        var sim = new SimulatedSensorTransport([new SimulatedPond(pond, "probe-" + pond)]);
        var t0 = DateTime.UtcNow.Date;
        var rows = Enumerable.Range(0, 144).SelectMany(i => sim.Emit(t0.AddMinutes(10 * i))).ToList();
        Console.WriteLine($"{await store.AppendReadingsAsync(rows)} {T("simulated readings stored in", "pembacaan simulasi disimpan di")} {db}");
    }
    var styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
    DateTime? from = opt.Has("from") ? DateTime.Parse(opt.Str("from"), CultureInfo.InvariantCulture, styles) : null;
    DateTime? to = opt.Has("to") ? DateTime.Parse(opt.Str("to"), CultureInfo.InvariantCulture, styles) : null;
    var readings = await store.QueryReadingsAsync(pond, null, from, to);
    if (outPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(outPath, TimeSeriesExport.WriteCsv(readings));
    else { await using var fs = File.Create(outPath); await TimeSeriesExport.WriteParquetAsync(readings, fs); }
    Console.WriteLine($"{outPath}: {readings.Count} {T("readings", "pembacaan")} ({new FileInfo(outPath).Length:N0} bytes)");
}

async Task OpcUa()
{
    var ponds = opt.Str("ponds", "P-01,P-02").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    await using var sim = new PondOpcUaSimulator(ponds, (int)opt.Num("port", 4841));
    await sim.StartAsync();
    Console.WriteLine($"OPC UA {T("pond simulator at", "simulator kolam di")} {sim.EndpointUrl}");
    await using var reader = new OpcUaReader(new OpcUaReaderOptions(sim.EndpointUrl));
    await reader.ConnectAsync();
    var poller = new OpcUaSensorPoller(reader, sim.NodeMap());
    var stop = DateTime.UtcNow.AddSeconds(opt.Num("seconds", 30));
    while (DateTime.UtcNow < stop)
    {
        var r = await poller.PollAsync();
        Console.WriteLine(string.Join("  |  ", r.GroupBy(x => x.PondId).Select(g => $"{g.Key}: " + string.Join(" ", g.Select(x => $"{x.Kind}={x.Value:0.##}")))));
        await Task.Delay(1000);
    }
}

void ModbusCmd()
{
    var frame = Convert.FromHexString(string.Concat(opt.Positional.Select(x => x.Replace(" ", ""))));
    var regs = Modbus.ParseReadResponseRtu(frame, (byte)opt.Num("unit", frame.Length > 0 ? frame[0] : 1));
    foreach (var (k, v) in Modbus.Decode(regs, 0, Modbus.TypicalProbeMap)) Console.WriteLine($"  {k,-16} {v:0.###}");
}

void Lora()
{
    var b = Convert.FromHexString(string.Concat(opt.Positional.Select(x => x.Replace(" ", ""))));
    var r = b.Length == 12 ? LoRaPayload.DecodePond(b, "node", "pond", DateTime.UtcNow) : LoRaPayload.CayenneToReadings(LoRaPayload.DecodeCayenne(b), "node", "pond", DateTime.UtcNow);
    foreach (var x in r) Console.WriteLine($"  {x.Kind,-16} {x.Value:0.###} {x.Unit}");
}

void Ras()
{
    var d = RasMassBalance.Design(opt.Num("feed", 10), opt.Num("protein", 40), opt.Num("volume", 20), opt.Num("tan", 1), opt.Num("eff", 0.5));
    Console.WriteLine($"TAN {d.TanProductionGPerDay:0} g/day · O2 {d.OxygenDemandKgPerDay:0.0} kg/day · CO2 {d.Co2ProductionKgPerDay:0.0} kg/day · solids {d.SolidsKgPerDay:0.0} kg/day");
    Console.WriteLine($"Flow {d.FlowM3PerHour:0.0} m³/h ({d.TurnoversPerHour:0.0} turnovers/h) · biofilter {d.BiofilterAreaM2:0} m² ({d.MediaVolumeM3:0.00} m³ media) · make-up {d.MakeupWaterM3PerDay:0.0} m³/day");
}

void Audit()
{
    var std = Enum.Parse<ComplianceStandard>(opt.Str("standard", "cbib"), true);
    foreach (var i in ComplianceAudit.Checklist(std)) Console.WriteLine($"  {i.Id,-8} [{i.Severity,-8}] {i.Category[lang]}: {i.Requirement[lang]}");
}

void Report()
{
    string fmt = opt.Str("format", "xlsx").ToLowerInvariant();
    var r = ReportingDemo.Run(ComplianceStandard.Cbib, null, lang);
    var f = r.Files.FirstOrDefault(x => x.Name.EndsWith("." + fmt)) ?? throw new ArgumentException($"Unknown format '{fmt}'.");
    string outPath = opt.Str("out", Path.GetFileName(f.Name));
    File.WriteAllBytes(outPath, f.Bytes);
    Console.WriteLine($"{outPath} ({f.Bytes.Length:N0} bytes)");
}

async Task Ask()
{
    string q = string.Join(' ', opt.Positional);
    if (string.IsNullOrWhiteSpace(q)) throw new ArgumentException("Usage: aqua ask \"question\"");
    var agent = AgentDemo.Create();
    Console.WriteLine($"[{agent.Model.Name}]");
    var a = await agent.AskAsync(q);
    Console.WriteLine(a.Text);
    Console.WriteLine($"(tools: {string.Join(", ", a.ToolsUsed)})");
}

/// <summary>Minimal --key value parser.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = [];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string k = args[i][2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o._kv[k] = args[++i]; else o._kv[k] = "true";
            }
            else o.Positional.Add(args[i]);
        }
        return o;
    }

    public bool Has(string k) => _kv.ContainsKey(k);
    public string Str(string k, string def = "") => _kv.TryGetValue(k, out var v) ? v : def;
    public double Num(string k, double def) => NumOrNull(k) ?? def;
    public double? NumOrNull(string k) => _kv.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : null;
}
