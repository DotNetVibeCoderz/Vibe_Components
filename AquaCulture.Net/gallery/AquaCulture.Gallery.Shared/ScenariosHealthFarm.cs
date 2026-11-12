using System.Text;
using AquaCulture.ComputerVision;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Species;
using AquaCulture.DigitalTwin;
using AquaCulture.Disease;
using AquaCulture.Environment;
using AquaCulture.GIS;
using AquaCulture.IoT;
using AquaCulture.Visualization;
using AquaCulture.WaterQuality;

namespace AquaCulture.Gallery.Shared;

// ───────────────────────────── Disease ─────────────────────────────
public sealed record DiseaseDemoResult(DiagnosisResult Diagnosis, IReadOnlyList<DiseaseRisk> Risks, string ProbabilitySvg, string RiskSvg, IReadOnlyList<Symptom> Symptoms, SpeciesProfile Species);

public static class DiseaseDemo
{
    public static readonly string[] DefaultShrimpSigns = ["white_spots_shell", "reduced_feeding", "red_body"];
    public static readonly string[] DefaultFishSigns = ["skin_ulcers", "hemorrhage", "reduced_feeding"];

    public static DiseaseDemoResult Run(string speciesId, IReadOnlyCollection<string> signs, double temp = 27, double swing = 4, double salinityDrop = 5, int doc = 45, double dawnDo = 4)
    {
        var s = SpeciesCatalog.Get(speciesId);
        var cond = new PondConditions { TemperatureC = temp, TemperatureSwing24h = swing, SalinityDrop = salinityDrop, DayOfCulture = doc, DawnDo = dawnDo,
            SalinityPpt = s.Habitat == Habitat.Freshwater ? 0 : 18, DensityRatio = 0.7, OrpMv = 160 };
        var dx = DiagnosisEngine.Diagnose(s, signs, cond);
        var risks = DiagnosisEngine.RiskProfile(s, cond);
        var top = dx.Candidates.Take(6).ToList();
        var probSvg = SvgCharts.Bars(top.Select(c => (c.Disease.Id.ToUpperInvariant(), Math.Round(100 * c.Probability, 1))).ToList(),
            new ChartOptions { Height = 220, YMax = 100, YMin = 0, YFormat = "0", Title = "P(disease | signs) %" }, top.Select((c, i) => i == 0 ? "#e0614a" : "#d9a13b").ToList());
        var riskSvg = SvgCharts.Bars(risks.Take(6).Select(r => (r.Disease.Id.ToUpperInvariant(), Math.Round(100 * r.Risk))).ToList(),
            new ChartOptions { Height = 200, YMax = 100, YMin = 0, YFormat = "0", Title = "Environmental risk %" }, ["#6c5ca8"]);
        return new DiseaseDemoResult(dx, risks, probSvg, riskSvg, DiseaseKnowledgeBase.SymptomsFor(s).ToList(), s);
    }
}

// ───────────────────────────── Vision ─────────────────────────────
public sealed record VisionDemoResult(byte[] Original, byte[] Annotated, CountResult Result, int TruthCount, int TruthSick, int Width, int Height);

public static class VisionDemo
{
    public static VisionDemoResult Run(bool shrimp = true, int count = 18, double sickFraction = 0.35, ulong seed = 8)
    {
        var (img, animals) = SyntheticAquaImage.Create(720, 460, count, shrimp, seed, sickFraction);
        var species = shrimp ? SpeciesCatalog.Vaname : SpeciesCatalog.Nila;
        var r = AnimalAnalyzer.Analyze(img, species, shrimp ? 0.25 : 0.32);
        var ann = AnimalAnalyzer.Annotate(img, r);
        return new VisionDemoResult(GalleryImages.Png(img), GalleryImages.Png(ann), r, animals.Count, animals.Count(a => a.WhiteSpots || a.RedLesion || a.BlackGill), img.Width, img.Height);
    }
}

