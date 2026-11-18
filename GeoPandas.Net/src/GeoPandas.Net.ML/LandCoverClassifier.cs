using Microsoft.ML;
using Microsoft.ML.Data;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.ML;

public enum LandCoverAlgorithm
{
    /// <summary>Random forest (one-versus-all FastForest).</summary>
    RandomForest,
    /// <summary>Gradient-boosted trees (one-versus-all FastTree).</summary>
    GradientBoosting,
    /// <summary>Multinomial logistic regression (SDCA maximum entropy).</summary>
    MaximumEntropy,
}

/// <summary>Accuracy assessment of a classification: confusion matrix (rows = reference, columns = predicted), overall accuracy and Cohen's kappa.</summary>
public sealed record AccuracyReport(IReadOnlyList<string> Labels, long[,] Confusion, double OverallAccuracy, double Kappa)
{
    public double ProducerAccuracy(int k) { long row = 0; for (int j = 0; j < Labels.Count; j++) row += Confusion[k, j]; return row == 0 ? double.NaN : (double)Confusion[k, k] / row; }
    public double UserAccuracy(int k) { long col = 0; for (int i = 0; i < Labels.Count; i++) col += Confusion[i, k]; return col == 0 ? double.NaN : (double)Confusion[k, k] / col; }

    public DataFrame ToFrame()
    {
        var cols = new List<Column> { Column.Create("reference", Labels.ToArray()) };
        for (int j = 0; j < Labels.Count; j++) { int jj = j; cols.Add(Column.Create(Labels[j], Enumerable.Range(0, Labels.Count).Select(i => Confusion[i, jj]).ToArray())); }
        cols.Add(Column.Create("producer_accuracy", Enumerable.Range(0, Labels.Count).Select(ProducerAccuracy).ToArray()));
        cols.Add(Column.Create("user_accuracy", Enumerable.Range(0, Labels.Count).Select(UserAccuracy).ToArray()));
        return new DataFrame(cols.ToArray());
    }

    public override string ToString() => $"Overall accuracy {OverallAccuracy:P2}, kappa {Kappa:F4} ({Labels.Count} classes)";
}

/// <summary>
/// Pixel-based land-cover classification with ML.NET: training pixels are taken from labelled points or polygons over a multi-band raster,
/// a classifier (random forest, boosted trees or maximum entropy) is trained, and the whole raster is classified. Includes accuracy
/// assessment and model persistence.
/// </summary>
public sealed class LandCoverClassifier
{
    private sealed class Row
    {
        public string Label { get; set; } = "";
        public float[] Features { get; set; } = [];
    }

    private sealed class Prediction
    {
        [ColumnName("PredictedLabel")] public string Predicted { get; set; } = "";
    }

    private readonly MLContext _ml;
    private readonly ITransformer _model;
    private readonly int[] _bands;

    private LandCoverClassifier(MLContext ml, ITransformer model, int[] bands, IReadOnlyList<string> labels)
    {
        _ml = ml; _model = model; _bands = bands; Labels = labels;
    }

