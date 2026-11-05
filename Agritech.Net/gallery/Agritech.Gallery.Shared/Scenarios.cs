using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Agritech.AI;
using Agritech.Core.Data;
using Agritech.Core.Farming;
using Agritech.Core.Geo;
using Agritech.Crops;
using Agritech.DigitalTwin;
using Agritech.Drones;
using Agritech.Fisheries;
using Agritech.Genomics;
using Agritech.IoT;
using Agritech.Livestock;
using Agritech.Marketplace;
using Agritech.Native;
using Agritech.RemoteSensing;
using Agritech.Robotics;
using Agritech.Simulation;
using Agritech.Soil;
using Agritech.Visualization;
using Agritech.Weather;

namespace Agritech.Gallery.Shared;

/// <summary>Reference farm used across demos: a paddy block in Karawang, West Java (Indonesia's rice belt).</summary>
public static class DemoFarm
{
    public static readonly GeoCoordinate Center = new(-6.3045, 107.3012, 18);
    public static readonly GeoPolygon Field = new(
    [
        GeoMath.FromLocal(Center, new(-170, -110)), GeoMath.FromLocal(Center, new(150, -130)),
        GeoMath.FromLocal(Center, new(185, 60)), GeoMath.FromLocal(Center, new(40, 125)), GeoMath.FromLocal(Center, new(-160, 95)),
    ]);
    public static readonly WeatherStation Station = new(Center.Latitude, 18, "Karawang");
    public static readonly SoilSample Soil = new()
    {
        Id = "KRW-01", Ph = 5.4, OrganicCarbonPct = 1.2, TotalNitrogenPct = 0.12, AvailablePMgKg = 9, ExchangeableKCmolKg = 0.28,
        CecCmolKg = 18, EcDsM = 0.6, SandPct = 22, SiltPct = 38, ClayPct = 40, BulkDensity = 1.32,
    };
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>Resolves CSS custom properties in generated SVG for renderers without CSS (Avalonia/Svg.Skia).</summary>
public static partial class SvgTheme
{
    [GeneratedRegex(@"var\(--[a-z0-9-]+,\s*(#[0-9a-fA-F]{3,8})\)")]
    private static partial Regex VarRegex();

    public static string Resolve(string svg, bool dark = false)
    {
        var s = VarRegex().Replace(svg, m => m.Groups[1].Value).Replace("font-family=\"inherit\"", "font-family=\"Segoe UI, Arial, sans-serif\"");
        if (dark) s = s.Replace("#23261f", "#e6ebe3").Replace("#6b6f66", "#9fa89b").Replace("#e3e0d6", "#33402f");
        return s;
    }
}

// ───────────────────────────── Remote sensing ─────────────────────────────
public sealed record NdviDemoResult(byte[] NdviPng, byte[] ZonesPng, byte[] TruePng, ZonalStatistics Stats, int Width, int Height,
    KernelBackend Backend, double MegapixelsPerSecond, double[] ZoneMeans);

public static class NdviDemo
{
    public static NdviDemoResult Run(int seed = 7, int stressPatches = 3)
    {
        var b = DemoFarm.Field.Bounds;
        var pad = new GeoBounds(b.MinLat - 0.0004, b.MinLon - 0.0004, b.MaxLat + 0.0004, b.MaxLon + 0.0004);
        var img = SyntheticScene.Create(pad, 5, seed, 0.68, stressPatches);
        var ndvi = VegetationIndices.Ndvi(img);
        // mask outside the field
        for (int r = 0; r < ndvi.Height; r++)
            for (int c = 0; c < ndvi.Width; c++)
                if (!DemoFarm.Field.Contains(ndvi.Transform.PixelCenter(c, r))) ndvi[c, r] = float.NaN;
        var zones = RasterAnalysis.ManagementZones(ndvi, 3);
        var zoneRaster = zones.Select(z => z == 0 ? float.NaN : (float)z).ToArray();
        var zoneMap = new ColorMap("Zones", 1, 3, (0, "#b5762e"), (0.5, "#d9c46a"), (1, "#2e6b3b"));
        var zoneMeans = Enumerable.Range(1, 3).Select(z => Math.Round(ndvi.Data.Where((v, i) => zones[i] == z).Select(v => (double)v).DefaultIfEmpty(double.NaN).Average(), 3)).ToArray();

        // true colour composite
        var rgb = new byte[ndvi.Width * ndvi.Height * 4];
        for (int i = 0; i < ndvi.Data.Length; i++)
        {
            bool inside = !float.IsNaN(ndvi.Data[i]);
            rgb[i * 4] = (byte)Math.Clamp(img[SpectralBand.Red].Data[i] * 900, 0, 255);
            rgb[i * 4 + 1] = (byte)Math.Clamp(img[SpectralBand.Green].Data[i] * 1300, 0, 255);
            rgb[i * 4 + 2] = (byte)Math.Clamp(img[SpectralBand.Blue].Data[i] * 1200, 0, 255);
            rgb[i * 4 + 3] = inside ? (byte)255 : (byte)70;
        }

        // throughput benchmark on 4 MP
        var a = new float[4_000_000]; var bb = new float[4_000_000]; var o = new float[4_000_000];
        for (int i = 0; i < a.Length; i++) { a[i] = 0.4f + (i % 97) * 0.003f; bb[i] = 0.1f + (i % 53) * 0.002f; }
        AgriKernels.NormalizedDifference(a, bb, o);
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 5; k++) AgriKernels.NormalizedDifference(a, bb, o);
        double mps = 20.0 / sw.Elapsed.TotalSeconds;

