using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;

namespace Electrical.StateEstimation;

/// <summary>Options for <see cref="WlsEstimator"/>.</summary>
public sealed record StateEstimationOptions
{
    /// <summary>Convergence tolerance on max |Δx| (rad / pu).</summary>
    public double Tolerance { get; init; } = 1e-6;
    public int MaxIterations { get; init; } = 30;
    /// <summary>Significance level of the chi-square test (0.01 → 99 % confidence).</summary>
    public double ChiSquareAlpha { get; init; } = 0.01;
    /// <summary>Compute normalized residuals (needs one gain-matrix solve per measurement).</summary>
    public bool ComputeNormalizedResiduals { get; init; } = true;

    public static StateEstimationOptions Default { get; } = new();
}

/// <summary>WLS state estimate. Voltages in pu / degrees aligned with the network bus order; residuals in measurement units.</summary>
public sealed class StateEstimationResult
{
    public required bool Converged { get; init; }
    public required bool Observable { get; init; }
    public required int Iterations { get; init; }
    public required int[] BusIds { get; init; }
    public required double[] Vm { get; init; }
    public required double[] VaDeg { get; init; }
    public required IReadOnlyList<Measurement> Measurements { get; init; }
    /// <summary>Estimated values h(x̂) in measurement units.</summary>
    public required double[] Estimated { get; init; }
    /// <summary>Residuals z − h(x̂) in measurement units.</summary>
    public required double[] Residuals { get; init; }
    /// <summary>|r_i| / √Ω_ii (NaN where Ω_ii ≈ 0, i.e. critical measurements, or when not computed).</summary>
    public required double[] NormalizedResiduals { get; init; }
    /// <summary>Objective J(x̂) = Σ (r_i/σ_i)².</summary>
    public required double ObjectiveJ { get; init; }
    /// <summary>Degrees of freedom m − n.</summary>
    public required int DegreesOfFreedom { get; init; }
    /// <summary>χ²_{m−n, 1−α} detection threshold.</summary>
    public required double ChiSquareThreshold { get; init; }
    /// <summary>True when J(x̂) exceeds the chi-square threshold (bad data suspected).</summary>
    public bool BadDataSuspected => ObjectiveJ > ChiSquareThreshold;
    /// <summary>History of max |Δx| per iteration.</summary>
    public IReadOnlyList<double> History { get; init; } = [];

    public double VmAt(int busId) => Vm[Array.IndexOf(BusIds, busId)];
    public double VaDegAt(int busId) => VaDeg[Array.IndexOf(BusIds, busId)];

    /// <summary>Index of the measurement with the largest normalized residual (−1 if none).</summary>
    public int LargestNormalizedResidualIndex
    {
        get
        {
            int best = -1; double v = 0;
            for (int i = 0; i < NormalizedResiduals.Length; i++)
                if (!double.IsNaN(NormalizedResiduals[i]) && NormalizedResiduals[i] > v) { v = NormalizedResiduals[i]; best = i; }
            return best;
        }
    }
}

