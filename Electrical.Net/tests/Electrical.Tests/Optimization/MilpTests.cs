using Electrical.Optimization;
using Electrical.Optimization.Highs;
using Electrical.Optimization.OrTools;
using Xunit.Abstractions;

namespace Electrical.Tests;

/// <summary>LP / MILP abstraction, built-in dual simplex and branch and bound, HiGHS and OR-Tools adapters.</summary>
public class MilpTests(ITestOutputHelper output)
{
    private static readonly SimplexSolver Simplex = new();
    private static readonly BranchAndBoundSolver Bnb = new();

    /// <summary>Hillier &amp; Lieberman "Wyndor Glass" LP: max 3x + 5y, x ≤ 4, 2y ≤ 12, 3x + 2y ≤ 18 → (2, 6), z = 36, shadow prices (0, 1.5, 1).</summary>
    internal static (LinearModel Model, ConstraintRef[] Rows) Wyndor()
    {
        var m = new LinearModel { Name = "wyndor" };
        var x = m.AddContinuous(name: "x");
        var y = m.AddContinuous(name: "y");
        var r = new[]
        {
            m.AddConstraint(x, ConstraintSense.LessOrEqual, 4, "plant1"),
            m.AddConstraint(2 * y, ConstraintSense.LessOrEqual, 12, "plant2"),
            m.AddConstraint(3 * x + 2 * y, ConstraintSense.LessOrEqual, 18, "plant3"),
        };
        m.SetObjective(3 * x + 5 * y, ObjectiveSense.Maximize);
        return (m, r);
    }

    /// <summary>min 2x + 3y + 0.5z, x + y ≥ 4, x + 3y ≥ 6, z − x = −1, z free → (3, 1, 2), z* = 10, duals (2.25, 0.25, 0.5).</summary>
    internal static LinearModel MixedSenses()
    {
        var m = new LinearModel();
        var x = m.AddContinuous();
        var y = m.AddContinuous();
        var z = m.AddContinuous(double.NegativeInfinity, double.PositiveInfinity, "z");
        m.AddConstraint(x + y, ConstraintSense.GreaterOrEqual, 4);
        m.AddConstraint(x + 3 * y, ConstraintSense.GreaterOrEqual, 6);
        m.AddConstraint(z - x, ConstraintSense.Equal, -1);
        m.SetObjective(2 * x + 3 * y + 0.5 * z, ObjectiveSense.Minimize);
        return m;
    }

    /// <summary>0/1 knapsack with deterministic data.</summary>
    internal static (LinearModel Model, double[] V, double[] W, double Cap) Knapsack(int n, int seed)
    {
        var rnd = new Random(seed);
        var v = Enumerable.Range(0, n).Select(_ => (double)rnd.Next(10, 60)).ToArray();
        var w = Enumerable.Range(0, n).Select(_ => (double)rnd.Next(5, 40)).ToArray();
        double cap = Math.Round(w.Sum() * 0.4);
        var m = new LinearModel { Name = "knapsack" };
        var x = Enumerable.Range(0, n).Select(i => m.AddBinary($"x{i}")).ToArray();
        var e = new LinearExpression();
        var o = new LinearExpression();
        for (int i = 0; i < n; i++) { e.Add(x[i], w[i]); o.Add(x[i], v[i]); }
        m.AddConstraint(e, ConstraintSense.LessOrEqual, cap);
        m.SetObjective(o, ObjectiveSense.Maximize);
        return (m, v, w, cap);
    }

    /// <summary>Small general-integer program: max 5a + 4b + 3c, 2a + 3b + c ≤ 5.5, 4a + b + 2c ≤ 11.3, 3a + 4b + 2c ≤ 8.7, 0 ≤ a,b,c ≤ 5 integer.</summary>
    internal static LinearModel IntegerProgram()
    {
        var m = new LinearModel();
        var a = m.AddInteger(0, 5);
        var b = m.AddInteger(0, 5);
        var c = m.AddInteger(0, 5);
        m.AddConstraint(2 * a + 3 * b + 1 * c, ConstraintSense.LessOrEqual, 5.5);
        m.AddConstraint(4 * a + 1 * b + 2 * c, ConstraintSense.LessOrEqual, 11.3);
        m.AddConstraint(3 * a + 4 * b + 2 * c, ConstraintSense.LessOrEqual, 8.7);
        m.SetObjective(5 * a + 4 * b + 3 * c, ObjectiveSense.Maximize);
        return m;
    }

    [Fact]
    public void Simplex_Wyndor_OptimumAndShadowPrices()
    {
        var (m, rows) = Wyndor();
        var s = Simplex.SolveLp(m);
        Assert.Equal(SolveStatus.Optimal, s.Status);
        Assert.Equal(36, s.Objective, 9);
        Assert.Equal(2, s.Values[0], 9);
        Assert.Equal(6, s.Values[1], 9);
        Assert.Equal(0, s.Dual(rows[0]), 9);
        Assert.Equal(1.5, s.Dual(rows[1]), 9);
        Assert.Equal(1, s.Dual(rows[2]), 9);
        // dual objective b·y equals the primal objective (strong duality)
        Assert.Equal(36, 4 * s.Duals![0] + 12 * s.Duals[1] + 18 * s.Duals[2], 9);
    }