        return new NdviDemoResult(
            PngEncoder.FromRaster(ndvi.Data, ndvi.Width, ndvi.Height, ColorMap.Ndvi, 4),
            PngEncoder.FromRaster(zoneRaster, ndvi.Width, ndvi.Height, zoneMap, 4),
            Upscale(rgb, ndvi.Width, ndvi.Height, 4),
            RasterAnalysis.Zonal(ndvi, DemoFarm.Field, "Blok A"), ndvi.Width, ndvi.Height, AgriKernels.ActiveBackend, Math.Round(mps, 0), zoneMeans);
    }

    private static byte[] Upscale(byte[] rgba, int w, int h, int s)
    {
        var o = new byte[w * s * h * s * 4];
        for (int y = 0; y < h * s; y++) for (int x = 0; x < w * s; x++) Buffer.BlockCopy(rgba, ((y / s) * w + x / s) * 4, o, (y * w * s + x) * 4, 4);
        return PngEncoder.Encode(o, w * s, h * s);
    }
}

// ───────────────────────────── Soil & crops ─────────────────────────────
public sealed record SoilDemoResult(SoilHealthReport Report, SoilTextureClass Texture, SoilHydraulics Hydraulics, FertilizerAdvice Advice, string IndicatorSvg);

public static class SoilDemo
{
    public static SoilDemoResult Run(SoilSample? sample = null, string cropId = "rice", double areaHa = 2.5, double targetYield = 6.5)
    {
        var s = sample ?? DemoFarm.Soil;
        var report = SoilHealth.Assess(s);
        var advice = FertilizerAdvisor.Advise(CropCatalog.Default.Get(cropId), s, areaHa, targetYield);
        var svg = SvgCharts.Bars(report.Indicators.Select(i => (i.Indicator, Math.Round(i.Score * 100))).ToList(),
            new ChartOptions { Width = 520, Height = 220, YMax = 100 }, "#2e6b3b", "0");
        return new SoilDemoResult(report, s.Texture, s.Hydraulics(), advice, svg);
    }
}

public static class CropDemo
{
    public static IReadOnlyList<CropSuitability> Run(double temp = 26.5, double rain = 1400, double ph = 5.6, double elev = 300, bool irrigation = false) =>
        new CropRecommender().Recommend(new SiteConditions(temp, rain, ph, elev, irrigation), 8);
}

// ───────────────────────────── Simulation ─────────────────────────────
public sealed record SimulationDemoResult(SimulationResult Rainfed, SimulationResult Irrigated, string LaiSvg, string WaterSvg, string BiomassSvg,
    IReadOnlyList<ScenarioSummary> Scenarios, string ScenarioSvg);

