using AquaCulture.Core;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;

namespace AquaCulture.Disease;

/// <summary>Pond conditions used for environmental disease risk.</summary>
public sealed record PondConditions
{
    public double? TemperatureC { get; init; }
    /// <summary>Max − min water temperature over the last 24 h (°C).</summary>
    public double? TemperatureSwing24h { get; init; }
    public double? DawnDo { get; init; }
    public double? Nh3 { get; init; }
    public double? SalinityPpt { get; init; }
    /// <summary>Salinity drop over the last 3 days (ppt), e.g. after heavy rain.</summary>
    public double? SalinityDrop { get; init; }
    /// <summary>Standing biomass as a fraction of the species carrying density (0–1+).</summary>
    public double? DensityRatio { get; init; }
    public double? Bod { get; init; }
    public double? OrpMv { get; init; }
    public int? DayOfCulture { get; init; }
    /// <summary>Netting, grading or transfer in the last 3 days.</summary>
    public bool RecentHandling { get; init; }
}

public sealed record DiseaseRisk(DiseaseInfo Disease, double Risk, IReadOnlyList<(RiskFactor Factor, double Score)> Drivers);

public sealed record DiagnosisCandidate(DiseaseInfo Disease, double Probability, IReadOnlyList<string> MatchedSigns, IReadOnlyList<string> MissingKeySigns, double EnvironmentalRisk);

public sealed record DiagnosisResult(IReadOnlyList<DiagnosisCandidate> Candidates, double ProbabilityNone, Text2 Advice)
{
    public DiagnosisCandidate? Top => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// Disease diagnosis by naive Bayes over clinical signs, with priors scaled by environmental risk.
/// Observed signs contribute P(sign|disease); signs explicitly reported absent contribute 1 − P(sign|disease);
/// a "none of these" hypothesis keeps probabilities calibrated when signs are vague.
/// </summary>
public static class DiagnosisEngine
{
    private const double Noise = 0.03;

    /// <summary>Scores 0–1 for each risk factor under the given conditions.</summary>
    public static double FactorScore(RiskFactor f, PondConditions c, SpeciesProfile s) => Math.Clamp(f switch
    {
        RiskFactor.HighTemperature => c.TemperatureC is { } t ? (t - (s.Temperature.OptHigh - 1)) / 3 : 0,
        RiskFactor.LowTemperature => c.TemperatureC is { } t ? (s.Temperature.OptLow + 1 - t) / 4 : 0,
        RiskFactor.TemperatureSwing => c.TemperatureSwing24h is { } sw ? (sw - 2) / 4 : 0,
        RiskFactor.LowOxygen => c.DawnDo is { } d ? (s.DoOptimal - d) / (s.DoOptimal - s.DoCritical) : 0,
        RiskFactor.HighAmmonia => c.Nh3 is { } n ? (n / s.Nh3Max - 0.8) / 2 : 0,
        RiskFactor.SalinityDrop => c.SalinityDrop is { } dr ? (dr - 1) / 5 : 0,
        RiskFactor.HighSalinity => c.SalinityPpt is { } sal ? (sal - 25) / 10 : 0,
        RiskFactor.HighDensity => c.DensityRatio is { } dn ? (dn - 0.6) / 0.5 : 0,
        RiskFactor.OrganicLoad => Math.Max(c.Bod is { } b ? (b - 10) / 20 : 0, c.OrpMv is { } o ? (150 - o) / 200 : 0),
        RiskFactor.EarlyCulture => c.DayOfCulture is { } doc ? doc <= 35 ? 1 : Math.Max(0, (50 - doc) / 15.0) : 0,
        RiskFactor.Handling => c.RecentHandling ? 1 : 0,
        _ => 0,
    }, 0, 1);

