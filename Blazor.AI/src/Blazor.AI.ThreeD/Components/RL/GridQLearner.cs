using Blazor.AI.Analytics;

namespace Blazor.AI.ThreeD;

/// <summary>Tabular learning algorithm for <see cref="GridQLearner"/>.</summary>
public enum GridAlgorithm
{
    QLearning,
    Sarsa,
}

/// <summary>Result of one episode: return, steps, outcome and the cells visited (x, y).</summary>
public sealed record GridEpisode(int Episode, double Return, int Steps, bool ReachedGoal, bool HitTrap, double Epsilon, IReadOnlyList<(int X, int Y)> Path);

/// <summary>
/// A small, fast tabular learner (Q-learning or SARSA) over a <see cref="DesignedGridWorld"/>. Deterministic for a seed,
/// cheap enough to train hundreds of episodes per frame, and able to snapshot its Q-table for policy playback.
/// </summary>
public sealed class GridQLearner
{
    private static readonly (int Dx, int Dy)[] Moves = [(0, -1), (1, 0), (0, 1), (-1, 0)];
    private readonly Random _rng;

    public GridQLearner(DesignedGridWorld world, GridAlgorithm algorithm = GridAlgorithm.QLearning, double alpha = 0.5, double gamma = 0.95, int seed = 7)
    {
        World = world;
        Algorithm = algorithm;
        Alpha = alpha;
        Gamma = gamma;
        _rng = new Random(seed);
        Q = new double[world.Width * world.Height, 4];
        Visits = new int[world.Width * world.Height];
    }

    public DesignedGridWorld World { get; }

    public GridAlgorithm Algorithm { get; }

    public double Alpha { get; set; }

    public double Gamma { get; set; }

    public double Epsilon { get; set; } = 1.0;

    public double EpsilonMin { get; set; } = 0.05;

    public double EpsilonDecay { get; set; } = 0.985;

    public double[,] Q { get; }

    /// <summary>How often each cell was visited during training.</summary>
    public int[] Visits { get; }

    public int Episodes { get; private set; }

    public int State(int x, int y) => y * World.Width + x;

    public int Greedy(int state)
    {
        var best = 0;
        for (var a = 1; a < 4; a++)
        {
            if (Q[state, a] > Q[state, best])
            {
                best = a;
            }
        }

        return best;
    }

    public double Value(int state) => Math.Max(Math.Max(Q[state, 0], Q[state, 1]), Math.Max(Q[state, 2], Q[state, 3]));

    private int Choose(int state, double epsilon) => _rng.NextDouble() < epsilon ? _rng.Next(4) : Greedy(state);

    /// <summary>Runs one episode; learns unless <paramref name="greedy"/> is set.</summary>
    public GridEpisode RunEpisode(bool greedy = false)
    {
        var w = World;
        w.Reset(_rng.Next());
        var eps = greedy ? 0 : Epsilon;
        var s = State(w.AgentX, w.AgentY);
        var a = Choose(s, eps);
        var path = new List<(int, int)> { (w.AgentX, w.AgentY) };
        double ret = 0;
        var steps = 0;
        while (true)
        {
            var r = w.Step(a);
            steps++;
            ret += r.Reward;
            var s2 = State(w.AgentX, w.AgentY);
            path.Add((w.AgentX, w.AgentY));
            if (!greedy)
            {
                Visits[s2]++;
            }

            var done = r.Terminated || r.Truncated;
            var a2 = Choose(s2, eps);
            if (!greedy)
            {
                var next = r.Terminated ? 0 : Algorithm == GridAlgorithm.Sarsa ? Q[s2, a2] : Value(s2);
                Q[s, a] += Alpha * (r.Reward + Gamma * next - Q[s, a]);
            }

            if (done)
            {
                var tile = w.At(w.AgentX, w.AgentY);
                if (!greedy)
                {
                    Episodes++;
                    Epsilon = Math.Max(EpsilonMin, Epsilon * EpsilonDecay);
                }

                return new GridEpisode(Episodes, ret, steps, tile == GridTile.Goal, tile == GridTile.Trap, eps, path);
            }

            s = s2;
            a = a2;
        }
    }

    /// <summary>A copy of the Q-table for later playback.</summary>
    public double[,] Snapshot() => (double[,])Q.Clone();

    public static (int Dx, int Dy) Move(int action) => Moves[action];
}

