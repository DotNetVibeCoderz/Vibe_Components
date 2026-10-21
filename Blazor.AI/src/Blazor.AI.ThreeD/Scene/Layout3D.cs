namespace Blazor.AI.ThreeD;

/// <summary>Deterministic 3-D layouts: force-directed graphs, layered trees, spheres and projections.</summary>
public static class Layout3D
{
    /// <summary>Fruchterman–Reingold in 3-D. Same input and seed give the same layout.</summary>
    public static Dictionary<string, double[]> Force(IReadOnlyList<string> nodes, IReadOnlyList<(string A, string B, double Weight)> edges, double radius = 4, int iterations = 220, int seed = 7)
    {
        var n = nodes.Count;
        var pos = new double[n][];
        var rng = new Random(seed);
        for (var i = 0; i < n; i++)
        {
            // Start on a Fibonacci sphere: stable and well spread.
            var p = Fibonacci(i, n, radius * 0.8);
            pos[i] = [p[0] + rng.NextDouble() * 0.01, p[1], p[2]];
        }

        if (n <= 1)
        {
            return nodes.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => pos[x.i]);
        }

        var index = nodes.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var links = edges.Where(e => index.ContainsKey(e.A) && index.ContainsKey(e.B) && e.A != e.B)
            .Select(e => (A: index[e.A], B: index[e.B], W: Math.Max(0.1, e.Weight))).ToArray();
        var k = radius * 1.6 / Math.Cbrt(n);
        var temp = radius * 0.5;
        var disp = new double[n][];
        for (var it = 0; it < iterations; it++)
        {
            for (var i = 0; i < n; i++)
            {
                disp[i] = [0, 0, 0];
            }

            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var d = Sub(pos[i], pos[j]);
                    var dist = Math.Max(0.01, Len(d));
                    var f = k * k / dist;
                    for (var c = 0; c < 3; c++)
                    {
                        var v = d[c] / dist * f;
                        disp[i][c] += v;
                        disp[j][c] -= v;
                    }
                }
            }

            foreach (var (a, b, w) in links)
            {
                var d = Sub(pos[a], pos[b]);
                var dist = Math.Max(0.01, Len(d));
                var f = dist * dist / k * Math.Min(w, 3);
                for (var c = 0; c < 3; c++)
                {
                    var v = d[c] / dist * f;
                    disp[a][c] -= v;
                    disp[b][c] += v;
                }
            }

            for (var i = 0; i < n; i++)
            {
                var len = Math.Max(0.01, Len(disp[i]));
                for (var c = 0; c < 3; c++)
                {
                    pos[i][c] += disp[i][c] / len * Math.Min(len, temp);
                    pos[i][c] -= pos[i][c] * 0.01; // gravity to centre
                }
            }

            temp *= 0.97;
        }

        // Scale so most nodes fill the radius; isolated outliers are pulled in rather than shrinking the whole graph.
        var lengths = pos.Select(Len).OrderBy(l => l).ToArray();
        var reference = lengths[(int)Math.Floor((lengths.Length - 1) * 0.85)];
        var s = reference > 1e-6 ? radius / reference : 1;
        return nodes.Select((id, i) => (id, i)).ToDictionary(x => x.id, x =>
        {
            var p = new[] { pos[x.i][0] * s, pos[x.i][1] * s, pos[x.i][2] * s };
            var len = Len(p);
            var cap = radius * 1.15;
            return len > cap ? [p[0] * cap / len, p[1] * cap / len, p[2] * cap / len] : p;
        });
    }

    /// <summary>Evenly spread point <paramref name="i"/> of <paramref name="n"/> on a sphere.</summary>
    public static double[] Fibonacci(int i, int n, double radius)
    {
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var y = n == 1 ? 0 : 1 - (i / (double)(n - 1)) * 2;
        var r = Math.Sqrt(1 - y * y);
        var theta = golden * i;
        return [Math.Cos(theta) * r * radius, y * radius, Math.Sin(theta) * r * radius];
    }

    /// <summary>Layered tree layout: depth goes down (−Y), siblings spread on X, alternating Z for readability.</summary>
    public static Dictionary<string, double[]> Tree<T>(T root, Func<T, string> id, Func<T, IEnumerable<T>> children, double levelGap = 1.6, double siblingGap = 1.4)
    {
        var result = new Dictionary<string, double[]>();
        var nextX = 0.0;
        double Place(T node, int depth)
        {
            var kids = children(node).ToList();
            double x;
            if (kids.Count == 0)
            {
                x = nextX;
                nextX += siblingGap;
            }
            else
            {
                var xs = kids.Select(c => Place(c, depth + 1)).ToList();
                x = (xs.Min() + xs.Max()) / 2;
            }

            result[id(node)] = [x, -depth * levelGap, (depth % 2 == 0 ? 0 : 0.35)];
            return x;
        }

        Place(root, 0);
        var mid = result.Values.Average(p => p[0]);
        foreach (var p in result.Values)
        {
            p[0] -= mid;
        }

        return result;
    }

    /// <summary>Principal component projection of rows to 3-D (power iteration, no external dependency).</summary>
    public static double[][] Pca3(IReadOnlyList<double[]> rows, int seed = 3)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var d = rows[0].Length;
        var mean = new double[d];
        foreach (var r in rows)
        {
            for (var j = 0; j < d; j++)
            {
                mean[j] += r[j] / rows.Count;
            }
        }

        var x = rows.Select(r => r.Select((v, j) => v - mean[j]).ToArray()).ToArray();
        var comps = new List<double[]>();
        var rng = new Random(seed);
        for (var c = 0; c < Math.Min(3, d); c++)
        {
            var v = Enumerable.Range(0, d).Select(_ => rng.NextDouble() - 0.5).ToArray();
            for (var it = 0; it < 60; it++)
            {
                var w = new double[d];
                foreach (var row in x)
                {
                    var dot = Dot(row, v);
                    for (var j = 0; j < d; j++)
                    {
                        w[j] += dot * row[j];
                    }
                }

                foreach (var prev in comps)
                {
                    var p = Dot(w, prev);
                    for (var j = 0; j < d; j++)
                    {
                        w[j] -= p * prev[j];
                    }
                }

                var len = Math.Sqrt(Dot(w, w));
                if (len < 1e-12)
                {
                    break;
                }

                v = w.Select(a => a / len).ToArray();
            }

            comps.Add(v);
        }

        while (comps.Count < 3)
        {
            comps.Add(new double[d]);
        }

        return x.Select(r => new[] { Dot(r, comps[0]), Dot(r, comps[1]), Dot(r, comps[2]) }).ToArray();
    }

    /// <summary>Rescales points so the largest absolute coordinate equals <paramref name="extent"/>.</summary>
    public static double[][] Normalize(IReadOnlyList<double[]> points, double extent = 4)
    {
        if (points.Count == 0)
        {
            return [];
        }

        var center = new double[3];
        foreach (var p in points)
        {
            for (var c = 0; c < 3; c++)
            {
                center[c] += p[c] / points.Count;
            }
        }

        var max = points.Max(p => Math.Max(Math.Abs(p[0] - center[0]), Math.Max(Math.Abs(p[1] - center[1]), Math.Abs(p[2] - center[2]))));
        var s = max > 1e-12 ? extent / max : 1;
        return points.Select(p => new[] { (p[0] - center[0]) * s, (p[1] - center[1]) * s, (p[2] - center[2]) * s }).ToArray();
    }

    /// <summary>A gently arched curve between two points (for edges that should not cut through nodes).</summary>
    public static double[][] Arc(double[] a, double[] b, double lift = 0.3, int segments = 12)
    {
        var pts = new double[segments + 1][];
        for (var i = 0; i <= segments; i++)
        {
            var t = i / (double)segments;
            var h = Math.Sin(Math.PI * t) * lift;
            pts[i] = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t + h, a[2] + (b[2] - a[2]) * t];
        }

        return pts;
    }

    public static double Distance(double[] a, double[] b) => Len(Sub(a, b));

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double Len(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);

    private static double Dot(double[] a, double[] b)
    {
        var s = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }
}