/// <summary>
/// Weighted-least-squares AC state estimator (Abur &amp; Expósito, "Power System State Estimation", ch. 2): Gauss–Newton iterations
/// G·Δx = HᵀW·[z − h(x)] with the sparse measurement Jacobian H and gain G = HᵀWH factorized by <see cref="SparseLU{T}"/>.
/// State: voltage angles of all non-reference buses and all voltage magnitudes (reference angle = 0 = slack bus).
/// </summary>
public static class WlsEstimator
{
    public static StateEstimationResult Estimate(PowerNetwork net, IReadOnlyList<Measurement> measurements, StateEstimationOptions? options = null)
    {
        options ??= StateEstimationOptions.Default;
        var model = new MeasurementModel(net);
        int m = measurements.Count, ns = model.StateCount;
        var z = new double[m]; var w = new double[m];
        for (int i = 0; i < m; i++)
        {
            z[i] = model.ToInternal(measurements[i], measurements[i].Value);
            double s = model.ToInternal(measurements[i], measurements[i].Sigma, isSigma: true);
            w[i] = 1 / (s * s);
        }
        var vm = Enumerable.Repeat(1.0, model.N).ToArray();
        var va = new double[model.N];
        int it = 0;
        bool converged = false, observable = true;
        var history = new List<double>();
        SparseLU<double>? lu = null;
        int[]? ordering = null;
        var h = new double[m];
        List<(int Col, double Val)>[] rows = [];
        for (; it < options.MaxIterations; it++)
        {
            rows = model.Evaluate(measurements, vm, va, h);
            var gain = Gain(rows, w, ns);
            var rhs = new double[ns];
            for (int i = 0; i < m; i++)
            {
                double ri = w[i] * (z[i] - h[i]);
                foreach (var (c, v) in rows[i]) rhs[c] += v * ri;
            }
            try
            {
                lu = SparseLU<double>.Factorize(gain, ordering);
                ordering = lu.Ordering;
            }
            catch (SingularMatrixException) { observable = false; break; }
            var dx = lu.Solve(rhs);
            if (dx.Any(double.IsNaN) || dx.Any(double.IsInfinity)) { observable = false; break; }
            double max = 0;
            for (int k = 0; k < ns; k++) max = Math.Max(max, Math.Abs(dx[k]));
            model.Apply(dx, vm, va);
            history.Add(max);
            if (max < options.Tolerance) { converged = true; it++; break; }
        }
        rows = model.Evaluate(measurements, vm, va, h);
        var residual = new double[m]; var est = new double[m];
        double j = 0;
        for (int i = 0; i < m; i++)
        {
            est[i] = model.ToExternal(measurements[i], h[i]);
            residual[i] = measurements[i].Value - est[i];
            j += (z[i] - h[i]) * (z[i] - h[i]) * w[i];
        }
        var rn = new double[m];
        Array.Fill(rn, double.NaN);
        if (observable && converged && options.ComputeNormalizedResiduals)
        {
            var gain = Gain(rows, w, ns);
            var glu = SparseLU<double>.Factorize(gain, ordering);
            var e = new double[ns];
            for (int i = 0; i < m; i++)
            {
                Array.Clear(e);
                foreach (var (c, v) in rows[i]) e[c] = v;
                var u = glu.Solve(e);
                double hgh = 0;
                foreach (var (c, v) in rows[i]) hgh += v * u[c];
                double omega = 1 / w[i] - hgh;
                rn[i] = omega > 1e-10 / w[i] ? Math.Abs(z[i] - h[i]) / Math.Sqrt(omega) : double.NaN;
            }
        }
        int dof = Math.Max(1, m - ns);
        return new StateEstimationResult
        {
            Converged = converged, Observable = observable, Iterations = it, BusIds = net.Buses.Select(b => b.Id).ToArray(), Vm = vm,
            VaDeg = va.Select(a => a * Scalar.Rad2Deg).ToArray(), Measurements = measurements, Estimated = est, Residuals = residual,
            NormalizedResiduals = rn, ObjectiveJ = j, DegreesOfFreedom = m - ns,
            ChiSquareThreshold = Distributions.ChiSquareInverseCdf(1 - options.ChiSquareAlpha, dof), History = history,
        };
    }

    private static SparseMatrix<double> Gain(List<(int Col, double Val)>[] rows, double[] w, int ns)
    {
        var b = new SparseBuilder<double>(ns, ns);
        for (int k = 0; k < ns; k++) b.Add(k, k, 0);
        for (int i = 0; i < rows.Length; i++)
            foreach (var (ca, va) in rows[i])
                foreach (var (cb, vb) in rows[i])
                    b.Add(ca, cb, w[i] * va * vb);
        return b.ToCsr();
    }

    /// <summary>Measurement functions h(x) at a given state in internal units (pu on the system base, radians for angles).</summary>
    public static double[] EvaluateMeasurements(PowerNetwork net, IReadOnlyList<Measurement> measurements, double[] vm, double[] vaDeg)
    {
        var h = new double[measurements.Count];
        new MeasurementModel(net).Evaluate(measurements, vm, vaDeg.Select(a => a * Scalar.Deg2Rad).ToArray(), h);
        return h;
    }

    /// <summary>Measurement Jacobian H (pu per rad / pu) at a given state (rows aligned with <paramref name="measurements"/>), dense, for analysis/teaching.</summary>
    public static DenseMatrix<double> Jacobian(PowerNetwork net, IReadOnlyList<Measurement> measurements, double[] vm, double[] vaDeg)
    {
        var model = new MeasurementModel(net);
        var h = new double[measurements.Count];
        var rows = model.Evaluate(measurements, vm, vaDeg.Select(a => a * Scalar.Deg2Rad).ToArray(), h);
        var d = new DenseMatrix<double>(measurements.Count, model.StateCount);
        for (int i = 0; i < rows.Length; i++) foreach (var (c, v) in rows[i]) d[i, c] += v;
        return d;
    }
}

/// <summary>Nonlinear measurement functions h(x) and their derivatives (polar coordinates, MATPOWER branch model).</summary>
internal sealed class MeasurementModel
{
    private readonly PowerNetwork _net;
    private readonly AdmittanceMatrix _adm;
    private readonly int[] _thetaCol;
    public readonly int N;
    public readonly int Ref;
    public readonly int StateCount;
    private readonly double _base;

