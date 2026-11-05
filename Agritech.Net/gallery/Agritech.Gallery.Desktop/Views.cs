using System.Globalization;
using Agritech.AI.Agent;
using Agritech.Gallery.Shared;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using static Agritech.Gallery.Desktop.Ui;

namespace Agritech.Gallery.Desktop;

/// <summary>Desktop renderings of every gallery use case (shared scenario logic from Agritech.Gallery.Shared).</summary>
internal static class Views
{
    private static string F(double v, string f = "0.0") => v.ToString(f, CultureInfo.InvariantCulture);

    public static Control Build(string slug, string lang)
    {
        string T(string en, string id) => lang == "id" ? id : en;
        return slug switch
        {
            "ndvi" => Ndvi(T),
            "soil" => Soil(T),
            "crops" => Crops(T, lang),
            "simulation" => Simulation(T),
            "irrigation" => Irrigation(T),
            "drones" => Drones(T),
            "twin" => Twin(T),
            "robotics" => Robotics(T),
            "livestock" => Livestock(T),
            "aquaculture" => Aqua(T),
            "market" => Market(T),
            "genomics" => Genomics(T),
            "agent" => Agent(T, lang),
            _ => Text("Unknown example."),
        };
    }

    /// <summary>Re-renders its content whenever <c>Refresh</c> is invoked (slider-driven views).</summary>
    private sealed class Live : ContentControl
    {
        private readonly Func<Control> _render;
        public Live(Func<Control> render) { _render = render; Content = render(); }
        public void Refresh() => Content = _render();
    }

