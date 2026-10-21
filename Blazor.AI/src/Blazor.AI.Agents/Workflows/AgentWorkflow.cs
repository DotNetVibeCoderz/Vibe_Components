using System.Text;

namespace Blazor.AI.Agents;

public enum WorkflowNodeKind
{
    Start,
    Agent,
    Tool,
    Condition,
    Transform,
    End,
}

public enum WorkflowNodeStatus
{
    Idle,
    Running,
    Done,
    Skipped,
    Failed,
}

/// <summary>A node on the workflow canvas.</summary>
public sealed class WorkflowNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public WorkflowNodeKind Kind { get; set; }

    public string Title { get; set; } = "Node";

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Agent id (Agent nodes) or tool name (Tool nodes).</summary>
    public string? Target { get; set; }

    /// <summary>
    /// Agent: extra instruction. Condition: keyword the input must contain to follow the "yes" edge.
    /// Transform: a template where {{input}} is replaced by the incoming text.
    /// Tool: argument template, e.g. {"expression": "{{input}}"}.
    /// </summary>
    public string? Config { get; set; }

    public WorkflowNodeStatus Status { get; set; }

    public string? Output { get; set; }
}

/// <summary>An edge; edges leaving a Condition node carry <c>"yes"</c> or <c>"no"</c> as label.</summary>
public sealed record WorkflowEdge(string From, string To, string? Label = null);

/// <summary>A directed workflow of agents, tools, conditions and transforms.</summary>
public sealed class AgentWorkflow
{
    public string Name { get; set; } = "Workflow";

    public List<WorkflowNode> Nodes { get; } = [];

    public List<WorkflowEdge> Edges { get; } = [];

    public WorkflowNode? Start => Nodes.FirstOrDefault(n => n.Kind == WorkflowNodeKind.Start);

    public IEnumerable<WorkflowNode> Next(WorkflowNode node, string? label = null) =>
        Edges.Where(e => e.From == node.Id && (label is null || e.Label is null || e.Label == label))
            .Select(e => Nodes.FirstOrDefault(n => n.Id == e.To))
            .Where(n => n is not null)!;

    public void Reset()
    {
        foreach (var node in Nodes)
        {
            node.Status = WorkflowNodeStatus.Idle;
            node.Output = null;
        }
    }

    /// <summary>A ready-made content pipeline: research → condition → write → review.</summary>
    public static AgentWorkflow Sample()
    {
        var workflow = new AgentWorkflow { Name = "Research & write" };
        WorkflowNode Add(WorkflowNodeKind kind, string title, double x, double y, string? target = null, string? config = null)
        {
            var node = new WorkflowNode { Kind = kind, Title = title, X = x, Y = y, Target = target, Config = config };
            workflow.Nodes.Add(node);
            return node;
        }

        var start = Add(WorkflowNodeKind.Start, "Topic", 30, 150);
        var research = Add(WorkflowNodeKind.Agent, "Research", 200, 150, "researcher", "List 5 key facts and 2 open questions.");
        var check = Add(WorkflowNodeKind.Condition, "Has numbers?", 380, 150, config: "%");
        var analyse = Add(WorkflowNodeKind.Agent, "Analyse numbers", 560, 50, "analyst", "Check every number and compute any derived figures.");
        var write = Add(WorkflowNodeKind.Agent, "Write article", 560, 250, "writer", "Write a 250-word article from the material.");
        var review = Add(WorkflowNodeKind.Agent, "Review", 740, 150, "reviewer");
        var end = Add(WorkflowNodeKind.End, "Result", 910, 150);

        workflow.Edges.AddRange(
        [
            new(start.Id, research.Id),
            new(research.Id, check.Id),
            new(check.Id, analyse.Id, "yes"),
            new(check.Id, write.Id, "no"),
            new(analyse.Id, write.Id),
            new(write.Id, review.Id),
            new(review.Id, end.Id),
        ]);
        return workflow;
    }
}

