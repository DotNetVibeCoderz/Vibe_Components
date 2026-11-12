using AquaCulture.Core.Data;

namespace AquaCulture.AI.Ml;

/// <summary>Tabular dataset (rows × features) with a numeric target.</summary>
public sealed class Dataset(string[] featureNames)
{
    public string[] FeatureNames { get; } = featureNames;
    public List<double[]> X { get; } = [];
    public List<double> Y { get; } = [];
    public int Count => X.Count;

    public void Add(double[] features, double target)
    {
        if (features.Length != FeatureNames.Length) throw new ArgumentException("Feature count mismatch.");
        X.Add(features); Y.Add(target);
    }

    /// <summary>Deterministic shuffle + split.</summary>
    public (Dataset Train, Dataset Test) Split(double testFraction = 0.2, ulong seed = 1)
    {
        var idx = Enumerable.Range(0, Count).ToArray();
        var rng = new DeterministicRandom(seed);
        for (int i = idx.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (idx[i], idx[j]) = (idx[j], idx[i]); }
        int nTest = (int)(Count * testFraction);
        Dataset a = new(FeatureNames), b = new(FeatureNames);
        for (int k = 0; k < idx.Length; k++) (k < nTest ? b : a).Add(X[idx[k]], Y[idx[k]]);
        return (a, b);
    }
}

/// <summary>A trained regression/classification model.</summary>
public interface IModel
{
    double Predict(ReadOnlySpan<double> features);
}

/// <summary>Z-score feature scaler.</summary>
public sealed class StandardScaler
{
    public double[] Mean { get; private set; } = [];
    public double[] Std { get; private set; } = [];

    public StandardScaler Fit(IReadOnlyList<double[]> x)
    {
        int p = x[0].Length;
        Mean = new double[p]; Std = new double[p];
        for (int j = 0; j < p; j++)
        {
            var col = x.Select(r => r[j]).ToArray();
            Mean[j] = col.Average();
            double sd = Stats.StdDev(col);
            Std[j] = sd > 1e-12 ? sd : 1;
        }
        return this;
    }

    public double[] Transform(ReadOnlySpan<double> r)
    {
        var o = new double[r.Length];
        for (int j = 0; j < r.Length; j++) o[j] = (r[j] - Mean[j]) / Std[j];
        return o;
    }
}

/// <summary>Ridge regression (L2) solved in closed form on standardised features.</summary>
public sealed class RidgeRegression(double lambda = 1.0) : IModel
{
    private readonly StandardScaler _scaler = new();
    public double[] Weights { get; private set; } = [];
    public double Intercept { get; private set; }

    public RidgeRegression Fit(Dataset d)
    {
        _scaler.Fit(d.X);
        int p = d.FeatureNames.Length, n = d.Count;
        var xtx = new double[p, p]; var xty = new double[p];
        double ym = d.Y.Average();
        foreach (var (row, y) in d.X.Zip(d.Y))
        {
            var z = _scaler.Transform(row);
            for (int i = 0; i < p; i++) { xty[i] += z[i] * (y - ym); for (int j = 0; j < p; j++) xtx[i, j] += z[i] * z[j]; }
        }
        for (int i = 0; i < p; i++) xtx[i, i] += lambda;
        Weights = Numerics.Solve(xtx, xty);
        Intercept = ym;
        return this;
    }

    public double Predict(ReadOnlySpan<double> features)
    {
        var z = _scaler.Transform(features);
        double s = Intercept;
        for (int i = 0; i < z.Length; i++) s += Weights[i] * z[i];
        return s;
    }
}

/// <summary>L2-regularised logistic regression (full-batch gradient descent on standardised features).</summary>
public sealed class LogisticRegression(double lambda = 0.01, int epochs = 800, double learningRate = 0.3) : IModel
{
    private readonly StandardScaler _scaler = new();
    public double[] Weights { get; private set; } = [];
    public double Bias { get; private set; }

