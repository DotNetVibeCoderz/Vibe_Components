using Agritech.Core.Data;

namespace Agritech.AI;

/// <summary>Standardised ridge (L2) linear regression solved in closed form. Small, fast and explainable.</summary>
public sealed class RidgeRegression(double lambda = 1.0)
{
    private double[] _mean = [], _std = [], _w = [];
    private double _bias;

    public double Lambda { get; } = lambda;
    public IReadOnlyList<double> Coefficients => _w;
    public double Intercept => _bias;
    public bool IsFitted => _w.Length > 0;

    public RidgeRegression Fit(double[][] x, double[] y)
    {
        int n = x.Length, p = x[0].Length;
        if (n != y.Length || n == 0) throw new ArgumentException("X and y must have the same non-zero length.");
        _mean = new double[p]; _std = new double[p];
        for (int j = 0; j < p; j++)
        {
            var col = x.Select(r => r[j]).ToArray();
            _mean[j] = Stats.Mean(col);
            _std[j] = Stats.StdDev(col) is var s && s > 1e-12 ? s : 1;
        }
        double yMean = Stats.Mean(y);
        var a = new Matrix(p, p);
        var b = new double[p];
        Span<double> z = p <= 64 ? stackalloc double[p] : new double[p];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < p; j++) z[j] = (x[i][j] - _mean[j]) / _std[j];
            for (int j = 0; j < p; j++)
            {
                b[j] += z[j] * (y[i] - yMean);
                for (int k = 0; k < p; k++) a[j, k] += z[j] * z[k];
            }
        }
        for (int j = 0; j < p; j++) a[j, j] += Lambda;
        var wz = Matrix.Solve(a, b);
        _w = new double[p];
        _bias = yMean;
        for (int j = 0; j < p; j++) { _w[j] = wz[j] / _std[j]; _bias -= _w[j] * _mean[j]; }
        return this;
    }

    public double Predict(ReadOnlySpan<double> features)
    {
        if (!IsFitted) throw new InvalidOperationException("Model is not fitted.");
        double s = _bias;
        for (int j = 0; j < _w.Length; j++) s += _w[j] * features[j];
        return s;
    }

    /// <summary>Standardised importance (|β| × σ) per feature.</summary>
    public double[] FeatureImportance() => _w.Select((w, j) => Math.Abs(w * _std[j])).ToArray();
}

public sealed record ModelEvaluation(double Rmse, double Mae, double R2, int Folds);

/// <summary>Yield record used to train the forecaster.</summary>
public sealed record YieldObservation(double SeasonRainMm, double MeanTempC, double PeakNdvi, double NitrogenKgHa, double SolarRadMj, double YieldTonHa);

/// <summary>
/// Season-level yield forecaster (features: rainfall, temperature, peak NDVI, N rate, radiation, with a temperature
/// quadratic term to capture the optimum). Includes k-fold cross-validation.
/// </summary>
public sealed class YieldForecaster(double lambda = 0.5)
{
    private RidgeRegression? _model;

    public static readonly string[] FeatureNames = ["rain", "temp", "temp²", "peakNdvi", "nitrogen", "radiation"];

    public static double[] Features(YieldObservation o) =>
        [o.SeasonRainMm, o.MeanTempC, o.MeanTempC * o.MeanTempC, o.PeakNdvi, o.NitrogenKgHa, o.SolarRadMj];

    public YieldForecaster Train(IReadOnlyList<YieldObservation> data)
    {
        _model = new RidgeRegression(lambda).Fit(data.Select(Features).ToArray(), data.Select(d => d.YieldTonHa).ToArray());
        return this;
    }

    public double Predict(YieldObservation o) =>
        Math.Max(0, (_model ?? throw new InvalidOperationException("Train first.")).Predict(Features(o)));

    public IReadOnlyDictionary<string, double> Importance()
    {
        var imp = (_model ?? throw new InvalidOperationException("Train first.")).FeatureImportance();
        double total = imp.Sum();
        return FeatureNames.Zip(imp).ToDictionary(p => p.First, p => Math.Round(p.Second / total * 100, 1));
    }

    public static ModelEvaluation CrossValidate(IReadOnlyList<YieldObservation> data, int folds = 5, double lambda = 0.5, int seed = 1)
    {
        var rng = new Random(seed);
        var idx = Enumerable.Range(0, data.Count).OrderBy(_ => rng.Next()).ToArray();
        var actual = new List<double>(); var pred = new List<double>();
        for (int f = 0; f < folds; f++)
        {
            var test = idx.Where((_, i) => i % folds == f).Select(i => data[i]).ToList();
            var train = idx.Where((_, i) => i % folds != f).Select(i => data[i]).ToList();
            if (test.Count == 0 || train.Count < 3) continue;
            var m = new YieldForecaster(lambda).Train(train);
            foreach (var t in test) { actual.Add(t.YieldTonHa); pred.Add(m.Predict(t)); }
        }
        var a = actual.ToArray(); var p = pred.ToArray();
        double mae = a.Zip(p).Average(z => Math.Abs(z.First - z.Second));
        double mean = Stats.Mean(a);
        double ssRes = a.Zip(p).Sum(z => (z.First - z.Second) * (z.First - z.Second));
        double ssTot = a.Sum(v => (v - mean) * (v - mean));
        return new ModelEvaluation(Math.Round(Stats.Rmse(a, p), 3), Math.Round(mae, 3), Math.Round(1 - ssRes / ssTot, 3), folds);
    }

    /// <summary>Synthetic multi-season dataset with agronomically realistic response curves (for demos/tests).</summary>
    public static List<YieldObservation> SyntheticDataset(int n = 120, int seed = 11, double potential = 8)
    {
        var rng = new Random(seed);
        var list = new List<YieldObservation>(n);
        for (int i = 0; i < n; i++)
        {
            double rain = 600 + rng.NextDouble() * 1400, temp = 23 + rng.NextDouble() * 8, ndvi = 0.45 + rng.NextDouble() * 0.4;
            double nRate = rng.NextDouble() * 180, rad = 15 + rng.NextDouble() * 8;
            double y = potential
                * Math.Min(1, rain / 1200)
                * Math.Exp(-Math.Pow((temp - 27) / 6, 2))
                * (0.4 + 0.7 * (ndvi - 0.45) / 0.4)
                * (0.6 + 0.4 * (1 - Math.Exp(-nRate / 70)))
                * (0.85 + 0.15 * (rad - 15) / 8)
                + rng.NextGaussian(0, 0.25);
            list.Add(new YieldObservation(Math.Round(rain), Math.Round(temp, 1), Math.Round(ndvi, 3), Math.Round(nRate), Math.Round(rad, 1), Math.Round(Math.Max(0.3, y), 2)));
        }
        return list;
    }
}
