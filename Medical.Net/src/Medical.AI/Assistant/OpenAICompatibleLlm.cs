using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Medical.Core;

namespace Medical.AI.Assistant;

/// <summary>
/// Backend for OpenAI-compatible chat-completions servers with function calling: Hugging Face Inference Providers
/// (router.huggingface.co/v1), Azure OpenAI, DeepSeek, Ollama, vLLM, LM Studio… Uses <see cref="JsonNode"/> only (AOT-friendly).
/// </summary>
public sealed class OpenAICompatibleLlm(HttpClient http, string endpoint, string? apiKey, string model) : IClinicalLlm
{
    private const int MaxIterations = 8;

    public string Provider => endpoint.Contains("huggingface", StringComparison.OrdinalIgnoreCase) ? "huggingface"
        : endpoint.Contains("azure", StringComparison.OrdinalIgnoreCase) ? "azure-openai"
        : endpoint.Contains("localhost:11434", StringComparison.Ordinal) ? "ollama" : "openai-compatible";

    public string Model => model;

    private string Url => endpoint.Contains("/chat/completions", StringComparison.Ordinal) ? endpoint : endpoint.TrimEnd('/') + "/chat/completions";

    public async Task<AssistantReply> RunAsync(string system, IReadOnlyList<ChatMessage> history, ClinicalTools tools, PatientRecord? patient, CancellationToken ct = default)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in history) messages.Add((JsonNode)new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "assistant", ["content"] = m.Text });
        var toolDefs = new JsonArray(tools.All.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
        }).ToArray());

        var calls = new List<ToolCallRecord>();
        for (int i = 0; i < MaxIterations; i++)
        {
            var body = new JsonObject { ["model"] = model, ["messages"] = messages.DeepClone(), ["tools"] = toolDefs.DeepClone(), ["tool_choice"] = "auto", ["max_tokens"] = 2048 };
            using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (apiKey is { Length: > 0 })
            {
                if (Provider == "azure-openai") req.Headers.Add("api-key", apiKey);
                else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{Provider} returned {(int)res.StatusCode}: {json[..Math.Min(400, json.Length)]}", null, res.StatusCode);

            var message = JsonNode.Parse(json)?["choices"]?[0]?["message"] ?? throw new InvalidDataException("No choices in response.");
            var toolCalls = message["tool_calls"]?.AsArray();
            if (toolCalls is null || toolCalls.Count == 0)
                return new AssistantReply(message["content"]?.GetValue<string>()?.Trim() ?? "", calls, Provider, model);

            messages.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = message["content"]?.DeepClone(), ["tool_calls"] = toolCalls.DeepClone() });
            foreach (var tc in toolCalls)
            {
                string id = tc?["id"]?.GetValue<string>() ?? "";
                string name = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                string args = tc?["function"]?["arguments"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : tc?["function"]?["arguments"]?.ToJsonString() ?? "{}";
                ToolCallRecord record;
                try { record = tools.Execute(name, args, patient); }
                catch (JsonException e) { record = new(name, args, new JsonObject { ["error"] = $"Invalid JSON arguments: {e.Message}" }.ToJsonString(), true); }
                calls.Add(record);
                messages.Add((JsonNode)new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = record.Output });
            }
        }
        return new AssistantReply("Stopped after too many tool calls.", calls, Provider, model);
    }
}
