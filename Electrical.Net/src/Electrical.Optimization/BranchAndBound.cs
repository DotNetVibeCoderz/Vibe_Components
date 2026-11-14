using System.Diagnostics;

namespace Electrical.Optimization;

/// <summary>
/// Built-in, dependency-free LP solver: bounded dual simplex with an explicit dense basis inverse, dual steepest-edge pricing, Harris ratio test
/// and Bland's rule as anti-cycling fallback (see <see cref="DualSimplex"/>). Returns constraint duals and reduced costs. Intended for small and
/// medium models (up to a few thousand rows); use the HiGHS or OR-Tools adapters for large ones.
/// </summary>
public sealed class SimplexSolver : ILpSolver
{
    public string Name => "Electrical.Net dual simplex";

    public LinearSolution SolveLp(LinearModel model, SolverOptions? options = null)
    {
        options ??= SolverOptions.Default;
        var sw = Stopwatch.StartNew();
        double sign = model.Sense == ObjectiveSense.Maximize ? -1 : 1;
        var cost = model.Variables.Select(v => sign * v.Cost).ToArray();
        var engine = new DualSimplex(model, cost);
        long deadline = Deadline(options);
        var outcome = engine.Run(double.PositiveInfinity, deadline, MaxIterations(model));
        return MakeLpSolution(model, engine, outcome, sign, sw.Elapsed, Name);
    }

    internal static long MaxIterations(LinearModel m) => Math.Max(20_000, 50L * (m.VariableCount + m.ConstraintCount));

    internal static long Deadline(SolverOptions o) =>
        o.TimeLimit is { } t ? Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * Stopwatch.Frequency) : 0;

    internal static LinearSolution MakeLpSolution(LinearModel model, DualSimplex engine, SimplexOutcome outcome, double sign, TimeSpan elapsed,
        string name)
    {
        var status = outcome switch
        {
            SimplexOutcome.Optimal => SolveStatus.Optimal,
            SimplexOutcome.Infeasible => SolveStatus.Infeasible,
            SimplexOutcome.Unbounded => SolveStatus.Unbounded,
            SimplexOutcome.TimeLimit => SolveStatus.TimeLimit,
            SimplexOutcome.IterationLimit => SolveStatus.IterationLimit,
            _ => SolveStatus.Error,
        };
        if (status != SolveStatus.Optimal)
            return new LinearSolution { Status = status, Iterations = engine.Iterations, SolveTime = elapsed, Solver = name, Message = outcome.ToString() };
        int n = model.VariableCount, m = model.ConstraintCount;
        var x = new double[n];
        for (int j = 0; j < n; j++) x[j] = engine.Value(j);
        var duals = new double[m];
        for (int i = 0; i < m; i++) duals[i] = sign * engine.RowDual(i);
        var rc = new double[n];
        for (int j = 0; j < n; j++) rc[j] = sign * engine.ReducedCost(j);
        double obj = model.EvaluateObjective(x);
        return new LinearSolution
        {
            Status = status, Objective = obj, BestBound = obj, Values = x, Duals = duals, ReducedCosts = rc, Iterations = engine.Iterations,
            SolveTime = elapsed, Solver = name,
        };
    }
}

/// <summary>
/// Built-in, dependency-free MILP solver: LP-based branch and bound on top of the dual simplex. Each node is dived depth-first
/// (round-to-nearest child first, warm-started from the parent basis, so a dive costs only a few dual pivots per level); when a dive ends
/// the open node with the best bound is resumed, warm-started from the current basis, which stays dual feasible under bound changes
/// (best-bound / depth-first hybrid). Nodes are pruned by bound against the
/// incumbent (the dual simplex stops early at the cutoff). Terminates at the relative gap <see cref="SolverOptions.RelativeGap"/>, the time limit
/// or <see cref="SolverOptions.MaxNodes"/>. Branches on the most fractional variable. Suited to small/medium models (e.g. unit commitment of a
/// few units over a day).
/// </summary>
public sealed class BranchAndBoundSolver : IMilpSolver, ILpSolver
{
    private const double IntTol = 1e-6;

    public string Name => "Electrical.Net branch and bound";

    public LinearSolution SolveLp(LinearModel model, SolverOptions? options = null) => new SimplexSolver().SolveLp(model, options);