    /// <summary>Environmental risk 0–1 of a disease: 1 − Π(1 − wᵢ·scoreᵢ).</summary>
    public static DiseaseRisk Risk(DiseaseInfo d, PondConditions c, SpeciesProfile s)
    {
        double keep = 1;
        var drivers = new List<(RiskFactor, double)>();
        foreach (var (f, w) in d.RiskFactors)
        {
            double sc = FactorScore(f, c, s);
            if (sc > 0) drivers.Add((f, Math.Round(sc * w, 3)));
            keep *= 1 - w * sc;
        }
        return new DiseaseRisk(d, Math.Round(1 - keep, 3), drivers.OrderByDescending(x => x.Item2).ToList());
    }

    /// <summary>Environmental risk of every disease affecting the species, highest first.</summary>
    public static IReadOnlyList<DiseaseRisk> RiskProfile(SpeciesProfile s, PondConditions c) =>
        DiseaseKnowledgeBase.For(s).Select(d => Risk(d, c, s)).OrderByDescending(r => r.Risk).ToList();

    /// <summary>Ranks diseases given observed (and optionally absent) signs.</summary>
    public static DiagnosisResult Diagnose(SpeciesProfile species, IEnumerable<string> observedSigns, PondConditions? conditions = null,
        IEnumerable<string>? absentSigns = null)
    {
        var obs = observedSigns.Distinct().ToList();
        var absent = (absentSigns ?? []).Except(obs).ToList();
        conditions ??= new PondConditions();
        var diseases = DiseaseKnowledgeBase.For(species).ToList();

        var scores = new List<(DiseaseInfo D, double LogP, double Risk)>();
        foreach (var d in diseases)
        {
            double risk = Risk(d, conditions, species).Risk;
            double logp = Math.Log(d.Prior * (0.5 + 1.5 * risk));
            foreach (var o in obs) logp += Math.Log(d.Signs.TryGetValue(o, out var p) ? p : Noise);
            foreach (var a in absent) logp += Math.Log(1 - (d.Signs.TryGetValue(a, out var p) ? p : Noise));
            scores.Add((d, logp, risk));
        }
        double logNone = Math.Log(0.3);
        foreach (var _ in obs) logNone += Math.Log(0.06);
        foreach (var _ in absent) logNone += Math.Log(0.94);

        double max = Math.Max(logNone, scores.Count == 0 ? logNone : scores.Max(x => x.LogP));
        double z = Math.Exp(logNone - max) + scores.Sum(x => Math.Exp(x.LogP - max));
        var cands = scores.Select(x => new DiagnosisCandidate(x.D, Math.Exp(x.LogP - max) / z,
                obs.Where(x.D.Signs.ContainsKey).ToList(),
                x.D.Signs.Where(kv => kv.Value >= 0.7 && !obs.Contains(kv.Key)).Select(kv => kv.Key).ToList(), x.Risk))
            .OrderByDescending(c => c.Probability).ToList();
        double pNone = Math.Exp(logNone - max) / z;

        var top = cands.FirstOrDefault();
        Text2 advice = top is null || top.Probability < 0.35
            ? new("Signs are not specific. Send 10–20 moribund animals to a fish-health laboratory (PCR/bacteriology) and check water quality.",
                  "Gejala belum spesifik. Kirim 10–20 hewan sekarat ke laboratorium kesehatan ikan (PCR/bakteriologi) dan periksa kualitas air.")
            : new($"Most likely {top.Disease.Name.En} ({top.Probability:P0}). Confirm by laboratory test. {top.Disease.Treatment.En}" + (top.Disease.Notifiable ? " This disease is notifiable." : ""),
                  $"Paling mungkin {top.Disease.Name.Id} ({top.Probability:P0}). Pastikan dengan uji laboratorium. {top.Disease.Treatment.Id}" + (top.Disease.Notifiable ? " Penyakit ini wajib dilaporkan." : ""));
        return new DiagnosisResult(cands, pNone, advice);
    }
}

public static class DiseaseBuilderExtensions
{
    public static AquaBuilder AddDisease(this AquaBuilder b) => b.TryAddModule("Disease", _ => { });
}
