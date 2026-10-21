using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Agents;

/// <summary>Wraps a tool so every invocation (arguments, result, duration, errors) lands in the run trace.</summary>
internal sealed class TracingFunction(AIFunction inner, AgentRun run, string agent) : AIFunction
{
    public override string Name => inner.Name;

    public override string Description => inner.Description;

    public override JsonElement JsonSchema => inner.JsonSchema;

    public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

    public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Serialize(arguments.ToDictionary(a => a.Key, a => a.Value), AIJsonUtilities.DefaultOptions);
        run.Add(new AgentStep(AgentStepKind.ToolCall, agent, Name, args));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            run.Add(new AgentStep(AgentStepKind.ToolResult, agent, Name, Format(result)) { Duration = stopwatch.Elapsed });
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Add(new AgentStep(AgentStepKind.ToolResult, agent, Name, ex.Message) { Duration = stopwatch.Elapsed, IsError = true });
            throw;
        }
    }

    private static string Format(object? result) => result switch
    {
        null => "(no result)",
        string s => s,
        JsonElement e => e.ToString(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
    };
}
