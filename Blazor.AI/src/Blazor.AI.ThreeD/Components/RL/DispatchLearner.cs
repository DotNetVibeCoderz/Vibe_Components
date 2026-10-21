namespace Blazor.AI.ThreeD;

/// <summary>A demand zone: name, map position and mean demand per period (Poisson).</summary>
public sealed record DemandZone(string Name, double X, double Z, double[] MeanDemand);

/// <summary>A resource-allocation scenario: zones, units to place each period and period names.</summary>
public sealed record AllocationScenario(string Name, string Unit, string[] Periods, int Units, IReadOnlyList<DemandZone> Zones)
{
    /// <summary>Ambulances across Jakarta districts.</summary>
    public static AllocationScenario Ambulance() => new("Ambulance dispatch", "ambulance", ["Morning", "Midday", "Evening", "Night"], 10,
    [
        new("Jakarta Pusat", 0, 0, [3.2, 2.6, 3.0, 1.2]),
        new("Jakarta Utara", 0.2, -3, [1.6, 1.8, 2.8, 1.4]),
        new("Jakarta Barat", -3, 0.3, [2.0, 2.2, 2.4, 0.9]),
        new("Jakarta Selatan", 0.4, 3.1, [3.0, 2.0, 2.2, 1.0]),
        new("Jakarta Timur", 3.1, -0.2, [2.4, 2.8, 3.4, 1.6]),
    ]);

    /// <summary>Battery storage units across microgrids.</summary>
    public static AllocationScenario Energy() => new("Battery storage", "battery", ["Dawn", "Noon", "Peak", "Night"], 10,
    [
        new("Industrial park", -2.8, -1.5, [3.0, 3.6, 3.2, 1.4]),
        new("Harbour", 2.6, -2.4, [1.8, 2.2, 2.6, 2.0]),
        new("Campus", -1.0, 2.8, [1.0, 2.8, 2.4, 0.6]),
        new("Old town", 1.6, 1.4, [1.6, 1.8, 3.8, 1.2]),
        new("Data centre", 3.2, 2.2, [2.4, 2.4, 2.6, 2.6]),
    ]);

    /// <summary>Cloud servers across regions.</summary>
    public static AllocationScenario Servers() => new("Cloud servers", "server", ["APAC peak", "EU peak", "US peak", "Quiet"], 12,
    [
        new("Jakarta", 2.8, 1.8, [4.2, 1.6, 1.0, 1.2]),
        new("Singapore", 2.2, 3.0, [3.6, 1.4, 1.2, 1.0]),
        new("Frankfurt", -0.6, -2.6, [1.2, 4.0, 1.6, 0.8]),
        new("Virginia", -3.2, 0.2, [1.0, 1.8, 4.4, 1.0]),
        new("Tokyo", 3.4, -1.4, [3.0, 1.0, 1.4, 1.0]),
    ]);
}

/// <summary>Allocation quality for one policy over many simulated periods.</summary>
public sealed record AllocationScore(string Policy, double ServedShare, double Unmet, double Idle, int[][] Allocation);

/// <summary>
/// Sequential dispatch learned with tabular Q-learning: each period the agent places units one at a time; a unit earns
/// +1 if demand in its zone reaches it (diminishing returns), so the agent learns to spread units by expected demand.
/// State = (period, units placed per zone capped at 4).
/// </summary>
public sealed class DispatchLearner(AllocationScenario scenario, int seed = 21)
{
    private readonly Dictionary<long, double[]> _q = [];
    private readonly Random _rng = new(seed);

    public AllocationScenario Scenario { get; } = scenario;

    public double Epsilon { get; set; } = 1;

    public int Episodes { get; private set; }

    private int Zones => Scenario.Zones.Count;

    private long Key(int period, int[] counts)
    {
        long k = period;
        foreach (var c in counts)
        {
            k = k * 5 + Math.Min(4, c);
        }

        return k;
    }

    private double[] Q(long key)
    {
        if (!_q.TryGetValue(key, out var v))
        {
            v = new double[Zones];
            _q[key] = v;
        }

        return v;
    }

    public int Poisson(double mean)
    {
        var l = Math.Exp(-mean);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= _rng.NextDouble();
        }
        while (p > l);
        return k - 1;
    }

    /// <summary>Trains on one simulated day (every period). The caller schedules <see cref="Epsilon"/>.</summary>
    public void TrainEpisode()
    {
        for (var period = 0; period < Scenario.Periods.Length; period++)
        {
            var demand = Scenario.Zones.Select(z => Poisson(z.MeanDemand[period])).ToArray();
            var counts = new int[Zones];
            for (var u = 0; u < Scenario.Units; u++)
            {
                var key = Key(period, counts);
                var q = Q(key);
                var a = _rng.NextDouble() < Epsilon ? _rng.Next(Zones) : ArgMax(q);
                counts[a]++;
                var reward = counts[a] <= demand[a] ? 1.0 : -0.05;
                var last = u == Scenario.Units - 1;
                var next = last ? 0 : Q(Key(period, counts)).Max();
                q[a] += 0.15 * (reward + 0.95 * next - q[a]);
            }
        }

        Episodes++;
    }

    public int[] Allocate(int period)
    {
        var counts = new int[Zones];
        for (var u = 0; u < Scenario.Units; u++)
        {
            counts[ArgMax(Q(Key(period, counts)))]++;
        }

        return counts;
    }

    public static int[] EqualSplit(AllocationScenario s)
    {
        var counts = new int[s.Zones.Count];
        for (var u = 0; u < s.Units; u++)
        {
            counts[u % counts.Length]++;
        }

        return counts;
    }

    /// <summary>Allocates in proportion to the all-day average demand (a typical static plan).</summary>
    public static int[] StaticProportional(AllocationScenario s)
    {
        var mean = s.Zones.Select(z => z.MeanDemand.Average()).ToArray();
        var total = mean.Sum();
        var counts = mean.Select(m => (int)Math.Floor(m / total * s.Units)).ToArray();
        while (counts.Sum() < s.Units)
        {
            var best = Enumerable.Range(0, counts.Length).MaxBy(i => mean[i] / total * s.Units - counts[i]);
            counts[best]++;
        }

        return counts;
    }

    public AllocationScore Score(string name, Func<int, int[]> policy, int days = 400)
    {
        var rng = new Random(777);
        double served = 0, demanded = 0, idle = 0;
        var alloc = Enumerable.Range(0, Scenario.Periods.Length).Select(policy).ToArray();
        for (var d = 0; d < days; d++)
        {
            for (var p = 0; p < Scenario.Periods.Length; p++)
            {
                for (var z = 0; z < Zones; z++)
                {
                    var demand = PoissonWith(rng, Scenario.Zones[z].MeanDemand[p]);
                    var s = Math.Min(demand, alloc[p][z]);
                    served += s;
                    demanded += demand;
                    idle += alloc[p][z] - s;
                }
            }
        }

        var periods = days * Scenario.Periods.Length;
        return new AllocationScore(name, demanded == 0 ? 1 : served / demanded, (demanded - served) / periods, idle / periods, alloc);
    }

    private static int PoissonWith(Random rng, double mean)
    {
        var l = Math.Exp(-mean);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= rng.NextDouble();
        }
        while (p > l);
        return k - 1;
    }

    private static int ArgMax(double[] q)
    {
        var b = 0;
        for (var i = 1; i < q.Length; i++)
        {
            if (q[i] > q[b])
            {
                b = i;
            }
        }

        return b;
    }
}
