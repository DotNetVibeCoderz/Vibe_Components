using AquaCulture.AI.Ml;
using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;
using AquaCulture.Disease;
using AquaCulture.Forecasting;
using AquaCulture.Growth;

namespace AquaCulture.AI;

/// <summary>Conditions used as model features.</summary>
public sealed record PondState(double AbwG, double TemperatureC, double DawnDo, double Nh3, double DensityRatio, double FeedRatio = 1, double SalinityPpt = double.NaN, int DayOfCulture = 30)
{
    public double[] Features(SpeciesProfile s) =>
    [
        Math.Log(AbwG / s.MarketWeightG), TemperatureC - (s.Temperature.OptLow + s.Temperature.OptHigh) / 2, DawnDo / s.DoOptimal, Nh3 / s.Nh3Max,
        DensityRatio, FeedRatio, double.IsNaN(SalinityPpt) ? 1 : s.Salinity.Suitability(SalinityPpt), Math.Min(DayOfCulture, 200) / 100.0,
    ];

    public static readonly string[] FeatureNames = ["log_rel_weight", "temp_dev", "do_ratio", "nh3_ratio", "density_ratio", "feed_ratio", "salinity_fit", "doc_100"];
}

/// <summary>Predicted growth over the next week.</summary>
public sealed record GrowthPrediction(double AdgG, double WeightIn7DaysG, double SgrPct, IReadOnlyList<(string Feature, double Importance)> Drivers);

/// <summary>
/// Growth prediction with gradient-boosted trees. By default it is trained on a deterministic synthetic dataset generated with
/// the AquaGrowth engine over randomised environments; call <see cref="Train"/> with farm records to calibrate to your site.
/// </summary>
public sealed class GrowthPredictor
{
    private GradientBoostedTrees _model = null!;
    private Dataset _holdout = null!;

    public GrowthPredictor(SpeciesProfile species, int samples = 1500, ulong seed = 17)
    {
        Species = species;
        Train(SyntheticDataset(species, samples, seed));
    }

    public SpeciesProfile Species { get; }
    public double HoldoutRmse { get; private set; }
    public double HoldoutR2 { get; private set; }

    /// <summary>Weekly ADG (g/day) samples simulated under random environments.</summary>
    public static Dataset SyntheticDataset(SpeciesProfile s, int samples, ulong seed)
    {
        var rng = new DeterministicRandom(seed);
        var sim = new GrowthSimulator(s);
        var d = new Dataset(PondState.FeatureNames);
        for (int i = 0; i < samples; i++)
        {
            double w = Math.Exp(rng.Uniform(Math.Log(Math.Max(s.StockingWeightG, s.MarketWeightG * 0.01)), Math.Log(s.MarketWeightG * 1.3)));
            var st = new PondState(w, rng.Uniform(s.Temperature.Min + 2, s.Temperature.Max - 1), rng.Uniform(s.DoCritical * 0.8, s.DoOptimal * 1.5),
                Math.Max(0, rng.LogNormal(s.Nh3Max * 0.7, 0.8)), rng.Uniform(0.1, 1.2), rng.Uniform(0.4, 1.2),
                s.Habitat == Habitat.Freshwater ? double.NaN : rng.Uniform(s.Salinity.Min, s.Salinity.Max), rng.Next(1, 180));
            double volume = 1000, count = st.DensityRatio * s.MaxBiomassKgM3 * volume * 1000 / w;
            var env = new DailyEnvironment(st.TemperatureC, st.DawnDo, st.Nh3, st.SalinityPpt, st.FeedRatio);
            var traj = sim.Simulate(new StockState(s, new DateOnly(2026, 1, 1), w, count, volume, st.DayOfCulture), 7, env);
            double adg = (traj.Final.AbwG - w) / 7 * Math.Exp(0.05 * rng.NextGaussian());
            d.Add(st.Features(s), adg / Math.Max(0.001, w) * 100); // target: SGR-like % per day for scale invariance
        }
        return d;
    }

    /// <summary>Trains on a dataset whose target is daily growth in % of body weight.</summary>
    public void Train(Dataset d)
    {
        var (train, test) = d.Split(0.2, 3);
        _model = new GradientBoostedTrees(150, 3, 0.1).Fit(train);
        _holdout = test;
        HoldoutRmse = Math.Round(Metrics.Rmse(_model, test), 4);
        HoldoutR2 = Math.Round(Metrics.R2(_model, test), 3);
    }

    public GrowthPrediction Predict(PondState state)
    {
        double pct = Math.Max(0, _model.Predict(state.Features(Species)));
        double adg = state.AbwG * pct / 100;
        double w7 = state.AbwG * Math.Pow(1 + pct / 100, 7);
        return new GrowthPrediction(Math.Round(adg, 3), Math.Round(w7, 2), Math.Round(pct, 3), _model.PermutationImportance(_holdout).Take(4).ToList());
    }
}

/// <summary>Mortality-event risk.</summary>
public sealed record MortalityRisk(double Probability, Text2 Level, IReadOnlyList<Text2> Reasons);

/// <summary>
/// Probability of a mortality event (daily mortality above 0.5 %) within three days, from a logistic model trained on
/// synthetic stress scenarios of the AquaGrowth mortality model.
/// </summary>
public sealed class MortalityPredictor
{
    private readonly LogisticRegression _model;
    public SpeciesProfile Species { get; }
    public double Auc { get; }

