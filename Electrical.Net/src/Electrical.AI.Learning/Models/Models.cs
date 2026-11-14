using Gravicode.Science.GraviLearn.Trees;
using Gravicode.Science.GraviNum;

namespace Electrical.AI.Learning;

/// <summary>A fitted regression model over dense feature vectors.</summary>
public interface IRegressionModel
{
    /// <summary>Backend description, e.g. "GraviLearn gradient boosting".</summary>
    string Kind { get; }
    int FeatureCount { get; }
    double Predict(ReadOnlySpan<double> features);
    /// <summary>Batch prediction (faster for ML.NET models).</summary>
    double[] Predict(IReadOnlyList<double[]> rows);
    /// <summary>Saves the model (<c>.json</c> for GraviLearn ensembles, ML.NET <c>.zip</c> for ML.NET models).</summary>
    void Save(string path);
}

/// <summary>A fitted classifier over dense feature vectors with string class labels.</summary>
public interface IClassificationModel
{
    string Kind { get; }
    int FeatureCount { get; }
    /// <summary>Class labels in <see cref="Probabilities"/> column order.</summary>
    IReadOnlyList<string> Labels { get; }
    string Predict(ReadOnlySpan<double> features);
    string[] Predict(IReadOnlyList<double[]> rows);
    /// <summary>Class probabilities aligned with <see cref="Labels"/>.</summary>
    double[] Probabilities(ReadOnlySpan<double> features);
    void Save(string path);
}

/// <summary>Regression backends.</summary>
public enum RegressionAlgorithm
{
    /// <summary>Gradient-boosted CART trees: GraviLearn <c>DecisionTreeRegressor</c> base learners fitted to residuals (stochastic, shrinkage).</summary>
    GradientBoosting,
    /// <summary>ML.NET FastTree (MART) regression.</summary>
    MlNetFastTree,
}

/// <summary>Classification backends.</summary>
public enum ClassificationAlgorithm
{
    /// <summary>GraviLearn <c>RandomForestClassifier</c> (bootstrap + feature subsampling, Gini).</summary>
    RandomForest,
    /// <summary>ML.NET one-versus-all over calibrated FastTree binary classifiers.</summary>
    MlNetFastTree,
}

/// <summary>Hyper-parameters of <see cref="ModelTrainer.TrainRegressor"/>.</summary>
public sealed record RegressionTrainingOptions
{
    public RegressionAlgorithm Algorithm { get; init; } = RegressionAlgorithm.GradientBoosting;
    /// <summary>Boosting rounds / trees.</summary>
    public int Trees { get; init; } = 150;
    /// <summary>Tree depth (GraviLearn); ML.NET uses <see cref="Leaves"/>.</summary>
    public int MaxDepth { get; init; } = 5;
    /// <summary>Maximum leaves per tree (ML.NET FastTree).</summary>
    public int Leaves { get; init; } = 20;
    public double LearningRate { get; init; } = 0.1;
    public int MinSamplesLeaf { get; init; } = 3;
    /// <summary>Row fraction sampled per boosting round (stochastic gradient boosting; 1 = all rows).</summary>
    public double Subsample { get; init; } = 0.8;
    public int Seed { get; init; } = 42;
    public IReadOnlyList<string>? FeatureNames { get; init; }
}

/// <summary>Hyper-parameters of <see cref="ModelTrainer.TrainClassifier"/>.</summary>
public sealed record ClassificationTrainingOptions
{
    public ClassificationAlgorithm Algorithm { get; init; } = ClassificationAlgorithm.RandomForest;
    public int Trees { get; init; } = 60;
    /// <summary>Maximum depth (0 = unlimited, GraviLearn).</summary>
    public int MaxDepth { get; init; }
    public int Leaves { get; init; } = 16;
    public double LearningRate { get; init; } = 0.2;
    public int MinSamplesLeaf { get; init; } = 1;
    /// <summary>Features tried per split (0 = √features, GraviLearn).</summary>
    public int MaxFeatures { get; init; }
    public int Seed { get; init; } = 42;
    public IReadOnlyList<string>? FeatureNames { get; init; }
}

