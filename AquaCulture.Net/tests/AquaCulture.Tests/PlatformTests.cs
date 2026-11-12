using System.IO.Compression;
using System.Text;
using AquaCulture.AI;
using AquaCulture.AI.Agent;
using AquaCulture.AI.Ml;
using AquaCulture.ComputerVision;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Species;
using AquaCulture.DigitalTwin;
using AquaCulture.GIS;
using AquaCulture.IoT;
using AquaCulture.Reporting;
using AquaCulture.Visualization;

namespace AquaCulture.Tests;

public class IoTTests
{
    [Fact]
    public void Modbus_crc_known_vector()
    {
        // read 2 registers from address 0 on unit 1: 01 03 00 00 00 02 C4 0B
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x02, 0xC4, 0x0B }, Modbus.ReadHoldingRegistersRtu(1, 0, 2));
    }

    [Fact]
    public void Modbus_probe_round_trip()
    {
        ushort[] regs = [512, 291, 785, unchecked((ushort)(short)-120), 205, 343];
        var frame = Modbus.BuildReadResponseRtu(7, regs);
        var parsed = Modbus.ParseReadResponseRtu(frame, 7);
        var values = Modbus.Decode(parsed, 0, Modbus.TypicalProbeMap).ToDictionary(x => x.Kind, x => x.Value);
        Assert.Equal(5.12, values[SensorKind.DissolvedOxygen], 6);
        Assert.Equal(29.1, values[SensorKind.Temperature], 6);
        Assert.Equal(7.85, values[SensorKind.Ph], 6);
        Assert.Equal(-120, values[SensorKind.Orp]);
        frame[4] ^= 0xFF;
        Assert.Throws<FormatException>(() => Modbus.ParseReadResponseRtu(frame, 7));
    }

    [Fact]
    public void Cayenne_lpp_decoding()
    {
        // ch3 temperature 27.2 °C, ch5 humidity 64 %
        var r = LoRaPayload.DecodeCayenne([0x03, 0x67, 0x01, 0x10, 0x05, 0x68, 0x80]);
        Assert.Equal(27.2, r[0].Values[0], 6);
        Assert.Equal(64, r[1].Values[0], 6);
        var payload = LoRaPayload.CayenneTemperature(1, 29.4).Concat(LoRaPayload.CayenneAnalog(2, 5.25)).ToArray();
        var readings = LoRaPayload.CayenneToReadings(LoRaPayload.DecodeCayenne(payload), "node", "P1", DateTime.UtcNow);
        Assert.Contains(readings, x => x.Kind == SensorKind.DissolvedOxygen && Math.Abs(x.Value - 5.25) < 1e-9);
    }

    [Fact]
    public void Compact_pond_payload_round_trip()
    {
        var bytes = LoRaPayload.EncodePond(5.42, 28.75, 7.91, 18.3, 0.412, 3.87);
        Assert.Equal(12, bytes.Length);
        var r = LoRaPayload.DecodePond(bytes, "d", "p", DateTime.UtcNow).ToDictionary(x => x.Kind, x => x.Value);
        Assert.Equal(5.42, r[SensorKind.DissolvedOxygen], 6);
        Assert.Equal(28.75, r[SensorKind.Temperature], 6);
        Assert.Equal(0.412, r[SensorKind.Ammonia], 6);
        string ttn = "{\"end_device_ids\":{\"device_id\":\"lora-k01\"},\"received_at\":\"2026-03-01T10:00:00Z\",\"uplink_message\":{\"frm_payload\":\"" + Convert.ToBase64String(bytes) + "\"}}";
        Assert.Equal("lora-k01", LoRaPayload.ParseTtnUplink(ttn, "K-01")[0].DeviceId);
    }

    [Fact]
    public void Telemetry_json_and_mqtt_topics()
    {
        var r = SensorReading.ParseTelemetry("""{"device":"esp32-01","pond":"K-01","ts":1767225600,"temp":29.1,"do":5.2,"ph":7.8,"unknown":1}""");
        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Equal("K-01", x.PondId));
        var o = new MqttOptions(FarmId: "tambak-1");
        var parsed = MqttSensorTransport.Parse(MqttSensorTransport.TelemetryTopic(o, "K-02", "esp-7"), """{"do":4.1}""");
        Assert.Equal("K-02", parsed[0].PondId);
        Assert.Equal("esp-7", parsed[0].DeviceId);
        Assert.Empty(MqttSensorTransport.Parse("aquaculture/x/cmd/esp", "{}"));
    }

    [Fact]
    public void Edge_analytics_rejects_spikes_and_detects_stuck_sensors()
    {
        var e = new EdgeAnalytics();
        var t = DateTime.UtcNow;
        for (int i = 0; i < 10; i++) Assert.False(e.Process(new SensorReading("d", "p", SensorKind.DissolvedOxygen, 5 + 0.05 * (i % 3), t)).IsOutlier);
        Assert.True(e.Process(new SensorReading("d", "p", SensorKind.DissolvedOxygen, 15, t)).IsOutlier);
        EdgeVerdict v = null!;
        for (int i = 0; i < 14; i++) v = e.Process(new SensorReading("d", "p", SensorKind.Ph, 7.0, t));
        Assert.True(v.IsStuck);
        e.Calibrate("d", SensorKind.Temperature, 20, 21, 30, 31);
        Assert.Equal(26, e.Process(new SensorReading("d", "p", SensorKind.Temperature, 25, t)).Corrected!.Value, 6);
    }
}

