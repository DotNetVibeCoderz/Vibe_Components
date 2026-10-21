using Markdig;
using Microsoft.AspNetCore.Components;

namespace Blazor.AI.Components.Infrastructure;

/// <summary>Markdown → HTML for model output. Raw HTML in the input is disabled, so output cannot inject markup.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString Render(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? default : new MarkupString(Markdown.ToHtml(markdown, Pipeline));

    public static string ToHtml(string? markdown) => string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);
}
