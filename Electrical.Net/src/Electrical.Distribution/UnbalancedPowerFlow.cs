using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Distribution;

/// <summary>Unbalanced power flow algorithms.</summary>
public enum DistributionSolverMethod
{
    /// <summary>Ladder sweep for radial feeders, current injection otherwise.</summary>
    Auto,
    /// <summary>Forward/backward sweep with Kersting's ladder iterative technique (generalized matrices a, b, c, d, A, B); radial only.</summary>
    LadderSweep,
    /// <summary>Fixed-point current injection on the sparse phase-frame Y-bus (Z-bus method; works for meshed networks).</summary>
    CurrentInjection,
}

/// <summary>How controllers participate in a single <see cref="UnbalancedPowerFlow.Solve"/>.</summary>
public enum ControlMode
{
    /// <summary>Controllers are ignored (taps and capacitor states stay as they are).</summary>
    Off,
    /// <summary>Iterate power flow → controllers (ignoring time delays) until no controller acts.</summary>
    Static,
}

/// <summary>Options of the unbalanced power flow.</summary>
public sealed record DistributionPowerFlowOptions
{
    public static DistributionPowerFlowOptions Default { get; } = new();
    public DistributionSolverMethod Method { get; init; } = DistributionSolverMethod.Auto;
    /// <summary>Convergence tolerance on the largest voltage change [pu].</summary>
    public double Tolerance { get; init; } = 1e-7;
    public int MaxIterations { get; init; } = 200;
    public ControlMode ControlMode { get; init; } = ControlMode.Static;
    public int MaxControlIterations { get; init; } = 30;
    /// <summary>Multiplier applied to every load (time-series load shapes).</summary>
    public double LoadMultiplier { get; init; } = 1;
}

/// <summary>Thrown when the ladder sweep is requested for a meshed network.</summary>
public sealed class NonRadialNetworkException(string message) : InvalidOperationException(message);

/// <summary>
/// Phase-frame unbalanced power flow for distribution feeders: Kersting's ladder (forward/backward sweep) for radial feeders and fixed-point
/// current injection on the sparse Y-bus for weakly meshed networks. Voltages in V, currents in A, powers in kW/kvar.
/// </summary>
public static class UnbalancedPowerFlow
{
    /// <summary>Solves the network; with <see cref="ControlMode.Static"/> the controllers in <see cref="DistributionNetwork.Controllers"/>
    /// adjust taps and capacitors until all are satisfied.</summary>
    public static DistributionPowerFlowResult Solve(DistributionNetwork network, DistributionPowerFlowOptions? options = null)
    {
        options ??= DistributionPowerFlowOptions.Default;
        var result = SolveOnce(network, options);
        if (options.ControlMode == ControlMode.Static && network.Controllers.Count > 0)
            result = RunControls(network, options, result, new ControlTiming(0, 0, true));
        return result;
    }

    /// <summary>Lets controllers act on <paramref name="result"/> and re-solves until none acts (or the iteration limit is reached).</summary>
    public static DistributionPowerFlowResult RunControls(DistributionNetwork network, DistributionPowerFlowOptions options,
        DistributionPowerFlowResult result, ControlTiming timing)
    {
        int rounds = 0;
        while (rounds < options.MaxControlIterations && result.Converged)
        {
            bool changed = false;
            foreach (var c in network.Controllers) changed |= c.Control(network, result, timing);
            if (!changed) break;
            rounds++;
            result = SolveOnce(network, options);
        }
        result.ControlIterations = rounds;
        return result;
    }

    /// <summary>One power flow without controller actions.</summary>
    public static DistributionPowerFlowResult SolveOnce(DistributionNetwork network, DistributionPowerFlowOptions? options = null)
    {
        options ??= DistributionPowerFlowOptions.Default;
        var m = FeederModel.Compile(network);
        var method = options.Method;
        Tree? tree = null;
        if (method != DistributionSolverMethod.CurrentInjection)
        {
            tree = Tree.Build(m, out var why);
            if (tree is null)
            {
                if (method == DistributionSolverMethod.LadderSweep) throw new NonRadialNetworkException(why!);
                method = DistributionSolverMethod.CurrentInjection;
            }
            else method = DistributionSolverMethod.LadderSweep;
        }
        var V = new Complex[m.NodeCount];
        bool converged;
        int iterations;
        if (method == DistributionSolverMethod.LadderSweep) (converged, iterations) = Ladder(m, tree!, V, options);
        else (converged, iterations) = CurrentInjection(m, V, options);
        return m.BuildResult(V, converged, iterations, method, options.LoadMultiplier);
    }

