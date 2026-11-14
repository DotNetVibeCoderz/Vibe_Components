using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;

namespace Electrical.Optimization;

/// <summary>
/// Polar AC-OPF formulation x = [θ (nb, rad), |V| (nb, pu), Pg (ng, pu), Qg (ng, pu)] with exact first and second derivatives.
/// Equalities g = [P(V) + Pd − Cg·Pg; Q(V) + Qd − Cg·Qg] (pu); inequalities h = [|Sf|² − Smax²; |St|² − Smax²] (pu²) for rated branches.
/// Derivatives are assembled term by term from the Y-bus entries (equivalent to MATPOWER's dSbus_dV / d2Sbus_dV2) and per branch end
/// (equivalent to dSbr_dV / d2ASbr_dV2).
/// </summary>
internal sealed class AcOpfModel
{
    public readonly PowerNetwork Net;
    public readonly AdmittanceMatrix Adm;
    public readonly int Nb, Ng, Nx, Ref;
    public readonly double BaseMva;
    public readonly int[] GenIndex;   // generator list positions (in-service only)
    public readonly int[] GenBus;     // bus position per OPF generator
    public readonly double[] Pd, Qd;  // pu
    public readonly int[] Yi, Yj; public readonly Complex[] Yv;
    public readonly LimitedBranch[] Limited;

    public readonly record struct LimitedBranch(int Index, int F, int T, BranchAdmittance Y, double RatingMva);

    public int IVa => 0;
    public int IVm => Nb;
    public int IPg => 2 * Nb;
    public int IQg => 2 * Nb + Ng;

    public AcOpfModel(PowerNetwork net, AcOpfOptions o)
    {
        Net = net;
        Nb = net.BusCount;
        BaseMva = net.BaseMva;
        Adm = AdmittanceMatrix.Build(net);
        var gi = new List<int>();
        for (int k = 0; k < net.Generators.Count; k++) if (net.Generators[k].InService) gi.Add(k);
        GenIndex = gi.ToArray();
        Ng = GenIndex.Length;
        GenBus = GenIndex.Select(k => net.IndexOf(net.Generators[k].Bus)).ToArray();
        Nx = 2 * Nb + 2 * Ng;
        Pd = new double[Nb]; Qd = new double[Nb];
        foreach (var l in net.Loads)
        {
            if (!l.InService) continue;
            int i = net.IndexOf(l.Bus);
            Pd[i] += l.P / BaseMva; Qd[i] += l.Q / BaseMva;
        }
        int r = net.Buses.FindIndex(b => b.Type == BusType.Slack);
        if (r < 0) r = Ng > 0 ? GenBus[Enumerable.Range(0, Ng).MaxBy(k => net.Generators[GenIndex[k]].Pmax)] : 0;
        Ref = r;

        var yi = new List<int>(); var yj = new List<int>(); var yv = new List<Complex>();
        var y = Adm.Y;
        for (int i = 0; i < Nb; i++)
        {
            var ci = y.RowIndices(i); var cv = y.RowValues(i);
            for (int q = 0; q < ci.Length; q++) { yi.Add(i); yj.Add(ci[q]); yv.Add(cv[q]); }
        }
        Yi = yi.ToArray(); Yj = yj.ToArray(); Yv = yv.ToArray();

        var lim = new List<LimitedBranch>();
        if (o.EnforceLineLimits)
        {
            for (int k = 0; k < net.Branches.Count; k++)
            {
                var br = net.Branches[k];
                double rate = o.RatingOverrides is not null && o.RatingOverrides.TryGetValue(k, out var ov) ? ov : br.RateA;
                rate *= o.RatingFactor;
                if (!br.InService || rate <= 0) continue;
                lim.Add(new LimitedBranch(k, net.IndexOf(br.From), net.IndexOf(br.To), Adm.Branches[k], rate));
            }
        }
        Limited = lim.ToArray();
    }

    public int EqualityCount => 2 * Nb;
    public int InequalityCount => 2 * Limited.Length;

    public Complex[] Voltages(double[] x)
    {
        var v = new Complex[Nb];
        for (int i = 0; i < Nb; i++) v[i] = Complex.FromPolarCoordinates(x[IVm + i], x[IVa + i]);
        return v;
    }

    public void Bounds(double[] lo, double[] up)
    {
        for (int i = 0; i < Nb; i++)
        {
            lo[IVa + i] = double.NegativeInfinity; up[IVa + i] = double.PositiveInfinity;
            lo[IVm + i] = Net.Buses[i].Vmin; up[IVm + i] = Net.Buses[i].Vmax;
        }
        lo[IVa + Ref] = up[IVa + Ref] = Net.Buses[Ref].VaDeg * Scalar.Deg2Rad;
        for (int k = 0; k < Ng; k++)
        {
            var g = Net.Generators[GenIndex[k]];
            lo[IPg + k] = g.Pmin / BaseMva; up[IPg + k] = g.Pmax / BaseMva;
            lo[IQg + k] = g.Qmin / BaseMva; up[IQg + k] = g.Qmax / BaseMva;
        }
    }

