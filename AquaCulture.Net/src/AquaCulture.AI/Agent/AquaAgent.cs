using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Disease;
using AquaCulture.Environment;
using AquaCulture.Feeding;
using AquaCulture.Forecasting;
using AquaCulture.Growth;
using AquaCulture.Hydrodynamic;
using AquaCulture.WaterQuality;

namespace AquaCulture.AI.Agent;

/// <summary>What the agent knows about the pond it is advising on.</summary>
public sealed class AquaContext
{
    public SpeciesProfile Species { get; set; } = SpeciesCatalog.Vaname;
    public string PondName { get; set; } = "Pond 1";
    public double AreaM2 { get; set; } = 2000;
    public double DepthM { get; set; } = 1.3;
    public CultureSystem System { get; set; } = CultureSystem.LinedPond;
    public double AerationKw { get; set; } = 6;
    public DateOnly StockingDate { get; set; } = DateOnly.FromDateTime(DateTime.Today).AddDays(-60);
    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public long Population { get; set; } = 180_000;
    public double AbwG { get; set; } = 12;
    public double FeedPricePerKg { get; set; } = 17_000;
    public double? SalePricePerKg { get; set; }
    public WaterSample Water { get; set; } = new() { TemperatureC = 29.5, DissolvedOxygen = 4.6, Ph = 8.1, SalinityPpt = 18, Ammonia = 0.8, Nitrite = 0.4, Alkalinity = 130, TurbidityNtu = 40 };
    /// <summary>Recent DO readings (for the overnight forecast).</summary>
    public TimeSeries? RecentDo { get; set; }
    /// <summary>Hourly weather forecast for the coming days.</summary>
    public IReadOnlyList<WeatherObservation>? Weather { get; set; }
    public double VolumeM3 => AreaM2 * DepthM;
    public int DayOfCulture => Today.DayNumber - StockingDate.DayNumber;
    public double BiomassKg => Population * AbwG / 1000;
}

/// <summary>Agent reply with the tools it used.</summary>
public sealed record AgentAnswer(string Text, IReadOnlyList<string> ToolsUsed, string Model, string Language);