/// <summary>Turns a grid world into 3D blocks: floor tiles, walls, goal and traps. One unit per cell, centred on the origin.</summary>
public static class GridScene
{
    public static double[] Cell(DesignedGridWorld w, int x, int y, double height = 0) => [x - (w.Width - 1) / 2.0, height, y - (w.Height - 1) / 2.0];

    /// <summary>Yaw (radians) that makes a +Z-facing model look along the move.</summary>
    public static double Yaw(int action) => action switch
    {
        0 => Math.PI,          // up (−Z)
        1 => Math.PI / 2,      // right (+X)
        3 => -Math.PI / 2,     // left (−X)
        _ => 0,                // down (+Z)
    };

    public static double YawBetween((int X, int Y) a, (int X, int Y) b) =>
        b.X > a.X ? Math.PI / 2 : b.X < a.X ? -Math.PI / 2 : b.Y < a.Y ? Math.PI : 0;

    public static List<Object3D> Build(DesignedGridWorld w, Func<int, int, string?>? floorColor = null)
    {
        var list = new List<Object3D>();
        for (var y = 0; y < w.Height; y++)
        {
            for (var x = 0; x < w.Width; x++)
            {
                var tile = w.At(x, y);
                var p = Cell(w, x, y);
                list.Add(new Object3D
                {
                    Id = $"floor-{x}-{y}", Kind = "box", Position = [p[0], -0.05, p[2]], Size = [0.96, 0.1, 0.96],
                    Color = floorColor?.Invoke(x, y) ?? ((x + y) % 2 == 0 ? "#1d2462" : "#18205a"), Roughness = 0.9, Pickable = false,
                });
                switch (tile)
                {
                    case GridTile.Wall:
                        list.Add(new Object3D { Id = $"wall-{x}-{y}", Kind = "box", Position = [p[0], 0.4, p[2]], Size = [0.96, 0.8, 0.96], Color = "#4a557f", Roughness = 0.7, Edges = "#8a96d6", Tooltip = "Wall" });
                        break;
                    case GridTile.Goal:
                        list.Add(new Object3D { Id = $"goal-{x}-{y}", Kind = "cylinder", Position = [p[0], 0.06, p[2]], Radius = 0.42, Height = 0.08, Color = ThreeDPalette.Nila, Emissive = ThreeDPalette.Nila, EmissiveIntensity = 0.8, Tooltip = "Goal (+reward)" });
                        list.Add(new Object3D { Id = $"goal-gem-{x}-{y}", Kind = "icosahedron", Position = [p[0], 0.55, p[2]], Radius = 0.18, Color = ThreeDPalette.Nila, Emissive = ThreeDPalette.Nila, EmissiveIntensity = 1.2, FlatShading = true, Animate = Animate3D.Spin(1.2), Pickable = false });
                        break;
                    case GridTile.Trap:
                        list.Add(new Object3D { Id = $"trap-{x}-{y}", Kind = "cone", Position = [p[0], 0.25, p[2]], Radius = 0.32, Height = 0.5, Segments = 4, Color = ThreeDPalette.Danger, Emissive = ThreeDPalette.Danger, EmissiveIntensity = 0.5, FlatShading = true, Tooltip = "Trap (−reward)" });
                        break;
                    case GridTile.Start:
                        list.Add(new Object3D { Id = $"start-{x}-{y}", Kind = "ring", Position = [p[0], 0.011, p[2]], Rotation = [-Math.PI / 2, 0, 0], InnerRadius = 0.3, Radius = 0.42, Color = ThreeDPalette.Soga, Emissive = ThreeDPalette.Soga, EmissiveIntensity = 0.6, Pickable = false });
                        break;
                }
            }
        }

        return list;
    }

    /// <summary>A 9×7 maze with two traps, used by the arenas.</summary>
    public static DesignedGridWorld Arena(GridRewards? rewards = null)
    {
        const string layout =
            "S...#...." +
            ".##.#.##." +
            ".#..T..#." +
            ".#.###.#." +
            "...#...#." +
            "##.#.#T.." +
            "....#...G";
        var tiles = layout.Select(c => c switch { '#' => GridTile.Wall, 'G' => GridTile.Goal, 'T' => GridTile.Trap, 'S' => GridTile.Start, _ => GridTile.Empty }).ToArray();
        return new DesignedGridWorld(9, 7, tiles, rewards ?? new GridRewards { MaxSteps = 120 });
    }
}
