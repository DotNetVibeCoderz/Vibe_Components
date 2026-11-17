using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GeoPandas.Net.AI;

/// <summary>Minimal chat-completion abstraction so any LLM backend can power <see cref="GeoAssistant"/>.</summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

public enum LlmProvider { OpenAI, AzureOpenAI, OpenAICompatible }

/// <summary>
/// Chat-completions client for OpenAI, Azure OpenAI (v1 API) and OpenAI-compatible servers
/// (DeepSeek, Ollama, LM Studio, vLLM…). Uses only System.Net.Http + System.Text.Json (AOT friendly).
/// </summary>
public sealed class OpenAiChatClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _model;
    private readonly bool _ownsHttp;

    public OpenAiChatClient(LlmProvider provider, string endpoint, string apiKey, string model, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _model = model;
        var baseUrl = endpoint.TrimEnd('/');
        switch (provider)
        {
            case LlmProvider.AzureOpenAI:
                _url = baseUrl.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/chat/completions" : baseUrl + "/openai/v1/chat/completions";
                _http.DefaultRequestHeaders.Remove("api-key");
                _http.DefaultRequestHeaders.Add("api-key", apiKey);
                break;
            default:
                _url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl
                    : baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || provider == LlmProvider.OpenAICompatible && baseUrl.Contains("deepseek") ? baseUrl + "/chat/completions"
                    : baseUrl + "/v1/chat/completions";
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }
    }

    /// <summary>
    /// Creates a client from environment variables: GEOPANDAS_LLM_PROVIDER (openai|azure|compatible),
    /// GEOPANDAS_LLM_ENDPOINT, GEOPANDAS_LLM_API_KEY, GEOPANDAS_LLM_MODEL. Returns null when not configured.
    /// </summary>
    public static OpenAiChatClient? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("GEOPANDAS_LLM_API_KEY");
        var model = Environment.GetEnvironmentVariable("GEOPANDAS_LLM_MODEL");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model)) return null;
        var prov = (Environment.GetEnvironmentVariable("GEOPANDAS_LLM_PROVIDER") ?? "openai").ToLowerInvariant() switch
        {
            "azure" or "azureopenai" => LlmProvider.AzureOpenAI,
            "compatible" or "deepseek" or "ollama" => LlmProvider.OpenAICompatible,
            _ => LlmProvider.OpenAI
        };
        var endpoint = Environment.GetEnvironmentVariable("GEOPANDAS_LLM_ENDPOINT") ?? "https://api.openai.com";
        return new OpenAiChatClient(prov, endpoint, key, model);
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", _model);
            w.WriteStartArray("messages");
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", systemPrompt); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("role", "user"); w.WriteString("content", userPrompt); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        using var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var resp = await _http.PostAsync(_url, content, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM request failed ({(int)resp.StatusCode}): {Truncate(body, 400)}");
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        return choice.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
