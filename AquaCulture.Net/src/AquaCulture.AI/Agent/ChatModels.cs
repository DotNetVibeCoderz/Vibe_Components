using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AquaCulture.AI.Agent;

public enum ChatRole { System, User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatMessage(ChatRole Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed record ChatResponse(string? Content, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>A function the model may call.</summary>
public sealed record AgentTool(string Name, string Description, string ParametersSchema, Func<JsonObject, string> Invoke);

/// <summary>Chat model with tool calling.</summary>
public interface IChatModel
{
    string Name { get; }
    Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default);
}

public enum LlmProvider { OpenAI, AzureOpenAI, Anthropic, DeepSeek, HuggingFace, Ollama, Custom }

/// <summary>LLM connection settings.</summary>
public sealed record LlmOptions(LlmProvider Provider, string Endpoint, string? ApiKey, string Model, int MaxTokens = 4000)
{
    /// <summary>Anthropic workspace id for organisation keys that are not workspace-scoped (<c>AQUA_LLM_WORKSPACE</c>).</summary>
    public string? WorkspaceId { get; init; }

    public string ChatCompletionsUrl => Provider switch
    {
        LlmProvider.AzureOpenAI => Endpoint.Contains("/chat/completions", StringComparison.Ordinal) ? Endpoint : $"{Endpoint.TrimEnd('/')}/openai/v1/chat/completions",
        LlmProvider.Anthropic => $"{Endpoint.TrimEnd('/')}/v1/messages",
        _ => Endpoint.TrimEnd('/').EndsWith("/chat/completions", StringComparison.Ordinal) ? Endpoint : $"{Endpoint.TrimEnd('/')}/chat/completions",
    };

    public static LlmOptions OpenAI(string apiKey, string model = "gpt-5-mini") => new(LlmProvider.OpenAI, "https://api.openai.com/v1", apiKey, model);
    public static LlmOptions AzureOpenAI(string endpoint, string apiKey, string deployment) => new(LlmProvider.AzureOpenAI, endpoint, apiKey, deployment);
    public static LlmOptions Anthropic(string apiKey, string model = "claude-sonnet-5-5") => new(LlmProvider.Anthropic, "https://api.anthropic.com", apiKey, model);
    public static LlmOptions DeepSeek(string apiKey, string model = "deepseek-chat") => new(LlmProvider.DeepSeek, "https://api.deepseek.com/v1", apiKey, model);
    public static LlmOptions HuggingFace(string token, string model = "Qwen/Qwen2.5-72B-Instruct") => new(LlmProvider.HuggingFace, "https://router.huggingface.co/v1", token, model);
    public static LlmOptions Ollama(string model = "llama3.1", string endpoint = "http://localhost:11434/v1") => new(LlmProvider.Ollama, endpoint, null, model);

    /// <summary>
    /// Reads <c>AQUA_LLM_PROVIDER</c> (openai | azure | anthropic | deepseek | hf | ollama | custom), <c>AQUA_LLM_ENDPOINT</c>,
    /// <c>AQUA_LLM_API_KEY</c>, <c>AQUA_LLM_MODEL</c> and (Anthropic) <c>AQUA_LLM_WORKSPACE</c>; falls back to <c>OPENAI_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>,
    /// <c>DEEPSEEK_API_KEY</c> or <c>HF_TOKEN</c>. Returns null when nothing is configured (the agent then runs offline).
    /// </summary>
    public static LlmOptions? FromEnvironment()
    {
        static string? Env(string k) => System.Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        var provider = Env("AQUA_LLM_PROVIDER")?.ToLowerInvariant();
        var key = Env("AQUA_LLM_API_KEY");
        var model = Env("AQUA_LLM_MODEL");
        var endpoint = Env("AQUA_LLM_ENDPOINT");
        switch (provider)
        {
            case "azure" when endpoint is not null && key is not null: return AzureOpenAI(endpoint, key, model ?? "gpt-5-mini");
            case "openai" when key is not null: return OpenAI(key, model ?? "gpt-5-mini") with { Endpoint = endpoint ?? "https://api.openai.com/v1" };
            case "anthropic" or "claude" when (key ?? Env("ANTHROPIC_API_KEY")) is { } ak: return Anthropic(ak, model ?? "claude-sonnet-5-5") with { Endpoint = endpoint ?? "https://api.anthropic.com", WorkspaceId = Env("AQUA_LLM_WORKSPACE") };
            case "deepseek" when (key ?? Env("DEEPSEEK_API_KEY")) is { } dk: return DeepSeek(dk, model ?? "deepseek-chat") with { Endpoint = endpoint ?? "https://api.deepseek.com/v1" };
            case "hf" or "huggingface" when (key ?? Env("HF_TOKEN")) is { } hf: return HuggingFace(hf, model ?? "Qwen/Qwen2.5-72B-Instruct");
            case "ollama": return Ollama(model ?? "llama3.1", endpoint ?? "http://localhost:11434/v1");
            case "custom" when endpoint is not null: return new LlmOptions(LlmProvider.Custom, endpoint, key, model ?? "default");
        }
        if (Env("OPENAI_API_KEY") is { } ok) return OpenAI(ok, model ?? "gpt-5-mini");
        if (Env("ANTHROPIC_API_KEY") is { } an) return Anthropic(an, model ?? "claude-sonnet-5-5") with { WorkspaceId = Env("AQUA_LLM_WORKSPACE") };
        if (Env("DEEPSEEK_API_KEY") is { } ds) return DeepSeek(ds, model ?? "deepseek-chat");
        if (Env("HF_TOKEN") is { } token) return HuggingFace(token, model ?? "Qwen/Qwen2.5-72B-Instruct");
        return null;
    }

    /// <summary>Creates the matching chat model.</summary>
    public IChatModel CreateModel(HttpClient? http = null) => Provider == LlmProvider.Anthropic ? new AnthropicChatModel(this, http) : new OpenAiCompatibleChatModel(this, http);
}

/// <summary>OpenAI-compatible Chat Completions (OpenAI, Azure OpenAI v1, DeepSeek, Hugging Face router, Ollama, vLLM, LM Studio).</summary>
public sealed class OpenAiCompatibleChatModel(LlmOptions options, HttpClient? http = null) : IChatModel
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    public string Name => $"{options.Provider}:{options.Model}";

    public async Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = options.Model, ["messages"] = new JsonArray(messages.Select(m => (JsonNode)ToJson(m)).ToArray()) };
        if (options.Provider is LlmProvider.OpenAI or LlmProvider.AzureOpenAI) body["max_completion_tokens"] = options.MaxTokens;
        else body["max_tokens"] = Math.Min(options.MaxTokens, 2048);
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.ParametersSchema) },
            }).ToArray());
            body["tool_choice"] = "auto";
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, options.ChatCompletionsUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (options.ApiKey is not null)
        {
            if (options.Provider == LlmProvider.AzureOpenAI) req.Headers.Add("api-key", options.ApiKey);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"LLM request failed ({(int)resp.StatusCode}): {Truncate(text, 400)}");
        var msg = JsonNode.Parse(text)?["choices"]?[0]?["message"] ?? throw new InvalidOperationException("Malformed chat completion response.");
        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray tc)
            foreach (var c in tc)
                calls.Add(new ToolCall(c!["id"]!.GetValue<string>(), c["function"]!["name"]!.GetValue<string>(), c["function"]!["arguments"]?.GetValue<string>() ?? "{}"));
        return new ChatResponse(msg["content"]?.GetValue<string>(), calls);
    }

    private static JsonObject ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    internal static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Anthropic Messages API (Claude) with tool use.</summary>