    public LogisticRegression Fit(Dataset d)
    {
        _scaler.Fit(d.X);
        var z = d.X.Select(r => _scaler.Transform(r)).ToArray();
        int p = d.FeatureNames.Length, n = d.Count;
        Weights = new double[p];
        for (int e = 0; e < epochs; e++)
        {
            var g = new double[p]; double gb = 0;
            for (int k = 0; k < n; k++)
            {
                double s = Bias; for (int j = 0; j < p; j++) s += Weights[j] * z[k][j];
                double err = Sigmoid(s) - d.Y[k];
                for (int j = 0; j < p; j++) g[j] += err * z[k][j];
                gb += err;
            }
            for (int j = 0; j < p; j++) Weights[j] -= learningRate * (g[j] / n + lambda * Weights[j]);
            Bias -= learningRate * gb / n;
        }
        return this;
    }

    public static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));

    /// <summary>Probability of the positive class.</summary>
    public double Predict(ReadOnlySpan<double> features)
    {
        var z = _scaler.Transform(features);
        double s = Bias; for (int j = 0; j < z.Length; j++) s += Weights[j] * z[j];
        return Sigmoid(s);
    }
}

/// <summary>CART regression tree (variance reduction) used as the weak learner of <see cref="GradientBoostedTrees"/>.</summary>
public sealed class RegressionTree : IModel
{
    private sealed record Node(int Feature, double Threshold, Node? Left, Node? Right, double Value);
    private Node? _root;
    public int MaxDepth { get; init; } = 3;
    public int MinLeaf { get; init; } = 10;

    public RegressionTree Fit(IReadOnlyList<double[]> x, IReadOnlyList<double> y, int[]? rows = null)
    {
        rows ??= Enumerable.Range(0, x.Count).ToArray();
        _root = Build(x, y, rows, 0);
        return this;
    }

    private Node Build(IReadOnlyList<double[]> x, IReadOnlyList<double> y, int[] rows, int depth)
    {
        double mean = rows.Average(r => y[r]);
        if (depth >= MaxDepth || rows.Length < 2 * MinLeaf) return new Node(-1, 0, null, null, mean);
        int p = x[0].Length; double bestGain = 0; int bf = -1; double bt = 0;
        double total = rows.Sum(r => y[r]), totalSq = rows.Sum(r => y[r] * y[r]);
        double baseSse = totalSq - total * total / rows.Length;
        for (int f = 0; f < p; f++)
        {
            var sorted = rows.OrderBy(r => x[r][f]).ToArray();
            double ls = 0, lsq = 0;
            for (int i = 0; i < sorted.Length - 1; i++)
            {
                double v = y[sorted[i]]; ls += v; lsq += v * v;
                int nl = i + 1, nr = sorted.Length - nl;
                if (nl < MinLeaf || nr < MinLeaf || x[sorted[i]][f] == x[sorted[i + 1]][f]) continue;
                double rs = total - ls, rsq = totalSq - lsq;
                double sse = lsq - ls * ls / nl + rsq - rs * rs / nr;
                double gain = baseSse - sse;
                if (gain > bestGain) { bestGain = gain; bf = f; bt = (x[sorted[i]][f] + x[sorted[i + 1]][f]) / 2; }
            }
        }
        if (bf < 0) return new Node(-1, 0, null, null, mean);
        var left = rows.Where(r => x[r][bf] <= bt).ToArray();
        var right = rows.Where(r => x[r][bf] > bt).ToArray();
        return new Node(bf, bt, Build(x, y, left, depth + 1), Build(x, y, right, depth + 1), mean);
    }

    public double Predict(ReadOnlySpan<double> f)
    {
        var n = _root ?? throw new InvalidOperationException("Tree not trained.");
        while (n.Feature >= 0) n = f[n.Feature] <= n.Threshold ? n.Left! : n.Right!;
        return n.Value;
    }
}

/// <summary>Gradient-boosted regression trees (squared loss) with shrinkage and row subsampling.</summary>
public sealed class GradientBoostedTrees(int trees = 120, int maxDepth = 3, double learningRate = 0.1, double subsample = 0.8, ulong seed = 11) : IModel
{
    private readonly List<RegressionTree> _trees = [];
    private double _base;

