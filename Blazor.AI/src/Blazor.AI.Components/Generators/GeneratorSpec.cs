namespace Blazor.AI.Components;

public enum GeneratorFieldKind
{
    Text,
    TextArea,
    Select,
    Number,
}

/// <summary>One input of an <see cref="AiGenerator"/> form.</summary>
public sealed record GeneratorField(
    string Name,
    string Label,
    GeneratorFieldKind Kind = GeneratorFieldKind.Text,
    string? Placeholder = null,
    string? Default = null,
    IReadOnlyList<string>? Options = null,
    bool Required = false,
    bool Wide = false);

/// <summary>Declarative description of a generator: its form, prompts and output.</summary>
/// <remarks>The user prompt is <see cref="Template"/> with <c>{{field}}</c> placeholders replaced by form values.</remarks>
public sealed record GeneratorSpec
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public string Icon { get; init; } = "bi-stars";

    public required string SystemPrompt { get; init; }

    public required string Template { get; init; }

    public IReadOnlyList<GeneratorField> Fields { get; init; } = [];

    public string Task { get; init; } = Blazor.AI.Abstractions.AiTasks.Generate;

    public string ActionLabel { get; init; } = "Generate";

    public string DownloadName { get; init; } = "output.md";

    public float? Temperature { get; init; } = 0.7f;

    /// <summary>Follow-up rewrites offered after the first result, e.g. "Make it shorter".</summary>
    public IReadOnlyList<string> Refinements { get; init; } = [];
}
