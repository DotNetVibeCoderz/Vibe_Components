using Agritech.AI.Agent;
using Agritech.Core.Farming;
using Agritech.Core.Geo;
using Agritech.DigitalTwin;
using Agritech.Drones;
using Agritech.Fisheries;
using Agritech.Genomics;
using Agritech.Gallery.Shared;
using Agritech.IoT;
using Agritech.Livestock;
using Agritech.Marketplace;
using Agritech.Robotics;
using Agritech.SupplyChain;
using Agritech.Visualization;

namespace Agritech.Tests;

public class DroneTests
{
    [Fact]
    public void Gsd_Mavic3MAt100m()
    {
        // 17.3 mm × 100 m / (12.29 mm × 5280 px) ≈ 2.67 cm/px
        Assert.Equal(2.67, CameraSpec.DjiMavic3M.Gsd(100), 2);
    }

    [Fact]
    public void Survey_CoversFieldAndExportsPlan()
    {
        var plan = MissionPlanner.PlanSurvey(DemoFarm.Field, CameraSpec.DjiMavic3M, 80);
        Assert.True(plan.Waypoints.Count >= 6);
        Assert.All(plan.Waypoints, w => Assert.True(GeoMath.Distance(w.Position, DemoFarm.Field.Centroid) < 400));
        Assert.True(plan.EstimatedPhotos > 10);
        var json = plan.ToQGroundControlPlan(DemoFarm.Center);
        Assert.Contains("\"fileType\": \"Plan\"", json);
    }

    [Fact]
    public void WeedDetector_FindsRowsAndWeeds()
    {
        var img = RgbImage.SyntheticRowCrop(320, 240, 5, 25, 1);
        var r = WeedDetector.Detect(img, 5);
        Assert.Equal(5, r.CropRowCentersX.Count);
        Assert.True(r.Weeds.Count >= 10, $"weeds={r.Weeds.Count}");
        Assert.InRange(r.WeedCoverPct, 0.3, 15);
    }

    [Fact]
    public void LesionAnalyzer_HealthyLeaf()
    {
        var leaf = new RgbImage(10, 10);
        for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) leaf[x, y] = (40, 150, 50);
        Assert.Equal("healthy", new LesionSeverityAnalyzer().Classify(leaf).Label);
    }

    [Fact]
    public void Orthomosaic_StitchesOverlappingImages()
    {
        var a = RgbImage.SyntheticRowCrop(100, 80, 3, 5, 1);
        var c = DemoFarm.Center;
        var imgs = new[] { new GeotaggedImage(a, c, 0, 0.05), new GeotaggedImage(a, GeoMath.Destination(c, 90, 3), 0, 0.05) };
        var m = OrthomosaicBuilder.Build(imgs);
        Assert.Equal(2, m.ImagesUsed);
        Assert.True(m.Mosaic.Width > 100);
    }
}

public class IoTTests
{
    [Fact]
    public void CayenneLpp_RoundTrip()
    {
        var bytes = new CayenneLpp.Writer().Temperature(1, 27.3).Humidity(2, 81.5).Analog(3, 34.12).Gps(4, -6.3045, 107.3012, 18.5).ToArray();
        var v = CayenneLpp.Decode(bytes);
        Assert.Equal(27.3, v[0].Value, 6);
        Assert.Equal(81.5, v[1].Value, 6);
        Assert.Equal(34.12, v[2].Value, 6);
        Assert.Equal(-6.3045, v[3].Values[0], 4);
        Assert.Equal(107.3012, v[3].Values[1], 4);
    }

    [Fact]
    public void CayenneLpp_KnownVector()
    {
        // 03 67 01 10 05 67 00 FF → ch3 temp 27.2 °C, ch5 temp 25.5 °C (spec example)
        var v = CayenneLpp.Decode(Convert.FromHexString("03670110056700FF"));
        Assert.Equal(27.2, v[0].Value, 6);
        Assert.Equal(25.5, v[1].Value, 6);
    }

