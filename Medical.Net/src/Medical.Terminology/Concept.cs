using System.Globalization;
using System.Text;
using Medical.Core;

namespace Medical.Terminology;

/// <summary>A concept in a code system with bilingual display and optional hierarchy / map / class metadata.</summary>
public sealed record Concept
{
    public required string System { get; init; }
    public required string Code { get; init; }
    public required string Display { get; init; }
    /// <summary>Bahasa Indonesia display, when available.</summary>
    public string? DisplayId { get; init; }
    /// <summary>Semantic tag (SNOMED), class (LOINC), chapter (ICD-10) or ATC code (RxNorm).</summary>
    public string? Category { get; init; }
    /// <summary>Direct is-a parents (SNOMED).</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];
    /// <summary>Free tags, e.g. RxNorm interaction classes ("nsaid", "anticoagulant").</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Default UCUM unit (LOINC).</summary>
    public string? Unit { get; init; }
    /// <summary>Target code of the default map (SNOMED → ICD-10).</summary>
    public string? MapTarget { get; init; }

    public string DisplayFor(string lang) => lang == "id" && DisplayId is not null ? DisplayId : Display;

    public ClinicalCode ToCode(string lang = "en") => new(System, Code, DisplayFor(lang));

    public override string ToString() => $"{CodeSystems.ShortName(System)} {Code} | {Display}";
}

/// <summary>A search hit with relevance score (higher is better).</summary>
public readonly record struct SearchHit(Concept Concept, double Score);

/// <summary>An in-memory code system with an inverted prefix index over English and Indonesian displays.</summary>
public sealed class CodeSystem
{
    private readonly Dictionary<string, Concept> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Concept> _concepts = [];
    private readonly Dictionary<string, List<int>> _postings = new(StringComparer.Ordinal);
    private string[] _sortedTokens = [];
    private Dictionary<string, List<string>>? _children;
    private bool _indexed;
    private readonly Lock _gate = new();

    public CodeSystem(string uri, string name, string? version = null)
    {
        Uri = uri; Name = name; Version = version;
    }

    public string Uri { get; }
    public string Name { get; }
    public string? Version { get; }
    public int Count => _concepts.Count;
    public IReadOnlyList<Concept> Concepts => _concepts;

    public void Add(Concept c)
    {
        if (_byCode.TryGetValue(c.Code, out var existing)) _concepts[_concepts.IndexOf(existing)] = c;
        else _concepts.Add(c);
        _byCode[c.Code] = c;
        _indexed = false;
        _children = null;
    }

    public void AddRange(IEnumerable<Concept> concepts)
    {
        foreach (var c in concepts) Add(c);
    }

    public Concept? Lookup(string code) => _byCode.GetValueOrDefault(code.Trim());

    public bool Contains(string code) => _byCode.ContainsKey(code.Trim());

    private void EnsureIndex()
    {
        if (_indexed) return;
        lock (_gate)
        {
            if (_indexed) return;
            _postings.Clear();
            for (int i = 0; i < _concepts.Count; i++) IndexConcept(i);
            _sortedTokens = [.. _postings.Keys.Order(StringComparer.Ordinal)];
            _indexed = true;
        }
    }

    private void IndexConcept(int i)
    {
        var c = _concepts[i];
        foreach (var t in Text.Tokens(c.Display).Concat(Text.Tokens(c.DisplayId)).Distinct())
        {
            if (!_postings.TryGetValue(t, out var list)) _postings[t] = list = [];
            if (list.Count == 0 || list[^1] != i) list.Add(i);
        }
    }

