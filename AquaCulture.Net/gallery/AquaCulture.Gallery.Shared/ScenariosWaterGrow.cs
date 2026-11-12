using System.Diagnostics;
using System.Globalization;
using System.Text;
using AquaCulture.AI;
using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Feeding;
using AquaCulture.Forecasting;
using AquaCulture.Growth;
using AquaCulture.Hydrodynamic;
using AquaCulture.IoT;
using AquaCulture.Native;
using AquaCulture.Visualization;
using AquaCulture.WaterQuality;

namespace AquaCulture.Gallery.Shared;

// ───────────────────────────── Water quality ─────────────────────────────
public sealed record WaterDemoResult(WaterQualityAssessment Assessment, IReadOnlyList<Recommendation> Actions, string GaugeSvg, string DaySvg,
    IReadOnlyList<WaterAlert> Alerts, double Nh3, double DoSat);

public static class WaterDemo
{
    public static WaterDemoResult Run(string speciesId = "vaname", double temp = 30.5, double dox = 3.8, double ph = 8.4, double tan = 1.6,
        double salinity = 18, double nitrite = 0.6, double alkalinity = 95, double orp = 180, double turbidity = 45)
    {
        var s = SpeciesCatalog.Get(speciesId);
        var sample = new WaterSample
        {
            PondId = "K-01", Timestamp = DemoFarm.Now, TemperatureC = temp, DissolvedOxygen = dox, Ph = ph, Ammonia = tan, SalinityPpt = s.Habitat == Habitat.Freshwater ? 0.3 : salinity,
            Nitrite = nitrite, Alkalinity = alkalinity, OrpMv = orp, TurbidityNtu = turbidity, Tss = 55, Nitrate = 12, Phosphate = 0.4, Bod = 9, Cod = 35, Tds = s.Habitat == Habitat.Freshwater ? 350 : null,
        };
        var a = WaterQualityIndex.Assess(sample, s);
        var pond = DemoFarm.Farm.Ponds[0];
        var cyc = DemoFarm.Farm.ActiveCycle(pond.Id)!;
        var actions = RecommendationEngine.Recommend(a, PondContext.From(pond, cyc.EstimatedBiomassKg));
        var gauge = SvgCharts.Gauge(Math.Round(a.Wqi), 100, "WQI", "");

        // the last 24 h of this pond from the simulated buoy, ending at the chosen DO
        var sim = new SimulatedSensorTransport([new SimulatedPond("K-01", "buoy-1", temp - 0.6, dox + 1.4, 2.2, ph - 0.25, salinity, tan)], 5) { DropoutProbability = 0 };
        var engine = AlertEngine.ForSpecies(s);
        var doPts = new List<(double, double)>(); var phPts = new List<(double, double)>();
        var alerts = new List<WaterAlert>();
        var snapshot = new WaterSample { PondId = "K-01" };
        for (int i = 0; i <= 48; i++)
        {
            var t = DemoFarm.Now.AddHours(-24 + i * 0.5);
            foreach (var r in sim.Emit(t))
                if ((int)r.Kind <= (int)SensorKind.Turbidity) snapshot = snapshot.With((WaterParameter)(int)r.Kind, r.Value) with { Timestamp = t };
            alerts.AddRange(engine.Evaluate(snapshot));
            doPts.Add((i * 0.5 - 24, snapshot.DissolvedOxygen ?? double.NaN));
            phPts.Add((i * 0.5 - 24, snapshot.Ph ?? double.NaN));
        }
        var day = SvgCharts.Line([new Series("DO mg/L", doPts, "#12a4b6", Area: true), new Series("pH", phPts, "#d9a13b")],
            new ChartOptions { Height = 250, XFormat = h => $"{(h < 0 ? "−" : "")}{Math.Abs(h):0} h", References = [new Reference(s.DoOptimal, $"DO opt {s.DoOptimal}", "#2f8f6a"), new Reference(s.DoCritical, $"DO crit {s.DoCritical}")] });
        return new WaterDemoResult(a, actions, gauge, day, alerts.Where(x => !x.Cleared).TakeLast(6).ToList(), a.UnionizedAmmonia ?? double.NaN, a.DoSaturationPct ?? double.NaN);
    }
}

