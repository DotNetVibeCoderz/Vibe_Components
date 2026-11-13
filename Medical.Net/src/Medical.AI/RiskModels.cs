using Medical.Clinical;
using Medical.Core;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;

namespace Medical.AI;

/// <summary>A feature's contribution to a linear risk model's log-odds (weight × standardised value).</summary>
public sealed record FeatureContribution(string Feature, double Value, double Contribution);

public sealed record RiskPrediction(string Model, double Probability, string Band, IReadOnlyList<FeatureContribution> Drivers, Localized Interpretation);

public sealed record RiskModelMetrics(double Auc, double Accuracy, double F1, double LogLoss, int TrainRows, int TestRows);

/// <summary>
/// ML.NET L-BFGS logistic-regression risk model over standardised features. Features are standardised in-house so the
/// learned weights translate directly into per-patient explanations. Trained on deterministic synthetic cohorts
/// (reproducible); retrain on local data before any clinical use.
/// </summary>
public abstract class LinearRiskModel
{
    private sealed class Row
    {
        [VectorType] public float[] Features { get; set; } = [];
        public bool Label { get; set; }
    }

    private sealed class Score
    {
        public float Probability { get; set; }
    }

    private readonly MLContext _ml = new(seed: 7);
    private ITransformer? _model;
    private PredictionEngine<Row, Score>? _engine;
    private double[] _mean = [], _sd = [];
    private double[] _weights = [];
    private readonly Lock _gate = new();

    public abstract string Name { get; }
    public abstract string[] FeatureNames { get; }
    public RiskModelMetrics? Metrics { get; private set; }
    public IReadOnlyList<double> Weights => _weights;

    /// <summary>Generates a labelled synthetic cohort (features + outcome).</summary>
    protected abstract IEnumerable<(double[] X, bool Y)> Cohort(int n, ulong seed);

    protected abstract (string Band, Localized Text) Interpret(double p);

