using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Blazor.AI.Abstractions;
using Blazor.AI.Core;
using Blazor.AI.Core.Services;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Agents;

public enum OrchestrationMode
{
    /// <summary>Each agent works on the previous agent's output (a pipeline).</summary>
    Sequential,

    /// <summary>Agents take turns on a shared conversation for N rounds (a discussion or debate).</summary>
    RoundRobin,

    /// <summary>A supervisor model picks which agent speaks next until it decides the task is done.</summary>
    Supervisor,
}

/// <summary>A message in a multi-agent conversation.</summary>
public sealed class AgentMessage(string agent, string icon)
{
    public string Agent { get; } = agent;

    public string Icon { get; } = icon;

    public string Text { get; set; } = string.Empty;

    public bool IsComplete { get; set; }

    public AgentRun? Run { get; set; }

    public string? Note { get; set; }
}

/// <summary>Coordinates several agents on one task.</summary>
public sealed class MultiAgentOrchestrator(AgentRuntime runtime, AiTextService ai)
{
    public async IAsyncEnumerable<AgentMessage> RunAsync(
        string task,
        IReadOnlyList<AgentDefinition> agents,
        OrchestrationMode mode,
        int rounds = 2,
        string? supervisorProvider = null,
        Action<AgentMessage>? onUpdate = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (agents.Count == 0)
        {
            yield break;
        }

        var transcript = new List<(string Agent, string Text)>();
        switch (mode)
        {
            case OrchestrationMode.Sequential:
                var input = task;
                foreach (var agent in agents)
                {
                    var prompt = transcript.Count == 0
                        ? task
                        : $"Task: {task}\n\nPrevious step by {transcript[^1].Agent}:\n{input}\n\nDo your part ({agent.Role}).";
                    var message = await Speak(agent, prompt, onUpdate, cancellationToken).ConfigureAwait(false);
                    yield return message;
                    input = message.Text;
                    transcript.Add((agent.Name, message.Text));
                }

                break;

            case OrchestrationMode.RoundRobin:
                for (var round = 0; round < Math.Max(1, rounds); round++)
                {
                    foreach (var agent in agents)
                    {
                        var message = await Speak(agent, Discussion(task, transcript, agent), onUpdate, cancellationToken).ConfigureAwait(false);
                        message.Note = $"Round {round + 1}";
                        yield return message;
                        transcript.Add((agent.Name, message.Text));
                    }
                }

                break;

            case OrchestrationMode.Supervisor:
                for (var turn = 0; turn < Math.Max(2, rounds * agents.Count); turn++)
                {
                    var (next, reason, done) = await ChooseNextAsync(task, agents, transcript, supervisorProvider, cancellationToken).ConfigureAwait(false);
                    if (done)
                    {
                        var final = new AgentMessage("Supervisor", "bi-diagram-3") { Text = reason, IsComplete = true, Note = "Task complete" };
                        yield return final;
                        yield break;
                    }

                    var message = await Speak(next, Discussion(task, transcript, next), onUpdate, cancellationToken).ConfigureAwait(false);
                    message.Note = $"Chosen by supervisor: {reason}";
                    yield return message;
                    transcript.Add((next.Name, message.Text));
                }

                break;
        }
    }

    private async Task<AgentMessage> Speak(AgentDefinition agent, string prompt, Action<AgentMessage>? onUpdate, CancellationToken cancellationToken)
    {
        var message = new AgentMessage(agent.Name, agent.Icon);
        onUpdate?.Invoke(message);
        var builder = new StringBuilder();
        await foreach (var token in runtime.StreamAsync(agent, prompt, onRun: run => message.Run = run, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            builder.Append(token);
            message.Text = builder.ToString();
            onUpdate?.Invoke(message);
        }

        message.IsComplete = true;
        return message;
    }

    private static string Discussion(string task, List<(string Agent, string Text)> transcript, AgentDefinition agent)
    {
        if (transcript.Count == 0)
        {
            return task;
        }

        var builder = new StringBuilder("Task: ").AppendLine(task).AppendLine().AppendLine("Conversation so far:");
        foreach (var (name, text) in transcript.TakeLast(8))
        {
            builder.Append("### ").AppendLine(name).AppendLine(text).AppendLine();
        }

        builder.Append("Now contribute as ").Append(agent.Name).Append(" (").Append(agent.Role).Append("). Build on what was said; do not repeat it.");
        return builder.ToString();
    }

    private async Task<(AgentDefinition Next, string Reason, bool Done)> ChooseNextAsync(string task, IReadOnlyList<AgentDefinition> agents, List<(string Agent, string Text)> transcript, string? provider, CancellationToken cancellationToken)
    {
        var roster = string.Join("\n", agents.Select(a => $"- {a.Name}: {a.Role}"));
        var system = "You supervise a team of agents. Pick who should act next, or finish when the task is fully done. " +
                     "Reply with JSON only: {\"next\": agent name or \"FINISH\", \"reason\": short reason}. " +
                     "When finishing, \"reason\" must be the final answer for the user, assembled from the team's work.";
        var history = string.Join("\n\n", transcript.TakeLast(6).Select(t => $"### {t.Agent}\n{t.Text}"));
        var user = $"Team:\n{roster}\n\nTask: {task}\n\n" + (history.Length == 0 ? "Nobody has spoken yet." : "Work so far:\n" + history);
        var node = await ai.GenerateJsonAsync(system, PromptTags.Wrap(PromptTags.Input, user), AiTasks.Plan, provider, cancellationToken).ConfigureAwait(false) as JsonObject;

        var next = node?["next"]?.ToString();
        var reason = node?["reason"]?.ToString() ?? string.Empty;

        // Offline/weak-model fallback: walk the roster once, then finish with the last contribution.
        if (next is null || (agents.All(a => !a.Name.Equals(next, StringComparison.OrdinalIgnoreCase)) && !next.Equals("FINISH", StringComparison.OrdinalIgnoreCase)))
        {
            if (transcript.Count >= agents.Count)
            {
                return (agents[0], transcript[^1].Text, true);
            }

            return (agents[transcript.Count], "next in the roster", false);
        }

        if (next.Equals("FINISH", StringComparison.OrdinalIgnoreCase))
        {
            return (agents[0], reason.Length > 0 ? reason : transcript.LastOrDefault().Text ?? string.Empty, true);
        }

        return (agents.First(a => a.Name.Equals(next, StringComparison.OrdinalIgnoreCase)), reason, false);
    }
}
