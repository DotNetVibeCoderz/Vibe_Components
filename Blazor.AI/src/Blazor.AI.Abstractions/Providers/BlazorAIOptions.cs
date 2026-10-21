using Blazor.AI.Abstractions.Security;

namespace Blazor.AI.Abstractions.Providers;

/// <summary>Root options for Blazor.AI, bound from the <c>BlazorAI</c> configuration section.</summary>
public sealed class BlazorAIOptions
{
    public const string SectionName = "BlazorAI";

    /// <summary>Name of the provider used when a component does not ask for one.</summary>
    public string? DefaultProvider { get; set; }

    public List<AiProviderOptions> Providers { get; set; } = [];

    public AiGuardrailOptions Guardrails { get; set; } = new();

    /// <summary>Dimensions of the built-in offline hashing embedding generator.</summary>
    public int OfflineEmbeddingDimensions { get; set; } = 384;
}
