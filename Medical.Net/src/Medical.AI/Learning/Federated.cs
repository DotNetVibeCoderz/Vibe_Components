using System.Security.Cryptography;

namespace Medical.AI.Learning;

/// <summary>One participating hospital / clinic: its private rows never leave the site object.</summary>
public sealed class FederatedSite(string name, IReadOnlyList<(double[] X, bool Y)> data)
{
    public string Name { get; } = name;
    internal IReadOnlyList<(double[] X, bool Y)> Data { get; } = data;
    public int Count => Data.Count;
}

/// <summary>Federated training settings.</summary>
public sealed record FederatedOptions
{
    public int Rounds { get; init; } = 30;
    public int LocalEpochs { get; init; } = 5;
    public double LearningRate { get; init; } = 0.5;
    public double L2 { get; init; } = 1e-3;
    /// <summary>Pairwise additive masks so the server only learns the sum of the site updates (Bonawitz et al., CCS 2017).</summary>
    public bool SecureAggregation { get; init; } = true;
    /// <summary>L2 bound on each site's update (DP-FedAvg, McMahan et al., ICLR 2018); null disables clipping and noise.</summary>
    public double? ClipNorm { get; init; }
    /// <summary>Gaussian noise multiplier σ: the aggregate receives N(0, (σ·C)²) per coordinate; 0 = clipping only.</summary>
    public double NoiseMultiplier { get; init; }
    public int Seed { get; init; } = 17;
}

/// <summary>What the coordinator observed in one round (only aggregates).</summary>
public sealed record FederatedRound(int Round, double MeanLoss, double UpdateNorm, int ClippedSites);

/// <summary>A logistic model: standardisation, weights and bias.</summary>
public sealed record LogisticModel(string[] FeatureNames, double[] Mean, double[] Sd, double[] Weights, double Bias)
{
    public double Predict(double[] x)
    {
        double z = Bias;
        for (int j = 0; j < Weights.Length; j++) z += Weights[j] * (x[j] - Mean[j]) / Sd[j];
        return 1 / (1 + Math.Exp(-z));
    }

    public BinaryMetrics Evaluate(IEnumerable<(double[] X, bool Y)> rows)
    {
        var list = rows.ToList();
        return ModelEvaluation.Evaluate(list.Select(r => Predict(r.X)).ToList(), list.Select(r => r.Y).ToList());
    }
}

/// <summary>Result of federated training.</summary>
public sealed record FederatedResult(LogisticModel Model, IReadOnlyList<FederatedRound> History, bool SecureAggregation, double? ClipNorm, double NoiseMultiplier)
{
    /// <summary>
    /// Site-level (ε, δ)-differential privacy of the released model under the Gaussian mechanism with full participation,
    /// from Rényi DP composition (Mironov 2017): ε = min over α of T·α / (2σ²) + ln(1/δ) / (α − 1). Infinite without noise.
    /// </summary>
    public double Epsilon(double delta = 1e-5) => ClipNorm is null ? double.PositiveInfinity : EpsilonFor(History.Count, NoiseMultiplier, delta);

    /// <summary>Site-level ε after <paramref name="rounds"/> Gaussian-mechanism rounds with noise multiplier σ (RDP, full participation).</summary>
    public static double EpsilonFor(int rounds, double noiseMultiplier, double delta = 1e-5)
    {
        if (noiseMultiplier <= 0) return double.PositiveInfinity;
        double best = double.PositiveInfinity, s2 = noiseMultiplier * noiseMultiplier;
        for (double a = 1.01; a <= 512; a *= 1.02)
            best = Math.Min(best, rounds * a / (2 * s2) + Math.Log(1 / delta) / (a - 1));
        return best;
    }
}

