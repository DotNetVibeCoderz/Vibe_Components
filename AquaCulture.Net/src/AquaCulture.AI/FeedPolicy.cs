using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Species;
using AquaCulture.Growth;

namespace AquaCulture.AI;

/// <summary>Discretised state for the feeding policy.</summary>
public readonly record struct FeedState(int DoBucket, int TempBucket, int Nh3Bucket);

/// <summary>Learned feeding decision.</summary>
public sealed record FeedDecision(double RationFactor, double ExpectedValue, Text2 Explanation);

/// <summary>
/// Reinforcement-learning feed optimisation: tabular Q-learning over (dawn DO, temperature, NH₃) buckets choosing a ration factor.
/// The reward is the value of biomass gained minus feed cost minus the value of animals lost, simulated with the AquaGrowth engine.
/// Training is deterministic for a given seed; it learns to cut rations when oxygen or ammonia are limiting and to feed fully otherwise.
/// For larger state spaces see Gravicode.RLNet.
/// </summary>
public sealed class FeedOptimizationPolicy
{
    public static readonly double[] Actions = [0, 0.5, 0.75, 1.0, 1.1];
    private readonly double[,,,] _q = new double[4, 3, 3, 5];

    public SpeciesProfile Species { get; }
    public double FeedPricePerKg { get; }
    public double FishPricePerKg { get; }
    public IReadOnlyList<double> EpisodeRewards { get; private set; } = [];

    public FeedOptimizationPolicy(SpeciesProfile species, double feedPricePerKg = 12_000, double? fishPricePerKg = null, int episodes = 3000, ulong seed = 31)
    {
        Species = species; FeedPricePerKg = feedPricePerKg; FishPricePerKg = fishPricePerKg ?? species.PricePerKg;
        Train(episodes, seed);
    }

    public FeedState Discretise(double dawnDo, double tempC, double nh3) => new(
        dawnDo < Species.DoCritical ? 0 : dawnDo < (Species.DoCritical + Species.DoOptimal) / 2 ? 1 : dawnDo < Species.DoOptimal ? 2 : 3,
        tempC < Species.Temperature.OptLow ? 0 : tempC <= Species.Temperature.OptHigh ? 1 : 2,
        nh3 <= Species.Nh3Max ? 0 : nh3 <= 2.5 * Species.Nh3Max ? 1 : 2);

    private (double Do, double T, double Nh3) Sample(FeedState s, DeterministicRandom r)
    {
        var sp = Species;
        double dox = s.DoBucket switch { 0 => r.Uniform(sp.DoCritical * 0.5, sp.DoCritical), 1 => r.Uniform(sp.DoCritical, (sp.DoCritical + sp.DoOptimal) / 2), 2 => r.Uniform((sp.DoCritical + sp.DoOptimal) / 2, sp.DoOptimal), _ => r.Uniform(sp.DoOptimal, sp.DoOptimal * 1.5) };
        double t = s.TempBucket switch { 0 => r.Uniform(sp.Temperature.Min + 1, sp.Temperature.OptLow), 1 => r.Uniform(sp.Temperature.OptLow, sp.Temperature.OptHigh), _ => r.Uniform(sp.Temperature.OptHigh, sp.Temperature.Max - 0.5) };
        double n = s.Nh3Bucket switch { 0 => r.Uniform(0, sp.Nh3Max), 1 => r.Uniform(sp.Nh3Max, 2.5 * sp.Nh3Max), _ => r.Uniform(2.5 * sp.Nh3Max, 5 * sp.Nh3Max) };
        return (dox, t, n);
    }

    /// <summary>One-day reward (currency per tonne of standing stock) for a ration factor under conditions.</summary>
    public double Reward(double ration, double dawnDo, double tempC, double nh3, double abwG)
    {
        var sim = new GrowthSimulator(Species);
        double count = 1_000_000 / abwG; // 1 t of stock
        // feeding when DO is low deepens the night oxygen sag (digestion raises demand ~ 40 %)
        double doEff = dawnDo - 0.8 * Math.Max(0, ration - 0.5) * (dawnDo < Species.DoOptimal ? 1 : 0.2);
        double nh3Eff = nh3 * (0.8 + 0.4 * ration);
        var env = new DailyEnvironment(tempC, doEff, nh3Eff, double.NaN, Math.Max(0.01, ration));
        var d = sim.Simulate(new StockState(Species, new DateOnly(2026, 1, 1), abwG, count, 400), 1, env).Final;
        double gainKg = (d.AbwG - abwG) * d.Survivors / 1000;
        double lostKg = (count - d.Survivors) * abwG / 1000 - count * sim.BaseMortality * abwG / 1000;
        double uneaten = ration > 1 ? (ration - 1) * d.FeedKg * 0.5 : 0;
        return gainKg * FishPricePerKg - (d.FeedKg + uneaten) * FeedPricePerKg - Math.Max(0, lostKg) * FishPricePerKg;
    }

    private void Train(int episodes, ulong seed)
    {
        var rng = new DeterministicRandom(seed);
        var rewards = new List<double>(episodes);
        for (int e = 0; e < episodes; e++)
        {
            var s = new FeedState(rng.Next(4), rng.Next(3), rng.Next(3));
            double eps = Math.Max(0.05, 1 - (double)e / (episodes * 0.7));
            int a = rng.NextDouble() < eps ? rng.Next(Actions.Length) : Best(s);
            var (dox, t, n) = Sample(s, rng);
            double abw = Species.MarketWeightG * rng.Uniform(0.05, 0.9);
            double r = Reward(Actions[a], dox, t, n, abw);
            double alpha = 0.1;
            _q[s.DoBucket, s.TempBucket, s.Nh3Bucket, a] += alpha * (r - _q[s.DoBucket, s.TempBucket, s.Nh3Bucket, a]);
            rewards.Add(r);
        }
        EpisodeRewards = rewards;
    }

    private int Best(FeedState s)
    {
        int best = 0;
        for (int a = 1; a < Actions.Length; a++) if (_q[s.DoBucket, s.TempBucket, s.Nh3Bucket, a] > _q[s.DoBucket, s.TempBucket, s.Nh3Bucket, best]) best = a;
        return best;
    }

    public FeedDecision Decide(double dawnDo, double tempC, double nh3)
    {
        var s = Discretise(dawnDo, tempC, nh3);
        int a = Best(s);
        double f = Actions[a];
        var why = f == 0 ? new Text2("Skip feeding: oxygen/ammonia make feeding lose money today", "Lewati pakan: oksigen/amonia membuat pakan merugi hari ini")
            : f < 1 ? new Text2($"Feed {f:P0} of the table: conditions limit growth", $"Beri {f:P0} dari tabel: kondisi membatasi pertumbuhan")
            : new Text2($"Feed {f:P0} of the table: conditions are good", $"Beri {f:P0} dari tabel: kondisi baik");
        return new FeedDecision(f, Math.Round(_q[s.DoBucket, s.TempBucket, s.Nh3Bucket, a]), why);
    }

    /// <summary>Policy table rows: DO bucket × temperature bucket × NH₃ bucket → ration.</summary>
    public IEnumerable<(FeedState State, double Ration)> Table()
    {
        for (int d = 0; d < 4; d++) for (int t = 0; t < 3; t++) for (int n = 0; n < 3; n++) { var s = new FeedState(d, t, n); yield return (s, Actions[Best(s)]); }
    }
}