    private static (bool, int) CurrentInjection(FeederModel m, Complex[] V, DistributionPowerFlowOptions o)
    {
        int n = m.NodeCount;
        var isSource = new bool[n];
        foreach (var s in m.SourceNodes) isSource[s] = true;
        var map = new int[n];
        int nl = 0;
        for (int i = 0; i < n; i++) map[i] = isSource[i] ? -1 : nl++;
        var yll = new SparseBuilder<Complex>(Math.Max(nl, 1), Math.Max(nl, 1));
        var yls = new List<(int Row, int SourceNode, Complex Value)>();
        foreach (var br in m.Branches)
        {
            var nodes = m.LocalNodes(br);
            for (int i = 0; i < nodes.Length; i++)
            {
                int gi = nodes[i];
                if (isSource[gi]) continue;
                for (int j = 0; j < nodes.Length; j++)
                {
                    var v = br.Y[i, j];
                    if (v == Complex.Zero) continue;
                    int gj = nodes[j];
                    if (isSource[gj]) yls.Add((map[gi], gj, v));
                    else yll.Add(map[gi], map[gj], v);
                }
            }
        }
        // tiny shunt on every node keeps isolated phases solvable
        for (int i = 0; i < nl; i++) yll.Add(i, i, new Complex(1e-12, 0));
        m.SetSourceVoltages(V);
        if (nl == 0) return (true, 0);
        var lu = SparseLU<Complex>.Factorize(yll.ToCsr());
        var rhs0 = new Complex[nl];
        foreach (var (row, sn, val) in yls) rhs0[row] -= val * V[sn];
        var x = lu.Solve(rhs0);
        for (int i = 0; i < n; i++) if (map[i] >= 0) V[i] = x[map[i]];
        var inj = new Complex[n];
        for (int it = 1; it <= o.MaxIterations; it++)
        {
            Array.Clear(inj);
            m.AddShuntInjections(V, inj, o.LoadMultiplier);
            var rhs = (Complex[])rhs0.Clone();
            for (int i = 0; i < n; i++) if (map[i] >= 0) rhs[map[i]] += inj[i];
            x = lu.Solve(rhs);
            double dv = 0;
            for (int i = 0; i < n; i++)
            {
                if (map[i] < 0) continue;
                dv = Math.Max(dv, (x[map[i]] - V[i]).Magnitude / m.NodeBaseV[i]);
                V[i] = x[map[i]];
            }
            if (dv < o.Tolerance) return (true, it);
        }
        return (false, o.MaxIterations);
    }

    private static (bool, int) Ladder(FeederModel m, Tree t, Complex[] V, DistributionPowerFlowOptions o)
    {
        m.SetSourceVoltages(V);
        int nb = t.Order.Count;
        var Im = new Complex[nb][];
        for (int k = 0; k < nb; k++) Im[k] = new Complex[t.Order[k].ToNodes.Length];
        Forward(t, V, Im);
        var nodeCurrent = new Complex[m.NodeCount];
        var vOld = new Complex[m.NodeCount];
        for (int it = 1; it <= o.MaxIterations; it++)
        {
            // backward sweep: currents drawn at each node, accumulated towards the source
            Array.Clear(nodeCurrent);
            m.AddShuntInjections(V, nodeCurrent, o.LoadMultiplier);
            for (int i = 0; i < nodeCurrent.Length; i++) nodeCurrent[i] = -nodeCurrent[i];
            for (int k = nb - 1; k >= 0; k--)
            {
                var e = t.Order[k];
                var im = Im[k];
                for (int i = 0; i < im.Length; i++) im[i] = nodeCurrent[e.ToNodes[i]];
                var vm = Gather(V, e.ToNodes);
                var In = CMat.Mul(e.G.Sd, im);
                var cv = CMat.Mul(e.G.Sc, vm);
                for (int i = 0; i < In.Length; i++) nodeCurrent[e.FromNodes[i]] += In[i] + cv[i];
            }
            Array.Copy(V, vOld, V.Length);
            Forward(t, V, Im);
            double dv = 0;
            for (int i = 0; i < V.Length; i++) dv = Math.Max(dv, (V[i] - vOld[i]).Magnitude / m.NodeBaseV[i]);
            if (dv < o.Tolerance) return (true, it);
        }
        return (false, o.MaxIterations);
    }