/// <summary>Regression model backed by a <see cref="TreeEnsemble"/> trained with GraviLearn trees.</summary>
public sealed class TreeEnsembleRegressor(TreeEnsemble ensemble) : IRegressionModel
{
    public TreeEnsemble Ensemble { get; } = ensemble;
    public string Kind => "GraviLearn gradient boosting";
    public int FeatureCount => Ensemble.FeatureCount;
    public double Predict(ReadOnlySpan<double> features) => Ensemble.PredictValue(features);
    public double[] Predict(IReadOnlyList<double[]> rows) => rows.Select(r => Ensemble.PredictValue(r)).ToArray();
    public void Save(string path) => Ensemble.Save(path);
}

/// <summary>Classifier backed by a <see cref="TreeEnsemble"/> exported from a GraviLearn random forest.</summary>
public sealed class TreeEnsembleClassifier(TreeEnsemble ensemble) : IClassificationModel
{
    public TreeEnsemble Ensemble { get; } = ensemble;
    public string Kind => "GraviLearn random forest";
    public int FeatureCount => Ensemble.FeatureCount;
    public IReadOnlyList<string> Labels => Ensemble.ClassLabels!;
    public double[] Probabilities(ReadOnlySpan<double> features) => Ensemble.PredictProbabilities(features);

    public string Predict(ReadOnlySpan<double> features)
    {
        var p = Probabilities(features);
        int best = 0;
        for (int c = 1; c < p.Length; c++) if (p[c] > p[best]) best = c;
        return Labels[best];
    }

    public string[] Predict(IReadOnlyList<double[]> rows) => rows.Select(r => Predict(r)).ToArray();
    public void Save(string path) => Ensemble.Save(path);
}

/// <summary>Trains regressors and classifiers on dense feature rows with GraviLearn or ML.NET (deterministic for a fixed seed).</summary>
public static class ModelTrainer
{
    /// <summary>Trains a regression model.</summary>
    public static IRegressionModel TrainRegressor(IReadOnlyList<double[]> x, IReadOnlyList<double> y, RegressionTrainingOptions? options = null)
    {
        var o = options ?? new RegressionTrainingOptions();
        Validate(x, y.Count);
        return o.Algorithm switch
        {
            RegressionAlgorithm.MlNetFastTree => MlNetRegressor.Train(x, y, o),
            _ => new TreeEnsembleRegressor(GradientBoost(x, y, o)),
        };
    }

    /// <summary>Trains a classifier on string labels.</summary>
    public static IClassificationModel TrainClassifier(IReadOnlyList<double[]> x, IReadOnlyList<string> labels, ClassificationTrainingOptions? options = null)
    {
        var o = options ?? new ClassificationTrainingOptions();
        Validate(x, labels.Count);
        return o.Algorithm switch
        {
            ClassificationAlgorithm.MlNetFastTree => MlNetClassifier.Train(x, labels, o),
            _ => new TreeEnsembleClassifier(Forest(x, labels, o)),
        };
    }

    /// <summary>Loads a regression model saved by <see cref="IRegressionModel.Save"/> (format detected from the content).</summary>
    public static IRegressionModel LoadRegressor(string path) =>
        IsZip(path) ? MlNetRegressor.Load(path) : new TreeEnsembleRegressor(TreeEnsemble.Load(path));

    /// <summary>Loads a classifier saved by <see cref="IClassificationModel.Save"/>.</summary>
    public static IClassificationModel LoadClassifier(string path) =>
        IsZip(path) ? MlNetClassifier.Load(path) : new TreeEnsembleClassifier(TreeEnsemble.Load(path));

    /// <summary>Fraction of equal labels.</summary>
    public static double Accuracy(IReadOnlyList<string> actual, IReadOnlyList<string> predicted)
    {
        if (actual.Count != predicted.Count) throw new ArgumentException("Length mismatch.");
        int ok = 0;
        for (int i = 0; i < actual.Count; i++) if (actual[i] == predicted[i]) ok++;
        return actual.Count == 0 ? 0 : (double)ok / actual.Count;
    }

