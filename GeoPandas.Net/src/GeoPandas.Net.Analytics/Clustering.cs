using GeoPandas.Net.Geometry.Index;

namespace GeoPandas.Net.Analytics;

/// <summary>Spatial clustering of point data.</summary>
public static class Clustering
{
    /// <summary>DBSCAN (Ester et al.). Returns a label per point; -1 = noise. Uses a KD-tree for region queries.</summary>
    public static int[] Dbscan(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, double eps, int minPoints)
    {
        int n = xs.Length;
        var tree = new KdTree(xs, ys);
        var x = xs.ToArray(); var y = ys.ToArray();
        var labels = Enumerable.Repeat(-2, n).ToArray(); // -2 unvisited
        int cluster = 0;
        var buffer = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (labels[i] != -2) continue;
            buffer.Clear();
            tree.RadiusInto(x[i], y[i], eps, buffer);
            if (buffer.Count < minPoints) { labels[i] = -1; continue; }
            labels[i] = cluster;
            var queue = new Queue<int>(buffer);
            while (queue.Count > 0)
            {
                int j = queue.Dequeue();
                if (labels[j] == -1) labels[j] = cluster;     // border point
                if (labels[j] != -2) continue;
                labels[j] = cluster;
                var nb = tree.Radius(x[j], y[j], eps);
                if (nb.Count >= minPoints) foreach (var k in nb) if (labels[k] is -2 or -1) queue.Enqueue(k);
            }
            cluster++;
        }
        return labels;
    }

    /// <summary>DBSCAN on a GeoDataFrame's (representative) points, adding a <c>cluster</c> column.</summary>
    public static GeoDataFrame Dbscan(GeoDataFrame gdf, double eps, int minPoints, string column = "cluster")
    {
        var (xs, ys) = SpatialWeights.Points(gdf.Geometry);
        return gdf.WithColumn(Column.Create(column, Dbscan(xs, ys, eps, minPoints).Select(v => (long)v).ToArray()));
    }

    /// <summary>K-Means with k-means++ seeding (Lloyd iterations). Returns labels and centroids.</summary>
    public static (int[] Labels, (double X, double Y)[] Centroids, double Inertia) KMeans(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int k, int maxIterations = 300, int seed = 42)
    {
        int n = xs.Length;
        if (k <= 0 || k > n) throw new ArgumentOutOfRangeException(nameof(k));
        var x = xs.ToArray(); var y = ys.ToArray();
        var rng = new Random(seed);
        var cx = new double[k]; var cy = new double[k];
        // k-means++
        int first = rng.Next(n);
        cx[0] = x[first]; cy[0] = y[first];
        var d2 = new double[n];
        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < c; j++) best = Math.Min(best, (x[i] - cx[j]) * (x[i] - cx[j]) + (y[i] - cy[j]) * (y[i] - cy[j]));
                d2[i] = best; total += best;
            }
            double r = rng.NextDouble() * total, acc = 0;
            int pick = n - 1;
            for (int i = 0; i < n; i++) { acc += d2[i]; if (acc >= r) { pick = i; break; } }
            cx[c] = x[pick]; cy[c] = y[pick];
        }

        var labels = new int[n];
        double inertia = 0;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;
            inertia = 0;
            for (int i = 0; i < n; i++)
            {
                int best = 0; double bd = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double d = (x[i] - cx[c]) * (x[i] - cx[c]) + (y[i] - cy[c]) * (y[i] - cy[c]);
                    if (d < bd) { bd = d; best = c; }
                }
                if (labels[i] != best || iter == 0) { changed |= labels[i] != best; labels[i] = best; }
                inertia += bd;
            }
            var sx = new double[k]; var sy = new double[k]; var cnt = new int[k];
            for (int i = 0; i < n; i++) { sx[labels[i]] += x[i]; sy[labels[i]] += y[i]; cnt[labels[i]]++; }
            for (int c = 0; c < k; c++) if (cnt[c] > 0) { cx[c] = sx[c] / cnt[c]; cy[c] = sy[c] / cnt[c]; }
            if (!changed && iter > 0) break;
        }
        return (labels, Enumerable.Range(0, k).Select(c => (cx[c], cy[c])).ToArray(), inertia);
    }

    public static GeoDataFrame KMeans(GeoDataFrame gdf, int k, string column = "cluster", int seed = 42)
    {
        var (xs, ys) = SpatialWeights.Points(gdf.Geometry);
        var (labels, _, _) = KMeans(xs, ys, k, seed: seed);
        return gdf.WithColumn(Column.Create(column, labels.Select(v => (long)v).ToArray()));
    }
}
