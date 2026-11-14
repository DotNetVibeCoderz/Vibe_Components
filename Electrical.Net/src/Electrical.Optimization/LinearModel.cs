namespace Electrical.Optimization;

/// <summary>Domain of a decision variable in a <see cref="LinearModel"/>.</summary>
public enum VariableType
{
    /// <summary>Real-valued variable.</summary>
    Continuous,
    /// <summary>Integer-valued variable.</summary>
    Integer,
    /// <summary>Integer variable restricted to {0, 1}.</summary>
    Binary,
}

/// <summary>Relation of a linear constraint: expression ≤ / = / ≥ right-hand side.</summary>
public enum ConstraintSense { LessOrEqual, Equal, GreaterOrEqual }

/// <summary>Optimization direction of the objective.</summary>
public enum ObjectiveSense { Minimize, Maximize }

/// <summary>Outcome of a solve.</summary>
public enum SolveStatus
{
    /// <summary>Proven optimal (for a MILP: within the requested relative gap).</summary>
    Optimal,
    /// <summary>A feasible solution was found but optimality was not proven (e.g. node or iteration limit).</summary>
    Feasible,
    /// <summary>The model has no feasible point.</summary>
    Infeasible,
    /// <summary>The objective is unbounded in the optimization direction.</summary>
    Unbounded,
    /// <summary>Stopped by the time limit; <see cref="LinearSolution.HasSolution"/> tells whether an incumbent exists.</summary>
    TimeLimit,
    /// <summary>Stopped by the iteration limit without a solution.</summary>
    IterationLimit,
    /// <summary>Numerical failure or solver error (see <see cref="LinearSolution.Message"/>).</summary>
    Error,
}

/// <summary>Handle of a variable in a <see cref="LinearModel"/> (column index).</summary>
public readonly record struct Variable(int Index)
{
    public static LinearExpression operator *(double a, Variable x) => new LinearExpression().Add(x, a);
    public static LinearExpression operator *(Variable x, double a) => new LinearExpression().Add(x, a);
    public static LinearExpression operator +(Variable x, Variable y) => new LinearExpression().Add(x, 1).Add(y, 1);
    public static LinearExpression operator -(Variable x, Variable y) => new LinearExpression().Add(x, 1).Add(y, -1);
    public static implicit operator LinearExpression(Variable x) => new LinearExpression().Add(x, 1);
}

/// <summary>Handle of a constraint in a <see cref="LinearModel"/> (row index).</summary>
public readonly record struct ConstraintRef(int Index);

/// <summary>Linear expression Σ a_j·x_j + constant. Repeated variables are summed when the expression is added to a model.</summary>
public sealed class LinearExpression
{
    private readonly List<int> _vars = [];
    private readonly List<double> _coefs = [];

    /// <summary>Constant term (moves to the right-hand side in constraints).</summary>
    public double Constant { get; set; }

    public int Count => _vars.Count;
    public IReadOnlyList<int> VariableIndices => _vars;
    public IReadOnlyList<double> Coefficients => _coefs;

    /// <summary>Adds a·x (in place) and returns this expression.</summary>
    public LinearExpression Add(Variable x, double a = 1)
    {
        if (a != 0) { _vars.Add(x.Index); _coefs.Add(a); }
        return this;
    }

    /// <summary>Adds a constant (in place) and returns this expression.</summary>
    public LinearExpression AddConstant(double c) { Constant += c; return this; }

    /// <summary>Adds k·e (in place) and returns this expression.</summary>
    public LinearExpression Add(LinearExpression e, double k = 1)
    {
        for (int i = 0; i < e._vars.Count; i++) if (e._coefs[i] * k != 0) { _vars.Add(e._vars[i]); _coefs.Add(e._coefs[i] * k); }
        Constant += k * e.Constant;
        return this;
    }

    public LinearExpression Clone() => new LinearExpression { Constant = Constant }.Add(this);

    public static LinearExpression operator +(LinearExpression a, LinearExpression b) => a.Clone().Add(b);
    public static LinearExpression operator -(LinearExpression a, LinearExpression b) => a.Clone().Add(b, -1);
    public static LinearExpression operator +(LinearExpression a, double c) => a.Clone().AddConstant(c);
    public static LinearExpression operator -(LinearExpression a, double c) => a.Clone().AddConstant(-c);
    public static LinearExpression operator *(double k, LinearExpression a) => new LinearExpression().Add(a, k);
}

