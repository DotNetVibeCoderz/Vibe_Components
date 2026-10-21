namespace Blazor.AI.Abstractions.Providers;

/// <summary>The model providers Blazor.AI can connect to out of the box.</summary>
public enum AiProviderKind
{
    /// <summary>Deterministic offline provider for demos, tests and design-time rendering.</summary>
    Simulated = 0,
    OpenAI,
    AzureOpenAI,
    Anthropic,
    Gemini,
    Mistral,
    Cohere,
    DeepSeek,
    Grok,
    Ollama,
    LocalGen,
    LlamaCpp,
    HuggingFace,
    OnnxRuntime,

    /// <summary>Hugging Face models run in-process with HF.Net (Blazor.AI.LocalModels).</summary>
    LocalTransformers,
    /// <summary>Any OpenAI-compatible endpoint, or a client registered in code.</summary>
    Custom,
}

/// <summary>Where a provider's inference runs. Used by the UI to badge local vs. cloud models.</summary>
public enum AiDeploymentMode
{
    Local,
    Hybrid,
    Cloud,
}
