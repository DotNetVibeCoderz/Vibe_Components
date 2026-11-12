using AquaCulture.ComputerVision;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Disease;
using AquaCulture.Environment;
using AquaCulture.Gallery.Shared;
using AquaCulture.Reporting;
using AquaCulture.StockAssessment;
using AquaCulture.Visualization;
using AquaCulture.WaterQuality;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using static AquaCulture.Gallery.Desktop.Ui;

namespace AquaCulture.Gallery.Desktop;

/// <summary>Code-first desktop views; each re-runs its shared scenario when a control changes.</summary>
internal static class Views
{
    private static string _lang = "en";
    private static string T(string en, string id) => _lang == "id" ? id : en;
    private static IReadOnlyList<(string, string)> SpeciesItems => SpeciesCatalog.All.Select(s => (s.Id, s.Name[_lang])).ToList();

    public static Control Build(string slug, string lang)
    {
        _lang = lang;
        return slug switch
        {
            "water" => Water(), "oxygen" => Oxygen(), "hydro" => Hydro(), "growth" => Growth(), "feeding" => Feeding(), "ai" => Ai(),
            "disease" => Disease(), "vision" => Vision(), "twin" => Twin(), "iot" => IoT(), "gis" => Gis(), "weather" => Weather(),
            "stock" => Stock(), "population" => Population(), "analytics" => Analytics(), "reporting" => Reporting(), "agent" => Agent(),
            _ => Text("Unknown demo"),
        };
    }

    /// <summary>Controls bar + results host that is rebuilt by <paramref name="render"/>.</summary>
    private static Control Live(Func<Control> render, out Action refresh, params Control[] controls)
    {
        var host = new ContentControl { Content = render() };
        refresh = () => host.Content = render();
        return Stack(Ui.Controls(controls), host);
    }

    private static string Tone(WaterQualityClass c) => c is WaterQualityClass.Excellent or WaterQualityClass.Good ? "good" : c == WaterQualityClass.Fair ? "warn" : "alert";

