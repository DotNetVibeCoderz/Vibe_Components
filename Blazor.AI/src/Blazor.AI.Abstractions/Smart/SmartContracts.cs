namespace Blazor.AI.Abstractions.Smart;

/// <summary>Describes one form field SmartPaste should fill.</summary>
/// <param name="Name">Stable field identifier (the input's name or id).</param>
/// <param name="Description">Human description that helps the model map clipboard text to the field.</param>
/// <param name="Type">Value type hint: <c>string</c>, <c>number</c>, <c>boolean</c>, <c>date</c>, <c>email</c>, <c>phone</c>.</param>
/// <param name="AllowedValues">For selects and radio groups, the only values the model may return.</param>
public sealed record SmartPasteField(
    string Name,
    string? Description = null,
    string Type = "string",
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>The values SmartPaste extracted, keyed by field name. Missing fields are omitted.</summary>
public sealed record SmartPasteResult(IReadOnlyDictionary<string, string> Values, string? RawResponse = null)
{
    public static SmartPasteResult Empty { get; } = new(new Dictionary<string, string>());
}

/// <summary>Extracts structured form values from unstructured text (usually the clipboard).</summary>
public interface ISmartPasteService
{
    Task<SmartPasteResult> ExtractAsync(string text, IReadOnlyList<SmartPasteField> fields, string? provider = null, CancellationToken cancellationToken = default);
}

/// <summary>Context for inline completions in <c>SmartTextArea</c>.</summary>
/// <param name="Role">Who is typing, e.g. "Customer support agent for an airline".</param>
/// <param name="UserPhrases">Phrases the user often types; the model prefers these.</param>
/// <param name="Tone">Optional tone, e.g. "friendly" or "formal".</param>
public sealed record SmartTextAreaContext(
    string? Role = null,
    IReadOnlyList<string>? UserPhrases = null,
    string? Tone = null);

/// <summary>Predicts the continuation of what the user is typing.</summary>
public interface ISmartTextAreaService
{
    Task<string?> SuggestAsync(string textBeforeCursor, string textAfterCursor, SmartTextAreaContext context, string? provider = null, CancellationToken cancellationToken = default);
}

/// <summary>A candidate ranked by semantic similarity.</summary>
public readonly record struct SemanticMatch<T>(T Item, string Text, double Score);

/// <summary>Ranks candidates by semantic similarity to a query (embeddings, not substring match).</summary>
public interface ISemanticMatcher
{
    Task<IReadOnlyList<SemanticMatch<T>>> MatchAsync<T>(
        string query,
        IEnumerable<T> candidates,
        Func<T, string> text,
        int top = 10,
        double minScore = 0.0,
        string? provider = null,
        CancellationToken cancellationToken = default);
}
