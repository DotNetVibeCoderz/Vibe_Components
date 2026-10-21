using System.ClientModel;
using Anthropic;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Core.Embeddings;
using Blazor.AI.Core.Simulated;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Blazor.AI.Core.Providers;

/// <summary>Builds the raw (unwrapped) client for a provider configuration.</summary>
internal static class ProviderClientBuilder
{
    public static IChatClient CreateChatClient(AiProviderOptions options)
    {
        var model = string.IsNullOrWhiteSpace(options.Model) ? ProviderDefaults.Model(options.Kind) : options.Model!;

        switch (options.Kind)
        {
            case AiProviderKind.Simulated:
                return new SimulatedChatClient(model);

            case AiProviderKind.Anthropic:
                return new CompatibilityChatClient(CreateAnthropicClient(options).AsIChatClient(model, options.MaxOutputTokens ?? 4096), model);

            case AiProviderKind.OnnxRuntime or AiProviderKind.LocalTransformers:
                throw new InvalidOperationException(
                    $"Provider '{options.Name}' runs a local model. Register it in code with builder.AddLocalTransformers(...) or builder.AddOnnxEmbeddings(...) from Blazor.AI.LocalModels.");

            default:
                return new CompatibilityChatClient(CreateOpenAIClient(options).GetChatClient(model).AsIChatClient(), model);
        }
    }

    private static AnthropicClient CreateAnthropicClient(AiProviderOptions options)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.Endpoint) ? "https://api.anthropic.com" : options.Endpoint!;

        // Keys that are not scoped to a workspace must name one with the anthropic-workspace-id header.
        if (options.Settings.TryGetValue("workspaceId", out var workspace) && !string.IsNullOrWhiteSpace(workspace))
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.Add("anthropic-workspace-id", workspace);
            return new AnthropicClient { ApiKey = Require(options), BaseUrl = baseUrl, HttpClient = http };
        }

        return new AnthropicClient { ApiKey = Require(options), BaseUrl = baseUrl };
    }

    public static IEmbeddingGenerator<string, Embedding<float>>? CreateEmbeddingGenerator(AiProviderOptions options, int offlineDimensions)
    {
        if (options.Kind == AiProviderKind.Simulated || string.IsNullOrWhiteSpace(options.EmbeddingModel))
        {
            return new HashingEmbeddingGenerator(offlineDimensions);
        }

        if (!ProviderDefaults.IsOpenAICompatible(options.Kind))
        {
            // Anthropic has no embeddings API; fall back to local hashing embeddings.
            return new HashingEmbeddingGenerator(offlineDimensions);
        }

        return CreateOpenAIClient(options).GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator();
    }

    private static OpenAIClient CreateOpenAIClient(AiProviderOptions options)
    {
        var endpoint = options.Kind switch
        {
            AiProviderKind.AzureOpenAI => ProviderDefaults.NormalizeAzureEndpoint(
                options.Endpoint ?? throw new InvalidOperationException($"Provider '{options.Name}' (Azure OpenAI) needs an Endpoint.")),
            AiProviderKind.Custom when string.IsNullOrWhiteSpace(options.Endpoint) =>
                throw new InvalidOperationException($"Provider '{options.Name}' (Custom) needs an OpenAI-compatible Endpoint."),
            _ => string.IsNullOrWhiteSpace(options.Endpoint) ? ProviderDefaults.Endpoint(options.Kind) : options.Endpoint,
        };

        var clientOptions = new OpenAIClientOptions();
        if (endpoint is not null)
        {
            clientOptions.Endpoint = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        }

        // Local servers (Ollama, llama.cpp, LocalGen) accept any non-empty key.
        var key = ProviderDefaults.RequiresApiKey(options.Kind) ? Require(options) : (options.ApiKey ?? "local");
        return new OpenAIClient(new ApiKeyCredential(key), clientOptions);
    }

    private static string Require(AiProviderOptions options) =>
        string.IsNullOrWhiteSpace(options.ApiKey)
            ? throw new InvalidOperationException($"Provider '{options.Name}' ({options.Kind}) needs an ApiKey. Set BlazorAI:Providers:[n]:ApiKey via user secrets or environment variables.")
            : options.ApiKey!;
}
