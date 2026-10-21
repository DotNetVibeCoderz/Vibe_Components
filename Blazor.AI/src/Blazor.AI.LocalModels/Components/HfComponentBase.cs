using Blazor.AI.Components.Infrastructure;
using Microsoft.AspNetCore.Components;

namespace Blazor.AI.LocalModels;

/// <summary>Base for Hugging Face components: engine choice (local vs hosted), model id and load status.</summary>
public abstract class HfComponentBase : AiComponentBase
{
    [Inject] protected LocalModelRegistry Registry { get; set; } = default!;

    [Inject] protected HuggingFaceInferenceClient HostedApi { get; set; } = default!;

    [Inject] protected LocalModelOptions ModelOptions { get; set; } = default!;

    /// <summary>Where inference runs. Local downloads the model once and runs it on this server's CPU.</summary>
    [Parameter] public InferenceEngine Engine { get; set; } = InferenceEngine.Local;

    /// <summary>Hugging Face model id; each component has a sensible default.</summary>
    [Parameter] public string? Model { get; set; }

    protected string? LoadStatus { get; set; }

    /// <summary>Loads (or reuses) a local model, showing a status message while the first download runs.</summary>
    protected async Task<T> EnsureLocalAsync<T>(string repoId, string kind, Func<string, Task<T>> load)
    {
        if (!Registry.IsLoaded(repoId, kind))
        {
            LoadStatus = $"Loading {repoId}… the first run downloads it from the Hugging Face Hub.";
            StateHasChanged();
        }

        try
        {
            return await load(repoId);
        }
        finally
        {
            LoadStatus = null;
        }
    }
}
