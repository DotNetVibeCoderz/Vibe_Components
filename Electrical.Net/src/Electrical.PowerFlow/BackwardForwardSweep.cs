using System.Diagnostics;
using System.Numerics;
using Electrical.Core.Network;

namespace Electrical.PowerFlow;

/// <summary>
/// Backward/forward sweep for radial distribution feeders (current-summation method). Robust for high R/X ratios.
/// Generators other than the substation (slack) are modelled as negative PQ injections (DG in PQ mode).
/// Line charging is lumped as half shunts at both ends; transformers use their real off-nominal ratio.
/// </summary>
public static class BackwardForwardSweep
{
    public static PowerFlowResult Solve(PowerNetwork net, PowerFlowOptions o)
    {
        var sw = Stopwatch.StartNew();
        if (!Topology.IsRadial(net)) throw new InvalidOperationException("Backward/forward sweep requires a radial network; use Newton–Raphson for meshed grids.");
        var pf = new PowerFlowCase(net, o.FlatStart, out var v);
        int n = pf.N;
        int root = Array.IndexOf(pf.Types, BusType.Slack);

        // Build tree: parent branch per bus, BFS order from the root
        var adj = new List<(int Other, int Branch)>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            if (!br.InService) continue;
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            adj[f].Add((t, k)); adj[t].Add((f, k));
        }
        var parent = new int[n]; var parentBranch = new int[n];
        Array.Fill(parent, -1); Array.Fill(parentBranch, -1);
        var order = new List<int>(n) { root };
        var seen = new bool[n]; seen[root] = true;
        for (int h = 0; h < order.Count; h++)
        {
            int u = order[h];
            foreach (var (w, k) in adj[u])
                if (!seen[w]) { seen[w] = true; parent[w] = u; parentBranch[w] = k; order.Add(w); }
        }

        var shunt = new Complex[n];
        for (int i = 0; i < n; i++) shunt[i] = new Complex(net.Buses[i].Gs, net.Buses[i].Bs) / net.BaseMva;
        foreach (var br in net.Branches)
        {
            if (!br.InService) continue;
            var half = new Complex(0, br.B / 2);
            shunt[net.IndexOf(br.From)] += half;
            shunt[net.IndexOf(br.To)] += half;
        }
        for (int i = 0; i < n; i++) v[i] = i == root ? v[root] : Complex.FromPolarCoordinates(1, v[root].Phase);

        var injection = new Complex[n];
        var j = new Complex[n]; // current in the branch feeding bus i (at the child side)
        var history = new List<double>();
        double norm = double.PositiveInfinity;
        int it = 0;
        for (; it < Math.Max(o.MaxIterations, 100); it++)
        {
            for (int i = 0; i < n; i++)
            {
                var sNet = pf.LoadAt(i, v[i].Magnitude) - pf.SGen[i];
                injection[i] = Complex.Conjugate(sNet / v[i]) + shunt[i] * v[i];
            }
            // backward sweep
            Array.Copy(injection, j, n);
            for (int h = order.Count - 1; h > 0; h--)
            {
                int u = order[h];
                var br = net.Branches[parentBranch[u]];
                double tap = br.EffectiveTap;
                bool fromIsParent = net.IndexOf(br.From) == parent[u];
                j[parent[u]] += fromIsParent ? j[u] / tap : j[u] * tap;
            }
            // forward sweep
            norm = 0;
            for (int h = 1; h < order.Count; h++)
            {
                int u = order[h];
                var br = net.Branches[parentBranch[u]];
                var z = new Complex(br.R, br.X);
                double tap = br.EffectiveTap;
                bool fromIsParent = net.IndexOf(br.From) == parent[u];
                var vNew = fromIsParent ? v[parent[u]] / tap - z * j[u] : (v[parent[u]] - z * j[u] * tap) * tap;
                norm = Math.Max(norm, (vNew - v[u]).Magnitude);
                v[u] = vNew;
            }
            history.Add(norm);
            if (norm < o.Tolerance) { it++; break; }
        }
        return pf.BuildResult(PowerFlowMethod.BackwardForwardSweep, v, norm < o.Tolerance, it, norm, sw.Elapsed, history);
    }
}