    // ───────── water ─────────
    private static Control Water()
    {
        string sp = "vaname"; double t = 30.5, dox = 3.8, ph = 8.4, tan = 1.6, sal = 18, no2 = 0.6, alk = 95;
        Action refresh = () => { };
        Control R()
        {
            var r = WaterDemo.Run(sp, t, dox, ph, tan, sal, no2, alk);
            var a = r.Assessment;
            var grid = new WrapPanel();
            foreach (var s in a.Scores)
            {
                var info = WaterParameters.Info[s.Parameter];
                string tone = s.Status switch { ParameterStatus.Optimal => "good", ParameterStatus.Acceptable => "info", ParameterStatus.Stress => "warn", _ => "critical" };
                var n = Note($"{info.Code}  {N(s.Value, "0.##")} {info.Unit}", info.Name[_lang], tone);
                n.Width = 150; n.Margin = new Thickness(0, 0, 8, 8);
                grid.Children.Add(n);
            }
            var actions = new StackPanel();
            foreach (var x in r.Actions) actions.Children.Add(Note($"{x.Priority}. {x.Action[_lang]}" + (x.Quantity is { } q ? $"  [{q:0.#} {x.Unit}]" : ""), x.Rationale[_lang], x.Priority == 1 ? "critical" : x.Priority == 2 ? "warn" : "good"));
            var issues = new StackPanel();
            foreach (var i in a.Issues) issues.Children.Add(Note(i[_lang], null, "warn"));
            return Stack(
                Kpis(Kpi("WQI", N(a.Wqi, "0"), null, WaterQualityAssessment.ClassName(a.Class)[_lang], Tone(a.Class)),
                     Kpi(T("Toxic NH₃-N", "NH₃-N beracun"), N(r.Nh3, "0.000"), "mg/L", $"≤ {a.Species.Nh3Max}", r.Nh3 > a.Species.Nh3Max ? "alert" : "good"),
                     Kpi(T("DO saturation", "Saturasi DO"), N(r.DoSat, "0"), "%"), Kpi(T("Free CO₂", "CO₂ bebas"), N(a.FreeCo2 ?? double.NaN, "0.0"), "mg/L"),
                     Kpi(T("Issues", "Masalah"), a.Issues.Count.ToString(), null, null, a.Issues.Count > 0 ? "warn" : "good")),
                Columns([1.5, 1], Panel(T("Parameters vs species tolerance", "Parameter vs toleransi spesies"), grid, a.Species.Name[_lang]),
                    Panel("Water Quality Index", Stack(SvgView(r.GaugeSvg, 220), issues))),
                Columns(2, Panel(T("Recommended actions", "Tindakan yang disarankan"), actions, "K-01"), Panel(T("Last 24 h on the buoy", "24 jam terakhir di pelampung"), SvgView(r.DaySvg))));
        }
        return Live(R, out refresh,
            Combo(T("Species", "Spesies"), SpeciesItems, sp, v => { sp = v; refresh(); }),
            Slider(T("Temp °C", "Suhu °C"), 20, 36, t, 0.1, "0.0", v => { t = v; refresh(); }),
            Slider("DO mg/L", 0.5, 10, dox, 0.1, "0.0", v => { dox = v; refresh(); }),
            Slider("pH", 6, 9.6, ph, 0.05, "0.00", v => { ph = v; refresh(); }),
            Slider("TAN mg/L", 0, 5, tan, 0.05, "0.00", v => { tan = v; refresh(); }),
            Slider(T("Salinity ppt", "Salinitas ppt"), 0, 40, sal, 0.5, "0.0", v => { sal = v; refresh(); }),
            Slider("NO₂-N", 0, 4, no2, 0.05, "0.00", v => { no2 = v; refresh(); }),
            Slider(T("Alkalinity", "Alkalinitas"), 20, 250, alk, 5, "0", v => { alk = v; refresh(); }));
    }

    // ───────── oxygen ─────────
    private static Control Oxygen()
    {
        double bio = 3000, chl = 120, kw = 4.4, spt = 4.5, wet = 1;
        Action refresh = () => { };
        Control R()
        {
            var r = OxygenDemo.Run(bio, chl, kw, spt, wet);
            return Stack(
                Kpis(Kpi(T("Dawn DO, no aeration", "DO subuh tanpa aerasi"), N(r.Off.MinDo), "mg/L", r.Off.MinTime.ToString("HH:mm"), r.Off.MinDo < 3 ? "alert" : "warn"),
                     Kpi(T("Dawn DO, set-point", "DO subuh, set-point"), N(r.On.MinDo), "mg/L", null, r.On.MinDo >= 3 ? "good" : "alert"),
                     Kpi(T("Aerator energy", "Energi kincir"), N(r.On.AeratorKwh, "0"), "kWh", Fmt.Rp(r.CostRp)),
                     Kpi("P(DO < 3)", N(r.Forecast.ProbabilityBelowCritical * 100, "0"), "%", r.Forecast.Method, r.Forecast.ProbabilityBelowCritical > 0.5 ? "alert" : "good"),
                     Kpi(T("Start aerators by", "Nyalakan kincir"), r.Forecast.AeratorStartBy?.ToString("HH:mm") ?? "—")),
                Columns([1.5, 1], Panel(T("36-hour oxygen budget", "Neraca oksigen 36 jam"), SvgView(r.ChartSvg), "K-01"),
                    Panel(T("Night fluxes", "Fluks malam"), Stack(SvgView(r.BudgetSvg), Note(r.Forecast.Advice[_lang], null, r.Forecast.ProbabilityBelowCritical > 0.5 ? "critical" : "warn")))));
        }
        return Live(R, out refresh,
            Slider(T("Biomass kg", "Biomassa kg"), 500, 6000, bio, 100, "0", v => { bio = v; refresh(); }),
            Slider(T("Chlorophyll-a", "Klorofil-a"), 10, 300, chl, 5, "0", v => { chl = v; refresh(); }),
            Slider(T("Aerators kW", "Kincir kW"), 0, 10, kw, 0.2, "0.0", v => { kw = v; refresh(); }),
            Slider(T("DO set-point", "Set-point DO"), 3, 6, spt, 0.1, "0.0", v => { spt = v; refresh(); }),
            Slider(T("Cloudiness", "Mendung"), 0, 2, wet, 0.1, "0.0", v => { wet = v; refresh(); }));
    }

    // ───────── hydro ─────────
    private static Control Hydro()
    {
        double wheels = 2, thrust = 0.25, hours = 1.5, init = 3.2;
        Action refresh = () => { };
        Control R()
        {
            var r = HydroDemo.Run((int)wheels, thrust, hours, init);
            var stage = new Grid { Children = { Png(r.FieldPng), SvgView(r.FlowSvg) } };
            return Stack(
                Kpis(Kpi(T("Mean DO", "DO rata-rata"), N(r.Mean), "mg/L"), Kpi(T("Minimum DO", "DO minimum"), N(r.Min), "mg/L", null, r.Min < 3 ? "alert" : "good"),
                     Kpi(T("Area below 5 mg/L", "Area < 5 mg/L"), N(r.LowAreaPct, "0"), "%", null, r.LowAreaPct > 30 ? "warn" : "good"),
                     Kpi("Kernel", r.Backend.ToString(), null, $"{r.Steps:N0} steps · {r.Milliseconds:0} ms")),
                Columns([1.5, 1], Panel(T("Dissolved oxygen field", "Sebaran oksigen terlarut"), stage, $"{r.Nx} × {r.Ny} · 0.7 m"),
                    Panel(T("Mean and minimum over time", "Rata-rata dan minimum"), SvgView(r.HistorySvg))));
        }
        return Live(R, out refresh,
            Slider(T("Paddle wheels", "Jumlah kincir"), 0, 4, wheels, 1, "0", v => { wheels = v; refresh(); }),
            Slider(T("Thrust", "Daya dorong"), 0.05, 0.5, thrust, 0.05, "0.00", v => { thrust = v; refresh(); }),
            Slider(T("Hours", "Jam"), 0.5, 6, hours, 0.5, "0.0", v => { hours = v; refresh(); }),
            Slider(T("Initial DO", "DO awal"), 1, 6, init, 0.1, "0.0", v => { init = v; refresh(); }));
    }

    // ───────── growth ─────────
    private static Control Growth()
    {
        string sp = "vaname"; double t = 29, dox = 5, dens = 125, feed = 1;
        Action refresh = () => { };
        Control R()
        {
            var r = GrowthDemo.Run(sp, t, dox, dens, feed);
            return Stack(
                Kpis(Kpi(T("Market size", "Ukuran pasar"), $"DOC {r.AtTarget.DayOfCulture}", null, $"{r.Species.MarketWeightG} g · {r.AtTarget.Date:yyyy-MM-dd}"),
                     Kpi("P10–P90", $"{r.MonteCarlo.DayP10}–{r.MonteCarlo.DayP90}", T("days", "hari"), $"P50 {r.MonteCarlo.DayP50} · {r.MonteCarlo.Backend}"),
                     Kpi(T("Survival", "Kelulushidupan"), N(r.AtTarget.SurvivalRate * 100, "0"), "%"), Kpi("FCR", N(r.AtTarget.Fcr, "0.00")),
                     Kpi(T("Best harvest", "Panen terbaik"), $"DOC {r.Optimal.DayOfCulture}", null, $"{N(r.Optimal.AbwG, "0.#")} g · {Fmt.Rp(r.Optimal.Margin)}", "good")),
                Columns(2, Panel(T("Average body weight", "Bobot rata-rata"), SvgView(r.WeightSvg)), Panel(T("Biomass and feed", "Biomassa dan pakan"), SvgView(r.BiomassSvg))),
                Columns(2, Panel(T("Monte-Carlo final weight", "Bobot akhir Monte-Carlo"), SvgView(r.HistogramSvg)),
                    Panel(T("At market size", "Pada ukuran pasar"), Table(["", ""], [
                        [T("Biomass", "Biomassa"), $"{N(r.AtTarget.BiomassKg, "N0")} kg"], [T("Yield", "Produktivitas"), $"{N(r.AtTarget.YieldKgPerM2, "0.00")} kg/m²"],
                        [T("Feed used", "Pakan"), $"{N(r.AtTarget.CumulativeFeedKg, "N0")} kg"], [T("Revenue", "Pendapatan"), Fmt.Rp(r.AtTarget.Revenue)],
                        [T("Feed cost", "Biaya pakan"), Fmt.Rp(r.AtTarget.FeedCost)], [T("Margin", "Margin"), Fmt.Rp(r.AtTarget.Margin)]]))));
        }
        return Live(R, out refresh,
            Combo(T("Species", "Spesies"), SpeciesItems, sp, v => { sp = v; refresh(); }),
            Slider(T("Water °C", "Air °C"), 22, 34, t, 0.5, "0.0", v => { t = v; refresh(); }),
            Slider(T("Dawn DO", "DO subuh"), 2, 8, dox, 0.1, "0.0", v => { dox = v; refresh(); }),
            Slider(T("Stocking /m²", "Tebar /m²"), 10, 250, dens, 5, "0", v => { dens = v; refresh(); }),
            Slider(T("Ration ×", "Ransum ×"), 0.5, 1.2, feed, 0.05, "0.00", v => { feed = v; refresh(); }));
    }

    // ───────── feeding ─────────
    private static Control Feeding()
    {
        string sp = "vaname"; double abw = 12, pop = 220_000, t = 29.5, dox = 4.3, left = 12;
        Action refresh = () => { };
        Control R()
        {
            var r = FeedingDemo.Run(sp, abw, pop, t, dox, left);
            var meals = new WrapPanel();
            foreach (var m in r.Plan.Meals) meals.Children.Add(new Border { Classes = { "panel" }, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 8), Child = Stack(Eyebrow(m.Time.ToString("HH:mm")), Text($"{m.Kg:0.0} kg", 18, weight: Avalonia.Media.FontWeight.Bold)) });
            var adj = new StackPanel();
            foreach (var a in r.Plan.Adjustments.Concat(r.Auto.Reasons)) adj.Children.Add(Note(a[_lang], null, "warn"));
            return Stack(
                Kpis(Kpi(T("Ration today", "Ransum hari ini"), N(r.Plan.DailyKg, "0.0"), "kg", $"{N(r.Plan.RatePercentBw, "0.00")} % BW"),
                     Kpi(T("Feed", "Pakan"), r.Plan.Feed?.Id ?? "-", null, $"{r.Plan.PelletMm} mm"), Kpi(T("Daily cost", "Biaya harian"), Fmt.Rp(r.Plan.DailyCost)),
                     Kpi(T("Next meal (auto)", "Pakan berikutnya"), r.Auto.Skip ? T("skip", "lewati") : N(r.Auto.Grams / 1000, "0.0"), r.Auto.Skip ? null : "kg", null, r.Auto.Skip ? "alert" : null),
                     Kpi("FCR K-01", N(r.CycleFcr, "0.00"), null, $"{T("feed cost/kg", "biaya pakan/kg")} {Fmt.Rp(r.Cost.FeedCostPerKgProduced)}")),
                Panel(T("Meal schedule", "Jadwal pakan"), Stack(meals, adj)),
                Columns(2, Panel(T("Least-cost feed", "Pakan termurah"), Table([T("Feed", "Pakan"), "%CP", "FCR", T("Cost/kg gain", "Biaya/kg")],
                        r.Ranking.Select(x => (IReadOnlyList<string>)[x.Feed.Name, $"{x.Feed.ProteinPct}", N(x.Fcr, "0.00"), Fmt.Rp(x.CostPerKgGain)]))),
                    Panel(T("FCR by pond", "FCR per kolam"), SvgView(r.FcrSvg))));
        }
        return Live(R, out refresh,
            Combo(T("Species", "Spesies"), SpeciesItems, sp, v => { sp = v; refresh(); }),
            Slider("ABW g", 0.5, 600, abw, 0.5, "0.0", v => { abw = v; refresh(); }),
            Slider(T("Population", "Populasi"), 1000, 300_000, pop, 1000, "N0", v => { pop = v; refresh(); }),
            Slider(T("Water °C", "Air °C"), 22, 34, t, 0.5, "0.0", v => { t = v; refresh(); }),
            Slider(T("Dawn DO", "DO subuh"), 2, 8, dox, 0.1, "0.0", v => { dox = v; refresh(); }),
            Slider(T("Leftover %", "Sisa pakan %"), 0, 50, left, 1, "0", v => { left = v; refresh(); }));
    }

    // ───────── AI ─────────
    private static Control Ai()
    {
        string sp = "vaname"; double abw = 12, t = 30, dox = 3.4, nh3 = 1.3, den = 0.8;
        Action refresh = () => { };
        Control R()
        {
            var r = AiDemo.Run(sp, abw, t, dox, nh3, den);
            var reasons = new StackPanel();
            foreach (var x in r.Risk.Reasons) reasons.Children.Add(Note(x[_lang], null, "warn"));
            return Stack(
                Kpis(Kpi(T("Predicted ADG", "Prediksi ADG"), N(r.Prediction.AdgG, "0.00"), T("g/day", "g/hari"), $"{N(r.Prediction.WeightIn7DaysG, "0.0")} g / 7d"),
                     Kpi(T("Growth R²", "R² pertumbuhan"), N(r.GrowthR2, "0.000"), null, $"ML.NET R² {N(r.MlNetR2, "0.00")}"),
                     Kpi(T("Mortality risk", "Risiko kematian"), N(r.Risk.Probability * 100, "0"), "%", r.Risk.Level[_lang], r.Risk.Probability > 0.6 ? "alert" : r.Risk.Probability > 0.3 ? "warn" : "good"),
                     Kpi("AUC", N(r.MortalityAuc, "0.000")), Kpi(T("RL ration", "Ransum RL"), $"{r.Decision.RationFactor:P0}", null, null, r.Decision.RationFactor < 1 ? "warn" : "good")),
                Columns(3, Panel(T("What drives growth", "Pendorong pertumbuhan"), SvgView(r.ImportanceSvg)), Panel(T("Mortality reasons", "Penyebab risiko"), reasons),
                    Panel(T("Learned feeding policy", "Kebijakan pakan"), Stack(SvgView(r.PolicySvg), Note(r.Decision.Explanation[_lang])))));
        }
        return Live(R, out refresh,
            Combo(T("Species", "Spesies"), SpeciesItems, sp, v => { sp = v; refresh(); }),
            Slider("ABW g", 1, 500, abw, 1, "0", v => { abw = v; refresh(); }),
            Slider(T("Water °C", "Air °C"), 22, 35, t, 0.5, "0.0", v => { t = v; refresh(); }),
            Slider(T("Dawn DO", "DO subuh"), 1, 8, dox, 0.1, "0.0", v => { dox = v; refresh(); }),
            Slider(T("NH₃ × limit", "NH₃ × batas"), 0, 4, nh3, 0.1, "0.0", v => { nh3 = v; refresh(); }),
            Slider(T("Density ratio", "Rasio kepadatan"), 0.1, 1.3, den, 0.05, "0.00", v => { den = v; refresh(); }));
    }

    // ───────── disease ─────────
    private static Control Disease()
    {
        string sp = "vaname"; double t = 27, swing = 4, drop = 5, doc = 45, dox = 4;
        var signs = new HashSet<string>(DiseaseDemo.DefaultShrimpSigns);
        Action refresh = () => { };
        Control R()
        {
            var r = DiseaseDemo.Run(sp, signs, t, swing, drop, (int)doc, dox);
            var chips = new WrapPanel();
            foreach (var s in r.Symptoms)
            {
                var cb = new CheckBox { Content = s.Name[_lang], IsChecked = signs.Contains(s.Id), Margin = new Thickness(0, 0, 14, 2) };
                var id = s.Id;
                cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) signs.Add(id); else signs.Remove(id); refresh(); };
                chips.Children.Add(cb);
            }
            var top = r.Diagnosis.Top;
            return Stack(
                Panel(T("Clinical signs observed", "Gejala klinis"), chips),
                Columns([1.4, 1], Panel(T("Diagnosis", "Diagnosis"), Stack(SvgView(r.ProbabilitySvg),
                        Table([T("Disease", "Penyakit"), "P"], r.Diagnosis.Candidates.Take(5).Select(c => (IReadOnlyList<string>)[c.Disease.Name[_lang], $"{c.Probability:P0}"])),
                        Note(r.Diagnosis.Advice[_lang], null, "warn"))),
                    Panel(T("Environmental risk", "Risiko lingkungan"), Stack(SvgView(r.RiskSvg),
                        top is null ? Text("") : Stack(Text(top.Disease.Name[_lang], 18, weight: Avalonia.Media.FontWeight.SemiBold), Text(top.Disease.Treatment[_lang], 13), Text(top.Disease.Prevention[_lang], 13, "soft"))))));
        }
        return Live(R, out refresh,
            Combo(T("Species", "Spesies"), SpeciesItems, sp, v =>
            {
                sp = v; signs.Clear();
                foreach (var s in SpeciesCatalog.Get(v).Group == SpeciesGroup.Shrimp ? DiseaseDemo.DefaultShrimpSigns : DiseaseDemo.DefaultFishSigns) signs.Add(s);
                refresh();
            }),
            Slider(T("Water °C", "Air °C"), 20, 34, t, 0.5, "0.0", v => { t = v; refresh(); }),
            Slider(T("24 h swing", "Fluktuasi 24 jam"), 0, 8, swing, 0.5, "0.0", v => { swing = v; refresh(); }),
            Slider(T("Salinity drop", "Penurunan salinitas"), 0, 10, drop, 0.5, "0.0", v => { drop = v; refresh(); }),
            Slider("DOC", 1, 150, doc, 1, "0", v => { doc = v; refresh(); }),
            Slider(T("Dawn DO", "DO subuh"), 1, 8, dox, 0.1, "0.0", v => { dox = v; refresh(); }));
    }