public static class SimulationDemo
{
    public static SimulationDemoResult Run(string cropId = "maize", double nitrogen = 120, int years = 12, int seed = 3)
    {
        var crop = CropCatalog.Default.Get(cropId);
        var planting = new DateOnly(2026, 4, 1); // start of the dry season (gadu) — water matters
        var weather = new SyntheticWeatherProvider(seed, annualRainMm: 1500).Generate(DemoFarm.Center, planting, planting.AddDays(crop.SeasonLengthDays + 60));
        var sim = new CropSimulator(crop, DemoFarm.Soil, DemoFarm.Station);
        var nApps = new List<(int, double)> { (0, nitrogen * 0.3), (25, nitrogen * 0.4), (45, nitrogen * 0.3) };
        var baseM = new Management { PlantingDate = planting, NitrogenApplications = nApps };
        var rainfed = sim.Run(baseM, weather);
        var irrigated = sim.Run(baseM with { Irrigation = IrrigationStrategy.AutoAtRaw }, weather);
        var labels = rainfed.Days.Select(d => d.Dap.ToString(CultureInfo.InvariantCulture)).ToList();
        string Chart(Func<SimulationDay, double> f, string y, double? max = null) => SvgCharts.Line(
        [
            new ChartSeries("Rainfed / Tadah hujan", rainfed.Days.Select(f).ToList(), "#b5762e"),
            new ChartSeries("Auto-irrigated / Irigasi otomatis", irrigated.Days.Select(f).ToList(), "#2f7da0", Area: true),
        ], new ChartOptions { XLabels = labels, YLabel = y, YMin = 0, YMax = max });

        var scenarios = new ScenarioRunner(crop, DemoFarm.Soil, DemoFarm.Center, 18).Run(
        [
            new("Rainfed · today", baseM),
            new("Irrigated · today", baseM with { Irrigation = IrrigationStrategy.AutoAtRaw }),
            new("Rainfed · SSP2-4.5 2050", baseM, ClimateScenario.Ssp245, 2050),
            new("Rainfed · SSP5-8.5 2050", baseM, ClimateScenario.Ssp585, 2050),
            new("Irrigated · SSP5-8.5 2050", baseM with { Irrigation = IrrigationStrategy.AutoAtRaw }, ClimateScenario.Ssp585, 2050),
        ], years, seed);
        var scenSvg = SvgCharts.Bars(scenarios.Select(s => (s.Name, s.MeanYield)).ToList(), new ChartOptions { Width = 560, Height = 160 }, "#2e6b3b", "0.00");
        return new SimulationDemoResult(rainfed, irrigated, Chart(d => d.Lai, "LAI (m²/m²)"), Chart(d => d.SoilWaterMm, "Soil water / Air tanah (mm)"),
            Chart(d => d.BiomassKgHa / 1000, "Biomass / Biomassa (t/ha)"), scenarios, scenSvg);
    }
}

// ───────────────────────────── Irrigation ─────────────────────────────
public sealed record IrrigationDemoResult(IrrigationRecommendation Recommendation, string DepletionSvg, double SeasonIrrigationMm, int Events);

public static class IrrigationDemo
{
    public static IrrigationDemoResult Run(string cropId = "chili", int daysSincePlanting = 55, double areaHa = 1.2, int seed = 5)
    {
        var crop = CropCatalog.Default.Get(cropId);
        var today = new DateOnly(2026, 8, 10);
        var planting = today.AddDays(-daysSincePlanting);
        var gen = new SyntheticWeatherProvider(seed, annualRainMm: 1100);
        var hist = gen.Generate(DemoFarm.Center, planting, today.AddDays(-1));
        var fc = gen.Generate(DemoFarm.Center, today, today.AddDays(6));
        var advisor = new IrrigationAdvisor(crop, DemoFarm.Soil.Hydraulics(), DemoFarm.Station);
        var rec = advisor.Recommend(planting, hist, fc, areaHa);
        var auto = advisor.Simulate(planting, hist, autoIrrigate: true);
        var svg = SvgCharts.Line(
        [
            new ChartSeries("Depletion / Deplesi (mm)", rec.Balance.Select(b => b.DepletionMm).ToList(), "#b5762e", Area: true),
            new ChartSeries("RAW threshold / Ambang RAW", rec.Balance.Select(b => b.RawMm).ToList(), "#a5432f", Dashed: true),
            new ChartSeries("TAW", rec.Balance.Select(b => b.TawMm).ToList(), "#2f7da0"),
            new ChartSeries("Rain / Hujan (mm)", rec.Balance.Select(b => b.Rain).ToList(), "#7fb3c9"),
        ], new ChartOptions { XLabels = rec.Balance.Select(b => b.Date.ToString("dd MMM", CultureInfo.InvariantCulture)).ToList(), YMin = 0 });
        return new IrrigationDemoResult(rec, svg, Math.Round(auto.Sum(a => a.Irrigation), 0), auto.Count(a => a.Irrigation > 0));
    }
}

// ───────────────────────────── Drones ─────────────────────────────
public sealed record DroneDemoResult(MissionPlan Plan, string MissionSvg, byte[] PhotoPng, byte[] OverlayPng, WeedDetectionResult Weeds, string QgcPlanJson, double FootprintW, double FootprintH);