// ───────────────────────────── Digital twin ─────────────────────────────
public sealed record TwinDemoResult(IReadOnlyList<TwinState> States, IReadOnlyList<TwinDecision> Decisions, string MapSvg, string HeatSvg, int Readings, int Outliers,
    IReadOnlyList<ActuatorCommand> Commands, IReadOnlyList<Pond> Ponds, IReadOnlyDictionary<string, double> DoByPond);

public static class TwinDemo
{
    public static readonly SimulatedPond[] Profiles =
    [
        new("K-01", "buoy-k01", 30.2, 5.6, 1.8, 8.0, 18, 0.7), new("K-02", "buoy-k02", 30.0, 4.6, 2.0, 8.3, 19, 1.4),
        new("K-03", "buoy-k03", 29.6, 6.1, 1.5, 7.9, 18, 0.4), new("K-04", "buoy-k04", 30.4, 4.1, 2.1, 8.4, 20, 1.9),
        new("K-05", "buoy-k05", 29.4, 6.4, 1.4, 7.8, 17, 0.2), new("K-06", "buoy-k06", 30.1, 5.3, 1.8, 8.1, 18, 0.9),
    ];

    public static TwinDemoResult Run(int hours = 30, bool autoActuate = true)
    {
        var farm = DemoFarm.Farm;
        var sim = new SimulatedSensorTransport(Profiles, 11) { DropoutProbability = 0.01 };
        var twin = new FarmTwin(farm, sim) { AutoActuate = autoActuate, WeatherForecast = DemoFarm.Weather(3) };
        foreach (var p in twin.Ponds) { p.AeratorDeviceId = $"aer-{p.Pond.Id.ToLowerInvariant()}"; }
        foreach (var pr in Profiles)
            twin.Devices.Register(new IoTDevice(pr.DeviceId, DeviceKind.Esp32, IoTProtocol.Mqtt, pr.PondId, [SensorKind.Temperature, SensorKind.DissolvedOxygen, SensorKind.Ph, SensorKind.Ammonia]));
        twin.StartAsync().GetAwaiter().GetResult();
        var start = DemoFarm.Now.AddHours(-hours);
        var heat = new double[Profiles.Length, hours];
        for (int i = 0; i < hours * 2; i++)
        {
            var t = start.AddMinutes(30 * i);
            var rs = sim.Emit(t);
            if (i % 2 == 0)
                for (int p = 0; p < Profiles.Length; p++)
                    heat[p, i / 2] = rs.FirstOrDefault(r => r.PondId == Profiles[p].PondId && r.Kind == SensorKind.DissolvedOxygen)?.Value ?? (i > 1 ? heat[p, i / 2 - 1] : 5);
        }
        var decisions = twin.EvaluateAsync(DemoFarm.Now).GetAwaiter().GetResult();
        var states = twin.States(DemoFarm.Now).OrderBy(s => s.PondId).ToList();
        var doBy = states.ToDictionary(s => s.PondId, s => s.Water.DissolvedOxygen ?? double.NaN);
        var map = SvgCharts.Map(farm.Ponds.Select(p => (p.Id, p.Outline!, doBy[p.Id], $"DO {doBy[p.Id]:0.0}")).ToList(), ColorRamp.Oxygen.WithRange(0, 8));
        var cols = Enumerable.Range(0, hours).Select(h => start.AddHours(h).ToString("HH")).ToList();
        var heatSvg = SvgCharts.Heatmap(Profiles.Select(p => p.PondId).ToList(), cols, heat, ColorRamp.Oxygen.WithRange(0, 8), new ChartOptions { Height = 40 + Profiles.Length * 26 });
        return new TwinDemoResult(states, decisions.OrderByDescending(d => d.Severity).ToList(), map, heatSvg, twin.ReadingsIngested, twin.OutliersRejected, sim.Commands.ToList(), farm.Ponds, doBy);
    }
}

// ───────────────────────────── IoT ─────────────────────────────
public sealed record IoTDemoResult(string ModbusRequestHex, string ModbusResponseHex, IReadOnlyList<(SensorKind Kind, double Value)> ModbusValues,
    string LoraHex, IReadOnlyList<SensorReading> LoraReadings, string MqttTopic, string MqttPayload, IReadOnlyList<SensorReading> MqttReadings,
    string EdgeSvg, int Outliers, string FirmwareSnippet);

