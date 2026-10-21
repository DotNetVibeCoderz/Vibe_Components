using Gravicode.Science.GraviLearn;
using Gravicode.Science.GraviLearn.Anomaly;
using Gravicode.Science.GraviLearn.Clustering;
using Gravicode.Science.GraviLearn.Decomposition;
using Gravicode.Science.GraviLearn.Preprocessing;
using Gravicode.Science.GraviNum;

namespace Blazor.AI.Analytics;

public enum ClusteringMethod
{
    KMeans,
    GaussianMixture,
    Agglomerative,
    Dbscan,
}

public sealed record ClusteringResult(int[] Labels, int Clusters, double Silhouette, double[,] Projection2D, IReadOnlyList<(int K, double Inertia)> Elbow, IReadOnlyList<IReadOnlyDictionary<string, double>> Profiles, int Noise);

public enum ReductionMethod
{
    Pca,
    TSne,
    Lda,
}

public sealed record ReductionResult(double[,] Points, IReadOnlyList<double> ExplainedVariance, IReadOnlyList<string> Axes, IReadOnlyList<(string Feature, double PC1, double PC2)> Loadings);

/// <summary>Clustering, dimensionality reduction and anomaly detection over GraviLearn.</summary>
public static class UnsupervisedMl
{
    public static ClusteringResult Cluster(AnalyticsDataset dataset, ClusteringMethod method, int k = 4, double epsilon = 0.6, IReadOnlyList<string>? features = null)
    {
        var names = (features ?? dataset.FeatureColumns).ToArray();
        var raw = dataset.Features(names);
        var x = new StandardScaler().FitTransform(raw);

        NdArray labels = method switch
        {
            ClusteringMethod.GaussianMixture => new GaussianMixture(components: k, seed: 1).FitPredict(x),
            ClusteringMethod.Agglomerative => new AgglomerativeClustering(clusters: k, linkage: Linkage.Average).FitPredict(x),
            ClusteringMethod.Dbscan => new Dbscan(epsilon: epsilon, minSamples: 5).FitPredict(x),
            _ => new KMeans(clusters: k, restarts: 4, seed: 1).FitPredict(x),
        };

        var ints = labels.ToArray().Select(v => (int)v).ToArray();
        var clusters = ints.Where(l => l >= 0).Distinct().Count();
        var silhouette = clusters >= 2 && ints.Any(l => l >= 0) ? Metrics.SilhouetteScore(x, labels) : 0;
        var elbow = method == ClusteringMethod.KMeans ? KMeans.ElbowCurve(x, maxK: 9, seed: 1) : [];

        var profiles = Enumerable.Range(0, clusters).Select(c =>
        {
            var rows = Enumerable.Range(0, ints.Length).Where(i => ints[i] == c).ToArray();
            var profile = new Dictionary<string, double> { ["size"] = rows.Length };
            for (var f = 0; f < names.Length; f++)
            {
                profile[names[f]] = rows.Length == 0 ? 0 : rows.Average(r => raw[r, f]);
            }

            return (IReadOnlyDictionary<string, double>)profile;
        }).ToArray();

        return new ClusteringResult(ints, clusters, silhouette, Project(x), elbow, profiles, ints.Count(l => l < 0));
    }

    public static ReductionResult Reduce(AnalyticsDataset dataset, ReductionMethod method, IReadOnlyList<string>? features = null, double perplexity = 25)
    {
        var names = (features ?? dataset.FeatureColumns).ToArray();
        var x = new StandardScaler().FitTransform(dataset.Features(names));
        switch (method)
        {
            case ReductionMethod.TSne:
                var tsne = new TStochasticNeighborEmbedding(components: 2, perplexity: perplexity, iterations: 500, seed: 1);
                return new ReductionResult(MlTrainer.ToMatrix(tsne.FitTransform(Sample(x, 400))), [], ["t-SNE 1", "t-SNE 2"], []);

            case ReductionMethod.Lda when dataset.Target is not null:
                var (y, _) = dataset.TargetVector();
                var lda = new LinearDiscriminantAnalysis(components: Math.Min(2, y.ToArray().Distinct().Count() - 1));
                var projected = lda.FitTransform(x, y);
                return new ReductionResult(Pad2D(projected), [], ["LD1", "LD2"], []);

            default:
                var components = Math.Min(names.Length, Math.Max(2, names.Length));
                var pca = new PCA(components);
                var points = pca.FitTransform(x);
                var loadings = names.Select((n, i) => (n, pca.ComponentVectors[0, i], pca.ComponentVectors.Rows > 1 ? pca.ComponentVectors[1, i] : 0)).ToArray();
                return new ReductionResult(Pad2D(points), pca.ExplainedVarianceRatio.ToArray(), ["PC1", "PC2"], loadings);
        }
    }

    /// <summary>One-class SVM over the chosen features: returns a flag per row and an anomaly score (higher = stranger).</summary>
    public static (bool[] Flags, double[] Scores) OneClassAnomalies(AnalyticsDataset dataset, IReadOnlyList<string> features, double nu = 0.03)
    {
        var x = new StandardScaler().FitTransform(dataset.Features(features));
        var svm = new OneClassSvm(nu: nu, kernel: SvmKernel.Rbf);
        svm.Fit(x);
        var decision = svm.DecisionFunction(x).ToArray();
        return (decision.Select(d => d < 0).ToArray(), decision.Select(d => -d).ToArray());
    }

    /// <summary>PCA to 2D for plotting.</summary>
    public static double[,] Project(NdArray scaled)
    {
        if (scaled.Columns <= 2)
        {
            return Pad2D(scaled);
        }

        return Pad2D(new PCA(2).FitTransform(scaled));
    }

    private static double[,] Pad2D(NdArray array)
    {
        var result = new double[array.Rows, 2];
        for (var r = 0; r < array.Rows; r++)
        {
            result[r, 0] = array[r, 0];
            result[r, 1] = array.Columns > 1 ? array[r, 1] : 0;
        }

        return result;
    }

    private static NdArray Sample(NdArray x, int max)
    {
        if (x.Rows <= max)
        {
            return x;
        }

        var rows = Enumerable.Range(0, max).Select(i => x.Row(i * x.Rows / max).ToArray()).ToList();
        return NdArray.FromRows(rows);
    }
}