/// <summary>Column data of a <see cref="LinearModel"/>.</summary>
public sealed class VariableInfo
{
    public double Lower { get; internal set; }
    public double Upper { get; internal set; }
    public VariableType Type { get; internal set; }
    public string? Name { get; internal set; }
    /// <summary>Objective coefficient.</summary>
    public double Cost { get; internal set; }
    public bool IsInteger => Type != VariableType.Continuous;
}

/// <summary>Row data of a <see cref="LinearModel"/>: Σ Coefficients[k]·x[Indices[k]] (Sense) Rhs. Indices are unique.</summary>
public sealed class ConstraintInfo
{
    public required int[] Indices { get; init; }
    public required double[] Coefficients { get; init; }
    public ConstraintSense Sense { get; internal set; }
    public double Rhs { get; internal set; }
    public string? Name { get; init; }

    /// <summary>Row lower bound (−∞ for ≤).</summary>
    public double Lower => Sense == ConstraintSense.LessOrEqual ? double.NegativeInfinity : Rhs;
    /// <summary>Row upper bound (+∞ for ≥).</summary>
    public double Upper => Sense == ConstraintSense.GreaterOrEqual ? double.PositiveInfinity : Rhs;
}

/// <summary>
/// Solver-independent linear / mixed-integer linear model: variables with bounds and types, linear constraints (≤, =, ≥) and a linear
/// objective (minimize or maximize). Solve it with any <see cref="ILpSolver"/> or <see cref="IMilpSolver"/>
/// (built-in <see cref="SimplexSolver"/> / <see cref="BranchAndBoundSolver"/>, or the HiGHS / OR-Tools adapters).
/// </summary>
/// <example><code>
/// var m = new LinearModel();
/// var x = m.AddVariable(0, 4, name: "x");
/// var y = m.AddVariable(0, double.PositiveInfinity, VariableType.Integer, "y");
/// m.AddConstraint(x + y, ConstraintSense.LessOrEqual, 5);
/// m.SetObjective(3 * x + 2 * y, ObjectiveSense.Maximize);
/// var sol = new BranchAndBoundSolver().Solve(m);
/// </code></example>
public sealed class LinearModel
{
    private readonly List<VariableInfo> _vars = [];
    private readonly List<ConstraintInfo> _rows = [];

    /// <summary>Optional model name (passed to external solvers).</summary>
    public string? Name { get; set; }
    public ObjectiveSense Sense { get; set; } = ObjectiveSense.Minimize;
    /// <summary>Constant term of the objective.</summary>
    public double ObjectiveConstant { get; set; }

    public int VariableCount => _vars.Count;
    public int ConstraintCount => _rows.Count;
    public IReadOnlyList<VariableInfo> Variables => _vars;
    public IReadOnlyList<ConstraintInfo> Constraints => _rows;
    /// <summary>True when at least one variable is integer or binary.</summary>
    public bool IsMip => _vars.Any(v => v.IsInteger);
    /// <summary>Number of structural non-zeros.</summary>
    public int NonZeros => _rows.Sum(r => r.Indices.Length);

    /// <summary>Adds a variable with bounds [lower, upper] (use ±∞ for free directions). Binary variables are clamped to [0, 1].</summary>
    public Variable AddVariable(double lower = 0, double upper = double.PositiveInfinity, VariableType type = VariableType.Continuous,
        string? name = null)
    {
        if (type == VariableType.Binary) { lower = Math.Max(lower, 0); upper = Math.Min(upper, 1); }
        if (double.IsNaN(lower) || double.IsNaN(upper)) throw new ArgumentException("Variable bounds must not be NaN.");
        if (lower > upper) throw new ArgumentException($"Variable {name}: lower bound {lower} exceeds upper bound {upper}.");
        _vars.Add(new VariableInfo { Lower = lower, Upper = upper, Type = type, Name = name });
        return new Variable(_vars.Count - 1);
    }

    public Variable AddContinuous(double lower = 0, double upper = double.PositiveInfinity, string? name = null) =>
        AddVariable(lower, upper, VariableType.Continuous, name);
    public Variable AddInteger(double lower = 0, double upper = double.PositiveInfinity, string? name = null) =>
        AddVariable(lower, upper, VariableType.Integer, name);
    public Variable AddBinary(string? name = null) => AddVariable(0, 1, VariableType.Binary, name);