public static class IoTDemo
{
    public static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    public static IoTDemoResult Run(double doMgL = 5.12, double tempC = 29.1, double spike = 14)
    {
        var req = Modbus.ReadHoldingRegistersRtu(7, 0, 6);
        ushort[] regs = [(ushort)Math.Round(doMgL * 100), (ushort)Math.Round(tempC * 10), 785, unchecked((ushort)(short)185), 182, 343];
        var resp = Modbus.BuildReadResponseRtu(7, regs);
        var values = Modbus.Decode(Modbus.ParseReadResponseRtu(resp, 7), 0, Modbus.TypicalProbeMap);
        var lora = LoRaPayload.EncodePond(doMgL, tempC, 7.85, 18.2, 0.84, 3.92);
        var loraR = LoRaPayload.DecodePond(lora, "lora-k02", "K-02", DemoFarm.Now);
        var o = new MqttOptions(FarmId: "mina-sejahtera");
        string topic = MqttSensorTransport.TelemetryTopic(o, "K-03", "esp32-k03");
        string payload = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$"""{"ts":"{{DemoFarm.Now:yyyy-MM-ddTHH:mm:ssZ}}","temp":{{tempC:0.0}},"do":{{doMgL:0.00}},"ph":7.92,"sal":18.4,"rssi":-71}""");
        var mqttR = MqttSensorTransport.Parse(topic, payload);
        // edge analytics on a 6-hour DO stream with a spike
        var edge = new EdgeAnalytics();
        var raw = new List<(double, double)>(); var clean = new List<(double, double)>(); var flagged = 0;
        var rng = new Core.Data.DeterministicRandom(3);
        for (int i = 0; i < 72; i++)
        {
            double v = 5.6 - 0.025 * i + 0.08 * rng.NextGaussian();
            if (i is 30 or 51) v = spike;
            var verdict = edge.Process(new SensorReading("buoy", "K-01", SensorKind.DissolvedOxygen, v, DemoFarm.Now.AddMinutes(5 * i)));
            raw.Add((i * 5 / 60.0, v));
            if (verdict.IsOutlier) flagged++; else clean.Add((i * 5 / 60.0, verdict.Smoothed));
        }
        var edgeSvg = SvgCharts.Line([new Series("raw", raw, "#e0614a"), new Series("filtered (EMA)", clean, "#12a4b6")], new ChartOptions { Height = 220, XFormat = x => $"{x:0.#} h" });
        const string fw = """
            // ESP32 (Arduino) — publish pond telemetry every 60 s
            #include <WiFi.h>
            #include <PubSubClient.h>
            WiFiClient net; PubSubClient mqtt(net);
            void loop() {
              float t = readTemp(), dox = readDO(), ph = readPH();
              char json[96];
              snprintf(json, sizeof json, "{\"temp\":%.2f,\"do\":%.2f,\"ph\":%.2f}", t, dox, ph);
              mqtt.publish("aquaculture/mina-sejahtera/K-03/esp32-k03/telemetry", json);
              delay(60000);
            }
            """;
        return new IoTDemoResult(Hex(req), Hex(resp), values, Hex(lora), loraR, topic, payload, mqttR, edgeSvg, flagged, fw);
    }
}

// ───────────────────────────── GIS ─────────────────────────────
public sealed record GisDemoResult(GeoRaster DoRaster, GeoRaster Dem, byte[] DoPng, byte[] DrainPng, IReadOnlyList<MapFeature> Features, IReadOnlyList<WaterSourceEvaluation> Sources,
    double FarmAreaM2, double BufferAreaM2, string GeoJsonSnippet, int Channels, string MapSvg);