    /// <summary>Trains on <paramref name="rows"/> synthetic rows with an 80/20 split and records test metrics.</summary>
    public RiskModelMetrics Train(int rows = 4000, ulong seed = 11)
    {
        var data = Cohort(rows, seed).ToArray();
        int f = FeatureNames.Length;
        _mean = new double[f]; _sd = new double[f];
        for (int j = 0; j < f; j++)
        {
            _mean[j] = data.Average(r => r.X[j]);
            double var = data.Sum(r => (r.X[j] - _mean[j]) * (r.X[j] - _mean[j])) / Math.Max(1, data.Length - 1);
            _sd[j] = var > 1e-12 ? Math.Sqrt(var) : 1;
        }
        var all = _ml.Data.LoadFromEnumerable(data.Select(r => new Row { Features = Standardise(r.X), Label = r.Y }),
            SchemaDefinition.Create(typeof(Row)).Also(s => s[nameof(Row.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, f)));
        var split = _ml.Data.TrainTestSplit(all, 0.2, seed: 3);
        var trainer = _ml.BinaryClassification.Trainers.LbfgsLogisticRegression(new LbfgsLogisticRegressionBinaryTrainer.Options
        {
            LabelColumnName = nameof(Row.Label), FeatureColumnName = nameof(Row.Features), L2Regularization = 0.01f, L1Regularization = 0f,
        });
        var transformer = trainer.Fit(split.TrainSet);
        var m = _ml.BinaryClassification.Evaluate(transformer.Transform(split.TestSet), nameof(Row.Label));
        lock (_gate)
        {
            _model = transformer;
            _weights = transformer.Model.SubModel.Weights.Select(w => (double)w).ToArray();
            _engine = _ml.Model.CreatePredictionEngine<Row, Score>(transformer, inputSchemaDefinition:
                SchemaDefinition.Create(typeof(Row)).Also(s => s[nameof(Row.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, f)));
        }
        int test = split.TestSet.GetColumn<bool>(nameof(Row.Label)).Count();
        Metrics = new RiskModelMetrics(Math.Round(m.AreaUnderRocCurve, 3), Math.Round(m.Accuracy, 3), Math.Round(m.F1Score, 3), Math.Round(m.LogLoss, 3), data.Length - test, test);
        return Metrics;
    }

    /// <summary>Probability of the outcome plus the top drivers (largest absolute contributions).</summary>
    public RiskPrediction Predict(double[] x)
    {
        if (x.Length != FeatureNames.Length) throw new ArgumentException($"Expected {FeatureNames.Length} features.", nameof(x));
        if (_engine is null) Train();
        var z = Standardise(x);
        float p;
        lock (_gate) p = _engine!.Predict(new Row { Features = z }).Probability;
        var drivers = FeatureNames.Select((n, i) => new FeatureContribution(n, x[i], Math.Round(_weights[i] * z[i], 3)))
            .OrderByDescending(c => Math.Abs(c.Contribution)).ToArray();
        var (band, text) = Interpret(p);
        return new RiskPrediction(Name, Math.Round(p, 3), band, drivers, text);
    }

    /// <summary>Saves the ML.NET model (zip) — standardisation parameters are stored alongside as a .stats file.</summary>
    public void Save(string path)
    {
        if (_model is null) throw new InvalidOperationException("Train the model first.");
        _ml.Model.Save(_model, null, path);
        File.WriteAllLines(path + ".stats", [string.Join(',', _mean), string.Join(',', _sd)]);
    }

    private float[] Standardise(double[] x)
    {
        var z = new float[x.Length];
        for (int i = 0; i < x.Length; i++) z[i] = (float)((x[i] - _mean[i]) / _sd[i]);
        return z;
    }

    protected static double Sigmoid(double v) => 1 / (1 + Math.Exp(-v));
}

/// <summary>
/// 48-hour clinical-deterioration (ICU transfer / rapid response) risk from NEWS2, qSOFA, lactate, WBC, CRP, eGFR, age
/// and comorbidity count.
/// </summary>
public sealed class DeteriorationRiskModel : LinearRiskModel
{
    public override string Name => "Deterioration48h";
    public override string[] FeatureNames { get; } = ["age", "news2", "qsofa", "lactate", "wbc", "crp", "egfr", "comorbidities"];

    protected override IEnumerable<(double[] X, bool Y)> Cohort(int n, ulong seed)
    {
        var r = new SeededRandom(seed);
        for (int i = 0; i < n; i++)
        {
            double sick = Math.Pow(r.NextDouble(), 1.6); // most patients are well
            double age = Math.Clamp(r.Gaussian(55 + 15 * sick, 16), 18, 98);
            double news2 = Math.Clamp(Math.Round(sick * 12 + r.Gaussian(0, 1.6)), 0, 20);
            double qsofa = Math.Clamp(Math.Round(sick * 2.6 + r.Gaussian(0, 0.5)), 0, 3);
            double lactate = Math.Max(0.4, 1 + sick * 3.5 + r.Gaussian(0, 0.5));
            double wbc = Math.Max(1, 7 + sick * 9 + r.Gaussian(0, 2.5));
            double crp = Math.Max(0, 5 + sick * 150 + r.Gaussian(0, 25));
            double egfr = Math.Clamp(95 - age * 0.4 - sick * 35 + r.Gaussian(0, 12), 5, 130);
            double comorb = Math.Clamp(Math.Round(age / 30 + sick * 2 + r.Gaussian(0, 0.8)), 0, 8);
            double logit = -7.2 + 0.36 * news2 + 0.55 * qsofa + 0.55 * lactate + 0.012 * crp + 0.02 * age - 0.012 * egfr + 0.15 * comorb;
            yield return ([age, news2, qsofa, lactate, wbc, crp, egfr, comorb], r.NextDouble() < Sigmoid(logit));
        }
    }

    protected override (string, Localized) Interpret(double p) => p switch
    {
        >= 0.5 => ("High", new("High 48-h deterioration risk — senior review and consider critical-care referral.", "Risiko perburukan 48 jam tinggi — evaluasi dokter senior, pertimbangkan rujukan ICU.")),
        >= 0.2 => ("Medium", new("Moderate risk — increase monitoring frequency and reassess within 4 h.", "Risiko sedang — tingkatkan frekuensi pemantauan, evaluasi ulang dalam 4 jam.")),
        _ => ("Low", new("Low risk — routine monitoring.", "Risiko rendah — pemantauan rutin.")),
    };

    /// <summary>Feature vector for a patient record (latest vitals and labs).</summary>
    public RiskPrediction Predict(PatientRecord record, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(record.LatestVitals?.Timestamp.UtcDateTime ?? DateTime.UtcNow);
        int age = record.Patient.AgeAt(day);
        var v = record.LatestVitals ?? new VitalSigns { RespiratoryRate = 16, SpO2 = 98, SystolicBp = 120, HeartRate = 75, TemperatureC = 36.8 };
        double cr = record.Latest(Loinc.Creatinine)?.Value ?? 0.9;
        return Predict([
            age, ClinicalScores.News2(v).Score, ClinicalScores.QSofa(v).Score, record.Latest(Loinc.Lactate)?.Value ?? 1.0,
            record.Latest(Loinc.Wbc)?.Value ?? 7, record.Latest(Loinc.Crp)?.Value ?? 5,
            Calculators.EgfrCkdEpi2021(cr, age, record.Patient.Gender).Egfr, record.Conditions.Count,
        ]);
    }
}

/// <summary>Undiagnosed type 2 diabetes risk (FINDRISC-inspired): age, BMI, waist, blood pressure, activity, diet, family history, hypertension treatment.</summary>
public sealed class DiabetesRiskModel : LinearRiskModel
{
    public override string Name => "Type2Diabetes";
    public override string[] FeatureNames { get; } = ["age", "bmi", "waist_cm", "systolic_bp", "active_30min_daily", "daily_vegetables", "family_history", "antihypertensive"];

    protected override IEnumerable<(double[] X, bool Y)> Cohort(int n, ulong seed)
    {
        var r = new SeededRandom(seed + 101);
        for (int i = 0; i < n; i++)
        {
            double age = Math.Clamp(r.Gaussian(47, 13), 20, 85);
            double bmi = Math.Clamp(r.Gaussian(25, 4.5), 16, 45);
            double waist = Math.Clamp(bmi * 3.4 + r.Gaussian(0, 6), 55, 150);
            double sbp = Math.Clamp(105 + age * 0.45 + (bmi - 22) * 0.9 + r.Gaussian(0, 12), 90, 210);
            double active = r.Chance(0.45) ? 1 : 0, veg = r.Chance(0.5) ? 1 : 0, family = r.Chance(0.3) ? 1 : 0, antiHtn = sbp > 145 && r.Chance(0.6) ? 1 : 0;
            double logit = -9.6 + 0.055 * age + 0.13 * bmi + 0.025 * waist + 0.006 * sbp - 0.45 * active - 0.25 * veg + 1.0 * family + 0.5 * antiHtn;
            yield return ([age, bmi, waist, sbp, active, veg, family, antiHtn], r.NextDouble() < Sigmoid(logit));
        }
    }

    protected override (string, Localized) Interpret(double p) => p switch
    {
        >= 0.33 => ("High", new("High risk of undiagnosed type 2 diabetes — check fasting glucose / HbA1c.", "Risiko tinggi DM tipe 2 yang belum terdiagnosis — periksa GDP / HbA1c.")),
        >= 0.15 => ("Moderate", new("Moderate risk — lifestyle counselling and screening within a year.", "Risiko sedang — konseling gaya hidup dan skrining dalam 1 tahun.")),
        _ => ("Low", new("Low risk — healthy lifestyle advice.", "Risiko rendah — anjuran gaya hidup sehat.")),
    };
}

internal static class FluentExt
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