    private static Control Stack(params Control[] items)
    {
        var sp = new StackPanel { Spacing = 14 };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    private static Control Ndvi(Func<string, string, string> T)
    {
        int seed = 7, patches = 3;
        Live? live = null;
        live = new Live(() =>
        {
            var r = NdviDemo.Run(seed, patches);
            double healthy = r.Stats.HealthShare.Where(k => k.Key >= Agritech.RemoteSensing.VegetationHealth.Healthy).Sum(k => k.Value);
            return Stack(
                Kpis(Kpi(T("Mean NDVI", "Rata-rata NDVI"), F(r.Stats.Stats.Mean, "0.000"), null, $"σ {F(r.Stats.Stats.StdDev, "0.000")}", "good"),
                     Kpi(T("Field area", "Luas lahan"), F(DemoFarm.Field.AreaHectares, "0.00"), "ha", $"{r.Stats.PixelCount:N0} px @ 5 m"),
                     Kpi(T("Healthy canopy", "Kanopi sehat"), F(healthy, "0"), "%"),
                     Kpi("Kernel", r.Backend.ToString(), null, $"{r.MegapixelsPerSecond:N0} MP/s")),
                Columns(3,
                    Panel(T("True colour", "Warna asli"), Png(r.TruePng, 300)),
                    Panel("NDVI", Png(r.NdviPng, 300)),
                    Panel(T("Management zones", "Zona pengelolaan"), Stack(Png(r.ZonesPng, 300),
                        Table([T("Zone", "Zona"), "NDVI"], [[T("1 · low", "1 · rendah"), F(r.ZoneMeans[0], "0.00")], [T("2 · medium", "2 · sedang"), F(r.ZoneMeans[1], "0.00")], [T("3 · high", "3 · tinggi"), F(r.ZoneMeans[2], "0.00")]], 1)))));
        });
        return Stack(Ui.Controls(
            Slider(T("Scene seed", "Seed citra"), 1, 40, seed, 1, v => { seed = (int)v; live!.Refresh(); }, "0"),
            Slider(T("Stress patches", "Titik cekaman"), 0, 8, patches, 1, v => { patches = (int)v; live!.Refresh(); }, "0")), live);
    }

    private static Control Soil(Func<string, string, string> T)
    {
        double ph = 5.4, oc = 1.2;
        Live? live = null;
        live = new Live(() =>
        {
            var r = SoilDemo.Run(DemoFarm.Soil with { Ph = ph, OrganicCarbonPct = oc });
            var rows = r.Advice.Schedule.SelectMany(a => a.Products.Select((p, i) => new[] { i == 0 ? a.Stage : "", i == 0 ? $"{a.FromDap}–{a.ToDap}" : "", p.Product.Name, F(p.KgPerHa), F(p.TotalKg) }));
            return Stack(
                Kpis(Kpi(T("Soil health", "Kesehatan tanah"), F(r.Report.Score, "0"), "/100", r.Report.Grade, r.Report.Score >= 65 ? "good" : null),
                     Kpi(T("Texture", "Tekstur"), r.Texture.ToString(), null, "USDA"),
                     Kpi(T("Available water", "Air tersedia"), F(r.Hydraulics.AvailableWaterMmPerM, "0"), "mm/m"),
                     Kpi(T("Fertilizer cost", "Biaya pupuk"), F(r.Advice.Plan.TotalCost / 1e6, "0.0"), T("M Rp", "jt Rp"))),
                Columns(2,
                    Panel(T("Indicator scores", "Skor indikator"), Stack(SvgView(r.IndicatorSvg), Text(string.Join("\n", r.Report.Recommendations.Select(x => "• " + x)), 13))),
                    Panel(T("Fertilizer schedule", "Jadwal pemupukan"), Table([T("Application", "Aplikasi"), T("DAP", "HST"), T("Product", "Produk"), "kg/ha", "Total kg"], rows, 1, 3, 4))));
        });
        return Stack(Ui.Controls(
            Slider("pH", 4, 8.5, ph, 0.1, v => { ph = v; live!.Refresh(); }, "0.0"),
            Slider(T("Organic C %", "C organik %"), 0.3, 4, oc, 0.1, v => { oc = v; live!.Refresh(); }, "0.0")), live);
    }

    private static Control Crops(Func<string, string, string> T, string lang)
    {
        double temp = 26.5, rain = 1400, ph = 5.6, elev = 300;
        Live? live = null;
        live = new Live(() => Panel(T("Ranked crops", "Peringkat tanaman"), Table(
            ["#", T("Crop", "Tanaman"), T("Class", "Kelas"), T("Score", "Skor"), T("Limiting factors", "Faktor pembatas")],
            CropDemo.Run(temp, rain, ph, elev).Select((s, i) => new[] { (i + 1).ToString(), lang == "id" ? s.Crop.NameId : s.Crop.Name, s.Class.ToString(), F(s.Score * 100, "0") + "%", s.LimitingFactors.Count == 0 ? "—" : string.Join("; ", s.LimitingFactors) }), 0, 3)));
        return Stack(Ui.Controls(
            Slider(T("Mean temp °C", "Suhu °C"), 12, 32, temp, 0.5, v => { temp = v; live!.Refresh(); }, "0.0"),
            Slider(T("Season rain mm", "Hujan mm"), 200, 3500, rain, 50, v => { rain = v; live!.Refresh(); }, "0"),
            Slider("pH", 4, 8.5, ph, 0.1, v => { ph = v; live!.Refresh(); }, "0.0"),
            Slider(T("Elevation m", "Ketinggian m"), 0, 2500, elev, 50, v => { elev = v; live!.Refresh(); }, "0")), live);
    }

    private static Control Simulation(Func<string, string, string> T)
    {
        var r = SimulationDemo.Run("maize", 120, 12);
        return Stack(
            Kpis(Kpi(T("Rainfed yield", "Hasil tadah hujan"), F(r.Rainfed.YieldTonHa, "0.00"), "t/ha"),
                 Kpi(T("Irrigated yield", "Hasil irigasi"), F(r.Irrigated.YieldTonHa, "0.00"), "t/ha", $"+{F(r.Irrigated.TotalIrrigationMm, "0")} mm", "good"),
                 Kpi(T("Water productivity", "Produktivitas air"), F(r.Irrigated.WaterProductivityKgM3, "0.00"), "kg/m³"),
                 Kpi(T("Maturity", "Masak"), r.Rainfed.MaturityDate?.ToString("dd MMM", CultureInfo.InvariantCulture) ?? "—")),
            Columns(2,
                Panel(T("Leaf area index", "Indeks luas daun"), SvgView(r.LaiSvg)),
                Panel(T("Biomass", "Biomassa"), SvgView(r.BiomassSvg)),
                Panel(T("Soil water", "Air tanah"), SvgView(r.WaterSvg)),
                Panel(T("Scenarios", "Skenario"), SvgView(r.ScenarioSvg), T("12 stochastic years", "12 tahun stokastik"))));
    }

    private static Control Irrigation(Func<string, string, string> T)
    {
        double dap = 55;
        Live? live = null;
        live = new Live(() =>
        {
            var r = IrrigationDemo.Run("chili", (int)dap);
            return Stack(
                Kpis(Kpi(T("Decision today", "Keputusan"), r.Recommendation.IrrigateNow ? T("Irrigate", "Irigasi") : T("Wait", "Tunda"), null, null, r.Recommendation.IrrigateNow ? "alert" : "good"),
                     Kpi(T("Gross depth", "Kedalaman bruto"), F(r.Recommendation.GrossDepthMm), "mm"),
                     Kpi(T("Volume", "Volume"), F(r.Recommendation.VolumeM3, "0"), "m³"),
                     Kpi(T("Depletion", "Deplesi"), F(r.Recommendation.CurrentDepletionPct, "0"), "% TAW")),
                Panel(T("Root-zone water balance", "Neraca air zona akar"), Stack(SvgView(r.DepletionSvg, 320), Text(r.Recommendation.Reason, 13, "soft")), "FAO-56"));
        });
        return Stack(Ui.Controls(Slider(T("Days after planting", "Hari setelah tanam"), 10, 100, dap, 1, v => { dap = v; live!.Refresh(); }, "0")), live);
    }

    private static Control Drones(Func<string, string, string> T)
    {
        double alt = 80;
        Live? live = null;
        live = new Live(() =>
        {
            var r = DroneDemo.Run(alt);
            return Stack(
                Kpis(Kpi("GSD", F(r.Plan.GsdCm, "0.00"), "cm/px", "DJI Mavic 3M"),
                     Kpi(T("Flight time", "Waktu terbang"), F(r.Plan.FlightTimeMin), "min"),
                     Kpi(T("Photos", "Foto"), r.Plan.EstimatedPhotos.ToString()),
                     Kpi(T("Weed patches", "Rumpun gulma"), r.Weeds.Weeds.Count.ToString(), null, $"{F(r.Weeds.WeedCoverPct)}%", "alert")),
                Columns(2,
                    Panel(T("Survey flight lines", "Jalur terbang survei"), SvgView(r.MissionSvg, 360)),
                    Panel(T("Weeds between rows", "Gulma di antara barisan"), Columns(1, Png(r.PhotoPng, 170), Png(r.OverlayPng, 170)), "ExG + Otsu")));
        });
        return Stack(Ui.Controls(Slider(T("Altitude m", "Ketinggian m"), 30, 120, alt, 5, v => { alt = v; live!.Refresh(); }, "0")), live);
    }

    private static Control Twin(Func<string, string, string> T)
    {
        var session = new TwinSession();
        for (int i = 0; i < 12; i++) session.Tick();
        Live? live = null;
        live = new Live(() =>
        {
            var snap = session.Twin.Snapshot();
            var decisions = new StackPanel();
            foreach (var d in snap.Decisions.OrderByDescending(d => d.CreatedAt).Take(6)) decisions.Children.Add(Decision(d.Title, d.Rationale, d.Type.ToString()));
            return Stack(
                Text($"{session.Now:ddd dd MMM yyyy · HH:mm} WIB · {snap.ReadingsProcessed:N0} {T("readings", "data")}", 12, "mono"),
                Columns(2,
                    Panel(snap.Farm, SvgView(session.IsometricSvg(), 380), T("height = progress, colour = NDVI", "tinggi = kemajuan, warna = NDVI")),
                    Panel(T("Decisions", "Keputusan"), decisions)),
                Panel(T("Fields", "Petak"), Table([T("Field", "Petak"), T("Crop", "Tanaman"), T("DAP", "HST"), T("Stage", "Fase"), T("Soil moisture", "Kelembapan"), "NDVI"],
                    snap.Fields.Select(f => new[] { f.Name, f.Crop ?? "", f.Dap?.ToString() ?? "", f.Stage ?? "", $"{f.SoilMoisture:0.0} %", $"{f.Ndvi:0.00}" }), 2, 4, 5)));
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { session.Tick(); live.Refresh(); };
        var play = new Button { Content = T("▶ Play hours", "▶ Putar jam"), Classes = { "primary" } };
        play.Click += (_, _) =>
        {
            if (timer.IsEnabled) { timer.Stop(); play.Content = T("▶ Play hours", "▶ Putar jam"); }
            else { timer.Start(); play.Content = T("❚❚ Pause", "❚❚ Jeda"); }
        };
        live.DetachedFromVisualTree += (_, _) => timer.Stop();
        return Stack(Ui.Controls(play), live);
    }

    private static Control Robotics(Func<string, string, string> T)
    {
        var r = RoboticsDemo.Run();
        return Stack(
            Kpis(Kpi(T("Passes", "Lintasan"), r.Passes.ToString()), Kpi(T("Path length", "Panjang jalur"), F(r.PathLengthM / 1000, "0.00"), "km"),
                 Kpi(T("Working time", "Waktu kerja"), F(r.DurationMin, "0"), "min"), Kpi(T("RMS cross-track", "Galat lintang RMS"), F(r.RmsCrossTrackM * 100, "0"), "cm", null, "good")),
            Columns(2,
                Panel(T("Planned path vs driven trajectory", "Jalur rencana vs lintasan"), SvgView(r.Svg, 380)),
                Panel(T("Harvest robot", "Robot panen"), Kpis(
                    Kpi(T("Ripe to pick", "Siap petik"), r.Harvest.Order.Count.ToString()), Kpi(T("Travel", "Perjalanan"), F(r.Harvest.TravelDistanceM, "0"), "m"),
                    Kpi(T("Cycle", "Siklus"), F(r.Harvest.EstimatedMinutes), "min")))));
    }

    private static Control Livestock(Func<string, string, string> T)
    {
        var r = LivestockDemo.Run();
        return Stack(
            Kpis(Kpi(T("Peak THI", "THI puncak"), F(r.PeakThi), null, r.PeakLevel.ToString(), r.PeakThi >= 79 ? "alert" : "good"),
                 Kpi(T("Milk loss / cow", "Susu hilang / ekor"), F(r.MilkLossKg), "kg/day"), Kpi(T("Weight @ girth 165 cm", "Bobot @ lingkar 165 cm"), F(r.GirthWeight, "0"), "kg")),
            Columns(2,
                Panel(T("THI over 24 h", "THI selama 24 jam"), SvgView(r.ThiSvg)),
                Panel(T("Reproduction calendar", "Kalender reproduksi"), Table([T("Animal", "Ternak"), T("Event", "Kegiatan"), T("Date", "Tanggal")],
                    r.Calendar.Select(c => new[] { c.TagId, c.Event, c.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) })))));
    }

    private static Control Aqua(Func<string, string, string> T)
    {
        double doMg = 4.2, ph = 8.1;
        Live? live = null;
        live = new Live(() =>
        {
            var r = AquaDemo.Run(29, doMg, ph, 1.1);
            var issues = new StackPanel();
            foreach (var i in r.Water.Issues) issues.Children.Add(Decision(r.Water.Status, i, "ScoutDisease"));
            issues.Children.Add(Text(r.Feed.Note, 13, "soft"));
            return Stack(
                Kpis(Kpi(T("Water status", "Status air"), r.Water.Status, null, null, r.Water.Status == "Good" ? "good" : "alert"),
                     Kpi(T("Toxic NH₃", "NH₃ beracun"), F(r.Water.UnionizedAmmoniaMgL, "0.000"), "mg/L"),
                     Kpi(T("Feed today", "Pakan hari ini"), F(r.Feed.DailyFeedKg), "kg"), Kpi(T("Days to 500 g", "Hari ke 500 g"), r.Growth[^1].Day.ToString())),
                Columns(2, Panel(T("Pond K-03 · tilapia", "Kolam K-03 · nila"), issues), Panel(T("Growth & feed", "Pertumbuhan & pakan"), SvgView(r.GrowthSvg))));
        });
        return Stack(Ui.Controls(
            Slider("DO mg/L", 1, 9, doMg, 0.1, v => { doMg = v; live!.Refresh(); }, "0.0"),
            Slider("pH", 6, 9.5, ph, 0.1, v => { ph = v; live!.Refresh(); }, "0.0")), live);
    }

    private static Control Market(Func<string, string, string> T)
    {
        var r = MarketDemo.Run();
        return Stack(
            Kpis(Kpi(T("Last price", "Harga terakhir"), $"{r.Insight.Last:N0}", "Rp/kg"), Kpi(T("Volatility", "Volatilitas"), F(r.Insight.VolatilityPct), "%"),
                 Kpi(T("Fair farm-gate", "Harga wajar petani"), $"{r.FairPrice:N0}", "Rp/kg"), Kpi(T("Trades", "Transaksi"), r.Trades.Count.ToString(), null, null, "good")),
            Columns(2,
                Panel(T("Chili price outlook", "Proyeksi harga cabai"), Stack(SvgView(r.PriceSvg), Text(r.Insight.Signal, 13, "soft"))),
                Panel(T("Matched trades", "Transaksi cocok"), Table([T("Seller → buyer", "Penjual → pembeli"), "kg", "Rp/kg"],
                    r.Trades.Select(t => new[] { $"{t.Seller} → {t.Buyer}", $"{t.QuantityKg:N0}", $"{t.PricePerKg:N0}" }), 1, 2))));
    }

    private static Control Genomics(Func<string, string, string> T)
    {
        var r = GenomicsDemo.Run();
        return Stack(
            Kpis(Kpi(T("Prediction accuracy", "Akurasi prediksi"), F(r.PredictionAccuracy, "0.00"), null, "rrBLUP", "good"), Kpi(T("Markers", "Penanda"), r.Markers.ToString()),
                 Kpi(T("Gain / year", "Kemajuan / tahun"), F(r.Response.PerYear, "0.00"), "t/ha")),
            Columns(2,
                Panel(T("Top lines by GEBV", "Galur teratas (GEBV)"), SvgView(r.GebvSvg)),
                Panel(T("Recommended crosses", "Persilangan disarankan"), Table([T("Parents", "Tetua"), "GEBV", T("Relationship", "Kekerabatan")],
                    r.Crosses.Select(c => new[] { $"{c.Parent1} × {c.Parent2}", F(c.ExpectedProgenyValue, "0.00"), F(c.Relationship, "0.00") }), 1, 2))));
    }

    private static Control Agent(Func<string, string, string> T, string lang)
    {
        var agent = new FarmAgent(new FarmContext
        {
            FarmName = "Kebun Sukamaju", CropId = "rice", AreaHa = 2, Soil = DemoFarm.Soil, Location = DemoFarm.Center, ElevationM = 18,
            PlantingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-3),
        });
        var log = new StackPanel { Spacing = 10 };
        var scroll = new ScrollViewer { Content = log, Height = 420 };
        var input = new TextBox { PlaceholderText = T("Type a question…", "Ketik pertanyaan…"), MinWidth = 360 };
        var ask = new Button { Content = T("Ask", "Tanya"), Classes = { "primary" } };

        void Bubble(string text, bool user, string? meta = null)
        {
            var sp = new StackPanel { Children = { Text(text, 13.5, fg: user ? Brush("Paper") : Brush("Ink")) } };
            if (meta is not null) sp.Children.Add(Text(meta, 10.5, "mono"));
            log.Children.Add(new Border
            {
                Background = user ? Brush("Ink") : Brush("Paper"), BorderBrush = Brush("Rule"), BorderThickness = new Thickness(user ? 0 : 1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9), MaxWidth = 640,
                HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left, Child = sp,
            });
            scroll.ScrollToEnd();
        }

        async Task AskAsync(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            Bubble(q, true);
            input.Text = "";
            ask.IsEnabled = false;
            try
            {
                var a = await agent.AskAsync(q);
                Bubble(a.Text, false, $"{a.Model} · {string.Join(", ", a.ToolsUsed.Distinct())}");
            }
            catch (Exception ex) { Bubble(ex.Message, false, "error"); }
            ask.IsEnabled = true;
        }

        ask.Click += async (_, _) => await AskAsync(input.Text ?? "");
        string[] suggestions = lang == "id"
            ? ["Berapa dosis pupuk terbaik minggu ini?", "Apakah sawah perlu diairi hari ini?", "Bagaimana risiko penyakit minggu ini?"]
            : ["What fertilizer should I apply this week?", "Do I need to irrigate today?", "What is the disease risk this week?"];
        var chips = new WrapPanel();
        foreach (var s in suggestions)
        {
            var b = new Button { Content = s, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(14) };
            b.Click += async (_, _) => await AskAsync(s);
            chips.Children.Add(b);
        }
        if (Screenshots.Active) Bubble(agent.Ask(suggestions[0]), false, $"{agent.Model.Name}");
        return Columns(2,
            Panel(T("Conversation", "Percakapan"), Stack(scroll, chips, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { input, ask } })),
            Panel(T("Farm profile", "Profil kebun"), Table(["", ""],
            [
                [T("Crop", "Tanaman"), "Rice (paddy) · 3 DAP"], [T("Area", "Luas"), "2 ha"], [T("Soil", "Tanah"), $"pH {DemoFarm.Soil.Ph} · {DemoFarm.Soil.Texture}"],
                [T("Location", "Lokasi"), "Karawang"], ["Model", agent.Model.Name],
            ])));
    }
}
