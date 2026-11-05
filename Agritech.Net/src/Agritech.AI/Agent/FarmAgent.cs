using System.Text.Json.Nodes;
using Agritech.Core.Geo;
using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

namespace Agritech.AI.Agent;

/// <summary>A callable tool exposed to the language model.</summary>
public sealed record AgentTool(string Name, string Description, string ParametersSchema, Func<JsonObject, CancellationToken, Task<string>> Invoke);

/// <summary>Everything the agent knows about the farm it is advising.</summary>
public sealed class FarmContext
{
    public string FarmName { get; set; } = "My Farm";
    public required string CropId { get; set; }
    public DateOnly PlantingDate { get; set; }
    public double AreaHa { get; set; } = 1;
    public SoilSample Soil { get; set; } = new();
    public GeoCoordinate Location { get; set; } = new(-6.595, 106.816); // Bogor, West Java
    public double ElevationM { get; set; } = 250;
    public double? TargetYieldTonHa { get; set; }
    public IWeatherProvider Weather { get; set; } = new SyntheticWeatherProvider();
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public CropCatalog Catalog { get; set; } = CropCatalog.Default;

    public DateOnly Today => DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);
    public int DaysAfterPlanting => Today.DayNumber - PlantingDate.DayNumber;
    public CropDefinition Crop => Catalog.Get(CropId);
    public WeatherStation Station => new(Location.Latitude, ElevationM, FarmName);
}

public sealed record AgentAnswer(string Text, IReadOnlyList<string> ToolsUsed, string Model, string Language);

/// <summary>
/// Agricultural AI agent. Answers natural-language questions (English or Bahasa Indonesia) by letting an LLM call
/// grounded agronomy tools (fertilizer, irrigation, weather, disease, pest, yield, soil, crop suitability).
/// Without an LLM configured it falls back to the offline <see cref="RuleBasedChatModel"/>.
/// </summary>
/// <example><code>
/// var agent = new FarmAgent(new FarmContext { CropId = "rice", PlantingDate = new(2026, 10, 20) });
/// string answer = agent.Ask("Berapa dosis pupuk terbaik minggu ini?");
/// </code></example>
public sealed class FarmAgent
{
    private readonly IChatModel _model;
    private readonly FarmContext _ctx;
    private readonly IReadOnlyList<AgentTool> _tools;

    public FarmAgent(FarmContext context, IChatModel? model = null)
    {
        _ctx = context;
        _model = model ?? (LlmOptions.FromEnvironment() is { } o ? new OpenAiCompatibleChatModel(o) : new RuleBasedChatModel());
        _tools = AgronomyTools.Create(context, () => Language);
    }

    public FarmContext Context => _ctx;
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
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt()),
            new(ChatRole.User, question),
        };
        var used = new List<string>();
        for (int round = 0; round <= MaxToolRounds; round++)
        {
            var resp = await _model.CompleteAsync(messages, round < MaxToolRounds ? _tools : [], ct).ConfigureAwait(false);
            if (resp.ToolCalls.Count == 0)
                return new AgentAnswer(resp.Content?.Trim() ?? "", used, _model.Name, Language);
            messages.Add(new ChatMessage(ChatRole.Assistant, resp.Content, resp.ToolCalls));
            foreach (var call in resp.ToolCalls)
            {
                used.Add(call.Name);
                var tool = _tools.FirstOrDefault(t => t.Name == call.Name);
                string result;
                try
                {
                    var args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
                    result = tool is null ? $"Unknown tool '{call.Name}'." : await tool.Invoke(args, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = $"Tool error: {ex.Message}";
                }
                messages.Add(new ChatMessage(ChatRole.Tool, result, ToolCallId: call.Id));
            }
        }
        return new AgentAnswer("Unable to complete the request within the tool-call budget.", used, _model.Name, Language);
    }

    private string SystemPrompt() =>
        $"""
        You are Agritech.Net Farm Assistant, an expert agronomist. {Core.AgritechInfo.CreditEn}.
        Farm: {_ctx.FarmName} at {_ctx.Location} ({_ctx.ElevationM} m). Crop: {_ctx.Crop.Name} ({_ctx.Crop.NameId}), planted {_ctx.PlantingDate:yyyy-MM-dd},
        today {_ctx.Today:yyyy-MM-dd} ({_ctx.DaysAfterPlanting} days after planting), area {_ctx.AreaHa} ha.
        Always ground numeric advice in the provided tools; never invent doses. Give concrete quantities (kg/ha and total kg for the field),
        timing and safety notes. Be concise and practical for farmers.
        Answer in {(Language == "id" ? "Bahasa Indonesia" : "English")}.
        """;

    private static readonly string[] IdMarkers =
        ["berapa", "apa", "bagaimana", "kapan", "pupuk", "tanam", "minggu", "ini", "yang", "hama", "penyakit", "cuaca", "saya", "bisa", "tanah", "air", "panen", "hari", "dosis", "padi", "jagung"];

    public static string DetectLanguage(string text)
    {
        var words = text.ToLowerInvariant().Split([' ', '?', ',', '.', '!', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int hits = words.Count(w => IdMarkers.Contains(w));
        return hits >= 1 && hits * 4 >= words.Length * 0.5 ? "id" : hits >= 2 ? "id" : "en";
    }
}