    [Fact]
    public void TtnWebhook_ParsesUplink()
    {
        var payload = Convert.ToBase64String(new CayenneLpp.Writer().Analog(3, 21.5).ToArray());
        var json = $$$"""{"end_device_ids":{"device_id":"soil-07","dev_eui":"70B3D57ED0000001"},"received_at":"2026-09-14T03:00:00Z","uplink_message":{"f_port":2,"f_cnt":42,"frm_payload":"{{{payload}}}","rx_metadata":[{"rssi":-97,"snr":7.5}]}}""";
        var up = TtnWebhook.ParseUplink(json);
        Assert.Equal("soil-07", up.DeviceId);
        Assert.Equal(-97, up.Rssi);
        var r = Assert.Single(TtnWebhook.ToReadings(up, new Dictionary<int, SensorKind> { [3] = SensorKind.SoilMoisture }));
        Assert.Equal(SensorKind.SoilMoisture, r.Kind);
        Assert.Equal(21.5, r.Value, 6);
    }

    [Fact]
    public void Mqtt_TopicParsing()
    {
        Assert.True(MqttSensorTransport.TryParse("agritech/farm1/soil-01/soilmoisture", """{"v":28.4,"u":"%VWC","ts":"2026-09-14T03:00:00Z"}""", out var r));
        Assert.Equal(SensorKind.SoilMoisture, r!.Kind);
        Assert.True(MqttSensorTransport.TryParse("agritech/farm1/ws-1/airtemperature/2", "31.5", out var t));
        Assert.Equal(2, t!.Channel);
        Assert.False(MqttSensorTransport.TryParse("bad/topic", "1", out _));
        var rt = new SensorReading("d", SensorKind.AirTemperature, 1.5, "°C", DateTimeOffset.UnixEpoch);
        Assert.True(MqttSensorTransport.TryParse(MqttSensorTransport.Topic(new MqttOptions(), rt), rt.ToJson(), out var back));
        Assert.Equal(1.5, back!.Value);
    }

    [Fact]
    public void Edge_RaisesThresholdAndAnomalyAlerts()
    {
        var edge = new EdgeProcessor(deadband: 0.5).AddDefaultAgronomicRules();
        var alerts = new List<SensorAlert>();
        edge.Alert += alerts.Add;
        var t = DateTimeOffset.UnixEpoch;
        for (int i = 0; i < 30; i++) edge.Process(new SensorReading("s", SensorKind.SoilMoisture, 30 + (i % 3) * 0.1, "%", t.AddMinutes(i)));
        edge.Process(new SensorReading("s", SensorKind.SoilMoisture, 12, "%", t.AddMinutes(31)));
        Assert.Contains(alerts, a => a.Message.Contains("drying"));
        Assert.Contains(alerts, a => a.Message.Contains("Anomalous"));
        Assert.True(edge.Forwarded < edge.Received); // deadband suppressed duplicates
    }
}

public class TwinTests
{
    [Fact]
    public void Twin_ProducesIrrigationDecisionForDrySoil()
    {
        var farm = new Farm("Test", DemoFarm.Center);
        var f = farm.AddField("A", DemoFarm.Field, "maize");
        f.PlantingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-30);
        var twin = new FarmTwin(farm).BindDevice("soil-1", f);
        twin.Ingest(new SensorReading("soil-1", SensorKind.SoilMoisture, 14, "%VWC", DateTimeOffset.UtcNow));
        var decisions = twin.Evaluate();
        Assert.Contains(decisions, d => d.Type == DecisionType.Irrigate);
        Assert.Empty(twin.Evaluate()); // deduplicated
        Assert.Equal(14, twin.Snapshot().Fields[0].SoilMoisture);
    }

    [Fact]
    public void TwinSession_Ticks()
    {
        var s = new TwinSession();
        var snap = s.Tick();
        Assert.Equal(4, snap.Fields.Count);
        Assert.True(snap.ReadingsProcessed > 0);
        Assert.Contains("<svg", s.IsometricSvg());
    }
}