public class GisTests
{
    [Fact]
    public void Geojson_round_trip_and_pond_block()
    {
        var ponds = FarmMapping.PondBlock(new GeoCoordinate(-6.3, 107.3), 2, 3, 40, 50);
        Assert.Equal(6, ponds.Count);
        Assert.Equal(2000, ponds[0].Outline!.AreaM2, 0);
        var json = GeoJson.Write(ponds.Select(p => new MapFeature(p.Id, "pond", p.Outline)).Append(new MapFeature("s1", "sensor", Point: new GeoCoordinate(-6.3, 107.3))));
        var back = GeoJson.Read(json);
        Assert.Equal(7, back.Count);
        Assert.Equal(2000, back[0].Polygon!.AreaM2, 0);
        Assert.Equal("sensor", back[^1].Layer);
    }

    [Fact]
    public void Utm_projection_round_trip_and_union()
    {
        var c = new GeoCoordinate(-6.3045, 107.3012);
        var (e, n, zone, north) = NtsInterop.ToUtm(c);
        Assert.Equal(48, zone); Assert.False(north);
        var back = NtsInterop.FromUtm(e, n, zone, north);
        Assert.Equal(c.Latitude, back.Latitude, 6);
        var r = GeoPolygon.Rectangle(c, 40, 50);
        Assert.InRange(NtsInterop.UnionAreaM2([r, r]), 1980, 2020); // spherical local projection vs ellipsoidal UTM (~0.3 %)
        Assert.True(NtsInterop.BufferMeters(r, 10).AreaM2 > r.AreaM2 + 1500);
    }

    [Fact]
    public void Idw_is_exact_at_samples_and_d8_drains_downhill()
    {
        var b = new GeoBounds(-6.31, 107.30, -6.30, 107.31);
        var r = FarmMapping.Idw([(new GeoCoordinate(-6.305, 107.305), 7), (new GeoCoordinate(-6.309, 107.301), 3)], b, 20, 20);
        Assert.InRange(r.Data.Where(float.IsFinite).Max(), 6.5, 7.0);
        var dem = DrainageAnalysis.SyntheticDem(b, 30, 30);
        var d = DrainageAnalysis.D8(dem);
        int start = 2 * 30 + 15;
        var path = DrainageAnalysis.FlowPath(d, start);
        Assert.True(dem.Data[path[^1]] <= dem.Data[start]);
        Assert.Equal(30 * 30, d.Accumulation.Where((_, i) => d.FlowDirection[i] == 255).Sum());
    }

    [Fact]
    public void Water_source_ranking_respects_salinity()
    {
        var site = new GeoCoordinate(-6.3, 107.3, 3);
        var srcs = new[]
        {
            new WaterSource("sea", WaterSourceKind.Sea, new GeoCoordinate(-6.29, 107.3), 32),
            new WaterSource("well", WaterSourceKind.Groundwater, new GeoCoordinate(-6.3001, 107.3), 0.5),
        };
        Assert.Equal("well", WaterSourceAnalysis.Evaluate(site, 3, srcs, 200, SpeciesCatalog.Nila)[0].Source.Id);
        Assert.Equal("sea", WaterSourceAnalysis.Evaluate(site, 3, srcs, 200, SpeciesCatalog.Kerapu)[0].Source.Id);
    }
}

