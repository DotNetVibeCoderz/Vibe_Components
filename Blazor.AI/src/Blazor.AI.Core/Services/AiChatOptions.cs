using Blazor.AI.Abstractions;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Services;

/// <summary>Helpers for building <see cref="ChatOptions"/> tagged with a Blazor.AI task.</summary>
public static class AiChatOptions
{
    public static ChatOptions ForTask(string task, float? temperature = null, int? maxOutputTokens = null, ChatResponseFormat? format = null)
    {
        var options = new ChatOptions
        {
            Temperature = temperature,
            MaxOutputTokens = maxOutputTokens,
            ResponseFormat = format,
            AdditionalProperties = new AdditionalPropertiesDictionary { [AiTasks.PropertyKey] = task },
        };
        return options;
    }

    public static string? GetTask(this ChatOptions? options) =>
        options?.AdditionalProperties is { } props && props.TryGetValue(AiTasks.PropertyKey, out var value) ? value?.ToString() : null;

    public static ChatOptions WithTask(this ChatOptions options, string task)
    {
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[AiTasks.PropertyKey] = task;
        return options;
    }
}

/// <summary>Tag helpers so prompts carry machine-readable sections (<c>&lt;input&gt;</c>, <c>&lt;labels&gt;</c>...).</summary>
public static class PromptTags
{
    public const string Input = "input";
    public const string Labels = "labels";
    public const string Fields = "fields";
    public const string Target = "target";
    public const string Context = "context";
    public const string Schema = "schema";

    public static string Wrap(string tag, string? value) => $"<{tag}>\n{value}\n</{tag}>";

    /// <summary>Reads the content of the first <c>&lt;tag&gt;...&lt;/tag&gt;</c> section.</summary>
    public static string? Read(string? text, string tag)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var open = $"<{tag}>";
        var close = $"</{tag}>";
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }
}
