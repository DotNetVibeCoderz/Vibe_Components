using Electrical.Core.Network;

namespace Electrical.GIS;

/// <summary>Options for <see cref="GeoLayout.ForceDirected"/>.</summary>
public sealed record GeoLayoutOptions
{
    /// <summary>Radius [km] of the area the layout is scaled into. Default 20 km.</summary>
    public double RadiusKm { get; init; } = 20;
    /// <summary>Fruchterman–Reingold iterations. Default 300.</summary>
    public int Iterations { get; init; } = 300;
    /// <summary>Replace existing bus locations too (otherwise located buses stay fixed and act as anchors). Default false.</summary>
    public bool Overwrite { get; init; }
    /// <summary>Seed of the deterministic initial jitter. Default 7.</summary>
    public int Seed { get; init; } = 7;
    /// <summary>Also set <see cref="Branch.LengthKm"/> of lines that have no length from the resulting coordinates. Default false.</summary>
    public bool AssignLengths { get; init; }
}

/// <summary>Deterministic automatic geographic layout for networks without coordinates.</summary>
public static class GeoLayout
{
    /// <summary>
    /// Places buses with a Fruchterman–Reingold force-directed layout around <paramref name="center"/>, starting from concentric
    /// breadth-first rings around the slack bus. Located buses are kept fixed unless <see cref="GeoLayoutOptions.Overwrite"/> is set.
    /// The result is identical across runs and processes. Returns the number of buses that received a location.
    /// </summary>
    public static int ForceDirected(PowerNetwork net, GeoPoint center, GeoLayoutOptions? options = null)
    {
        options ??= new GeoLayoutOptions();
        int n = net.BusCount;
        if (n == 0) return 0;
        var fixedMask = new bool[n];
        var x = new double[n];
        var y = new double[n];
        for (int i = 0; i < n; i++)
            if (!options.Overwrite && net.Buses[i].Location is GeoPoint p)
            {
                fixedMask[i] = true;
                (x[i], y[i]) = Geo.LocalKm(center, p);
            }
        int free = fixedMask.Count(f => !f);
        if (free == 0) return 0;

        // Undirected edge list (bus positions), parallel branches collapsed.
        var edges = new HashSet<(int, int)>();
        foreach (var br in net.Branches)
        {
            if (!net.ContainsBus(br.From) || !net.ContainsBus(br.To)) continue;
            int a = net.IndexOf(br.From), b = net.IndexOf(br.To);
            if (a != b) edges.Add(a < b ? (a, b) : (b, a));
        }
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        foreach (var (a, b) in edges) { adj[a].Add(b); adj[b].Add(a); }

        // Initial placement: BFS rings from the slack bus (or the first bus), deterministic jitter.
        double R = options.RadiusKm;
        var depth = new int[n];
        Array.Fill(depth, -1);
        var order = new List<int>();
        int start = net.SlackBus is { } s ? net.IndexOf(s.Id) : 0;
        for (int root = -1; order.Count < n;)
        {
            root = root < 0 ? start : Enumerable.Range(0, n).First(i => depth[i] < 0);
            var q = new Queue<int>();
            depth[root] = root == start ? 0 : 1;
            q.Enqueue(root);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                order.Add(u);
                foreach (int v in adj[u].Order()) if (depth[v] < 0) { depth[v] = depth[u] + 1; q.Enqueue(v); }
            }
        }
        int maxDepth = Math.Max(1, depth.Max());
        var rng = new Random(options.Seed);
        var byDepth = order.GroupBy(i => depth[i]).ToDictionary(gp => gp.Key, gp => gp.ToList());
        foreach (var (d, members) in byDepth)
            for (int j = 0; j < members.Count; j++)
            {
                int i = members[j];
                double jx = (rng.NextDouble() - 0.5) * 0.05 * R, jy = (rng.NextDouble() - 0.5) * 0.05 * R;
                if (fixedMask[i]) continue;
                double rr = R * d / maxDepth, ang = 2 * Math.PI * (j + 0.5 * (d % 2)) / members.Count;
                x[i] = rr * Math.Cos(ang) + jx;
                y[i] = rr * Math.Sin(ang) + jy;
            }

        // Fruchterman–Reingold.
        double k = 2 * R / Math.Sqrt(n), t = R / 5;
        var dx = new double[n];
        var dy = new double[n];
        for (int it = 0; it < options.Iterations; it++)
        {
            Array.Clear(dx); Array.Clear(dy);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double ex = x[i] - x[j], ey = y[i] - y[j];
                    double d2 = ex * ex + ey * ey;
                    if (d2 < 1e-12) { ex = 1e-3 * (i - j); ey = 1e-3; d2 = ex * ex + ey * ey; }
                    double f = k * k / d2; // repulsion k²/d, applied along the unit vector (e/d)
                    dx[i] += ex * f; dy[i] += ey * f;
                    dx[j] -= ex * f; dy[j] -= ey * f;
                }
            foreach (var (a, b) in edges)
            {
                double ex = x[a] - x[b], ey = y[a] - y[b];
                double d = Math.Sqrt(ex * ex + ey * ey) + 1e-12;
                double f = d / k; // attraction d²/k along (e/d)
                dx[a] -= ex * f; dy[a] -= ey * f;
                dx[b] += ex * f; dy[b] += ey * f;
            }
            for (int i = 0; i < n; i++)
            {
                if (fixedMask[i]) continue;
                // gentle gravity towards the center keeps disconnected parts together
                double gr = 0.05 * Math.Sqrt(x[i] * x[i] + y[i] * y[i]) / k; // weak spring to the center: 0.05·d²/k
                dx[i] -= x[i] * gr; dy[i] -= y[i] * gr;
                double len = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (len < 1e-12) continue;
                double step = Math.Min(len, t);
                x[i] += dx[i] / len * step;
                y[i] += dy[i] / len * step;
            }
            t = Math.Max(R / 500, t * 0.985);
        }

        // Scale free buses into the requested radius when nothing anchors the layout.
        if (fixedMask.All(f => !f))
        {
            double cx = x.Average(), cy = y.Average();
            double maxR = 1e-9;
            for (int i = 0; i < n; i++) maxR = Math.Max(maxR, Math.Sqrt((x[i] - cx) * (x[i] - cx) + (y[i] - cy) * (y[i] - cy)));
            for (int i = 0; i < n; i++) { x[i] = (x[i] - cx) / maxR * R; y[i] = (y[i] - cy) / maxR * R; }
        }
        int placed = 0;
        for (int i = 0; i < n; i++)
        {
            if (fixedMask[i]) continue;
            var g = Geo.Offset(center, x[i], y[i]);
            net.Buses[i].Location = new GeoPoint(Math.Round(g.Latitude, 7), Math.Round(g.Longitude, 7));
            placed++;
        }
        if (options.AssignLengths) LineLengths.Assign(net, overwrite: false);
        return placed;
    }
}
