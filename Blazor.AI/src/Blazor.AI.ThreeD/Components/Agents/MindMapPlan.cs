using System.Text.Json.Nodes;

namespace Blazor.AI.ThreeD;

public enum StepStatus
{
    Pending,
    Running,
    Done,
    Blocked,
}

/// <summary>One step of an agent's reasoning or plan.</summary>
public sealed record MindStep(string Id, string Title, string Detail, IReadOnlyList<string> DependsOn, string Kind = "step")
{
    public StepStatus Status { get; set; }
}

/// <summary>Builds a plan from the model's JSON or, offline, from a deterministic decomposition of the goal.</summary>
public static class MindMapPlan
{
    public const string Schema = """{"steps":[{"id":"1","title":"Short step title","detail":"What to do and why","dependsOn":["id of an earlier step"],"kind":"question|evidence|decision|action|check"}]}""";

    public static IReadOnlyList<MindStep>? FromJson(JsonNode? node)
    {
        if (node?["steps"] is not JsonArray arr)
        {
            return null;
        }

        var steps = new List<MindStep>();
        foreach (var s in arr)
        {
            var id = s?["id"]?.ToString()?.Trim();
            var title = s?["title"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title) || steps.Any(x => x.Id == id) || title == "Short step title")
            {
                continue;
            }

            var deps = (s?["dependsOn"] as JsonArray)?.Select(d => d?.ToString()?.Trim() ?? string.Empty).Where(d => d.Length > 0 && d != id).ToArray() ?? [];
            steps.Add(new MindStep(id, title, s?["detail"]?.GetValue<string>() ?? string.Empty, deps, s?["kind"]?.GetValue<string>() ?? "step"));
        }

        // Keep only dependencies on earlier steps (guarantees a DAG).
        var known = new HashSet<string>();
        var clean = new List<MindStep>();
        foreach (var s in steps)
        {
            clean.Add(s with { DependsOn = s.DependsOn.Where(known.Contains).ToArray() });
            known.Add(s.Id);
        }

        return clean.Count >= 4 ? clean : null;
    }

    /// <summary>A sensible plan skeleton for any goal, phrased around the goal's own words.</summary>
    public static IReadOnlyList<MindStep> Local(string goal)
    {
        var g = goal.Trim().TrimEnd('.', '?', '!');
        var subject = g.Length > 60 ? g[..60] + "…" : g;
        return
        [
            new("1", "Clarify the goal", $"Restate \"{subject}\" as a measurable outcome and list constraints (time, budget, people).", [], "question"),
            new("2", "Gather facts", "Collect current data, prior attempts and stakeholder needs.", ["1"], "evidence"),
            new("3", "Identify risks", "List what could go wrong and how likely and costly each risk is.", ["1"], "evidence"),
            new("4", "Generate options", "Draft at least three ways to reach the goal, including a low-cost one.", ["2"], "question"),
            new("5", "Evaluate options", "Score options against value, cost, effort and the risks found.", ["3", "4"], "decision"),
            new("6", "Choose and plan", "Pick the best option and break it into milestones with owners.", ["5"], "decision"),
            new("7", "Execute first milestone", "Deliver the smallest useful slice to learn quickly.", ["6"], "action"),
            new("8", "Measure results", "Compare outcomes with the target metric defined in step 1.", ["7"], "check"),
            new("9", "Iterate or scale", "Adjust the plan from what was learned, then roll out further.", ["8"], "action"),
        ];
    }

    /// <summary>Topological level of each step (0 = no dependencies).</summary>
    public static Dictionary<string, int> Levels(IReadOnlyList<MindStep> steps)
    {
        var level = new Dictionary<string, int>();
        foreach (var s in steps)
        {
            level[s.Id] = s.DependsOn.Count == 0 ? 0 : s.DependsOn.Max(d => level.GetValueOrDefault(d)) + 1;
        }

        return level;
    }
}