    public MortalityPredictor(SpeciesProfile species, int samples = 2000, ulong seed = 23)
    {
        Species = species;
        var sim = new GrowthSimulator(species);
        var rng = new DeterministicRandom(seed);
        var d = new Dataset(PondState.FeatureNames);
        for (int i = 0; i < samples; i++)
        {
            var st = new PondState(Math.Exp(rng.Uniform(Math.Log(species.MarketWeightG * 0.01), Math.Log(species.MarketWeightG))),
                rng.Uniform(species.Temperature.Min, species.Temperature.Max + 1), rng.Uniform(species.DoCritical * 0.4, species.DoOptimal * 1.4),
                Math.Max(0, rng.LogNormal(species.Nh3Max, 1.0)), rng.Uniform(0.1, 1.3), rng.Uniform(0.5, 1.2), double.NaN, rng.Next(1, 150));
            double m = 0;
            for (int k = 0; k < 3; k++)
                m = Math.Max(m, sim.MortalityRate(new DailyEnvironment(st.TemperatureC + rng.Normal(0, 0.6), st.DawnDo + rng.Normal(0, 0.4), st.Nh3 * rng.LogNormal(1, 0.2)), st.AbwG));
            m *= 1 + 0.4 * Math.Max(0, st.DensityRatio - 0.9);
            d.Add(st.Features(species), m > 0.005 ? 1 : 0);
        }
        var (train, test) = d.Split(0.25, 9);
        _model = new LogisticRegression().Fit(train);
        Auc = Math.Round(Metrics.Auc(_model, test), 3);
    }

    public MortalityRisk Predict(PondState s)
    {
        double p = Math.Round(_model.Predict(s.Features(Species)), 3);
        var reasons = new List<Text2>();
        if (s.DawnDo < Species.DoOptimal) reasons.Add(new($"Dawn DO {s.DawnDo:0.0} mg/L below {Species.DoOptimal}", $"DO subuh {s.DawnDo:0.0} mg/L di bawah {Species.DoOptimal}"));
        if (s.Nh3 > Species.Nh3Max) reasons.Add(new($"NH₃ {s.Nh3:0.000} mg/L above {Species.Nh3Max}", $"NH₃ {s.Nh3:0.000} mg/L di atas {Species.Nh3Max}"));
        if (!Species.Temperature.IsOptimal(s.TemperatureC)) reasons.Add(new($"Temperature {s.TemperatureC:0.0} °C outside the optimum", $"Suhu {s.TemperatureC:0.0} °C di luar optimum"));
        if (s.DensityRatio > 0.9) reasons.Add(new("Biomass close to carrying capacity", "Biomassa mendekati daya dukung"));
        var level = p >= 0.6 ? new Text2("High", "Tinggi") : p >= 0.3 ? new Text2("Moderate", "Sedang") : new Text2("Low", "Rendah");
        return new MortalityRisk(p, level, reasons);
    }
}

/// <summary>Disease outbreak outlook combining the knowledge-base environmental risk with recent mortality.</summary>
public sealed record DiseaseOutlook(IReadOnlyList<DiseaseRisk> Risks, double OutbreakProbability, Text2 Advice);

public static class DiseasePredictor
{
    /// <summary>Outbreak probability: noisy-OR of the top environmental risks, raised by recent unexplained mortality (% per day).</summary>
    public static DiseaseOutlook Predict(SpeciesProfile s, PondConditions c, double recentMortalityPctPerDay = 0)
    {
        var risks = DiagnosisEngine.RiskProfile(s, c);
        double keep = 1;
        foreach (var r in risks.Take(3)) keep *= 1 - r.Risk * r.Disease.Prior * 4;
        double p = 1 - keep;
        p = 1 - (1 - p) * Math.Exp(-recentMortalityPctPerDay / 0.8);
        p = Math.Round(Math.Clamp(p, 0, 0.99), 3);
        var top = risks.FirstOrDefault();
        var advice = top is null ? new Text2("No disease data for this species.", "Belum ada data penyakit untuk spesies ini.")
            : new Text2($"Highest risk: {top.Disease.Name.En} ({top.Risk:P0}). {top.Disease.Prevention.En}", $"Risiko tertinggi: {top.Disease.Name.Id} ({top.Risk:P0}). {top.Disease.Prevention.Id}");
        return new DiseaseOutlook(risks, p, advice);
    }
}

/// <summary>Harvest forecasting facade (Monte-Carlo, native kernel).</summary>
public static class HarvestOutlook
{
    public static HarvestForecast Forecast(SpeciesProfile s, DateOnly from, double abwG, double population, double? targetWeightG = null, double meanTempC = 29,
        int days = 240, int simulations = 400, ulong seed = 2026)
    {
        var temps = Enumerable.Range(0, days).Select(d => meanTempC + 0.8 * Math.Sin(2 * Math.PI * d / 365)).ToArray();
        return HarvestForecaster.Forecast(s, from, abwG, population, targetWeightG ?? s.MarketWeightG, temps, simulations, 1.2, 0.08, seed);
    }
}

public static class AiBuilderExtensions
{
    public static AquaBuilder AddAI(this AquaBuilder b) => b.TryAddModule("AI", _ => { });
}
