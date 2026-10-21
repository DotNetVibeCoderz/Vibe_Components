namespace Blazor.AI.Visualization;

/// <summary>Fruchterman–Reingold force-directed layout. Deterministic for a given seed, O(n²) per iteration (fine up to a few hundred nodes).</summary>
public static class ForceLayout
{
    public static Dictionary<string, (double X, double Y)> Compute(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        double width,
        double height,
        int iterations = 300,
        int seed = 7)
    {
        var n = nodes.Count;
        var result = new Dictionary<string, (double X, double Y)>(n);
        if (n == 0)
        {
            return result;
        }

        var index = new Dictionary<string, int>(n);
        for (var i = 0; i < n; i++)
        {
            index[nodes[i].Id] = i;
        }

        var rng = new Random(seed);
        var x = new double[n];
        var y = new double[n];
        var dx = new double[n];
        var dy = new double[n];

        // Start on a circle (plus jitter) so the result does not depend on input order too much.
        for (var i = 0; i < n; i++)
        {
            var angle = 2 * Math.PI * i / n;
            x[i] = width / 2 + Math.Cos(angle) * width / 3 + rng.NextDouble() * 4;
            y[i] = height / 2 + Math.Sin(angle) * height / 3 + rng.NextDouble() * 4;
        }

        var links = edges
            .Where(e => index.ContainsKey(e.Source) && index.ContainsKey(e.Target) && e.Source != e.Target)
            .Select(e => (A: index[e.Source], B: index[e.Target]))
            .ToArray();

        var k = Math.Sqrt(width * height / n) * 0.75;
        var temperature = width / 8;
        var cooling = temperature / (iterations + 1);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(dx);
            Array.Clear(dy);

            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var ddx = x[i] - x[j];
                    var ddy = y[i] - y[j];
                    var dist = Math.Max(0.01, Math.Sqrt(ddx * ddx + ddy * ddy));
                    var force = k * k / dist;
                    var fx = ddx / dist * force;
                    var fy = ddy / dist * force;
                    dx[i] += fx;
                    dy[i] += fy;
                    dx[j] -= fx;
                    dy[j] -= fy;
                }
            }

            foreach (var (a, b) in links)
            {
                var ddx = x[a] - x[b];
                var ddy = y[a] - y[b];
                var dist = Math.Max(0.01, Math.Sqrt(ddx * ddx + ddy * ddy));
                var force = dist * dist / k;
                var fx = ddx / dist * force;
                var fy = ddy / dist * force;
                dx[a] -= fx;
                dy[a] -= fy;
                dx[b] += fx;
                dy[b] += fy;
            }

            for (var i = 0; i < n; i++)
            {
                // Gentle gravity keeps disconnected components on screen.
                dx[i] += (width / 2 - x[i]) * 0.02 * k / 10;
                dy[i] += (height / 2 - y[i]) * 0.02 * k / 10;

                var disp = Math.Max(0.01, Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]));
                x[i] += dx[i] / disp * Math.Min(disp, temperature);
                y[i] += dy[i] / disp * Math.Min(disp, temperature);
            }

            temperature = Math.Max(0.5, temperature - cooling);
        }

        // Fit into the viewport with a margin.
        const double margin = 40;
        double minX = x.Min(), maxX = x.Max(), minY = y.Min(), maxY = y.Max();
        var sx = (width - 2 * margin) / Math.Max(1, maxX - minX);
        var sy = (height - 2 * margin) / Math.Max(1, maxY - minY);
        for (var i = 0; i < n; i++)
        {
            result[nodes[i].Id] = (
                n == 1 ? width / 2 : margin + (x[i] - minX) * sx,
                n == 1 ? height / 2 : margin + (y[i] - minY) * sy);
        }

        return result;
    }
}