/// <summary>
/// Federated averaging (McMahan et al., AISTATS 2017) of a logistic-regression risk model across sites that never share
/// rows: the sites first agree on a standardisation through aggregated sums, then each round trains locally from the
/// global weights and sends only its update. With <see cref="FederatedOptions.SecureAggregation"/> the updates are
/// masked so that the coordinator sees nothing but their sum; with <see cref="FederatedOptions.ClipNorm"/> and a noise
/// multiplier the aggregate is differentially private at site level.
/// </summary>
public static class FederatedLogisticRegression
{
    public static FederatedResult Train(string[] featureNames, IReadOnlyList<FederatedSite> sites, FederatedOptions? options = null)
    {
        var o = options ?? new FederatedOptions();
        if (sites.Count < 2) throw new ArgumentException("Federated training needs at least two sites.", nameof(sites));
        int f = featureNames.Length;
        if (sites.Any(s => s.Data.Any(r => r.X.Length != f))) throw new ArgumentException($"Every row must have {f} features.", nameof(sites));
        var rng = new Random(o.Seed);
        var masks = new PairwiseMasks(sites.Count, o.Seed);

        // 1. Global standardisation from (securely) aggregated sufficient statistics: Σx, Σx², n.
        var stats = Aggregate(sites.Select(s =>
        {
            var v = new double[2 * f + 1];
            foreach (var (x, _) in s.Data) for (int j = 0; j < f; j++) { v[j] += x[j]; v[f + j] += x[j] * x[j]; }
            v[2 * f] = s.Count;
            return v;
        }).ToList(), o.SecureAggregation ? masks : null, 0);
        double n = stats[2 * f];
        var mean = new double[f];
        var sd = new double[f];
        for (int j = 0; j < f; j++)
        {
            mean[j] = stats[j] / n;
            double var = Math.Max(0, stats[f + j] / n - mean[j] * mean[j]) * n / Math.Max(1, n - 1);
            sd[j] = var > 1e-12 ? Math.Sqrt(var) : 1;
        }

        // 2. Rounds of local training + aggregation of updates.
        var w = new double[f + 1];   // last = bias
        var history = new List<FederatedRound>();
        bool dp = o.ClipNorm is not null;
        for (int round = 1; round <= o.Rounds; round++)
        {
            int clipped = 0;
            var payloads = new List<double[]>();
            double lossSum = 0;
            foreach (var s in sites)
            {
                var local = (double[])w.Clone();
                for (int e = 0; e < o.LocalEpochs; e++) Step(local, s.Data, mean, sd, o.LearningRate, o.L2);
                var delta = local.Zip(w, (a, b) => a - b).ToArray();
                if (dp)
                {
                    double norm = Math.Sqrt(delta.Sum(d => d * d));
                    if (norm > o.ClipNorm!.Value) { clipped++; for (int j = 0; j < delta.Length; j++) delta[j] *= o.ClipNorm.Value / norm; }
                }
                // Payload: update (weighted by n without DP; DP-FedAvg uses equal weights so one site's influence is bounded), loss and weight.
                double weight = dp ? 1 : s.Count;
                var payload = new double[delta.Length + 2];
                for (int j = 0; j < delta.Length; j++) payload[j] = delta[j] * weight;
                payload[delta.Length] = Loss(local, s.Data, mean, sd);
                payload[delta.Length + 1] = weight;
                payloads.Add(payload);
            }
            var sum = Aggregate(payloads, o.SecureAggregation ? masks : null, round);
            double totalWeight = sum[^1];
            lossSum = sum[^2];
            var step = new double[f + 1];
            for (int j = 0; j <= f; j++)
            {
                double noise = dp && o.NoiseMultiplier > 0 ? Gaussian(rng) * o.NoiseMultiplier * o.ClipNorm!.Value : 0;
                step[j] = (sum[j] + noise) / totalWeight;
                w[j] += step[j];
            }
            history.Add(new FederatedRound(round, Math.Round(lossSum / sites.Count, 5), Math.Round(Math.Sqrt(step.Sum(x => x * x)), 6), clipped));
        }
        var model = new LogisticModel(featureNames, mean, sd, w[..f], w[f]);
        return new FederatedResult(model, history, o.SecureAggregation, o.ClipNorm, dp ? o.NoiseMultiplier : 0);
    }

