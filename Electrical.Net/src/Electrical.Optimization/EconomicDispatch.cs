using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Optimization;

/// <summary>A dispatchable unit: quadratic cost C(P) [$/h] with P in MW, limits in MW.</summary>
public sealed record DispatchUnit(string Name, PolynomialCost Cost, double PminMw, double PmaxMw, int Bus = 0)
{
    /// <summary>Output [MW] at which the marginal cost equals λ [$/MWh], clipped to the limits.</summary>
    public double OutputAt(double lambda, double penaltyFactor = 1)
    {
        double l = lambda / penaltyFactor;
        if (Cost.C2 <= 0) return l > Cost.C1 ? PmaxMw : PminMw;
        return Math.Clamp((l - Cost.C1) / (2 * Cost.C2), PminMw, PmaxMw);
    }

    /// <summary>Units from the in-service generators of a network (order preserved).</summary>
    public static List<DispatchUnit> FromNetwork(PowerNetwork net) =>
        net.Generators.Where(g => g.InService)
            .Select((g, i) => new DispatchUnit(g.Name ?? $"G{i + 1}@{g.Bus}", g.Cost, g.Pmin, g.Pmax, g.Bus)).ToList();
}

/// <summary>Economic dispatch solution.</summary>
/// <param name="Lambda">System incremental cost λ [$/MWh].</param>
/// <param name="OutputMw">Unit outputs [MW].</param>
/// <param name="LossesMw">Transmission losses from the loss formula [MW] (0 when lossless).</param>
/// <param name="TotalCost">Σ C_i(P_i) [$/h].</param>
/// <param name="PenaltyFactors">L_i = 1/(1 − ∂P_L/∂P_i) (1 when lossless).</param>
public sealed record EconomicDispatchResult(double Lambda, double[] OutputMw, double LossesMw, double TotalCost, int Iterations, bool Converged,
    double[] PenaltyFactors)
{
    public double TotalGenerationMw => OutputMw.Sum();
}

/// <summary>
/// Kron's loss formula P_L = PᵀBP + B0ᵀP + B00 [MW] with P in MW (B in 1/MW, B0 dimensionless, B00 in MW).
/// </summary>
public sealed record LossCoefficients(double[,] B, double[] B0, double B00)
{
    public int Count => B0.Length;

    public double Losses(ReadOnlySpan<double> p)
    {
        double s = B00;
        for (int i = 0; i < Count; i++)
        {
            s += B0[i] * p[i];
            for (int j = 0; j < Count; j++) s += p[i] * B[i, j] * p[j];
        }
        return s;
    }

    /// <summary>Incremental transmission loss ∂P_L/∂P_i = 2Σ_j B_ij P_j + B0_i.</summary>
    public double Incremental(ReadOnlySpan<double> p, int i)
    {
        double s = B0[i];
        for (int j = 0; j < Count; j++) s += 2 * B[i, j] * p[j];
        return s;
    }