    // ───────── vision ─────────
    private static Control Vision()
    {
        string kind = "shrimp"; double n = 18, sick = 0.35, seed = 8;
        Action refresh = () => { };
        Control R()
        {
            var r = VisionDemo.Run(kind == "shrimp", (int)n, sick, (ulong)seed);
            return Stack(
                Kpis(Kpi(T("Counted", "Terhitung"), r.Result.Count.ToString(), null, $"{T("truth", "aktual")} {r.TruthCount}", Math.Abs(r.Result.Count - r.TruthCount) <= 1 ? "good" : "warn"),
                     Kpi(T("Mean length", "Panjang rata-rata"), N(r.Result.MeanLengthCm), "cm", $"CV {N(r.Result.CvLengthPct, "0")} %"),
                     Kpi(T("Mean weight", "Bobot rata-rata"), N(r.Result.MeanWeightG), "g"), Kpi(T("Sample biomass", "Biomassa sampel"), N(r.Result.BiomassG, "0"), "g"),
                     Kpi(T("Suspicious", "Mencurigakan"), r.Result.Suspicious.ToString(), null, $"{T("truth", "aktual")} {r.TruthSick}", r.Result.Suspicious > 0 ? "alert" : "good")),
                Columns(2, Panel(T("Input image", "Citra masukan"), Png(r.Original, null, false)), Panel(T("Detections", "Deteksi"), Png(r.Annotated, null, false))),
                Panel(T("Per animal", "Per individu"), Table(["#", T("Length", "Panjang"), T("Weight", "Bobot"), T("Finding", "Temuan")],
                    r.Result.Animals.Select(a => (IReadOnlyList<string>)[a.Blob.Id.ToString(), $"{a.LengthCm:0.0} cm", $"{a.WeightG:0.0} g", AquaLabels.Describe(a.Finding)[_lang]]), 1)));
        }
        return Live(R, out refresh,
            Combo(T("Animals", "Biota"), [("shrimp", T("Shrimp tray", "Nampan udang")), ("fish", T("Fish sample", "Sampel ikan"))], kind, v => { kind = v; refresh(); }),
            Slider(T("Count", "Jumlah"), 4, 30, n, 1, "0", v => { n = v; refresh(); }),
            Slider(T("Diseased share", "Porsi sakit"), 0, 1, sick, 0.05, "0.00", v => { sick = v; refresh(); }),
            Slider(T("Image seed", "Seed citra"), 1, 40, seed, 1, "0", v => { seed = v; refresh(); }));
    }

