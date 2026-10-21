namespace Blazor.AI.Abstractions.Providers;

/// <summary>Configuration for a single named model provider.</summary>
/// <remarks>Bound from the <c>BlazorAI:Providers</c> configuration section or built in code.</remarks>
public sealed class AiProviderOptions
{
    /// <summary>Unique name used to resolve the provider, e.g. <c>"azure"</c> or <c>"local"</c>.</summary>
    public string Name { get; set; } = "default";

    public AiProviderKind Kind { get; set; } = AiProviderKind.Simulated;

    /// <summary>API key. Prefer user secrets, environment variables or <see cref="Security.IApiKeyVault"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Endpoint override. When empty the provider's well-known endpoint is used.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Default chat model id.</summary>
    public string? Model { get; set; }

    /// <summary>Embedding model id. When empty the offline hashing generator is used for embeddings.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Optional display label shown by provider pickers.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Maximum output tokens for providers that require it (Anthropic).</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Extra provider-specific settings.</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AiDeploymentMode Deployment => Kind switch
    {
        AiProviderKind.Simulated or AiProviderKind.Ollama or AiProviderKind.LocalGen
            or AiProviderKind.LlamaCpp or AiProviderKind.OnnxRuntime or AiProviderKind.LocalTransformers => AiDeploymentMode.Local,
        AiProviderKind.Custom => AiDeploymentMode.Hybrid,
        _ => AiDeploymentMode.Cloud,
    };

    public string Label => DisplayName ?? (Model is null ? Name : $"{Name} · {Model}");

    public AiProviderOptions Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        ApiKey = ApiKey,
        Endpoint = Endpoint,
        Model = Model,
        EmbeddingModel = EmbeddingModel,
        DisplayName = DisplayName,
        MaxOutputTokens = MaxOutputTokens,
        Settings = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase),
    };
}