public sealed class AnthropicChatModel(LlmOptions options, HttpClient? http = null) : IChatModel
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    public string Name => $"Anthropic:{options.Model}";

    public async Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        string? system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var msgs = new JsonArray();
        foreach (var m in messages.Where(m => m.Role != ChatRole.System))
        {
            if (m.Role == ChatRole.Tool)
            {
                var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content ?? "" };
                // consecutive tool results go in one user turn
                if (msgs.Count > 0 && msgs[^1]!["role"]!.GetValue<string>() == "user" && msgs[^1]!["content"] is JsonArray arr && arr.Count > 0 && arr[0]!["type"]?.GetValue<string>() == "tool_result")
                    arr.Add((JsonNode)block);
                else msgs.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
                continue;
            }
            var content = new JsonArray();
            if (!string.IsNullOrEmpty(m.Content)) content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = m.Content });
            foreach (var c in m.ToolCalls ?? [])
                content.Add((JsonNode)new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) });
            msgs.Add((JsonNode)new JsonObject { ["role"] = m.Role == ChatRole.Assistant ? "assistant" : "user", ["content"] = content });
        }
        var body = new JsonObject { ["model"] = options.Model, ["max_tokens"] = options.MaxTokens, ["messages"] = msgs };
        if (!string.IsNullOrWhiteSpace(system)) body["system"] = system;
        if (tools.Count > 0)
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonNode.Parse(t.ParametersSchema) }).ToArray());
        using var req = new HttpRequestMessage(HttpMethod.Post, options.ChatCompletionsUrl) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", options.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        if (options.WorkspaceId is { Length: > 0 } ws) req.Headers.Add("anthropic-workspace-id", ws);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Anthropic request failed ({(int)resp.StatusCode}): {OpenAiCompatibleChatModel.Truncate(text, 400)}");
        var root = JsonNode.Parse(text)!;
        var sb = new StringBuilder(); var calls = new List<ToolCall>();
        foreach (var b in root["content"]!.AsArray())
        {
            string type = b!["type"]!.GetValue<string>();
            if (type == "text") sb.Append(b["text"]!.GetValue<string>());
            else if (type == "tool_use") calls.Add(new ToolCall(b["id"]!.GetValue<string>(), b["name"]!.GetValue<string>(), b["input"]?.ToJsonString() ?? "{}"));
        }
        return new ChatResponse(sb.Length == 0 ? null : sb.ToString(), calls);
    }
}