public class AnimalTests
{
    [Fact]
    public void Thi_KnownValue()
    {
        // 91.4 − (0.55 − 0.385)(59.4 − 26) = 85.89
        Assert.Equal(85.89, LivestockMath.Thi(33, 70), 2);
        Assert.Equal(HeatStressLevel.Moderate, LivestockMath.HeatStress(85.89));
        Assert.Equal(349.7, LivestockMath.WeightFromHeartGirth(165), 1);
    }

    [Fact]
    public void Herd_CalendarAndVitals()
    {
        var r = LivestockDemo.Run();
        Assert.Contains(r.Calendar, c => c.Event.StartsWith("Expected parturition"));
        Assert.Contains(r.Alerts, a => a.Message.StartsWith("Fever"));
    }

    [Fact]
    public void UnionizedAmmonia_IncreasesWithPh()
    {
        double f7 = WaterQuality.UnionizedAmmoniaFraction(28, 7), f9 = WaterQuality.UnionizedAmmoniaFraction(28, 9);
        Assert.InRange(f7, 0.005, 0.01);
        Assert.InRange(f9, 0.3, 0.5);
        Assert.InRange(WaterQuality.DoSaturation(25), 8.1, 8.4);
    }

    [Fact]
    public void FeedOptimizer_SuspendsFeedingAtLowDo()
    {
        var pond = new Pond("p", AquaSpecies.Tilapia, 500, 1) { StockedCount = 5000, AvgWeightG = 100 };
        var plan = FeedOptimizer.Plan(pond, new WaterSample(DateTimeOffset.Now, 29, 1.5, 7.5, 0.3));
        Assert.Equal(0, plan.DailyFeedKg);
        double w = FeedOptimizer.ProjectWeight(50, 1.8, 29, FeedOptimizer.DaysToTarget(50, 400, 1.8, 29));
        Assert.InRange(w, 400, 420);
    }
}

public class MarketAndChainTests
{
    [Fact]
    public void OrderBook_MatchesWithPriceTimePriority()
    {
        var book = new OrderBook();
        book.Submit(Side.Sell, "A", "rice", 100, 10_000);
        book.Submit(Side.Sell, "B", "rice", 100, 9_500);
        book.Submit(Side.Buy, "X", "rice", 150, 10_000);
        Assert.Equal(2, book.Trades.Count);
        Assert.Equal("B", book.Trades[0].Seller);
        Assert.Equal(9_500, book.Trades[0].PricePerKg);
        Assert.Equal(50, book.Asks("rice").Single().RemainingKg);
    }

    [Fact]
    public void OrderBook_TransportCostBlocksDistantMatch()
    {
        var book = new OrderBook(transportCostPerKgPerKm: 10);
        book.Submit(Side.Sell, "far", "chili", 100, 30_000, location: new GeoCoordinate(-7.8, 110.4));
        book.Submit(Side.Buy, "jkt", "chili", 100, 31_000, location: new GeoCoordinate(-6.2, 106.8));
        Assert.Empty(book.Trades);
    }

    [Fact]
    public void Market_Demo()
    {
        var r = MarketDemo.Run();
        Assert.NotEmpty(r.Trades);
        Assert.Equal(21, r.Insight.Forecast.Count);
    }

    [Fact]
    public void Traceability_ChainVerifiesAndRecallsDescendants()
    {
        var ledger = new TraceabilityLedger();
        var lot = ledger.Harvest("chili", 1000, "KT Sri Rejeki", new DateOnly(2026, 9, 1), "farmer");
        var parts = ledger.Split(lot.Id, "packer", (600, "A"), (400, "B"));
        var merged = ledger.Merge([parts[0].Id], "collector", "Garut hub");
        Assert.Empty(ledger.Verify());
        Assert.Contains(ledger.TraceBack(merged.Id), l => l.Id == lot.Id);
        var recalled = ledger.Recall(lot.Id, "Pesticide residue", "inspector");
        Assert.Equal(4, recalled.Count);
    }