    /// <summary>
    /// Derives B-coefficients from a solved AC power flow (Kron reduction, as in Saadat "Power System Analysis" §7.6):
    /// Z_bus = Y_bus⁻¹; load currents are fixed fractions l_k of the total load current; the total load current is eliminated with the
    /// reference-bus voltage equation, giving bus currents I = C·[I_g; I_n0]; generator currents I_g = α_g·P_g with
    /// α_g = (1 − j·Q_g/P_g)/V_g*; then P_L = xᵀ·Re(ΓᴴCᴴ·Re(Z)·CΓ)·x with x = [P; 1]. Exact at the given operating point.
    /// Generators are the in-service units of the network, in order.
    /// </summary>
    public static LossCoefficients FromPowerFlow(PowerFlowResult pf)
    {
        var net = pf.Network;
        int n = net.BusCount;
        double baseMva = net.BaseMva;
        var y = AdmittanceMatrix.Build(net).Y.ToDense();
        var z = y.Inverse();
        var v = pf.Voltages;
        var iLoad = new Complex[n];
        Complex iD = 0;
        for (int k = 0; k < n; k++)
        {
            var b = pf.Buses[k];
            var sl = new Complex(b.PLoad, b.QLoad) / baseMva;
            iLoad[k] = Complex.Conjugate(sl / v[k]);
            iD += iLoad[k];
        }
        if (iD.Magnitude < 1e-12) throw new InvalidOperationException("No load current; loss formula undefined.");
        int r = net.SlackBus is { } sb ? net.IndexOf(sb.Id) : 0;
        var l = iLoad.Select(c => c / iD).ToArray();
        Complex rho = 0;
        for (int k = 0; k < n; k++) rho += l[k] * z[r, k];

        var gens = pf.Generators.ToList();
        int m = gens.Count;
        var c = new DenseMatrix<Complex>(n, m + 1);
        var gamma = new Complex[m + 1];
        // bus totals for Q/P ratio
        var busP = new Dictionary<int, double>(); var busQ = new Dictionary<int, double>();
        foreach (var g in gens) { busP[g.Bus] = busP.GetValueOrDefault(g.Bus) + g.P; busQ[g.Bus] = busQ.GetValueOrDefault(g.Bus) + g.Q; }
        for (int gi = 0; gi < m; gi++)
        {
            int bg = net.IndexOf(gens[gi].Bus);
            for (int k = 0; k < n; k++) c[k, gi] = (k == bg ? 1 : 0) - l[k] * z[r, bg] / rho;
            double pb = busP[gens[gi].Bus], qb = busQ[gens[gi].Bus];
            double ratio = Math.Abs(pb) > 1e-6 ? qb / pb : 0;
            gamma[gi] = new Complex(1, -ratio) / Complex.Conjugate(v[bg]);
        }
        for (int k = 0; k < n; k++) c[k, m] = -l[k];
        gamma[m] = -v[r] / rho;

        // T = Γᴴ Cᴴ R C Γ
        var rc = new Complex[n, m + 1];
        for (int a = 0; a < n; a++)
            for (int j = 0; j <= m; j++)
            {
                Complex s = 0;
                for (int k = 0; k < n; k++) s += z[a, k].Real * c[k, j];
                rc[a, j] = s;
            }
        var t = new double[m + 1, m + 1];
        for (int i = 0; i <= m; i++)
            for (int j = 0; j <= m; j++)
            {
                Complex s = 0;
                for (int a = 0; a < n; a++) s += Complex.Conjugate(c[a, i]) * rc[a, j];
                t[i, j] = (Complex.Conjugate(gamma[i]) * s * gamma[j]).Real;
            }
        var bm = new double[m, m];
        var b0 = new double[m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) bm[i, j] = 0.5 * (t[i, j] + t[j, i]) / baseMva;
            b0[i] = (t[i, m] + t[m, i]);
        }
        return new LossCoefficients(bm, b0, t[m, m] * baseMva);
    }
}

/// <summary>
/// Classical economic dispatch by λ-iteration (bisection on the incremental cost) with unit limits and, optionally,
/// transmission losses through B-coefficients (coordination equations dC_i/dP_i · L_i = λ, solved by inner Gauss–Seidel).
/// </summary>
public static class EconomicDispatch
{
    /// <param name="demandMw">Total demand [MW] to be met (plus losses when <paramref name="losses"/> is given).</param>
    public static EconomicDispatchResult Solve(IReadOnlyList<DispatchUnit> units, double demandMw, LossCoefficients? losses = null,
        double toleranceMw = 1e-7, int maxIterations = 200)
    {
        int n = units.Count;
        if (n == 0) throw new ArgumentException("No units.", nameof(units));
        double pminSum = units.Sum(u => u.PminMw), pmaxSum = units.Sum(u => u.PmaxMw);
        if (losses is null && (demandMw < pminSum - 1e-9 || demandMw > pmaxSum + 1e-9))
            throw new InvalidOperationException($"Demand {demandMw:0.###} MW outside the feasible range [{pminSum:0.###}, {pmaxSum:0.###}] MW.");
        var p = new double[n];
        double lo = units.Min(u => u.Cost.Marginal(u.PminMw)) - 1;
        double hi = units.Max(u => u.Cost.Marginal(u.PmaxMw)) + 1;
        if (losses is not null) hi = hi * 3 + 100;
        int it = 0;
        double lambda = 0, pl = 0;
        bool converged = false;
        for (; it < maxIterations; it++)
        {
            lambda = 0.5 * (lo + hi);
            pl = Allocate(units, lambda, losses, p);
            double mismatch = p.Sum() - demandMw - pl;
            if (Math.Abs(mismatch) < toleranceMw) { converged = true; break; }
            if (mismatch > 0) hi = lambda; else lo = lambda;
            if (hi - lo < 1e-13 * Math.Max(1, Math.Abs(lambda))) break;
        }
        if (!converged)
        {
            // linear-cost units sitting exactly at λ absorb the residual mismatch
            double mismatch = p.Sum() - demandMw - pl;
            for (int i = 0; i < n && Math.Abs(mismatch) > toleranceMw; i++)
            {
                var u = units[i];
                if (u.Cost.C2 > 0 && Math.Abs(u.Cost.Marginal(p[i]) - lambda) > 1e-6) continue;
                double np = Math.Clamp(p[i] - mismatch, u.PminMw, u.PmaxMw);
                mismatch -= p[i] - np;
                p[i] = np;
            }
            if (losses is not null) pl = losses.Losses(p);
            converged = Math.Abs(p.Sum() - demandMw - pl) < Math.Max(toleranceMw, 1e-6);
        }
        var pf = new double[n];
        for (int i = 0; i < n; i++) pf[i] = losses is null ? 1 : 1 / (1 - losses.Incremental(p, i));
        double cost = 0;
        for (int i = 0; i < n; i++) cost += units[i].Cost.Evaluate(p[i]);
        return new EconomicDispatchResult(lambda, p, pl, cost, it + 1, converged, pf);
    }

