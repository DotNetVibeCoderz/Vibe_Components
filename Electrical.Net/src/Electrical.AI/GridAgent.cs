using System.Text.Json.Nodes;
using Electrical.Core;

namespace Electrical.AI;

/// <summary>Answer of the <see cref="GridAgent"/>.</summary>
/// <param name="ToolsUsed">Names of the tools called, in order.</param>
/// <param name="Model">Name of the chat model that produced the answer.</param>
/// <param name="Language">"en" or "id".</param>
public sealed record AgentAnswer(string Text, IReadOnlyList<string> ToolsUsed, string Model, string Language);

/// <summary>
/// LLM grid-operator assistant. Answers natural-language questions (English or Bahasa Indonesia) by letting a chat model call grounded
/// Electrical.Net tools (<see cref="GridTools"/>: power flow, N-1, short circuit, economic dispatch, voltage stability, hosting capacity, load
/// forecast, explained alarms, digital-twin status). Without an LLM configured (<see cref="LlmOptions.FromEnvironment"/>) it uses the offline
/// <see cref="RuleBasedChatModel"/>.
/// </summary>
/// <example><code>
/// var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });
/// string answer = agent.Ask("Berapa arus hubung singkat 3 fasa di bus 4?");
/// </code></example>
public sealed class GridAgent
{
    private readonly IChatModel _model;
    private readonly GridAgentContext _ctx;
    private readonly IReadOnlyList<AgentTool> _tools;

    public GridAgent(GridAgentContext? context = null, IChatModel? model = null)
    {
        _ctx = context ?? new GridAgentContext();
        _model = model ?? CreateDefaultModel();
        _tools = GridTools.Create(_ctx, () => Language);
    }

    /// <summary>LLM client from the environment, or the offline rule-based model.</summary>
    public static IChatModel CreateDefaultModel() =>
        LlmOptions.FromEnvironment() is { } o ? new OpenAiCompatibleChatModel(o) : new RuleBasedChatModel();

    public GridAgentContext Context => _ctx;
    public IChatModel Model => _model;
    public IReadOnlyList<AgentTool> Tools => _tools;
    public int MaxToolRounds { get; init; } = 5;

    /// <summary>Language of the current question ("id" or "en").</summary>
    public string Language { get; private set; } = "en";

    /// <summary>Synchronous convenience wrapper returning only the answer text.</summary>
    public string Ask(string question) => AskAsync(question).GetAwaiter().GetResult().Text;

    public async Task<AgentAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        Language = DetectLanguage(question);
        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt()), new(ChatRole.User, question) };
        var used = new List<string>();
        for (int round = 0; round <= MaxToolRounds; round++)
        {
            var resp = await _model.CompleteAsync(messages, round < MaxToolRounds ? _tools : [], ct).ConfigureAwait(false);
            if (resp.ToolCalls.Count == 0) return new AgentAnswer(resp.Content?.Trim() ?? "", used, _model.Name, Language);
            messages.Add(new ChatMessage(ChatRole.Assistant, resp.Content, resp.ToolCalls));
            foreach (var call in resp.ToolCalls)
            {
                used.Add(call.Name);
                messages.Add(new ChatMessage(ChatRole.Tool, await InvokeToolAsync(call, ct).ConfigureAwait(false), ToolCallId: call.Id));
            }
        }
        return new AgentAnswer(Language == "id" ? "Tidak dapat menyelesaikan permintaan dalam batas pemanggilan alat." : "Unable to complete the request within the tool-call budget.",
            used, _model.Name, Language);
    }

    /// <summary>Executes one tool call (errors are returned as text so the model can recover).</summary>
    public async Task<string> InvokeToolAsync(ToolCall call, CancellationToken ct = default)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null) return $"Unknown tool '{call.Name}'. Available: {string.Join(", ", _tools.Select(t => t.Name))}.";
        try
        {
            var args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
            return await tool.Invoke(args, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Tool error ({call.Name}): {ex.Message}";
        }
    }

    private string SystemPrompt() =>
        $"""
        You are the Electrical.Net Grid Operator Assistant, an expert power-system engineer. {ElectricalInfo.CreditEn}.
        Default network: {(_ctx.Network?.ToString() ?? _ctx.CaseName)}. Built-in study cases: case9, case14, case33bw (Baran-Wu 33-bus feeder), syntheticN.
        {(_ctx.Twin is null ? "No live digital twin is attached." : "A live digital twin is attached (use twin_status / explain_alarms).")}
        Always ground numbers in the tools; never invent results. Units: MW, Mvar, MVA, kA, kV, pu. Voltage band 0.95-1.05 pu.
        Give concise, operator-oriented answers with the key numbers and a recommended action when relevant.
        Answer in {(Language == "id" ? "Bahasa Indonesia" : "English")}.
        """;

    private static readonly HashSet<string> IdMarkers =
    [
        "berapa", "apa", "bagaimana", "kapan", "mengapa", "kenapa", "yang", "ini", "itu", "di", "dan", "untuk", "pada", "dari", "saya", "bisa",
        "tegangan", "arus", "beban", "aliran", "daya", "hubung", "singkat", "gangguan", "saluran", "pembangkit", "rugi", "besok", "prakiraan",
        "jelaskan", "tolong", "berikan", "kapasitas", "stabilitas", "naik", "turun", "jam", "biaya", "kesehatan", "trafo",
    ];

    /// <summary>Very small language detector: "id" when Indonesian function words / power-system terms dominate, else "en".</summary>
    public static string DetectLanguage(string text)
    {
        var words = text.ToLowerInvariant().Split([' ', '?', ',', '.', '!', '\n', ':', ';'], StringSplitOptions.RemoveEmptyEntries);
        int hits = words.Count(IdMarkers.Contains);
        return hits >= 2 || (hits == 1 && words.Length <= 4) ? "id" : "en";
    }
}