public static class DroneDemo
{
    public static DroneDemoResult Run(double altitude = 80, double front = 0.8, double side = 0.7)
    {
        var cam = CameraSpec.DjiMavic3M;
        var plan = MissionPlanner.PlanSurvey(DemoFarm.Field, cam, altitude, front, side);
        var svg = MapSvg.Render(DemoFarm.Field, [plan.Waypoints.Select(w => w.Position).ToList()], "#2f7da0", showVertices: true);
        var photo = RgbImage.SyntheticRowCrop(360, 240, 5, 30, 4);
        var weeds = WeedDetector.Detect(photo, 5);
        var overlay = new byte[photo.Width * photo.Height * 4];
        for (int i = 0; i < photo.Width * photo.Height; i++)
        {
            byte r = photo.Pixels[i * 3], g = photo.Pixels[i * 3 + 1], b = photo.Pixels[i * 3 + 2];
            if (weeds.WeedMask[i]) { r = 229; g = 72; b = 46; }
            else if (weeds.VegetationMask[i]) { r = (byte)(r / 2); g = (byte)Math.Min(255, g + 20); b = (byte)(b / 2); }
            else { r = (byte)(r * 0.6); g = (byte)(g * 0.6); b = (byte)(b * 0.6); }
            overlay[i * 4] = r; overlay[i * 4 + 1] = g; overlay[i * 4 + 2] = b; overlay[i * 4 + 3] = 255;
        }
        var (fw, fh) = cam.Footprint(altitude);
        return new DroneDemoResult(plan, svg, PngEncoder.FromRgb(photo.Pixels, photo.Width, photo.Height), PngEncoder.Encode(overlay, photo.Width, photo.Height),
            weeds, plan.ToQGroundControlPlan(DemoFarm.Center), Math.Round(fw, 1), Math.Round(fh, 1));
    }
}

/// <summary>Renders a field boundary and paths (geo) as SVG in a local metric frame.</summary>
public static class MapSvg
{
    public static string Render(GeoPolygon field, IReadOnlyList<IReadOnlyList<GeoCoordinate>> paths, string pathColor, bool showVertices = false, int width = 560, int height = 400)
        => RenderLocal(field.Outer.Select(p => GeoMath.ToLocal(field.Outer[0], p)).ToList(),
            paths.Select(pp => (IReadOnlyList<Point2>)pp.Select(p => GeoMath.ToLocal(field.Outer[0], p)).ToList()).ToList(), pathColor, showVertices, width, height);

    public static string RenderLocal(IReadOnlyList<Point2> ring, IReadOnlyList<IReadOnlyList<Point2>> paths, string pathColor, bool showVertices = false, int width = 560, int height = 400, IReadOnlyList<Point2>? secondary = null)
    {
        var all = ring.Concat(paths.SelectMany(p => p)).ToList();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double s = Math.Min((width - 40) / (maxX - minX), (height - 40) / (maxY - minY));
        string P(Point2 p) => string.Create(CultureInfo.InvariantCulture, $"{20 + (p.X - minX) * s:0.#},{height - 20 - (p.Y - minY) * s:0.#}");
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"100%\" role=\"img\">");
        sb.Append("<defs><pattern id=\"agtRows\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(-8)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"8\" stroke=\"#2e6b3b\" stroke-opacity=\"0.18\" stroke-width=\"3\"/></pattern></defs>");
        sb.Append($"<polygon points=\"{string.Join(' ', ring.Select(P))}\" fill=\"#cfe0c3\" stroke=\"#2e6b3b\" stroke-width=\"2\"/>");
        sb.Append($"<polygon points=\"{string.Join(' ', ring.Select(P))}\" fill=\"url(#agtRows)\"/>");
        foreach (var path in paths)
        {
            if (path.Count < 2) continue;
            sb.Append($"<polyline points=\"{string.Join(' ', path.Select(P))}\" fill=\"none\" stroke=\"{pathColor}\" stroke-width=\"1.8\" stroke-linejoin=\"round\"/>");
            sb.Append($"<circle cx=\"{P(path[0]).Split(',')[0]}\" cy=\"{P(path[0]).Split(',')[1]}\" r=\"5\" fill=\"#2e6b3b\"/>");
            sb.Append($"<circle cx=\"{P(path[^1]).Split(',')[0]}\" cy=\"{P(path[^1]).Split(',')[1]}\" r=\"5\" fill=\"#a5432f\"/>");
        }
        if (secondary is { Count: > 1 })
            sb.Append($"<polyline points=\"{string.Join(' ', secondary.Select(P))}\" fill=\"none\" stroke=\"#b5762e\" stroke-width=\"1.2\" stroke-dasharray=\"3 3\"/>");
        if (showVertices) foreach (var v in ring) { var xy = P(v).Split(','); sb.Append($"<rect x=\"{xy[0]}\" y=\"{xy[1]}\" width=\"6\" height=\"6\" transform=\"translate(-3,-3)\" fill=\"#fff\" stroke=\"#1c2620\"/>"); }
        return sb.Append("</svg>").ToString();
    }
}