    /// <summary>Dispatches the in-service generators of a network to its total load (lossless unless loss coefficients are given).</summary>
    public static EconomicDispatchResult Solve(PowerNetwork net, LossCoefficients? losses = null) =>
        Solve(DispatchUnit.FromNetwork(net), net.TotalLoadMw, losses);

    /// <summary>
    /// Loss-aware dispatch of a network: B-coefficients are derived from an AC power flow, the dispatch is solved,
    /// written back to the generators, and the procedure repeats until the outputs settle (typically 2–4 passes).
    /// </summary>
    public static EconomicDispatchResult SolveWithLosses(PowerNetwork net, int passes = 5)
    {
        var work = net.Clone();
        var units = DispatchUnit.FromNetwork(work);
        var inService = work.Generators.Where(g => g.InService).ToList();
        EconomicDispatchResult? res = null;
        for (int k = 0; k < passes; k++)
        {
            var pf = PowerFlowSolver.Solve(work);
            if (!pf.Converged) throw new InvalidOperationException("Power flow did not converge while computing loss coefficients.");
            var b = LossCoefficients.FromPowerFlow(pf);
            var r = Solve(units, work.TotalLoadMw, b);
            double change = res is null ? double.PositiveInfinity : r.OutputMw.Zip(res.OutputMw).Max(t => Math.Abs(t.First - t.Second));
            res = r;
            for (int i = 0; i < inService.Count; i++) inService[i].P = r.OutputMw[i];
            if (change < 1e-3) break;
        }
        return res!;
    }

    private static double Allocate(IReadOnlyList<DispatchUnit> units, double lambda, LossCoefficients? losses, double[] p)
    {
        int n = units.Count;
        if (losses is null)
        {
            for (int i = 0; i < n; i++) p[i] = units[i].OutputAt(lambda);
            return 0;
        }
        // Saadat eq. (7.67): P_i = [λ(1 − B0_i − 2Σ_{j≠i}B_ij P_j) − β_i] / [2(γ_i + λB_ii)], Gauss–Seidel sweeps
        for (int i = 0; i < n; i++) if (p[i] == 0) p[i] = 0.5 * (units[i].PminMw + Math.Min(units[i].PmaxMw, 1e4));
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double delta = 0;
            for (int i = 0; i < n; i++)
            {
                var u = units[i];
                double s = 0;
                for (int j = 0; j < n; j++) if (j != i) s += losses.B[i, j] * p[j];
                double num = lambda * (1 - losses.B0[i] - 2 * s) - u.Cost.C1;
                double den = 2 * (u.Cost.C2 + lambda * losses.B[i, i]);
                double np = den > 0 ? Math.Clamp(num / den, u.PminMw, u.PmaxMw) : (num > 0 ? u.PmaxMw : u.PminMw);
                delta = Math.Max(delta, Math.Abs(np - p[i]));
                p[i] = np;
            }
            if (delta < 1e-10) break;
        }
        return losses.Losses(p);
    }
}
