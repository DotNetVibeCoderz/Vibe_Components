namespace Medical.AI.Learning;

/// <summary>Discrimination and calibration of a probabilistic binary classifier.</summary>
public sealed record BinaryMetrics(int N, double Prevalence, double Auc, double Brier, double LogLoss, double Accuracy, double Sensitivity, double Specificity, double Ece, double Threshold)
{
    public override string ToString() => FormattableString.Invariant($"n={N} prev={Prevalence:0.###} AUC={Auc:0.###} Brier={Brier:0.###} ECE={Ece:0.###} sens={Sensitivity:0.###} spec={Specificity:0.###}");
}

/// <summary>One bin of a reliability diagram.</summary>
public sealed record CalibrationBin(double Lower, double Upper, int Count, double MeanPredicted, double ObservedRate);

/// <summary>Evaluation helpers shared by model cards, drift monitoring and federated training.</summary>
public static class ModelEvaluation
{
    /// <summary>Area under the ROC curve (Mann-Whitney U, ties counted half).</summary>
    public static double Auc(IReadOnlyList<double> p, IReadOnlyList<bool> y)
    {
        Check(p, y);
        var order = Enumerable.Range(0, p.Count).OrderBy(i => p[i]).ToArray();
        var ranks = new double[p.Count];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && p[order[j + 1]] == p[order[i]]) j++;
            double r = (i + j) / 2.0 + 1;
            for (int k = i; k <= j; k++) ranks[order[k]] = r;
            i = j + 1;
        }
        long pos = y.Count(v => v), neg = y.Count - pos;
        if (pos == 0 || neg == 0) return double.NaN;
        double sumPos = Enumerable.Range(0, p.Count).Where(i => y[i]).Sum(i => ranks[i]);
        return (sumPos - pos * (pos + 1) / 2.0) / (pos * (double)neg);
    }

    /// <summary>Equal-width reliability bins.</summary>
    public static IReadOnlyList<CalibrationBin> Calibration(IReadOnlyList<double> p, IReadOnlyList<bool> y, int bins = 10)
    {
        Check(p, y);
        var res = new List<CalibrationBin>();
        for (int b = 0; b < bins; b++)
        {
            double lo = (double)b / bins, hi = (double)(b + 1) / bins;
            var idx = Enumerable.Range(0, p.Count).Where(i => p[i] >= lo && (p[i] < hi || (b == bins - 1 && p[i] <= hi))).ToList();
            if (idx.Count == 0) { res.Add(new(lo, hi, 0, double.NaN, double.NaN)); continue; }
            res.Add(new(lo, hi, idx.Count, idx.Average(i => p[i]), idx.Count(i => y[i]) / (double)idx.Count));
        }
        return res;
    }

    /// <summary>Expected calibration error: count-weighted mean |observed − predicted| over the bins.</summary>
    public static double Ece(IReadOnlyList<double> p, IReadOnlyList<bool> y, int bins = 10) =>
        Calibration(p, y, bins).Where(b => b.Count > 0).Sum(b => b.Count * Math.Abs(b.ObservedRate - b.MeanPredicted)) / p.Count;

    public static BinaryMetrics Evaluate(IReadOnlyList<double> p, IReadOnlyList<bool> y, double threshold = 0.5)
    {
        Check(p, y);
        int n = p.Count, tp = 0, tn = 0, fp = 0, fn = 0;
        double brier = 0, ll = 0;
        for (int i = 0; i < n; i++)
        {
            double q = Math.Clamp(p[i], 1e-15, 1 - 1e-15);
            brier += (p[i] - (y[i] ? 1 : 0)) * (p[i] - (y[i] ? 1 : 0));
            ll -= y[i] ? Math.Log(q) : Math.Log(1 - q);
            bool pred = p[i] >= threshold;
            if (pred && y[i]) tp++; else if (pred) fp++; else if (y[i]) fn++; else tn++;
        }
        return new BinaryMetrics(n, Math.Round(y.Count(v => v) / (double)n, 4), Math.Round(Auc(p, y), 4), Math.Round(brier / n, 4), Math.Round(ll / n, 4),
            Math.Round((tp + tn) / (double)n, 4), tp + fn == 0 ? double.NaN : Math.Round(tp / (double)(tp + fn), 4), tn + fp == 0 ? double.NaN : Math.Round(tn / (double)(tn + fp), 4),
            Math.Round(Ece(p, y), 4), threshold);
    }

    private static void Check(IReadOnlyList<double> p, IReadOnlyList<bool> y)
    {
        if (p.Count != y.Count) throw new ArgumentException("Predictions and labels differ in length.");
        if (p.Count == 0) throw new ArgumentException("No predictions.");
    }
}