// ───────────────────────────── Robotics ─────────────────────────────
public sealed record RoboticsDemoResult(string Svg, double PathLengthM, double RmsCrossTrackM, int Passes, double DurationMin, HarvestPlan Harvest);

public static class RoboticsDemo
{
    public static RoboticsDemoResult Run(double implementWidth = 12, double lookahead = 4, double speed = 2.5)
    {
        var path = PathPlanner.FieldCoverage(DemoFarm.Field, implementWidth, 8, out var origin);
        var (traj, rms) = PurePursuitController.Simulate(path, new TractorModel(), speed, lookahead);
        var ring = DemoFarm.Field.Outer.Select(p => GeoMath.ToLocal(origin, p)).ToList();
        var svg = MapSvg.RenderLocal(ring, [path], "#2f7da0", secondary: traj.Select(t => t.Position).ToList());
        double len = 0; for (int i = 1; i < path.Count; i++) len += path[i - 1].DistanceTo(path[i]);
        var rng = new Random(2);
        var fruits = Enumerable.Range(0, 40).Select(i => new FruitTarget(i, new Point2(rng.NextDouble() * 30, rng.NextDouble() * 8), 0.6 + rng.NextDouble(), rng.NextDouble())).ToList();
        return new RoboticsDemoResult(svg, Math.Round(len, 0), Math.Round(rms, 3), path.Count / 2, Math.Round(len / speed / 60, 1), HarvestPlanner.Plan(fruits, new Point2(0, 0)));
    }
}

// ───────────────────────────── Digital twin ─────────────────────────────
/// <summary>Live digital-twin session (stateful; one per UI view).</summary>
public sealed class TwinSession
{
    private readonly InMemoryTransport _transport = new();
    private readonly SimulatedSensorNetwork[] _nets;
    private readonly Random _rng = new(4);
    private DateTimeOffset _clock = new(2026, 9, 14, 5, 0, 0, TimeSpan.FromHours(7));

    public TwinSession()
    {
        var farm = new Farm("Kebun Sukamaju", DemoFarm.Center, "Kelompok Tani Sukamaju");
        string[] crops = ["rice", "chili", "maize", "shallot"];
        string[] names = ["Sawah Utara", "Cabai Timur", "Jagung Selatan", "Bawang Barat"];
        for (int i = 0; i < 4; i++)
        {
            var f = farm.AddField(names[i], GeoPolygon.Rectangle(GeoMath.Destination(DemoFarm.Center, 90 * i, 160), 120, 90), crops[i]);
            f.PlantingDate = DateOnly.FromDateTime(_clock.Date).AddDays(-(18 + i * 17));
        }
        Twin = new FarmTwin(farm, _transport, clock: new FixedClock(() => _clock));
        double[] moisture = [38, 27, 31, 35];
        double[] ndvi = [0.72, 0.41, 0.63, 0.55];
        var today = DateOnly.FromDateTime(_clock.Date);
        _nets = farm.Fields.Select((f, i) =>
        {
            Twin.BindDevice($"ws-{i}", f).BindDevice($"soil-{i}", f);
            Twin.SetNdvi(f, ndvi[i]);
            Twin.SetSoil(f, DemoFarm.Soil);
            // Catch the crop lifecycle up from planting to today with typical lowland temperatures.
            var lc = Twin.State(f).Lifecycle!;
            while (lc.CurrentDate < today) lc.Advance(23.5, 32);
            return new SimulatedSensorNetwork(_transport, 10 + i, moisture[i], 0.1 + i * 0.03).AddWeatherStation($"ws-{i}").AddSoilProbe($"soil-{i}");
        }).ToArray();
        Twin.StartAsync().GetAwaiter().GetResult();
        // warm up a few hours of history
        for (int k = 0; k < 6; k++) Tick();
    }

    public FarmTwin Twin { get; }
    public DateTimeOffset Now => _clock;
    public List<(DateTimeOffset T, double[] SoilMoisture)> History { get; } = [];

