using System.Text;
using Blazor.AI.Abstractions;
using Blazor.AI.Components.Infrastructure;
using Blazor.AI.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blazor.AI.ThreeD;

/// <summary>
/// Base for AI-powered 3D components: a <see cref="Scene3D"/> reference, a mutable object list, and helpers to ask the
/// model for narration or structured plans. Geometry, physics and analytics are computed in .NET; the model explains.
/// </summary>
public abstract class ThreeDComponentBase : AiComponentBase
{
    protected Scene3D? Scene;

    /// <summary>Height of the 3D viewport (CSS length).</summary>
    [Parameter] public string Height { get; set; } = "520px";

    /// <summary>Language for AI narration; null follows the model's default.</summary>
    [Parameter] public string? Language { get; set; }

    /// <summary>The objects currently shown. Assign a new array (do not mutate) so the scene diffs it.</summary>
    protected IReadOnlyList<Object3D> Objects { get; set; } = [];

    protected string? Narration { get; set; }

    /// <summary>Streams a short explanation into <see cref="Narration"/>.</summary>
    protected Task NarrateAsync(string role, string facts, string task = AiTasks.Insight) => RunAsync(async ct =>
    {
        var system = $"You are {role}. Explain what the user is looking at in this 3D view in 2-4 short sentences, then one suggestion. " +
                     "Use only the facts given; never invent numbers." + (Language is null ? string.Empty : $" Answer in {Language}.");
        var builder = new StringBuilder();
        Narration = string.Empty;
        await foreach (var token in Ai.StreamAsync(system, PromptTags.Wrap(PromptTags.Input, facts), task, Provider, null, ct))
        {
            builder.Append(token);
            Narration = builder.ToString();
            RenderThrottled();
        }
    });

    /// <summary>Asks for JSON matching <paramref name="schemaExample"/>; returns null if the model's reply is not usable.</summary>
    protected async Task<System.Text.Json.Nodes.JsonNode?> PlanAsync(string system, string input, string schemaExample, CancellationToken ct) =>
        await Ai.GenerateJsonAsync(system + " Reply with JSON only, matching the schema.", PromptTags.Wrap(PromptTags.Input, input) + "\n" + PromptTags.Wrap(PromptTags.Schema, schemaExample), AiTasks.Json, Provider, ct);

    protected static double[] V(double x, double y, double z) => [x, y, z];
}
