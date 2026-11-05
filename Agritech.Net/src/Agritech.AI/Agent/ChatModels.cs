using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Agritech.AI.Agent;

public enum ChatRole { System, User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatMessage(ChatRole Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed record ChatResponse(string? Content, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>Chat-completion model abstraction with function/tool calling.</summary>
public interface IChatModel
{
    string Name { get; }
    Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default);
}

public enum LlmProvider { OpenAI, AzureOpenAI, HuggingFace, Ollama, Custom }

/// <summary>Connection settings for any OpenAI-compatible Chat Completions endpoint.</summary>
public sealed record LlmOptions(LlmProvider Provider, string Endpoint, string? ApiKey, string Model, int MaxCompletionTokens = 4000)
{
    /// <summary>Full chat-completions URL for the provider.</summary>
    public string ChatCompletionsUrl => Provider switch
    {
        LlmProvider.AzureOpenAI => $"{Endpoint.TrimEnd('/')}/openai/v1/chat/completions",
        _ => Endpoint.TrimEnd('/').EndsWith("/chat/completions", StringComparison.Ordinal) ? Endpoint : $"{Endpoint.TrimEnd('/')}/chat/completions",
    };

    public static LlmOptions OpenAI(string apiKey, string model = "gpt-5-mini") => new(LlmProvider.OpenAI, "https://api.openai.com/v1", apiKey, model);
    /// <param name="endpoint">Resource endpoint, e.g. <c>https://my-resource.openai.azure.com/</c>.</param>
    /// <param name="deployment">Deployment (model) name.</param>
    public static LlmOptions AzureOpenAI(string endpoint, string apiKey, string deployment) => new(LlmProvider.AzureOpenAI, endpoint, apiKey, deployment);
    public static LlmOptions HuggingFace(string token, string model = "Qwen/Qwen2.5-72B-Instruct") => new(LlmProvider.HuggingFace, "https://router.huggingface.co/v1", token, model);
    public static LlmOptions Ollama(string model = "llama3.1", string endpoint = "http://localhost:11434/v1") => new(LlmProvider.Ollama, endpoint, null, model);

    /// <summary>
    /// Reads <c>AGRITECH_LLM_PROVIDER</c> (openai|azure|hf|ollama), <c>AGRITECH_LLM_ENDPOINT</c>, <c>AGRITECH_LLM_API_KEY</c>
    /// and <c>AGRITECH_LLM_MODEL</c>; falls back to <c>OPENAI_API_KEY</c> or <c>HF_TOKEN</c>. Returns null when nothing is configured.
    /// </summary>
    public static LlmOptions? FromEnvironment()
    {
        string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        var provider = Env("AGRITECH_LLM_PROVIDER")?.ToLowerInvariant();
        var key = Env("AGRITECH_LLM_API_KEY");
        var model = Env("AGRITECH_LLM_MODEL");
        var endpoint = Env("AGRITECH_LLM_ENDPOINT");
        switch (provider)
        {
            case "azure" when endpoint is not null && key is not null: return AzureOpenAI(endpoint, key, model ?? "gpt-5-mini");
            case "openai" when key is not null: return OpenAI(key, model ?? "gpt-5-mini") with { Endpoint = endpoint ?? "https://api.openai.com/v1" };
            case "hf" or "huggingface" when (key ?? Env("HF_TOKEN")) is { } hf: return HuggingFace(hf, model ?? "Qwen/Qwen2.5-72B-Instruct");
            case "ollama": return Ollama(model ?? "llama3.1", endpoint ?? "http://localhost:11434/v1");
            case "custom" when endpoint is not null: return new LlmOptions(LlmProvider.Custom, endpoint, key, model ?? "default");
        }
        if (Env("OPENAI_API_KEY") is { } ok) return OpenAI(ok, model ?? "gpt-5-mini");
        if (Env("HF_TOKEN") is { } token) return HuggingFace(token, model ?? "Qwen/Qwen2.5-72B-Instruct");
        return null;
    }
}

/// <summary>OpenAI-compatible Chat Completions client (OpenAI, Azure OpenAI v1, Hugging Face router, Ollama, vLLM, LM Studio…).</summary>
public sealed class OpenAiCompatibleChatModel(LlmOptions options, HttpClient? http = null) : IChatModel
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

    public string Name => $"{options.Provider}:{options.Model}";

    public async Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)ToJson(m)).ToArray()),
        };
        if (options.Provider is LlmProvider.OpenAI or LlmProvider.AzureOpenAI) body["max_completion_tokens"] = options.MaxCompletionTokens;
        else body["max_tokens"] = Math.Min(options.MaxCompletionTokens, 2048);
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.ParametersSchema) },
            }).ToArray());
            body["tool_choice"] = "auto";
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, options.ChatCompletionsUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (options.ApiKey is not null)
        {
            if (options.Provider == LlmProvider.AzureOpenAI) req.Headers.Add("api-key", options.ApiKey);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM request failed ({(int)resp.StatusCode}): {Truncate(text, 400)}");
        var msg = JsonNode.Parse(text)?["choices"]?[0]?["message"] ?? throw new InvalidOperationException("Malformed chat completion response.");
        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray tc)
            foreach (var c in tc)
                calls.Add(new ToolCall(c!["id"]!.GetValue<string>(), c["function"]!["name"]!.GetValue<string>(), c["function"]!["arguments"]?.GetValue<string>() ?? "{}"));
        return new ChatResponse(msg["content"]?.GetValue<string>(), calls);
    }

    private static JsonObject ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant() };
        o["content"] = m.Content;
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>
/// Offline, deterministic "model": routes the question to tools by bilingual keyword intent and returns the tool output.
/// Lets the agent work without any API key (edge devices, tests, demos).
/// </summary>
public sealed class RuleBasedChatModel : IChatModel
{
    private static readonly (string Tool, string[] Keywords)[] Intents =
    [
        ("fertilizer_plan", ["pupuk", "dosis", "fertili", "urea", "npk", "nitrogen", "pemupukan", "kcl", "sp36", "sp-36"]),
        ("irrigation_advice", ["irigasi", "siram", "air", "irrigat", "water", "kelembapan tanah", "pengairan"]),
        ("disease_risk", ["penyakit", "jamur", "blas", "disease", "fung", "blight", "busuk", "bercak"]),
        ("pest_risk", ["hama", "wereng", "ulat", "pest", "insect", "armyworm", "serangga"]),
        ("weather_forecast", ["cuaca", "hujan", "weather", "forecast", "rain", "prakiraan", "suhu", "temperature"]),
        ("yield_estimate", ["hasil", "panen", "yield", "harvest", "produksi", "ton"]),
        ("soil_health", ["tanah", "soil", "ph", "kesuburan", "organic", "organik"]),
        ("crop_recommendation", ["tanaman apa", "rekomendasi tanaman", "cocok", "which crop", "recommend crop", "suitable"]),
    ];

    public string Name => "rule-based (offline)";

    public Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        var last = messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            var toolOutputs = messages.SkipWhile(m => m.Role != ChatRole.Tool).Where(m => m.Role == ChatRole.Tool).Select(m => m.Content);
            return Task.FromResult(new ChatResponse(string.Join("\n\n", toolOutputs), []));
        }
        string q = (last.Content ?? "").ToLowerInvariant();
        var hits = Intents.Where(i => i.Keywords.Any(q.Contains)).Select(i => i.Tool)
            .Where(t => tools.Any(x => x.Name == t)).Distinct().Take(2).ToList();
        // "crop recommendation" outranks generic "soil" mentions
        if (hits.Contains("crop_recommendation")) hits = ["crop_recommendation"];
        if (hits.Count == 0) hits = ["farm_overview"];
        return Task.FromResult(new ChatResponse(null, hits.Select((h, i) => new ToolCall($"call_{i}", h, "{}")).ToList()));
    }
}
