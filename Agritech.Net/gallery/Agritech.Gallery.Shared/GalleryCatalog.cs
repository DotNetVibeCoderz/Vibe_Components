namespace Agritech.Gallery.Shared;

/// <summary>Bilingual text pair.</summary>
public readonly record struct L(string En, string Id)
{
    public string this[string lang] => lang == "id" ? Id : En;
}

/// <summary>One gallery use case.</summary>
public sealed record DemoInfo(string Slug, string Group, L Title, L Summary, string Ramp, string Module, string Code);

/// <summary>The gallery's table of contents, shared by the Blazor and Avalonia apps.</summary>
public static class GalleryCatalog
{
    public static readonly L Credit = new(Core.AgritechInfo.CreditEn, Core.AgritechInfo.CreditId);

    public static readonly (string Key, L Name)[] Groups =
    [
        ("field", new("Field & canopy", "Lahan & kanopi")),
        ("water", new("Water & climate", "Air & iklim")),
        ("ops", new("Machines & twins", "Mesin & kembaran digital")),
        ("animals", new("Animals & ponds", "Ternak & kolam")),
        ("market", new("Market & genetics", "Pasar & genetika")),
        ("ai", new("Ask the agronomist", "Tanya ahli agronomi")),
    ];

    public static readonly DemoInfo[] Demos =
    [
        new("ndvi", "field", new("Satellite NDVI & management zones", "NDVI satelit & zona pengelolaan"),
            new("Compute vegetation indices on a 10 m Sentinel-2 style scene, split the field into vigour zones and read zonal statistics.",
                "Hitung indeks vegetasi pada citra bergaya Sentinel-2 10 m, bagi lahan menjadi zona vigor, dan baca statistik zonal."),
            "ndvi", "Agritech.RemoteSensing", """
            var image = SyntheticScene.Create(field.Bounds, pixelSizeM: 10);
            var ndvi  = VegetationIndices.Ndvi(image);          // Rust kernel, SIMD fallback
            var zones = RasterAnalysis.ManagementZones(ndvi, 3);
            var stats = RasterAnalysis.Zonal(ndvi, field, "Blok A");
            """),
        new("soil", "field", new("Soil health & fertilizer plan", "Kesehatan tanah & rencana pupuk"),
            new("Score a lab soil test, classify texture, and turn a target yield into a split fertilizer schedule with product amounts and cost.",
                "Nilai hasil uji tanah, klasifikasikan tekstur, dan ubah target hasil menjadi jadwal pemupukan bertahap lengkap dengan dosis dan biaya."),
            "soil", "Agritech.Soil · Agritech.AI", """
            var report = SoilHealth.Assess(sample);
            var advice = FertilizerAdvisor.Advise(crops.Get("rice"), sample, areaHa: 2.5, targetYieldTonHa: 6.5);
            foreach (var app in advice.Schedule) Console.WriteLine(app.Stage);
            """),
        new("crops", "field", new("Crop suitability", "Kesesuaian tanaman"),
            new("Rank crops for a site with fuzzy FAO land-suitability classes and see which factor limits each one.",
                "Urutkan tanaman untuk suatu lokasi dengan kelas kesesuaian lahan FAO dan lihat faktor pembatas masing-masing."),
            "ndvi", "Agritech.Crops", """
            var site = new SiteConditions(MeanTempC: 26.5, SeasonalRainfallMm: 1400, SoilPh: 5.6, ElevationM: 300);
            var top  = new CropRecommender().Recommend(site, top: 6);
            """),
        new("simulation", "water", new("Season simulation & climate scenarios", "Simulasi musim & skenario iklim"),
            new("Run the daily crop model (phenology, LAI, soil water, nitrogen) and compare irrigation and SSP climate scenarios over 20 stochastic years.",
                "Jalankan model tanaman harian (fenologi, LAI, air tanah, nitrogen) dan bandingkan skenario irigasi serta iklim SSP selama 20 tahun stokastik."),
            "water", "Agritech.Simulation", """
            var sim = new CropSimulator(crop, soil, station);
            var result = sim.Run(new Management { PlantingDate = planting, Irrigation = IrrigationStrategy.AutoAtRaw }, weather);
            var summary = new ScenarioRunner(crop, soil, location).Run(scenarios, years: 20);
            """),
        new("irrigation", "water", new("Irrigation scheduling (FAO-56)", "Penjadwalan irigasi (FAO-56)"),
            new("Track root-zone depletion day by day and get an irrigation depth and volume only when the crop is about to stress.",
                "Pantau deplesi zona akar setiap hari dan dapatkan kedalaman serta volume irigasi hanya saat tanaman mulai tercekam."),
            "water", "Agritech.AI · Agritech.Weather", """
            var advisor = new IrrigationAdvisor(crop, soil.Hydraulics(), station);
            var rec = advisor.Recommend(planting, history, forecast, areaHa: 1.2);
            if (rec.IrrigateNow) Pump.Run(rec.VolumeM3);
            """),
        new("drones", "ops", new("Drone mission & weed detection", "Misi drone & deteksi gulma"),
            new("Plan a photogrammetry survey from camera specs and overlap, export it to QGroundControl, and find weeds between crop rows.",
                "Rencanakan survei fotogrametri dari spesifikasi kamera dan overlap, ekspor ke QGroundControl, dan temukan gulma di antara barisan tanaman."),
            "ndvi", "Agritech.Drones", """
            var plan = MissionPlanner.PlanSurvey(field, CameraSpec.DjiMavic3M, altitudeM: 80, frontOverlap: 0.8, sideOverlap: 0.7);
            File.WriteAllText("field.plan", plan.ToQGroundControlPlan(home));
            var weeds = WeedDetector.Detect(photo);
            """),
        new("twin", "ops", new("Farm digital twin", "Kembaran digital kebun"),
            new("Stream simulated sensors into a live twin of four fields and watch it raise explained decisions.",
                "Alirkan data sensor simulasi ke kembaran digital empat petak dan lihat keputusan beserta alasannya."),
            "heat", "Agritech.DigitalTwin · Agritech.IoT", """
            var twin = new FarmTwin(farm, new MqttSensorTransport(new MqttOptions("broker.local")));
            twin.BindDevice("soil-01", farm.Fields[0]);
            await twin.StartAsync();
            foreach (var d in twin.Evaluate()) Notify(d.Title, d.Rationale);
            """),
        new("robotics", "ops", new("Autonomous tractor path", "Jalur traktor otonom"),
            new("Generate a headland-aware coverage path and follow it with a pure-pursuit controller on a tractor model.",
                "Buat jalur cakupan yang memperhitungkan headland lalu ikuti dengan pengendali pure-pursuit pada model traktor."),
            "ndvi", "Agritech.Robotics", """
            var path = PathPlanner.FieldCoverage(field, implementWidthM: 6, headlandM: 8, out var origin);
            var (trajectory, rmsCte) = PurePursuitController.Simulate(path, new TractorModel(), speedMs: 2.5);
            """),
        new("livestock", "animals", new("Herd heat stress & breeding", "Cekaman panas & reproduksi ternak"),
            new("Follow the temperature-humidity index through a day and keep a reproduction calendar for the herd.",
                "Ikuti indeks suhu-kelembapan sepanjang hari dan kelola kalender reproduksi ternak."),
            "heat", "Agritech.Livestock", """
            double thi = LivestockMath.Thi(tempC: 33, rhPct: 70);   // 85 → moderate stress
            var calendar = herd.ReproductionCalendar(today);
            """),
        new("aquaculture", "animals", new("Pond water quality & feeding", "Kualitas air & pakan kolam"),
            new("Check toxic ammonia and oxygen, size today's ration and project growth to harvest weight.",
                "Periksa amonia beracun dan oksigen, hitung ransum hari ini, dan proyeksikan pertumbuhan hingga bobot panen."),
            "water", "Agritech.Fisheries", """
            var report = WaterQuality.Assess(sample, AquaSpecies.Tilapia);
            var feed   = FeedOptimizer.Plan(pond, sample);
            var growth = FeedOptimizer.Project(pond, meanTempC: 29, targetWeightG: 500);
            """),
        new("market", "market", new("Marketplace & price outlook", "Pasar & proyeksi harga"),
            new("Match farmer offers with buyer bids including transport cost, and forecast prices with Holt smoothing.",
                "Cocokkan penawaran petani dengan permintaan pembeli termasuk biaya angkut, dan proyeksikan harga dengan pemulusan Holt."),
            "change", "Agritech.Marketplace · Agritech.SupplyChain", """
            var book = new OrderBook(transportCostPerKgPerKm: 2.5);
            book.Submit(Side.Sell, "KT Sri Rejeki", "chili", 800, 31_000, location: farm);
            book.Submit(Side.Buy, "Pasar Induk", "chili", 500, 34_000, location: market);
            var outlook = PriceIntelligence.Analyze(prices);
            """),
        new("genomics", "market", new("Genomic selection & crosses", "Seleksi genomik & persilangan"),
            new("Predict breeding values from SNP markers (rrBLUP) and rank crosses that balance gain with diversity.",
                "Prediksi nilai pemuliaan dari penanda SNP (rrBLUP) dan urutkan persilangan yang menyeimbangkan kemajuan dengan keragaman."),
            "change", "Agritech.Genomics", """
            var (gebv, effects) = BreedingAdvisor.GenomicPrediction(geno, phenotypes, heritability: 0.5);
            var crosses = BreedingAdvisor.PlanCrosses(lines, gebv, geno.GenomicRelationship(), top: 8);
            """),
        new("agent", "ai", new("Farm agent", "Agen pertanian"),
            new("Ask in English or Bahasa Indonesia. The agent answers with grounded tool calls: fertilizer, irrigation, weather, disease, pests, yield.",
                "Bertanya dalam Bahasa Indonesia atau Inggris. Agen menjawab berdasarkan perhitungan alat: pupuk, irigasi, cuaca, penyakit, hama, hasil."),
            "ndvi", "Agritech.AI", """
            var agent = new FarmAgent(new FarmContext { CropId = "rice", PlantingDate = planting, AreaHa = 2 });
            string answer = agent.Ask("Berapa dosis pupuk terbaik minggu ini?");
            """),
    ];

    public static DemoInfo Get(string slug) => Demos.First(d => d.Slug == slug);
}