    /// <summary>Adds the constraint expr (sense) rhs; the expression constant is moved to the right-hand side.</summary>
    public ConstraintRef AddConstraint(LinearExpression expr, ConstraintSense sense, double rhs, string? name = null)
    {
        var acc = new Dictionary<int, double>();
        for (int k = 0; k < expr.Count; k++)
        {
            int j = expr.VariableIndices[k];
            if ((uint)j >= (uint)_vars.Count) throw new ArgumentException($"Variable index {j} is not part of this model.");
            acc[j] = acc.GetValueOrDefault(j) + expr.Coefficients[k];
        }
        var idx = acc.Where(kv => kv.Value != 0).Select(kv => kv.Key).Order().ToArray();
        _rows.Add(new ConstraintInfo
        {
            Indices = idx, Coefficients = idx.Select(j => acc[j]).ToArray(), Sense = sense, Rhs = rhs - expr.Constant, Name = name,
        });
        return new ConstraintRef(_rows.Count - 1);
    }

    /// <summary>Adds Σ coefficients[k]·variables[k] (sense) rhs.</summary>
    public ConstraintRef AddConstraint(ReadOnlySpan<Variable> variables, ReadOnlySpan<double> coefficients, ConstraintSense sense, double rhs,
        string? name = null)
    {
        if (variables.Length != coefficients.Length) throw new ArgumentException("variables and coefficients differ in length.");
        var e = new LinearExpression();
        for (int k = 0; k < variables.Length; k++) e.Add(variables[k], coefficients[k]);
        return AddConstraint(e, sense, rhs, name);
    }

    /// <summary>Replaces the objective by expr (its constant becomes <see cref="ObjectiveConstant"/>).</summary>
    public void SetObjective(LinearExpression expr, ObjectiveSense sense)
    {
        foreach (var v in _vars) v.Cost = 0;
        for (int k = 0; k < expr.Count; k++) _vars[expr.VariableIndices[k]].Cost += expr.Coefficients[k];
        ObjectiveConstant = expr.Constant;
        Sense = sense;
    }

    /// <summary>Sets the objective coefficient of one variable.</summary>
    public void SetObjectiveCoefficient(Variable x, double cost) => _vars[x.Index].Cost = cost;

    public void SetBounds(Variable x, double lower, double upper)
    {
        if (lower > upper) throw new ArgumentException($"lower bound {lower} exceeds upper bound {upper}.");
        _vars[x.Index].Lower = lower;
        _vars[x.Index].Upper = upper;
    }

    public void SetType(Variable x, VariableType type) => _vars[x.Index].Type = type;

    /// <summary>Changes the right-hand side of a constraint.</summary>
    public void SetRhs(ConstraintRef c, double rhs) => _rows[c.Index].Rhs = rhs;

    /// <summary>Deep copy (used e.g. to fix integers and re-solve an LP for prices).</summary>
    public LinearModel Clone()
    {
        var m = new LinearModel { Name = Name, Sense = Sense, ObjectiveConstant = ObjectiveConstant };
        foreach (var v in _vars) m._vars.Add(new VariableInfo { Lower = v.Lower, Upper = v.Upper, Type = v.Type, Name = v.Name, Cost = v.Cost });
        foreach (var r in _rows)
            m._rows.Add(new ConstraintInfo { Indices = r.Indices, Coefficients = r.Coefficients, Sense = r.Sense, Rhs = r.Rhs, Name = r.Name });
        return m;
    }

    /// <summary>Copy in which every integer variable is fixed to the rounded value in <paramref name="values"/> and made continuous.</summary>
    public LinearModel FixIntegers(IReadOnlyList<double> values)
    {
        var m = Clone();
        for (int j = 0; j < m._vars.Count; j++)
        {
            var v = m._vars[j];
            if (!v.IsInteger) continue;
            double r = Math.Round(values[j]);
            v.Lower = v.Upper = r;
            v.Type = VariableType.Continuous;
        }
        return m;
    }

    /// <summary>Objective value of a point (including the constant).</summary>
    public double EvaluateObjective(IReadOnlyList<double> x)
    {
        double s = ObjectiveConstant;
        for (int j = 0; j < _vars.Count; j++) s += _vars[j].Cost * x[j];
        return s;
    }

