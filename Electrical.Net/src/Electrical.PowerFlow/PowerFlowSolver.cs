using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;

namespace Electrical.PowerFlow;

/// <summary>Entry point for AC and DC power flow.</summary>
public static class PowerFlowSolver
{
    public static PowerFlowResult Solve(PowerNetwork net, PowerFlowOptions? options = null)
    {
        options ??= PowerFlowOptions.Default;
        var issues = net.Validate().Where(i => !i.StartsWith("Island", StringComparison.Ordinal)).ToList();
        if (issues.Count > 0) throw new InvalidOperationException("Invalid network: " + string.Join(" ", issues));
        return options.Method switch
        {
            PowerFlowMethod.NewtonRaphson => NewtonRaphson(net, options),
            PowerFlowMethod.GaussSeidel => GaussSeidel(net, options),
            PowerFlowMethod.FastDecoupled => FastDecoupled(net, options),
            PowerFlowMethod.DC => DcPowerFlow.Solve(net),
            PowerFlowMethod.BackwardForwardSweep => BackwardForwardSweep.Solve(net, options),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    // ---------------- Newton–Raphson (polar, sparse Jacobian) ----------------

    private static PowerFlowResult NewtonRaphson(PowerNetwork net, PowerFlowOptions o)
    {
        var sw = Stopwatch.StartNew();
        var pf = new PowerFlowCase(net, o.FlatStart, out var v);
        var qLimited = new HashSet<int>();
        var history = new List<double>();
        int totalIt = 0;
        bool converged = false;
        double norm = double.NaN;
        for (int outer = 0; outer < 10; outer++)
        {
            (converged, int it, norm) = NewtonCore(pf, v, o, history);
            totalIt += it;
            if (!converged || !o.EnforceQLimits) break;
            if (!ApplyQLimits(pf, v, qLimited)) break;
        }
        return pf.BuildResult(PowerFlowMethod.NewtonRaphson, v, converged, totalIt, norm, sw.Elapsed, history, qLimited);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (bool Converged, int Iterations, double Norm) NewtonCore(PowerFlowCase pf, Complex[] v, PowerFlowOptions o, List<double> history)
    {
        int n = pf.N;
        var pv = pf.Indices(BusType.PV);
        var pq = pf.Indices(BusType.PQ);
        var pvpq = pv.Concat(pq).Order().ToArray();
        var aIdx = new int[n]; var mIdx = new int[n];
        Array.Fill(aIdx, -1); Array.Fill(mIdx, -1);
        for (int k = 0; k < pvpq.Length; k++) aIdx[pvpq[k]] = k;
        for (int k = 0; k < pq.Length; k++) mIdx[pq[k]] = pvpq.Length + k;
        int dim = pvpq.Length + pq.Length;
        var y = pf.Adm.Y;
        // Fill-reducing ordering from the bus graph, expanded to the 2×2 Jacobian blocks (θ then |V| of each bus).
        var busOrder = SparseLU<Complex>.MinimumDegreeOrdering(y);
        var ordering = new int[dim];
        int pos = 0;
        foreach (int i in busOrder)
        {
            if (aIdx[i] >= 0) ordering[pos++] = aIdx[i];
            if (mIdx[i] >= 0) ordering[pos++] = mIdx[i];
        }
        var f = new double[dim];
        double norm = Mismatch(pf, v, pvpq, pq, f);
        history.Add(norm);
        if (norm < o.Tolerance) return (true, 0, norm);
        var vm = new double[n]; var va = new double[n];
        for (int i = 0; i < n; i++) { vm[i] = v[i].Magnitude; va[i] = v[i].Phase; }

        for (int it = 1; it <= o.MaxIterations; it++)
        {
            var ibus = y.Multiply(v);
            var jb = new SparseBuilder<double>(dim, dim);
            for (int i = 0; i < n; i++)
            {
                int ra = aIdx[i], rm = mIdx[i];
                if (ra < 0) continue; // slack row not in system
                var cols = y.RowIndices(i);
                var vals = y.RowValues(i);
                for (int q = 0; q < cols.Length; q++)
                {
                    int k = cols[q];
                    Complex dVa, dVm;
                    var vk = v[k];
                    var unitK = vm[k] > 0 ? vk / vm[k] : Complex.One;
                    if (k == i)
                    {
                        dVa = Complex.ImaginaryOne * v[i] * Complex.Conjugate(ibus[i] - vals[q] * v[i]);
                        dVm = v[i] * Complex.Conjugate(vals[q] * unitK) + Complex.Conjugate(ibus[i]) * unitK + pf.LoadDerivative(i, vm[i]);
                    }
                    else
                    {
                        dVa = Complex.ImaginaryOne * v[i] * Complex.Conjugate(-vals[q] * vk);
                        dVm = v[i] * Complex.Conjugate(vals[q] * unitK);
                    }
                    int ca = aIdx[k], cm = mIdx[k];
                    if (ca >= 0) jb.Add(ra, ca, dVa.Real);
                    if (cm >= 0) jb.Add(ra, cm, dVm.Real);
                    if (rm >= 0)
                    {
                        if (ca >= 0) jb.Add(rm, ca, dVa.Imaginary);
                        if (cm >= 0) jb.Add(rm, cm, dVm.Imaginary);
                    }
                }
            }
            var j = jb.ToCsr();
            var lu = SparseLU<double>.Factorize(j, ordering);
            var dx = lu.Solve(f);
            foreach (int i in pvpq) va[i] -= dx[aIdx[i]];
            foreach (int i in pq) vm[i] -= dx[mIdx[i]];
            for (int i = 0; i < n; i++) v[i] = Complex.FromPolarCoordinates(vm[i], va[i]);
            norm = Mismatch(pf, v, pvpq, pq, f);
            history.Add(norm);
            if (double.IsNaN(norm) || norm > 1e10) return (false, it, norm);
            if (norm < o.Tolerance) return (true, it, norm);
        }
        return (false, o.MaxIterations, norm);
    }

    /// <summary>F = S_calc − S_sched stacked as [ΔP(pvpq); ΔQ(pq)]; returns ‖F‖∞.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double Mismatch(PowerFlowCase pf, Complex[] v, int[] pvpq, int[] pq, double[] f)
    {
        var s = pf.Adm.Injections(v);
        double max = 0;
        for (int k = 0; k < pvpq.Length; k++)
        {
            int i = pvpq[k];
            var d = s[i] - pf.Scheduled(i, v[i].Magnitude);
            f[k] = d.Real;
            max = Math.Max(max, Math.Abs(d.Real));
        }
        for (int k = 0; k < pq.Length; k++)
        {
            int i = pq[k];
            var d = s[i] - pf.Scheduled(i, v[i].Magnitude);
            f[pvpq.Length + k] = d.Imaginary;
            max = Math.Max(max, Math.Abs(d.Imaginary));
        }
        return max;
    }

    /// <summary>Converts PV buses whose generators exceed Q limits to PQ at the limit. Returns true if anything changed.</summary>
    private static bool ApplyQLimits(PowerFlowCase pf, Complex[] v, HashSet<int> limited)
    {
        var s = pf.Adm.Injections(v);
        bool changed = false;
        for (int i = 0; i < pf.N; i++)
        {
            if (pf.Types[i] != BusType.PV) continue;
            double qg = (s[i] + pf.LoadAt(i, v[i].Magnitude)).Imaginary * pf.Net.BaseMva;
            double qmax = pf.GensAt[i].Sum(k => pf.Net.Generators[k].Qmax);
            double qmin = pf.GensAt[i].Sum(k => pf.Net.Generators[k].Qmin);
            double lim = qg > qmax ? qmax : qg < qmin ? qmin : double.NaN;
            if (double.IsNaN(lim)) continue;
            pf.Types[i] = BusType.PQ;
            pf.SGen[i] = new Complex(pf.SGen[i].Real, lim / pf.Net.BaseMva);
            limited.Add(i);
            changed = true;
        }
        return changed;
    }

    // ---------------- Gauss–Seidel ----------------

    private static PowerFlowResult GaussSeidel(PowerNetwork net, PowerFlowOptions o)
    {
        var sw = Stopwatch.StartNew();
        var pf = new PowerFlowCase(net, o.FlatStart, out var v);
        var y = pf.Adm.Y;
        var pv = pf.Indices(BusType.PV);
        var pq = pf.Indices(BusType.PQ);
        var pvpq = pv.Concat(pq).Order().ToArray();
        var f = new double[pvpq.Length + pq.Length];
        var history = new List<double>();
        int maxIt = Math.Max(o.MaxIterations, 1000);
        double norm = Mismatch(pf, v, pvpq, pq, f);
        const double accel = 1.6;
        int it = 0;
        for (; it < maxIt && norm >= o.Tolerance; it++)
        {
            foreach (int i in pvpq)
            {
                var cols = y.RowIndices(i);
                var vals = y.RowValues(i);
                Complex sum = 0, yii = 0;
                for (int q = 0; q < cols.Length; q++)
                {
                    if (cols[q] == i) yii = vals[q];
                    else sum += vals[q] * v[cols[q]];
                }
                Complex sSch = pf.Scheduled(i, v[i].Magnitude);
                if (pf.Types[i] == BusType.PV)
                {
                    double qCalc = -(Complex.Conjugate(v[i]) * (sum + yii * v[i])).Imaginary;
                    sSch = new Complex(sSch.Real, qCalc);
                }
                var vNew = (Complex.Conjugate(sSch / v[i]) - sum) / yii;
                if (pf.Types[i] == BusType.PV) vNew = Complex.FromPolarCoordinates(pf.VSet[i], vNew.Phase);
                else vNew = v[i] + accel * (vNew - v[i]);
                v[i] = vNew;
            }
            norm = Mismatch(pf, v, pvpq, pq, f);
            history.Add(norm);
        }
        return pf.BuildResult(PowerFlowMethod.GaussSeidel, v, norm < o.Tolerance, it, norm, sw.Elapsed, history);
    }

    // ---------------- Fast decoupled (XB) ----------------

    private static PowerFlowResult FastDecoupled(PowerNetwork net, PowerFlowOptions o)
    {
        var sw = Stopwatch.StartNew();
        var pf = new PowerFlowCase(net, o.FlatStart, out var v);
        int n = pf.N;
        var pv = pf.Indices(BusType.PV);
        var pq = pf.Indices(BusType.PQ);
        var pvpq = pv.Concat(pq).Order().ToArray();
        var aIdx = new int[n]; var mIdx = new int[n];
        Array.Fill(aIdx, -1); Array.Fill(mIdx, -1);
        for (int k = 0; k < pvpq.Length; k++) aIdx[pvpq[k]] = k;
        for (int k = 0; k < pq.Length; k++) mIdx[pq[k]] = k;

        // B' : series reactance only, no shunts / taps / resistance.  B'' : −Im(Ybus)
        var bp = new SparseBuilder<double>(pvpq.Length, pvpq.Length);
        foreach (var br in net.Branches)
        {
            if (!br.InService) continue;
            int fi = net.IndexOf(br.From), ti = net.IndexOf(br.To);
            double b = 1 / br.X;
            int a = aIdx[fi], c = aIdx[ti];
            if (a >= 0) bp.Add(a, a, b);
            if (c >= 0) bp.Add(c, c, b);
            if (a >= 0 && c >= 0) { bp.Add(a, c, -b); bp.Add(c, a, -b); }
        }
        var bpp = new SparseBuilder<double>(pq.Length, pq.Length);
        foreach (int i in pq)
        {
            var cols = pf.Adm.Y.RowIndices(i);
            var vals = pf.Adm.Y.RowValues(i);
            for (int q = 0; q < cols.Length; q++)
                if (mIdx[cols[q]] >= 0) bpp.Add(mIdx[i], mIdx[cols[q]], -vals[q].Imaginary);
        }
        var luP = pvpq.Length > 0 ? SparseLU<double>.Factorize(bp.ToCsr()) : null;
        var luQ = pq.Length > 0 ? SparseLU<double>.Factorize(bpp.ToCsr()) : null;

        var vm = v.Select(c => c.Magnitude).ToArray();
        var va = v.Select(c => c.Phase).ToArray();
        var history = new List<double>();
        double norm = double.PositiveInfinity;
        int it = 0;
        var dp = new double[pvpq.Length];
        var dq = new double[pq.Length];
        for (; it < Math.Max(o.MaxIterations, 50); it++)
        {
            norm = HalfMismatch(pf, v, pvpq, pq, dp, dq);
            history.Add(norm);
            if (norm < o.Tolerance) break;
            if (luP is not null)
            {
                var d = luP.Solve(dp);
                foreach (int i in pvpq) va[i] += d[aIdx[i]];
                for (int i = 0; i < n; i++) v[i] = Complex.FromPolarCoordinates(vm[i], va[i]);
            }
            HalfMismatch(pf, v, pvpq, pq, dp, dq);
            if (luQ is not null)
            {
                var d = luQ.Solve(dq);
                foreach (int i in pq) vm[i] += d[mIdx[i]];
                for (int i = 0; i < n; i++) v[i] = Complex.FromPolarCoordinates(vm[i], va[i]);
            }
        }
        return pf.BuildResult(PowerFlowMethod.FastDecoupled, v, norm < o.Tolerance, it, norm, sw.Elapsed, history);
    }

    /// <summary>Scaled mismatches ΔP/|V| and ΔQ/|V| (scheduled − calculated).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double HalfMismatch(PowerFlowCase pf, Complex[] v, int[] pvpq, int[] pq, double[] dp, double[] dq)
    {
        var s = pf.Adm.Injections(v);
        double max = 0;
        for (int k = 0; k < pvpq.Length; k++)
        {
            int i = pvpq[k];
            var d = pf.Scheduled(i, v[i].Magnitude) - s[i];
            dp[k] = d.Real / v[i].Magnitude;
            max = Math.Max(max, Math.Abs(d.Real));
        }
        for (int k = 0; k < pq.Length; k++)
        {
            int i = pq[k];
            var d = pf.Scheduled(i, v[i].Magnitude) - s[i];
            dq[k] = d.Imaginary / v[i].Magnitude;
            max = Math.Max(max, Math.Abs(d.Imaginary));
        }
        return max;
    }
}
