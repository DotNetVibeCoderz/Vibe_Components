namespace Blazor.AI.Components;

/// <summary>A rendered conversation turn (the view model behind <see cref="AiChat"/>).</summary>
public sealed class ChatTurn
{
    public required string Role { get; init; }

    public string Text { get; set; } = string.Empty;

    public string? Author { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public List<ToolCallInfo> ToolCalls { get; } = [];

    public bool IsError { get; set; }

    public bool IsUser => Role == "user";
}

/// <summary>A tool invocation made while producing an assistant turn.</summary>
public sealed record ToolCallInfo(string CallId, string Name, string Arguments)
{
    public string? Result { get; set; }
}
