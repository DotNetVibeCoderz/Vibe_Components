using System.Diagnostics;
using Gravicode.Science.GraviLearn;
using Gravicode.Science.GraviLearn.Explain;
using Gravicode.Science.GraviLearn.ModelSelection;
using Gravicode.Science.GraviLearn.Preprocessing;
using Gravicode.Science.GraviNum;

namespace Blazor.AI.Analytics;

/// <summary>Everything produced by one training run.</summary>
public sealed record TrainingResult
{
    public required string Model { get; init; }

    public required MlTask Task { get; init; }

    public required IReadOnlyList<string> Features { get; init; }

    public required IReadOnlyDictionary<string, double> Metrics { get; init; }

    public required double[] TestActual { get; init; }

    public required double[] TestPredicted { get; init; }

    /// <summary>Positive-class probabilities for binary classifiers (ROC); empty otherwise.</summary>
    public double[] TestScores { get; init; } = [];

    public IReadOnlyList<string> ClassLabels { get; init; } = [];

    public double[,]? ConfusionMatrix { get; init; }

    public IReadOnlyList<(string Feature, double Importance)> FeatureImportance { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public string? Report { get; init; }

    /// <summary>The fitted model and scaler, for predicting new samples.</summary>
    public IEstimator? Estimator { get; init; }

    public StandardScaler? Scaler { get; init; }

    public double PrimaryMetric => Task == MlTask.Regression ? Metrics.GetValueOrDefault("R²") : Metrics.GetValueOrDefault("Accuracy");

    public string PrimaryMetricName => Task == MlTask.Regression ? "R²" : "Accuracy";
}

/// <summary>Trains, evaluates and compares GraviLearn models on an <see cref="AnalyticsDataset"/>.</summary>
public static class MlTrainer
{
    public static TrainingResult Train(AnalyticsDataset dataset, ModelSpec spec, IReadOnlyDictionary<string, double>? hyperParameters = null, IReadOnlyList<string>? features = null, double testSize = 0.25, bool computeImportance = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var featureNames = (features ?? dataset.FeatureColumns).ToArray();
        var x = dataset.Features(featureNames);
        var (y, labels) = dataset.TargetVector();
        var classification = spec.Task == MlTask.Classification;

        var split = Selection.Split(x, y, testSize: testSize, seed: 42, shuffle: true, stratify: classification);
        var scaler = new StandardScaler();
        var trainX = scaler.FitTransform(split.TrainX);
        var testX = scaler.Transform(split.TestX);

        var model = spec.Build(hyperParameters);
        model.Fit(trainX, split.TrainY);
        var predicted = model.Predict(testX);
        var actual = split.TestY;

        var metrics = new Dictionary<string, double>();
        double[,]? confusion = null;
        double[] scores = [];
        string? report;
        if (classification)
        {
            metrics["Accuracy"] = Metrics.Accuracy(actual, predicted);
            metrics["F1 (macro)"] = Metrics.F1Average(actual, predicted, "macro");
            metrics["Precision (macro)"] = Metrics.PrecisionAverage(actual, predicted, "macro");
            metrics["Recall (macro)"] = Metrics.RecallAverage(actual, predicted, "macro");
            var classes = actual.ToArray().Concat(predicted.ToArray()).Distinct().Order().ToList();
            var cm = Metrics.ConfusionMatrix(actual, predicted, classes);
            confusion = ToMatrix(cm);
            if (classes.Count == 2 && model is IClassifier classifier)
            {
                var proba = classifier.PredictProbabilities(testX);
                scores = Enumerable.Range(0, proba.Rows).Select(i => proba[i, proba.Columns - 1]).ToArray();
                metrics["ROC AUC"] = Metrics.RocAucScore(actual, NdArray.FromValues(scores), classes[1]);
            }

            report = Metrics.ClassificationReport(actual, predicted);
        }
        else
        {
            metrics["R²"] = Metrics.R2Score(actual, predicted);
            metrics["RMSE"] = Metrics.RootMeanSquaredError(actual, predicted);
            metrics["MAE"] = Metrics.MeanAbsoluteError(actual, predicted);
            metrics["MAPE %"] = Mape(actual.ToArray(), predicted.ToArray());
            report = Metrics.RegressionReport(actual, predicted, featureNames.Length);
        }

        IReadOnlyList<(string, double)> importance = [];
        if (computeImportance && featureNames.Length > 0)
        {
            importance = PermutationImportance.Compute(model, testX, actual, repeats: 3, seed: 1)
                .Select(f => (featureNames[f.Feature], Math.Max(0, f.Mean)))
                .OrderByDescending(f => f.Item2)
                .ToArray();
        }

        return new TrainingResult
        {
            Model = spec.Name,
            Task = spec.Task,
            Features = featureNames,
            Metrics = metrics,
            TestActual = actual.ToArray(),
            TestPredicted = predicted.ToArray(),
            TestScores = scores,
            ClassLabels = labels,
            ConfusionMatrix = confusion,
            FeatureImportance = importance,
            Duration = stopwatch.Elapsed,
            Report = report,
            Estimator = model,
            Scaler = scaler,
        };
    }

