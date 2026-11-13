using System.Globalization;

namespace Medical.Fhir.Search;

/// <summary>One search criterion: <c>param[:modifier]=value[,value…]</c> (comma = OR).</summary>
public sealed record SearchCriterion(string Param, string? Modifier, IReadOnlyList<string> Values);

/// <summary>An <c>_include</c> / <c>_revinclude</c> directive: <c>SourceType:param[:TargetType]</c> (or <c>*</c>).</summary>
public sealed record IncludeDirective(string SourceType, string Param, string? TargetType)
{
    public static IncludeDirective? Parse(string value)
    {
        if (value == "*") return new("*", "*", null);
        var p = value.Split(':');
        return p.Length switch { 2 => new(p[0], p[1], null), 3 => new(p[0], p[1], p[2]), _ => null };
    }
}

/// <summary>A parsed FHIR search request.</summary>
public sealed class FhirQuery
{
    public List<SearchCriterion> Criteria { get; } = [];
    public List<IncludeDirective> Includes { get; } = [];
    public List<IncludeDirective> RevIncludes { get; } = [];
    public List<(string Param, bool Descending)> Sort { get; } = [];
    public int Count { get; set; } = 50;
    public int Offset { get; set; }
    public bool SummaryCount { get; set; }
    /// <summary>Unsupported parameters (reported in the bundle as an OperationOutcome warning).</summary>
    public List<string> Ignored { get; } = [];

    public static FhirQuery Parse(string type, IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        var q = new FhirQuery();
        var supported = FhirSearchIndex.ParametersFor(type);
        foreach (var (rawKey, rawValue) in parameters ?? [])
        {
            string key = rawKey.Trim(), value = rawValue ?? "";
            switch (key)
            {
                case "_count": if (int.TryParse(value, out var c)) q.Count = Math.Clamp(c, 0, 1000); continue;
                case "_offset": if (int.TryParse(value, out var o)) q.Offset = Math.Max(0, o); continue;
                case "_summary": q.SummaryCount = value == "count"; continue;
                case "_sort":
                    foreach (var s in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        q.Sort.Add((s.TrimStart('-'), s.StartsWith('-')));
                    continue;
                case "_include": if (IncludeDirective.Parse(value) is { } inc) q.Includes.Add(inc); continue;
                case "_revinclude": if (IncludeDirective.Parse(value) is { } rev) q.RevIncludes.Add(rev); continue;
                case "_format" or "_pretty" or "_elements": continue;
            }
            var parts = key.Split(':', 2);
            string param = parts[0];
            if (param is not ("_id" or "_lastUpdated") && !supported.ContainsKey(param)) { q.Ignored.Add(key); continue; }
            q.Criteria.Add(new SearchCriterion(param, parts.Length > 1 ? parts[1] : null, value.Split(',')));
        }
        return q;
    }

    /// <summary>True when the resource (with its index) satisfies every criterion (AND across parameters, OR within one).</summary>
    public bool Matches(string type, string id, DateTimeOffset? lastUpdated, IReadOnlyList<SearchIndexEntry> index)
    {
        var supported = FhirSearchIndex.ParametersFor(type);
        foreach (var c in Criteria)
        {
            bool any = c.Param switch
            {
                "_id" => c.Values.Contains(id),
                "_lastUpdated" => lastUpdated is { } lu && c.Values.Any(v => MatchDate(v, lu, lu)),
                _ => c.Values.Any(v => MatchOne(supported[c.Param], c, v, index.Where(e => e.Param == c.Param).ToArray())),
            };
            if (c.Modifier == "not") any = !any;
            if (!any) return false;
        }
        return true;
    }

    private static bool MatchOne(FhirSearchKind type, SearchCriterion c, string value, SearchIndexEntry[] entries)
    {
        if (c.Modifier == "missing") return bool.TryParse(value, out var missing) && missing == (entries.Length == 0);
        switch (type)
        {
            case FhirSearchKind.Token:
                int bar = value.IndexOf('|');
                string? sys = bar < 0 ? null : value[..bar];
                string code = bar < 0 ? value : value[(bar + 1)..];
                return entries.Any(e =>
                    (code.Length == 0 || string.Equals(e.Value, code, StringComparison.OrdinalIgnoreCase)) &&
                    (sys is null || (sys.Length == 0 ? e.System is null : e.System == sys)));
            case FhirSearchKind.Reference:
                string target = value.Contains('/') ? FhirSearchIndex.Normalize(value) : value;
                if (c.Modifier is { Length: > 0 } t && t != "not" && !target.Contains('/')) target = $"{t}/{target}";
                return entries.Any(e => target.Contains('/') ? e.Value == target : e.Value.EndsWith("/" + target, StringComparison.Ordinal));
            case FhirSearchKind.String:
                return entries.Any(e => c.Modifier switch
                {
                    "exact" => e.Value == value,
                    "contains" => e.Value.Contains(value, StringComparison.OrdinalIgnoreCase),
                    _ => e.Value.StartsWith(value, StringComparison.OrdinalIgnoreCase),
                });
            case FhirSearchKind.Date:
                return entries.Any(e => e.Start is { } s && e.End is { } en && MatchDate(value, s, en));
            default:
                return false;
        }
    }

    /// <summary>FHIR date search with prefixes eq, ne, gt, lt, ge, le, sa, eb against a resource range.</summary>
    public static bool MatchDate(string query, DateTimeOffset start, DateTimeOffset end)
    {
        string prefix = query.Length > 2 && char.IsLetter(query[0]) && char.IsLetter(query[1]) ? query[..2] : "eq";
        var r = FhirSearchIndex.Range(prefix == "eq" && !char.IsLetter(query[0]) ? query : query[2..]);
        if (r is not { } q) return false;
        return prefix switch
        {
            "eq" => start >= q.Start && end <= q.End,
            "ne" => !(start >= q.Start && end <= q.End),
            "gt" or "sa" => end > q.End,
            "lt" or "eb" => start < q.Start,
            "ge" => end >= q.Start,
            "le" => start <= q.End,
            _ => false,
        };
    }

    public static string Format(DateTimeOffset d) => d.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);
}
