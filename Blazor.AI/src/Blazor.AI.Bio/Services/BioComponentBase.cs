using System.Text;
using Blazor.AI.Abstractions;
using Blazor.AI.Components.Infrastructure;
using Blazor.AI.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blazor.AI.Bio;

/// <summary>
/// Base for biology components: deterministic analysis runs locally with Biotech.Net; the optional
/// "Explain with AI" step sends only the computed results (never raw patient data) to the model.
/// </summary>
public abstract class BioComponentBase : AiComponentBase
{
    /// <summary>Who the explanation is for, e.g. "a molecular biology student" or "a lab manager".</summary>
    [Parameter] public string Audience { get; set; } = "a molecular biologist";

    /// <summary>Language of the AI explanation; null follows the model's default.</summary>
    [Parameter] public string? Language { get; set; }

    protected string? Explanation { get; set; }

    /// <summary>Streams an explanation of <paramref name="results"/> into <see cref="Explanation"/>.</summary>
    protected Task ExplainAsync(string analysis, string results) => RunAsync(async ct =>
    {
        var system = $"You are a senior bioinformatician explaining a {analysis} to {Audience}. Interpret the computed results: what they mean, " +
                     "what stands out, caveats of the method, and one suggested next experiment or analysis. Use only the numbers given; never invent values. " +
                     "Be concise, use markdown." + (Language is null ? string.Empty : $" Answer in {Language}.");
        var builder = new StringBuilder();
        Explanation = string.Empty;
        await foreach (var token in Ai.StreamAsync(system, PromptTags.Wrap(PromptTags.Input, results), AiTasks.Insight, Provider, null, ct))
        {
            builder.Append(token);
            Explanation = builder.ToString();
            RenderThrottled();
        }
    });

    /// <summary>Runs CPU-bound Biotech.Net work off the render thread and captures format errors as messages.</summary>
    protected async Task<T?> AnalyseAsync<T>(Func<T> work)
        where T : class
    {
        Error = null;
        try
        {
            return await Task.Run(work);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return null;
        }
    }
}
