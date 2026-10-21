using Microsoft.AspNetCore.Components;

namespace Blazor.AI.ThreeD;

/// <summary>
/// Base for digital twins: owns a <see cref="TwinSimulator"/>, steps it on a timer, and pushes scene patches.
/// The timer starts after the first render, can be paused, and stops when the component is disposed.
/// </summary>
public abstract class TwinComponentBase : ThreeDComponentBase
{
    private CancellationTokenSource? _loop;

    /// <summary>Simulation updates per second (clamped to 0.5–5).</summary>
    [Parameter] public double UpdatesPerSecond { get; set; } = 2;

    /// <summary>Start the live simulation automatically.</summary>
    [Parameter] public bool AutoStart { get; set; } = true;

    protected TwinSimulator Sim { get; set; } = default!;

    protected bool Running { get; private set; }

    /// <summary>Called once per tick after the simulator stepped; return object patches for the scene.</summary>
    protected abstract IEnumerable<ObjectPatch> OnTick();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && AutoStart)
        {
            Start();
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    protected void Start()
    {
        if (Running)
        {
            return;
        }

        Running = true;
        _loop = new CancellationTokenSource();
        _ = RunLoopAsync(_loop.Token);
    }

    protected void Pause()
    {
        Running = false;
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    protected void Toggle()
    {
        if (Running)
        {
            Pause();
        }
        else
        {
            Start();
        }
    }

    /// <summary>Advances the simulation once and pushes the result (also used for single-step buttons).</summary>
    protected async Task StepOnceAsync()
    {
        Sim.Step();
        var patches = OnTick().ToArray();
        if (Scene is not null && patches.Length > 0)
        {
            await Scene.UpdateAsync(patches, (int)(800 / Math.Clamp(UpdatesPerSecond, 0.5, 5)));
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / Math.Clamp(UpdatesPerSecond, 0.5, 5)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await InvokeAsync(async () =>
                {
                    await StepOnceAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override ValueTask DisposeAsync()
    {
        Pause();
        return base.DisposeAsync();
    }
}