/// <summary>Grounded tools the agent can call. Every number in an answer comes from AquaCulture.Net calculations.</summary>
public static class AquaTools
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Empty = """{"type":"object","properties":{}}""";

    public static IReadOnlyList<AgentTool> Create(AquaContext c, Func<string> lang)
    {
        string T(string en, string id) => lang() == "id" ? id : en;
        double? Num(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

        return
        [
            new("pond_overview", "Summary of the pond: species, day of culture, stock, biomass and the latest water readings.", Empty, _ =>
            {
                var a = WaterQualityIndex.Assess(c.Water, c.Species);
                return T($"{c.PondName}: {c.Species.Name.En}, DOC {c.DayOfCulture}, {c.Population:N0} animals × {c.AbwG:0.#} g = {c.BiomassKg:N0} kg biomass in {c.AreaM2:N0} m² × {c.DepthM} m ({c.System}). Water: WQI {a.Wqi:0} ({WaterQualityAssessment.ClassName(a.Class).En}).",
                         $"{c.PondName}: {c.Species.Name.Id}, DOC {c.DayOfCulture}, {c.Population:N0} ekor × {c.AbwG:0.#} g = biomassa {c.BiomassKg:N0} kg di {c.AreaM2:N0} m² × {c.DepthM} m ({c.System}). Air: WQI {a.Wqi:0} ({WaterQualityAssessment.ClassName(a.Class).Id}).");
            }),
            new("water_quality_check", "Assess water quality for the species and list prioritised actions with doses. Optional overrides of the latest readings.",
                """{"type":"object","properties":{"temperature":{"type":"number"},"do":{"type":"number"},"ph":{"type":"number"},"tan":{"type":"number","description":"total ammonia nitrogen mg/L"},"salinity":{"type":"number"},"nitrite":{"type":"number"},"alkalinity":{"type":"number"}}}""", a =>
            {
                var s = c.Water with
                {
                    TemperatureC = Num(a, "temperature") ?? c.Water.TemperatureC, DissolvedOxygen = Num(a, "do") ?? c.Water.DissolvedOxygen, Ph = Num(a, "ph") ?? c.Water.Ph,
                    Ammonia = Num(a, "tan") ?? c.Water.Ammonia, SalinityPpt = Num(a, "salinity") ?? c.Water.SalinityPpt, Nitrite = Num(a, "nitrite") ?? c.Water.Nitrite,
                    Alkalinity = Num(a, "alkalinity") ?? c.Water.Alkalinity,
                };
                var r = WaterQualityIndex.Assess(s, c.Species);
                var recs = RecommendationEngine.Recommend(r, new PondContext(c.VolumeM3, c.AreaM2, c.BiomassKg, c.AerationKw, c.System));
                var sb = new StringBuilder();
                sb.Append(Inv, $"WQI {r.Wqi:0} — {WaterQualityAssessment.ClassName(r.Class)[lang()]}. ");
                if (r.UnionizedAmmonia is { } nh3) sb.Append(Inv, $"NH₃ {nh3:0.000} mg/L. ");
                if (r.DoSaturationPct is { } sat) sb.Append(Inv, $"DO {sat:0}% {T("saturation", "saturasi")}. ");
                foreach (var i in r.Issues) sb.Append("\n• ").Append(i[lang()]);
                sb.Append('\n').Append(T("Actions:", "Tindakan:"));
                foreach (var rec in recs.Take(5)) sb.Append($"\n{rec.Priority}. ").Append(rec.Action[lang()]).Append(" — ").Append(rec.Rationale[lang()]);
                return sb.ToString();
            }),
            new("feeding_plan", "Today's feeding plan: ration, meals and times, pellet size and cost, adjusted for temperature, DO and ammonia.",
                """{"type":"object","properties":{"abw_g":{"type":"number"},"population":{"type":"number"}}}""", a =>
            {
                double abw = Num(a, "abw_g") ?? c.AbwG, pop = Num(a, "population") ?? c.Population;
                double? nh3 = c.Water.Ammonia is { } tan && c.Water.TemperatureC is { } t && c.Water.Ph is { } ph ? WaterChemistry.UnionizedAmmonia(tan, t, ph, c.Water.SalinityPpt ?? 0) : null;
                var plan = FeedingCalculator.Plan(c.Species, abw, pop, c.Water.TemperatureC ?? 29, c.Water.DissolvedOxygen, nh3);
                var sb = new StringBuilder();
                sb.Append(Inv, $"{T("Daily ration", "Ransum harian")}: {plan.DailyKg:0.0} kg ({plan.RatePercentBw:0.00}% BW), {plan.MealsPerDay} {T("meals", "kali")}: ");
                sb.Append(string.Join(", ", plan.Meals.Select(m => $"{m.Time:HH\\:mm} {m.Kg:0.0} kg")));
                if (plan.Feed is { } f) sb.Append(Inv, $". {T("Feed", "Pakan")}: {f.Name}, {T("cost", "biaya")} ≈ Rp {plan.DailyKg * f.PricePerKg:N0}/{T("day", "hari")}");
                foreach (var adj in plan.Adjustments) sb.Append("\n• ").Append(adj[lang()]);
                return sb.ToString();
            }),
            new("growth_projection", "Project growth with the AquaGrowth engine and the ML growth model: ADG, weight in 7 days, days to a target weight.",
                """{"type":"object","properties":{"target_weight_g":{"type":"number"}}}""", a =>
            {
                double target = Num(a, "target_weight_g") ?? c.Species.MarketWeightG;
                var sim = new GrowthSimulator(c.Species);
                double nh3 = c.Water.Ammonia is { } tan && c.Water.TemperatureC is { } t && c.Water.Ph is { } ph ? WaterChemistry.UnionizedAmmonia(tan, t, ph, c.Water.SalinityPpt ?? 0) : 0;
                var env = new DailyEnvironment(c.Water.TemperatureC ?? 29, c.Water.DissolvedOxygen ?? 5, nh3, c.Water.SalinityPpt ?? double.NaN);
                var traj = sim.SimulateToWeight(new StockState(c.Species, c.Today, c.AbwG, c.Population, c.VolumeM3, c.DayOfCulture), target, (_, _) => env);
                var hit = traj.FirstReaching(target);
                var ml = new GrowthPredictor(c.Species, 600).Predict(new PondState(c.AbwG, env.TemperatureC, env.DissolvedOxygen, nh3, c.BiomassKg / c.VolumeM3 / c.Species.MaxBiomassKgM3, 1, env.SalinityPpt, c.DayOfCulture));
                return T($"ML model: ADG {ml.AdgG:0.00} g/day, {ml.WeightIn7DaysG:0.0} g in 7 days. Simulation: {(hit is null ? $"target {target} g not reached in {traj.Days.Count} days" : $"{target:0} g on {hit.Date:yyyy-MM-dd} (DOC {hit.Day}), survival {hit.Survivors / c.Population:P0}, biomass {hit.BiomassKg:N0} kg, FCR {hit.Fcr:0.00}")}.",
                         $"Model ML: ADG {ml.AdgG:0.00} g/hari, {ml.WeightIn7DaysG:0.0} g dalam 7 hari. Simulasi: {(hit is null ? $"target {target} g belum tercapai dalam {traj.Days.Count} hari" : $"{target:0} g pada {hit.Date:yyyy-MM-dd} (DOC {hit.Day}), kelulushidupan {hit.Survivors / c.Population:P0}, biomassa {hit.BiomassKg:N0} kg, FCR {hit.Fcr:0.00}")}.");
            }),
            new("harvest_forecast", "Probabilistic harvest date and biomass (P10/P50/P90) by Monte-Carlo simulation.",
                """{"type":"object","properties":{"target_weight_g":{"type":"number"}}}""", a =>
            {
                double target = Num(a, "target_weight_g") ?? c.Species.MarketWeightG;
                var f = HarvestOutlook.Forecast(c.Species, c.Today, c.AbwG, c.Population, target, c.Water.TemperatureC ?? 29, 240, 300);
                if (f.DayP50 < 0) return T("Target weight is not reached within the horizon.", "Bobot target tidak tercapai dalam horizon.");
                double price = c.SalePricePerKg ?? HarvestPredictor.DefaultPriceCurve(c.Species)(target);
                return T($"Harvest at {target:0} g: P50 in {f.DayP50} days ({f.DateP50:yyyy-MM-dd}), P10–P90 {f.DayP10}–{f.DayP90} days. Expected value ≈ Rp {f.BiomassAtHorizonP50Kg * price:N0} at horizon.",
                         $"Panen pada {target:0} g: P50 dalam {f.DayP50} hari ({f.DateP50:yyyy-MM-dd}), P10–P90 {f.DayP10}–{f.DayP90} hari. Nilai perkiraan ≈ Rp {f.BiomassAtHorizonP50Kg * price:N0} pada horizon.");
            }),
            new("disease_diagnosis", "Diagnose from clinical signs. Use sign ids: " + string.Join(", ", DiseaseKnowledgeBase.Symptoms.Select(s => s.Id)),
                """{"type":"object","properties":{"signs":{"type":"array","items":{"type":"string"}}},"required":["signs"]}""", a =>
            {
                var signs = a["signs"] is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList() : [];
                var r = DiagnosisEngine.Diagnose(c.Species, signs, Conditions(c));
                var sb = new StringBuilder();
                foreach (var cand in r.Candidates.Take(3)) sb.Append(Inv, $"• {cand.Disease.Name[lang()]}: {cand.Probability:P0}\n");
                return sb.Append(r.Advice[lang()]).ToString();
            }),
            new("disease_risk", "Environmental disease risk for the pond today, highest first.", Empty, _ =>
            {
                var o = DiseasePredictor.Predict(c.Species, Conditions(c));
                var sb = new StringBuilder(T($"Outbreak probability {o.OutbreakProbability:P0}.", $"Peluang wabah {o.OutbreakProbability:P0}."));
                foreach (var r in o.Risks.Take(3)) sb.Append(Inv, $"\n• {r.Disease.Name[lang()]}: {r.Risk:P0}");
                return sb.Append('\n').Append(o.Advice[lang()]).ToString();
            }),
            new("do_forecast", "Forecast tonight's dissolved-oxygen minimum and when to start aerators.", Empty, _ =>
            {
                var recent = c.RecentDo ?? new TimeSeries("DO", "mg/L");
                var weather = c.Weather ?? SyntheticWeather.Generate(-6.3, DateTime.Today, 2);
                var pond = new OxygenBudgetParameters { VolumeM3 = c.VolumeM3, DepthM = c.DepthM, BiomassKg = c.BiomassKg, MeanWeightG = c.AbwG, Group = c.Species.Group, SalinityPpt = c.Water.SalinityPpt ?? 0 };
                var now = recent.Last?.Time ?? DateTime.Today.AddHours(19);
                if (recent.Count == 0) recent.Add(now, c.Water.DissolvedOxygen ?? 5);
                var f = DoCrashForecaster.Forecast(recent, c.Species, pond, weather, now);
                return f.Advice[lang()] + T($" (method: {f.Method}, P(DO < {c.Species.DoCritical}) = {f.ProbabilityBelowCritical:P0})", $" (metode: {f.Method}, P(DO < {c.Species.DoCritical}) = {f.ProbabilityBelowCritical:P0})");
            }),
            new("weather_impact", "Effects of the coming weather on the pond (temperature, rain dilution, pH, plankton/DO, stratification, storms).", Empty, _ =>
            {
                var w = c.Weather ?? SyntheticWeather.Generate(-6.3, DateTime.Today, 3);
                var r = WeatherImpactPredictor.Predict(w.Take(72).ToList(), new PondExposure(c.DepthM, c.Water.SalinityPpt ?? 0, c.System, false, c.Water.TemperatureC ?? double.NaN));
                var sb = new StringBuilder(T($"Water {r.WaterTempMinC}–{r.WaterTempMaxC} °C, rain {r.TotalRainMm} mm.", $"Air {r.WaterTempMinC}–{r.WaterTempMaxC} °C, hujan {r.TotalRainMm} mm."));
                foreach (var i in r.Impacts) sb.Append("\n• ").Append(i.Effect[lang()]).Append(" → ").Append(i.Advice[lang()]);
                if (r.Impacts.Count == 0) sb.Append(T(" No significant weather risk.", " Tidak ada risiko cuaca berarti."));
                return sb.ToString();
            }),
            new("economics", "Projected revenue, feed cost and margin at harvest; optimal harvest day.",
                """{"type":"object","properties":{"price_per_kg":{"type":"number"}}}""", a =>
            {
                var sim = new GrowthSimulator(c.Species);
                var env = new DailyEnvironment(c.Water.TemperatureC ?? 29, c.Water.DissolvedOxygen ?? 5, 0, c.Water.SalinityPpt ?? double.NaN);
                var traj = sim.Simulate(new StockState(c.Species, c.Today, c.AbwG, c.Population, c.VolumeM3, c.DayOfCulture), 120, (_, _) => env);
                double? price = Num(a, "price_per_kg") ?? c.SalePricePerKg;
                var econ = new HarvestEconomics(c.FeedPricePerKg, c.AerationKw * 18 * 1_500, price is { } p ? _ => p : null);
                var best = HarvestPredictor.Optimal(traj, c.AreaM2, econ);
                return T($"Best harvest: DOC {best.DayOfCulture} ({best.Date:yyyy-MM-dd}) at {best.AbwG:0.#} g, biomass {best.BiomassKg:N0} kg, revenue Rp {best.Revenue:N0}, feed Rp {best.FeedCost:N0}, margin Rp {best.Margin:N0}.",
                         $"Panen terbaik: DOC {best.DayOfCulture} ({best.Date:yyyy-MM-dd}) pada {best.AbwG:0.#} g, biomassa {best.BiomassKg:N0} kg, pendapatan Rp {best.Revenue:N0}, pakan Rp {best.FeedCost:N0}, margin Rp {best.Margin:N0}.");
            }),
        ];
    }

    private static PondConditions Conditions(AquaContext c) => new()
    {
        TemperatureC = c.Water.TemperatureC, DawnDo = c.Water.DissolvedOxygen, SalinityPpt = c.Water.SalinityPpt, DayOfCulture = c.DayOfCulture,
        Nh3 = c.Water.Ammonia is { } tan && c.Water.TemperatureC is { } t && c.Water.Ph is { } ph ? WaterChemistry.UnionizedAmmonia(tan, t, ph, c.Water.SalinityPpt ?? 0) : null,
        DensityRatio = c.BiomassKg / c.VolumeM3 / c.Species.MaxBiomassKgM3, OrpMv = c.Water.OrpMv, Bod = c.Water.Bod,
    };
}

