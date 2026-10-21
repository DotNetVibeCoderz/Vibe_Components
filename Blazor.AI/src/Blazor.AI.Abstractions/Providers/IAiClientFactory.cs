using Microsoft.Extensions.AI;

namespace Blazor.AI.Abstractions.Providers;

/// <summary>Resolves chat clients and embedding generators by provider name.</summary>
/// <remarks>
/// Every Blazor.AI component talks to models through this factory, so swapping OpenAI for Ollama,
/// Anthropic or a local model is a configuration change, not a code change.
/// </remarks>
public interface IAiClientFactory
{
    /// <summary>The registered providers, in registration order.</summary>
    IReadOnlyList<AiProviderOptions> Providers { get; }

    /// <summary>The provider used when <c>null</c> is passed.</summary>
    AiProviderOptions DefaultProvider { get; }

    /// <summary>Gets a cached, thread-safe chat client. <c>null</c> means the default provider.</summary>
    IChatClient GetChatClient(string? provider = null);

    /// <summary>Gets an embedding generator. Falls back to the offline hashing generator when the provider has no embedding model.</summary>
    IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(string? provider = null);

    /// <summary>Registers or replaces a provider at runtime (for example from a settings page).</summary>
    void Register(AiProviderOptions options);

    /// <summary>Registers a client instance built in code under a name.</summary>
    void Register(string name, IChatClient chatClient, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null, AiProviderKind kind = AiProviderKind.Custom);

    /// <summary>Changes the default provider.</summary>
    void SetDefault(string provider);

    /// <summary>Raised after providers change, so pickers can refresh.</summary>
    event Action? ProvidersChanged;
}