    [Fact]
    public void Simplex_MixedSensesFreeVariable_Duals()
    {
        var s = Simplex.SolveLp(MixedSenses());
        Assert.Equal(SolveStatus.Optimal, s.Status);
        Assert.Equal(10, s.Objective, 9);
        Assert.Equal(new double[] { 3, 1, 2 }, s.Values.Select(v => Math.Round(v, 9)));
        Assert.Equal(2.25, s.Duals![0], 9);
        Assert.Equal(0.25, s.Duals[1], 9);
        Assert.Equal(0.5, s.Duals[2], 9);
        Assert.All(s.ReducedCosts!, rc => Assert.Equal(0, rc, 9)); // all variables basic
    }

    [Fact]
    public void Simplex_BoundedVariables_ReducedCosts()
    {
        // max x + 2y, x + y ≤ 10, 0 ≤ x ≤ 8, 0 ≤ y ≤ 3 → y = 3 at its upper bound with reduced cost 2 − 1 = 1, x = 7, z = 13
        var m = new LinearModel();
        var x = m.AddContinuous(0, 8);
        var y = m.AddContinuous(0, 3);
        var c = m.AddConstraint(x + y, ConstraintSense.LessOrEqual, 10);
        m.SetObjective(x + 2 * y, ObjectiveSense.Maximize);
        var s = Simplex.SolveLp(m);
        Assert.Equal(13, s.Objective, 9);
        Assert.Equal(7, s.Value(x), 9);
        Assert.Equal(1, s.Dual(c), 9);
        Assert.Equal(1, s.ReducedCost(y), 9);
        Assert.Equal(0, s.ReducedCost(x), 9);
    }

    [Fact]
    public void Simplex_DetectsInfeasibleAndUnbounded()
    {
        var m = new LinearModel();
        var x = m.AddContinuous();
        var y = m.AddContinuous();
        m.AddConstraint(x + y, ConstraintSense.LessOrEqual, 1);
        m.AddConstraint(x + y, ConstraintSense.GreaterOrEqual, 2);
        m.SetObjective(x + y, ObjectiveSense.Minimize);
        Assert.Equal(SolveStatus.Infeasible, Simplex.SolveLp(m).Status);
        Assert.Equal(SolveStatus.Infeasible, Bnb.Solve(m).Status);

        var u = new LinearModel();
        var a = u.AddContinuous();
        var b = u.AddContinuous();
        u.AddConstraint(a - b, ConstraintSense.LessOrEqual, 1);
        u.SetObjective(a + b, ObjectiveSense.Maximize);
        Assert.Equal(SolveStatus.Unbounded, Simplex.SolveLp(u).Status);
    }

    [Fact]
    public void Simplex_BealeCyclingExample_Terminates()
    {
        // Beale (1955): cycles under the textbook Dantzig rule; optimum −1/20 at x = (1/25, 0, 1, 0)
        var m = new LinearModel();
        var x = Enumerable.Range(0, 4).Select(_ => m.AddContinuous()).ToArray();
        m.AddConstraint(0.25 * x[0] - 60 * x[1] - 0.04 * x[2] + 9 * x[3], ConstraintSense.LessOrEqual, 0);
        m.AddConstraint(0.5 * x[0] - 90 * x[1] - 0.02 * x[2] + 3 * x[3], ConstraintSense.LessOrEqual, 0);
        m.AddConstraint(x[2], ConstraintSense.LessOrEqual, 1);
        m.SetObjective(-0.75 * x[0] + 150 * x[1] - 0.02 * x[2] + 6 * x[3], ObjectiveSense.Minimize);
        var s = Simplex.SolveLp(m);
        Assert.Equal(SolveStatus.Optimal, s.Status);
        Assert.Equal(-0.05, s.Objective, 9);
        Assert.Equal(0.04, s.Values[0], 9);
        Assert.Equal(1, s.Values[2], 9);
    }