public class AiTests
{
    [Fact]
    public void Ridge_and_gbt_fit_simple_functions()
    {
        var d = new Dataset(["x1", "x2"]);
        for (int i = 0; i < 300; i++) { double a = i % 17 / 17.0, b = i % 11 / 11.0; d.Add([a, b], 3 * a - 2 * b + 1); }
        var ridge = new RidgeRegression(1e-6).Fit(d);
        Assert.Equal(3 * 0.5 - 2 * 0.2 + 1, ridge.Predict([0.5, 0.2]), 3);
        var gbt = new GradientBoostedTrees(200, 3, 0.1).Fit(d);
        Assert.True(Metrics.R2(gbt, d) > 0.95);
    }

    [Fact]
    public void Growth_and_mortality_predictors_learn_the_engine()
    {
        var gp = new GrowthPredictor(SpeciesCatalog.Nila, 800);
        Assert.True(gp.HoldoutR2 > 0.8, $"R² {gp.HoldoutR2}");
        var good = gp.Predict(new PondState(100, 29, 6, 0.01, 0.3));
        var bad = gp.Predict(new PondState(100, 29, 2.3, 0.15, 0.3));
        Assert.True(good.AdgG > bad.AdgG);
        var mp = new MortalityPredictor(SpeciesCatalog.Vaname, 1200);
        Assert.True(mp.Auc > 0.85, $"AUC {mp.Auc}");
        Assert.True(mp.Predict(new PondState(10, 29, 1.5, 0.4, 1.1)).Probability > mp.Predict(new PondState(10, 29, 6, 0.02, 0.3)).Probability);
    }

    [Fact]
    public void Feed_policy_cuts_rations_under_hypoxia()
    {
        var p = new FeedOptimizationPolicy(SpeciesCatalog.Vaname, episodes: 2500);
        Assert.True(p.Decide(1.8, 29, 0.05).RationFactor <= 0.5);
        Assert.True(p.Decide(6.5, 29, 0.03).RationFactor >= 1.0);
    }

    [Fact]
    public void MlNet_backend_trains()
    {
        var data = GrowthPredictor.SyntheticDataset(SpeciesCatalog.Nila, 400, 3);
        var m = new MlNetGrowthModel().Train(MlNetGrowthModel.FromDataset(data));
        Assert.True(m.RSquared > 0.3, $"{m.RSquared}");
        string path = Path.Combine(Path.GetTempPath(), $"aqua-mlnet-{Guid.NewGuid():N}.zip");
        m.Save(path);
        var loaded = new MlNetGrowthModel().Load(path);
        var rec = MlNetGrowthModel.FromDataset(data).First();
        Assert.Equal(m.Predict(rec), loaded.Predict(rec), 4);
        File.Delete(path);
    }

    [Fact]
    public async Task Offline_agent_answers_in_the_question_language_with_grounded_tools()
    {
        var agent = new AquaAgent(new AquaContext(), new RuleBasedChatModel());
        var id = await agent.AskAsync("Berapa pakan udang hari ini?");
        Assert.Equal("id", id.Language);
        Assert.Contains("feeding_plan", id.ToolsUsed);
        Assert.Contains("Ransum harian", id.Text);
        var en = await agent.AskAsync("What is the water quality and oxygen risk tonight?");
        Assert.Equal("en", en.Language);
        Assert.Contains("water_quality_check", en.ToolsUsed);
        Assert.Contains("WQI", en.Text);
    }

    [Fact]
    public void All_agent_tools_run()
    {
        var ctx = new AquaContext();
        foreach (var t in AquaTools.Create(ctx, () => "en"))
        {
            var args = t.Name == "disease_diagnosis" ? new System.Text.Json.Nodes.JsonObject { ["signs"] = new System.Text.Json.Nodes.JsonArray("white_spots_shell") } : [];
            Assert.False(string.IsNullOrWhiteSpace(t.Invoke(args)), t.Name);
            Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(t.ParametersSchema));
        }
    }

    [Fact]
    public void Llm_options_urls()
    {
        Assert.Equal("https://x.openai.azure.com/openai/v1/chat/completions", LlmOptions.AzureOpenAI("https://x.openai.azure.com/", "k", "gpt").ChatCompletionsUrl);
        Assert.Equal("https://api.anthropic.com/v1/messages", LlmOptions.Anthropic("k").ChatCompletionsUrl);
        Assert.Equal("http://localhost:11434/v1/chat/completions", LlmOptions.Ollama().ChatCompletionsUrl);
    }
}