    // ---------------------------------------------------------------- objective

    /// <summary>Total cost [$/h]; gradient w.r.t. x.</summary>
    public double Objective(double[] x, double[] grad)
    {
        Array.Clear(grad);
        double f = 0;
        for (int k = 0; k < Ng; k++)
        {
            var c = Net.Generators[GenIndex[k]].Cost;
            double pmw = x[IPg + k] * BaseMva;
            f += c.Evaluate(pmw);
            grad[IPg + k] = BaseMva * c.Marginal(pmw);
        }
        return f;
    }

    // ---------------------------------------------------------------- constraints

    public void Constraints(double[] x, double[] g, double[] h)
    {
        var v = Voltages(x);
        var s = Injections(v);
        for (int i = 0; i < Nb; i++) { g[i] = s[i].Real + Pd[i]; g[Nb + i] = s[i].Imaginary + Qd[i]; }
        for (int k = 0; k < Ng; k++) { g[GenBus[k]] -= x[IPg + k]; g[Nb + GenBus[k]] -= x[IQg + k]; }
        int nl = Limited.Length;
        for (int l = 0; l < nl; l++)
        {
            var (sf, st) = BranchFlows(Limited[l], v);
            double lim = Limited[l].RatingMva / BaseMva;
            h[l] = sf.Real * sf.Real + sf.Imaginary * sf.Imaginary - lim * lim;
            h[nl + l] = st.Real * st.Real + st.Imaginary * st.Imaginary - lim * lim;
        }
    }

    public Complex[] Injections(Complex[] v)
    {
        var cur = new Complex[Nb];
        for (int e = 0; e < Yv.Length; e++) cur[Yi[e]] += Yv[e] * v[Yj[e]];
        var s = new Complex[Nb];
        for (int i = 0; i < Nb; i++) s[i] = v[i] * Complex.Conjugate(cur[i]);
        return s;
    }

    public static (Complex Sf, Complex St) BranchFlows(LimitedBranch b, Complex[] v) =>
        (v[b.F] * Complex.Conjugate(b.Y.Yff * v[b.F] + b.Y.Yft * v[b.T]), v[b.T] * Complex.Conjugate(b.Y.Ytf * v[b.F] + b.Y.Ytt * v[b.T]));

    public (SparseMatrix<double> Jg, SparseMatrix<double> Jh) Jacobians(double[] x)
    {
        var jg = new SparseBuilder<double>(2 * Nb, Nx);
        for (int e = 0; e < Yv.Length; e++)
        {
            int i = Yi[e], j = Yj[e];
            double vi = x[IVm + i];
            if (i == j)
            {
                var d = 2 * vi * Complex.Conjugate(Yv[e]);
                Add2(jg, i, IVm + i, d);
                Add2(jg, i, IVa + i, Complex.Zero);
                continue;
            }
            double vj = x[IVm + j];
            var w = Complex.Conjugate(Yv[e]) * Complex.FromPolarCoordinates(1, x[IVa + i] - x[IVa + j]);
            var t = vi * vj * w;
            var jt = Complex.ImaginaryOne * t;
            Add2(jg, i, IVa + i, jt);
            Add2(jg, i, IVa + j, -jt);
            Add2(jg, i, IVm + i, vj * w);
            Add2(jg, i, IVm + j, vi * w);
        }
        for (int k = 0; k < Ng; k++)
        {
            jg.Add(GenBus[k], IPg + k, -1);
            jg.Add(Nb + GenBus[k], IQg + k, -1);
        }
        int nl = Limited.Length;
        var jh = new SparseBuilder<double>(2 * nl, Nx);
        if (nl > 0)
        {
            Span<double> grad = stackalloc double[4];
            for (int l = 0; l < nl; l++)
            {
                var b = Limited[l];
                for (int end = 0; end < 2; end++)
                {
                    var idx = EndVariables(b, end);
                    EndDerivatives(b, end, x, out var s, out var ds, out _);
                    for (int a = 0; a < 4; a++) grad[a] = 2 * (Complex.Conjugate(s) * ds[a]).Real;
                    for (int a = 0; a < 4; a++) jh.Add(end * nl + l, idx[a], grad[a]);
                }
            }
        }
        return (jg.ToCsr(), jh.ToCsr());
    }

    private void Add2(SparseBuilder<double> b, int bus, int col, Complex d)
    {
        b.Add(bus, col, d.Real);
        b.Add(Nb + bus, col, d.Imaginary);
    }

