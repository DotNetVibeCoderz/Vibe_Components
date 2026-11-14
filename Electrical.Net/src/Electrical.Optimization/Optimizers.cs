using Electrical.Core.Hosting;
using Electrical.PowerFlow;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Optimization;

/// <summary>Result of a derivative-free minimization.</summary>
public sealed record OptimizationResult(double[] X, double Value, int Iterations, int Evaluations, bool Converged);

/// <summary>Nelder–Mead downhill simplex (Lagarias et al. 1998 coefficients: reflection 1, expansion 2, contraction ½, shrink ½).</summary>
public static class NelderMead
{
    /// <param name="initialStep">Simplex edge length per coordinate (default 5 % of |x0| or 0.00025 for zeros, as in MATLAB fminsearch).</param>
    public static OptimizationResult Minimize(Func<double[], double> f, double[] x0, double[]? initialStep = null, double tolerance = 1e-10,
        int maxIterations = 5000)
    {
        int n = x0.Length, evals = 0;
        double F(double[] x) { evals++; return f(x); }
        var pts = new double[n + 1][];
        var fv = new double[n + 1];
        pts[0] = (double[])x0.Clone();
        fv[0] = F(pts[0]);
        for (int i = 0; i < n; i++)
        {
            var p = (double[])x0.Clone();
            p[i] += initialStep?[i] ?? (x0[i] != 0 ? 0.05 * x0[i] : 0.00025);
            pts[i + 1] = p;
            fv[i + 1] = F(p);
        }
        int it = 0;
        bool converged = false;
        for (; it < maxIterations; it++)
        {
            var idx = Enumerable.Range(0, n + 1).OrderBy(i => fv[i]).ToArray();
            pts = idx.Select(i => pts[i]).ToArray();
            fv = idx.Select(i => fv[i]).ToArray();
            double size = 0;
            for (int i = 1; i <= n; i++) for (int j = 0; j < n; j++) size = Math.Max(size, Math.Abs(pts[i][j] - pts[0][j]));
            if (Math.Abs(fv[n] - fv[0]) <= tolerance * (1 + Math.Abs(fv[0])) && size <= Math.Sqrt(tolerance) * (1 + pts[0].Max(Math.Abs)))
            { converged = true; break; }
            var c = new double[n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) c[j] += pts[i][j] / n;
            double[] Along(double t) { var r = new double[n]; for (int j = 0; j < n; j++) r[j] = c[j] + t * (pts[n][j] - c[j]); return r; }
            var xr = Along(-1); double fr = F(xr);
            if (fr < fv[0])
            {
                var xe = Along(-2); double fe = F(xe);
                if (fe < fr) { pts[n] = xe; fv[n] = fe; } else { pts[n] = xr; fv[n] = fr; }
            }
            else if (fr < fv[n - 1]) { pts[n] = xr; fv[n] = fr; }
            else
            {
                var xc = fr < fv[n] ? Along(-0.5) : Along(0.5);
                double fc = F(xc);
                if (fc < Math.Min(fr, fv[n])) { pts[n] = xc; fv[n] = fc; }
                else
                {
                    for (int i = 1; i <= n; i++)
                    {
                        for (int j = 0; j < n; j++) pts[i][j] = pts[0][j] + 0.5 * (pts[i][j] - pts[0][j]);
                        fv[i] = F(pts[i]);
                    }
                }
            }
        }
        int b = Array.IndexOf(fv, fv.Min());
        return new OptimizationResult(pts[b], fv[b], it, evals, converged);
    }
}