    // ───────── twin ─────────
    private static Control Twin()
    {
        double hours = 30; string pond = "K-02"; string auto = "on";
        Action refresh = () => { };
        Control R()
        {
            var r = TwinDemo.Run((int)hours, auto == "on");
            var s = r.States.First(x => x.PondId == pond);
            bool aer = s.AeratorOn || r.Commands.Any(c => c.DeviceId.EndsWith(pond.ToLowerInvariant()));
            double ratio = s.DensityKgM3 / DemoFarm.Species.MaxBiomassKgM3;
            Control view3d = Screenshots.Active
                ? (Twin3D.RenderPng(s, aer, ratio) is { } png ? Png(png, 440, false) : SvgView(r.MapSvg, 420))
                : Twin3D.CreateView(s, aer, ratio) ?? Stack(Note(T("ThreeNet 3D needs a GPU adapter; showing the 2-D map instead.", "ThreeNet 3D butuh GPU; menampilkan peta 2-D."), null, "warn"), SvgView(r.MapSvg, 420));
            var decisions = new StackPanel();
            foreach (var d in r.Decisions.Take(10))
                decisions.Children.Add(Note(d.Title[_lang], d.Rationale[_lang], d.Severity == AlertSeverity.Critical ? "critical" : d.Severity == AlertSeverity.Warning ? "warn" : "info", $"{d.PondId} · {d.Kind}"));
            return Stack(
                Kpis(Kpi(T("Readings ingested", "Data masuk"), r.Readings.ToString("N0"), null, $"{r.Outliers} {T("outliers rejected", "outlier ditolak")}"),
                     Kpi(T("Standing biomass", "Biomassa"), N(r.States.Sum(x => x.BiomassKg), "N0"), "kg"),
                     Kpi(T("Decisions", "Keputusan"), r.Decisions.Count.ToString(), null, $"{r.Decisions.Count(d => d.Severity == AlertSeverity.Critical)} {T("critical", "kritis")}"),
                     Kpi(T("Commands sent", "Perintah"), r.Commands.Count.ToString(), null, "aerator_on")),
                Columns([1.5, 1], Panel(T("3D digital pond", "Kolam digital 3D"), view3d, $"{pond} · ThreeNet"), Panel(T("Farm map", "Peta tambak"), SvgView(r.MapSvg), "DO mg/L")),
                Columns(2, Panel(T("Explained decisions", "Keputusan beralasan"), decisions),
                    Panel(T("Pond states", "Status kolam"), Stack(Table([T("Pond", "Kolam"), "DOC", "ABW", T("Biomass", "Biomassa"), "WQI", T("Dawn DO", "DO subuh")],
                        r.States.Select(x => (IReadOnlyList<string>)[x.PondId, x.DayOfCulture.ToString(), $"{x.AbwG:0.0} g", $"{x.BiomassKg:N0} kg", N(x.Wqi, "0"), N(x.ForecastDawnDo ?? double.NaN)])),
                        SvgView(r.HeatSvg)))));
        }
        return Live(R, out refresh,
            Slider(T("History hours", "Riwayat jam"), 6, 48, hours, 2, "0", v => { hours = v; refresh(); }),
            Combo(T("Auto-actuate", "Aktuasi otomatis"), [("on", T("on", "aktif")), ("off", T("off", "mati"))], auto, v => { auto = v; refresh(); }),
            Combo(T("3D pond", "Kolam 3D"), DemoFarm.Farm.Ponds.Select(p => (p.Id, p.Id)).ToList(), pond, v => { pond = v; refresh(); }));
    }

