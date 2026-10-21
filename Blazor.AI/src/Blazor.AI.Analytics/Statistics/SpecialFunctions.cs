namespace Blazor.AI.Analytics;

/// <summary>Numerically stable special functions for p-values (Lanczos log-gamma, continued fractions).</summary>
public static class SpecialFunctions
{
    private static readonly double[] Lanczos =
    [
        676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
        12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    public static double LogGamma(double x)
    {
        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        }

        x -= 1;
        var a = 0.99999999999980993;
        var t = x + 7.5;
        for (var i = 0; i < Lanczos.Length; i++)
        {
            a += Lanczos[i] / (x + i + 1);
        }

        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    /// <summary>Regularised incomplete beta I_x(a, b).</summary>
    public static double IncompleteBeta(double a, double b, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var front = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        return x < (a + 1) / (a + b + 2)
            ? front * BetaContinuedFraction(a, b, x) / a
            : 1 - front * BetaContinuedFraction(b, a, 1 - x) / b;
    }

    /// <summary>Regularised lower incomplete gamma P(a, x).</summary>
    public static double IncompleteGamma(double a, double x)
    {
        if (x <= 0) return 0;
        if (x < a + 1)
        {
            double sum = 1 / a, term = sum;
            for (var n = 1; n < 500; n++)
            {
                term *= x / (a + n);
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * 1e-14) break;
            }

            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }

        // Continued fraction for Q(a, x) (Lentz).
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
        for (var i = 1; i < 500; i++)
        {
            var an = -i * (i - a);
            b += 2;
            d = an * d + b;
            if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c;
            if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-14) break;
        }

        return 1 - Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }

    /// <summary>Two-sided p-value of a Student t statistic.</summary>
    public static double StudentTTwoSided(double t, double df) => IncompleteBeta(df / 2, 0.5, df / (df + t * t));

    /// <summary>Upper-tail p-value of an F statistic.</summary>
    public static double FUpper(double f, double df1, double df2) => f <= 0 ? 1 : IncompleteBeta(df2 / 2, df1 / 2, df2 / (df2 + df1 * f));

    /// <summary>Upper-tail p-value of a chi-square statistic.</summary>
    public static double ChiSquareUpper(double x, double df) => 1 - IncompleteGamma(df / 2, x / 2);

    /// <summary>Standard normal CDF (Abramowitz–Stegun 7.1.26 via erf).</summary>
    public static double NormalCdf(double z) => 0.5 * (1 + Erf(z / Math.Sqrt(2)));

    public static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * x);
        var y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }

    /// <summary>Two-sided critical t value via bisection (used for confidence intervals).</summary>
    public static double StudentTCritical(double alpha, double df)
    {
        double lo = 0, hi = 100;
        for (var i = 0; i < 100; i++)
        {
            var mid = (lo + hi) / 2;
            if (StudentTTwoSided(mid, df) > alpha) lo = mid; else hi = mid;
        }

        return (lo + hi) / 2;
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const double tiny = 1e-300;
        double qab = a + b, qap = a + 1, qam = a - 1, c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1 / d;
        var h = d;
        for (var m = 1; m <= 300; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d;
            if (Math.Abs(d) < tiny) d = tiny;
            c = 1 + aa / c;
            if (Math.Abs(c) < tiny) c = tiny;
            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 3e-14) break;
        }

        return h;
    }
}
