using AquaCulture.Core;

namespace AquaCulture.Gallery.Shared;

/// <summary>Bilingual text pair for the gallery.</summary>
public readonly record struct L(string En, string Id)
{
    public string this[string lang] => lang == "id" ? Id : En;
    public static implicit operator L(Text2 t) => new(t.En, t.Id);
}

/// <summary>One gallery use case.</summary>
public sealed record DemoInfo(string Slug, string Group, L Title, L Summary, string Module, string Code, string Accent = "teal");

/// <summary>The gallery's table of contents, shared by the Blazor and Avalonia apps.</summary>
public static class GalleryCatalog
{
    public static readonly L Credit = new(AquaInfo.CreditEn, AquaInfo.CreditId);
    public static readonly L Tagline = new(AquaInfo.Tagline, AquaInfo.TaglineId);

    public static readonly (string Key, L Name)[] Groups =
    [
        ("water", new("Water & oxygen", "Air & oksigen")),
        ("grow", new("Growth & feed", "Pertumbuhan & pakan")),
        ("health", new("Fish health", "Kesehatan ikan")),
        ("farm", new("Farm operations", "Operasional tambak")),
        ("science", new("Fisheries science", "Sains perikanan")),
        ("business", new("Business & compliance", "Bisnis & kepatuhan")),
        ("agent", new("Ask the AquaAgent", "Tanya AquaAgent")),
    ];