    // ───────── IoT ─────────
    private static Control IoT()
    {
        double dox = 5.12, t = 29.1, spike = 14;
        Action refresh = () => { };
        Control R()
        {
            var r = IoTDemo.Run(dox, t, spike);
            return Stack(
                Columns(3,
                    Panel("Modbus RTU", Stack(Eyebrow(T("request", "permintaan")), Ui.Code(r.ModbusRequestHex), Eyebrow(T("response", "respons")), Ui.Code(r.ModbusResponseHex),
                        Table(["", ""], r.ModbusValues.Select(x => (IReadOnlyList<string>)[x.Kind.ToString(), N(x.Value, "0.##")]))), "unit 7 · 0x03"),
                    Panel("LoRaWAN", Stack(Ui.Code(r.LoraHex), Table(["", ""], r.LoraReadings.Select(x => (IReadOnlyList<string>)[x.Kind.ToString(), $"{N(x.Value, "0.###")} {x.Unit}"]))), "12-byte codec"),
                    Panel("MQTT", Stack(Eyebrow("topic"), Ui.Code(r.MqttTopic), Eyebrow("payload"), Ui.Code(r.MqttPayload),
                        Table(["", ""], r.MqttReadings.Select(x => (IReadOnlyList<string>)[x.Kind.ToString(), $"{N(x.Value, "0.##")} {x.Unit}"]))), "ESP32")),
                Columns([1.5, 1], Panel(T("Edge analytics", "Analitik edge"), SvgView(r.EdgeSvg), $"Hampel + EMA · {r.Outliers} outliers"),
                    Panel(T("Firmware sketch", "Contoh firmware"), Ui.Code(r.FirmwareSnippet))));
        }
        return Live(R, out refresh,
            Slider("DO mg/L", 0.5, 9, dox, 0.01, "0.00", v => { dox = v; refresh(); }),
            Slider(T("Temp °C", "Suhu °C"), 20, 35, t, 0.1, "0.0", v => { t = v; refresh(); }),
            Slider(T("Spike value", "Nilai lonjakan"), 0, 20, spike, 0.5, "0.0", v => { spike = v; refresh(); }));
    }

    // ───────── GIS ─────────
    private static Control Gis()
    {
        double thr = 40, buf = 50;
        Action refresh = () => { };
        Control R()
        {
            var r = GisDemo.Run((int)thr, buf);
            return Stack(
                Kpis(Kpi(T("Water surface", "Luas air"), N(r.FarmAreaM2, "N0"), "m²"), Kpi(T("Buffer zone", "Zona penyangga"), N(r.BufferAreaM2 / 10000, "0.00"), "ha"),
                     Kpi(T("Drainage cells", "Sel drainase"), r.Channels.ToString("N0")), Kpi(T("Best water source", "Sumber air terbaik"), r.Sources[0].Source.Id, null, $"{r.Sources[0].Suitability:P0}", "good")),
                Columns(2, Panel(T("Interpolated DO (IDW)", "DO terinterpolasi (IDW)"), Png(r.DoPng, 380)), Panel(T("Biomass by pond", "Biomassa per kolam"), SvgView(r.MapSvg, 380))),
                Columns(2, Panel(T("Coastal DEM and D8 drainage", "DEM pesisir dan drainase D8"), Png(r.DrainPng, 360)),
                    Panel(T("Water sources", "Sumber air"), Stack(Table([T("Source", "Sumber"), T("Distance", "Jarak"), "Head", T("Score", "Skor")],
                        r.Sources.Select(s => (IReadOnlyList<string>)[s.Source.Id, $"{s.DistanceM:N0} m", $"{s.HeadM:0.0} m", $"{s.Suitability * 100:0}"])), Ui.Code(r.GeoJsonSnippet)))));
        }
        return Live(R, out refresh,
            Slider(T("Channel threshold", "Ambang saluran"), 10, 200, thr, 5, "0", v => { thr = v; refresh(); }),
            Slider(T("Green-belt buffer m", "Sabuk hijau m"), 10, 200, buf, 5, "0", v => { buf = v; refresh(); }));
    }

    // ───────── weather ─────────
    private static Control Weather()
    {
        double wet = 1.4, depth = 1.3, sal = 18, seed = 31; string sys = nameof(CultureSystem.LinedPond);
        Action refresh = () => { };
        Control R()
        {
            var r = WeatherDemo.Run(wet, Enum.Parse<CultureSystem>(sys), depth, sal, (ulong)seed);
            var impacts = new StackPanel();
            foreach (var i in r.Impact.Impacts) impacts.Children.Add(Note(i.Effect[_lang], i.Advice[_lang], i.Severity >= ImpactSeverity.High ? "critical" : "warn", i.Code));
            if (r.Impact.Impacts.Count == 0) impacts.Children.Add(Note(T("No significant weather risk.", "Tidak ada risiko cuaca berarti."), null, "good"));
            return Stack(
                Kpis(Kpi(T("Water temperature", "Suhu air"), $"{r.Impact.WaterTempMinC}–{r.Impact.WaterTempMaxC}", "°C"), Kpi(T("Rain 72 h", "Hujan 72 jam"), N(r.Impact.TotalRainMm, "0"), "mm"),
                     Kpi(T("Salinity drop", "Penurunan salinitas"), N(r.Impact.SalinityDropPpt), "ppt", null, r.Impact.SalinityDropPpt > 2 ? "warn" : null),
                     Kpi(T("Light / clear sky", "Cahaya"), N(r.Impact.PhotosynthesisIndex * 100, "0"), "%", null, r.Impact.PhotosynthesisIndex < 0.45 ? "warn" : "good"),
                     Kpi(T("Overall impact", "Dampak"), r.Impact.Overall.ToString(), null, null, r.Impact.Overall >= ImpactSeverity.High ? "alert" : null)),
                Columns(2, Panel(T("72-hour forecast", "Prakiraan 72 jam"), Stack(SvgView(r.ForecastSvg), SvgView(r.RainSvg))), Panel(T("Predicted pond effects", "Dampak ke kolam"), impacts)),
                Columns([1, 1.5], Panel(T("Climate risk matrix", "Matriks risiko iklim"), SvgView(r.MatrixSvg, 320)),
                    Panel(T("Yearly hazards", "Bahaya tahunan"), Table(["#", T("Hazard", "Bahaya"), T("Events/yr", "Kejadian/thn"), "L×I"],
                        r.Climate.Hazards.Select((h, k) => (IReadOnlyList<string>)[(k + 1).ToString(), $"{h.Name[_lang]} — {h.Level[_lang]}", N(h.EventsPerYear), $"{h.Likelihood}×{h.Impact}"]), 2), r.System.ToString())));
        }
        return Live(R, out refresh,
            Slider(T("Wet season", "Musim hujan"), 0, 2.5, wet, 0.1, "0.0", v => { wet = v; refresh(); }),
            Combo(T("System", "Sistem"), Enum.GetNames<CultureSystem>().Select(n => (n, n)).ToList(), sys, v => { sys = v; refresh(); }),
            Slider(T("Depth m", "Kedalaman m"), 0.6, 6, depth, 0.1, "0.0", v => { depth = v; refresh(); }),
            Slider(T("Salinity ppt", "Salinitas"), 0, 35, sal, 1, "0", v => { sal = v; refresh(); }),
            Slider(T("Scenario seed", "Seed"), 1, 60, seed, 1, "0", v => { seed = v; refresh(); }));
    }

    // ───────── stock ─────────
    private static Control Stock()
    {
        double eg = 0.07, tc = 0.4, m = 0.9;
        Action refresh = () => { };
        Control R()
        {
            var r = StockDemo.Run(eg, tc, m);
            string kobe = r.Fit.Status switch { KobeStatus.Healthy => T("Green", "Hijau"), KobeStatus.Overfished => T("Red", "Merah"), _ => T("Amber", "Kuning") };
            return Stack(
                Kpis(Kpi("MSY", N(r.Fit.Msy, "N0"), "t", $"r {N(r.Fit.R, "0.00")} · K {N(r.Fit.K, "N0")}"), Kpi("B/Bmsy", N(r.Fit.BOverBmsy, "0.00"), null, null, r.Fit.BOverBmsy >= 1 ? "good" : "alert"),
                     Kpi("F/Fmsy", N(r.Fit.FOverFmsy, "0.00"), null, null, r.Fit.FOverFmsy <= 1 ? "good" : "alert"),
                     Kpi("Kobe", kobe, null, DynamicSchaeferFit.StatusName(r.Fit.Status)[_lang], r.Fit.Status == KobeStatus.Healthy ? "good" : "alert"),
                     Kpi("Z", N(r.Z, "0.00"), null, $"Pauly M {N(r.PaulyM, "0.00")} · E {N(r.E, "0.00")}", r.E > 0.5 ? "warn" : "good")),
                Columns(2, Panel(T("CPUE fit", "Kecocokan CPUE"), Stack(SvgView(r.CpueSvg), Table(["Model", "MSY", "Emsy", "R²"], [
                        ["Schaefer", N(r.Schaefer.Msy, "N0"), N(r.Schaefer.EffortAtMsy, "N0"), N(r.Schaefer.RSquared, "0.00")], ["Fox", N(r.Fox.Msy, "N0"), N(r.Fox.EffortAtMsy, "N0"), N(r.Fox.RSquared, "0.00")]]))),
                    Panel(T("Kobe plot", "Plot Kobe"), SvgView(r.KobeSvg, 330))),
                Panel(T("Beverton–Holt yield per recruit", "Yield per recruit Beverton–Holt"), Stack(SvgView(r.YprSvg), Text($"Fmax {N(r.Ypr.Fmax, "0.00")} · F0.1 {N(r.Ypr.F01, "0.00")} · F40% {N(r.Ypr.F40, "0.00")}", 13, "soft"))));
        }
        return Live(R, out refresh,
            Slider(T("Effort growth /yr", "Pertumbuhan upaya"), 0, 0.12, eg, 0.005, "0.000", v => { eg = v; refresh(); }),
            Slider(T("Age at first capture", "Umur tertangkap"), 0.3, 3, tc, 0.1, "0.0", v => { tc = v; refresh(); }),
            Slider("M", 0.3, 1.8, m, 0.05, "0.00", v => { m = v; refresh(); }));
    }

    // ───────── population ─────────
    private static Control Population()
    {
        double f = 0.6, cv = 0.3, mig = 0.02; string pred = "yes";
        Action refresh = () => { };
        Control R()
        {
            var r = PopulationDemo.Run(f, pred == "yes", cv, mig);
            return Stack(
                Kpis(Kpi("Leslie λ", N(r.Lambda, "0.000"), null, r.Lambda > 1 ? T("growing", "bertumbuh") : T("declining", "menurun"), r.Lambda > 1 ? "good" : "warn"),
                     Kpi(T("SSB, no fishing", "SSB tanpa tangkap"), N(r.NoFishing[^1].SpawningBiomass, "N0"), "kg"), Kpi(T("SSB at F", "SSB pada F"), N(r.ConstantF[^1].SpawningBiomass, "N0"), "kg"),
                     Kpi(T("40-yr catch, F", "Tangkapan F"), N(r.ConstantF.Sum(x => x.Catch), "N0"), "kg"), Kpi(T("40-yr catch, HCR", "Tangkapan HCR"), N(r.HockeyStick.Sum(x => x.Catch), "N0"), "kg")),
                Columns(2, Panel(T("Spawning stock biomass", "Biomassa pemijah"), SvgView(r.BiomassSvg)), Panel(T("Catch", "Tangkapan"), SvgView(r.CatchSvg))),
                Panel(T("Predator–prey", "Mangsa–pemangsa"), SvgView(r.PredatorSvg)));
        }
        return Live(R, out refresh,
            Slider(T("Fishing mortality F", "Mortalitas tangkap F"), 0, 1.5, f, 0.05, "0.00", v => { f = v; refresh(); }),
            Slider(T("Recruitment CV", "CV rekrutmen"), 0, 0.8, cv, 0.05, "0.00", v => { cv = v; refresh(); }),
            Slider(T("Emigration /yr", "Emigrasi"), 0, 0.2, mig, 0.01, "0.00", v => { mig = v; refresh(); }),
            Combo(T("Predator", "Pemangsa"), [("yes", T("present", "ada")), ("no", T("absent", "tidak ada"))], pred, v => { pred = v; refresh(); }));
    }

    // ───────── analytics ─────────
    private static Control Analytics()
    {
        string pond = "K-04"; double price = 72_000;
        Action refresh = () => { };
        Control R()
        {
            var r = AnalyticsDemo.Run(pond, price);
            var k = r.Selected;
            var bench = new StackPanel();
            foreach (var b in k.Benchmarks) bench.Children.Add(Note(b.Comment[_lang], null, b.Rating is AquaCulture.Analytics.BenchmarkRating.Poor or AquaCulture.Analytics.BenchmarkRating.BelowTarget ? "warn" : "good", b.Rating.ToString()));
            return Stack(
                Kpis(Kpi("ABW", N(k.Abw), "g", $"ADG {N(k.Adg, "0.00")} · SGR {N(k.Sgr)}"), Kpi("SR", N(k.Sr, "0"), "%"), Kpi("FCR", N(k.Fcr, "0.00")),
                     Kpi(T("Biomass", "Biomassa"), N(k.BiomassKg, "N0"), "kg", $"{N(k.ProductivityTonHa)} t/ha"),
                     Kpi(T("Profit", "Laba"), Fmt.Rp(k.Profit), null, $"BEP {Fmt.Rp(k.BreakEvenPrice)}/kg · B/C {N(k.Bcr, "0.00")}", k.Profit > 0 ? "good" : "alert")),
                Columns(3, Panel(T("Cost structure", "Struktur biaya"), SvgView(r.CostSvg), r.PondId), Panel(T("FCR by pond", "FCR per kolam"), SvgView(r.FcrSvg)), Panel(T("Survival by pond", "SR per kolam"), SvgView(r.SrSvg))),
                Columns(2, Panel(T("Benchmarks", "Pembanding"), bench), Panel(T("Farm summary", "Ringkasan"), Table(["", ""], [
                    [T("Active cycles", "Siklus aktif"), $"{r.Summary.ActiveCycles} / {r.Summary.Ponds}"], [T("Standing biomass", "Biomassa"), $"{N(r.Summary.StandingBiomassKg, "N0")} kg"],
                    [T("Mean FCR", "FCR rata-rata"), N(r.Summary.MeanFcr, "0.00")], [T("Costs to date", "Biaya"), Fmt.Rp(r.Summary.Costs)], [T("Value of stock", "Nilai stok"), Fmt.Rp(r.Summary.Revenue)]]))));
        }
        return Live(R, out refresh,
            Combo(T("Pond", "Kolam"), DemoFarm.Farm.Ponds.Select(p => (p.Id, p.Id)).ToList(), pond, v => { pond = v; refresh(); }),
            Slider(T("Sale price Rp/kg", "Harga jual Rp/kg"), 40_000, 110_000, price, 1000, "N0", v => { price = v; refresh(); }));
    }

    // ───────── reporting ─────────
    private static Control Reporting()
    {
        var std = ComplianceStandard.Cbib;
        var answers = ReportingDemo.DefaultAnswers(std);
        Action refresh = () => { };
        Control R()
        {
            var r = ReportingDemo.Run(std, answers, _lang);
            var list = new StackPanel { Spacing = 4 };
            foreach (var it in r.Checklist)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetNames<ComplianceAnswer>(), SelectedItem = answers[it.Id].ToString(), MinWidth = 140 };
                var id = it.Id;
                combo.SelectionChanged += (_, _) => { if (Enum.TryParse<ComplianceAnswer>(combo.SelectedItem?.ToString(), out var a) && a != answers[id]) { answers[id] = a; refresh(); } };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { Stack(Eyebrow($"{it.Id} · {it.Category[_lang]} · {it.Severity}"), Text(it.Requirement[_lang], 13)), combo } };
                Grid.SetColumn(combo, 1);
                list.Children.Add(new Border { Background = Brush("Paper"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Child = row });
            }
            var files = new WrapPanel();
            foreach (var f in r.Files)
            {
                var b = new Button { Content = Stack(Text(Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant(), 14, weight: Avalonia.Media.FontWeight.Bold), Text($"{f.Bytes.Length / 1024.0:0.0} KB", 11, "soft")), Margin = new Thickness(0, 0, 8, 8), MinWidth = 90 };
                var file = f;
                b.Click += async (_, _) =>
                {
                    var top = TopLevel.GetTopLevel(b);
                    if (top is null) return;
                    var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = file.Name });
                    if (target is null) return;
                    await using var s = await target.OpenWriteAsync();
                    await s.WriteAsync(file.Bytes);
                };
                files.Children.Add(b);
            }
            var ncs = new StackPanel();
            foreach (var n in r.Compliance.NonConformities) ncs.Children.Add(Note(n.Item.Requirement[_lang], n.Answer.ToString(), n.Item.Severity >= ComplianceSeverity.Major && n.Answer == ComplianceAnswer.No ? "critical" : "warn", n.Item.Id));
            return Stack(
                Kpis(Kpi(T("Score", "Skor"), N(r.Compliance.ScorePct, "0"), "%", null, r.Compliance.ScorePct >= 90 ? "good" : "warn"),
                     Kpi(T("Readiness", "Kesiapan"), r.Compliance.Readiness.ToString(), null, r.Compliance.ReadinessText[_lang], r.Compliance.Readiness == ReadinessLevel.Ready ? "good" : "alert"),
                     Kpi(T("Major gaps", "Kekurangan mayor"), r.Compliance.Majors.ToString(), null, null, r.Compliance.Majors > 0 ? "alert" : "good"), Kpi(T("Clauses", "Butir"), r.Compliance.Applicable.ToString())),
                Columns([1.4, 1], Panel(T("Self-assessment", "Penilaian mandiri"), list, std.ToString()),
                    Panel(T("Export the report", "Ekspor laporan"), Stack(Text(T("Save as Excel, PDF, Word, CSV, JSON, HTML or Markdown.", "Simpan sebagai Excel, PDF, Word, CSV, JSON, HTML atau Markdown."), 13, "soft"), files, ncs))));
        }
        return Live(R, out refresh,
            Combo(T("Standard", "Standar"), Enum.GetNames<ComplianceStandard>().Select(n => (n, n.ToUpperInvariant())).ToList(), std.ToString(), v =>
            {
                std = Enum.Parse<ComplianceStandard>(v); answers = ReportingDemo.DefaultAnswers(std); refresh();
            }));
    }

    // ───────── agent ─────────
    private static Control Agent()
    {
        var agent = AgentDemo.Create();
        var log = new StackPanel { Spacing = 8 };
        var input = new TextBox { PlaceholderText = T("Type a question…", "Ketik pertanyaan…"), MinWidth = 420 };
        var send = new Button { Content = T("Ask", "Tanya") };
        async Task Ask(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            input.Text = "";
            log.Children.Add(new Border { Background = Brush("Ink"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 620, Child = Text(q, 14, fg: Brush("Paper")) });
            try
            {
                var a = await agent.AskAsync(q);
                log.Children.Add(new Border { Classes = { "panel" }, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 760, Child = Stack(Text(a.Text, 13.5), Eyebrow("⚙ " + string.Join(" · ", a.ToolsUsed))) });
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException) { log.Children.Add(Note(e.Message, null, "critical")); }
        }
        send.Click += async (_, _) => await Ask(input.Text ?? "");
        var suggestions = new StackPanel { Spacing = 6 };
        foreach (var (en, id) in AgentDemo.Suggestions)
        {
            var q = _lang == "id" ? id : en;
            var b = new Button { Content = Text(q, 12.5), HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += async (_, _) => await Ask(q);
            suggestions.Children.Add(b);
        }
        if (Screenshots.Active)
        {
            var first = _lang == "id" ? AgentDemo.Suggestions[0].Id : AgentDemo.Suggestions[0].En;
            Ask(first).GetAwaiter().GetResult();
        }
        return Columns([1.5, 1],
            Panel("AquaAgent", Stack(log, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { input, send } }), agent.Model.Name),
            Panel(T("Try", "Coba"), Stack(suggestions, Text(T("Set AQUA_LLM_PROVIDER / ENDPOINT / API_KEY / MODEL to use Azure OpenAI, OpenAI, Claude, DeepSeek, Hugging Face or Ollama; otherwise the offline model answers from the same tools.",
                "Atur AQUA_LLM_PROVIDER / ENDPOINT / API_KEY / MODEL untuk memakai Azure OpenAI, OpenAI, Claude, DeepSeek, Hugging Face atau Ollama; tanpa itu model offline menjawab dengan alat yang sama."), 12, "soft"))));
    }
}
