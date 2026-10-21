namespace Blazor.AI.Analytics;

/// <summary>The outcome of a statistical test, phrased for non-statisticians in <see cref="Interpretation"/>.</summary>
public sealed record TestResult(string Test, double Statistic, string StatisticName, double PValue, double? DegreesOfFreedom, double Alpha, string Interpretation)
{
    public bool Significant => PValue < Alpha;

    public double? EffectSize { get; init; }

    public string? EffectName { get; init; }

    public (double Low, double High)? ConfidenceInterval { get; init; }
}

/// <summary>Classical hypothesis tests with exact p-values from <see cref="SpecialFunctions"/>.</summary>
public static class HypothesisTests
{
    /// <summary>Welch's t-test (unequal variances) for two independent samples.</summary>
    public static TestResult WelchTTest(IReadOnlyList<double> a, IReadOnlyList<double> b, double alpha = 0.05, string nameA = "A", string nameB = "B")
    {
        var (ma, va, na) = Moments(a);
        var (mb, vb, nb) = Moments(b);
        var se = Math.Sqrt(va / na + vb / nb);
        var t = se == 0 ? 0 : (ma - mb) / se;
        var df = Math.Pow(va / na + vb / nb, 2) / (Math.Pow(va / na, 2) / (na - 1) + Math.Pow(vb / nb, 2) / (nb - 1));
        var p = SpecialFunctions.StudentTTwoSided(t, df);
        var pooled = Math.Sqrt(((na - 1) * va + (nb - 1) * vb) / (na + nb - 2));
        var d = pooled == 0 ? 0 : (ma - mb) / pooled;
        var crit = SpecialFunctions.StudentTCritical(alpha, df);
        return new TestResult("Welch two-sample t-test", t, "t", p, df, alpha,
            p < alpha
                ? $"The mean of {nameA} ({ma:0.##}) differs from {nameB} ({mb:0.##}); a difference this large would happen by chance only {p:P2} of the time."
                : $"No significant difference between {nameA} ({ma:0.##}) and {nameB} ({mb:0.##}) at the {alpha:P0} level.")
        {
            EffectSize = d,
            EffectName = "Cohen's d",
            ConfidenceInterval = (ma - mb - crit * se, ma - mb + crit * se),
        };
    }

    /// <summary>Paired t-test (before/after on the same subjects).</summary>
    public static TestResult PairedTTest(IReadOnlyList<double> before, IReadOnlyList<double> after, double alpha = 0.05)
    {
        var diffs = before.Zip(after, (x, y) => y - x).ToArray();
        var (m, v, n) = Moments(diffs);
        var se = Math.Sqrt(v / n);
        var t = se == 0 ? 0 : m / se;
        var p = SpecialFunctions.StudentTTwoSided(t, n - 1);
        var crit = SpecialFunctions.StudentTCritical(alpha, n - 1);
        return new TestResult("Paired t-test", t, "t", p, n - 1, alpha,
            p < alpha ? $"The average change ({m:+0.##;-0.##}) is statistically significant." : $"The average change ({m:+0.##;-0.##}) could be chance.")
        {
            EffectSize = Math.Sqrt(v) == 0 ? 0 : m / Math.Sqrt(v),
            EffectName = "Cohen's dz",
            ConfidenceInterval = (m - crit * se, m + crit * se),
        };
    }

    /// <summary>One-sample t-test against a hypothesised mean.</summary>
    public static TestResult OneSampleTTest(IReadOnlyList<double> sample, double mu, double alpha = 0.05)
    {
        var (m, v, n) = Moments(sample);
        var se = Math.Sqrt(v / n);
        var t = se == 0 ? 0 : (m - mu) / se;
        var p = SpecialFunctions.StudentTTwoSided(t, n - 1);
        return new TestResult("One-sample t-test", t, "t", p, n - 1, alpha,
            p < alpha ? $"The mean ({m:0.##}) is significantly different from {mu:0.##}." : $"The mean ({m:0.##}) is consistent with {mu:0.##}.")
        {
            EffectSize = Math.Sqrt(v) == 0 ? 0 : (m - mu) / Math.Sqrt(v),
            EffectName = "Cohen's d",
        };
    }