/// <summary>
/// Aquaculture assistant: answers farmer questions in English or Bahasa Indonesia, grounded in tool calls to the AquaCulture.Net
/// models (water quality, feeding, growth, harvest, disease, DO forecast, weather, economics). Works with any <see cref="IChatModel"/>;
/// without one it uses <see cref="RuleBasedChatModel"/> offline.
/// </summary>
public sealed class AquaAgent
{
    private readonly List<ChatMessage> _history = [];
    private string _lang = "en";

    public AquaAgent(AquaContext context, IChatModel? model = null)
    {
        Context = context;
        Model = model ?? (LlmOptions.FromEnvironment() is { } o ? o.CreateModel() : new RuleBasedChatModel());
        Tools = AquaTools.Create(context, () => _lang);
    }

    public AquaContext Context { get; }
    public IChatModel Model { get; }
    public IReadOnlyList<AgentTool> Tools { get; }
    public int MaxToolRounds { get; init; } = 4;
    public IReadOnlyList<ChatMessage> History => _history;

    private static readonly string[] IndonesianMarkers = ["berapa", "apa", "bagaimana", "kapan", "kolam", "udang", "ikan", "pakan", "panen", "air", "yang", "hari", "saya", "tolong", "apakah", "kenapa", "mengapa"];

    /// <summary>Detects Indonesian vs English from common words.</summary>
    public static string DetectLanguage(string text)
    {
        var words = text.ToLowerInvariant().Split([' ', ',', '.', '?', '!'], StringSplitOptions.RemoveEmptyEntries);
        return words.Count(w => IndonesianMarkers.Contains(w)) >= 1 ? "id" : "en";
    }

