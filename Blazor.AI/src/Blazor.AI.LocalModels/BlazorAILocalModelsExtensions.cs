using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Core;
using Blazor.AI.LocalModels;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAILocalModelsExtensions
{
    /// <summary>Registers the Hugging Face hub, hosted API client and the shared local model registry.</summary>
    public static IServiceCollection AddBlazorAILocalModels(this IServiceCollection services, Action<LocalModelOptions>? configure = null)
    {
        var options = new LocalModelOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<LocalModelRegistry>();
        services.TryAddSingleton<HuggingFaceHubService>();
        services.AddHttpClient<HuggingFaceInferenceClient>(c => c.Timeout = TimeSpan.FromMinutes(2));
        return services;
    }

    /// <summary>
    /// Adds a fully local provider: chat with an HF.Net causal LM and embeddings with a sentence-transformer, both on CPU.
    /// Requires <see cref="AddBlazorAILocalModels"/>.
    /// </summary>
    public static BlazorAIBuilder AddLocalTransformers(this BlazorAIBuilder builder, string? chatModel = null, string? embeddingModel = null, string name = "local-hf") =>
        builder.AddChatClient(
            name,
            sp => new LocalChatClient(sp.GetRequiredService<LocalModelRegistry>(), chatModel),
            sp => new LocalEmbeddingGenerator(sp.GetRequiredService<LocalModelRegistry>(), embeddingModel),
            AiProviderKind.LocalTransformers);

    /// <summary>Adds ONNX Runtime embeddings (with the offline simulated chat) under a provider name.</summary>
    public static BlazorAIBuilder AddOnnxEmbeddings(this BlazorAIBuilder builder, string modelPath, string tokenizerPath, string name = "onnx") =>
        builder.AddChatClient(
            name,
            _ => new Blazor.AI.Core.Simulated.SimulatedChatClient(),
            _ => new OnnxEmbeddingGenerator(modelPath, tokenizerPath),
            AiProviderKind.OnnxRuntime);
}