    /// <summary>
    /// Ranked search. Every query token must prefix-match a token of the English or Indonesian display, or the whole query
    /// must prefix the code. Diacritics and case are ignored ("hipertensi", "Diabetes tipe 2", "E11", "gagal jan").
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 20)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        EnsureIndex();

        var scores = new Dictionary<int, double>();
        // code match
        for (int i = 0; i < _concepts.Count; i++)
        {
            var code = _concepts[i].Code;
            if (code.Equals(query, StringComparison.OrdinalIgnoreCase)) scores[i] = 100;
            else if (code.StartsWith(query, StringComparison.OrdinalIgnoreCase)) scores[i] = 50 - Math.Min(20, code.Length - query.Length);
        }

        var qTokens = Text.Tokens(query).ToArray();
        if (qTokens.Length > 0)
        {
            HashSet<int>? candidates = null;
            var weight = new Dictionary<int, double>();
            foreach (var qt in qTokens)
            {
                var matched = new HashSet<int>();
                int lo = LowerBound(_sortedTokens, qt);
                for (int k = lo; k < _sortedTokens.Length && _sortedTokens[k].StartsWith(qt, StringComparison.Ordinal); k++)
                {
                    bool exact = _sortedTokens[k].Length == qt.Length;
                    foreach (var i in _postings[_sortedTokens[k]])
                    {
                        matched.Add(i);
                        weight[i] = weight.GetValueOrDefault(i) + (exact ? 3 : 1.5);
                    }
                }
                if (candidates is null) candidates = matched; else candidates.IntersectWith(matched);
                if (candidates.Count == 0) break;
            }
            foreach (var i in candidates ?? [])
            {
                var c = _concepts[i];
                double s = 10 + weight[i] - Math.Min(8, c.Display.Length / 12.0);
                if (Text.Normalize(c.Display).StartsWith(Text.Normalize(query), StringComparison.Ordinal) ||
                    (c.DisplayId is { } id && Text.Normalize(id).StartsWith(Text.Normalize(query), StringComparison.Ordinal))) s += 8;
                scores[i] = Math.Max(scores.GetValueOrDefault(i), s);
            }
        }

        return scores.OrderByDescending(kv => kv.Value).ThenBy(kv => _concepts[kv.Key].Code, StringComparer.Ordinal).Take(limit)
            .Select(kv => new SearchHit(_concepts[kv.Key], Math.Round(kv.Value, 2))).ToArray();
    }

    /// <summary>Direct children (inverse is-a).</summary>
    public IReadOnlyList<Concept> Children(string code)
    {
        _children ??= BuildChildren();
        return _children.TryGetValue(code, out var kids) ? kids.Select(k => _byCode[k]).ToArray() : [];
    }

    /// <summary>All ancestors via is-a (transitive closure, breadth-first, cycle-safe).</summary>
    public IReadOnlyList<Concept> Ancestors(string code)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Concept>();
        var queue = new Queue<string>(Lookup(code)?.Parents ?? []);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (!seen.Add(p) || Lookup(p) is not { } pc) continue;
            result.Add(pc);
            foreach (var gp in pc.Parents) queue.Enqueue(gp);
        }
        return result;
    }

    /// <summary>All descendants via inverse is-a (value-set expansion "&lt;&lt; code" minus self).</summary>
    public IReadOnlyList<Concept> Descendants(string code)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Concept>();
        var stack = new Stack<Concept>(Children(code));
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (!seen.Add(c.Code)) continue;
            result.Add(c);
            foreach (var k in Children(c.Code)) stack.Push(k);
        }
        return result;
    }

    /// <summary>True if <paramref name="code"/> is <paramref name="ancestor"/> or a descendant of it.</summary>
    public bool Subsumes(string ancestor, string code) =>
        string.Equals(ancestor, code, StringComparison.OrdinalIgnoreCase) || Ancestors(code).Any(a => string.Equals(a.Code, ancestor, StringComparison.OrdinalIgnoreCase));

    private Dictionary<string, List<string>> BuildChildren()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _concepts)
            foreach (var p in c.Parents)
            {
                if (!map.TryGetValue(p, out var l)) map[p] = l = [];
                l.Add(c.Code);
            }
        return map;
    }

    private static int LowerBound(string[] a, string key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(a[mid], key) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>Text normalisation used by the search index.</summary>
public static class Text
{
    private static readonly HashSet<string> Stop = ["of", "the", "and", "or", "in", "by", "with", "without", "dan", "atau", "yang", "di", "dengan", "tanpa", "tidak", "spesifik", "unspecified", "other"];

    /// <summary>Lower-case, strip diacritics, keep letters/digits.</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return sb.ToString();
    }

    public static IEnumerable<string> Tokens(string? s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        foreach (var t in Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!Stop.Contains(t)) yield return t;
    }
}
