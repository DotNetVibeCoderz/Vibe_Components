using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Electrical.AI;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A tool invocation requested by the model; <paramref name="ArgumentsJson"/> is a JSON object.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatMessage(ChatRole Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed record ChatResponse(string? Content, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>A callable tool exposed to the language model. <paramref name="ParametersSchema"/> is a JSON Schema object.</summary>
public sealed record AgentTool(string Name, string Description, string ParametersSchema, Func<JsonObject, CancellationToken, Task<string>> Invoke);

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
    public const string DefaultOpenAIModel = "gpt-5-mini";
    public const string DefaultHuggingFaceModel = "Qwen/Qwen2.5-72B-Instruct";

    /// <summary>Full chat-completions URL for the provider.</summary>
    public string ChatCompletionsUrl => Provider switch
    {
        LlmProvider.AzureOpenAI => $"{Endpoint.TrimEnd('/')}/openai/v1/chat/completions",
        _ => Endpoint.TrimEnd('/').EndsWith("/chat/completions", StringComparison.Ordinal) ? Endpoint : $"{Endpoint.TrimEnd('/')}/chat/completions",
    };

    public static LlmOptions OpenAI(string apiKey, string model = DefaultOpenAIModel) => new(LlmProvider.OpenAI, "https://api.openai.com/v1", apiKey, model);
    /// <param name="endpoint">Resource endpoint, e.g. <c>https://my-resource.openai.azure.com/</c> (v1 API).</param>
    /// <param name="deployment">Deployment (model) name.</param>
    public static LlmOptions AzureOpenAI(string endpoint, string apiKey, string deployment) => new(LlmProvider.AzureOpenAI, endpoint, apiKey, deployment);
    /// <summary>Hugging Face Inference Providers router (OpenAI-compatible).</summary>
    public static LlmOptions HuggingFace(string token, string model = DefaultHuggingFaceModel) => new(LlmProvider.HuggingFace, "https://router.huggingface.co/v1", token, model);
    public static LlmOptions Ollama(string model = "llama3.1", string endpoint = "http://localhost:11434/v1") => new(LlmProvider.Ollama, endpoint, null, model);

    /// <summary>
    /// Reads <c>ELECTRICAL_LLM_PROVIDER</c> (openai | azure | hf | ollama | custom), <c>ELECTRICAL_LLM_ENDPOINT</c>, <c>ELECTRICAL_LLM_API_KEY</c>
    /// and <c>ELECTRICAL_LLM_MODEL</c>; without a provider falls back to <c>OPENAI_API_KEY</c> or <c>HF_TOKEN</c>.
    /// Returns null when nothing is configured (the agent then uses <see cref="RuleBasedChatModel"/>).
    /// </summary>
    public static LlmOptions? FromEnvironment() => FromVariables(Environment.GetEnvironmentVariable);

    /// <summary>Same as <see cref="FromEnvironment"/> with a custom variable source (testable).</summary>
    public static LlmOptions? FromVariables(Func<string, string?> getVariable)
    {
        string? Env(string k) => getVariable(k) is { Length: > 0 } v ? v.Trim() : null;
        var provider = Env("ELECTRICAL_LLM_PROVIDER")?.ToLowerInvariant();
        var key = Env("ELECTRICAL_LLM_API_KEY");
        var model = Env("ELECTRICAL_LLM_MODEL");
        var endpoint = Env("ELECTRICAL_LLM_ENDPOINT");
        switch (provider)
        {
            case "azure" or "azureopenai" when endpoint is not null && key is not null: return AzureOpenAI(endpoint, key, model ?? DefaultOpenAIModel);
            case "openai" when (key ?? Env("OPENAI_API_KEY")) is { } ok: return OpenAI(ok, model ?? DefaultOpenAIModel) with { Endpoint = endpoint ?? "https://api.openai.com/v1" };
            case "hf" or "huggingface" when (key ?? Env("HF_TOKEN")) is { } hf: return HuggingFace(hf, model ?? DefaultHuggingFaceModel) with { Endpoint = endpoint ?? "https://router.huggingface.co/v1" };
            case "ollama": return Ollama(model ?? "llama3.1", endpoint ?? "http://localhost:11434/v1");
            case "custom" when endpoint is not null: return new LlmOptions(LlmProvider.Custom, endpoint, key, model ?? "default");
        }
        if (Env("OPENAI_API_KEY") is { } o) return OpenAI(o, model ?? DefaultOpenAIModel);
        if (Env("HF_TOKEN") is { } t) return HuggingFace(t, model ?? DefaultHuggingFaceModel);
        return null;
    }

    /// <summary>Redacted description (never contains the key).</summary>
    public override string ToString() => $"{Provider} {Model} @ {Endpoint}{(ApiKey is null ? "" : " (key set)")}";
}

/// <summary>
/// OpenAI-compatible Chat Completions client with tool calling: OpenAI, Azure OpenAI (v1 API, <c>api-key</c> header), Hugging Face router,
/// Ollama, vLLM, LM Studio. JSON is built with <see cref="JsonNode"/> (AOT-safe).
/// </summary>
public sealed class OpenAiCompatibleChatModel(LlmOptions options, HttpClient? http = null) : IChatModel
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

    public LlmOptions Options => options;
    public string Name => $"{options.Provider}:{options.Model}";

    public async Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        var body = BuildRequest(messages, tools);
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
            throw new HttpRequestException($"LLM request to {options.Provider} failed ({(int)resp.StatusCode}): {Truncate(text, 400)}");
        return ParseResponse(text);
    }

    /// <summary>Request body for the chat-completions call.</summary>
    public JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools)
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
        return body;
    }

    /// <summary>Parses a chat-completions response (first choice).</summary>
    public static ChatResponse ParseResponse(string json)
    {
        var msg = JsonNode.Parse(json)?["choices"]?[0]?["message"] ?? throw new InvalidOperationException("Malformed chat completion response.");
        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray tc)
            for (int i = 0; i < tc.Count; i++)
            {
                var c = tc[i]!;
                var args = c["function"]?["arguments"];
                string argText = args is null ? "{}" : args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args.ToJsonString();
                calls.Add(new ToolCall(c["id"]?.GetValue<string>() ?? $"call_{i}", c["function"]!["name"]!.GetValue<string>(), argText));
            }
        return new ChatResponse(msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var content) ? content : null, calls);
    }

    private static JsonObject ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content };
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
/// Offline, deterministic "model": routes the question to tools by bilingual (English / Bahasa Indonesia) keyword intent, extracts simple
/// arguments (case, bus, fault type, load scale, hours) and returns the tool outputs as the answer. Lets the agent work without any API key.
/// </summary>
public sealed partial class RuleBasedChatModel : IChatModel
{
    private static readonly (string Tool, string[] Keywords)[] Intents =
    [
        ("short_circuit", ["short circuit", "short-circuit", "hubung singkat", "fault current", "arus gangguan", "arus hubung", "ik3", "kva hubung"]),
        ("contingency_n1", ["n-1", "n1", "contingency", "kontingensi", "outage", "keandalan saluran"]),
        ("economic_dispatch", ["dispatch", "economic", "ekonomis", "biaya", "cost", "lambda", "marginal"]),
        ("voltage_stability_margin", ["stability", "stabilitas", "collapse", "runtuh", "loadability", "nose", "margin"]),
        ("hosting_capacity", ["hosting", "pv", "solar", "surya", "plts", "kapasitas penampung"]),
        ("forecast_load", ["forecast", "prakiraan", "ramalan", "prediksi", "tomorrow", "besok", "predict"]),
        ("explain_alarms", ["alarm", "why", "mengapa", "kenapa", "violation", "pelanggaran", "explain", "jelaskan", "penyebab"]),
        ("twin_status", ["twin", "kembaran", "health", "kesehatan", "aging", "ageing", "penuaan", "status aset", "asset"]),
        ("run_power_flow", ["power flow", "load flow", "aliran daya", "losses", "rugi", "voltage", "tegangan", "loading", "pembebanan", "beban"]),
    ];