    /// <summary>One-way ANOVA across several groups.</summary>
    public static TestResult OneWayAnova(IReadOnlyList<IReadOnlyList<double>> groups, double alpha = 0.05)
    {
        var all = groups.SelectMany(g => g).ToArray();
        var grand = all.Average();
        var ssBetween = groups.Sum(g => g.Count * Math.Pow(g.Average() - grand, 2));
        var ssWithin = groups.Sum(g => { var m = g.Average(); return g.Sum(x => (x - m) * (x - m)); });
        double dfB = groups.Count - 1, dfW = all.Length - groups.Count;
        var f = dfW == 0 || ssWithin == 0 ? 0 : ssBetween / dfB / (ssWithin / dfW);
        var p = SpecialFunctions.FUpper(f, dfB, dfW);
        return new TestResult("One-way ANOVA", f, "F", p, dfB, alpha,
            p < alpha ? "At least one group mean differs from the others." : "The group means are not significantly different.")
        {
            EffectSize = ssBetween / (ssBetween + ssWithin),
            EffectName = "η²",
        };
    }

    /// <summary>Chi-square test of independence on a contingency table (rows × columns of counts).</summary>
    public static TestResult ChiSquareIndependence(double[,] observed, double alpha = 0.05)
    {
        int rows = observed.GetLength(0), cols = observed.GetLength(1);
        var total = observed.Cast<double>().Sum();
        double chi = 0;
        for (var r = 0; r < rows; r++)
        {
            var rowSum = Enumerable.Range(0, cols).Sum(c => observed[r, c]);
            for (var c = 0; c < cols; c++)
            {
                var colSum = Enumerable.Range(0, rows).Sum(i => observed[i, c]);
                var expected = rowSum * colSum / total;
                if (expected > 0)
                {
                    chi += Math.Pow(observed[r, c] - expected, 2) / expected;
                }
            }
        }

        var df = (rows - 1) * (cols - 1);
        var p = SpecialFunctions.ChiSquareUpper(chi, df);
        return new TestResult("Chi-square test of independence", chi, "χ²", p, df, alpha,
            p < alpha ? "The two categorical variables are associated." : "No evidence that the variables are associated.")
        {
            EffectSize = Math.Sqrt(chi / (total * Math.Max(1, Math.Min(rows, cols) - 1))),
            EffectName = "Cramér's V",
        };
    }

    /// <summary>Two-proportion z-test (e.g. conversion rates).</summary>
    public static TestResult TwoProportionZ(int successA, int totalA, int successB, int totalB, double alpha = 0.05)
    {
        double pa = successA / (double)totalA, pb = successB / (double)totalB;
        var pooled = (successA + successB) / (double)(totalA + totalB);
        var se = Math.Sqrt(pooled * (1 - pooled) * (1.0 / totalA + 1.0 / totalB));
        var z = se == 0 ? 0 : (pb - pa) / se;
        var p = 2 * (1 - SpecialFunctions.NormalCdf(Math.Abs(z)));
        return new TestResult("Two-proportion z-test", z, "z", p, null, alpha,
            p < alpha ? $"B's rate ({pb:P1}) differs significantly from A's ({pa:P1})." : $"The difference between {pa:P1} and {pb:P1} could be chance.")
        {
            EffectSize = pb - pa,
            EffectName = "Difference",
        };
    }

    public static (double Mean, double Variance, int Count) Moments(IReadOnlyList<double> values)
    {
        var n = values.Count;
        var mean = values.Average();
        var variance = n < 2 ? 0 : values.Sum(v => (v - mean) * (v - mean)) / (n - 1);
        return (mean, variance, n);
    }
}
