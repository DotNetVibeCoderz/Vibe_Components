using Gravicode.Science.GraviNum;
using Gravicode.Science.GraviProb;

namespace Blazor.AI.Analytics;

public sealed record BayesianAbResult(
    Beta PosteriorA,
    Beta PosteriorB,
    double ProbabilityBBeatsA,
    double ExpectedLift,
    (double Low, double High) LiftInterval,
    double ExpectedLossChoosingB,
    double[] Grid,
    double[] DensityA,
    double[] DensityB);

/// <summary>Bayesian A/B testing with Beta–Binomial conjugate posteriors (GraviProb) and Monte Carlo comparisons.</summary>
public static class BayesianAb
{
    public static BayesianAbResult Analyse(int successA, int totalA, int successB, int totalB, double priorAlpha = 1, double priorBeta = 1, int samples = 40_000, int seed = 17)
    {
        var prior = new Beta(priorAlpha, priorBeta);
        var postA = prior.PosteriorAfter(successA, totalA - successA);
        var postB = prior.PosteriorAfter(successB, totalB - successB);

        var rng = new GraviRandom(seed);
        var a = postA.Sample(rng, samples).ToArray();
        var b = postB.Sample(rng, samples).ToArray();

        var wins = 0;
        var lifts = new double[samples];
        double loss = 0;
        for (var i = 0; i < samples; i++)
        {
            if (b[i] > a[i]) wins++;
            lifts[i] = a[i] <= 0 ? 0 : (b[i] - a[i]) / a[i];
            loss += Math.Max(0, a[i] - b[i]);
        }

        Array.Sort(lifts);
        var lo = Math.Max(0, Math.Min(postA.Mean, postB.Mean) - 5 * Math.Max(postA.StandardDeviation, postB.StandardDeviation));
        var hi = Math.Min(1, Math.Max(postA.Mean, postB.Mean) + 5 * Math.Max(postA.StandardDeviation, postB.StandardDeviation));
        var grid = Enumerable.Range(0, 160).Select(i => lo + (hi - lo) * i / 159).ToArray();

        return new BayesianAbResult(
            postA,
            postB,
            wins / (double)samples,
            lifts.Average(),
            (lifts[(int)(samples * 0.025)], lifts[(int)(samples * 0.975)]),
            loss / samples,
            grid,
            grid.Select(postA.Density).ToArray(),
            grid.Select(postB.Density).ToArray());
    }
}