    private static bool IsZip(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> h = stackalloc byte[2];
        return fs.Read(h) == 2 && h[0] == (byte)'P' && h[1] == (byte)'K';
    }

    private static void Validate(IReadOnlyList<double[]> x, int targets)
    {
        if (x.Count == 0) throw new ArgumentException("No training rows.", nameof(x));
        if (x.Count != targets) throw new ArgumentException("Features and targets differ in length.");
        int n = x[0].Length;
        if (x.Any(r => r.Length != n)) throw new ArgumentException("All rows must have the same feature count.", nameof(x));
    }

    internal static NdArray Matrix(IReadOnlyList<double[]> x, IReadOnlyList<int>? rows = null)
    {
        int n = rows?.Count ?? x.Count, p = x[0].Length;
        var d = new double[n * p];
        for (int i = 0; i < n; i++) Array.Copy(x[rows is null ? i : rows[i]], 0, d, i * p, p);
        return new NdArray(d, n, p);
    }

    /// <summary>
    /// Stochastic gradient boosting with squared loss (Friedman 2002): F₀ = mean(y); each round fits a GraviLearn regression tree to the
    /// residuals of a row subsample and adds it with shrinkage ν. GraviLearn's own <c>GradientBoostingRegressor</c> does not expose its
    /// trees, so the loop is run here to keep the fitted trees exportable.
    /// </summary>
    private static TreeEnsemble GradientBoost(IReadOnlyList<double[]> x, IReadOnlyList<double> y, RegressionTrainingOptions o)
    {
        int n = x.Count, p = x[0].Length;
        double init = y.Average();
        var pred = new double[n];
        Array.Fill(pred, init);
        var rng = new Random(o.Seed);
        int m = Math.Clamp((int)Math.Round(n * o.Subsample), 2, n);
        var trees = new List<DecisionTree>(o.Trees);
        var all = Enumerable.Range(0, n).ToArray();
        var full = Matrix(x);
        for (int round = 0; round < o.Trees; round++)
        {
            int[] rows = m == n ? all : SampleWithoutReplacement(rng, n, m);
            var r = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) r[i] = y[rows[i]] - pred[rows[i]];
            var tree = new DecisionTreeRegressor(o.MaxDepth, 2, o.MinSamplesLeaf, 0, o.Seed + round);
            tree.Fit(Matrix(x, rows), new NdArray(r, r.Length));
            trees.Add(tree);
            var upd = tree.Predict(full).ToArray();
            for (int i = 0; i < n; i++) pred[i] += o.LearningRate * upd[i];
        }
        return TreeEnsemble.FromGraviLearn(trees, TreeCombination.Sum, p, init, o.LearningRate, null, o.FeatureNames);
    }

    private static int[] SampleWithoutReplacement(Random rng, int n, int m)
    {
        var idx = Enumerable.Range(0, n).ToArray();
        for (int i = 0; i < m; i++) { int j = rng.Next(i, n); (idx[i], idx[j]) = (idx[j], idx[i]); }
        var r = idx.AsSpan(0, m).ToArray();
        Array.Sort(r);
        return r;
    }

    private static TreeEnsemble Forest(IReadOnlyList<double[]> x, IReadOnlyList<string> labels, ClassificationTrainingOptions o)
    {
        var classes = labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var code = classes.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => (double)t.i, StringComparer.Ordinal);
        var y = new NdArray(labels.Select(l => code[l]).ToArray(), labels.Count);
        var rf = new RandomForestClassifier(o.Trees, o.MaxDepth, 2, o.MinSamplesLeaf, o.MaxFeatures, true, o.Seed);
        rf.Fit(Matrix(x), y);
        // GraviLearn orders classes ascending by numeric code, i.e. the same order as `classes`.
        var labelsInOrder = rf.Classes.Select(c => classes[(int)c]).ToArray();
        return TreeEnsemble.FromGraviLearn(rf.Trees, TreeCombination.Average, x[0].Length, 0, 1, labelsInOrder, o.FeatureNames);
    }
}
