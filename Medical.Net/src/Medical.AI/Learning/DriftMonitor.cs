namespace Medical.AI.Learning;

/// <summary>Drift severity by the usual PSI thresholds (&lt; 0.1 stable, 0.1–0.25 moderate, &gt; 0.25 major).</summary>
public enum DriftLevel { Stable, Moderate, Major }

/// <summary>Drift of one feature (or of the model output).</summary>
public sealed record FeatureDrift(string Feature, double Psi, double KsStatistic, double KsPValue, double ReferenceMean, double CurrentMean, DriftLevel Level)
{
    public override string ToString() => FormattableString.Invariant($"{Feature}: PSI {Psi:0.###} KS {KsStatistic:0.###} (p {KsPValue:0.####}) mean {ReferenceMean:0.##}→{CurrentMean:0.##} {Level}");
}

/// <summary>A drift report: input features, predictions and — when outcomes are known — performance and calibration.</summary>
public sealed record DriftReport(int ReferenceCount, int CurrentCount, IReadOnlyList<FeatureDrift> Features, FeatureDrift? Prediction,
    BinaryMetrics? ReferencePerformance, BinaryMetrics? CurrentPerformance, IReadOnlyList<string> Alerts)
{
    public DriftLevel Worst => Features.Select(f => f.Level).Append(Prediction?.Level ?? DriftLevel.Stable).Max();
}

/// <summary>
/// Monitors a deployed model against its reference (validation) window: Population Stability Index on reference-decile
/// bins and the two-sample Kolmogorov-Smirnov test per feature, PSI of the predicted risk, and — when labels arrive —
/// AUC and expected calibration error, raising alerts when discrimination drops or calibration degrades.
/// </summary>
public sealed class DriftMonitor
{
    private readonly string[] _features;
    private readonly double[][] _reference;   // per feature, sorted
    private readonly double[][] _edges;
    private readonly double[]? _refPred;
    private readonly BinaryMetrics? _refPerf;

    public double AucDropAlert { get; init; } = 0.05;
    public double EceRiseAlert { get; init; } = 0.05;

    public DriftMonitor(string[] featureNames, IReadOnlyList<double[]> reference, IReadOnlyList<double>? referencePredictions = null, IReadOnlyList<bool>? referenceLabels = null)
    {
        if (reference.Count < 20) throw new ArgumentException("The reference window needs at least 20 rows.", nameof(reference));
        _features = featureNames;
        _reference = Enumerable.Range(0, featureNames.Length).Select(j => reference.Select(r => r[j]).Order().ToArray()).ToArray();
        _edges = _reference.Select(Edges).ToArray();
        if (referencePredictions is not null)
        {
            _refPred = referencePredictions.Order().ToArray();
            if (referenceLabels is not null) _refPerf = ModelEvaluation.Evaluate(referencePredictions, referenceLabels);
        }
    }

    public DriftReport Check(IReadOnlyList<double[]> current, IReadOnlyList<double>? predictions = null, IReadOnlyList<bool>? labels = null)
    {
        if (current.Count == 0) throw new ArgumentException("No current rows.", nameof(current));
        var alerts = new List<string>();
        var feats = new List<FeatureDrift>();
        for (int j = 0; j < _features.Length; j++)
        {
            var cur = current.Select(r => r[j]).ToArray();
            var d = Compare(_features[j], _reference[j], _edges[j], cur);
            feats.Add(d);
            if (d.Level != DriftLevel.Stable) alerts.Add($"{d.Feature}: {d.Level.ToString().ToLowerInvariant()} shift (PSI {d.Psi:0.###}; mean {d.ReferenceMean:0.##} → {d.CurrentMean:0.##}).");
        }
        FeatureDrift? pd = null;
        if (_refPred is not null && predictions is not null)
        {
            pd = Compare("predicted risk", _refPred, Edges(_refPred), predictions.ToArray());
            if (pd.Level != DriftLevel.Stable) alerts.Add($"Predicted-risk distribution shifted (PSI {pd.Psi:0.###}).");
        }
        BinaryMetrics? perf = null;
        if (predictions is not null && labels is not null)
        {
            perf = ModelEvaluation.Evaluate(predictions, labels);
            if (_refPerf is not null)
            {
                if (_refPerf.Auc - perf.Auc > AucDropAlert) alerts.Add($"AUC fell from {_refPerf.Auc:0.###} to {perf.Auc:0.###} — review or retrain.");
                if (perf.Ece - _refPerf.Ece > EceRiseAlert) alerts.Add($"Calibration degraded (ECE {_refPerf.Ece:0.###} → {perf.Ece:0.###}) — recalibrate.");
            }
        }
        return new DriftReport(_reference[0].Length, current.Count, feats, pd, _refPerf, perf, alerts);
    }

    /// <summary>Population Stability Index of <paramref name="current"/> against bins at the reference deciles.</summary>
    public static double Psi(double[] referenceSorted, double[] edges, double[] current)
    {
        int bins = edges.Length + 1;
        var r = Histogram(referenceSorted, edges);
        var c = Histogram(current, edges);
        double psi = 0;
        for (int b = 0; b < bins; b++)
        {
            double pr = Math.Max(r[b] / referenceSorted.Length, 1e-4), pc = Math.Max(c[b] / current.Length, 1e-4);
            psi += (pc - pr) * Math.Log(pc / pr);
        }
        return psi;
    }

    /// <summary>Two-sample Kolmogorov-Smirnov statistic and asymptotic p-value.</summary>
    public static (double D, double P) KolmogorovSmirnov(double[] aSorted, double[] b)
    {
        var bs = b.Order().ToArray();
        int i = 0, j = 0;
        double d = 0;
        while (i < aSorted.Length && j < bs.Length)
        {
            double v = Math.Min(aSorted[i], bs[j]);
            while (i < aSorted.Length && aSorted[i] <= v) i++;
            while (j < bs.Length && bs[j] <= v) j++;
            d = Math.Max(d, Math.Abs((double)i / aSorted.Length - (double)j / bs.Length));
        }
        double ne = (double)aSorted.Length * bs.Length / (aSorted.Length + bs.Length);
        double lambda = (Math.Sqrt(ne) + 0.12 + 0.11 / Math.Sqrt(ne)) * d;
        double p = 0;
        for (int k = 1; k <= 100; k++) p += 2 * Math.Pow(-1, k - 1) * Math.Exp(-2 * k * k * lambda * lambda);
        return (d, Math.Clamp(p, 0, 1));
    }

    private static FeatureDrift Compare(string name, double[] refSorted, double[] edges, double[] cur)
    {
        double psi = Psi(refSorted, edges, cur);
        var (d, p) = KolmogorovSmirnov(refSorted, cur);
        var level = psi > 0.25 ? DriftLevel.Major : psi >= 0.1 ? DriftLevel.Moderate : DriftLevel.Stable;
        return new FeatureDrift(name, Math.Round(psi, 4), Math.Round(d, 4), p, Math.Round(refSorted.Average(), 4), Math.Round(cur.Average(), 4), level);
    }

    private static double[] Edges(double[] sorted) =>
        Enumerable.Range(1, 9).Select(k => sorted[(int)Math.Floor(k / 10.0 * (sorted.Length - 1))]).Distinct().ToArray();

    private static double[] Histogram(double[] values, double[] edges)
    {
        var h = new double[edges.Length + 1];
        foreach (var v in values)
        {
            int b = Array.BinarySearch(edges, v);
            b = b >= 0 ? b + 1 : ~b;   // values equal to an edge go to the upper bin
            h[b]++;
        }
        return h;
    }
}
