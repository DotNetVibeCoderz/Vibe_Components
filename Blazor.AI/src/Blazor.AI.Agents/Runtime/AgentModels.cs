namespace Blazor.AI.Agents;

/// <summary>Everything that defines an agent. Agents are plain data, so they can be saved, edited in AgentStudio and shared.</summary>
public sealed class AgentDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];

    public string Name { get; set; } = "Assistant";

    /// <summary>One-line role, shown in pickers and used by supervisors to route work.</summary>
    public string Role { get; set; } = "General assistant";

    public string Instructions { get; set; } = "You are a helpful assistant. Use tools when they help. Be concise.";

    /// <summary>Provider name; <c>null</c> uses the default.</summary>
    public string? Provider { get; set; }

    /// <summary>Names of tools from the <see cref="ToolRegistry"/> this agent may call.</summary>
    public List<string> Tools { get; set; } = [];

    public float? Temperature { get; set; } = 0.4f;

    /// <summary>Upper bound on model ↔ tool round trips per run.</summary>
    public int MaxIterations { get; set; } = 6;

    /// <summary>Gives the agent "remember" and "recall" tools backed by vector memory.</summary>
    public bool UseMemory { get; set; }

    public string Icon { get; set; } = "bi-robot";

    /// <summary>A new agent with the same settings (new id, "(copy)" name).</summary>
    public AgentDefinition Clone()
    {
        var copy = Copy();
        copy.Id = Guid.NewGuid().ToString("N")[..10];
        copy.Name = Name + " (copy)";
        return copy;
    }

    /// <summary>An editable copy with the same id and name.</summary>
    public AgentDefinition Copy() => new()
    {
        Id = Id,
        Name = Name,
        Role = Role,
        Instructions = Instructions,
        Provider = Provider,
        Tools = [.. Tools],
        Temperature = Temperature,
        MaxIterations = MaxIterations,
        UseMemory = UseMemory,
        Icon = Icon,
    };
}

public enum AgentStepKind
{
    Input,
    ToolCall,
    ToolResult,
    Output,
    Error,
    Handoff,
}

/// <summary>One entry in an agent run's trace (what AgentDebugger shows).</summary>
public sealed record AgentStep(AgentStepKind Kind, string Agent, string Title, string? Detail = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public TimeSpan? Duration { get; init; }

    public bool IsError { get; init; }
}

/// <summary>The full record of one agent run.</summary>
public sealed class AgentRun
{
    private readonly List<AgentStep> _steps = [];
    private readonly Lock _gate = new();

    public required string AgentName { get; init; }

    public required string Input { get; init; }

    public string Output { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public bool Failed { get; set; }

    public IReadOnlyList<AgentStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return _steps.ToArray();
            }
        }
    }

    public int ToolCalls => Steps.Count(s => s.Kind == AgentStepKind.ToolCall);

    public TimeSpan Elapsed => (CompletedAt ?? DateTimeOffset.Now) - StartedAt;

    public event Action<AgentStep>? StepAdded;

    public void Add(AgentStep step)
    {
        lock (_gate)
        {
            _steps.Add(step);
        }

        StepAdded?.Invoke(step);
    }
}
