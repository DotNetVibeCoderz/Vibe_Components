using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blazor.AI.ThreeD;

/// <summary>An entity in an extracted graph.</summary>
public sealed record GraphEntity(string Name, string Type);

/// <summary>A directed relation between two entities.</summary>
public sealed record GraphRelation(string Source, string Target, string Relation);

/// <summary>Entities and relations extracted from text.</summary>
public sealed record ExtractedGraph(IReadOnlyList<GraphEntity> Entities, IReadOnlyList<GraphRelation> Relations, bool FromModel)
{
    public int Degree(string name) => Relations.Count(r => r.Source == name || r.Target == name);
}

/// <summary>Turns text into a graph: the model's JSON when it is usable, otherwise a deterministic local extractor.</summary>
public static partial class GraphExtraction
{
    public const string Schema = """{"entities":[{"name":"Entity name","type":"Person|Place|Organization|Concept|Event|Date|Thing"}],"relations":[{"source":"Entity name","target":"Other entity","relation":"verb phrase"}]}""";

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "A", "An", "It", "Its", "This", "That", "These", "Those", "In", "On", "At", "By", "For", "From", "With", "And", "But", "Or", "As", "Of", "To",
        "Yang", "Di", "Ke", "Dari", "Dan", "Pada", "Ini", "Itu", "Dengan", "Untuk", "Oleh", "Sebagai", "Dalam", "Para", "Sejak", "Kini", "Today", "Later", "When", "After",
    };

    [GeneratedRegex(@"\b(?:[A-Z][\p{L}\-']+|[A-Z]{2,})(?:\s+(?:of|de|van|bin|binti|dan|[A-Z][\p{L}\-']+|[A-Z]{2,}|\d+))*", RegexOptions.CultureInvariant)]
    private static partial Regex CapitalisedPhrase();

    [GeneratedRegex(@"\b(1[0-9]{3}|20[0-9]{2}|abad ke-\d+|\d{1,2}(?:st|nd|rd|th) century)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplit();

    public static ExtractedGraph? FromJson(JsonNode? node, string sourceText)
    {
        if (node?["entities"] is not JsonArray entities || node["relations"] is not JsonArray relations)
        {
            return null;
        }

        var list = entities.Select(e => (Name: e?["name"]?.GetValue<string>()?.Trim(), Type: e?["type"]?.GetValue<string>()?.Trim()))
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Name!.Length < 60)
            .GroupBy(e => e.Name!, StringComparer.OrdinalIgnoreCase).Select(g => new GraphEntity(g.First().Name!, NormalizeType(g.First().Type))).ToList();
        var names = list.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rels = relations.Select(r => new GraphRelation(r?["source"]?.GetValue<string>()?.Trim() ?? "", r?["target"]?.GetValue<string>()?.Trim() ?? "", r?["relation"]?.GetValue<string>()?.Trim() ?? "related to"))
            .Where(r => names.Contains(r.Source) && names.Contains(r.Target) && !string.Equals(r.Source, r.Target, StringComparison.OrdinalIgnoreCase))
            .Select(r => r with { Source = list.First(e => e.Name.Equals(r.Source, StringComparison.OrdinalIgnoreCase)).Name, Target = list.First(e => e.Name.Equals(r.Target, StringComparison.OrdinalIgnoreCase)).Name })
            .Distinct().ToList();

        // Reject template echoes and graphs whose entities do not occur in the text (a simulated or confused model).
        var grounded = list.Count(e => sourceText.Contains(e.Name, StringComparison.OrdinalIgnoreCase));
        if (list.Count < 3 || rels.Count < 2 || grounded < list.Count * 0.6 || list.Any(e => e.Name == "Entity name"))
        {
            return null;
        }

        return new ExtractedGraph(list, rels, true);
    }

    /// <summary>Capitalised phrases and dates become entities; entities in the same sentence are linked by the words between them.</summary>
    public static ExtractedGraph Local(string text, int maxEntities = 28)
    {
        var entities = new Dictionary<string, GraphEntity>(StringComparer.OrdinalIgnoreCase);
        var relations = new List<GraphRelation>();
        foreach (var sentence in SentenceSplit().Split(text))
        {
            var found = new List<(string Name, int Index, int End)>();
            foreach (Match m in CapitalisedPhrase().Matches(sentence))
            {
                var name = TrimStop(m.Value);
                if (name.Length < 3 || Stop.Contains(name))
                {
                    continue;
                }

                found.Add((name, m.Index + (m.Value.Length - m.Value.TrimStart().Length), m.Index + m.Length));
            }

            foreach (Match m in DatePattern().Matches(sentence))
            {
                found.Add((m.Value, m.Index, m.Index + m.Length));
            }

            found = found.OrderBy(f => f.Index).GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            foreach (var f in found)
            {
                if (!entities.ContainsKey(f.Name) && entities.Count < maxEntities)
                {
                    entities[f.Name] = new GraphEntity(f.Name, GuessType(f.Name, sentence));
                }
            }

            for (var i = 0; i < found.Count; i++)
            {
                for (var j = i + 1; j < Math.Min(found.Count, i + 3); j++)
                {
                    if (!entities.ContainsKey(found[i].Name) || !entities.ContainsKey(found[j].Name))
                    {
                        continue;
                    }

                    var between = found[j].Index > found[i].End ? sentence[found[i].End..found[j].Index] : string.Empty;
                    relations.Add(new GraphRelation(entities[found[i].Name].Name, entities[found[j].Name].Name, RelationLabel(between)));
                }
            }
        }

        return new ExtractedGraph(entities.Values.ToList(), relations.Distinct().ToList(), false);
    }

    private static string TrimStop(string phrase)
    {
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && Stop.Contains(words[0]))
        {
            words.RemoveAt(0);
        }

        while (words.Count > 0 && Stop.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }

    private static string RelationLabel(string between)
    {
        var words = between.Split([' ', ',', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !w.All(char.IsDigit)).Take(4).ToArray();
        return words.Length == 0 ? "related to" : string.Join(' ', words).Trim().ToLowerInvariant();
    }

    private static string GuessType(string name, string sentence)
    {
        if (DatePattern().IsMatch(name))
        {
            return "Date";
        }

        var lower = name.ToLowerInvariant();
        string[] org = ["unesco", "university", "universitas", "institut", "company", "pt ", "bank", "ministry", "kementerian", "dinasti", "dynasty", "kerajaan", "kingdom", "pemerintah", "foundation"];
        string[] place = ["java", "jawa", "bali", "jakarta", "yogyakarta", "magelang", "sumatra", "indonesia", "kabupaten", "city", "kota", "island", "pulau", "mount", "gunung", "river", "sungai", "temple", "candi"];
        if (org.Any(lower.Contains))
        {
            return "Organization";
        }

        if (place.Any(lower.Contains))
        {
            return "Place";
        }

        var idx = sentence.IndexOf(name, StringComparison.Ordinal);
        var before = idx > 0 ? sentence[Math.Max(0, idx - 12)..idx].ToLowerInvariant() : string.Empty;
        if (before.Contains(" in ") || before.Contains(" di ") || before.Contains(" at ") || before.Contains(" near "))
        {
            return "Place";
        }

        return name.Split(' ').Length is 2 or 3 && name.Split(' ').All(w => char.IsUpper(w[0]) && w.Length > 2 && !w.All(char.IsUpper)) ? "Person" : "Concept";
    }

    private static string NormalizeType(string? type) => type?.ToLowerInvariant() switch
    {
        "person" or "people" or "tokoh" => "Person",
        "place" or "location" or "tempat" or "lokasi" => "Place",
        "organization" or "organisation" or "org" or "company" => "Organization",
        "event" or "peristiwa" => "Event",
        "date" or "time" or "year" => "Date",
        "thing" or "object" or "product" => "Thing",
        _ => "Concept",
    };
}