    /// <summary>Variable indices (θa, θb, |Va|, |Vb|) of a branch end (end 0 = from, 1 = to).</summary>
    public int[] EndVariables(LimitedBranch b, int end)
    {
        int a = end == 0 ? b.F : b.T, c = end == 0 ? b.T : b.F;
        return [IVa + a, IVa + c, IVm + a, IVm + c];
    }

    /// <summary>
    /// S = Va·conj(yd·Va + yo·Vb) = va²·conj(yd) + va·vb·w, w = conj(yo)·e^{j(θa−θb)}; first (4) and second (4×4) derivatives
    /// with respect to (θa, θb, va, vb).
    /// </summary>
    public void EndDerivatives(LimitedBranch b, int end, double[] x, out Complex s, out Complex[] ds, out Complex[,] d2s)
    {
        int a = end == 0 ? b.F : b.T, c = end == 0 ? b.T : b.F;
        var yd = end == 0 ? b.Y.Yff : b.Y.Ytt;
        var yo = end == 0 ? b.Y.Yft : b.Y.Ytf;
        double va = x[IVm + a], vb = x[IVm + c];
        var w = Complex.Conjugate(yo) * Complex.FromPolarCoordinates(1, x[IVa + a] - x[IVa + c]);
        var t = va * vb * w;
        var j = Complex.ImaginaryOne;
        var cyd = Complex.Conjugate(yd);
        s = va * va * cyd + t;
        ds = [j * t, -j * t, 2 * va * cyd + vb * w, va * w];
        d2s = new Complex[4, 4];
        d2s[0, 0] = -t; d2s[0, 1] = t; d2s[0, 2] = j * vb * w; d2s[0, 3] = j * va * w;
        d2s[1, 1] = -t; d2s[1, 2] = -j * vb * w; d2s[1, 3] = -j * va * w;
        d2s[2, 2] = 2 * cyd; d2s[2, 3] = w;
        d2s[3, 3] = 0;
        for (int p = 0; p < 4; p++) for (int q = 0; q < p; q++) d2s[p, q] = d2s[q, p];
    }

    // ---------------------------------------------------------------- Hessian of the Lagrangian

    public SparseMatrix<double> Hessian(double[] x, double sigma, double[] lambda, double[] mu)
    {
        var hb = new SparseBuilder<double>(Nx, Nx);
        for (int k = 0; k < Ng; k++)
        {
            var c = Net.Generators[GenIndex[k]].Cost;
            hb.Add(IPg + k, IPg + k, sigma * 2 * c.C2 * BaseMva * BaseMva);
        }
        // λPᵀP + λQᵀQ = Re(Σ_i c_i·S_i) with c_i = λP_i − j·λQ_i
        var j = Complex.ImaginaryOne;
        for (int e = 0; e < Yv.Length; e++)
        {
            int i = Yi[e], k = Yj[e];
            var ci = new Complex(lambda[i], -lambda[Nb + i]);
            double vi = x[IVm + i];
            if (i == k)
            {
                hb.Add(IVm + i, IVm + i, (2 * ci * Complex.Conjugate(Yv[e])).Real);
                continue;
            }
            double vk = x[IVm + k];
            var w = ci * Complex.Conjugate(Yv[e]) * Complex.FromPolarCoordinates(1, x[IVa + i] - x[IVa + k]);
            var t = vi * vk * w;
            int ai = IVa + i, ak = IVa + k, mi = IVm + i, mk = IVm + k;
            hb.Add(ai, ai, -t.Real); hb.Add(ak, ak, -t.Real);
            Sym(hb, ai, ak, t.Real);
            Sym(hb, ai, mi, (j * vk * w).Real);
            Sym(hb, ai, mk, (j * vi * w).Real);
            Sym(hb, ak, mi, (-j * vk * w).Real);
            Sym(hb, ak, mk, (-j * vi * w).Real);
            Sym(hb, mi, mk, w.Real);
        }
        int nl = Limited.Length;
        for (int l = 0; l < nl; l++)
        {
            var b = Limited[l];
            for (int end = 0; end < 2; end++)
            {
                double m = mu[end * nl + l];
                var idx = EndVariables(b, end);
                EndDerivatives(b, end, x, out var s, out var ds, out var d2s);
                var cs = Complex.Conjugate(s);
                for (int p = 0; p < 4; p++)
                    for (int q = 0; q < 4; q++)
                        hb.Add(idx[p], idx[q], m * 2 * (cs * d2s[p, q] + ds[p] * Complex.Conjugate(ds[q])).Real);
            }
        }
        return hb.ToCsr();
    }

    private static void Sym(SparseBuilder<double> b, int r, int c, double v)
    {
        b.Add(r, c, v);
        b.Add(c, r, v);
    }
}
