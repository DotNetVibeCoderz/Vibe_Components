using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Blazor.AI.Abstractions;
using Blazor.AI.Core.Services;
using Blazor.AI.Core.Simulated;
using Blazor.AI.Visualization;

namespace Blazor.AI.RAG;

/// <summary>Extracts entities and relations from text with an LLM, falling back to co-occurrence when the model returns nothing.</summary>
public sealed partial class KnowledgeGraphExtractor(AiTextService ai)
{
    public async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ExtractAsync(string text, int maxEntities = 30, string? provider = null, CancellationToken cancellationToken = default)
    {
        // Keep prompts bounded; long documents are sampled from the start, which usually introduces the key entities.
        var input = text.Length > 12_000 ? text[..12_000] : text;
        var system = $"Extract a knowledge graph. Find at most {maxEntities} important entities (people, organisations, places, products, concepts, events) " +
                     "and the relations between them. Reply with JSON only: {\"entities\": [{\"id\": short-kebab-id, \"name\": string, \"type\": " +
                     "\"Person\"|\"Organization\"|\"Place\"|\"Product\"|\"Concept\"|\"Event\", \"description\": one sentence}], " +
                     "\"relations\": [{\"source\": id, \"target\": id, \"label\": verb phrase}]}.";
        var node = await ai.GenerateJsonAsync(system, PromptTags.Wrap(PromptTags.Input, input), AiTasks.Extract, provider, cancellationToken).ConfigureAwait(false);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        if (node?["entities"] is JsonArray entities)
        {
            foreach (var e in entities.OfType<JsonObject>())
            {
                var id = e["id"]?.ToString() ?? e["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(id) || nodes.Any(n => n.Id == id))
                {
                    continue;
                }

                nodes.Add(new GraphNode(id, e["name"]?.ToString() ?? id, e["type"]?.ToString()) { Description = e["description"]?.ToString() });
            }

            var ids = nodes.Select(n => n.Id).ToHashSet();
            foreach (var r in (node["relations"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var source = r["source"]?.ToString();
                var target = r["target"]?.ToString();
                if (source is not null && target is not null && ids.Contains(source) && ids.Contains(target))
                {
                    edges.Add(new GraphEdge(source, target, r["label"]?.ToString()));
                }
            }
        }

        return nodes.Count >= 2 ? (nodes, edges) : CoOccurrence(input, maxEntities);
    }

    [GeneratedRegex(@"\b(?:[A-Z][\w\-]+(?:\s+(?:of|de|van|bin|dan)\s+|\s+)?){1,4}", RegexOptions.CultureInvariant)]
    private static partial Regex CapitalisedPhrase();

    /// <summary>Offline fallback: capitalised phrases are entities; appearing in the same sentence relates them.</summary>
    public static (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) CoOccurrence(string text, int maxEntities = 30)
    {
        var sentences = TextHeuristics.Sentences(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var perSentence = new List<List<string>>();
        foreach (var sentence in sentences)
        {
            var found = CapitalisedPhrase().Matches(sentence)
                .Select(m => m.Value.Trim())
                .Where(v => v.Length > 2 && !StopEntities.Contains(v))
                .Distinct()
                .ToList();
            perSentence.Add(found);
            foreach (var f in found)
            {
                counts[f] = counts.GetValueOrDefault(f) + 1;
            }
        }

        var top = counts.OrderByDescending(c => c.Value).Take(maxEntities).Select(c => c.Key).ToHashSet();
        var nodes = top.Select(t => new GraphNode(t, t, "Entity", 1 + Math.Log(counts[t]))).ToList();
        var edges = new Dictionary<(string, string), int>();
        foreach (var found in perSentence)
        {
            var present = found.Where(top.Contains).ToList();
            for (var i = 0; i < present.Count; i++)
            {
                for (var j = i + 1; j < present.Count; j++)
                {
                    var key = string.CompareOrdinal(present[i], present[j]) < 0 ? (present[i], present[j]) : (present[j], present[i]);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }
        }

        return (nodes, edges.Select(e => new GraphEdge(e.Key.Item1, e.Key.Item2, "co-occurs", e.Value)).ToList());
    }

    private static readonly HashSet<string> StopEntities = new(StringComparer.Ordinal)
    {
        "The", "This", "That", "These", "Those", "In", "On", "At", "It", "We", "They", "He", "She", "A", "An", "And", "But", "If", "When",
        "Dalam", "Pada", "Ini", "Itu", "Yang", "Dan", "Untuk", "Kami", "Saya",
    };
}
