using Blazor.AI.Analytics;
using Gravicode.Science.GraviLearn;
using Gravicode.Science.GraviLearn.Clustering;
using Gravicode.Science.GraviLearn.Preprocessing;
using Gravicode.Science.GraviNum;

namespace Blazor.AI.ThreeD;

/// <summary>Clustering algorithms offered by <c>AIClusterExplorer</c>.</summary>
public enum ClusterAlgorithm
{
    KMeans,
    Dbscan,
    Hdbscan,
    Hierarchical,
}

/// <summary>A clustered dataset projected to 3-D: labels, positions, centroids and per-cluster feature means.</summary>
public sealed record ClusterLayout(
    string Dataset,
    IReadOnlyList<string> Features,
    int[] Labels,
    double[][] Points,
    int Clusters,
    int Noise,
    double Silhouette,
    IReadOnlyList<ClusterSummary> Summaries);

/// <summary>One cluster: its 3-D centroid, size, and the mean and z-score of each feature.</summary>
public sealed record ClusterSummary(int Id, int Size, double[] Centroid, IReadOnlyDictionary<string, double> Means, IReadOnlyDictionary<string, double> ZScores)
{
    /// <summary>A short local name from the most distinctive features, e.g. "High avgBasket · low discountShare".</summary>
    public string AutoName => string.Join(" · ", ZScores.OrderByDescending(z => Math.Abs(z.Value)).Take(2)
        .Select(z => $"{(z.Value >= 0 ? "high" : "low")} {z.Key}")) is { Length: > 0 } n ? char.ToUpperInvariant(n[0]) + n[1..] : $"Cluster {Id + 1}";
}

/// <summary>Runs GraviLearn clustering on standardised features and lays the rows out in 3-D with PCA.</summary>
public static class ClusterEngine
{
    public static IReadOnlyList<(string Name, Func<AnalyticsDataset> Create)> Datasets { get; } =
    [
        ("Customer segments", SampleDatasets.Segments),
        ("Crop yield (farms)", SampleDatasets.CropYield),
        ("Machine sensors", SampleDatasets.MachineSensor),
        ("House prices", SampleDatasets.HousePrices),
    ];

    public static ClusterLayout Run(AnalyticsDataset dataset, ClusterAlgorithm algorithm, int k = 4, double epsilon = 0.8, int minClusterSize = 12)
    {
        var names = dataset.FeatureColumns.ToArray();
        var raw = dataset.Features(names);
        var x = new StandardScaler().FitTransform(raw);
        var rows = raw.Shape[0];
        var cols = raw.Shape[1];

        NdArray labels = algorithm switch
        {
            ClusterAlgorithm.Dbscan => new Dbscan(epsilon: epsilon, minSamples: 5).FitPredict(x),
            ClusterAlgorithm.Hdbscan => new Hdbscan(minClusterSize: Math.Max(3, minClusterSize)).FitPredict(x),
            ClusterAlgorithm.Hierarchical => new AgglomerativeClustering(clusters: Math.Max(2, k), linkage: Linkage.Average).FitPredict(x),
            _ => new KMeans(clusters: Math.Max(2, k), restarts: 4, seed: 1).FitPredict(x),
        };

        var ints = labels.ToArray().Select(v => (int)v).ToArray();
        var clusterIds = ints.Where(l => l >= 0).Distinct().Order().ToArray();
        double silhouette = 0;
        if (clusterIds.Length >= 2)
        {
            try
            {
                silhouette = Metrics.SilhouetteScore(x, labels);
            }
            catch (ArgumentException)
            {
            }
        }

        var scaledRows = new double[rows][];
        for (var r = 0; r < rows; r++)
        {
            scaledRows[r] = new double[cols];
            for (var c = 0; c < cols; c++)
            {
                scaledRows[r][c] = x[r, c];
            }
        }

        var points = Layout3D.Normalize(cols == 3 ? scaledRows : Layout3D.Pca3(scaledRows), 3.2);
        // Lift so the cloud sits above the grid.
        foreach (var p in points)
        {
            p[1] += 3.4;
        }

        var means = new double[cols];
        var sds = new double[cols];
        for (var c = 0; c < cols; c++)
        {
            var col = Enumerable.Range(0, rows).Select(r => raw[r, c]).ToArray();
            means[c] = col.Average();
            sds[c] = Math.Sqrt(col.Sum(v => (v - means[c]) * (v - means[c])) / Math.Max(1, rows - 1));
        }

        var summaries = clusterIds.Select(id =>
        {
            var members = Enumerable.Range(0, rows).Where(i => ints[i] == id).ToArray();
            var centroid = new double[3];
            foreach (var m in members)
            {
                for (var d = 0; d < 3; d++)
                {
                    centroid[d] += points[m][d] / members.Length;
                }
            }

            var fm = new Dictionary<string, double>();
            var fz = new Dictionary<string, double>();
            for (var c = 0; c < cols; c++)
            {
                var mean = members.Average(m => raw[m, c]);
                fm[names[c]] = mean;
                fz[names[c]] = sds[c] > 1e-12 ? (mean - means[c]) / sds[c] : 0;
            }

            return new ClusterSummary(id, members.Length, centroid, fm, fz);
        }).ToArray();

        return new ClusterLayout(dataset.Name, names, ints, points, clusterIds.Length, ints.Count(l => l < 0), silhouette, summaries);
    }
}
