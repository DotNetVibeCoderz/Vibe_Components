using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Medical.Core;

namespace Medical.AI.Assistant;

/// <summary>
/// Claude backend on the official Anthropic C# SDK. Runs a manual tool loop on the beta Messages endpoint with adaptive
/// thinking, and opts into server-side refusal fallbacks (<c>fallbacks: "default"</c>) so a safety-classifier false
/// positive on a benign clinical question is re-served instead of failing.
/// </summary>
public sealed class AnthropicClinicalLlm : IClinicalLlm
{
    public const string DefaultModel = "claude-opus-5-5";
    private const int MaxIterations = 8;
    private readonly IAnthropicClient _client;

    /// <param name="apiKey">Explicit key; when null the SDK resolves credentials itself (ANTHROPIC_API_KEY, profiles…).</param>
    /// <param name="workspaceId">Workspace for keys that are not scoped to one (sent as <c>anthropic-workspace-id</c>); defaults to ANTHROPIC_WORKSPACE_ID.</param>
    public AnthropicClinicalLlm(string? apiKey = null, string model = DefaultModel, string? workspaceId = null)
    {
        workspaceId ??= Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID");
        var http = string.IsNullOrEmpty(workspaceId) ? null : new HttpClient(new WorkspaceHeaderHandler(workspaceId)) { Timeout = TimeSpan.FromMinutes(10) };
        _client = (apiKey, http) switch
        {
            (null, null) => new AnthropicClient(),
            (null, _) => new AnthropicClient { HttpClient = http },
            (_, null) => new AnthropicClient { ApiKey = apiKey },
            _ => new AnthropicClient { ApiKey = apiKey, HttpClient = http },
        };
        Model = model;
    }

    public string Provider => "anthropic";
    public string Model { get; }

    public async Task<AssistantReply> RunAsync(string system, IReadOnlyList<ChatMessage> history, ClinicalTools tools, PatientRecord? patient, CancellationToken ct = default)
    {
        var messages = new List<BetaMessageParam>();
        foreach (var m in history)
            messages.Add(m.Role == ChatRole.User ? new BetaUserMessageParam { Content = m.Text } : new BetaAssistantMessageParam { Content = m.Text });

        var toolDefs = tools.All.Select(t => (BetaToolUnion)new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new()
            {
                Properties = t.InputSchema["properties"]!.AsObject().ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                Required = t.InputSchema["required"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
            },
        }).ToList();

        var calls = new List<ToolCallRecord>();
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            var response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new Default(),
                Thinking = new BetaThinkingConfigAdaptive(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.High },
                System = system,
                Tools = toolDefs,
                Messages = messages,
            }, ct).ConfigureAwait(false);

            if (response.StopReason == "refusal")
                return new AssistantReply(
                    "The model declined this request. Please rephrase or consult the clinical references directly. / Model menolak permintaan ini; silakan ubah pertanyaan.",
                    calls, Provider, Model, Refused: true);

            var assistantBlocks = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            var text = new System.Text.StringBuilder();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var t))
                {
                    assistantBlocks.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Append(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                {
                    assistantBlocks.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                }
                else if (block.TryPickRedactedThinking(out var rt))
                {
                    assistantBlocks.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                }
                else if (block.TryPickToolUse(out var tu))
                {
                    assistantBlocks.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    var input = JsonSerializer.SerializeToElement(tu.Input);
                    var record = tools.Execute(tu.Name, input, patient);
                    calls.Add(record);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = record.Output, IsError = record.IsError });
                }
            }

            if (response.StopReason != "tool_use" || results.Count == 0)
                return new AssistantReply(text.ToString().Trim(), calls, Provider, response.Model.ToString()?.Trim('"') ?? Model);

            messages.Add(new BetaAssistantMessageParam { Content = assistantBlocks });
            messages.Add(new BetaUserMessageParam { Content = results });
        }
        return new AssistantReply("Stopped after too many tool calls. / Dihentikan karena terlalu banyak pemanggilan alat.", calls, Provider, Model);
    }
}

/// <summary>Adds <c>anthropic-workspace-id</c> to every request (for API keys not scoped to a single workspace).</summary>
internal sealed class WorkspaceHeaderHandler(string workspaceId) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("anthropic-workspace-id");
        request.Headers.Add("anthropic-workspace-id", workspaceId);
        return base.SendAsync(request, cancellationToken);
    }
}