public static class GisDemo
{
    public static GisDemoResult Run(int channelThreshold = 40, double bufferM = 50)
    {
        var farm = DemoFarm.Farm;
        var b = DemoFarm.Bounds;
        var twin = TwinDemo.Profiles;
        var buoys = farm.Ponds.Select((p, i) => (p.Location!.Value, twin[i].MeanDo - twin[i].DoAmplitude * 0.6)).ToList();
        var rnd = new Core.Data.DeterministicRandom(9);
        var extra = Enumerable.Range(0, 6).Select(i => (GeoMath.FromLocal(DemoFarm.Origin, new LocalPoint(rnd.Uniform(0, 135), rnd.Uniform(0, 105))), rnd.Uniform(2.5, 6.5))).ToList();
        var samples = buoys.Concat(extra).ToList();
        var doR = FarmMapping.Idw(samples, b, 96, 76);
        var dem = DrainageAnalysis.SyntheticDem(b.Expand(0.002), 90, 70, 5);
        var d8 = DrainageAnalysis.D8(dem);
        var drain = new float[dem.Data.Length];
        for (int i = 0; i < drain.Length; i++) drain[i] = d8.Accumulation[i] >= channelThreshold ? float.NaN : dem.Data[i];
        var demPng = PngEncoder.Raster(drain, dem.Width, dem.Height, ColorRamp.Terrain.WithRange(dem.Data.Min(), dem.Data.Max()), 5);
        var features = FarmMapping.Features(farm);
        var boundary = new GeoPolygon(ConvexHull(farm.Ponds.SelectMany(p => p.Outline!.Ring).ToList()));
        double area = NtsInterop.UnionAreaM2(farm.Ponds.Select(p => p.Outline!));
        var buffer = NtsInterop.BufferMeters(boundary, bufferM);
        var sources = WaterSourceAnalysis.Evaluate(DemoFarm.Origin, 2, [
            new WaterSource("sea-intake", WaterSourceKind.Sea, GeoMath.Destination(DemoFarm.Origin, 900, 0) with { Elevation = 0 }, 31, 0.75, 0.95),
            new WaterSource("river-cimanuk", WaterSourceKind.River, GeoMath.Destination(DemoFarm.Origin, 2400, 120) with { Elevation = 1 }, 0.3, 0.6, 0.7),
            new WaterSource("irrigation-canal", WaterSourceKind.IrrigationCanal, GeoMath.Destination(DemoFarm.Origin, 600, 200) with { Elevation = 3 }, 0.4, 0.55, 0.5),
            new WaterSource("deep-well", WaterSourceKind.Groundwater, GeoMath.Destination(DemoFarm.Origin, 80, 270) with { Elevation = 2 }, 6, 0.9, 0.9),
        ], 650, SpeciesCatalog.Vaname);
        var geo = GeoJson.Write(features.Take(1), indented: true);
        var map = SvgCharts.Map(farm.Ponds.Select(p => (p.Id, p.Outline!, (double)farm.ActiveCycle(p.Id)!.EstimatedBiomassKg, $"{farm.ActiveCycle(p.Id)!.EstimatedBiomassKg:N0} kg")).ToList(),
            new ColorRamp("Biomass", 0, 4000, (0, "#e8f3ef"), (0.5, "#7cc6a4"), (1, "#127a7a")));
        return new GisDemoResult(doR, dem, PngEncoder.Raster(doR.Data, doR.Width, doR.Height, ColorRamp.Oxygen.WithRange(0, 8), 6), demPng, features, sources, area, buffer.AreaM2,
            geo.Length > 900 ? geo[..900] + "\n  …" : geo, d8.Channels(channelThreshold).Count(), map);
    }