    public static readonly DemoInfo[] Demos =
    [
        new("water", "water", new("Water quality, alerts & actions", "Kualitas air, peringatan & tindakan"),
            new("Score 15 parameters against the species' tolerance, compute toxic NH₃ and DO saturation, and turn the result into dosed actions.",
                "Nilai 15 parameter terhadap toleransi spesies, hitung NH₃ beracun dan saturasi DO, lalu ubah hasilnya menjadi tindakan lengkap dengan dosis."),
            "AquaCulture.WaterQuality", """
            var a = WaterQualityIndex.Assess(sample, SpeciesCatalog.Vaname);      // WQI, NH₃, DO %sat, issues
            var actions = RecommendationEngine.Recommend(a, PondContext.From(pond, biomassKg));
            var alerts = AlertEngine.ForSpecies(SpeciesCatalog.Vaname, bus).Evaluate(sample);
            """),
        new("oxygen", "water", new("Night oxygen forecast & aeration", "Prakiraan oksigen malam & aerasi"),
            new("Hourly pond oxygen budget (plankton, stock, sediment, wind, paddle wheels) predicts the dawn minimum and the energy aeration needs.",
                "Neraca oksigen kolam per jam (plankton, biota, sedimen, angin, kincir) memprediksi DO minimum subuh dan energi yang dibutuhkan aerasi."),
            "AquaCulture.Hydrodynamic · AquaCulture.Forecasting", """
            var off = PondOxygenBudget.Simulate(pond, weather, initialDo: 6.5);
            var on  = PondOxygenBudget.Simulate(pond, weather, 6.5, PondOxygenBudget.SetPoint(4.5));
            var f   = DoCrashForecaster.Forecast(recentDo, species, pond, weather);   // P(DO < critical), start time
            """),
        new("hydro", "water", new("Pond circulation & oxygen field", "Sirkulasi kolam & sebaran oksigen"),
            new("Paddle-wheel circulation drives a 2-D advection–diffusion model of dissolved oxygen, solved by the Rust kernel.",
                "Sirkulasi kincir menggerakkan model adveksi–difusi 2-D oksigen terlarut yang diselesaikan oleh kernel Rust."),
            "AquaCulture.Hydrodynamic · AquaCulture.Native", """
            var grid = new Grid2D(80, 64, cellSizeM: 0.7).WithDikes();
            FlowFields.PaddleWheelCirculation(grid, 0.18, wheels);
            var model = new ScalarTransport(grid) { Diffusivity = 0.03, Decay = kLa, Equilibrium = saturation, Background = -respiration };
            var result = model.Run(initial, seconds: 3 * 3600);                  // Native kernel, managed fallback
            """, "blue"),
        new("growth", "grow", new("AquaGrowth: growth & harvest forecast", "AquaGrowth: pertumbuhan & prakiraan panen"),
            new("Daily TGC growth modulated by temperature, oxygen, ammonia and crowding; Monte-Carlo harvest dates and the most profitable harvest day.",
                "Pertumbuhan TGC harian yang dipengaruhi suhu, oksigen, amonia dan kepadatan; tanggal panen Monte-Carlo dan hari panen paling menguntungkan."),
            "AquaCulture.Growth · AquaCulture.Forecasting", """
            var traj = new GrowthSimulator(species).Simulate(stock, 160, env);
            var best = HarvestPredictor.Optimal(traj, areaM2, new HarvestEconomics(feedPrice, dailyCost));
            var mc   = HarvestForecaster.Forecast(species, today, abw, population, target, temps, 500);
            """, "green"),
        new("feeding", "grow", new("Feeding plan, FCR & least-cost feed", "Rencana pakan, FCR & pakan termurah"),
            new("Species feeding tables adjusted for water quality, meal schedules, cost per kilogram of gain and Pearson-square blending.",
                "Tabel pakan spesies yang disesuaikan kualitas air, jadwal pemberian, biaya per kg pertambahan bobot dan pencampuran metode Pearson."),
            "AquaCulture.Feeding", """
            var plan = FeedingCalculator.Plan(species, abwG, population, tempC, dawnDo, nh3);
            var rank = FeedOptimizer.Rank(species, abwG, FeedCatalog.Products);
            var cmd  = new AutoFeedingAdvisor(species).Next(plan.Meals[0], new FeedingObservation(LeftoverPct: 18));
            """, "green"),
        new("ai", "grow", new("AI models & RL feed policy", "Model AI & kebijakan pakan RL"),
            new("Gradient-boosted growth model, logistic mortality risk, an ML.NET backend and a Q-learning feeding policy trained on the AquaGrowth engine.",
                "Model pertumbuhan gradient boosting, risiko kematian logistik, backend ML.NET, dan kebijakan pakan Q-learning yang dilatih dengan mesin AquaGrowth."),
            "AquaCulture.AI", """
            var growth = new GrowthPredictor(species);                 // GBT, R² on hold-out
            var risk   = new MortalityPredictor(species).Predict(state);
            var policy = new FeedOptimizationPolicy(species).Decide(dawnDo: 2.8, tempC: 30, nh3: 0.08);
            """, "violet"),
        new("disease", "health", new("Disease diagnosis & risk", "Diagnosis & risiko penyakit"),
            new("Pick the clinical signs you see. Bayesian scoring over 15 shrimp and fish diseases, weighted by today's pond conditions.",
                "Pilih gejala klinis yang terlihat. Penilaian Bayesian atas 15 penyakit udang dan ikan, diboboti kondisi kolam hari ini."),
            "AquaCulture.Disease", """
            var dx   = DiagnosisEngine.Diagnose(SpeciesCatalog.Vaname, ["white_spots_shell", "red_body"], conditions);
            var risk = DiagnosisEngine.RiskProfile(SpeciesCatalog.Vaname, conditions);
            """, "coral"),
        new("vision", "health", new("Computer vision: count, size & screen", "Visi komputer: hitung, ukur & skrining"),
            new("Count animals in a tray image, estimate length and weight from the species length–weight curve and flag white spots, red lesions and dark gills. YOLO/ONNX models plug in the same way.",
                "Hitung biota pada citra nampan, perkirakan panjang dan bobot dari kurva panjang–bobot spesies, dan tandai bintik putih, luka merah, serta insang hitam. Model YOLO/ONNX dapat dipasang dengan cara yang sama."),
            "AquaCulture.ComputerVision", """
            var image  = OpenCvInterop.Load("tray.jpg");                  // or a camera frame
            var result = AnimalAnalyzer.Analyze(image, SpeciesCatalog.Vaname, cmPerPixel: 0.25);
            using var yolo = new OnnxDetector("shrimp-disease.onnx", AquaLabels.ShrimpDisease);
            """, "coral"),
        new("twin", "farm", new("3D digital twin of the farm", "Kembaran digital 3D tambak"),
            new("Six lined ponds stream simulated IoT data into a live farm twin that forecasts dawn DO, proposes explained decisions and switches aerators.",
                "Enam kolam HDPE mengalirkan data IoT simulasi ke kembaran digital yang memprakirakan DO subuh, mengusulkan keputusan beralasan, dan menyalakan aerator."),
            "AquaCulture.DigitalTwin · AquaCulture.IoT · AquaCulture.Blazor", """
            await using var twin = new FarmTwin(farm, new MqttSensorTransport(new("broker.local"))) { AutoActuate = true };
            await twin.StartAsync();
            var decisions = await twin.EvaluateAsync(DateTime.Now);       // aerator_on, feed, exchange…
            """, "blue"),
        new("iot", "farm", new("IoT: MQTT, LoRaWAN, Modbus & edge", "IoT: MQTT, LoRaWAN, Modbus & analitik edge"),
            new("Decode a Modbus RTU probe, a LoRaWAN uplink and ESP32 MQTT telemetry, then clean the stream with edge analytics.",
                "Dekode probe Modbus RTU, uplink LoRaWAN dan telemetri MQTT ESP32, lalu bersihkan aliran data dengan analitik edge."),
            "AquaCulture.IoT", """
            var regs = Modbus.ParseReadResponseRtu(frame, unit: 7);
            var values = Modbus.Decode(regs, 0, Modbus.TypicalProbeMap);
            var lora = LoRaPayload.ParseTtnUplink(webhookJson, pondId: "K-01");
            var verdict = edge.Process(reading);                          // Hampel outlier, stuck sensor
            """, "blue"),
        new("gis", "farm", new("Farm GIS, sensors & drainage", "GIS tambak, sensor & drainase"),
            new("Pond layout and GeoJSON, IDW interpolation of oxygen between buoys, D8 drainage on a coastal DEM and water-source ranking.",
                "Tata letak kolam dan GeoJSON, interpolasi IDW oksigen antar pelampung, drainase D8 pada DEM pesisir dan pemeringkatan sumber air."),
            "AquaCulture.GIS", """
            var ponds = FarmMapping.PondBlock(origin, rows: 2, cols: 3, 40, 50);
            var doMap = FarmMapping.Idw(buoys, bounds, 96, 72);
            var drain = DrainageAnalysis.D8(dem);
            var green = NtsInterop.BufferMeters(farmBoundary, 50);       // NetTopologySuite + ProjNet
            """, "green"),
        new("weather", "farm", new("Weather impact & climate risk", "Dampak cuaca & risiko iklim"),
            new("Translate a 72-hour forecast into pond effects (temperature, dilution, pH, dull days, stratification, storms) and rank yearly climate hazards.",
                "Terjemahkan prakiraan 72 jam menjadi dampak ke kolam (suhu, pengenceran, pH, mendung, stratifikasi, badai) dan urutkan bahaya iklim tahunan."),
            "AquaCulture.Environment", """
            var forecast = await new OpenMeteoClient().GetHourlyForecastAsync(lat, lon, 3);
            var impact   = WeatherImpactPredictor.Predict(forecast, new PondExposure(depthM: 1.3, salinityPpt: 18));
            var risk     = ClimateRiskAnalysis.Analyze(DailyWeather.FromHourly(history), CultureSystem.FloatingNetCage);
            """, "amber"),
        new("stock", "science", new("Stock assessment: MSY & Kobe", "Kajian stok: MSY & Kobe"),
            new("Fit a dynamic Schaefer model to catch and effort, read MSY and the Kobe status, and find Fmax and F0.1 from Beverton–Holt yield per recruit.",
                "Paskan model Schaefer dinamis ke data tangkapan dan upaya, baca MSY dan status Kobe, lalu temukan Fmax dan F0.1 dari yield per recruit Beverton–Holt."),
            "AquaCulture.StockAssessment", """
            var fit = SurplusProduction.FitDynamic(catchEffort);           // r, K, q, MSY, B/Bmsy, F/Fmsy
            var ypr = YieldPerRecruit.Analyze(new YprParameters(wInf, k, t0, m, tr, tc));
            var z   = LengthBased.CatchCurve(lengthFrequency, lInf, k).Z;
            """, "violet"),
        new("population", "science", new("Population dynamics", "Dinamika populasi"),
            new("Age-structured stock with recruitment, spawning season, migration, predation and harvest rules; Leslie growth rate and predator–prey cycles.",
                "Stok berstruktur umur dengan rekrutmen, musim pemijahan, migrasi, predasi dan aturan panen; laju pertumbuhan Leslie dan siklus mangsa–pemangsa."),
            "AquaCulture.Population", """
            var model = new AgeStructuredModel(parameters);
            var runs  = model.Run(model.Equilibrium(r0), 40, new HarvestPolicy.HockeyStick(0.5, bTrigger, bLim));
            var (lambda, _) = new LeslieMatrix(fecundity, survival).Dominant();
            """, "violet"),
        new("analytics", "business", new("KPIs & cycle economics", "KPI & ekonomi siklus"),
            new("FCR, SR, ABW, ADG, SGR, productivity, cost structure, break-even price and benchmarks for a block of ponds.",
                "FCR, SR, ABW, ADG, SGR, produktivitas, struktur biaya, harga impas dan pembanding untuk satu blok kolam."),
            "AquaCulture.Analytics", """
            var kpi = KpiCalculator.Calculate(cycle, pond, today, salePricePerKg: 72_000);
            Console.WriteLine($"FCR {kpi.Fcr} · SR {kpi.Sr}% · profit {kpi.Profit:N0} · BEP {kpi.BreakEvenPrice:N0}/kg");
            """, "amber"),
        new("reporting", "business", new("Reports & GAP / CBIB / ASC / BAP", "Laporan & GAP / CBIB / ASC / BAP"),
            new("Self-assess certification readiness and export the same report to Excel, PDF, Word, CSV, JSON, HTML or Markdown with no external libraries.",
                "Nilai kesiapan sertifikasi secara mandiri dan ekspor laporan yang sama ke Excel, PDF, Word, CSV, JSON, HTML atau Markdown tanpa pustaka eksternal."),
            "AquaCulture.Reporting", """
            var audit = ComplianceAudit.Evaluate(ComplianceStandard.Cbib, answers);
            var doc   = Reports.Cycle(kpi, "K-01", lang: "id");
            File.WriteAllBytes("siklus.xlsx", ReportWriters.Xlsx(doc));
            File.WriteAllBytes("siklus.pdf",  ReportWriters.Pdf(doc));
            """, "amber"),
        new("agent", "agent", new("AquaAgent: ask in Indonesian or English", "AquaAgent: bertanya dalam Bahasa Indonesia atau Inggris"),
            new("An LLM agent grounded in AquaCulture.Net tools: feeding, water quality, DO tonight, growth, harvest, disease and economics. Works offline without a key.",
                "Agen LLM yang berpijak pada alat AquaCulture.Net: pakan, kualitas air, DO malam ini, pertumbuhan, panen, penyakit dan ekonomi. Berjalan offline tanpa kunci API."),
            "AquaCulture.AI.Agent", """
            var agent = new AquaAgent(context, LlmOptions.FromEnvironment()?.CreateModel());   // Azure OpenAI, Claude, DeepSeek, HF, Ollama
            var answer = await agent.AskAsync("Berapa pakan udang hari ini dan apakah DO aman malam ini?");
            """, "teal"),
    ];

    public static DemoInfo Get(string slug) => Demos.First(d => d.Slug == slug);
}
