using Blazor.AI.Components.Infrastructure;
using Microsoft.AspNetCore.Components;

namespace Blazor.AI.Analytics;

/// <summary>Base for analytics components: binds to the workspace dataset (or an explicit one) and reacts to changes.</summary>
public abstract class AnalyticsComponentBase : AiComponentBase
{
    [Inject] protected AnalyticsWorkspace Workspace { get; set; } = default!;

    /// <summary>Use this dataset instead of the shared workspace dataset.</summary>
    [Parameter] public AnalyticsDataset? Dataset { get; set; }

    protected AnalyticsDataset Data => Dataset ?? Workspace.Dataset;

    protected override void OnInitialized()
    {
        Workspace.Changed += HandleChanged;
        OnDatasetChanged();
    }

    /// <summary>Called on first render and whenever the workspace dataset changes. Reset results here.</summary>
    protected virtual void OnDatasetChanged()
    {
    }

    /// <summary>Runs CPU-heavy work off the render thread (a no-op hop on WebAssembly).</summary>
    protected static Task<T> Compute<T>(Func<T> work, CancellationToken cancellationToken) => Task.Run(work, cancellationToken);

    private void HandleChanged() => InvokeAsync(() =>
    {
        if (Dataset is null)
        {
            OnDatasetChanged();
            StateHasChanged();
        }
    });

    public override ValueTask DisposeAsync()
    {
        Workspace.Changed -= HandleChanged;
        return base.DisposeAsync();
    }
}