public class VisionTests
{
    [Fact]
    public void Counts_and_screens_synthetic_shrimp()
    {
        var (img, animals) = SyntheticAquaImage.Create(640, 420, 14, shrimp: true, seed: 4, diseasedFraction: 0.5);
        var r = AnimalAnalyzer.Analyze(img, SpeciesCatalog.Vaname, cmPerPixel: 0.25);
        Assert.InRange(r.Count, animals.Count - 1, animals.Count + 1);
        int sick = animals.Count(a => a.WhiteSpots || a.RedLesion || a.BlackGill);
        Assert.InRange(r.Suspicious, Math.Max(1, sick - 3), sick + 2);
        Assert.InRange(r.MeanLengthCm, 8, 16);
    }

    [Fact]
    public void Yolo_decoding_and_nms()
    {
        // two overlapping boxes of class 0 and one of class 1 (YOLOv8 layout [4 + nc, anchors])
        int anchors = 3, nc = 2;
        var o = new float[(4 + nc) * anchors];
        void Set(int a, float cx, float cy, float w, float h, float c0, float c1) { o[a] = cx; o[anchors + a] = cy; o[2 * anchors + a] = w; o[3 * anchors + a] = h; o[4 * anchors + a] = c0; o[5 * anchors + a] = c1; }
        Set(0, 100, 100, 40, 40, 0.9f, 0); Set(1, 104, 102, 40, 40, 0.8f, 0); Set(2, 300, 300, 50, 30, 0, 0.7f);
        var d = YoloDecoder.Decode(o, anchors, nc, ["fish", "shrimp"], 1, 0, 0);
        Assert.Equal(2, d.Count);
        Assert.Equal("fish", d[0].Label);
        Assert.Equal(80, d[0].Box.X, 3);
    }

    [Fact]
    public void Letterbox_keeps_aspect()
    {
        var (lb, scale, px, py) = ImageOps.Letterbox(new RgbImage(320, 160), 640);
        Assert.Equal(2, scale, 6); Assert.Equal(0, px); Assert.Equal(160, py);
        Assert.Equal(640 * 640 * 3, lb.Data.Length);
    }
}

public class TwinTests
{
    [Fact]
    public async Task Farm_twin_ingests_simulated_sensors_and_decides()
    {
        var farm = new Farm { Id = "F1", Name = "Demo" };
        var pond = new Pond { Id = "K-01", AreaM2 = 2000, DepthM = 1.3, AerationKw = 4, System = CultureSystem.LinedPond };
        farm.Ponds.Add(pond);
        farm.Cycles.Add(new CultureCycle { Id = "c", PondId = "K-01", Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 1, 1), StockedCount = 200_000, StockingWeightG = 0.002 }
            .LogSampling(new DateOnly(2026, 2, 20), 10));
        var sim = new SimulatedSensorTransport([new SimulatedPond("K-01", "esp-1", MeanDo: 3.6, DoAmplitude: 2.0, SalinityPpt: 18, Tan: 1.4)]) { DropoutProbability = 0 };
        await using var twin = new FarmTwin(farm, sim) { AutoActuate = true };
        twin["K-01"].AeratorDeviceId = "aer-1";
        await twin.StartAsync();
        var t0 = new DateTime(2026, 2, 21, 12, 0, 0);
        for (int i = 0; i < 48; i++) sim.Emit(t0.AddMinutes(30 * i));
        Assert.True(twin.ReadingsIngested > 300);
        var decisions = await twin.EvaluateAsync(t0.AddHours(24));
        Assert.Contains(decisions, d => d.Kind == DecisionKind.Feeding);
        var state = twin.States(t0.AddHours(24))[0];
        Assert.True(state.BiomassKg > 1000);
        Assert.NotNull(state.ForecastDawnDo);
        Assert.Contains(sim.Commands, c => c.Action == "aerator_on");
    }

    [Fact]
    public void What_if_aerator_failure_hurts()
    {
        var twin = new PondTwin(new Pond { Id = "p", AreaM2 = 1000, DepthM = 1.2 }, SpeciesCatalog.Nila, new DateOnly(2026, 1, 1), 100, 5000);
        twin.Ingest(new SensorReading("d", "p", SensorKind.DissolvedOxygen, 5.5, DateTime.UtcNow));
        twin.Ingest(new SensorReading("d", "p", SensorKind.Temperature, 29, DateTime.UtcNow));
        var r = twin.WhatIf("aerators off", 14, e => e with { DissolvedOxygen = 1.5 });
        Assert.True(r.BiomassDeltaKg < 0);
        Assert.True(r.SurvivalDeltaPct < 0);
    }
}