    /// <summary>Advances simulated time by one hour.</summary>
    public FarmSnapshot Tick()
    {
        _clock = _clock.AddHours(1);
        double rain = _rng.NextDouble() < 0.08 ? _rng.NextDouble() * 8 : 0;
        foreach (var n in _nets) n.TickAsync(_clock, rain).GetAwaiter().GetResult();
        if (_clock.Hour == 0) Twin.AdvanceDay(new DailyWeather(DateOnly.FromDateTime(_clock.Date), 23, 32, rain));
        Twin.Evaluate();
        var snap = Twin.Snapshot();
        History.Add((_clock, snap.Fields.Select(f => f.SoilMoisture ?? double.NaN).ToArray()));
        if (History.Count > 72) History.RemoveAt(0);
        return snap;
    }

    public string IsometricSvg()
    {
        var snap = Twin.Snapshot();
        var tiles = snap.Fields.Select((f, i) => new FarmTile(i % 2, i / 2, f.Name, f.Ndvi ?? 0.5, Math.Min(1, (f.Progress ?? 0.2) * 1.4 + 0.1),
            HasSensor: true, Alert: f.ActiveAlerts > 0)).ToList();
        return IsometricFarm.Render(tiles, 92, 30, "Farm digital twin");
    }

    public string MoistureSvg() => SvgCharts.Line(
        Twin.Snapshot().Fields.Select((f, i) => new ChartSeries(f.Name, History.Select(h => h.SoilMoisture.ElementAtOrDefault(i)).ToList())).ToList(),
        new ChartOptions { Width = 1100, Height = 260, YLabel = "Soil moisture / Kelembapan tanah (%VWC)", XLabels = History.Select(h => h.T.ToString("HH:mm", CultureInfo.InvariantCulture)).ToList() });

    private sealed class FixedClock(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now().ToUniversalTime();
    }
}

// ───────────────────────────── Livestock ─────────────────────────────
public sealed record LivestockDemoResult(string ThiSvg, double PeakThi, HeatStressLevel PeakLevel, double MilkLossKg, IReadOnlyList<(string TagId, string Event, DateOnly Date)> Calendar,
    IReadOnlyList<HealthAlert> Alerts, double GirthWeight);

public static class LivestockDemo
{
    public static LivestockDemoResult Run(double maxTemp = 34, double minRh = 55)
    {
        var hours = Enumerable.Range(0, 24).ToArray();
        var temps = hours.Select(h => 25 + (maxTemp - 25) * Math.Max(0, Math.Sin((h - 7) / 14.0 * Math.PI))).ToArray();
        var rh = hours.Select(h => 92 - (92 - minRh) * Math.Max(0, Math.Sin((h - 7) / 14.0 * Math.PI))).ToArray();
        var thi = hours.Select(h => Math.Round(LivestockMath.Thi(temps[h], rh[h]), 1)).ToList();
        var svg = SvgCharts.Line(
        [
            new ChartSeries("THI", thi, "#a5432f", Area: true),
            new ChartSeries("Mild stress / Cekaman ringan (72)", hours.Select(_ => 72.0).ToList(), "#b5762e", Dashed: true),
            new ChartSeries("Moderate / Sedang (79)", hours.Select(_ => 79.0).ToList(), "#a5432f", Dashed: true),
        ], new ChartOptions { XLabels = hours.Select(h => $"{h:00}:00").ToList(), YMin = 60, YMax = 95 });
        var today = new DateOnly(2026, 9, 14);
        var herd = new Herd();
        var a = herd.Add(new Animal("ID-0231", Species.DairyCattle, Sex.Female, new(2022, 3, 1), "Friesian Holstein"));
        a.Breeding.Add(new BreedingEvent(today.AddDays(-120), "AI", "Sire-FH77"));
        a.Breeding.Add(new BreedingEvent(today.AddDays(-80), "PregCheck", Pregnant: true));
        var b = herd.Add(new Animal("ID-0418", Species.BeefCattle, Sex.Female, new(2021, 6, 9), "Bali"));
        b.Breeding.Add(new BreedingEvent(today.AddDays(-12), "AI", "Sire-BL09"));
        var c = herd.Add(new Animal("KB-077", Species.Goat, Sex.Female, new(2024, 1, 20), "Etawa"));
        c.Breeding.Add(new BreedingEvent(today.AddDays(-50), "Natural", "Buck-03"));
        c.Breeding.Add(new BreedingEvent(today.AddDays(-15), "PregCheck", Pregnant: true));
        var alerts = herd.CheckVitals(new VitalSigns("ID-0231", DateTimeOffset.Now, 40.4, 88, 72, 280)).ToList();
        double peak = thi.Max();
        return new LivestockDemoResult(svg, peak, LivestockMath.HeatStress(peak), Math.Round(LivestockMath.MilkLossKg(peak), 1), herd.ReproductionCalendar(today), alerts,
            LivestockMath.WeightFromHeartGirth(165));
    }
}