    public LinearSolution Solve(LinearModel model, SolverOptions? options = null)
    {
        options ??= SolverOptions.Default;
        if (!model.IsMip)
        {
            var lp = new SimplexSolver().SolveLp(model, options);
            return new LinearSolution
            {
                Status = lp.Status, Objective = lp.Objective, BestBound = lp.BestBound, Values = lp.Values, Duals = lp.Duals,
                ReducedCosts = lp.ReducedCosts, Iterations = lp.Iterations, SolveTime = lp.SolveTime, Solver = Name, Message = lp.Message,
            };
        }
        var sw = Stopwatch.StartNew();
        double sign = model.Sense == ObjectiveSense.Maximize ? -1 : 1;
        var cost = model.Variables.Select(v => sign * v.Cost).ToArray();
        int n = model.VariableCount;
        var ints = Enumerable.Range(0, n).Where(j => model.Variables[j].IsInteger).ToArray();
        // integer bounds rounded inward
        var rootLo = new double[ints.Length];
        var rootUp = new double[ints.Length];
        for (int k = 0; k < ints.Length; k++)
        {
            var v = model.Variables[ints[k]];
            rootLo[k] = Math.Ceiling(v.Lower - IntTol);
            rootUp[k] = Math.Floor(v.Upper + IntTol);
            if (rootLo[k] > rootUp[k])
                return new LinearSolution { Status = SolveStatus.Infeasible, Solver = Name, SolveTime = sw.Elapsed, Message = "empty integer domain" };
        }
        var engine = new DualSimplex(model, cost);
        for (int k = 0; k < ints.Length; k++) engine.SetBounds(ints[k], rootLo[k], rootUp[k]);
        long deadline = SimplexSolver.Deadline(options);
        long maxIt = SimplexSolver.MaxIterations(model);

        double constant = sign * model.ObjectiveConstant;  // internal objective = Σ c x (constant added at the end)
        double incumbent = double.PositiveInfinity;
        double[]? best = null;
        long nodes = 0;
        bool timedOut = false, unbounded = false, nodeLimit = false;
        var open = new PriorityQueue<OpenNode, double>();
        double rootBound = double.NegativeInfinity;
        double prunedBound = double.PositiveInfinity;
        bool cutoffPruned = false;
        var pc = new PseudoCosts(ints.Length);
        var pending = Branch.None;

        var curLo = (double[])rootLo.Clone();
        var curUp = (double[])rootUp.Clone();
        double curBound = double.NegativeInfinity;
        bool first = true;
        while (true)
        {
            // ---- dive from the current node (engine holds its bounds and a warm basis)
            while (true)
            {
                double cutoff = incumbent < double.PositiveInfinity ? incumbent - AbsoluteTolerance(incumbent, options) : double.PositiveInfinity;
                var outcome = engine.Run(cutoff, deadline, maxIt);
                nodes++;
                if (outcome == SimplexOutcome.TimeLimit) { timedOut = true; break; }
                if (outcome == SimplexOutcome.Unbounded) { unbounded = true; break; }
                if (outcome is SimplexOutcome.Error or SimplexOutcome.IterationLimit)
                {
                    if (first)
                        return new LinearSolution { Status = SolveStatus.Error, Solver = Name, SolveTime = sw.Elapsed, Message = outcome.ToString() };
                    break;
                }
                if (outcome is SimplexOutcome.Infeasible or SimplexOutcome.Cutoff) { if (outcome == SimplexOutcome.Cutoff) cutoffPruned = true; break; }
                double obj = engine.Objective;
                if (first) { rootBound = obj; first = false; }
                if (pending.Var >= 0) pc.Record(pending.Var, pending.Up, (obj - pending.ParentObj) / pending.Dist);
                pending = Branch.None;
                if (obj >= cutoff) { cutoffPruned = true; break; }
                // branching variable: pseudo-cost product score (most fractional while no history exists)
                int pick = pc.Select(ints, engine);
                if (pick < 0)
                {
                    incumbent = obj;
                    best = new double[n];
                    for (int j = 0; j < n; j++) best[j] = engine.Value(j);
                    foreach (int j in ints) best[j] = Math.Round(best[j]);
                    break;
                }
                double val = engine.Value(ints[pick]);
                double fl = Math.Floor(val), ce = fl + 1;
                bool upFirst = val - fl >= 0.5;
                // the other child goes to the open list
                var otherLo = (double[])curLo.Clone();
                var otherUp = (double[])curUp.Clone();
                if (upFirst) otherUp[pick] = fl; else otherLo[pick] = ce;
                open.Enqueue(new OpenNode(otherLo, otherUp, obj, new Branch(pick, !upFirst, obj, upFirst ? val - fl : ce - val)), obj);
                if (upFirst) curLo[pick] = ce; else curUp[pick] = fl;
                pending = new Branch(pick, upFirst, obj, upFirst ? ce - val : val - fl);
                engine.SetBounds(ints[pick], curLo[pick], curUp[pick]);
                if (nodes >= options.MaxNodes) { nodeLimit = true; break; }
            }
            if (timedOut || unbounded || nodeLimit) break;
            if (first) break; // root infeasible
            // ---- prune and pick the best-bound open node
            double tol = incumbent < double.PositiveInfinity ? AbsoluteTolerance(incumbent, options) : 0;
            OpenNode? next = null;
            while (open.TryDequeue(out var node, out double bound))
            {
                if (bound < incumbent - tol) { next = node; break; }
                prunedBound = Math.Min(prunedBound, bound); // pruned within the gap tolerance: still a valid lower bound
            }
            if (next is null) break;
            curLo = next.Lo; curUp = next.Up; curBound = next.Bound; pending = next.Origin;
            for (int k = 0; k < ints.Length; k++) engine.SetBounds(ints[k], curLo[k], curUp[k]);
            // the current basis stays dual feasible under any bound change: warm-start the node from it (no refactorisation)
        }

        // lower bound on the optimum: incumbent, open nodes and (when interrupted) the node in progress
        double bestBound = Math.Min(incumbent, prunedBound);
        if (cutoffPruned && incumbent < double.PositiveInfinity) bestBound = Math.Min(bestBound, incumbent - AbsoluteTolerance(incumbent, options));
        if (open.TryPeek(out _, out double openBound)) bestBound = Math.Min(bestBound, openBound);
        if (timedOut || nodeLimit) bestBound = Math.Min(bestBound, double.IsNegativeInfinity(curBound) ? rootBound : curBound);
        if (unbounded)
            return new LinearSolution { Status = SolveStatus.Unbounded, Solver = Name, Nodes = nodes, Iterations = engine.Iterations, SolveTime = sw.Elapsed };
        if (best is null)
        {
            var st = timedOut ? SolveStatus.TimeLimit : nodeLimit ? SolveStatus.IterationLimit : SolveStatus.Infeasible;
            return new LinearSolution
            {
                Status = st, Solver = Name, Nodes = nodes, Iterations = engine.Iterations, SolveTime = sw.Elapsed,
                Message = nodeLimit ? "node limit reached without an incumbent" : null,
            };
        }
        double objUser = model.EvaluateObjective(best);
        double boundUser = double.IsInfinity(bestBound) ? objUser : sign * (bestBound + constant);
        double gap = Math.Abs(objUser - boundUser) / Math.Max(1, Math.Abs(objUser));
        var status = timedOut ? SolveStatus.TimeLimit : nodeLimit && gap > options.RelativeGap ? SolveStatus.Feasible : SolveStatus.Optimal;
        return new LinearSolution
        {
            Status = status, Objective = objUser, BestBound = boundUser, MipGap = gap, Values = best, Nodes = nodes, Iterations = engine.Iterations,
            SolveTime = sw.Elapsed, Solver = Name,
        };
    }