public class ReportingTests
{
    private static ReportDocument Sample() => new ReportDocument { Title = "Laporan uji — °C, NH₃", Subtitle = "test" }
        .H("KPI").Kv(("FCR", "1.32"), ("SR", "84")).P("Paragraph with (parentheses) and ≥ symbols.").List("a", "b")
        .Table("Daily", ["Day", "ABW", "Note"], [[1, 2.5, "ok"], [2, 2.9, "a,b \"q\""]])
        .Image(PngEncoder.Raster([0f, 0.5f, 1f, float.NaN], 2, 2, ColorRamp.Score.WithRange(0, 1), 4), "chart");

    [Fact]
    public void Csv_quotes_fields()
    {
        var csv = ReportWriters.Csv(Sample());
        Assert.Contains("\"a,b \"\"q\"\"\"", csv);
        Assert.StartsWith("Day,ABW,Note", csv);
    }

    [Fact]
    public void Xlsx_and_docx_are_valid_packages()
    {
        foreach (var (bytes, main) in new[] { (ReportWriters.Xlsx(Sample()), "xl/workbook.xml"), (ReportWriters.Docx(Sample()), "word/document.xml") })
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            var entry = zip.GetEntry(main)!;
            using var r = new StreamReader(entry.Open());
            System.Xml.Linq.XDocument.Parse(r.ReadToEnd());
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            {
                using var s = new StreamReader(e.Open());
                System.Xml.Linq.XDocument.Parse(s.ReadToEnd());
            }
        }
    }

    [Fact]
    public void Pdf_has_valid_structure()
    {
        var pdf = ReportWriters.Pdf(Sample());
        string head = Encoding.Latin1.GetString(pdf, 0, 8), tail = Encoding.Latin1.GetString(pdf, pdf.Length - 6, 6);
        Assert.StartsWith("%PDF-1.4", head);
        Assert.Contains("%%EOF", tail);
        string text = Encoding.Latin1.GetString(pdf);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long offset = long.Parse(text[(sx + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", text[(int)offset..]);
    }

    [Fact]
    public void Json_markdown_html()
    {
        var d = Sample();
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(ReportWriters.Json(d)));
        Assert.Contains("| Day | ABW | Note |", ReportWriters.Markdown(d));
        Assert.Contains("Gravicode Studios", ReportWriters.Html(d));
    }

    [Fact]
    public void Compliance_major_no_blocks_readiness()
    {
        var all = ComplianceAudit.Checklist(ComplianceStandard.Cbib).ToDictionary(i => i.Id, _ => ComplianceAnswer.Yes);
        Assert.Equal(ReadinessLevel.Ready, ComplianceAudit.Evaluate(ComplianceStandard.Cbib, all).Readiness);
        all["cbib-6"] = ComplianceAnswer.No;
        var r = ComplianceAudit.Evaluate(ComplianceStandard.Cbib, all);
        Assert.Equal(ReadinessLevel.NotReady, r.Readiness);
        Assert.Equal("cbib-6", r.NonConformities[0].Item.Id);
        foreach (var s in Enum.GetValues<ComplianceStandard>()) Assert.True(ComplianceAudit.Checklist(s).Count >= 10);
    }

    [Fact]
    public void Png_encoder_writes_valid_signature_and_svg_is_xml()
    {
        var png = PngEncoder.Raster([1f, 2f, 3f, 4f], 2, 2, ColorRamp.Oxygen);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        var svg = SvgCharts.Line([new Series("DO", [(0, 5), (1, 6), (2, 4)])], new ChartOptions { Title = "DO <test>", References = [new Reference(3, "critical")] });
        System.Xml.Linq.XDocument.Parse(svg);
        System.Xml.Linq.XDocument.Parse(SvgCharts.Gauge(72, 100, "WQI"));
        System.Xml.Linq.XDocument.Parse(SvgCharts.Bars([("A", 1), ("B", 2)]));
    }
}