    /// <summary>Class labels in output order (the value of a class pixel is its index here).</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Training/test pixels from labelled points or polygons (at most <paramref name="maxPixelsPerFeature"/> per polygon).</summary>
    public static List<(string Label, float[] Features)> SamplePixels(RasterGrid image, GeoDataFrame samples, string labelColumn, int[] bands,
        int maxPixelsPerFeature = 500, int seed = 1)
    {
        var rng = new Random(seed);
        var rows = new List<(string, float[])>();
        float[]? Pixel(int r, int c)
        {
            var f = new float[bands.Length];
            for (int b = 0; b < bands.Length; b++)
            {
                float v = image.Bands[bands[b]][r * image.Width + c];
                if (float.IsNaN(v) || image.IsNoData(v)) return null;
                f[b] = v;
            }
            return f;
        }
        for (int i = 0; i < samples.RowCount; i++)
        {
            var g = samples.Geometry[i];
            var label = Convert.ToString(samples[labelColumn].GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            if (g is null || label is null) continue;
            if (g is Point p)
            {
                var (col, row) = image.Transform.WorldToPixel(p.X, p.Y);
                int r = (int)Math.Floor(row), c = (int)Math.Floor(col);
                if (r >= 0 && c >= 0 && r < image.Height && c < image.Width && Pixel(r, c) is { } f) rows.Add((label, f));
                continue;
            }
            var prepared = PreparedGeometryFactory.Prepare(g);
            var env = g.EnvelopeInternal;
            var (c0, r0) = image.Transform.WorldToPixel(env.MinX, env.MaxY);
            var (c1, r1) = image.Transform.WorldToPixel(env.MaxX, env.MinY);
            var inside = new List<(int, int)>();
            for (int r = Math.Max(0, (int)Math.Floor(Math.Min(r0, r1))); r <= Math.Min(image.Height - 1, (int)Math.Ceiling(Math.Max(r0, r1))); r++)
                for (int c = Math.Max(0, (int)Math.Floor(Math.Min(c0, c1))); c <= Math.Min(image.Width - 1, (int)Math.Ceiling(Math.Max(c0, c1))); c++)
                {
                    var (x, y) = image.Transform.PixelToWorld(c + 0.5, r + 0.5);
                    if (prepared.Contains(Geometry.Geo.Point(x, y))) inside.Add((r, c));
                }
            foreach (var (r, c) in inside.OrderBy(_ => rng.Next()).Take(maxPixelsPerFeature))
                if (Pixel(r, c) is { } f) rows.Add((label, f));
        }
        return rows;
    }

    public static LandCoverClassifier Train(RasterGrid image, GeoDataFrame samples, string labelColumn, int[]? bands = null,
        LandCoverAlgorithm algorithm = LandCoverAlgorithm.RandomForest, int seed = 1, int maxPixelsPerFeature = 500)
    {
        var b = bands ?? Enumerable.Range(0, image.BandCount).ToArray();
        return Train(SamplePixels(image, samples, labelColumn, b, maxPixelsPerFeature, seed), b, algorithm, seed);
    }

    /// <summary>Trains from pre-extracted pixel vectors.</summary>
    public static LandCoverClassifier Train(IReadOnlyList<(string Label, float[] Features)> pixels, int[] bands, LandCoverAlgorithm algorithm = LandCoverAlgorithm.RandomForest, int seed = 1)
    {
        if (pixels.Count == 0) throw new ArgumentException("No training pixels (samples outside the raster or on no-data?).");
        var ml = new MLContext(seed);
        var schema = SchemaDefinition.Create(typeof(Row));
        schema["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, bands.Length);
        var data = ml.Data.LoadFromEnumerable(pixels.Select(p => new Row { Label = p.Label, Features = p.Features }), schema);
        var labels = pixels.Select(p => p.Label).Distinct().Order(StringComparer.Ordinal).ToList();
        var key = ml.Transforms.Conversion.MapValueToKey("LabelKey", "Label", keyOrdinality: Microsoft.ML.Transforms.ValueToKeyMappingEstimator.KeyOrdinality.ByValue)
            .Append(ml.Transforms.NormalizeMinMax("Features"));
        var pipeline = algorithm switch
        {
            LandCoverAlgorithm.MaximumEntropy => key.Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy("LabelKey", "Features"))
                .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel")),
            LandCoverAlgorithm.GradientBoosting => key.Append(ml.MulticlassClassification.Trainers.OneVersusAll(ml.BinaryClassification.Trainers.FastTree(labelColumnName: "LabelKey", featureColumnName: "Features"), "LabelKey"))
                .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel")),
            _ => key.Append(ml.MulticlassClassification.Trainers.OneVersusAll(ml.BinaryClassification.Trainers.FastForest(labelColumnName: "LabelKey", featureColumnName: "Features", numberOfTrees: 100), "LabelKey"))
                .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel")),
        };
        var model = pipeline.Fit(data);
        return new LandCoverClassifier(ml, model, bands, labels);
    }

    /// <summary>Predicted labels of pixel vectors.</summary>
    public IReadOnlyList<string> PredictPixels(IEnumerable<float[]> pixels)
    {
        var schema = SchemaDefinition.Create(typeof(Row));
        schema["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, _bands.Length);
        var data = _ml.Data.LoadFromEnumerable(pixels.Select(p => new Row { Features = p }), schema);
        return _ml.Data.CreateEnumerable<Prediction>(_model.Transform(data), reuseRowObject: false).Select(p => p.Predicted).ToList();
    }

    /// <summary>Classifies every pixel; the value is the label index in <see cref="Labels"/> (no-data → NaN).</summary>
    public RasterGrid Predict(RasterGrid image)
    {
        int n = image.Width * image.Height;
        var valid = new List<int>(n);
        var vectors = new List<float[]>(n);
        for (int i = 0; i < n; i++)
        {
            var f = new float[_bands.Length];
            bool ok = true;
            for (int b = 0; b < _bands.Length; b++)
            {
                float v = image.Bands[_bands[b]][i];
                if (float.IsNaN(v) || image.IsNoData(v)) { ok = false; break; }
                f[b] = v;
            }
            if (ok) { valid.Add(i); vectors.Add(f); }
        }
        var predicted = PredictPixels(vectors);
        var index = Labels.Select((l, k) => (l, k)).ToDictionary(t => t.l, t => (float)t.k);
        var band = new float[n];
        Array.Fill(band, float.NaN);
        for (int j = 0; j < valid.Count; j++) band[valid[j]] = index[predicted[j]];
        return new RasterGrid(image.Width, image.Height, [band], image.Transform, image.Crs, float.NaN) { BandNames = ["land_cover"] };
    }

    /// <summary>Accuracy against reference pixels (from <see cref="SamplePixels"/>).</summary>
    public AccuracyReport Evaluate(IReadOnlyList<(string Label, float[] Features)> reference)
    {
        var predicted = PredictPixels(reference.Select(r => r.Features));
        var labels = Labels.Union(reference.Select(r => r.Label)).Distinct().Order(StringComparer.Ordinal).ToList();
        var ix = labels.Select((l, k) => (l, k)).ToDictionary(t => t.l, t => t.k);
        var m = new long[labels.Count, labels.Count];
        for (int i = 0; i < reference.Count; i++) m[ix[reference[i].Label], ix[predicted[i]]]++;
        double n = reference.Count, agree = 0, chance = 0;
        for (int k = 0; k < labels.Count; k++)
        {
            agree += m[k, k];
            long row = 0, col = 0;
            for (int j = 0; j < labels.Count; j++) { row += m[k, j]; col += m[j, k]; }
            chance += (double)row * col;
        }
        double po = agree / n, pe = chance / (n * n);
        return new AccuracyReport(labels, m, po, pe >= 1 ? 1 : (po - pe) / (1 - pe));
    }

    public void Save(string path) => _ml.Model.Save(_model, null, path);
}
