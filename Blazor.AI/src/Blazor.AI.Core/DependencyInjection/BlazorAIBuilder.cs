using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Blazor.AI.Core;

/// <summary>Fluent configuration returned by <c>services.AddBlazorAI()</c>.</summary>
public sealed class BlazorAIBuilder
{
    internal BlazorAIBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    /// <summary>Adds (or replaces) a provider. The first provider added becomes the default unless <see cref="UseDefault"/> is called.</summary>
    public BlazorAIBuilder AddProvider(AiProviderOptions provider)
    {
        Services.Configure<BlazorAIOptions>(o =>
        {
            o.Providers.RemoveAll(p => p.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase));
            o.Providers.Add(provider);
        });
        return this;
    }

    public BlazorAIBuilder AddSimulated(string name = "simulated") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Simulated, DisplayName = "Simulated (offline)" });

    public BlazorAIBuilder AddOpenAI(string apiKey, string model = "gpt-5-mini", string? embeddingModel = "text-embedding-3-small", string name = "openai") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.OpenAI, ApiKey = apiKey, Model = model, EmbeddingModel = embeddingModel });

    /// <param name="deployment">The Azure deployment name of the chat model.</param>
    public BlazorAIBuilder AddAzureOpenAI(string endpoint, string apiKey, string deployment, string? embeddingDeployment = null, string name = "azure") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.AzureOpenAI, Endpoint = endpoint, ApiKey = apiKey, Model = deployment, EmbeddingModel = embeddingDeployment });

    /// <param name="workspaceId">Required when the key is not scoped to a workspace (sent as <c>anthropic-workspace-id</c>).</param>
    public BlazorAIBuilder AddAnthropic(string apiKey, string model = "claude-sonnet-5-5", int maxOutputTokens = 4096, string name = "anthropic", string? workspaceId = null)
    {
        var provider = new AiProviderOptions { Name = name, Kind = AiProviderKind.Anthropic, ApiKey = apiKey, Model = model, MaxOutputTokens = maxOutputTokens };
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            provider.Settings["workspaceId"] = workspaceId;
        }

        return AddProvider(provider);
    }

    public BlazorAIBuilder AddGemini(string apiKey, string model = "gemini-2.5-flash", string? embeddingModel = "gemini-embedding-001", string name = "gemini") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Gemini, ApiKey = apiKey, Model = model, EmbeddingModel = embeddingModel });

    public BlazorAIBuilder AddMistral(string apiKey, string model = "mistral-small-latest", string? embeddingModel = "mistral-embed", string name = "mistral") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Mistral, ApiKey = apiKey, Model = model, EmbeddingModel = embeddingModel });

    public BlazorAIBuilder AddCohere(string apiKey, string model = "command-a-03-2025", string? embeddingModel = "embed-v4.0", string name = "cohere") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Cohere, ApiKey = apiKey, Model = model, EmbeddingModel = embeddingModel });

    public BlazorAIBuilder AddDeepSeek(string apiKey, string model = "deepseek-chat", string? endpoint = null, string name = "deepseek") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.DeepSeek, ApiKey = apiKey, Model = model, Endpoint = endpoint });

    public BlazorAIBuilder AddGrok(string apiKey, string model = "grok-4", string name = "grok") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Grok, ApiKey = apiKey, Model = model });

    public BlazorAIBuilder AddOllama(string model = "llama3.2", string? embeddingModel = "nomic-embed-text", string endpoint = "http://localhost:11434/v1/", string name = "ollama") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Ollama, Model = model, EmbeddingModel = embeddingModel, Endpoint = endpoint });

    public BlazorAIBuilder AddLlamaCpp(string endpoint = "http://localhost:8080/v1/", string model = "default", string name = "llamacpp") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.LlamaCpp, Model = model, Endpoint = endpoint });

    public BlazorAIBuilder AddLocalGen(string endpoint = "http://localhost:5000/v1/", string model = "local", string name = "localgen") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.LocalGen, Model = model, Endpoint = endpoint });

    public BlazorAIBuilder AddHuggingFace(string token, string model = "meta-llama/Llama-3.1-8B-Instruct", string name = "huggingface") =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.HuggingFace, ApiKey = token, Model = model });

    /// <summary>Any OpenAI-compatible endpoint (vLLM, LM Studio, LiteLLM, OpenRouter, Together...).</summary>
    public BlazorAIBuilder AddOpenAICompatible(string name, string endpoint, string model, string? apiKey = null, string? embeddingModel = null) =>
        AddProvider(new AiProviderOptions { Name = name, Kind = AiProviderKind.Custom, Endpoint = endpoint, Model = model, ApiKey = apiKey, EmbeddingModel = embeddingModel });

    /// <summary>Registers a client you built yourself (any <see cref="IChatClient"/>).</summary>
    public BlazorAIBuilder AddChatClient(string name, Func<IServiceProvider, IChatClient> chatClient, Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? embeddings = null, AiProviderKind kind = AiProviderKind.Custom)
    {
        Services.AddSingleton(new CustomClientRegistration(name, chatClient, embeddings, kind));
        return this;
    }

    public BlazorAIBuilder UseDefault(string provider)
    {
        Services.Configure<BlazorAIOptions>(o => o.DefaultProvider = provider);
        return this;
    }

    public BlazorAIBuilder UseGuardrails(Action<AiGuardrailOptions> configure)
    {
        Services.Configure<BlazorAIOptions>(o => configure(o.Guardrails));
        return this;
    }
}

/// <summary>A client registered in code with <see cref="BlazorAIBuilder.AddChatClient"/>.</summary>
public sealed record CustomClientRegistration(
    string Name,
    Func<IServiceProvider, IChatClient> ChatClient,
    Func<IServiceProvider, IEmbeddingGenerator<string, Embedding<float>>>? Embeddings,
    AiProviderKind Kind);