/// <summary>
/// Global-best particle swarm optimization with constriction coefficients (Clerc &amp; Kennedy 2002: w = 0.7298, c1 = c2 = 1.49618)
/// inside box bounds. Deterministic for a given seed.
/// </summary>
public static class ParticleSwarm
{
    public static OptimizationResult Minimize(Func<double[], double> f, double[] lower, double[] upper, int particles = 30, int iterations = 200,
        int seed = 12345, double tolerance = 0)
    {
        int n = lower.Length, evals = 0;
        var rng = new Random(seed);
        var x = new double[particles][]; var v = new double[particles][];
        var pb = new double[particles][]; var pbf = new double[particles];
        double[] gb = new double[n]; double gbf = double.PositiveInfinity;
        for (int k = 0; k < particles; k++)
        {
            x[k] = new double[n]; v[k] = new double[n];
            for (int j = 0; j < n; j++)
            {
                double w = upper[j] - lower[j];
                x[k][j] = lower[j] + rng.NextDouble() * w;
                v[k][j] = (rng.NextDouble() - 0.5) * w * 0.2;
            }
            pb[k] = (double[])x[k].Clone();
            pbf[k] = f(x[k]); evals++;
            if (pbf[k] < gbf) { gbf = pbf[k]; gb = (double[])x[k].Clone(); }
        }
        const double w0 = 0.7298, c1 = 1.49618, c2 = 1.49618;
        int it = 0;
        for (; it < iterations; it++)
        {
            for (int k = 0; k < particles; k++)
            {
                for (int j = 0; j < n; j++)
                {
                    double vmax = 0.5 * (upper[j] - lower[j]);
                    v[k][j] = Math.Clamp(w0 * v[k][j] + c1 * rng.NextDouble() * (pb[k][j] - x[k][j]) + c2 * rng.NextDouble() * (gb[j] - x[k][j]), -vmax, vmax);
                    x[k][j] = Math.Clamp(x[k][j] + v[k][j], lower[j], upper[j]);
                }
                double fx = f(x[k]); evals++;
                if (fx < pbf[k]) { pbf[k] = fx; pb[k] = (double[])x[k].Clone(); if (fx < gbf) { gbf = fx; gb = (double[])x[k].Clone(); } }
            }
            if (tolerance > 0 && gbf <= tolerance) { it++; break; }
        }
        return new OptimizationResult(gb, gbf, it, evals, true);
    }
}

/// <summary>DI facade for optimization studies.</summary>
public interface IOptimizationService
{
    EconomicDispatchResult EconomicDispatch(PowerNetwork net, bool withLosses = false);
    DcOpfResult DcOpf(PowerNetwork net, DcOpfOptions? options = null);
    UnitCommitmentResult UnitCommitment(IReadOnlyList<UcUnit> units, IReadOnlyList<double> demandMw, double reserveFraction = 0.1);
    CapacitorPlacementResult CapacitorPlacement(PowerNetwork net, int maxCapacitors = 3);
}

internal sealed class OptimizationService : IOptimizationService
{
    public EconomicDispatchResult EconomicDispatch(PowerNetwork net, bool withLosses = false) =>
        withLosses ? Optimization.EconomicDispatch.SolveWithLosses(net) : Optimization.EconomicDispatch.Solve(net);
    public DcOpfResult DcOpf(PowerNetwork net, DcOpfOptions? options = null) => Optimization.DcOpf.Solve(net, options);
    public UnitCommitmentResult UnitCommitment(IReadOnlyList<UcUnit> units, IReadOnlyList<double> demandMw, double reserveFraction = 0.1) =>
        Optimization.UnitCommitment.Solve(units, demandMw, reserveFraction);
    public CapacitorPlacementResult CapacitorPlacement(PowerNetwork net, int maxCapacitors = 3) => Optimization.CapacitorPlacement.Optimize(net, maxCapacitors);
}

public static class OptimizationBuilderExtensions
{
    /// <summary>Registers <see cref="IOptimizationService"/>, <see cref="IAcOpfService"/> (and the PowerFlow module).</summary>
    public static ElectricalBuilder AddOptimization(this ElectricalBuilder b)
    {
        b.AddPowerFlow();
        if (b.TryAddModule("Optimization"))
        {
            b.Services.TryAddSingleton<IOptimizationService, OptimizationService>();
            b.Services.TryAddSingleton<IAcOpfService, AcOpfService>();
        }
        return b;
    }
}
