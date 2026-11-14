using System.Diagnostics;
using Electrical.Core.Hosting;
using Google.OrTools.LinearSolver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrVariable = Google.OrTools.LinearSolver.Variable;

namespace Electrical.Optimization.OrTools;

/// <summary>
/// <see cref="ILpSolver"/> / <see cref="IMilpSolver"/> backed by Google OR-Tools' linear solver wrapper (<c>Google.OrTools</c> NuGet package).
/// MILPs use <see cref="MipBackend"/> (default "SCIP"; "CBC" also bundled), LPs use <see cref="LpBackend"/> (default "GLOP", which returns duals
/// through <c>Constraint.DualValue()</c> and reduced costs through <c>Variable.ReducedCost()</c>). Duals are reported with the solver-independent
/// convention of <see cref="LinearSolution"/> (∂objective/∂rhs).
/// </summary>
public sealed class OrToolsSolver : IMilpSolver, ILpSolver
{
    /// <summary>OR-Tools solver id for models with integer variables ("SCIP", "CBC", "SAT", ...).</summary>
    public string MipBackend { get; init; } = "SCIP";
    /// <summary>OR-Tools solver id for continuous models ("GLOP", "PDLP", "CLP", ...).</summary>
    public string LpBackend { get; init; } = "GLOP";

    public string Name => $"OR-Tools ({MipBackend}/{LpBackend})";

    public LinearSolution SolveLp(LinearModel model, SolverOptions? options = null) => Run(model, options ?? SolverOptions.Default, integer: false);

    public LinearSolution Solve(LinearModel model, SolverOptions? options = null) => Run(model, options ?? SolverOptions.Default, model.IsMip);

    private LinearSolution Run(LinearModel model, SolverOptions options, bool integer)
    {
        var sw = Stopwatch.StartNew();
        string backend = integer ? MipBackend : LpBackend;
        using var solver = Solver.CreateSolver(backend)
                           ?? throw new InvalidOperationException($"OR-Tools solver '{backend}' is not available in this build of Google.OrTools.");
        if (options.Verbose) solver.EnableOutput(); else solver.SuppressOutput();
        if (options.TimeLimit is { } t) solver.SetTimeLimit((long)Math.Ceiling(t.TotalMilliseconds));
        if (options.Threads > 0) solver.SetNumThreads(options.Threads);
        int n = model.VariableCount, m = model.ConstraintCount;
        var vars = new OrVariable[n];
        for (int j = 0; j < n; j++)
        {
            var v = model.Variables[j];
            vars[j] = solver.MakeVar(v.Lower, v.Upper, integer && v.IsInteger, v.Name ?? $"x{j}");
        }
        var rows = new Google.OrTools.LinearSolver.Constraint[m];
        for (int i = 0; i < m; i++)
        {
            var r = model.Constraints[i];
            var c = solver.MakeConstraint(r.Lower, r.Upper, r.Name ?? $"r{i}");
            for (int k = 0; k < r.Indices.Length; k++) c.SetCoefficient(vars[r.Indices[k]], r.Coefficients[k]);
            rows[i] = c;
        }
        var obj = solver.Objective();
        for (int j = 0; j < n; j++) if (model.Variables[j].Cost != 0) obj.SetCoefficient(vars[j], model.Variables[j].Cost);
        obj.SetOffset(model.ObjectiveConstant);
        if (model.Sense == ObjectiveSense.Maximize) obj.SetMaximization(); else obj.SetMinimization();

        using var prm = new MPSolverParameters();
        if (integer) prm.SetDoubleParam(MPSolverParameters.DoubleParam.RELATIVE_MIP_GAP, options.RelativeGap);
        var rs = solver.Solve(prm);
        bool overTime = options.TimeLimit is { } tl && sw.Elapsed >= tl;
        var status = rs switch
        {
            Solver.ResultStatus.OPTIMAL => SolveStatus.Optimal,
            Solver.ResultStatus.FEASIBLE => overTime ? SolveStatus.TimeLimit : SolveStatus.Feasible,
            Solver.ResultStatus.INFEASIBLE => SolveStatus.Infeasible,
            Solver.ResultStatus.UNBOUNDED => SolveStatus.Unbounded,
            Solver.ResultStatus.NOT_SOLVED when overTime => SolveStatus.TimeLimit,
            _ => SolveStatus.Error,
        };
        bool hasSolution = rs is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE;
        if (!hasSolution)
            return new LinearSolution { Status = status, Solver = Name, SolveTime = sw.Elapsed, Message = rs.ToString() };
        var x = vars.Select(v => v.SolutionValue()).ToArray();
        double value = model.EvaluateObjective(x);
        double[]? duals = null, rc = null;
        if (!integer)
        {
            duals = rows.Select(c => c.DualValue()).ToArray();
            rc = vars.Select(v => v.ReducedCost()).ToArray();
        }
        double bound = integer ? obj.BestBound() : value;
        double gap = integer ? Math.Abs(value - bound) / Math.Max(1, Math.Abs(value)) : 0;
        return new LinearSolution
        {
            Status = status, Objective = value, Values = x, Duals = duals, ReducedCosts = rc, BestBound = bound, MipGap = gap,
            Iterations = solver.Iterations(), Nodes = integer ? solver.Nodes() : 0, SolveTime = sw.Elapsed, Solver = Name, Message = rs.ToString(),
        };
    }
}

public static class OrToolsBuilderExtensions
{
    /// <summary>Makes <see cref="OrToolsSolver"/> the <see cref="IMilpSolver"/> and <see cref="ILpSolver"/> (replacing the built-in default).</summary>
    public static ElectricalBuilder AddOrTools(this ElectricalBuilder b, string mipBackend = "SCIP", string lpBackend = "GLOP")
    {
        b.TryAddModule("Optimization.OrTools");
        var solver = new OrToolsSolver { MipBackend = mipBackend, LpBackend = lpBackend };
        b.Services.RemoveAll<IMilpSolver>();
        b.Services.RemoveAll<ILpSolver>();
        b.Services.AddSingleton<IMilpSolver>(solver);
        b.Services.AddSingleton<ILpSolver>(solver);
        return b;
    }
}
