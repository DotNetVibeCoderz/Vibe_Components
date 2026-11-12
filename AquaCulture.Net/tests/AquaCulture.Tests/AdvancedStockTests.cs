using AquaCulture.StockAssessment;

namespace AquaCulture.Tests;

public class AdvancedStockTests
{
    [Fact]
    public void Elefan_recovers_the_growth_curve_of_synthetic_length_frequencies()
    {
        var lfq = Elefan.Synthetic(lInf: 60, k: 0.5, tAnchor: 0.3);
        var (scores, asp) = Elefan.Restructure(lfq);
        Assert.True(asp > 0);
        Assert.All(scores, s => Assert.Equal(s.Where(v => v > 0).Sum(), -s.Where(v => v < 0).Sum(), 6));   // balanced peaks and troughs

        var lGrid = Enumerable.Range(0, 21).Select(i => 50 + i * 1.0).ToArray();
        var kGrid = Enumerable.Range(0, 17).Select(i => 0.3 + i * 0.025).ToArray();
        var fit = Elefan.Fit(lfq, lGrid, kGrid, anchorSteps: 20);
        Assert.InRange(fit.LInfinity, 57, 63);
        Assert.InRange(fit.K, 0.45, 0.55);
        Assert.True(fit.Rn > Elefan.Rn(Elefan.Esp(lfq, scores, 52, 0.8, fit.TAnchor), asp), "the true curve scores above a wrong one");
        Assert.Equal(Math.Log10(0.5) + 2 * Math.Log10(60), new ElefanResult(60, 0.5, 0, 0, [], [], new double[0, 0]).Phi, 9);
    }

    [Fact]
    public void Cmsy_estimates_msy_from_catches_only()
    {
        var data = BayesianSurplusProduction.Synthetic(r: 0.5, k: 10_000);
        var catches = data.Select(d => d.Catch).ToList();
        var (start, end) = Cmsy.DepletionPriors(catches);
        Assert.Equal((0.5, 0.9), start);                 // the fishery started small
        Assert.Equal((0.01, 0.4), end);                  // the last catch is just under half the peak; true B/K ≈ 0.36 lies inside
        var c = Cmsy.Fit(catches, Resilience.Medium);
        Assert.True(c.ViablePairs > 50);
        Assert.InRange(c.Msy, 1250 * 0.8, 1250 * 1.2);
        Assert.InRange(1250, c.MsyRange.Lo, c.MsyRange.Hi);
        // the r estimate follows the resilience prior, and lower r needs a larger K for the same catches
        var low = Cmsy.Fit(catches, Resilience.Low);
        Assert.True(low.R < c.R && low.K > c.K);
        // a high-resilience stock (r ≥ 0.6) could not have been depleted this far by these catches: no viable pairs
        Assert.Throws<InvalidOperationException>(() => Cmsy.Fit(catches, Resilience.High));
    }

    [Fact]
    public void Bayesian_surplus_production_gives_credible_intervals_and_kobe_probabilities()
    {
        var data = BayesianSurplusProduction.Synthetic(r: 0.5, k: 10_000, sigma: 0.1);
        var b = BayesianSurplusProduction.Fit(data, Resilience.Medium);
        Assert.InRange(b.AcceptanceRate, 0.1, 0.5);
        Assert.InRange(b.Msy.Median, 1250 * 0.9, 1250 * 1.1);
        Assert.True(b.Msy.Lo < b.Msy.Median && b.Msy.Median < b.Msy.Hi);
        // true final B/Bmsy ≈ 0.72: overfished, fishing just around Fmsy after the management phase
        Assert.InRange(0.72, b.FinalBOverBmsy.Lo, b.FinalBOverBmsy.Hi);
        Assert.True(b.ProbabilityOverfished > 0.9);
        Assert.InRange(b.ProbabilityOverfishing, 0.1, 0.9);
        Assert.Equal(b, BayesianSurplusProduction.Fit(data, Resilience.Medium) with { Draws = b.Draws });   // deterministic

        // noisier index → wider interval
        var noisy = BayesianSurplusProduction.Fit(BayesianSurplusProduction.Synthetic(sigma: 0.3), Resilience.Medium);
        Assert.True(noisy.Msy.Hi - noisy.Msy.Lo > b.Msy.Hi - b.Msy.Lo);
    }
}
