using System.Diagnostics;
using Electrical.Core.Hosting;
using Highs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Optimization.Highs;

/// <summary>
/// <see cref="ILpSolver"/> / <see cref="IMilpSolver"/> backed by the HiGHS open-source solver (ERGO-Code, MIT) through the managed wrapper of
/// the <c>Highs.Native</c> NuGet package (<c>Highs.HighsLpSolver</c>, native <c>highs</c> library for win/linux/osx x64/arm64).
/// LPs are solved with HiGHS' dual simplex by default and return row duals / column duals; MILPs use HiGHS' branch and cut.
/// Duals are converted to the solver-independent convention of <see cref="LinearSolution"/> (∂objective/∂rhs).
/// </summary>
public sealed class HighsSolver : IMilpSolver, ILpSolver
{
    /// <summary>HiGHS option <c>solver</c> for LPs ("simplex", "ipm", "pdlp" or "choose").</summary>
    public string LpAlgorithm { get; init; } = "simplex";

    static HighsSolver() => HighsNativeLoader.EnsureInstalled();

    public string Name => "HiGHS";

    public LinearSolution SolveLp(LinearModel model, SolverOptions? options = null) => Run(model, options ?? SolverOptions.Default, integer: false);

    public LinearSolution Solve(LinearModel model, SolverOptions? options = null) => Run(model, options ?? SolverOptions.Default, model.IsMip);

    private LinearSolution Run(LinearModel model, SolverOptions options, bool integer)
    {
        var sw = Stopwatch.StartNew();
        int n = model.VariableCount, m = model.ConstraintCount;
        var hm = new HighsModel
        {
            sense = model.Sense == ObjectiveSense.Maximize ? HighsObjectiveSense.kMaximize : HighsObjectiveSense.kMinimize,
            offset = model.ObjectiveConstant,
            colcost = new double[n], collower = new double[n], colupper = new double[n],
            rowlower = new double[m], rowupper = new double[m],
            a_format = HighsMatrixFormat.kRowwise,
        };
        for (int j = 0; j < n; j++)
        {
            var v = model.Variables[j];
            hm.colcost[j] = v.Cost;
            hm.collower[j] = v.Lower;
            hm.colupper[j] = v.Upper;
        }
        int nnz = model.NonZeros;
        hm.astart = new int[m];
        hm.aindex = new int[nnz];
        hm.avalue = new double[nnz];
        for (int i = 0, p = 0; i < m; i++)
        {
            var r = model.Constraints[i];
            hm.rowlower[i] = r.Lower;
            hm.rowupper[i] = r.Upper;
            hm.astart[i] = p;
            r.Indices.CopyTo(hm.aindex, p);
            r.Coefficients.CopyTo(hm.avalue, p);
            p += r.Indices.Length;
        }
        if (integer)
            hm.highs_integrality = model.Variables.Select(v => v.IsInteger ? (int)HighsIntegrality.kInteger : (int)HighsIntegrality.kContinuous).ToArray();

        using var solver = new HighsLpSolver();
        solver.setBoolOptionValue("output_flag", options.Verbose ? 1 : 0);
        if (options.TimeLimit is { } t) solver.setDoubleOptionValue("time_limit", t.TotalSeconds);
        if (options.Threads > 0) solver.setIntOptionValue("threads", options.Threads);
        if (integer) solver.setDoubleOptionValue("mip_rel_gap", options.RelativeGap);
        else solver.setStringOptionValue("solver", LpAlgorithm);

        var pass = integer ? solver.passMip(hm) : solver.passLp(hm);
        if (pass == HighsStatus.kError)
            return new LinearSolution { Status = SolveStatus.Error, Solver = Name, SolveTime = sw.Elapsed, Message = "HiGHS rejected the model" };
        var run = solver.run();
        var ms = solver.GetModelStatus();
        var info = solver.getInfo();
        var status = ms switch
        {
            HighsModelStatus.kOptimal => SolveStatus.Optimal,
            HighsModelStatus.kInfeasible => SolveStatus.Infeasible,
            HighsModelStatus.kUnbounded or HighsModelStatus.kUnboundedOrInfeasible => SolveStatus.Unbounded,
            HighsModelStatus.kTimeLimit => SolveStatus.TimeLimit,
            HighsModelStatus.kIterationLimit or HighsModelStatus.kSolutionLimit => SolveStatus.Feasible,
            HighsModelStatus.kModelEmpty => SolveStatus.Optimal,
            _ => SolveStatus.Error,
        };
        if (run == HighsStatus.kError && status == SolveStatus.Optimal) status = SolveStatus.Error;
        bool hasSolution = status is SolveStatus.Optimal or SolveStatus.Feasible
                           || (status == SolveStatus.TimeLimit && integer && !double.IsInfinity(info.ObjectiveValue) && Math.Abs(info.ObjectiveValue) < 1e30);
        if (!hasSolution)
            return new LinearSolution { Status = status, Solver = Name, SolveTime = sw.Elapsed, Message = ms.ToString(), Nodes = integer ? info.NodeCount : 0 };
        var sol = solver.getSolution();
        var x = sol.colvalue.Take(n).ToArray();
        if (integer && status == SolveStatus.Optimal && info.MipGap > options.RelativeGap * 1.0001 && info.MipGap < double.PositiveInfinity)
            status = SolveStatus.Feasible;
        double obj = model.EvaluateObjective(x);
        double[]? duals = null, rc = null;
        if (!integer)
        {
            // HiGHS duals: row_dual = ∂objective/∂(row bound), col_dual = reduced cost, both in the objective's own sense
            duals = sol.rowdual.Take(m).ToArray();
            rc = sol.coldual.Take(n).ToArray();
        }
        return new LinearSolution
        {
            Status = status, Objective = obj, Values = x, Duals = duals, ReducedCosts = rc,
            BestBound = integer ? info.DualBound : obj, MipGap = integer ? info.MipGap : 0,
            Iterations = info.SimplexIterationCount + info.IpmIterationCount, Nodes = integer ? info.NodeCount : 0,
            SolveTime = sw.Elapsed, Solver = Name, Message = ms.ToString(),
        };
    }
}

public static class HighsBuilderExtensions
{
    /// <summary>Makes <see cref="HighsSolver"/> the <see cref="IMilpSolver"/> and <see cref="ILpSolver"/> (replacing the built-in default).</summary>
    public static ElectricalBuilder AddHighs(this ElectricalBuilder b)
    {
        b.TryAddModule("Optimization.Highs");
        var solver = new HighsSolver();
        b.Services.RemoveAll<IMilpSolver>();
        b.Services.RemoveAll<ILpSolver>();
        b.Services.AddSingleton<IMilpSolver>(solver);
        b.Services.AddSingleton<ILpSolver>(solver);
        return b;
    }
}