// ───────────────────────────── Oxygen budget ─────────────────────────────
public sealed record OxygenDemoResult(OxygenBudgetResult Off, OxygenBudgetResult On, DoForecast Forecast, string ChartSvg, string BudgetSvg, double CostRp);

public static class OxygenDemo
{
    public static OxygenDemoResult Run(double biomassKg = 3000, double chlorophyll = 120, double aeratorKw = 4.4, double setPoint = 4.5, double wet = 1.0)
    {
        var pond = DemoFarm.Farm.Ponds[0];
        var weather = DemoFarm.Weather(2, 21, wet).Where(w => w.Time >= DemoFarm.Today.ToDateTime(new TimeOnly(12, 0))).Take(36).ToList();
        var p = new OxygenBudgetParameters { VolumeM3 = pond.VolumeM3, DepthM = pond.DepthM, BiomassKg = biomassKg, MeanWeightG = 12, Group = SpeciesGroup.Shrimp,
            ChlorophyllUgL = chlorophyll, SalinityPpt = 18, AeratorKw = aeratorKw };
        var off = PondOxygenBudget.Simulate(p, weather, 6.8);
        var on = PondOxygenBudget.Simulate(p, weather, 6.8, PondOxygenBudget.SetPoint(setPoint));
        var recent = new TimeSeries("DO", "mg/L");
        foreach (var h in off.Hours.Where(h => h.Time.Hour >= 17 && h.Time.Hour <= 21 && h.Time.Date == weather[0].Time.Date)) recent.Add(h.Time, h.Do);
        var now = recent.Last?.Time ?? weather[0].Time.AddHours(8);
        var f = DoCrashForecaster.Forecast(recent, DemoFarm.Species, p, weather, now);
        double X(DateTime t) => (t - weather[0].Time).TotalHours + 12;
        var chart = SvgCharts.Line(
            [
                new Series("No aeration / tanpa aerasi", off.Hours.Select(h => (X(h.Time), h.Do)).ToList(), "#e0614a"),
                new Series($"Set-point {setPoint} mg/L", on.Hours.Select(h => (X(h.Time), h.Do)).ToList(), "#12a4b6"),
                new Series("Saturation / jenuh", off.Hours.Select(h => (X(h.Time), h.Saturation)).ToList(), "#5b7279", Dashed: true),
            ],
            new ChartOptions { Height = 280, YMin = 0, XFormat = h => $"{(int)h % 24:00}:00", References = [new Reference(DemoFarm.Species.DoCritical, "critical / kritis")] });
        var night = off.Hours.Where(h => h.Time.Hour is >= 20 or < 6).ToList();
        var budget = SvgCharts.Bars(
            [
                ("Plankton+stock+SOD", -Math.Round(night.Average(h => h.Respiration), 2)), ("Reaeration", Math.Round(night.Average(h => h.Reaeration), 2)),
                ("Photosynthesis (day)", Math.Round(off.Hours.Where(h => h.Time.Hour is >= 9 and <= 15).Average(h => h.Photosynthesis), 2)),
                ("Aeration (night)", Math.Round(on.Hours.Where(h => h.Time.Hour is >= 20 or < 6).Average(h => h.Aeration), 2)),
            ], new ChartOptions { Height = 230, YFormat = "0.00", Title = "mg O₂ L⁻¹ h⁻¹" }, ["#e0614a", "#7cc6a4", "#3e8e5e", "#12a4b6"]);
        return new OxygenDemoResult(off, on, f, chart, budget, on.AeratorKwh * 1_500);
    }
}

// ───────────────────────────── Hydrodynamics ─────────────────────────────
public sealed record HydroDemoResult(byte[] FieldPng, string FlowSvg, int Nx, int Ny, double Mean, double Min, double Max, double LowAreaPct, KernelBackend Backend,
    double Milliseconds, int Steps, string HistorySvg);