// ───────────────────────────── Aquaculture ─────────────────────────────
public sealed record AquaDemoResult(WaterQualityReport Water, FeedingPlan Feed, IReadOnlyList<GrowthProjection> Growth, string GrowthSvg, int RecommendedStocking, Pond Pond);

public static class AquaDemo
{
    public static AquaDemoResult Run(double tempC = 29, double doMgL = 4.2, double ph = 8.1, double tan = 1.1)
    {
        var pond = new Pond("K-03", AquaSpecies.Tilapia, 800, 1.2) { StockedCount = 12_000, AvgWeightG = 45, SurvivalRate = 0.92 };
        var w = new WaterSample(DateTimeOffset.Now, tempC, doMgL, ph, tan, TransparencyCm: 32);
        var growth = FeedOptimizer.Project(pond, tempC, 500);
        var svg = SvgCharts.Line(
        [
            new ChartSeries("Avg weight / Bobot rata-rata (g)", growth.Select(g => g.AvgWeightG).ToList(), "#2f7da0", Area: true),
            new ChartSeries("Cumulative feed / Pakan kumulatif (×10 kg)", growth.Select(g => g.CumulativeFeedKg / 10).ToList(), "#b5762e"),
        ], new ChartOptions { XLabels = growth.Select(g => $"D{g.Day}").ToList(), YMin = 0 });
        return new AquaDemoResult(WaterQuality.Assess(w, pond.Species), FeedOptimizer.Plan(pond, w), growth, svg, FeedOptimizer.RecommendedStocking(pond, 500), pond);
    }
}

// ───────────────────────────── Market ─────────────────────────────
public sealed record MarketDemoResult(IReadOnlyList<Trade> Trades, IReadOnlyList<Order> OpenBids, IReadOnlyList<Order> OpenAsks, PriceInsight Insight, string PriceSvg, double FairPrice);

public static class MarketDemo
{
    public static MarketDemoResult Run(double transportCost = 2.5)
    {
        var book = new OrderBook(transportCost);
        var garut = new GeoCoordinate(-7.2, 107.9); var brebes = new GeoCoordinate(-6.87, 109.04); var jakarta = new GeoCoordinate(-6.17, 106.82);
        var t0 = new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.FromHours(7));
        book.Submit(Side.Sell, "KT Sri Rejeki (Garut)", "chili", 800, 31_000, location: garut, time: t0);
        book.Submit(Side.Sell, "Gapoktan Brebes", "chili", 1_200, 29_500, location: brebes, time: t0.AddMinutes(4));
        book.Submit(Side.Sell, "KT Makmur (Garut)", "chili", 400, 33_000, location: garut, time: t0.AddMinutes(9));
        book.Submit(Side.Buy, "Pasar Induk Kramat Jati", "chili", 1_500, 30_500, location: jakarta, time: t0.AddMinutes(15));
        book.Submit(Side.Buy, "Restoran Padang Sederhana", "chili", 300, 36_000, location: jakarta, time: t0.AddMinutes(21));
        book.Submit(Side.Buy, "Pabrik Saus Bandung", "chili", 900, 29_000, location: new GeoCoordinate(-6.92, 107.61), time: t0.AddMinutes(30));
        var series = PriceIntelligence.SyntheticSeries(new DateOnly(2026, 3, 1), 180, 32_000);
        var prices = series.Select(s => s.Price).ToList();
        var insight = PriceIntelligence.Analyze(prices, 21);
        var hist = prices.TakeLast(60).ToList();
        var svg = SvgCharts.Line(
        [
            new ChartSeries("Price / Harga (Rp/kg)", hist.Concat(Enumerable.Repeat(double.NaN, insight.Forecast.Count)).ToList(), "#2e6b3b"),
            new ChartSeries("Forecast / Proyeksi", Enumerable.Repeat(double.NaN, hist.Count - 1).Append(hist[^1]).Concat(insight.Forecast).ToList(), "#b5762e", Dashed: true),
            new ChartSeries("MA-7", TimeSeriesExtensions.MovingAverage(prices.ToArray(), 7).TakeLast(60).ToList(), "#2f7da0"),
        ], new ChartOptions { Height = 240 });
        return new MarketDemoResult(book.Trades, book.Bids("chili").ToList(), book.Asks("chili").ToList(), insight, svg,
            PriceIntelligence.FairFarmGatePrice(totalCost: 165_000_000, yieldKg: 9_000, marginPct: 25, marketPrice: insight.Last));
    }
}