    public string Name => "rule-based (offline)";

    public Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AgentTool> tools, CancellationToken ct = default)
    {
        var last = messages[^1];
        if (last.Role == ChatRole.Tool || tools.Count == 0)
        {
            var user = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? "";
            bool id = GridAgent.DetectLanguage(user) == "id";
            var outputs = messages.SkipWhile(m => m.Role != ChatRole.Tool).Where(m => m.Role == ChatRole.Tool).Select(m => m.Content);
            string head = id ? "Asisten offline (berbasis aturan) — hasil perhitungan Electrical.Net:" : "Offline assistant (rule-based) — results from Electrical.Net tools:";
            return Task.FromResult(new ChatResponse(head + "\n\n" + string.Join("\n\n", outputs), []));
        }
        string q = (last.Content ?? "").ToLowerInvariant();
        var hits = Intents.Where(i => i.Keywords.Any(k => ContainsWord(q, k))).Select(i => i.Tool)
            .Where(t => tools.Any(x => x.Name == t)).Distinct().Take(2).ToList();
        // explanation questions about voltages want the alarm explainer, not a second plain power flow
        if (hits.Contains("explain_alarms")) hits.Remove("run_power_flow");
        if (hits.Count == 0) hits = [tools.Any(t => t.Name == "grid_overview") ? "grid_overview" : tools[0].Name];
        var args = ExtractArguments(q);
        return Task.FromResult(new ChatResponse(null, hits.Select((h, i) => new ToolCall($"call_{i}", h, args.ToJsonString())).ToList()));
    }

    private static bool ContainsWord(string text, string keyword) =>
        keyword.Length <= 3 ? Regex.IsMatch(text, $@"(?<![a-z0-9]){Regex.Escape(keyword)}(?![a-z0-9])") : text.Contains(keyword, StringComparison.Ordinal);

    /// <summary>Extracts tool arguments from free text (case, bus, fault_type, load_scale, hours).</summary>
    public static JsonObject ExtractArguments(string text)
    {
        string q = text.ToLowerInvariant();
        var a = new JsonObject();
        var c = CaseRegex().Match(q);
        if (c.Success) a["case"] = c.Groups[1].Value switch { "9" => "case9", "14" => "case14", _ => "case33bw" };
        var b = BusRegex().Match(q);
        if (b.Success) a["bus"] = int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture);
        if (Regex.IsMatch(q, @"\b(dlg|llg|double.line|2 ?fasa.?tanah|dua fasa ke tanah)")) a["fault_type"] = "dlg";
        else if (Regex.IsMatch(q, @"\b(slg|lg|single.line|ground|satu fasa|1 ?fasa|fasa.tanah|ke tanah)")) a["fault_type"] = "slg";
        else if (Regex.IsMatch(q, @"\b(ll|line.to.line|phase.to.phase|antar ?fasa|fasa.fasa)\b")) a["fault_type"] = "ll";
        else if (Regex.IsMatch(q, @"(3.?ph|three.phase|tiga fasa|3 fasa|simetris)")) a["fault_type"] = "3ph";
        var p = PercentRegex().Match(q);
        if (p.Success)
        {
            double v = double.Parse(p.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            bool down = Regex.IsMatch(q, @"(decrease|reduce|turun|kurang|lower|drop)");
            bool up = Regex.IsMatch(q, @"(increase|naik|raise|grow|tambah|more|\bup\b|\+)");
            a["load_scale"] = down ? 1 - v / 100 : up || v < 50 ? 1 + v / 100 : v / 100;
        }
        else if (ScaleRegex().Match(q) is { Success: true } s)
            a["load_scale"] = double.Parse(s.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var h = HoursRegex().Match(q);
        if (h.Success) a["hours"] = int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture);
        else if (q.Contains("tomorrow") || q.Contains("besok")) a["hours"] = 24;
        else if (q.Contains("week") || q.Contains("minggu")) a["hours"] = 168;
        return a;
    }

    [GeneratedRegex(@"(?:case|ieee|kasus)[\s-]*(9|14|33)")]
    private static partial Regex CaseRegex();
    [GeneratedRegex(@"\b(?:bus|rel|node|gardu)\s*#?\s*(\d+)")]
    private static partial Regex BusRegex();
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:%|persen|percent)")]
    private static partial Regex PercentRegex();
    [GeneratedRegex(@"(?:x|×|scale|skala)\s*(\d+(?:[.,]\d+)?)")]
    private static partial Regex ScaleRegex();
    [GeneratedRegex(@"(\d+)\s*(?:h\b|hours?|jam)")]
    private static partial Regex HoursRegex();
}
