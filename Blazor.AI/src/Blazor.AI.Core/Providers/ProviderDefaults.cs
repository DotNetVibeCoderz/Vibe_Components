using Blazor.AI.Abstractions.Providers;

namespace Blazor.AI.Core.Providers;

/// <summary>Well-known endpoints and default models per provider.</summary>
public static class ProviderDefaults
{
    public static string? Endpoint(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai/",
        AiProviderKind.Mistral => "https://api.mistral.ai/v1/",
        AiProviderKind.Cohere => "https://api.cohere.ai/compatibility/v1/",
        AiProviderKind.DeepSeek => "https://api.deepseek.com/v1/",
        AiProviderKind.Grok => "https://api.x.ai/v1/",
        AiProviderKind.Ollama => "http://localhost:11434/v1/",
        AiProviderKind.LocalGen => "http://localhost:5000/v1/",
        AiProviderKind.LlamaCpp => "http://localhost:8080/v1/",
        AiProviderKind.HuggingFace => "https://router.huggingface.co/v1/",
        _ => null,
    };

    public static string Model(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenAI => "gpt-5-mini",
        AiProviderKind.AzureOpenAI => "gpt-5-mini",
        AiProviderKind.Anthropic => "claude-sonnet-5-5",
        AiProviderKind.Gemini => "gemini-2.5-flash",
        AiProviderKind.Mistral => "mistral-small-latest",
        AiProviderKind.Cohere => "command-a-03-2025",
        AiProviderKind.DeepSeek => "deepseek-chat",
        AiProviderKind.Grok => "grok-4",
        AiProviderKind.Ollama => "llama3.2",
        AiProviderKind.HuggingFace => "meta-llama/Llama-3.1-8B-Instruct",
        AiProviderKind.Simulated => "blazorai-sim-1",
        _ => "default",
    };

    public static bool RequiresApiKey(AiProviderKind kind) => kind is AiProviderKind.OpenAI or AiProviderKind.AzureOpenAI
        or AiProviderKind.Anthropic or AiProviderKind.Gemini or AiProviderKind.Mistral or AiProviderKind.Cohere
        or AiProviderKind.DeepSeek or AiProviderKind.Grok or AiProviderKind.HuggingFace;

    /// <summary>True for providers reached through the OpenAI-compatible chat completions API.</summary>
    public static bool IsOpenAICompatible(AiProviderKind kind) => kind is not (AiProviderKind.Anthropic
        or AiProviderKind.Simulated or AiProviderKind.OnnxRuntime or AiProviderKind.LocalTransformers);

    /// <summary>Azure OpenAI resources expose the OpenAI v1 API under <c>/openai/v1/</c>.</summary>
    public static string NormalizeAzureEndpoint(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        var marker = trimmed.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            trimmed = trimmed[..marker];
        }

        return trimmed + "/openai/v1/";
    }
}