    private static List<GeoCoordinate> ConvexHull(List<GeoCoordinate> p)
    {
        var pts = p.Distinct().OrderBy(x => x.Longitude).ThenBy(x => x.Latitude).ToList();
        double Cross(GeoCoordinate o, GeoCoordinate a, GeoCoordinate b) => (a.Longitude - o.Longitude) * (b.Latitude - o.Latitude) - (a.Latitude - o.Latitude) * (b.Longitude - o.Longitude);
        var lower = new List<GeoCoordinate>(); var upper = new List<GeoCoordinate>();
        foreach (var x in pts) { while (lower.Count >= 2 && Cross(lower[^2], lower[^1], x) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(x); }
        foreach (var x in Enumerable.Reverse(pts)) { while (upper.Count >= 2 && Cross(upper[^2], upper[^1], x) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(x); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        return lower.Concat(upper).ToList();
    }
}

// ───────────────────────────── Weather ─────────────────────────────
public sealed record WeatherDemoResult(IReadOnlyList<WeatherObservation> Forecast, WeatherImpactReport Impact, ClimateRiskReport Climate, string ForecastSvg, string RainSvg, string MatrixSvg, CultureSystem System);

public static class WeatherDemo
{
    public static WeatherDemoResult Run(double wetness = 1.4, CultureSystem system = CultureSystem.LinedPond, double depth = 1.3, double salinity = 18, ulong seed = 31)
    {
        var fc = SyntheticWeather.Generate(DemoFarm.Origin.Latitude, DemoFarm.Today.ToDateTime(TimeOnly.MinValue), 3, seed, 28.5, wetness);
        var impact = WeatherImpactPredictor.Predict(fc, new PondExposure(depth, salinity, system, system == CultureSystem.EarthenPond, 29.5));
        var tw = WeatherImpactPredictor.WaterTemperature(fc, depth, 29.5);
        var year = DailyWeather.FromHourly(SyntheticWeather.Generate(DemoFarm.Origin.Latitude, new DateTime(2025, 1, 1), 365, seed + 1, 28, wetness));
        var climate = ClimateRiskAnalysis.Analyze(year, system);
        var tSvg = SvgCharts.Line(
            [
                new Series("Air °C", fc.Select((w, i) => ((double)i, w.AirTempC)).ToList(), "#d9a13b"),
                new Series("Water °C", tw.Select((w, i) => ((double)i, w)).ToList(), "#12a4b6"),
            ], new ChartOptions { Height = 230, XFormat = h => $"+{h:0}h" });
        var rain = fc.Chunk(6).Select((c, i) => ($"+{i * 6}h", Math.Round(c.Sum(x => x.RainfallMm), 1))).ToList();
        var rSvg = SvgCharts.Bars(rain, new ChartOptions { Height = 180, YFormat = "0", Title = "Rain mm / 6 h" }, ["#2f6fb0"]);
        // 5 × 5 risk matrix
        var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 330 300\" font-family=\"inherit\">");
        for (int li = 1; li <= 5; li++)
            for (int im = 1; im <= 5; im++)
            {
                int score = li * im; string col = score >= 15 ? "#e0614a" : score >= 10 ? "#e9a35a" : score >= 5 ? "#e9d38a" : "#9fd3b8";
                sb.Append($"<rect x=\"{40 + (im - 1) * 56}\" y=\"{10 + (5 - li) * 52}\" width=\"54\" height=\"50\" rx=\"4\" fill=\"{col}\" opacity=\"0.85\"/>");
            }
        foreach (var (h, k) in climate.Hazards.Select((h, k) => (h, k)))
        {
            double x = 40 + (h.Impact - 1) * 56 + 10 + (k % 3) * 16, y = 10 + (5 - h.Likelihood) * 52 + 14 + (k / 3) * 18;
            sb.Append($"<circle cx=\"{x}\" cy=\"{y}\" r=\"7\" fill=\"#0b2e36\"/><text x=\"{x}\" y=\"{y + 3.5}\" text-anchor=\"middle\" font-size=\"9\" fill=\"#fff\">{k + 1}</text>");
        }
        sb.Append("<text x=\"180\" y=\"292\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--aq-muted, #5b7279)\">Impact →</text><text x=\"14\" y=\"140\" transform=\"rotate(-90 14 140)\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--aq-muted, #5b7279)\">Likelihood →</text></svg>");
        return new WeatherDemoResult(fc, impact, climate, tSvg, rSvg, sb.ToString(), system);
    }
}