public static class HydroDemo
{
    public static HydroDemoResult Run(int wheels = 2, double thrust = 0.25, double hours = 3, double initialDo = 3.2)
    {
        var g = new Grid2D(80, 64, 0.7).WithDikes(2);
        var wl = new List<(double X, double Y, double Thrust)>();
        double w = g.Nx * g.Dx, h = g.Ny * g.Dx;
        (double, double)[] spots = [(0.2, 0.5), (0.8, 0.5), (0.5, 0.2), (0.5, 0.8)];
        for (int i = 0; i < Math.Clamp(wheels, 0, 4); i++) wl.Add((spots[i].Item1 * w, spots[i].Item2 * h, thrust));
        FlowFields.PaddleWheelCirculation(g, wheels == 0 ? 0.01 : 0.06 + 0.05 * wheels, wl);
        double cs = WaterChemistry.OxygenSaturation(30, 18);
        // surface reaeration K_L ≈ 1 m/day over 1.3 m depth; stock + plankton + sediment respiration 0.45 mg/L/h
        var model = new ScalarTransport(g) { Diffusivity = 0.02, Decay = 1.0 / 86400 / 1.3, Equilibrium = cs, Background = -0.45 / 3600 };
        // each 2.2 kW paddle wheel transfers 2.2 kW × 1.8 kg/kWh × 0.6 (field efficiency) of oxygen into the cells around it
        double gPerS = 2.2 * 1.8 * 0.6 * 1000 / 3600 * thrust / 0.25;
        foreach (var (x, y, _) in wl)
            model.Sources.Add(new ScalarSource((int)(x / g.Dx), (int)(y / g.Dx), gPerS / (g.Dx * g.Dx * 1.3), 3));
        var init = Enumerable.Repeat((float)initialDo, g.Count).ToArray();
        var sw = Stopwatch.StartNew();
        var r = model.Run(init, hours * 3600, 900);
        sw.Stop();
        var masked = r.Field.Select((v, i) => g.Wet[i] == 0 ? float.NaN : v).ToArray();
        var png = PngEncoder.Raster(masked, g.Nx, g.Ny, ColorRamp.Oxygen.WithRange(0, 8), 6, flipY: true);
        double low = 100.0 * masked.Count(v => !float.IsNaN(v) && v < DemoFarm.Species.DoOptimal) / masked.Count(v => !float.IsNaN(v));

        // flow arrows overlay (same pixel space as the PNG)
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {g.Nx * 6} {g.Ny * 6}\">");
        double maxS = Math.Max(1e-6, g.MaxSpeed);
        for (int j = 4; j < g.Ny - 2; j += 5)
            for (int i = 4; i < g.Nx - 2; i += 5)
            {
                int k = g.Index(i, j); if (g.Wet[k] == 0) continue;
                double u = g.U[k] / maxS, v = g.V[k] / maxS, x0 = i * 6 + 3, y0 = (g.Ny - 1 - j) * 6 + 3, x1 = x0 + u * 14, y1 = y0 - v * 14;
                sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{x0:0.#}\" y1=\"{y0:0.#}\" x2=\"{x1:0.#}\" y2=\"{y1:0.#}\" stroke=\"#ffffff\" stroke-opacity=\"0.75\" stroke-width=\"1.4\"/><circle cx=\"{x1:0.#}\" cy=\"{y1:0.#}\" r=\"1.6\" fill=\"#ffffff\" fill-opacity=\"0.85\"/>");
            }
        foreach (var (x, y, _) in wl)
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{x / g.Dx * 6 - 10:0.#}\" y=\"{(g.Ny - y / g.Dx) * 6 - 5:0.#}\" width=\"20\" height=\"10\" rx=\"2\" fill=\"#d9a13b\" stroke=\"#0b2e36\"/>");
        sb.Append("</svg>");
        var hist = SvgCharts.Line([new Series("mean", r.History.Select(x => (x.Seconds / 3600, x.Mean)).ToList(), "#12a4b6"), new Series("min", r.History.Select(x => (x.Seconds / 3600, x.Min)).ToList(), "#e0614a")],
            new ChartOptions { Height = 200, YMin = 0, XFormat = x => $"{x:0.#} h" });
        return new HydroDemoResult(png, sb.ToString(), g.Nx, g.Ny, r.Mean, r.Min, r.Max, low, r.Backend, sw.Elapsed.TotalMilliseconds, r.Steps, hist);
    }
}

// ───────────────────────────── Growth ─────────────────────────────
public sealed record GrowthDemoResult(GrowthTrajectory Trajectory, HarvestPrediction AtTarget, HarvestPrediction Optimal, HarvestForecast MonteCarlo,
    string WeightSvg, string BiomassSvg, string HistogramSvg, SpeciesProfile Species);

public static class GrowthDemo
{
    public static GrowthDemoResult Run(string speciesId = "vaname", double temp = 29, double dawnDo = 5.0, double densityPerM2 = 125, double feedRatio = 1.0)
    {
        var s = SpeciesCatalog.Get(speciesId);
        double area = 2000, depth = 1.3;
        long count = (long)(densityPerM2 * area * (s.Group == SpeciesGroup.Shrimp ? 1 : 0.04));
        var sim = new GrowthSimulator(s);
        var env = new DailyEnvironment(temp, dawnDo, s.Nh3Max * 0.4, s.Habitat == Habitat.Freshwater ? double.NaN : (s.Salinity.OptLow + s.Salinity.OptHigh) / 2, feedRatio);
        var start = new StockState(s, DemoFarm.Today, s.StockingWeightG, count, area * depth);
        var t = sim.Simulate(start, (int)(s.TypicalCycleDays * 1.35), env);
        var econ = new HarvestEconomics(s.Group == SpeciesGroup.Shrimp ? 17_500 : 12_000, 4.4 * 18 * 1_500 + 85_000);
        var at = HarvestPredictor.AtWeight(t, s.MarketWeightG, area, econ);
        var best = HarvestPredictor.Optimal(t, area, econ);
        var mc = HarvestForecaster.Forecast(s, DemoFarm.Today, s.StockingWeightG, count, s.MarketWeightG, Enumerable.Repeat(temp, t.Days.Count).ToArray(), 500, 1.2, 0.08, 7);
        var wSvg = SvgCharts.Line([new Series("ABW g", t.Days.Select(d => ((double)d.Day, d.AbwG)).ToList(), "#3e8e5e", Area: true)],
            new ChartOptions { Height = 250, XFormat = x => $"DOC {x:0}", References = [new Reference(s.MarketWeightG, $"market {s.MarketWeightG} g", "#d9a13b")] });
        var bSvg = SvgCharts.Line(
            [
                new Series("Biomass kg", t.Days.Select(d => ((double)d.Day, d.BiomassKg)).ToList(), "#12a4b6"),
                new Series("Cum. feed kg", t.Days.Select(d => ((double)d.Day, d.CumulativeFeedKg)).ToList(), "#d9a13b", Dashed: true),
            ], new ChartOptions { Height = 250, YFormat = "N0", XFormat = x => $"DOC {x:0}", References = [new Reference(best.BiomassKg, $"optimal DOC {best.DayOfCulture}", "#2f8f6a")] });
        // histogram of final weights
        var fw = mc.FinalWeights.Take(Math.Min(mc.FinalWeights.Length, 500)).ToArray();
        int bins = 14; double lo = fw.Min(), hi = fw.Max(), bw = (hi - lo) / bins;
        var hist = Enumerable.Range(0, bins).Select(b => ($"{lo + (b + 0.5) * bw:0.#}", (double)fw.Count(x => x >= lo + b * bw && (x < lo + (b + 1) * bw || b == bins - 1)))).ToList();
        var hSvg = SvgCharts.Bars(hist, new ChartOptions { Height = 210, YFormat = "0", Title = $"g at DOC {t.Days.Count}" }, ["#6c5ca8"]);
        return new GrowthDemoResult(t, at, best, mc, wSvg, bSvg, hSvg, s);
    }
}

// ───────────────────────────── Feeding ─────────────────────────────
public sealed record FeedingDemoResult(FeedingPlan Plan, IReadOnlyList<(FeedProduct Feed, double Fcr, double CostPerKgGain)> Ranking,
    (double FractionHigh, double FractionLow, double PricePerKg) Blend, FeedCostAnalysis Cost, double CycleFcr, FeederCommand Auto, string FcrSvg, SpeciesProfile Species);

public static class FeedingDemo
{
    public static FeedingDemoResult Run(string speciesId = "vaname", double abw = 12, double population = 220_000, double temp = 29.5, double dawnDo = 4.3, double leftover = 12)
    {
        var s = SpeciesCatalog.Get(speciesId);
        double nh3 = s.Nh3Max * 0.6;
        var plan = FeedingCalculator.Plan(s, abw, population, temp, dawnDo, nh3);
        var rank = FeedOptimizer.Rank(s, abw, FeedCatalog.Products);
        var feeds = FeedCatalog.Products.Where(p => p.Group == s.Group).OrderByDescending(p => p.ProteinPct).ToList();
        var blend = FeedOptimizer.Blend(feeds.First(), feeds.Last(), s.ProteinRequirementPct);
        var cycle = DemoFarm.Farm.Cycles[0];
        var cost = FeedOptimizer.Analyze(cycle, 17_500);
        var af = new AutoFeedingAdvisor(s);
        var cmd = af.Next(plan.Meals[0], new FeedingObservation(leftover, 4), dawnDo, temp, nh3);
        // cumulative FCR across the six demo ponds
        var bars = DemoFarm.Farm.Cycles.Select(c => (c.PondId, Math.Round(Fcr.ForCycle(c), 2))).ToList();
        var fcrSvg = SvgCharts.Bars(bars, new ChartOptions { Height = 220, YFormat = "0.00", YMin = 0, Title = "FCR" }, bars.Select(b => b.Item2 <= s.TypicalFcr ? "#3e8e5e" : "#d9a13b").ToList());
        return new FeedingDemoResult(plan, rank, blend, cost, Fcr.ForCycle(cycle), cmd, fcrSvg, s);
    }
}

// ───────────────────────────── AI ─────────────────────────────
public sealed record AiDemoResult(double GrowthR2, double GrowthRmse, GrowthPrediction Prediction, string ImportanceSvg, double MortalityAuc, MortalityRisk Risk,
    FeedDecision Decision, string PolicySvg, double MlNetR2, string Species);

public static class AiDemo
{
    private static readonly Dictionary<string, (GrowthPredictor G, MortalityPredictor M, FeedOptimizationPolicy P, double MlNet)> Cache = [];
    private static readonly object Gate = new();

