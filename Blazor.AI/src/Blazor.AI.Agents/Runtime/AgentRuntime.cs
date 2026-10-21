using System.Runtime.CompilerServices;
using System.Text;
using Blazor.AI.Abstractions;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Core.Services;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Agents;

/// <summary>
/// Runs agents: builds the tool list (registry + memory), wraps tools for tracing, and drives the
/// Microsoft.Extensions.AI function-invocation loop with the agent's iteration limit.
/// </summary>
public sealed class AgentRuntime(IAiClientFactory factory, ToolRegistry tools, AgentMemory memory)
{
    private readonly List<AgentRun> _history = [];
    private readonly Lock _gate = new();

    public ToolRegistry Tools => tools;

    public AgentMemory Memory => memory;

    /// <summary>Recent runs, newest first (for AgentDebugger).</summary>
    public IReadOnlyList<AgentRun> History
    {
        get
        {
            lock (_gate)
            {
                return _history.AsEnumerable().Reverse().ToArray();
            }
        }
    }

    public event Action<AgentRun>? RunStarted;

    public event Action<AgentRun>? RunCompleted;

    public async Task<AgentRun> RunAsync(AgentDefinition agent, string input, IReadOnlyList<ChatMessage>? history = null, CancellationToken cancellationToken = default)
    {
        AgentRun? run = null;
        await foreach (var _ in StreamAsync(agent, input, history, r => run = r, cancellationToken).ConfigureAwait(false))
        {
        }

        return run!;
    }

    /// <summary>Streams the agent's answer. <paramref name="onRun"/> receives the run object first, so callers can watch its trace live.</summary>
    public async IAsyncEnumerable<string> StreamAsync(AgentDefinition agent, string input, IReadOnlyList<ChatMessage>? history = null, Action<AgentRun>? onRun = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = new AgentRun { AgentName = agent.Name, Input = input };
        lock (_gate)
        {
            _history.Add(run);
            if (_history.Count > 100)
            {
                _history.RemoveAt(0);
            }
        }

        onRun?.Invoke(run);
        RunStarted?.Invoke(run);
        run.Add(new AgentStep(AgentStepKind.Input, agent.Name, "Input", input));

        var functions = tools.Resolve(agent.Tools).OfType<AIFunction>().ToList();
        if (agent.UseMemory)
        {
            functions.AddRange(memory.ToolsFor(agent.Name));
        }

        var options = AiChatOptions.ForTask(AiTasks.Agent, agent.Temperature);
        if (functions.Count > 0)
        {
            options.Tools = [.. functions.Select(f => (AITool)new TracingFunction(f, run, agent.Name))];
        }

        var client = new ChatClientBuilder(factory.GetChatClient(agent.Provider))
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = Math.Max(1, agent.MaxIterations);
                f.IncludeDetailedErrors = true;
            })
            .Build();

        var messages = new List<ChatMessage> { new(ChatRole.System, BuildSystemPrompt(agent, functions)) };
        if (history is not null)
        {
            messages.AddRange(history);
        }

        messages.Add(new ChatMessage(ChatRole.User, input));

        var output = new StringBuilder();
        var stream = client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    run.Failed = true;
                    run.Add(new AgentStep(AgentStepKind.Error, agent.Name, "Error", ex.Message) { IsError = true });
                    throw;
                }

                if (update.Contents.OfType<UsageContent>().FirstOrDefault() is { } usage)
                {
                    run.InputTokens += usage.Details.InputTokenCount ?? 0;
                    run.OutputTokens += usage.Details.OutputTokenCount ?? 0;
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    output.Append(update.Text);
                    run.Output = output.ToString();
                    yield return update.Text;
                }
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            run.CompletedAt = DateTimeOffset.Now;
            if (!run.Failed)
            {
                run.Add(new AgentStep(AgentStepKind.Output, agent.Name, "Answer", run.Output) { Duration = run.Elapsed });
            }

            RunCompleted?.Invoke(run);
        }
    }

    private static string BuildSystemPrompt(AgentDefinition agent, IReadOnlyList<AIFunction> functions)
    {
        var builder = new StringBuilder(agent.Instructions.Trim());
        builder.Append("\n\nYou are ").Append(agent.Name).Append(", ").Append(agent.Role).Append('.');
        if (functions.Count > 0)
        {
            builder.Append(" Use your tools whenever they give a more accurate answer than guessing (always use calculate for arithmetic).");
        }

        if (agent.UseMemory)
        {
            builder.Append(" Call recall at the start when past context may help, and remember durable facts the user shares.");
        }

        builder.Append(" Today is ").Append(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('.');
        return builder.ToString();
    }
}