    private static void Forward(Tree t, Complex[] V, Complex[][] Im)
    {
        for (int k = 0; k < t.Order.Count; k++)
        {
            var e = t.Order[k];
            var vn = Gather(V, e.FromNodes);
            var av = CMat.Mul(e.G.A, vn);
            var bi = CMat.Mul(e.G.B, Im[k]);
            for (int i = 0; i < av.Length; i++) V[e.ToNodes[i]] = av[i] - bi[i];
        }
    }

    private static Complex[] Gather(Complex[] v, int[] nodes)
    {
        var r = new Complex[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) r[i] = v[nodes[i]];
        return r;
    }

    /// <summary>Radial tree with oriented elements in breadth-first order from the source.</summary>
    private sealed class Tree
    {
        public sealed record Edge(BranchModel Branch, int[] FromNodes, int[] ToNodes, GeneralizedMatrices G);
        public List<Edge> Order { get; } = [];

        public static Tree? Build(FeederModel m, out string? reason)
        {
            reason = null;
            int nb = m.BusCount;
            var adj = new List<int>[nb];
            for (int i = 0; i < nb; i++) adj[i] = [];
            for (int k = 0; k < m.Branches.Count; k++)
            {
                adj[m.Branches[k].FromBus].Add(k);
                adj[m.Branches[k].ToBus].Add(k);
            }
            var parent = Enumerable.Repeat(-2, nb).ToArray();
            var fedPhases = new PhaseSet[nb];
            var used = new bool[m.Branches.Count];
            var t = new Tree();
            var q = new Queue<int>();
            q.Enqueue(m.SourceBus);
            parent[m.SourceBus] = -1;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                foreach (var k in adj[u])
                {
                    if (used[k]) continue;
                    used[k] = true;
                    var br = m.Branches[k];
                    bool forward = br.FromBus == u;
                    int v = forward ? br.ToBus : br.FromBus;
                    var vPhases = PhaseSetExtensions.FromIndices(forward ? br.ToPhases : br.FromPhases);
                    if (v == u) { reason = $"Element {br.Name} connects bus to itself."; return null; }
                    if (parent[v] == -2) { parent[v] = u; q.Enqueue(v); }
                    else if (parent[v] != u || (fedPhases[v] & vPhases) != 0)
                    {
                        reason = $"Network is not radial (loop closed by {br.Name}).";
                        return null;
                    }
                    fedPhases[v] |= vPhases;
                    var y = forward ? br.Y : Swap(br.Y, br.Nf);
                    var g = br.Kind == DistributionBranchKind.Line
                        ? GeneralizedMatrices.ForLine((LineSegment)br.Element)
                        : GeneralizedMatrices.FromAdmittance(y, br.Nf, forceTransformer: true);
                    var nodes = m.LocalNodes(br);
                    var fromNodes = nodes[..br.Nf];
                    var toNodes = nodes[br.Nf..];
                    t.Order.Add(forward ? new Edge(br, fromNodes, toNodes, g) : new Edge(br, toNodes, fromNodes, g));
                }
            }
            for (int i = 0; i < nb; i++)
                if (parent[i] == -2 && m.BusHasNodes(i)) { reason = $"Bus {m.BusName(i)} is not connected to the source."; return null; }
            return t;
        }

        private static Complex[,] Swap(Complex[,] y, int nf)
        {
            int n = y.GetLength(0), nt = n - nf;
            var perm = Enumerable.Range(nf, nt).Concat(Enumerable.Range(0, nf)).ToArray();
            return CMat.Select(y, perm, perm);
        }
    }
}
