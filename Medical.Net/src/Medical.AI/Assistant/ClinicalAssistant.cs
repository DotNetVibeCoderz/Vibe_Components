using Medical.Core;

namespace Medical.AI.Assistant;

public enum ChatRole { User, Assistant }

public sealed record ChatMessage(ChatRole Role, string Text);

/// <summary>The assistant's final answer plus every tool call it made to produce it.</summary>
public sealed record AssistantReply(string Text, IReadOnlyList<ToolCallRecord> ToolCalls, string Provider, string Model, bool Refused = false);

/// <summary>A language-model backend that runs the tool loop with <see cref="ClinicalTools"/>.</summary>
public interface IClinicalLlm
{
    string Provider { get; }
    string Model { get; }
    Task<AssistantReply> RunAsync(string system, IReadOnlyList<ChatMessage> history, ClinicalTools tools, PatientRecord? patient, CancellationToken ct = default);
}

public enum LlmProvider { Offline, Anthropic, OpenAICompatible }

/// <summary>LLM selection. <see cref="FromEnvironment"/> reads MEDNET_LLM_* variables, then ANTHROPIC_API_KEY / HF_TOKEN.</summary>
public sealed record LlmOptions
{
    public LlmProvider Provider { get; init; } = LlmProvider.Offline;
    public string? ApiKey { get; init; }
    /// <summary>Base URL for OpenAI-compatible servers, e.g. https://router.huggingface.co/v1, http://localhost:11434/v1.</summary>
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    /// <summary>Anthropic workspace id for keys not scoped to a workspace.</summary>
    public string? WorkspaceId { get; init; }

    public static LlmOptions FromEnvironment()
    {
        string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;
        var provider = Env("MEDNET_LLM_PROVIDER")?.ToLowerInvariant();
        string? key = Env("MEDNET_LLM_API_KEY"), endpoint = Env("MEDNET_LLM_ENDPOINT"), model = Env("MEDNET_LLM_MODEL");
        return provider switch
        {
            "anthropic" or "claude" => new() { Provider = LlmProvider.Anthropic, ApiKey = key ?? Env("ANTHROPIC_API_KEY"), Model = model },
            "openai" or "azure" or "deepseek" or "ollama" => new() { Provider = LlmProvider.OpenAICompatible, ApiKey = key, Endpoint = endpoint ?? "http://localhost:11434/v1", Model = model ?? "llama3.1" },
            "huggingface" or "hf" => new() { Provider = LlmProvider.OpenAICompatible, ApiKey = key ?? Env("HF_TOKEN"), Endpoint = endpoint ?? "https://router.huggingface.co/v1", Model = model ?? "Qwen/Qwen2.5-72B-Instruct" },
            "offline" => new(),
            _ when Env("ANTHROPIC_API_KEY") is { } ak => new() { Provider = LlmProvider.Anthropic, ApiKey = ak, Model = model },
            _ when Env("HF_TOKEN") is { } hf => new() { Provider = LlmProvider.OpenAICompatible, ApiKey = hf, Endpoint = "https://router.huggingface.co/v1", Model = model ?? "Qwen/Qwen2.5-72B-Instruct" },
            _ => new(),
        };
    }

    public IClinicalLlm Create(HttpClient? http = null) => Provider switch
    {
        LlmProvider.Anthropic => new AnthropicClinicalLlm(ApiKey, Model ?? AnthropicClinicalLlm.DefaultModel, WorkspaceId),
        LlmProvider.OpenAICompatible => new OpenAICompatibleLlm(http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, Endpoint!, ApiKey, Model!),
        _ => new OfflineClinicalLlm(),
    };
}

/// <summary>
/// Clinical assistant (EN/ID). Answers are grounded in <see cref="ClinicalTools"/> results and the patient in context;
/// the conversation history is kept per instance. Every question is audited.
/// </summary>
public sealed class ClinicalAssistant(IClinicalLlm llm, ClinicalTools? tools = null, IAuditLog? audit = null)
{
    private readonly List<ChatMessage> _history = [];

    public const string SystemPrompt = """
        You are Medical.Net Clinical Assistant, a decision-support tool for licensed clinicians in Indonesia and elsewhere.
        - Answer in the language of the question (English or Bahasa Indonesia).
        - Ground every number (scores, doses, eGFR, codes, interactions) in a tool result; call get_patient_summary before discussing the current patient. If no tool can answer, say what is missing instead of estimating.
        - Be concise and clinically structured: assessment, key findings, recommended next steps, red flags.
        - You support, not replace, clinical judgement; for emergencies advise escalation per local protocol.
        """;

    public IClinicalLlm Llm { get; } = llm;
    public ClinicalTools Tools { get; } = tools ?? new ClinicalTools();
    public PatientRecord? Patient { get; set; }
    public IReadOnlyList<ChatMessage> History => _history;

    public void Reset() => _history.Clear();

    public async Task<AssistantReply> AskAsync(string question, CancellationToken ct = default)
    {
        _history.Add(new ChatMessage(ChatRole.User, question));
        var reply = await Llm.RunAsync(SystemPrompt, _history, Tools, Patient, ct).ConfigureAwait(false);
        _history.Add(new ChatMessage(ChatRole.Assistant, reply.Text));
        audit?.Record(new AuditEvent(DateTimeOffset.UtcNow, "assistant", AuditAction.Query, $"ClinicalAssistant/{Llm.Provider}", Patient?.Patient.Id,
            $"tools:{string.Join(',', reply.ToolCalls.Select(t => t.Name))}"));
        return reply;
    }
}
