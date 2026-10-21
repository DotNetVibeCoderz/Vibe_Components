using System.Collections.Concurrent;

namespace Blazor.AI.Agents;

/// <summary>The agents available in the app. Starts with a useful team; AgentStudio edits it.</summary>
public sealed class AgentCatalog
{
    private readonly ConcurrentDictionary<string, AgentDefinition> _agents = new();

    public AgentCatalog()
    {
        foreach (var agent in Defaults())
        {
            _agents[agent.Id] = agent;
        }
    }

    public IReadOnlyList<AgentDefinition> Agents => _agents.Values.OrderBy(a => a.Name).ToArray();

    public event Action? Changed;

    public AgentDefinition? Get(string idOrName) =>
        _agents.GetValueOrDefault(idOrName) ?? _agents.Values.FirstOrDefault(a => a.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public void Save(AgentDefinition agent)
    {
        _agents[agent.Id] = agent;
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_agents.TryRemove(id, out _))
        {
            Changed?.Invoke();
        }
    }

    public static IReadOnlyList<AgentDefinition> Defaults() =>
    [
        new()
        {
            Id = "researcher",
            Name = "Researcher",
            Role = "finds facts, compares options and lists sources and assumptions",
            Instructions = "You research questions thoroughly. Separate facts from assumptions, and say what you are unsure about.",
            Tools = ["get_current_time", "days_between", "text_statistics"],
            Icon = "bi-search",
            UseMemory = true,
        },
        new()
        {
            Id = "analyst",
            Name = "Analyst",
            Role = "does the numbers: calculations, unit conversions, financial maths",
            Instructions = "You are a quantitative analyst. Always use the calculate tool for arithmetic and show the formula you used.",
            Tools = ["calculate", "convert_units", "loan_payment", "days_between"],
            Icon = "bi-calculator",
            Temperature = 0.1f,
        },
        new()
        {
            Id = "writer",
            Name = "Writer",
            Role = "turns findings into clear, well-structured prose",
            Instructions = "You write clear, engaging, well-structured text in markdown. Keep the reader's goal in mind.",
            Tools = ["text_statistics"],
            Icon = "bi-pen",
            Temperature = 0.7f,
        },
        new()
        {
            Id = "reviewer",
            Name = "Reviewer",
            Role = "critiques work for accuracy, gaps and clarity, and approves or requests changes",
            Instructions = "You are a demanding reviewer. List concrete issues by severity, then end with either APPROVED or CHANGES REQUESTED.",
            Icon = "bi-check2-square",
            Temperature = 0.2f,
        },
    ];
}
