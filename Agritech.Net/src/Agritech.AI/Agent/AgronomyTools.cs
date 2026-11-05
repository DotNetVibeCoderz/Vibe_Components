using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

namespace Agritech.AI.Agent;

/// <summary>Grounded agronomy tools available to <see cref="FarmAgent"/>. Outputs are localised (en/id) plain text.</summary>
public static class AgronomyTools
{
    private const string NoArgs = """{"type":"object","properties":{}}""";

    public static IReadOnlyList<AgentTool> Create(FarmContext ctx, Func<string> language)
    {
        bool Id() => language() == "id";
        string T(string en, string id) => Id() ? id : en;
        static string F(double v, int d = 1) => v.ToString("N" + d, CultureInfo.InvariantCulture);

        async Task<(IReadOnlyList<DailyWeather> History, IReadOnlyList<DailyWeather> Forecast)> WeatherAsync(CancellationToken ct)
        {
            var from = ctx.PlantingDate < ctx.Today ? ctx.PlantingDate : ctx.Today.AddDays(-1);
            var history = await ctx.Weather.GetHistoricalAsync(ctx.Location, from, ctx.Today.AddDays(-1), ct).ConfigureAwait(false);
            IReadOnlyList<DailyWeather> forecast;
            try { forecast = await ctx.Weather.GetForecastAsync(ctx.Location, 7, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { forecast = new SyntheticWeatherProvider().Generate(ctx.Location, ctx.Today, ctx.Today.AddDays(6)); }
            return (history, forecast);
        }

        return
        [
            new("farm_overview", "Summary of the farm, crop growth stage, days after planting and soil status.", NoArgs, (_, _) =>
            {
                var crop = ctx.Crop;
                int dap = ctx.DaysAfterPlanting;
                var stageIdx = 0; int acc = 0;
                for (int i = 0; i < crop.StageDays.Length; i++) { acc += crop.StageDays[i]; if (dap > acc) stageIdx = Math.Min(3, i + 1); }
                string[] stagesEn = ["initial", "development", "mid-season", "late-season"], stagesId = ["awal", "perkembangan", "pertengahan", "akhir"];
                var health = SoilHealth.Assess(ctx.Soil);
                return Task.FromResult(T(
                    $"{ctx.FarmName}: {crop.Name}, {F(ctx.AreaHa, 2)} ha, planted {ctx.PlantingDate:yyyy-MM-dd} ({dap} DAP, {stagesEn[stageIdx]} stage, expected harvest ≈ {ctx.PlantingDate.AddDays(crop.SeasonLengthDays):yyyy-MM-dd}). Soil health {health.Score}/100 ({health.Grade}), pH {ctx.Soil.Ph}.",
                    $"{ctx.FarmName}: {crop.NameId}, {F(ctx.AreaHa, 2)} ha, tanam {ctx.PlantingDate:yyyy-MM-dd} ({dap} HST, fase {stagesId[stageIdx]}, perkiraan panen ≈ {ctx.PlantingDate.AddDays(crop.SeasonLengthDays):yyyy-MM-dd}). Kesehatan tanah {health.Score}/100 ({health.Grade}), pH {ctx.Soil.Ph}."));
            }),

            new("fertilizer_plan", "Site-specific fertilizer recommendation (products, kg/ha, total kg) and which application is due this week.", NoArgs, (_, _) =>
            {
                var adv = FertilizerAdvisor.Advise(ctx.Crop, ctx.Soil, ctx.AreaHa, ctx.TargetYieldTonHa);
                int dap = ctx.DaysAfterPlanting;
                var due = adv.DueWithinWeek(dap);
                var sb = new StringBuilder();
                sb.AppendLine(T($"Fertilizer plan for {ctx.Crop.Name}, target yield {F(adv.TargetYieldTonHa)} t/ha on {F(ctx.AreaHa, 2)} ha (nutrient need N {F(adv.Need.N)}, P2O5 {F(adv.Need.P2O5)}, K2O {F(adv.Need.K2O)} kg/ha). Today is {dap} DAP.",
                                $"Rencana pemupukan {ctx.Crop.NameId}, target hasil {F(adv.TargetYieldTonHa)} t/ha pada {F(ctx.AreaHa, 2)} ha (kebutuhan hara N {F(adv.Need.N)}, P2O5 {F(adv.Need.P2O5)}, K2O {F(adv.Need.K2O)} kg/ha). Hari ini {dap} HST."));
                if (due.Count == 0)
                {
                    var next = adv.Schedule.FirstOrDefault(a => a.FromDap > dap);
                    sb.AppendLine(next is null
                        ? T("No fertilizer application is due this week; the schedule is complete.", "Tidak ada jadwal pemupukan minggu ini; seluruh jadwal sudah selesai.")
                        : T($"No application due this week. Next: {next.Stage} at {next.FromDap}–{next.ToDap} DAP ({ctx.PlantingDate.AddDays(next.FromDap):dd MMM}).",
                            $"Tidak ada pemupukan minggu ini. Berikutnya: {next.Stage} pada {next.FromDap}–{next.ToDap} HST ({ctx.PlantingDate.AddDays(next.FromDap):dd MMM})."));
                }
                foreach (var a in due)
                {
                    sb.AppendLine(T($"DUE THIS WEEK — {a.Stage} ({a.FromDap}–{a.ToDap} DAP):", $"JADWAL MINGGU INI — {a.Stage} ({a.FromDap}–{a.ToDap} HST):"));
                    foreach (var p in a.Products) sb.AppendLine($"  • {p.Product.Name}: {F(p.KgPerHa)} kg/ha → {F(p.TotalKg)} kg");
                }
                sb.AppendLine(T("Full schedule:", "Jadwal lengkap:"));
                foreach (var a in adv.Schedule)
                    sb.AppendLine($"  - {a.Stage} {a.FromDap}–{a.ToDap} {T("DAP", "HST")}: " + string.Join(", ", a.Products.Select(p => $"{p.Product.Name} {F(p.KgPerHa)} kg/ha")));
                sb.Append(T($"Estimated cost ≈ Rp {adv.Plan.TotalCost:N0}. Apply on moist soil, incorporate or band urea, avoid application before heavy rain.",
                            $"Perkiraan biaya ≈ Rp {adv.Plan.TotalCost:N0}. Aplikasikan saat tanah lembap, benamkan/larikan urea, hindari sebelum hujan lebat."));
                return Task.FromResult(sb.ToString());
            }),

            new("irrigation_advice", "FAO-56 soil water balance: whether to irrigate now, depth (mm) and volume (m³), next irrigation date.", NoArgs, async (_, ct) =>
            {
                var (hist, fc) = await WeatherAsync(ct).ConfigureAwait(false);
                var advisor = new IrrigationAdvisor(ctx.Crop, ctx.Soil.Hydraulics(), ctx.Station);
                var r = advisor.Recommend(ctx.PlantingDate, hist, fc, ctx.AreaHa);
                return r.IrrigateNow
                    ? T($"Irrigate now: net {F(r.NetDepthMm)} mm, gross {F(r.GrossDepthMm)} mm (≈ {F(r.VolumeM3, 0)} m³ for the field). Depletion {F(r.CurrentDepletionPct)}% of available water. {r.Reason}",
                        $"Lakukan irigasi sekarang: neto {F(r.NetDepthMm)} mm, bruto {F(r.GrossDepthMm)} mm (≈ {F(r.VolumeM3, 0)} m³ untuk lahan). Deplesi {F(r.CurrentDepletionPct)}% dari air tersedia.")
                    : T($"No irrigation needed today. {r.Reason}" + (r.NextIrrigation is { } n ? $" Next irrigation expected around {n:yyyy-MM-dd}." : ""),
                        $"Belum perlu irigasi hari ini (deplesi {F(r.CurrentDepletionPct)}% air tersedia)." + (r.NextIrrigation is { } m ? $" Perkiraan irigasi berikutnya sekitar {m:yyyy-MM-dd}." : ""));
            }),

            new("weather_forecast", "7-day weather forecast with rainfall, temperature and reference evapotranspiration.", NoArgs, async (_, ct) =>
            {
                var (_, fc) = await WeatherAsync(ct).ConfigureAwait(false);
                var et0 = Evapotranspiration.Compute(fc, ctx.Station);
                var sb = new StringBuilder(T($"Forecast ({ctx.Weather.Name}):\n", $"Prakiraan cuaca ({ctx.Weather.Name}):\n"));
                for (int i = 0; i < fc.Count; i++)
                    sb.AppendLine($"  {fc[i].Date:ddd dd MMM}: {F(fc[i].TminC)}–{F(fc[i].TmaxC)} °C, {T("rain", "hujan")} {F(fc[i].RainMm)} mm, ET0 {F(et0[i])} mm");
                sb.Append(T($"Total rain {F(fc.Sum(f => f.RainMm))} mm vs ET0 {F(et0.Sum())} mm.", $"Total hujan {F(fc.Sum(f => f.RainMm))} mm vs ET0 {F(et0.Sum())} mm."));
                return sb.ToString();
            }),

            new("disease_risk", "Weather-driven infection risk for the crop's major diseases over the last 7 days.", NoArgs, async (_, ct) =>
            {
                var (hist, _) = await WeatherAsync(ct).ConfigureAwait(false);
                var recent = hist.TakeLast(7).Select(d => new InfectionConditions(d.Date, DiseaseRiskModel.EstimateLeafWetnessHours(d.RhMeanPct ?? 80, d.RainMm), d.TminC + 0.35 * (d.TmaxC - d.TminC), d.RhMeanPct ?? 80)).ToList();
                var models = DiseaseRiskModel.ForCrop(ctx.Crop).ToList();
                if (models.Count == 0) return T($"No disease model available for {ctx.Crop.Name}.", $"Belum ada model penyakit untuk {ctx.Crop.NameId}.");
                return string.Join("\n", models.Select(m => DiseaseRiskModel.Assess(m, recent)).Select(r =>
                    $"{T(r.Pathogen.Name, r.Pathogen.NameId)}: {r.Level} — {r.Explanation}"));
            }),

            new("pest_risk", "Degree-day based pest generation status for the crop's key insect pests.", NoArgs, async (_, ct) =>
            {
                var (hist, _) = await WeatherAsync(ct).ConfigureAwait(false);
                var pests = PestModel.All.Where(p => p.HostCropIds.Contains(ctx.Crop.Id)).ToList();
                if (pests.Count == 0) return T("No pest model for this crop.", "Belum ada model hama untuk tanaman ini.");
                return string.Join("\n", pests.Select(p =>
                {
                    double dd = hist.Sum(d => p.DailyDegreeDays(d.TminC, d.TmaxC));
                    double gen = p.Generations(dd);
                    double toNext = (Math.Floor(gen) + 1) * p.DegreeDaysPerGeneration - dd;
                    return T($"{p.Name}: {F(dd, 0)} DD since planting ≈ {F(gen, 2)} generations; next generation in ≈ {F(toNext, 0)} DD. {p.Management}",
                             $"{p.NameId}: {F(dd, 0)} derajat-hari sejak tanam ≈ {F(gen, 2)} generasi; generasi berikutnya ≈ {F(toNext, 0)} DH lagi. {p.Management}");
                }));
            }),

            new("yield_estimate", "Water-limited yield estimate (FAO-33 Ky) from the season's simulated water balance.", NoArgs, async (_, ct) =>
            {
                var (hist, _) = await WeatherAsync(ct).ConfigureAwait(false);
                var advisor = new IrrigationAdvisor(ctx.Crop, ctx.Soil.Hydraulics(), ctx.Station);
                var bal = advisor.Simulate(ctx.PlantingDate, hist);
                double eta = bal.Sum(b => b.EtcAdj), etm = bal.Sum(b => b.Kc * b.Et0);
                double y = YieldEstimator.WaterLimited(ctx.Crop, eta, etm);
                return T($"Estimated yield {F(y, 2)} t/ha (potential {F(ctx.Crop.PotentialYieldTonHa)} t/ha, ETa/ETm = {F(etm == 0 ? 1 : eta / etm, 2)}) → ≈ {F(y * ctx.AreaHa, 2)} t for {F(ctx.AreaHa, 2)} ha.",
                         $"Perkiraan hasil {F(y, 2)} t/ha (potensi {F(ctx.Crop.PotentialYieldTonHa)} t/ha, ETa/ETm = {F(etm == 0 ? 1 : eta / etm, 2)}) → ≈ {F(y * ctx.AreaHa, 2)} t untuk {F(ctx.AreaHa, 2)} ha.");
            }),

            new("soil_health", "Soil health score, indicator interpretation and soil improvement recommendations.", NoArgs, (_, _) =>
            {
                var r = SoilHealth.Assess(ctx.Soil);
                var sb = new StringBuilder($"Soil health {r.Score}/100 — {r.Grade}. Texture: {ctx.Soil.Texture}.\n");
                foreach (var i in r.Indicators) sb.AppendLine($"  {i.Indicator}: {i.Value} ({i.Interpretation}, score {F(i.Score * 100, 0)})");
                foreach (var rec in r.Recommendations) sb.AppendLine($"  → {rec}");
                return Task.FromResult(sb.ToString().TrimEnd());
            }),

            new("crop_recommendation", "Ranks crops by land suitability (FAO classes) for given or current site conditions.",
                """{"type":"object","properties":{"mean_temp_c":{"type":"number"},"seasonal_rain_mm":{"type":"number"},"soil_ph":{"type":"number"},"irrigation":{"type":"boolean"}}}""",
                async (args, ct) =>
                {
                    double temp = args["mean_temp_c"]?.GetValue<double>() ?? double.NaN;
                    double rain = args["seasonal_rain_mm"]?.GetValue<double>() ?? double.NaN;
                    if (double.IsNaN(temp) || double.IsNaN(rain))
                    {
                        var hist = await ctx.Weather.GetHistoricalAsync(ctx.Location, ctx.Today.AddDays(-120), ctx.Today.AddDays(-1), ct).ConfigureAwait(false);
                        if (double.IsNaN(temp)) temp = hist.Average(h => h.TmeanC);
                        if (double.IsNaN(rain)) rain = hist.Sum(h => h.RainMm);
                    }
                    var site = new SiteConditions(temp, rain, args["soil_ph"]?.GetValue<double>() ?? ctx.Soil.Ph, ctx.ElevationM, args["irrigation"]?.GetValue<bool>() ?? false);
                    var recs = new CropRecommender(ctx.Catalog).Recommend(site, 5);
                    return T($"Site: {F(site.MeanTempC)} °C, {F(site.SeasonalRainfallMm, 0)} mm/season, pH {F(site.SoilPh)}. Best crops:\n",
                             $"Kondisi lahan: {F(site.MeanTempC)} °C, {F(site.SeasonalRainfallMm, 0)} mm/musim, pH {F(site.SoilPh)}. Tanaman paling sesuai:\n")
                        + string.Join("\n", recs.Select((r, i) => $"  {i + 1}. {T(r.Crop.Name, r.Crop.NameId)} — {r.Class} ({F(r.Score * 100, 0)}%)" +
                            (r.LimitingFactors.Count > 0 ? $"; {T("limits", "pembatas")}: {string.Join("; ", r.LimitingFactors)}" : "")));
                }),
        ];
    }
}