    [Fact]
    public void Gs1_CheckDigits()
    {
        Assert.True(Gs1.IsValid("8991002101234".Length == 13 ? "8992761111113" : ""));
        Assert.Equal(18, Gs1.Sscc('0', "8991234", 42).Length);
        Assert.True(Gs1.IsValid(Gs1.Sscc('0', "8991234", 42)));
    }

    [Fact]
    public void ShelfLife_WarmExcursionConsumesShelfLife()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var cold = Enumerable.Range(0, 49).Select(h => new TemperatureLog(t0.AddHours(h), 12)).ToList();
        var warm = Enumerable.Range(0, 49).Select(h => new TemperatureLog(t0.AddHours(h), h is > 10 and < 30 ? 28 : 12)).ToList();
        var a = ShelfLife.Evaluate(PerishableProfile.Tomato, cold);
        var b = ShelfLife.Evaluate(PerishableProfile.Tomato, warm);
        Assert.Equal(2, a.ConsumedDays, 2);
        Assert.True(b.ConsumedDays > a.ConsumedDays);
        Assert.Single(b.Excursions);
    }

    [Fact]
    public void Inventory_PicksFefo()
    {
        var inv = new Inventory();
        inv.Receive(new StockItem("L2", "rice", 100, new(2026, 12, 1), "A"));
        inv.Receive(new StockItem("L1", "rice", 100, new(2026, 10, 1), "A"));
        var pick = inv.Pick("rice", 150, new(2026, 9, 1));
        Assert.Equal(("L1", 100.0), pick[0]);
        Assert.Equal(50, inv.OnHand("rice"));
    }
}

public class GenomicsAndRoboticsTests
{
    [Fact]
    public void Dna_BasicOperations()
    {
        var s = new NucleotideSequence("ATGGCCTAA");
        Assert.Equal("TTAGGCCAT", s.ReverseComplement().Residues);
        Assert.Equal("MA*", s.Translate());
        Assert.Equal("MA", s.Translate(toStop: true));
        Assert.Equal(44.44, s.GcContent, 2);
        Assert.Equal("AUGGCCUAA", s.Transcribe().Residues);
        Assert.Throws<FormatException>(() => new NucleotideSequence("ATGX"));
    }

    [Fact]
    public void Fasta_RoundTrip()
    {
        var seqs = Fasta.Parse(">s1 rice gene\nATGC\nGGTA\n>s2\nTTTT\n");
        Assert.Equal(2, seqs.Count);
        Assert.Equal("ATGCGGTA", seqs[0].Residues);
        Assert.Equal("rice gene", seqs[0].Description);
        Assert.Equal(seqs[1].Residues, Fasta.Parse(Fasta.Write(seqs))[1].Residues);
    }

    [Fact]
    public void Alignment_GlobalAndLocal()
    {
        var g = Alignment.Global("GATTACA", "GCATGCU");
        Assert.Equal(g.AlignedA.Length, g.AlignedB.Length);
        var l = Alignment.Local("TTTTACGTACGTTTTT", "GGACGTACGGG");
        Assert.Contains("ACGTACG", l.AlignedA);
    }

    [Fact]
    public void Breeding_ResponseAndInbreeding()
    {
        Assert.InRange(BreedingAdvisor.SelectionIntensity(0.1), 1.750, 1.760); // tabulated i(10%) = 1.755
        var ped = new List<PedigreeEntry> { new("A", null, null), new("B", null, null), new("C", "A", "B"), new("D", "A", "B"), new("E", "C", "D") };
        Assert.Equal(0.25, BreedingAdvisor.InbreedingCoefficients(ped)["E"], 6); // full-sib mating
    }

    [Fact]
    public void GenomicPrediction_IsAccurate()
    {
        var r = GenomicsDemo.Run();
        Assert.True(r.PredictionAccuracy > 0.3, $"accuracy {r.PredictionAccuracy}"); // 100 training lines, h²=0.5
        Assert.NotEmpty(r.Crosses);
        Assert.NotEmpty(r.Orfs);
    }

