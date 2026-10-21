namespace Blazor.AI.Analytics;

/// <summary>Shared RL state for a page: the designed grid world, its rewards and all training sessions.</summary>
public sealed class RlWorkspace
{
    public GridRewards Rewards { get; } = new();

    public DesignedGridWorld Grid { get; private set; }

    public List<TrainingSession> Sessions { get; } = [];

    public event Action? Changed;

    public RlWorkspace()
    {
        Grid = DesignedGridWorld.Sample(Rewards);
    }

    public void SetGrid(int width, int height, GridTile[] tiles)
    {
        Grid = new DesignedGridWorld(width, height, tiles, Rewards);
        Notify();
    }

    /// <summary>A fresh copy of the designed world (training must not share state with the editor).</summary>
    public DesignedGridWorld CreateGrid() => new(Grid.Width, Grid.Height, (GridTile[])Grid.Tiles.Clone(), Rewards);

    public void Add(TrainingSession session)
    {
        Sessions.Add(session);
        Notify();
    }

    public void Notify() => Changed?.Invoke();
}
