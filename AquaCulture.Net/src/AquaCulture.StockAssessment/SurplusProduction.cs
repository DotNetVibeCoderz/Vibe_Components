using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Hosting;

namespace AquaCulture.StockAssessment;

/// <summary>Annual catch and effort of a fishery.</summary>
public sealed record CatchEffort(int Year, double Catch, double Effort)
{
    public double Cpue => Catch / Effort;
}

/// <summary>Equilibrium surplus-production result.</summary>
public sealed record EquilibriumFit(string Model, double Msy, double EffortAtMsy, double RSquared, double A, double B)
{
    /// <summary>Equilibrium yield at an effort level.</summary>
    public double YieldAt(double effort) => Model == "Fox" ? effort * Math.Exp(A + B * effort) : A * effort + B * effort * effort;
}

public enum KobeStatus { Healthy, Recovering, OverfishingHealthyStock, Overfished }

/// <summary>Dynamic Schaefer fit (observation-error estimator).</summary>
public sealed record DynamicSchaeferFit(double R, double K, double Q, double Msy, double Bmsy, double Fmsy, IReadOnlyList<double> Biomass,
    IReadOnlyList<double> PredictedCpue, double BOverBmsy, double FOverFmsy, KobeStatus Status, double Rmse)
{
    public static Text2 StatusName(KobeStatus s) => s switch
    {
        KobeStatus.Healthy => new("Healthy (not overfished, no overfishing)", "Sehat (tidak overfished, tanpa overfishing)"),
        KobeStatus.Recovering => new("Overfished but recovering (F < Fmsy)", "Overfished namun pulih (F < Fmsy)"),
        KobeStatus.OverfishingHealthyStock => new("Overfishing on a healthy stock", "Overfishing pada stok yang masih sehat"),
        _ => new("Overfished and subject to overfishing", "Overfished dan terjadi overfishing"),
    };
}

/// <summary>Surplus-production (biomass dynamic) models: Schaefer (1954) and Fox (1970), equilibrium and dynamic.</summary>
public static class SurplusProduction
{
    /// <summary>Schaefer equilibrium (Gulland method): CPUE = a + b·E, MSY = −a²/(4b), E_msy = −a/(2b).</summary>
    public static EquilibriumFit Schaefer(IReadOnlyList<CatchEffort> data)
    {
        var fit = Stats.LinearRegression(data.Select(d => d.Effort).ToArray(), data.Select(d => d.Cpue).ToArray());
        double a = fit.Intercept, b = fit.Slope;
        return new EquilibriumFit("Schaefer", -a * a / (4 * b), -a / (2 * b), fit.RSquared, a, b);
    }

    /// <summary>Fox exponential model: ln CPUE = c + d·E, E_msy = −1/d, MSY = −(1/d)·e^(c−1).</summary>
    public static EquilibriumFit Fox(IReadOnlyList<CatchEffort> data)
    {
        var fit = Stats.LinearRegression(data.Select(d => d.Effort).ToArray(), data.Select(d => Math.Log(d.Cpue)).ToArray());
        double c = fit.Intercept, d = fit.Slope;
        return new EquilibriumFit("Fox", -(1 / d) * Math.Exp(c - 1), -1 / d, fit.RSquared, c, d);
    }

    /// <summary>Projects Schaefer biomass B[t+1] = B[t] + r·B[t]·(1 − B[t]/K) − C[t], starting at B0 = depletion·K.</summary>
    public static double[] ProjectBiomass(double r, double k, IReadOnlyList<double> catches, double initialDepletion = 1)
    {
        var b = new double[catches.Count + 1];
        b[0] = k * initialDepletion;
        for (int t = 0; t < catches.Count; t++) b[t + 1] = Math.Max(1e-6 * k, b[t] + r * b[t] * (1 - b[t] / k) - catches[t]);
        return b;
    }