    [Fact]
    public void AStar_FindsPathAroundObstacle()
    {
        var g = new OccupancyGrid(50, 50, 1);
        g.AddRectangleObstacle(new(20, 0), new(22, 40));
        var path = PathPlanner.AStar(g.Inflate(0.5), new(5, 5), new(45, 5));
        Assert.NotNull(path);
        Assert.All(path!, p => Assert.True(PathPlanner.LineOfSight(g, p, p)));
        Assert.Contains(path!, p => p.Y > 40);
    }

    [Fact]
    public void PurePursuit_TracksCoveragePathAccurately()
    {
        var r = RoboticsDemo.Run();
        Assert.True(r.RmsCrossTrackM < 2.5, $"rms={r.RmsCrossTrackM}");
        Assert.True(r.Passes > 5);
        Assert.NotEmpty(r.Harvest.Order);
    }

    [Fact]
    public void GreenhouseRobot_CompletesMission()
    {
        var benches = Enumerable.Range(0, 12).Select(i => new Point2(i * 4, (i % 2) * 6)).ToList();
        var robot = new GreenhouseRobot(benches, new(0, 0));
        int sprayed = robot.RunMission();
        Assert.Equal(GreenhouseRobotState.Idle, robot.State);
        Assert.InRange(sprayed, 1, 12);
    }
}

public class VisualizationAndAgentTests
{
    [Fact]
    public void Png_HasValidSignatureAndChunks()
    {
        var png = PngEncoder.FromRaster(new float[] { 0, 0.5f, float.NaN, 0.9f }, 2, 2, ColorMap.Ndvi, 2);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.Contains("IEND", System.Text.Encoding.ASCII.GetString(png));
    }

    [Fact]
    public void Svg_ChartsRender()
    {
        Assert.StartsWith("<svg", SvgCharts.Line([new ChartSeries("a", [1, 2, 3])]));
        Assert.Contains("rect", SvgCharts.Bars([("x", 1.0)]));
        Assert.Contains("polygon", IsometricFarm.Render([new FarmTile(0, 0, "A", 0.7, 0.5)]));
        Assert.DoesNotContain("var(", SvgTheme.Resolve(SvgCharts.Gauge(0.5, "x", "50%")));
    }

    [Fact]
    public async Task Agent_OfflineAnswersFertilizerQuestionInIndonesian()
    {
        var agent = new FarmAgent(new FarmContext { CropId = "rice", PlantingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-3), AreaHa = 2, Soil = DemoFarm.Soil },
            new RuleBasedChatModel());
        var answer = await agent.AskAsync("Berapa dosis pupuk terbaik minggu ini?");
        Assert.Equal("id", answer.Language);
        Assert.Contains("fertilizer_plan", answer.ToolsUsed);
        Assert.Contains("JADWAL MINGGU INI", answer.Text);
        Assert.Contains("kg/ha", answer.Text);
    }

    [Fact]
    public async Task Agent_OfflineRoutesEnglishIrrigationQuestion()
    {
        var agent = new FarmAgent(new FarmContext { CropId = "maize", PlantingDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-40) }, new RuleBasedChatModel());
        var a = await agent.AskAsync("Should I irrigate my field today?");
        Assert.Equal("en", a.Language);
        Assert.Contains("irrigation_advice", a.ToolsUsed);
    }

    [Fact]
    public void LanguageDetection()
    {
        Assert.Equal("id", FarmAgent.DetectLanguage("Kapan waktu tanam yang tepat?"));
        Assert.Equal("en", FarmAgent.DetectLanguage("When should I plant maize?"));
    }

    [Fact]
    public void AllGalleryScenariosRun()
    {
        Assert.True(NdviDemo.Run().Stats.PixelCount > 100);
        Assert.NotNull(SoilDemo.Run().Advice);
        Assert.NotEmpty(CropDemo.Run());
        Assert.NotNull(IrrigationDemo.Run().Recommendation);
        Assert.True(DroneDemo.Run().Plan.Waypoints.Count > 0);
        Assert.NotEmpty(AquaDemo.Run().Growth);
        Assert.Equal(13, GalleryCatalog.Demos.Length);
    }
}