/// <summary>
/// Executes a workflow as a DAG. A node runs once every predecessor has settled (done or skipped) and at least one
/// active edge delivered input to it; a node whose incoming edges are all inactive is skipped. Inputs are joined.
/// </summary>
public sealed class WorkflowExecutor(AgentRuntime runtime, AgentCatalog catalog)
{
    public async Task<string> RunAsync(AgentWorkflow workflow, string input, Action? onUpdate = null, CancellationToken cancellationToken = default)
    {
        workflow.Reset();
        var start = workflow.Start ?? throw new InvalidOperationException("The workflow needs a Start node.");
        var inputs = new Dictionary<string, List<string>> { [start.Id] = [input] };
        var result = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = workflow.Nodes.FirstOrDefault(n => n.Status == WorkflowNodeStatus.Idle && IsReady(workflow, n));
            if (node is null)
            {
                break;
            }

            if (!inputs.TryGetValue(node.Id, out var received) || received.Count == 0)
            {
                node.Status = WorkflowNodeStatus.Skipped;
                onUpdate?.Invoke();
                continue;
            }

            var text = string.Join("\n\n", received);
            node.Status = WorkflowNodeStatus.Running;
            onUpdate?.Invoke();

            string? branch = null;
            try
            {
                node.Output = node.Kind switch
                {
                    WorkflowNodeKind.Agent => await RunAgentAsync(node, text, onUpdate, cancellationToken).ConfigureAwait(false),
                    WorkflowNodeKind.Tool => await RunToolAsync(node, text, cancellationToken).ConfigureAwait(false),
                    WorkflowNodeKind.Transform => (node.Config ?? "{{input}}").Replace("{{input}}", text, StringComparison.Ordinal),
                    _ => text,
                };

                if (node.Kind == WorkflowNodeKind.Condition)
                {
                    branch = !string.IsNullOrEmpty(node.Config) && text.Contains(node.Config, StringComparison.OrdinalIgnoreCase) ? "yes" : "no";
                }

                node.Status = WorkflowNodeStatus.Done;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                node.Status = WorkflowNodeStatus.Failed;
                node.Output = ex.Message;
                onUpdate?.Invoke();
                throw;
            }

            // Conditions pass their input through unchanged; their output shows the branch taken.
            var payload = node.Kind == WorkflowNodeKind.Condition ? text : node.Output ?? string.Empty;
            if (node.Kind == WorkflowNodeKind.Condition)
            {
                node.Output = $"→ {branch}";
            }

            if (node.Kind == WorkflowNodeKind.End)
            {
                result = payload;
            }

            foreach (var next in workflow.Next(node, branch))
            {
                if (!inputs.TryGetValue(next.Id, out var list))
                {
                    inputs[next.Id] = list = [];
                }

                list.Add(payload);
            }

            onUpdate?.Invoke();
        }

        return result.Length > 0 ? result : workflow.Nodes.LastOrDefault(n => n.Status == WorkflowNodeStatus.Done && n.Output is not null)?.Output ?? string.Empty;
    }

    private static bool IsReady(AgentWorkflow workflow, WorkflowNode node) =>
        node.Kind == WorkflowNodeKind.Start
        || workflow.Edges.Where(e => e.To == node.Id)
            .Select(e => workflow.Nodes.First(n => n.Id == e.From).Status)
            .All(s => s is WorkflowNodeStatus.Done or WorkflowNodeStatus.Skipped or WorkflowNodeStatus.Failed);

    private async Task<string> RunAgentAsync(WorkflowNode node, string input, Action? onUpdate, CancellationToken cancellationToken)
    {
        var agent = catalog.Get(node.Target ?? string.Empty) ?? throw new InvalidOperationException($"Node '{node.Title}' has no agent assigned.");
        var prompt = string.IsNullOrWhiteSpace(node.Config) ? input : $"{node.Config}\n\nInput:\n{input}";
        var builder = new StringBuilder();
        await foreach (var token in runtime.StreamAsync(agent, prompt, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            builder.Append(token);
            node.Output = builder.ToString();
            onUpdate?.Invoke();
        }

        return builder.ToString();
    }

    private async Task<string> RunToolAsync(WorkflowNode node, string input, CancellationToken cancellationToken)
    {
        var tool = runtime.Tools.Get(node.Target ?? string.Empty) ?? throw new InvalidOperationException($"Node '{node.Title}' has no tool assigned.");
        var json = (node.Config ?? "{}").Replace("{{input}}", System.Text.Json.JsonEncodedText.Encode(input.Trim()).ToString(), StringComparison.Ordinal);
        var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        var result = await tool.Function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(args), cancellationToken).ConfigureAwait(false);
        return result?.ToString() ?? string.Empty;
    }
}
