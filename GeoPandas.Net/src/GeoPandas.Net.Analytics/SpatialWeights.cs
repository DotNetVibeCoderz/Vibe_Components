using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Analytics;

/// <summary>
/// Sparse spatial weights matrix W (PySAL <c>libpysal.weights</c> equivalent).
/// Stored as adjacency lists; <see cref="RowStandardize"/> produces a row-stochastic matrix.
/// </summary>
public sealed class SpatialWeights
{
    public SpatialWeights(int[][] neighbors, double[][] weights, string kind)
    {
        if (neighbors.Length != weights.Length) throw new ArgumentException("neighbors/weights length mismatch.");
        Neighbors = neighbors;
        Weights = weights;
        Kind = kind;
    }

    public int[][] Neighbors { get; }
    public double[][] Weights { get; }
    public string Kind { get; }
    public bool IsRowStandardized { get; private init; }
    public int N => Neighbors.Length;

    /// <summary>Sum of all weights (S0).</summary>
    public double S0 => Weights.Sum(w => w.Sum());
    /// <summary>Observations without neighbours.</summary>
    public int[] Islands => Enumerable.Range(0, N).Where(i => Neighbors[i].Length == 0).ToArray();
    public double MeanNeighbors => Neighbors.Average(n => n.Length);

    public SpatialWeights RowStandardize()
    {
        var w = Weights.Select(row => { double s = row.Sum(); return s == 0 ? row.ToArray() : row.Select(v => v / s).ToArray(); }).ToArray();
        return new SpatialWeights(Neighbors, w, Kind) { IsRowStandardized = true };
    }

    /// <summary>Spatial lag W·y.</summary>
    public double[] Lag(ReadOnlySpan<double> y)
    {
        var lag = new double[N];
        for (int i = 0; i < N; i++)
        {
            double s = 0;
            var nb = Neighbors[i]; var wt = Weights[i];
            for (int k = 0; k < nb.Length; k++) s += wt[k] * y[nb[k]];
            lag[i] = s;
        }
        return lag;
    }

    /// <summary>Queen contiguity: polygons sharing at least one boundary point.</summary>
    public static SpatialWeights Queen(GeoDataFrame gdf) => Contiguity(gdf, rook: false);

    /// <summary>Rook contiguity: polygons sharing a boundary segment (non-zero length).</summary>
    public static SpatialWeights Rook(GeoDataFrame gdf) => Contiguity(gdf, rook: true);

    private static SpatialWeights Contiguity(GeoDataFrame gdf, bool rook)
    {
        var geoms = gdf.Geometry;
        var tree = gdf.SIndex;
        int n = gdf.RowCount;
        var nbs = new int[n][];
        Parallel.For(0, n, i =>
        {
            var g = geoms[i];
            if (g is null) { nbs[i] = []; return; }
            var env = new Envelope(g.EnvelopeInternal);
            env.ExpandBy(Math.Max(env.Width, env.Height) * 1e-9);
            var cand = tree.Query(env);
            var list = new List<int>();
            var prepared = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(g);
            foreach (var j in cand)
            {
                if (j == i) continue;
                var h = geoms[j]!;
                if (!prepared.Intersects(h)) continue;
                if (rook)
                {
                    var inter = g.Boundary.Intersection(h.Boundary);
                    if (inter.Length <= 0) continue;
                }
                list.Add(j);
            }
            list.Sort();
            nbs[i] = list.ToArray();
        });
        return new SpatialWeights(nbs, nbs.Select(a => Enumerable.Repeat(1.0, a.Length).ToArray()).ToArray(), rook ? "rook" : "queen");
    }

    /// <summary>k-nearest neighbours on (representative) points.</summary>
    public static SpatialWeights KNearest(GeoDataFrame gdf, int k)
    {
        var (xs, ys) = Points(gdf.Geometry);
        var tree = new KdTree(xs, ys);
        var nbs = new int[xs.Length][];
        Parallel.For(0, xs.Length, i =>
        {
            nbs[i] = tree.Nearest(xs[i], ys[i], k + 1).Where(r => r.Index != i).Take(k).Select(r => r.Index).ToArray();
        });
        return new SpatialWeights(nbs, nbs.Select(a => Enumerable.Repeat(1.0, a.Length).ToArray()).ToArray(), $"knn{k}");
    }

    /// <summary>Distance band: neighbours within <paramref name="threshold"/>; optional inverse-distance weights (power alpha).</summary>
    public static SpatialWeights DistanceBand(GeoDataFrame gdf, double threshold, bool binary = true, double alpha = -1)
    {
        var (xs, ys) = Points(gdf.Geometry);
        var tree = new KdTree(xs, ys);
        var nbs = new int[xs.Length][];
        var wts = new double[xs.Length][];
        Parallel.For(0, xs.Length, i =>
        {
            var idx = tree.Radius(xs[i], ys[i], threshold).Where(j => j != i).OrderBy(j => j).ToArray();
            nbs[i] = idx;
            wts[i] = idx.Select(j =>
            {
                if (binary) return 1.0;
                double d = Math.Sqrt((xs[i] - xs[j]) * (xs[i] - xs[j]) + (ys[i] - ys[j]) * (ys[i] - ys[j]));
                return d == 0 ? 0 : Math.Pow(d, alpha);
            }).ToArray();
        });
        return new SpatialWeights(nbs, wts, binary ? "distance_band" : "inverse_distance");
    }

    internal static (double[] X, double[] Y) Points(GeoSeries g)
    {
        var xs = new double[g.Length]; var ys = new double[g.Length];
        for (int i = 0; i < g.Length; i++)
        {
            NtsGeometry? geom = g[i];
            var p = geom switch { null => null, Point pt => pt, _ => geom.InteriorPoint };
            xs[i] = p?.X ?? double.NaN; ys[i] = p?.Y ?? double.NaN;
        }
        return (xs, ys);
    }
}
