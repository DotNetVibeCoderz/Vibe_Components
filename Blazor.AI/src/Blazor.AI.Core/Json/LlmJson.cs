using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazor.AI.Core;

/// <summary>Robust helpers for pulling JSON out of model output (code fences, chatter before or after, trailing commas).</summary>
public static class LlmJson
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Returns the first JSON object or array found in <paramref name="text"/>, or <c>null</c>.</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var span = text.AsSpan();
        var fence = span.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var body = span[(fence + 3)..];
            var newline = body.IndexOf('\n');
            if (newline >= 0)
            {
                body = body[(newline + 1)..];
            }

            var close = body.IndexOf("```", StringComparison.Ordinal);
            if (close >= 0)
            {
                var candidate = Balanced(body[..close]);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return Balanced(span);
    }

    public static JsonNode? ParseNode(string? text)
    {
        var json = ExtractJson(text);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json, documentOptions: Lenient);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Flattens a JSON object's top-level values to strings. Arrays become comma-separated lists.</summary>
    public static Dictionary<string, string> ToStringMap(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject obj)
        {
            return map;
        }

        foreach (var (key, value) in obj)
        {
            var text = value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray a => string.Join(", ", a.Select(x => x?.ToString())),
                _ => value.ToJsonString(),
            };

            if (!string.IsNullOrWhiteSpace(text) && !text.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                map[key] = text.Trim();
            }
        }

        return map;
    }

    private static string? Balanced(ReadOnlySpan<char> span)
    {
        var start = span.IndexOfAny('{', '[');
        while (start >= 0)
        {
            var open = span[start];
            var close = open == '{' ? '}' : ']';
            var depth = 0;
            var inString = false;
            for (var i = start; i < span.Length; i++)
            {
                var c = span[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == open)
                {
                    depth++;
                }
                else if (c == close && --depth == 0)
                {
                    return span[start..(i + 1)].ToString();
                }
            }

            var next = span[(start + 1)..].IndexOfAny('{', '[');
            start = next < 0 ? -1 : start + 1 + next;
        }

        return null;
    }
}
