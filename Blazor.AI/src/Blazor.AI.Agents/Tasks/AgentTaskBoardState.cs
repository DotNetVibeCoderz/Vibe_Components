using System.Text.Json.Nodes;
using Blazor.AI.Abstractions;
using Blazor.AI.Core.Services;

namespace Blazor.AI.Agents;

public enum AgentTaskStatus
{
    Todo,
    InProgress,
    Review,
    Done,
}

/// <summary>A card on the agent task board.</summary>
public sealed class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? AgentId { get; set; }

    public AgentTaskStatus Status { get; set; }

    public string? Result { get; set; }

    public bool IsRunning { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Board state plus AI planning: turn a goal into tasks assigned to the best-suited agents.</summary>
public sealed class AgentTaskBoardState(AgentCatalog catalog, AiTextService ai)
{
    public List<AgentTask> Tasks { get; } = [];

    public string? Goal { get; set; }

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    public async Task PlanAsync(string goal, string? provider = null, CancellationToken cancellationToken = default)
    {
        Goal = goal;
        var roster = string.Join("\n", catalog.Agents.Select(a => $"- {a.Name}: {a.Role}"));
        var system = "Break the goal into 3-6 tasks and assign each to the best agent from the roster. Reply with JSON only: " +
                     "{\"tasks\": [{\"title\": short imperative, \"description\": one sentence, \"agent\": agent name}]}.";
        var node = await ai.GenerateJsonAsync(system, $"Roster:\n{roster}\n\n" + PromptTags.Wrap(PromptTags.Input, goal), AiTasks.Plan, provider, cancellationToken).ConfigureAwait(false);
        foreach (var task in (node?["tasks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var agent = catalog.Get(task["agent"]?.ToString() ?? string.Empty) ?? catalog.Agents.FirstOrDefault();
            Tasks.Add(new AgentTask
            {
                Title = task["title"]?.ToString() ?? "Task",
                Description = task["description"]?.ToString(),
                AgentId = agent?.Id,
            });
        }

        Notify();
    }
}
