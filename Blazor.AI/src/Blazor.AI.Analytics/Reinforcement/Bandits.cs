using Gravicode.Science.GraviNum;
using Gravicode.Science.GraviProb;

namespace Blazor.AI.Analytics;

public enum BanditStrategy
{
    EpsilonGreedy,
    Ucb1,
    ThompsonSampling,
    Uniform,
}

public sealed record BanditRun(BanditStrategy Strategy, double[] CumulativeReward, double[] CumulativeRegret, int[] Pulls, double[] Estimates);

/// <summary>
/// Multi-armed bandits for strategy optimisation (e.g. which offer, banner or price to show): compares exploration
/// strategies on Bernoulli arms. Thompson sampling uses GraviProb Beta posteriors.
/// </summary>
public static class Bandits
{
    public static BanditRun Run(BanditStrategy strategy, IReadOnlyList<double> trueRates, int rounds, double epsilon = 0.1, int seed = 3)
    {
        var rng = new Random(seed);
        var graviRng = new GraviRandom(seed);
        var k = trueRates.Count;
        var pulls = new int[k];
        var wins = new double[k];
        var best = trueRates.Max();
        var cumulativeReward = new double[rounds];
        var cumulativeRegret = new double[rounds];
        double reward = 0, regret = 0;

        for (var t = 0; t < rounds; t++)
        {
            int arm;
            switch (strategy)
            {
                case BanditStrategy.EpsilonGreedy:
                    arm = rng.NextDouble() < epsilon || t < k ? rng.Next(k) : ArgMax(i => pulls[i] == 0 ? 0 : wins[i] / pulls[i], k);
                    break;
                case BanditStrategy.Ucb1:
                    arm = t < k ? t : ArgMax(i => wins[i] / pulls[i] + Math.Sqrt(2 * Math.Log(t + 1) / pulls[i]), k);
                    break;
                case BanditStrategy.ThompsonSampling:
                    arm = ArgMax(i => new Beta(1 + wins[i], 1 + pulls[i] - wins[i]).Sample(graviRng), k);
                    break;
                default:
                    arm = rng.Next(k);
                    break;
            }

            var success = rng.NextDouble() < trueRates[arm] ? 1 : 0;
            pulls[arm]++;
            wins[arm] += success;
            reward += success;
            regret += best - trueRates[arm];
            cumulativeReward[t] = reward;
            cumulativeRegret[t] = regret;
        }

        return new BanditRun(strategy, cumulativeReward, cumulativeRegret, pulls, Enumerable.Range(0, k).Select(i => pulls[i] == 0 ? 0 : wins[i] / pulls[i]).ToArray());
    }

    private static int ArgMax(Func<int, double> score, int k)
    {
        var best = 0;
        var bestValue = double.MinValue;
        for (var i = 0; i < k; i++)
        {
            var value = score(i);
            if (value > bestValue)
            {
                bestValue = value;
                best = i;
            }
        }

        return best;
    }
}
