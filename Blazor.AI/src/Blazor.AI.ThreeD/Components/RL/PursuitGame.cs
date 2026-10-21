namespace Blazor.AI.ThreeD;

/// <summary>How a side in <see cref="PursuitGame"/> picks its moves.</summary>
public enum BattlePolicy
{
    /// <summary>Tabular Q-learning over the joint (hunter, runner) state.</summary>
    Learning,

    /// <summary>Moves towards (hunter) or away from (runner) the opponent, greedily.</summary>
    Heuristic,

    Random,
}

/// <summary>Outcome of one match.</summary>
public sealed record BattleMatch(int Index, bool Captured, int Steps, IReadOnlyList<((int X, int Y) Hunter, (int X, int Y) Runner)> Frames);

/// <summary>
/// Pursuit–evasion on a small grid: a hunter tries to catch a runner within a step limit. Each side is an independent
/// tabular Q-learner (or a heuristic/random opponent), so you can watch two learners co-adapt.
/// </summary>
public sealed class PursuitGame
{
    private static readonly (int Dx, int Dy)[] Moves = [(0, 0), (0, -1), (1, 0), (0, 1), (-1, 0)];
    private readonly Random _rng;
    private readonly double[,] _qh;
    private readonly double[,] _qr;

    public PursuitGame(int size = 7, int maxSteps = 28, int seed = 5)
    {
        Size = size;
        MaxSteps = maxSteps;
        _rng = new Random(seed);
        Walls = new bool[size * size];
        foreach (var (x, y) in new[] { (2, 2), (4, 2), (2, 4), (4, 4), (3, 1), (3, 5) })
        {
            if (x < size && y < size)
            {
                Walls[y * size + x] = true;
            }
        }

        var n = size * size;
        _qh = new double[n * n, 5];
        _qr = new double[n * n, 5];
    }

    public int Size { get; }

    public int MaxSteps { get; }

    public bool[] Walls { get; }

    public BattlePolicy Hunter { get; set; } = BattlePolicy.Learning;

    public BattlePolicy Runner { get; set; } = BattlePolicy.Learning;

    public double Epsilon { get; set; } = 0.3;

    public double Alpha { get; set; } = 0.3;

    public double Gamma { get; set; } = 0.92;

    public int Played { get; private set; }

    public bool IsWall(int x, int y) => x < 0 || y < 0 || x >= Size || y >= Size || Walls[y * Size + x];

    private int State((int X, int Y) h, (int X, int Y) r) => (h.Y * Size + h.X) * Size * Size + r.Y * Size + r.X;

    private (int X, int Y) Apply((int X, int Y) p, int a)
    {
        var (dx, dy) = Moves[a];
        return IsWall(p.X + dx, p.Y + dy) ? p : (p.X + dx, p.Y + dy);
    }

    private int Choose(BattlePolicy policy, double[,] q, int s, (int X, int Y) self, (int X, int Y) other, bool chase, double eps)
    {
        switch (policy)
        {
            case BattlePolicy.Random:
                return _rng.Next(5);
            case BattlePolicy.Heuristic:
                if (_rng.NextDouble() < 0.15)
                {
                    return _rng.Next(5);
                }

                var best = 0;
                var bestScore = double.NegativeInfinity;
                for (var a = 0; a < 5; a++)
                {
                    var p = Apply(self, a);
                    var d = Math.Abs(p.X - other.X) + Math.Abs(p.Y - other.Y);
                    var score = chase ? -d : d;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = a;
                    }
                }

                return best;
            default:
                if (_rng.NextDouble() < eps)
                {
                    return _rng.Next(5);
                }

                var b = 0;
                for (var a = 1; a < 5; a++)
                {
                    if (q[s, a] > q[s, b])
                    {
                        b = a;
                    }
                }

                return b;
        }
    }

    /// <summary>Plays one match; both learning sides update unless <paramref name="evaluate"/> is set.</summary>
    public BattleMatch Play(bool evaluate = false)
    {
        var h = (0, 0);
        var r = (Size - 1, Size - 1);
        var frames = new List<((int, int), (int, int))> { (h, r) };
        var eps = evaluate ? 0.05 : Epsilon;
        for (var step = 1; step <= MaxSteps; step++)
        {
            var s = State(h, r);
            var ah = Choose(Hunter, _qh, s, h, r, chase: true, eps);
            var ar = Choose(Runner, _qr, s, r, h, chase: false, eps);
            var h2 = Apply(h, ah);
            var r2 = Apply(r, ar);
            var caught = h2 == r2 || (h2 == r && r2 == h);
            var end = caught || step == MaxSteps;
            var dist = Math.Abs(h2.Item1 - r2.Item1) + Math.Abs(h2.Item2 - r2.Item2);
            var rh = caught ? 1.0 : -0.02 - dist * 0.002;
            var rr = caught ? -1.0 : step == MaxSteps ? 0.6 : 0.02;
            if (!evaluate)
            {
                var s2 = State(h2, r2);
                Learn(_qh, s, ah, rh, s2, end, Hunter);
                Learn(_qr, s, ar, rr, s2, end, Runner);
            }

            h = h2;
            r = r2;
            frames.Add((h, r));
            if (caught)
            {
                return Finish(true, step, frames, evaluate);
            }
        }

        return Finish(false, MaxSteps, frames, evaluate);
    }

    private BattleMatch Finish(bool caught, int steps, List<((int, int), (int, int))> frames, bool evaluate)
    {
        if (!evaluate)
        {
            Played++;
            Epsilon = Math.Max(0.05, Epsilon * 0.9993);
        }

        return new BattleMatch(Played, caught, steps, frames);
    }

    private void Learn(double[,] q, int s, int a, double reward, int s2, bool end, BattlePolicy policy)
    {
        if (policy != BattlePolicy.Learning)
        {
            return;
        }

        var next = 0.0;
        if (!end)
        {
            next = q[s2, 0];
            for (var b = 1; b < 5; b++)
            {
                next = Math.Max(next, q[s2, b]);
            }
        }

        q[s, a] += Alpha * (reward + Gamma * next - q[s, a]);
    }
}
