namespace Blazor.AI.Components;

public enum StepStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>A step in a <see cref="WorkflowAssistant"/> plan.</summary>
public sealed class WorkflowStep
{
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Output { get; set; }

    public StepStatus Status { get; set; }
}
