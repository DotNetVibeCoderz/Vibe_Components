namespace Blazor.AI.ThreeD;

/// <summary>Classic cart–pole balancing physics (Barto, Sutton &amp; Anderson), deterministic for a seed.</summary>
public sealed class CartPole
{
    private const double Gravity = 9.8, CartMass = 1.0, PoleMass = 0.1, Length = 0.5, Force = 10.0, Tau = 0.02;

    public double X { get; private set; }
    public double XDot { get; private set; }
    public double Theta { get; private set; }
    public double ThetaDot { get; private set; }

    public void Reset(Random rng)
    {
        X = (rng.NextDouble() - 0.5) * 0.1;
        XDot = (rng.NextDouble() - 0.5) * 0.1;
        Theta = (rng.NextDouble() - 0.5) * 0.1;
        ThetaDot = (rng.NextDouble() - 0.5) * 0.1;
    }

    /// <summary>Applies push left (0) or right (1); returns false when the pole falls or the cart leaves the track.</summary>
    public bool Step(int action)
    {
        var force = action == 1 ? Force : -Force;
        var cos = Math.Cos(Theta);
        var sin = Math.Sin(Theta);
        var total = CartMass + PoleMass;
        var temp = (force + PoleMass * Length * ThetaDot * ThetaDot * sin) / total;
        var thetaAcc = (Gravity * sin - cos * temp) / (Length * (4.0 / 3.0 - PoleMass * cos * cos / total));
        var xAcc = temp - PoleMass * Length * thetaAcc * cos / total;
        X += Tau * XDot;
        XDot += Tau * xAcc;
        Theta += Tau * ThetaDot;
        ThetaDot += Tau * thetaAcc;
        return Math.Abs(X) < 2.4 && Math.Abs(Theta) < 12 * Math.PI / 180;
    }
}

/// <summary>A linear policy (4 weights + bias) with its fitness and parent, so lineages can be traced.</summary>
public sealed record Genome(int Generation, int Index, double[] Weights, double Fitness, int ParentA, int ParentB, bool Elite);

/// <summary>
/// Neuro-evolution of linear cart–pole controllers: elitism, tournament selection, uniform crossover and Gaussian
/// mutation. Fitness is the mean number of steps balanced (max <see cref="MaxSteps"/>) over a few seeded trials.
/// </summary>
public sealed class PolicyEvolution(int population = 24, int seed = 17)
{
    private readonly Random _rng = new(seed);

    public int Population { get; } = population;

    public int MaxSteps { get; set; } = 400;

    public int Trials { get; set; } = 3;

    public double MutationScale { get; set; } = 0.35;

    public List<List<Genome>> Generations { get; } = [];

    public static int Act(double[] w, CartPole c) =>
        w[0] * c.X + w[1] * c.XDot + w[2] * c.Theta + w[3] * c.ThetaDot + w[4] > 0 ? 1 : 0;

    public double Evaluate(double[] w, int trialSeed = 1000)
    {
        var total = 0.0;
        for (var t = 0; t < Trials; t++)
        {
            var rng = new Random(trialSeed + t);
            var c = new CartPole();
            c.Reset(rng);
            var steps = 0;
            while (steps < MaxSteps && c.Step(Act(w, c)))
            {
                steps++;
            }

            total += steps;
        }

        return total / Trials;
    }

    /// <summary>Records a trajectory (x, θ) of the controller for playback.</summary>
    public List<(double X, double Theta)> Rollout(double[] w, int trialSeed = 1000, int max = 400)
    {
        var rng = new Random(trialSeed);
        var c = new CartPole();
        c.Reset(rng);
        var frames = new List<(double, double)> { (c.X, c.Theta) };
        for (var i = 0; i < max && c.Step(Act(w, c)); i++)
        {
            frames.Add((c.X, c.Theta));
        }

        return frames;
    }

    public List<Genome> Step()
    {
        var g = Generations.Count;
        List<Genome> next;
        if (g == 0)
        {
            next = Enumerable.Range(0, Population)
                .Select(i => Make(g, i, Enumerable.Range(0, 5).Select(_ => Gauss() * 0.5).ToArray(), -1, -1, false)).ToList();
        }
        else
        {
            var prev = Generations[^1].OrderByDescending(x => x.Fitness).ToList();
            next = [];
            for (var e = 0; e < 3; e++)
            {
                next.Add(Make(g, next.Count, (double[])prev[e].Weights.Clone(), prev[e].Index, prev[e].Index, true));
            }

            while (next.Count < Population)
            {
                var a = Tournament(prev);
                var b = Tournament(prev);
                var w = a.Weights.Select((v, k) => (_rng.NextDouble() < 0.5 ? v : b.Weights[k]) + Gauss() * MutationScale).ToArray();
                next.Add(Make(g, next.Count, w, a.Index, b.Index, false));
            }
        }

        Generations.Add(next);
        return next;
    }

    /// <summary>The champion of the last generation and its ancestors back to generation 0.</summary>
    public List<Genome> BestLineage()
    {
        if (Generations.Count == 0)
        {
            return [];
        }

        var line = new List<Genome>();
        var current = Generations[^1].MaxBy(x => x.Fitness)!;
        line.Add(current);
        for (var g = Generations.Count - 2; g >= 0 && current.ParentA >= 0; g--)
        {
            current = Generations[g][current.ParentA];
            line.Add(current);
        }

        line.Reverse();
        return line;
    }

    private Genome Make(int g, int i, double[] w, int pa, int pb, bool elite) => new(g, i, w, Evaluate(w), pa, pb, elite);

    private Genome Tournament(List<Genome> pool)
    {
        var best = pool[_rng.Next(pool.Count)];
        for (var k = 0; k < 2; k++)
        {
            var c = pool[_rng.Next(pool.Count)];
            if (c.Fitness > best.Fitness)
            {
                best = c;
            }
        }

        return best;
    }

    private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - _rng.NextDouble())) * Math.Cos(2 * Math.PI * _rng.NextDouble());
}