    private string SystemPrompt() =>
        $"""
        You are AquaCulture.Net's aquaculture advisor for fish and shrimp farmers. Farm context: {Context.Species.Name.En} ({Context.Species.ScientificName}),
        pond "{Context.PondName}", {Context.AreaM2:N0} m², depth {Context.DepthM} m, system {Context.System}, day of culture {Context.DayOfCulture}, ABW {Context.AbwG} g,
        population {Context.Population:N0}. Always call the tools for numbers; never invent values. Answer in {(_lang == "id" ? "Bahasa Indonesia" : "English")},
        concisely (max ~8 lines), practical and actionable, citing the key figures from tool results. If a disease is suspected, recommend laboratory confirmation.
        """;

    public string Ask(string question) => AskAsync(question).GetAwaiter().GetResult().Text;

    public async Task<AgentAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        _lang = DetectLanguage(question);
        if (_history.Count == 0 || _history[0].Role != ChatRole.System) _history.Insert(0, new ChatMessage(ChatRole.System, SystemPrompt()));
        else _history[0] = new ChatMessage(ChatRole.System, SystemPrompt());
        _history.Add(new ChatMessage(ChatRole.User, question));
        var used = new List<string>();
        for (int round = 0; round <= MaxToolRounds; round++)
        {
            var resp = await Model.CompleteAsync(_history, round == MaxToolRounds ? [] : Tools, ct).ConfigureAwait(false);
            if (resp.ToolCalls.Count == 0)
            {
                string text = resp.Content ?? "";
                _history.Add(new ChatMessage(ChatRole.Assistant, text));
                return new AgentAnswer(text, used, Model.Name, _lang);
            }
            _history.Add(new ChatMessage(ChatRole.Assistant, resp.Content, resp.ToolCalls));
            foreach (var call in resp.ToolCalls)
            {
                var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
                string result;
                try
                {
                    var args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
                    result = tool is null ? $"Unknown tool {call.Name}" : tool.Invoke(args);
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { result = $"Tool error: {e.Message}"; }
                used.Add(call.Name);
                _history.Add(new ChatMessage(ChatRole.Tool, result, ToolCallId: call.Id));
            }
        }
        return new AgentAnswer("", used, Model.Name, _lang);
    }

    public void Reset() => _history.Clear();
}