    [Fact]
    public void Simplex_RandomLps_SatisfyKktConditions()
    {
        var rnd = new Random(11);
        for (int trial = 0; trial < 20; trial++)
        {
            int n = rnd.Next(3, 15), mRows = rnd.Next(2, 12);
            var m = new LinearModel();
            var x = Enumerable.Range(0, n).Select(_ => m.AddContinuous(0, rnd.Next(1, 20))).ToArray();
            for (int i = 0; i < mRows; i++)
            {
                var e = new LinearExpression();
                foreach (var v in x) if (rnd.NextDouble() < 0.6) e.Add(v, rnd.Next(-5, 10));
                var sense = (ConstraintSense)rnd.Next(3);
                double rhs = sense == ConstraintSense.LessOrEqual ? rnd.Next(5, 50) : rnd.Next(0, 10);
                m.AddConstraint(e, sense, rhs);
            }
            var o = new LinearExpression();
            foreach (var v in x) o.Add(v, rnd.Next(-10, 10));
            m.SetObjective(o, trial % 2 == 0 ? ObjectiveSense.Minimize : ObjectiveSense.Maximize);
            var s = Simplex.SolveLp(m);
            if (s.Status != SolveStatus.Optimal) { Assert.Equal(SolveStatus.Infeasible, s.Status); continue; }
            Assert.True(m.MaxViolation(s.Values) < 1e-7);
            // stationarity: c_j − Σ_i y_i a_ij = reduced cost_j; dual objective = primal objective
            var rc = m.Variables.Select(v => v.Cost).ToArray();
            for (int i = 0; i < m.ConstraintCount; i++)
            {
                var r = m.Constraints[i];
                for (int k = 0; k < r.Indices.Length; k++) rc[r.Indices[k]] -= s.Duals![i] * r.Coefficients[k];
            }
            double dualObj = 0;
            for (int i = 0; i < m.ConstraintCount; i++) dualObj += s.Duals![i] * m.Constraints[i].Rhs;
            for (int j = 0; j < n; j++)
            {
                Assert.Equal(rc[j], s.ReducedCosts![j], 6);
                dualObj += s.ReducedCosts[j] * s.Values[j];
            }
            Assert.Equal(s.Objective, dualObj, 6);
        }
    }

    [Fact]
    public void BranchAndBound_KnapsackMatchesBruteForce()
    {
        for (int seed = 1; seed <= 3; seed++)
        {
            var (m, v, w, cap) = Knapsack(14, seed);
            double best = 0;
            for (int mask = 0; mask < 1 << v.Length; mask++)
            {
                double wt = 0, val = 0;
                for (int i = 0; i < v.Length; i++) if ((mask >> i & 1) != 0) { wt += w[i]; val += v[i]; }
                if (wt <= cap && val > best) best = val;
            }
            var s = Bnb.Solve(m);
            output.WriteLine($"seed {seed}: brute force {best}, B&B {s}");
            Assert.Equal(SolveStatus.Optimal, s.Status);
            Assert.Equal(best, s.Objective, 6);
            Assert.True(m.MaxViolation(s.Values) < 1e-9);
            Assert.All(s.Values, x => Assert.True(x is 0 or 1));
            Assert.Null(s.Duals);
        }
    }

    [Fact]
    public void BranchAndBound_GeneralIntegersMatchEnumeration()
    {
        var m = IntegerProgram();
        double best = double.NegativeInfinity;
        for (int a = 0; a <= 5; a++)
            for (int b = 0; b <= 5; b++)
                for (int c = 0; c <= 5; c++)
                {
                    double[] pt = [a, b, c];
                    if (m.MaxViolation(pt) <= 0) best = Math.Max(best, m.EvaluateObjective(pt));
                }
        var s = Bnb.Solve(m);
        Assert.Equal(SolveStatus.Optimal, s.Status);
        Assert.Equal(best, s.Objective, 9);
        Assert.True(s.Nodes >= 1);
        Assert.True(s.MipGap <= SolverOptions.Default.RelativeGap);
    }

    public static TheoryData<string> ExternalSolvers => new() { "HiGHS", "OR-Tools" };

    internal static IMilpSolver External(string name) => name == "HiGHS" ? new HighsSolver() : new OrToolsSolver();

    [Theory]
    [MemberData(nameof(ExternalSolvers))]
    public void Adapters_LpParity_ObjectiveAndDuals(string name)
    {
        var ext = External(name);
        var lp = (ILpSolver)ext;
        foreach (var model in new[] { Wyndor().Model, MixedSenses() })
        {
            var a = Simplex.SolveLp(model);
            var b = lp.SolveLp(model);
            output.WriteLine($"{name}: {b}  duals [{string.Join(", ", b.Duals!.Select(d => d.ToString("0.####")))}]");
            Assert.Equal(SolveStatus.Optimal, b.Status);
            Assert.Equal(a.Objective, b.Objective, 6);
            for (int i = 0; i < model.ConstraintCount; i++) Assert.Equal(a.Duals![i], b.Duals![i], 6);
        }
    }

    [Theory]
    [MemberData(nameof(ExternalSolvers))]
    public void Adapters_MilpParity_KnapsackAndIntegerProgram(string name)
    {
        var ext = External(name);
        foreach (var model in new[] { Knapsack(14, 2).Model, Knapsack(20, 5).Model, IntegerProgram() })
        {
            var a = Bnb.Solve(model);
            var b = ext.Solve(model);
            output.WriteLine($"built-in {a}\n{name} {b}");
            Assert.Equal(SolveStatus.Optimal, b.Status);
            Assert.Equal(a.Objective, b.Objective, 6);
        }
    }
}