    /// <summary>Pooled (centralised) training with the same optimiser, as the reference federated training should approach.</summary>
    public static LogisticModel TrainCentralised(string[] featureNames, IReadOnlyList<(double[] X, bool Y)> rows, int epochs = 150, double learningRate = 0.5, double l2 = 1e-3)
    {
        int f = featureNames.Length;
        var mean = Enumerable.Range(0, f).Select(j => rows.Average(r => r.X[j])).ToArray();
        var sd = Enumerable.Range(0, f).Select(j => { double v = rows.Sum(r => (r.X[j] - mean[j]) * (r.X[j] - mean[j])) / Math.Max(1, rows.Count - 1); return v > 1e-12 ? Math.Sqrt(v) : 1; }).ToArray();
        var w = new double[f + 1];
        for (int e = 0; e < epochs; e++) Step(w, rows, mean, sd, learningRate, l2);
        return new LogisticModel(featureNames, mean, sd, w[..f], w[f]);
    }

    /// <summary>
    /// What the coordinator receives: with masks each payload looks random; only the sum is meaningful. Exposed so the
    /// masking can be inspected and tested.
    /// </summary>
    internal static double[] Aggregate(List<double[]> payloads, PairwiseMasks? masks, int round)
    {
        int len = payloads[0].Length;
        var sum = new double[len];
        for (int i = 0; i < payloads.Count; i++)
        {
            var sent = masks is null ? payloads[i] : masks.Mask(i, payloads[i], round);
            for (int j = 0; j < len; j++) sum[j] += sent[j];
        }
        return sum;
    }

    private static void Step(double[] w, IReadOnlyList<(double[] X, bool Y)> data, double[] mean, double[] sd, double lr, double l2)
    {
        int f = mean.Length;
        var g = new double[f + 1];
        foreach (var (x, y) in data)
        {
            double z = w[f];
            for (int j = 0; j < f; j++) z += w[j] * (x[j] - mean[j]) / sd[j];
            double err = 1 / (1 + Math.Exp(-z)) - (y ? 1 : 0);
            for (int j = 0; j < f; j++) g[j] += err * (x[j] - mean[j]) / sd[j];
            g[f] += err;
        }
        for (int j = 0; j < f; j++) w[j] -= lr * (g[j] / data.Count + l2 * w[j]);
        w[f] -= lr * g[f] / data.Count;
    }

    private static double Loss(double[] w, IReadOnlyList<(double[] X, bool Y)> data, double[] mean, double[] sd)
    {
        int f = mean.Length;
        double l = 0;
        foreach (var (x, y) in data)
        {
            double z = w[f];
            for (int j = 0; j < f; j++) z += w[j] * (x[j] - mean[j]) / sd[j];
            double p = Math.Clamp(1 / (1 + Math.Exp(-z)), 1e-12, 1 - 1e-12);
            l -= y ? Math.Log(p) : Math.Log(1 - p);
        }
        return l / data.Count;
    }

    private static double Gaussian(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}

/// <summary>
/// Pairwise masks for secure aggregation: sites i &lt; j share a seed; i adds the pseudo-random vector and j subtracts
/// it, so the masks cancel in the sum. In a deployment the pair seeds come from a Diffie-Hellman key agreement between
/// the sites; here they are derived per pair for simulation.
/// </summary>
internal sealed class PairwiseMasks(int sites, int seed)
{
    private const double Scale = 1e3;

    public double[] Mask(int site, double[] payload, int round)
    {
        var res = (double[])payload.Clone();
        for (int other = 0; other < sites; other++)
        {
            if (other == site) continue;
            var m = Stream(Math.Min(site, other), Math.Max(site, other), round, payload.Length);
            double sign = site < other ? 1 : -1;
            for (int j = 0; j < res.Length; j++) res[j] += sign * m[j];
        }
        return res;
    }

    private double[] Stream(int a, int b, int round, int len)
    {
        var key = SHA256.HashData(BitConverter.GetBytes(((long)seed << 40) ^ ((long)a << 24) ^ ((long)b << 8) ^ round));
        var rng = new Random(BitConverter.ToInt32(key, 0));
        return Enumerable.Range(0, len).Select(_ => (rng.NextDouble() * 2 - 1) * Scale).ToArray();
    }
}