    private static double AbsoluteTolerance(double incumbent, SolverOptions o) =>
        Math.Max(1e-9, o.RelativeGap * Math.Max(1, Math.Abs(incumbent)));

    /// <summary>How a node was created: branched variable (index into the integer list), direction, parent objective, distance moved.</summary>
    private readonly record struct Branch(int Var, bool Up, double ParentObj, double Dist)
    {
        public static Branch None => new(-1, false, 0, 1);
    }

    private sealed record OpenNode(double[] Lo, double[] Up, double Bound, Branch Origin);

    /// <summary>Pseudo-costs (average objective degradation per unit change, Bénichou et al. 1971) with the product score of Achterberg (2007).</summary>
    private sealed class PseudoCosts(int count)
    {
        private readonly double[] _sumUp = new double[count], _sumDown = new double[count];
        private readonly int[] _nUp = new int[count], _nDown = new int[count];
        private double _allUp, _allDown;
        private int _cntUp, _cntDown;

        public void Record(int k, bool up, double gainPerUnit)
        {
            if (double.IsNaN(gainPerUnit) || double.IsInfinity(gainPerUnit)) return;
            gainPerUnit = Math.Max(0, gainPerUnit);
            if (up) { _sumUp[k] += gainPerUnit; _nUp[k]++; _allUp += gainPerUnit; _cntUp++; }
            else { _sumDown[k] += gainPerUnit; _nDown[k]++; _allDown += gainPerUnit; _cntDown++; }
        }

        public int Select(int[] ints, DualSimplex engine)
        {
            double avgUp = _cntUp > 0 ? _allUp / _cntUp : 1, avgDown = _cntDown > 0 ? _allDown / _cntDown : 1;
            int pick = -1;
            double best = -1;
            for (int k = 0; k < ints.Length; k++)
            {
                double v = engine.Value(ints[k]);
                double f = v - Math.Floor(v);
                if (Math.Min(f, 1 - f) <= IntTol) continue;
                double pu = _nUp[k] > 0 ? _sumUp[k] / _nUp[k] : avgUp;
                double pd = _nDown[k] > 0 ? _sumDown[k] / _nDown[k] : avgDown;
                double score = Math.Max(f * pd, 1e-6) * Math.Max((1 - f) * pu, 1e-6);
                if (score > best + 1e-15) { best = score; pick = k; }
            }
            return pick;
        }
    }
}