    /// <summary>Largest bound or constraint violation of a point (integrality not checked).</summary>
    public double MaxViolation(IReadOnlyList<double> x)
    {
        double worst = 0;
        for (int j = 0; j < _vars.Count; j++)
            worst = Math.Max(worst, Math.Max(_vars[j].Lower - x[j], x[j] - _vars[j].Upper));
        foreach (var r in _rows)
        {
            double a = 0;
            for (int k = 0; k < r.Indices.Length; k++) a += r.Coefficients[k] * x[r.Indices[k]];
            worst = Math.Max(worst, Math.Max(r.Lower - a, a - r.Upper));
        }
        return worst;
    }
}

/// <summary>Options shared by all LP / MILP solvers.</summary>
public sealed record SolverOptions
{
    /// <summary>Wall-clock time limit (null = none).</summary>
    public TimeSpan? TimeLimit { get; init; }
    /// <summary>Relative MIP gap |incumbent − bound| / max(1, |incumbent|) at which a MILP is declared optimal.</summary>
    public double RelativeGap { get; init; } = 1e-6;
    /// <summary>Thread count for solvers that support it (0 = solver default). The built-in solvers are single-threaded.</summary>
    public int Threads { get; init; }
    /// <summary>Let external solvers print their log.</summary>
    public bool Verbose { get; init; }
    /// <summary>Maximum branch-and-bound nodes of the built-in MILP solver.</summary>
    public int MaxNodes { get; init; } = 1_000_000;

    public static SolverOptions Default { get; } = new();
}

/// <summary>
/// Result of a solve. Dual conventions are solver-independent: <see cref="Duals"/>[i] = ∂objective/∂rhs_i and
/// <see cref="ReducedCosts"/>[j] = ∂objective/∂(active bound of x_j), in the model's own objective sense (for a minimisation a binding ≤ row has a
/// dual ≤ 0 and a binding ≥ row a dual ≥ 0). Duals are only reported for continuous models (LPs).
/// </summary>
public sealed class LinearSolution
{
    public required SolveStatus Status { get; init; }
    /// <summary>Objective value including <see cref="LinearModel.ObjectiveConstant"/> (NaN when no solution).</summary>
    public double Objective { get; init; } = double.NaN;
    /// <summary>Variable values (empty when no solution).</summary>
    public double[] Values { get; init; } = [];
    /// <summary>Constraint duals (LP only, else null).</summary>
    public double[]? Duals { get; init; }
    /// <summary>Reduced costs (LP only, else null).</summary>
    public double[]? ReducedCosts { get; init; }
    /// <summary>Best proven objective bound (MILP), equal to <see cref="Objective"/> for an optimal LP.</summary>
    public double BestBound { get; init; } = double.NaN;
    /// <summary>Relative MIP gap at termination (0 for LPs).</summary>
    public double MipGap { get; init; }
    /// <summary>Simplex iterations (summed over nodes).</summary>
    public long Iterations { get; init; }
    /// <summary>Branch-and-bound nodes explored (0 for LPs).</summary>
    public long Nodes { get; init; }
    public TimeSpan SolveTime { get; init; }
    /// <summary>Name of the solver that produced this result.</summary>
    public string Solver { get; init; } = "";
    public string? Message { get; init; }

    public bool HasSolution => Values.Length > 0;
    public bool IsOptimal => Status == SolveStatus.Optimal;
    public double Value(Variable x) => Values[x.Index];
    public double Dual(ConstraintRef c) => Duals is null ? double.NaN : Duals[c.Index];
    public double ReducedCost(Variable x) => ReducedCosts is null ? double.NaN : ReducedCosts[x.Index];

    public override string ToString() => $"{Solver}: {Status}, objective {Objective:G10}, gap {MipGap:P4}, {Nodes} nodes, {Iterations} iterations";
}

/// <summary>Linear-programming solver. Integrality is ignored (the continuous relaxation is solved); duals are returned.</summary>
public interface ILpSolver
{
    string Name { get; }
    LinearSolution SolveLp(LinearModel model, SolverOptions? options = null);
}

/// <summary>Mixed-integer linear-programming solver. Pure LP models are solved as LPs (with duals).</summary>
public interface IMilpSolver
{
    string Name { get; }
    LinearSolution Solve(LinearModel model, SolverOptions? options = null);
}
