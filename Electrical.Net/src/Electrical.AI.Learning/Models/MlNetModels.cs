using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace Electrical.AI.Learning;

/// <summary>ML.NET input row (single-precision feature vector + label). Public only because ML.NET binds rows by reflection.</summary>
public sealed class MlNetFeatureRow
{
    public float[] Features { get; set; } = [];
    public float Label { get; set; }
    public string LabelText { get; set; } = "";
}

internal static class MlNetData
{
    public static SchemaDefinition Schema(int features)
    {
        var s = SchemaDefinition.Create(typeof(MlNetFeatureRow));
        s[nameof(MlNetFeatureRow.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, features);
        return s;
    }

    public static IDataView Load(MLContext ml, IReadOnlyList<double[]> x, Func<int, float>? label = null, Func<int, string>? text = null)
    {
        int p = x.Count == 0 ? 0 : x[0].Length;
        var rows = new MlNetFeatureRow[x.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var f = new float[p];
            for (int j = 0; j < p; j++) f[j] = (float)x[i][j];
            rows[i] = new MlNetFeatureRow { Features = f, Label = label?.Invoke(i) ?? 0, LabelText = text?.Invoke(i) ?? "" };
        }
        return ml.Data.LoadFromEnumerable(rows, Schema(p));
    }

    public static int FeatureCount(DataViewSchema input) =>
        input[nameof(MlNetFeatureRow.Features)].Type is VectorDataViewType v ? v.Size : throw new InvalidDataException("Model has no fixed-size Features column.");
}

/// <summary>ML.NET FastTree regression model (single-threaded, seeded, saved as an ML.NET model zip).</summary>
public sealed class MlNetRegressor : IRegressionModel
{
    private readonly MLContext _ml;
    private readonly ITransformer _model;
    private readonly DataViewSchema _inputSchema;

    private MlNetRegressor(MLContext ml, ITransformer model, DataViewSchema inputSchema)
    {
        _ml = ml; _model = model; _inputSchema = inputSchema;
        FeatureCount = MlNetData.FeatureCount(inputSchema);
    }

    public string Kind => "ML.NET FastTree";
    public int FeatureCount { get; }
    /// <summary>The ML.NET transformer (for advanced use, e.g. feature importance).</summary>
    public ITransformer Model => _model;

    internal static MlNetRegressor Train(IReadOnlyList<double[]> x, IReadOnlyList<double> y, RegressionTrainingOptions o)
    {
        var ml = new MLContext(seed: o.Seed);
        var data = MlNetData.Load(ml, x, i => (float)y[i]);
        var trainer = ml.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
        {
            NumberOfTrees = o.Trees,
            NumberOfLeaves = o.Leaves,
            MinimumExampleCountPerLeaf = Math.Max(1, o.MinSamplesLeaf),
            LearningRate = o.LearningRate,
            NumberOfThreads = 1,
            Seed = o.Seed,
            FeatureFraction = 1.0,
            BaggingSize = 0,
        });
        return new MlNetRegressor(ml, trainer.Fit(data), data.Schema);
    }

    public double Predict(ReadOnlySpan<double> features) => Predict([features.ToArray()])[0];

    public double[] Predict(IReadOnlyList<double[]> rows)
    {
        if (rows.Count == 0) return [];
        var scored = _model.Transform(MlNetData.Load(_ml, rows));
        return scored.GetColumn<float>("Score").Select(v => (double)v).ToArray();
    }

    public void Save(string path) => _ml.Model.Save(_model, _inputSchema, path);

    /// <summary>Loads a model saved by <see cref="Save"/>.</summary>
    public static MlNetRegressor Load(string path)
    {
        var ml = new MLContext(seed: 0);
        var model = ml.Model.Load(path, out var schema);
        return new MlNetRegressor(ml, model, schema);
    }
}

/// <summary>ML.NET multiclass model: label → key, one-versus-all over FastTree binary classifiers, key → label.</summary>
public sealed class MlNetClassifier : IClassificationModel
{
    private readonly MLContext _ml;
    private readonly ITransformer _model;
    private readonly DataViewSchema _inputSchema;

    private MlNetClassifier(MLContext ml, ITransformer model, DataViewSchema inputSchema)
    {
        _ml = ml; _model = model; _inputSchema = inputSchema;
        FeatureCount = MlNetData.FeatureCount(inputSchema);
        // Class names are the slot names of the Score column (taken from the label key values).
        var output = model.GetOutputSchema(inputSchema);
        VBuffer<ReadOnlyMemory<char>> names = default;
        output["Score"].GetSlotNames(ref names);
        Labels = names.DenseValues().Select(m => m.ToString()).ToArray();
    }

    public string Kind => "ML.NET FastTree one-versus-all";
    public int FeatureCount { get; }
    public IReadOnlyList<string> Labels { get; }

    internal static MlNetClassifier Train(IReadOnlyList<double[]> x, IReadOnlyList<string> labels, ClassificationTrainingOptions o)
    {
        var ml = new MLContext(seed: o.Seed);
        var data = MlNetData.Load(ml, x, text: i => labels[i]);
        var binary = ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
        {
            NumberOfTrees = o.Trees,
            NumberOfLeaves = o.Leaves,
            MinimumExampleCountPerLeaf = Math.Max(1, o.MinSamplesLeaf),
            LearningRate = o.LearningRate,
            NumberOfThreads = 1,
            Seed = o.Seed,
        });
        var pipeline = ml.Transforms.Conversion.MapValueToKey("Label", nameof(MlNetFeatureRow.LabelText))
            .Append(ml.MulticlassClassification.Trainers.OneVersusAll(binary))
            .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
        return new MlNetClassifier(ml, pipeline.Fit(data), data.Schema);
    }

    public string Predict(ReadOnlySpan<double> features) => Predict([features.ToArray()])[0];

    public string[] Predict(IReadOnlyList<double[]> rows)
    {
        if (rows.Count == 0) return [];
        var scored = _model.Transform(MlNetData.Load(_ml, rows));
        return scored.GetColumn<ReadOnlyMemory<char>>("PredictedLabel").Select(m => m.ToString()).ToArray();
    }

    public double[] Probabilities(ReadOnlySpan<double> features)
    {
        var scored = _model.Transform(MlNetData.Load(_ml, [features.ToArray()]));
        var s = scored.GetColumn<float[]>("Score").First();
        return s.Select(v => (double)v).ToArray();
    }

    public void Save(string path) => _ml.Model.Save(_model, _inputSchema, path);

    public static MlNetClassifier Load(string path)
    {
        var ml = new MLContext(seed: 0);
        var model = ml.Model.Load(path, out var schema);
        return new MlNetClassifier(ml, model, schema);
    }
}