// ───────────────────────────── Genomics ─────────────────────────────
public sealed record GenomicsDemoResult(double PredictionAccuracy, int Markers, int Individuals, IReadOnlyList<CrossRecommendation> Crosses, string GebvSvg,
    (double PerCycle, double PerYear) Response, IReadOnlyList<OpenReadingFrame> Orfs, double GcContent);

public static class GenomicsDemo
{
    public static GenomicsDemoResult Run(double h2 = 0.5, int seed = 5)
    {
        var (geno, y, tbv) = BreedingAdvisor.SyntheticPopulation(140, 220, 18, h2, seed);
        var qc = geno.Filter(0.05);
        int nTrain = 100;
        var trainIdx = Enumerable.Range(0, nTrain).ToArray();
        var train = Subset(qc, trainIdx);
        var (gebv, _) = BreedingAdvisor.GenomicPrediction(train, trainIdx.Select(i => y[i]).ToArray(), h2, qc);
        var testIdx = Enumerable.Range(nTrain, qc.Individuals.Count - nTrain).ToArray();
        double acc = Stats.Correlation(testIdx.Select(i => gebv[i]).ToArray(), testIdx.Select(i => tbv[i]).ToArray());
        var top = Enumerable.Range(0, qc.Individuals.Count).OrderByDescending(i => gebv[i]).Take(12).ToArray();
        var g = qc.GenomicRelationship();
        var rel = new double[top.Length, top.Length];
        for (int i = 0; i < top.Length; i++) for (int j = 0; j < top.Length; j++) rel[i, j] = g[top[i], top[j]];
        var crosses = BreedingAdvisor.PlanCrosses(top.Select(i => qc.Individuals[i]).ToList(), top.Select(i => gebv[i]).ToArray(), rel, 8);
        var svg = SvgCharts.Bars(top.Select(i => (qc.Individuals[i], Math.Round(gebv[i], 2))).ToList(), new ChartOptions { Width = 520, Height = 200 }, "#2e6b3b", "0.00");
        var seq = new NucleotideSequence("ATGGCTAGCAAAGGAGAAGAACTTTTCACTGGAGTTGTCCCAATTCTTGTTGAATTAGATGGTGATGTTAATGGGCACAAATTTTCTGTCAGTGGAGAGGGTGAAGGTGATGCAACATACGGAAAACTTACCCTTAAATTTATTTGCACTACTGGAAAACTACCTGTTCCATGGCCAACACTTGTCACTACTTTCGGTTATGGTGTTCAATGCTTTGCGAGATACCCAGATCATATGAAACAGCATGACTTTTTCAAGAGTGCCATGCCCGAAGGTTATGTACAGGAAAGAACTATATTTTTCAAAGATGACGGGAACTACAAGACACGTGCTGAAGTCAAGTTTGAAGGTGATACCCTTGTTAATAGAATCGAGTTAAAAGGTATTGATTTTAAAGAAGATGGAAACATTCTTGGACACAAATTGGAATACAACTATAACTCACACAATGTATACATCATGGCAGACAAACAAAAGAATGGAATCAAAGTTAACTTCAAAATTAGACACAACATTGAAGATGGAAGCGTTCAACTAGCAGACCATTATCAACAAAATACTCCAATTGGCGATGGCCCTGTCCTTTTACCAGACAACCATTACCTGTCCACACAATCTGCCCTTTCGAAAGATCCCAACGAAAAGAGAGACCACATGGTCCTTCTTGAGTTTGTAACAGCTGCTGGGATTACACATGGCATGGATGAACTATACAAATAA", id: "gfp-marker");
        return new GenomicsDemoResult(Math.Round(acc, 3), qc.Markers.Count, qc.Individuals.Count, crosses, svg,
            BreedingAdvisor.ResponseToSelection(h2, Stats.StdDev(y), 0.1, 1.5), seq.FindOrfs(100), Math.Round(seq.GcContent, 1));
    }

    private static GenotypeMatrix Subset(GenotypeMatrix g, int[] rows)
    {
        var d = new sbyte[rows.Length, g.Markers.Count];
        for (int i = 0; i < rows.Length; i++) for (int m = 0; m < g.Markers.Count; m++) d[i, m] = g.Dosage[rows[i], m];
        return new GenotypeMatrix(rows.Select(r => g.Individuals[r]).ToList(), g.Markers, d);
    }
}
