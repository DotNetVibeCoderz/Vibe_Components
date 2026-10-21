using RLNet.Environments;
using RLNet.Spaces;

namespace Blazor.AI.Analytics;

public enum GridTile
{
    Empty,
    Wall,
    Goal,
    Trap,
    Start,
}

/// <summary>Reward shaping for <see cref="DesignedGridWorld"/> (edited by RewardDesigner).</summary>
public sealed class GridRewards
{
    public float Step { get; set; } = -0.04f;

    public float Goal { get; set; } = 1f;

    public float Trap { get; set; } = -1f;

    public float WallBump { get; set; } = -0.1f;

    /// <summary>Probability that the agent slips to a random neighbouring direction (stochastic worlds).</summary>
    public double Slip { get; set; }

    public int MaxSteps { get; set; } = 100;
}

/// <summary>
/// A grid world whose layout and rewards are designed in the UI. Implements RLNet's <see cref="IDiscreteEnvironment"/>,
/// so any RLNet discrete agent (Q-table, DQN, PPO, A2C) can learn it. Observation: normalised (x, y).
/// </summary>
public sealed class DesignedGridWorld : IDiscreteEnvironment
{
    public static readonly string[] ActionNames = ["up", "right", "down", "left"];
    private static readonly (int Dx, int Dy)[] Moves = [(0, -1), (1, 0), (0, 1), (-1, 0)];

    private readonly float[] _observation = new float[2];
    private Random _random = new(1);

    public DesignedGridWorld(int width, int height, GridTile[] tiles, GridRewards rewards)
    {
        if (tiles.Length != width * height)
        {
            throw new ArgumentException("Tile count must equal width × height.", nameof(tiles));
        }

        Width = width;
        Height = height;
        Tiles = tiles;
        Rewards = rewards;
        var start = Array.IndexOf(tiles, GridTile.Start);
        StartIndex = start < 0 ? Array.FindIndex(tiles, t => t == GridTile.Empty) : start;
        ObservationSpace = BoxSpace.Uniform(2, 0, 1, ["x", "y"]);
        Reset();
    }

    public int Width { get; }

    public int Height { get; }

    public GridTile[] Tiles { get; }

    public GridRewards Rewards { get; }

    public int StartIndex { get; }

    public int AgentX { get; private set; }

    public int AgentY { get; private set; }

    public string Name => "Designed grid world";

    public Space ObservationSpace { get; }

    public DiscreteSpace ActionSpace { get; } = new(4, ActionNames);

    Space IEnvironment.ActionSpace => ActionSpace;

    public ReadOnlySpan<float> Observation => _observation;

    public int ElapsedSteps { get; private set; }

    public int MaxEpisodeSteps => Rewards.MaxSteps;

    public GridTile At(int x, int y) => Tiles[y * Width + x];

    /// <summary>The observation a cell produces (used to read Q-values for every cell).</summary>
    public float[] ObservationOf(int x, int y) => [Width == 1 ? 0 : x / (float)(Width - 1), Height == 1 ? 0 : y / (float)(Height - 1)];

    public long KeyOf(ReadOnlySpan<float> observation) =>
        (long)Math.Round(observation[1] * (Height - 1)) * Width + (long)Math.Round(observation[0] * (Width - 1));

    public void Reset(int? seed = null)
    {
        if (seed is { } s)
        {
            _random = new Random(s);
        }

        AgentX = StartIndex % Width;
        AgentY = StartIndex / Width;
        ElapsedSteps = 0;
        Update();
    }

    public StepResult Step(int action)
    {
        if (Rewards.Slip > 0 && _random.NextDouble() < Rewards.Slip)
        {
            action = (action + (_random.Next(2) == 0 ? 1 : 3)) % 4;
        }

        ElapsedSteps++;
        var (dx, dy) = Moves[action];
        int nx = AgentX + dx, ny = AgentY + dy;
        var reward = Rewards.Step;
        if (nx < 0 || ny < 0 || nx >= Width || ny >= Height || At(nx, ny) == GridTile.Wall)
        {
            reward += Rewards.WallBump;
        }
        else
        {
            AgentX = nx;
            AgentY = ny;
        }

        Update();
        var tile = At(AgentX, AgentY);
        var terminated = tile is GridTile.Goal or GridTile.Trap;
        if (tile == GridTile.Goal) reward = Rewards.Goal;
        if (tile == GridTile.Trap) reward = Rewards.Trap;
        return new StepResult(reward, terminated, !terminated && ElapsedSteps >= MaxEpisodeSteps);
    }

    private void Update()
    {
        var o = ObservationOf(AgentX, AgentY);
        _observation[0] = o[0];
        _observation[1] = o[1];
    }

    /// <summary>A 7×6 starter layout with walls, a trap and a goal.</summary>
    public static DesignedGridWorld Sample(GridRewards? rewards = null)
    {
        const string layout =
            "S..#..." +
            ".#.#.#." +
            ".#...#." +
            ".####T." +
            "......#" +
            "#.#...G";
        var tiles = layout.Select(c => c switch { '#' => GridTile.Wall, 'G' => GridTile.Goal, 'T' => GridTile.Trap, 'S' => GridTile.Start, _ => GridTile.Empty }).ToArray();
        return new DesignedGridWorld(7, 6, tiles, rewards ?? new GridRewards());
    }
}