    public MeasurementModel(PowerNetwork net)
    {
        _net = net;
        _adm = AdmittanceMatrix.Build(net);
        N = net.BusCount;
        Ref = net.SlackBus is { } s ? net.IndexOf(s.Id) : 0;
        _thetaCol = new int[N];
        for (int i = 0, c = 0; i < N; i++) _thetaCol[i] = i == Ref ? -1 : c++;
        StateCount = (N - 1) + N;
        _base = net.BaseMva;
    }

    public int VCol(int bus) => N - 1 + bus;
    public int ThetaCol(int bus) => _thetaCol[bus];
    public string StateName(int col) =>
        col < N - 1 ? $"θ@bus {_net.Buses[Array.IndexOf(_thetaCol, col)].Id}" : $"V@bus {_net.Buses[col - (N - 1)].Id}";

    public double ToInternal(Measurement m, double value, bool isSigma = false) => m.Type switch
    {
        MeasurementType.VoltageMagnitude => value,
        MeasurementType.VoltageAngle => value * Scalar.Deg2Rad,
        _ => value / _base,
    };

    public double ToExternal(Measurement m, double value) => m.Type switch
    {
        MeasurementType.VoltageMagnitude => value,
        MeasurementType.VoltageAngle => value * Scalar.Rad2Deg,
        _ => value * _base,
    };

    public void Apply(double[] dx, double[] vm, double[] va)
    {
        for (int i = 0; i < N; i++)
        {
            if (_thetaCol[i] >= 0) va[i] += dx[_thetaCol[i]];
            vm[i] += dx[VCol(i)];
        }
    }

    public List<(int Col, double Val)>[] Evaluate(IReadOnlyList<Measurement> ms, double[] vm, double[] va, double[] h)
    {
        var v = new Complex[N];
        for (int i = 0; i < N; i++) v[i] = Complex.FromPolarCoordinates(vm[i], va[i]);
        var rows = new List<(int, double)>[ms.Count];
        var terms = new List<(int Bus, Complex Y)>(8);
        for (int k = 0; k < ms.Count; k++)
        {
            var m = ms[k];
            var row = new List<(int, double)>(8);
            switch (m.Type)
            {
                case MeasurementType.VoltageMagnitude:
                {
                    int i = _net.IndexOf(m.Location);
                    h[k] = vm[i];
                    row.Add((VCol(i), 1));
                    break;
                }
                case MeasurementType.VoltageAngle:
                {
                    int i = _net.IndexOf(m.Location);
                    h[k] = va[i];
                    if (_thetaCol[i] >= 0) row.Add((_thetaCol[i], 1));
                    break;
                }
                default:
                {
                    int a;
                    terms.Clear();
                    if (m.IsFlow)
                    {
                        var br = _net.Branches[m.Location];
                        var y = _adm.Branches[m.Location];
                        int f = _net.IndexOf(br.From), t = _net.IndexOf(br.To);
                        if (!br.InService) { h[k] = 0; break; }
                        if (m.End == BranchEnd.From) { a = f; terms.Add((f, y.Yff)); terms.Add((t, y.Yft)); }
                        else { a = t; terms.Add((f, y.Ytf)); terms.Add((t, y.Ytt)); }
                    }
                    else
                    {
                        a = _net.IndexOf(m.Location);
                        var cols = _adm.Y.RowIndices(a);
                        var vals = _adm.Y.RowValues(a);
                        for (int q = 0; q < cols.Length; q++) terms.Add((cols[q], vals[q]));
                    }
                    Complex cur = 0;
                    foreach (var (b, y) in terms) cur += y * v[b];
                    var s = v[a] * Complex.Conjugate(cur);
                    bool p = m.Type is MeasurementType.ActiveInjection or MeasurementType.ActiveFlow;
                    h[k] = p ? s.Real : s.Imaginary;
                    // dS/dθ_b = −j·V_a·conj(y_b V_b);  dS/dV_b = V_a·conj(y_b V_b)/V_b;  plus self terms of V_a
                    var dTheta = new Dictionary<int, Complex>(terms.Count + 1);
                    var dV = new Dictionary<int, Complex>(terms.Count + 1);
                    foreach (var (b, y) in terms)
                    {
                        var c = v[a] * Complex.Conjugate(y * v[b]);
                        dTheta[b] = dTheta.GetValueOrDefault(b) - Complex.ImaginaryOne * c;
                        dV[b] = dV.GetValueOrDefault(b) + c / vm[b];
                    }
                    dTheta[a] = dTheta.GetValueOrDefault(a) + Complex.ImaginaryOne * s;
                    dV[a] = dV.GetValueOrDefault(a) + s / vm[a];
                    foreach (var (b, d) in dTheta) if (_thetaCol[b] >= 0) row.Add((_thetaCol[b], p ? d.Real : d.Imaginary));
                    foreach (var (b, d) in dV) row.Add((VCol(b), p ? d.Real : d.Imaginary));
                    break;
                }
            }
            rows[k] = row;
        }
        return rows;
    }
}
