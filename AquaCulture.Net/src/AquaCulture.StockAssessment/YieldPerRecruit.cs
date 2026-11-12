using AquaCulture.Core.Data;

namespace AquaCulture.StockAssessment;

/// <summary>Growth and selection parameters for the Beverton–Holt yield-per-recruit model.</summary>
/// <param name="WInfinityG">Asymptotic weight (g).</param>
/// <param name="K">von Bertalanffy K (year⁻¹).</param>
/// <param name="T0">von Bertalanffy t₀ (years).</param>
/// <param name="M">Natural mortality (year⁻¹).</param>
/// <param name="Tr">Age at recruitment to the fishing ground (years).</param>
/// <param name="Tc">Age at first capture (years).</param>
public sealed record YprParameters(double WInfinityG, double K, double T0, double M, double Tr, double Tc);

public sealed record YprPoint(double F, double YieldPerRecruit, double BiomassPerRecruit);

/// <summary>Reference points of a YPR curve.</summary>
public sealed record YprResult(IReadOnlyList<YprPoint> Curve, double Fmax, double YprAtFmax, double F01, double YprAtF01, double F40, double SprAtF40);

/// <summary>Beverton–Holt (1957) yield per recruit with knife-edge selection.</summary>
public static class YieldPerRecruit
{
    private static readonly double[] U = [1, -3, 3, -1];

    /// <summary>Y/R (g) at fishing mortality F.</summary>
    public static double Ypr(YprParameters p, double f)
    {
        if (f <= 0) return 0;
        double z = f + p.M, s = 0;
        for (int n = 0; n < 4; n++) s += U[n] * Math.Exp(-n * p.K * (p.Tc - p.T0)) / (z + n * p.K);
        return f * Math.Exp(-p.M * (p.Tc - p.Tr)) * p.WInfinityG * s;
    }

    /// <summary>Exploited biomass per recruit (g) at F: (Y/R)/F, or the unfished integral at F = 0.</summary>
    public static double Bpr(YprParameters p, double f)
    {
        if (f > 1e-9) return Ypr(p, f) / f;
        double z = p.M, s = 0;
        for (int n = 0; n < 4; n++) s += U[n] * Math.Exp(-n * p.K * (p.Tc - p.T0)) / (z + n * p.K);
        return Math.Exp(-p.M * (p.Tc - p.Tr)) * p.WInfinityG * s;
    }

    /// <summary>YPR curve over F ∈ [0, fMax] with Fmax, F0.1 and F40 % (biomass-per-recruit proxy).</summary>
    public static YprResult Analyze(YprParameters p, double fMax = 3, int steps = 300)
    {
        var curve = new List<YprPoint>(steps + 1);
        for (int i = 0; i <= steps; i++) { double f = fMax * i / steps; curve.Add(new(f, Ypr(p, f), Bpr(p, f))); }
        var (fm, ym) = Numerics.GoldenMax(f => Ypr(p, f), 1e-4, fMax);
        double slope0 = Ypr(p, 1e-4) / 1e-4;
        double Slope(double f) => (Ypr(p, f + 1e-5) - Ypr(p, f - 1e-5)) / 2e-5;
        double f01 = Slope(fm) > 0.1 * slope0 ? fm : Numerics.Brent(f => Slope(f) - 0.1 * slope0, 2e-5, fm);
        double b0 = Bpr(p, 0);
        double f40 = Bpr(p, fMax) > 0.4 * b0 ? fMax : Numerics.Brent(f => Bpr(p, f) - 0.4 * b0, 1e-6, fMax);
        return new YprResult(curve, fm, ym, f01, Ypr(p, f01), f40, Bpr(p, f40) / b0);
    }
}

/// <summary>Length-based methods in the spirit of FSA / TropFishR.</summary>
public static class LengthBased
{
    /// <summary>Pauly (1980) natural mortality: log₁₀M = −0.0066 − 0.279 log₁₀L∞ + 0.6543 log₁₀K + 0.4634 log₁₀T.</summary>
    public static double PaulyM(double lInfCm, double k, double meanTempC) =>
        Math.Pow(10, -0.0066 - 0.279 * Math.Log10(lInfCm) + 0.6543 * Math.Log10(k) + 0.4634 * Math.Log10(meanTempC));

    /// <summary>Then et al. (2015) growth-based estimator: M = 4.118 K^0.73 L∞^−0.33.</summary>
    public static double ThenM(double lInfCm, double k) => 4.118 * Math.Pow(k, 0.73) * Math.Pow(lInfCm, -0.33);

    /// <summary>
    /// Powell–Wetherall plot from a length-frequency: for each cut-off L′, mean length L̄ of fish ≥ L′;
    /// regress L̄ − L′ on L′ over the fully selected range ⇒ L∞ = −a/b, Z/K = −(1 + b)/b.
    /// </summary>
    public static (double LInfinity, double ZOverK, double RSquared) PowellWetherall(IReadOnlyList<(double LengthCm, double Count)> lfq, double minCutoffCm)
    {
        var sorted = lfq.OrderBy(x => x.LengthCm).ToArray();
        double width = sorted.Length > 1 ? sorted[1].LengthCm - sorted[0].LengthCm : 1;
        var xs = new List<double>(); var ys = new List<double>();
        for (int i = 0; i < sorted.Length; i++)
        {
            double cut = sorted[i].LengthCm - width / 2;
            if (cut < minCutoffCm) continue;
            double n = 0, sum = 0;
            for (int j = i; j < sorted.Length; j++) { n += sorted[j].Count; sum += sorted[j].Count * sorted[j].LengthCm; }
            if (n < 5) break;
            xs.Add(cut); ys.Add(sum / n - cut);
        }
        var fit = Stats.LinearRegression(xs.ToArray(), ys.ToArray());
        return (-fit.Intercept / fit.Slope, -(1 + fit.Slope) / fit.Slope, fit.RSquared);
    }

    /// <summary>
    /// Length-converted catch curve (Pauly 1983): relative age of each class from von Bertalanffy, then ln(N/Δt) vs t on the
    /// descending limb (classes after the modal class, below 0.95 L∞). Returns total mortality Z.
    /// </summary>
    public static (double Z, double RSquared, int Points) CatchCurve(IReadOnlyList<(double LengthCm, double Count)> lfq, double lInfCm, double k, double t0 = 0)
    {
        var s = lfq.OrderBy(x => x.LengthCm).ToArray();
        double width = s.Length > 1 ? s[1].LengthCm - s[0].LengthCm : 1;
        int mode = Array.IndexOf(s, s.MaxBy(x => x.Count));
        var xs = new List<double>(); var ys = new List<double>();
        double Age(double l) => t0 - Math.Log(1 - l / lInfCm) / k;
        for (int i = mode + 1; i < s.Length; i++)
        {
            double l1 = s[i].LengthCm - width / 2, l2 = s[i].LengthCm + width / 2;
            if (l2 >= 0.95 * lInfCm || s[i].Count <= 0) break;
            double dt = Age(l2) - Age(l1), tm = Age(s[i].LengthCm);
            xs.Add(tm); ys.Add(Math.Log(s[i].Count / dt));
        }
        if (xs.Count < 3) return (double.NaN, double.NaN, xs.Count);
        var fit = Stats.LinearRegression(xs.ToArray(), ys.ToArray());
        return (-fit.Slope, fit.RSquared, xs.Count);
    }

    /// <summary>Fishing mortality and exploitation rate E = F/Z (E &gt; 0.5 suggests over-exploitation, Gulland).</summary>
    public static (double F, double E) Exploitation(double z, double m) => (z - m, (z - m) / z);
}