    public IReadOnlyList<double> TrainingLoss { get; private set; } = [];
    public string[] FeatureNames { get; private set; } = [];

    public GradientBoostedTrees Fit(Dataset d)
    {
        FeatureNames = d.FeatureNames;
        _base = d.Y.Average();
        var pred = Enumerable.Repeat(_base, d.Count).ToArray();
        var rng = new DeterministicRandom(seed);
        var loss = new List<double>();
        for (int t = 0; t < trees; t++)
        {
            var resid = d.Y.Select((y, i) => y - pred[i]).ToArray();
            var rows = Enumerable.Range(0, d.Count).Where(_ => rng.NextDouble() < subsample).ToArray();
            var tree = new RegressionTree { MaxDepth = maxDepth, MinLeaf = Math.Max(5, d.Count / 200) }.Fit(d.X, resid, rows);
            _trees.Add(tree);
            for (int i = 0; i < d.Count; i++) pred[i] += learningRate * tree.Predict(d.X[i]);
            loss.Add(Math.Sqrt(d.Y.Select((y, i) => (y - pred[i]) * (y - pred[i])).Average()));
        }
        TrainingLoss = loss;
        return this;
    }

    public double Predict(ReadOnlySpan<double> features)
    {
        double s = _base;
        foreach (var t in _trees) s += learningRate * t.Predict(features);
        return s;
    }

    /// <summary>Permutation importance (increase in RMSE when a feature is shuffled) on a dataset.</summary>
    public IReadOnlyList<(string Feature, double Importance)> PermutationImportance(Dataset d, ulong seed = 5)
    {
        double baseRmse = Rmse(d, null, -1, seed);
        return FeatureNames.Select((f, j) => (f, Math.Round(Rmse(d, null, j, seed) - baseRmse, 5))).OrderByDescending(x => x.Item2).ToList();
    }

    private double Rmse(Dataset d, double[]? _, int shuffle, ulong seed)
    {
        var rng = new DeterministicRandom(seed);
        var perm = Enumerable.Range(0, d.Count).ToArray();
        if (shuffle >= 0) for (int i = perm.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }
        double s = 0;
        for (int i = 0; i < d.Count; i++)
        {
            var row = (double[])d.X[i].Clone();
            if (shuffle >= 0) row[shuffle] = d.X[perm[i]][shuffle];
            double e = d.Y[i] - Predict(row); s += e * e;
        }
        return Math.Sqrt(s / d.Count);
    }
}

/// <summary>Regression and classification metrics.</summary>
public static class Metrics
{
    public static double Rmse(IModel m, Dataset d) => Math.Sqrt(d.X.Select((x, i) => Math.Pow(d.Y[i] - m.Predict(x), 2)).Average());

    public static double R2(IModel m, Dataset d)
    {
        double mean = d.Y.Average();
        double ssr = d.X.Select((x, i) => Math.Pow(d.Y[i] - m.Predict(x), 2)).Sum(), sst = d.Y.Sum(y => (y - mean) * (y - mean));
        return 1 - ssr / sst;
    }

    /// <summary>Area under the ROC curve (Mann–Whitney).</summary>
    public static double Auc(IModel m, Dataset d)
    {
        var scored = d.X.Select((x, i) => (S: m.Predict(x), Y: d.Y[i] >= 0.5)).OrderBy(t => t.S).ToArray();
        double rankSum = 0; int pos = 0;
        for (int i = 0; i < scored.Length; i++) if (scored[i].Y) { rankSum += i + 1; pos++; }
        int neg = scored.Length - pos;
        return pos == 0 || neg == 0 ? double.NaN : (rankSum - pos * (pos + 1) / 2.0) / (pos * (double)neg);
    }

    public static double Accuracy(IModel m, Dataset d, double threshold = 0.5) => d.X.Select((x, i) => (m.Predict(x) >= threshold) == (d.Y[i] >= 0.5) ? 1.0 : 0).Average();
}