    public static AiDemoResult Run(string speciesId = "vaname", double abw = 12, double temp = 30, double dawnDo = 3.4, double nh3Ratio = 1.3, double density = 0.8)
    {
        var s = SpeciesCatalog.Get(speciesId);
        (GrowthPredictor G, MortalityPredictor M, FeedOptimizationPolicy P, double MlNet) m;
        lock (Gate)
        {
            if (!Cache.TryGetValue(s.Id, out m))
            {
                var g = new GrowthPredictor(s, 1200);
                var ml = new MlNetGrowthModel().Train(MlNetGrowthModel.FromDataset(GrowthPredictor.SyntheticDataset(s, 1200, 17)));
                m = (g, new MortalityPredictor(s, 1500), new FeedOptimizationPolicy(s, s.Group == SpeciesGroup.Shrimp ? 17_500 : 12_000, episodes: 2500), ml.RSquared);
                Cache[s.Id] = m;
            }
        }
        var state = new PondState(abw, temp, dawnDo, s.Nh3Max * nh3Ratio, density, 1, (s.Salinity.OptLow + s.Salinity.OptHigh) / 2, 60);
        var pred = m.G.Predict(state);
        var imp = SvgCharts.Bars(pred.Drivers.Select(d => (d.Feature, Math.Round(d.Importance, 3))).ToList(), new ChartOptions { Height = 200, YFormat = "0.###", Title = "Permutation importance (ΔRMSE)" }, ["#6c5ca8"]);
        var risk = m.M.Predict(state);
        var dec = m.P.Decide(dawnDo, temp, s.Nh3Max * nh3Ratio);
        // policy heatmap: DO bucket × temperature bucket (NH₃ ok)
        string[] rows = ["DO < crit", "DO low", "DO sub-opt", "DO ok"], cols = ["cool", "optimal", "hot"];
        var vals = new double[4, 3];
        foreach (var (st, ration) in m.P.Table().Where(x => x.State.Nh3Bucket == 0)) vals[st.DoBucket, st.TempBucket] = ration;
        var heat = SvgCharts.Heatmap(rows, cols, vals, ColorRamp.Score.WithRange(0, 1.1), new ChartOptions { Width = 420, Height = 160 });
        return new AiDemoResult(m.G.HoldoutR2, m.G.HoldoutRmse, pred, imp, m.M.Auc, risk, dec, heat, m.MlNet, s.Id);
    }
}