    /// <summary>
    /// Fits the dynamic Schaefer model to a CPUE index by minimising log-residuals (Nelder–Mead), with q solved analytically.
    /// </summary>
    public static DynamicSchaeferFit FitDynamic(IReadOnlyList<CatchEffort> data, double initialDepletion = 1)
    {
        var catches = data.Select(d => d.Catch).ToArray();
        var cpue = data.Select(d => d.Cpue).ToArray();
        double maxC = catches.Max();

        (double Sse, double Q, double[] B) Evaluate(double r, double k)
        {
            var b = ProjectBiomass(r, k, catches, initialDepletion);
            double lq = 0;
            for (int t = 0; t < cpue.Length; t++) lq += Math.Log(cpue[t] / b[t]);
            lq /= cpue.Length;
            double sse = 0;
            for (int t = 0; t < cpue.Length; t++) { double e = Math.Log(cpue[t]) - (lq + Math.Log(b[t])); sse += e * e; }
            return (sse, Math.Exp(lq), b);
        }

        // multi-start in log-space for robustness
        OptimizationResult? best = null;
        foreach (double r0 in new[] { 0.2, 0.5, 0.9 })
            foreach (double kMul in new[] { 5.0, 10.0, 20.0 })
            {
                var res = Numerics.NelderMead(p =>
                {
                    double r = Math.Exp(p[0]), k = Math.Exp(p[1]);
                    if (r > 2.5 || k < maxC) return 1e9;
                    return Evaluate(r, k).Sse;
                }, [Math.Log(r0), Math.Log(maxC * kMul)], [0.3, 0.3], 4000, 1e-12);
                if (best is null || res.Value < best.Value) best = res;
            }
        double rr = Math.Exp(best!.Parameters[0]), kk = Math.Exp(best.Parameters[1]);
        var (sse, q, bio) = Evaluate(rr, kk);
        double msy = rr * kk / 4, bmsy = kk / 2, fmsy = rr / 2;
        double bLast = bio[^2], fLast = catches[^1] / bLast;
        double bb = bLast / bmsy, ff = fLast / fmsy;
        var status = bb >= 1 ? (ff <= 1 ? KobeStatus.Healthy : KobeStatus.OverfishingHealthyStock) : (ff <= 1 ? KobeStatus.Recovering : KobeStatus.Overfished);
        var pred = bio.Take(cpue.Length).Select(b => q * b).ToList();
        return new DynamicSchaeferFit(rr, kk, q, msy, bmsy, fmsy, bio, pred, bb, ff, status, Math.Sqrt(sse / cpue.Length));
    }
}

/// <summary>Stock–recruitment models.</summary>
public static class StockRecruitment
{
    /// <summary>Beverton–Holt R = a·S / (1 + b·S), fitted by regressing S/R on S.</summary>
    public static (double A, double B, double RSquared) FitBevertonHolt(IReadOnlyList<(double Spawners, double Recruits)> data)
    {
        var fit = Stats.LinearRegression(data.Select(d => d.Spawners).ToArray(), data.Select(d => d.Spawners / d.Recruits).ToArray());
        double a = 1 / fit.Intercept, b = fit.Slope * a;
        return (a, b, fit.RSquared);
    }

    public static double BevertonHolt(double a, double b, double spawners) => a * spawners / (1 + b * spawners);

    /// <summary>Ricker R = a·S·e^(−b·S), fitted by regressing ln(R/S) on S.</summary>
    public static (double A, double B, double RSquared) FitRicker(IReadOnlyList<(double Spawners, double Recruits)> data)
    {
        var fit = Stats.LinearRegression(data.Select(d => d.Spawners).ToArray(), data.Select(d => Math.Log(d.Recruits / d.Spawners)).ToArray());
        return (Math.Exp(fit.Intercept), -fit.Slope, fit.RSquared);
    }

    public static double Ricker(double a, double b, double spawners) => a * spawners * Math.Exp(-b * spawners);
}

public static class StockAssessmentBuilderExtensions
{
    public static AquaBuilder AddStockAssessment(this AquaBuilder b) => b.TryAddModule("StockAssessment", _ => { });
}
