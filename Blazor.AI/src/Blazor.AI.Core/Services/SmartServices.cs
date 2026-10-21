using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Blazor.AI.Abstractions;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Smart;
using Blazor.AI.Core.Vectors;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Services;

/// <summary>SmartPaste: maps unstructured text onto form fields with one structured LLM call.</summary>
public sealed class SmartPasteService(AiTextService text) : ISmartPasteService
{
    public async Task<SmartPasteResult> ExtractAsync(string input, IReadOnlyList<SmartPasteField> fields, string? provider = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input) || fields.Count == 0)
        {
            return SmartPasteResult.Empty;
        }

        var spec = new StringBuilder("[");
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            spec.Append(i == 0 ? string.Empty : ",")
                .Append("{\"name\":").Append(JsonSerializer.Serialize(f.Name, CoreJsonContext.Default.String))
                .Append(",\"type\":\"").Append(f.Type).Append('"');
            if (!string.IsNullOrWhiteSpace(f.Description))
            {
                spec.Append(",\"description\":").Append(JsonSerializer.Serialize(f.Description, CoreJsonContext.Default.String));
            }

            if (f.AllowedValues is { Count: > 0 })
            {
                spec.Append(",\"allowed\":").Append(JsonSerializer.Serialize(f.AllowedValues.ToList(), CoreJsonContext.Default.ListString));
            }

            spec.Append('}');
        }

        spec.Append(']');

        var system = "You fill in web forms from pasted text. Return one JSON object whose keys are the field names. " +
                     "Only include fields the text clearly supports; never guess. Dates as yyyy-MM-dd, numbers without units, " +
                     "booleans as true/false. For fields with 'allowed' values use exactly one of them. Today is " +
                     DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".";
        var user = PromptTags.Wrap(PromptTags.Fields, spec.ToString()) + "\n" + PromptTags.Wrap(PromptTags.Input, input);
        var raw = await text.CompleteAsync(system, user, AiTasks.SmartPaste, provider, new ChatOptions { Temperature = 0f }, cancellationToken).ConfigureAwait(false);

        var values = LlmJson.ToStringMap(LlmJson.ParseNode(raw));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!values.TryGetValue(field.Name, out var value))
            {
                continue;
            }

            if (field.AllowedValues is { Count: > 0 } allowed)
            {
                value = allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase))
                        ?? allowed.FirstOrDefault(a => value.Contains(a, StringComparison.OrdinalIgnoreCase)
                                                       || a.Contains(value, StringComparison.OrdinalIgnoreCase));
                if (value is null)
                {
                    continue;
                }
            }

            result[field.Name] = value;
        }

        return new SmartPasteResult(result, raw);
    }
}

/// <summary>SmartTextArea: short, low-latency continuations of what the user is typing.</summary>
public sealed class SmartTextAreaService(AiTextService text) : ISmartTextAreaService
{
    public async Task<string?> SuggestAsync(string textBeforeCursor, string textAfterCursor, SmartTextAreaContext context, string? provider = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(textBeforeCursor) || textBeforeCursor.Length < 3)
        {
            return null;
        }

        var system = new StringBuilder("Predict how the user's text continues. Reply with ONLY the continuation (at most one sentence, ")
            .Append("no quotes, no explanation). Reply with an empty string when unsure.");
        if (context.Role is not null)
        {
            system.Append(" The user is: ").Append(context.Role).Append('.');
        }

        if (context.Tone is not null)
        {
            system.Append(" Tone: ").Append(context.Tone).Append('.');
        }

        if (context.UserPhrases is { Count: > 0 })
        {
            system.Append(" Prefer these phrases when relevant: ").AppendJoin(" | ", context.UserPhrases).Append('.');
        }

        // Keep the prompt small: completions only need local context.
        var before = textBeforeCursor.Length > 1_500 ? textBeforeCursor[^1_500..] : textBeforeCursor;
        var after = textAfterCursor.Length > 300 ? textAfterCursor[..300] : textAfterCursor;
        var user = PromptTags.Wrap(PromptTags.Input, before) + "\n" + PromptTags.Wrap(PromptTags.Context, after);

        var suggestion = await text.CompleteAsync(system.ToString(), user, AiTasks.SmartTextArea, provider, new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 40 }, cancellationToken).ConfigureAwait(false);
        suggestion = suggestion.Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(suggestion))
        {
            return null;
        }

        // Ensure a separating space when the model omitted it.
        return char.IsLetterOrDigit(before[^1]) && char.IsLetterOrDigit(suggestion[0]) ? " " + suggestion : suggestion;
    }
}

/// <summary>Embedding-based ranking with a per-provider embedding cache (candidate lists are usually static).</summary>
public sealed class SemanticMatcher(IAiClientFactory factory) : ISemanticMatcher
{
    private const int MaxCacheEntries = 20_000;
    private readonly ConcurrentDictionary<(string Provider, string Text), float[]> _cache = new();

    public async Task<IReadOnlyList<SemanticMatch<T>>> MatchAsync<T>(string query, IEnumerable<T> candidates, Func<T, string> text, int top = 10, double minScore = 0, string? provider = null, CancellationToken cancellationToken = default)
    {
        var items = candidates.Select(c => (Item: c, Text: text(c))).ToList();
        if (string.IsNullOrWhiteSpace(query) || items.Count == 0)
        {
            return [];
        }

        var key = provider ?? factory.DefaultProvider.Name;
        var missing = items.Select(i => i.Text).Append(query).Distinct().Where(t => !_cache.ContainsKey((key, t))).ToList();
        if (missing.Count > 0)
        {
            var generator = factory.GetEmbeddingGenerator(provider);
            var embeddings = await generator.GenerateAsync(missing, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (_cache.Count + missing.Count > MaxCacheEntries)
            {
                _cache.Clear();
            }

            for (var i = 0; i < missing.Count; i++)
            {
                _cache[(key, missing[i])] = embeddings[i].Vector.ToArray();
            }
        }

        var q = _cache[(key, query)];
        return items
            .Select(i => new SemanticMatch<T>(i.Item, i.Text, VectorMath.Cosine(q, _cache[(key, i.Text)])))
            .Where(m => m.Score >= minScore)
            .OrderByDescending(m => m.Score)
            .Take(top)
            .ToArray();
    }
}