    /// <summary>k-fold cross-validates every algorithm for the task and ranks them (AutoML).</summary>
    public static IReadOnlyList<(ModelSpec Model, double Mean, double Std, TimeSpan Duration)> Leaderboard(AnalyticsDataset dataset, MlTask task, int folds = 5, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var x = new StandardScaler().FitTransform(dataset.Features());
        var (y, _) = dataset.TargetVector();
        var results = new List<(ModelSpec, double, double, TimeSpan)>();
        foreach (var spec in ModelZoo.For(task))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(spec.Name);
            var stopwatch = Stopwatch.StartNew();
            Func<NdArray, NdArray, double> scorer = task == MlTask.Regression ? Metrics.R2Score : Metrics.Accuracy;
            var cv = Selection.CrossValidate(() => spec.Build(), x, y, folds: folds, stratified: task == MlTask.Classification, seed: 7, scorer: scorer);
            results.Add((spec, cv.Mean, cv.StandardDeviation, stopwatch.Elapsed));
        }

        return results.OrderByDescending(r => r.Item2).ToArray();
    }

    /// <summary>Predicts one new sample with a trained result.</summary>
    public static double Predict(TrainingResult result, IReadOnlyList<double> sample)
    {
        var x = NdArray.FromRows([sample.ToArray()]);
        var scaled = result.Scaler!.Transform(x);
        return result.Estimator!.Predict(scaled)[0];
    }

    public static (double[] Fpr, double[] Tpr) Roc(TrainingResult result)
    {
        if (result.TestScores.Length == 0)
        {
            return ([], []);
        }

        var positive = result.TestActual.Distinct().Max();
        var (fpr, tpr, _) = Metrics.RocCurve(NdArray.FromValues(result.TestActual), NdArray.FromValues(result.TestScores), positive);
        return (fpr, tpr);
    }

    /// <summary>Mean absolute percentage error in percent, skipping zero actuals.</summary>
    public static double Mape(IReadOnlyList<double> actual, IReadOnlyList<double> predicted)
    {
        double sum = 0;
        var n = 0;
        for (var i = 0; i < actual.Count; i++)
        {
            if (Math.Abs(actual[i]) > 1e-12)
            {
                sum += Math.Abs((actual[i] - predicted[i]) / actual[i]);
                n++;
            }
        }

        return n == 0 ? 0 : sum / n * 100;
    }

    internal static double[,] ToMatrix(NdArray array)
    {
        var matrix = new double[array.Rows, array.Columns];
        for (var r = 0; r < array.Rows; r++)
        {
            for (var c = 0; c < array.Columns; c++)
            {
                matrix[r, c] = array[r, c];
            }
        }

        return matrix;
    }
}
