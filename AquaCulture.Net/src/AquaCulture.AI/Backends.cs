using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AquaCulture.AI;

/// <summary>A growth record for ML.NET training (one weekly observation).</summary>
public sealed class GrowthRecord
{
    public float RelWeight { get; set; }
    public float TempDeviation { get; set; }
    public float DoRatio { get; set; }
    public float Nh3Ratio { get; set; }
    public float DensityRatio { get; set; }
    public float FeedRatio { get; set; }
    /// <summary>Label: daily growth (% body weight).</summary>
    public float Label { get; set; }
}

public sealed class GrowthScore
{
    [ColumnName("Score")] public float Score { get; set; }
}

/// <summary>ML.NET backend: trains an SDCA regression on growth records, evaluates it and saves/loads the model (.zip).</summary>
public sealed class MlNetGrowthModel
{
    private readonly MLContext _ml = new(seed: 1);
    private ITransformer? _model;
    private DataViewSchema? _schema;

    public double RSquared { get; private set; }
    public double Rmse { get; private set; }

    private static readonly string[] Features = [nameof(GrowthRecord.RelWeight), nameof(GrowthRecord.TempDeviation), nameof(GrowthRecord.DoRatio),
        nameof(GrowthRecord.Nh3Ratio), nameof(GrowthRecord.DensityRatio), nameof(GrowthRecord.FeedRatio)];

    public MlNetGrowthModel Train(IEnumerable<GrowthRecord> records)
    {
        var data = _ml.Data.LoadFromEnumerable(records);
        var split = _ml.Data.TrainTestSplit(data, 0.2, seed: 1);
        var pipeline = _ml.Transforms.Concatenate("Features", Features)
            .Append(_ml.Transforms.NormalizeMeanVariance("Features"))
            .Append(_ml.Regression.Trainers.Sdca(labelColumnName: "Label", featureColumnName: "Features", maximumNumberOfIterations: 200));
        _model = pipeline.Fit(split.TrainSet);
        _schema = data.Schema;
        var metrics = _ml.Regression.Evaluate(_model.Transform(split.TestSet));
        RSquared = Math.Round(metrics.RSquared, 3); Rmse = Math.Round(metrics.RootMeanSquaredError, 4);
        return this;
    }

    /// <summary>Converts the synthetic AquaGrowth dataset into ML.NET records.</summary>
    public static IEnumerable<GrowthRecord> FromDataset(Ml.Dataset d) => d.X.Select((x, i) => new GrowthRecord
    {
        RelWeight = (float)x[0], TempDeviation = (float)x[1], DoRatio = (float)x[2], Nh3Ratio = (float)x[3], DensityRatio = (float)x[4], FeedRatio = (float)x[5], Label = (float)d.Y[i],
    });

    public float Predict(GrowthRecord r)
    {
        if (_model is null) throw new InvalidOperationException("Train or load a model first.");
        return _ml.Model.CreatePredictionEngine<GrowthRecord, GrowthScore>(_model).Predict(r).Score;
    }

    public void Save(string path) => _ml.Model.Save(_model ?? throw new InvalidOperationException("No model."), _schema, path);

    public MlNetGrowthModel Load(string path) { _model = _ml.Model.Load(path, out _schema); return this; }
}

/// <summary>
/// ONNX Runtime backend for tabular models exported from scikit-learn, XGBoost, LightGBM, PyTorch or ML.NET
/// (input: a float tensor [1, features]; output: the first float tensor).
/// </summary>
public sealed class OnnxTabularModel : IDisposable
{
    private readonly InferenceSession _session;

    public OnnxTabularModel(string modelPath, SessionOptions? options = null) => _session = new InferenceSession(modelPath, options ?? new SessionOptions());
    public OnnxTabularModel(byte[] model, SessionOptions? options = null) => _session = new InferenceSession(model, options ?? new SessionOptions());

    public string InputName => _session.InputMetadata.Keys.First();
    public IReadOnlyList<string> OutputNames => _session.OutputMetadata.Keys.ToList();

    public float[] Predict(ReadOnlySpan<float> features)
    {
        var tensor = new DenseTensor<float>(features.ToArray(), [1, features.Length]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(InputName, tensor)]);
        return results.First(r => r.Value is Tensor<float>).AsEnumerable<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