/// <summary>
/// Offline deterministic "model": routes the question to tools by bilingual keywords and returns the tool output.
/// Lets the agent work on edge devices and in tests with no API key.
/// </summary>
public sealed class RuleBasedChatModel : IChatModel
{
    private static readonly (string Tool, string[] Keywords)[] Intents =
    [
        ("feeding_plan", ["pakan", "ransum", "feed", "ration", "fcr", "pemberian makan", "berapa kg"]),
        ("water_quality_check", ["kualitas air", "water quality", "amonia", "ammonia", "ph", "salinitas", "salinity", "nitrit", "nitrite", "air kolam", "wqi"]),
        ("do_forecast", ["oksigen", "oxygen", "do ", "aerator", "kincir", "subuh", "dawn", "malam ini", "tonight"]),
        ("disease_risk", ["penyakit", "disease", "wssv", "ems", "ahpnd", "ehp", "vibrio", "bintik putih", "white spot", "risiko"]),
        ("growth_projection", ["pertumbuhan", "growth", "tumbuh", "adg", "berat", "weight", "size", "ukuran"]),
        ("harvest_forecast", ["panen", "harvest", "kapan", "when", "biomassa", "biomass", "yield", "hasil"]),
        ("weather_impact", ["cuaca", "hujan", "weather", "rain", "panas", "heat", "angin", "storm"]),
        ("economics", ["untung", "profit", "biaya", "cost", "harga", "price", "margin", "impas", "break-even"]),
    ];

    public string Name => "rule-based (offline)";

    public Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        var last = messages[^1];
        if (last.Role == ChatRole.Tool)
        {
            int lastUser = messages.ToList().FindLastIndex(m => m.Role == ChatRole.User);
            var outputs = messages.Skip(lastUser + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.Content);
            return Task.FromResult(new ChatResponse(string.Join("\n\n", outputs), []));
        }
        string q = " " + (last.Content ?? "").ToLowerInvariant() + " ";
        var hits = Intents.Where(i => i.Keywords.Any(q.Contains)).Select(i => i.Tool).Where(t => tools.Any(x => x.Name == t)).Distinct().Take(2).ToList();
        if (hits.Count == 0) hits = ["pond_overview"];
        return Task.FromResult(new ChatResponse(null, hits.Select((h, i) => new ToolCall($"call_{i}", h, "{}")).ToList()));
    }
}
