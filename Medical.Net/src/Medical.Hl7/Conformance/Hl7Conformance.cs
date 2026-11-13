using System.Text.Json;

namespace Medical.Hl7.Conformance;

/// <summary>R required, RE required-but-may-be-empty, O optional, X not supported (must be empty).</summary>
public enum Usage { R, RE, O, X }

public sealed record FieldRule(int Index, Usage Usage, int? MaxLength = null, IReadOnlyList<string>? Table = null);

public sealed record SegmentRule(string Name, Usage Usage, int Min, int? Max, IReadOnlyList<FieldRule> Fields);

/// <summary>A segment-level conformance profile for one message type (a pragmatic subset of HL7 v2 conformance profiles).</summary>
public sealed record Hl7ConformanceProfile(string MessageType, string Description, IReadOnlyList<SegmentRule> Segments);

public enum ConformanceSeverity { Error, Warning }

public sealed record ConformanceIssue(ConformanceSeverity Severity, string Location, string Message)
{
    public override string ToString() => $"{Severity} {Location}: {Message}";
}

public sealed record ConformanceResult(string Profile, IReadOnlyList<ConformanceIssue> Issues)
{
    public bool IsValid => Issues.All(i => i.Severity != ConformanceSeverity.Error);
}

/// <summary>
/// Checks HL7 v2 messages against segment-level profiles: segment presence and cardinality, field usage (R/RE/X), maximum
/// length and coded-table values (first component). Built-in profiles cover ADT^A01, ORU^R01, ORM^O01, OML^O21, SIU^S12
/// and MDM^T02; load your interface specifications with <see cref="ParseProfiles"/>.
/// </summary>
public sealed class Hl7ConformanceValidator
{
    private readonly Dictionary<string, Hl7ConformanceProfile> _profiles = new(StringComparer.Ordinal);

    public Hl7ConformanceValidator(IEnumerable<Hl7ConformanceProfile>? profiles = null)
    {
        foreach (var p in profiles ?? BuiltIn.Value) _profiles[p.MessageType] = p;
    }

    public static Lazy<IReadOnlyList<Hl7ConformanceProfile>> BuiltIn { get; } = new(() =>
    {
        using var s = typeof(Hl7ConformanceValidator).Assembly.GetManifestResourceStream("Medical.Hl7.Conformance.profiles.json")!;
        using var r = new StreamReader(s);
        return ParseProfiles(r.ReadToEnd());
    });

    public IReadOnlyCollection<Hl7ConformanceProfile> Profiles => _profiles.Values;

    public void Add(Hl7ConformanceProfile profile) => _profiles[profile.MessageType] = profile;

    public static IReadOnlyList<Hl7ConformanceProfile> ParseProfiles(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Hl7ConformanceProfile>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var segs = new List<SegmentRule>();
            foreach (var s in p.GetProperty("segments").EnumerateArray())
            {
                var fields = new List<FieldRule>();
                if (s.TryGetProperty("fields", out var fs))
                    foreach (var f in fs.EnumerateArray())
                        fields.Add(new FieldRule(
                            f.GetProperty("index").GetInt32(), Enum.Parse<Usage>(f.GetProperty("usage").GetString()!),
                            f.TryGetProperty("maxLength", out var ml) ? ml.GetInt32() : null,
                            f.TryGetProperty("table", out var t) ? t.EnumerateArray().Select(x => x.GetString()!).ToArray() : null));
                var max = s.GetProperty("max");
                segs.Add(new SegmentRule(s.GetProperty("name").GetString()!, Enum.Parse<Usage>(s.GetProperty("usage").GetString()!),
                    s.GetProperty("min").GetInt32(), max.ValueKind == JsonValueKind.Number ? max.GetInt32() : null, fields));
            }
            list.Add(new Hl7ConformanceProfile(p.GetProperty("messageType").GetString()!, p.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", segs));
        }
        return list;
    }

    /// <summary>Validates against the profile for the message's MSH-9 (warning when no profile exists).</summary>
    public ConformanceResult Validate(Hl7Message message) => Validate(message.Raw, message.Type);

    public ConformanceResult Validate(string er7, string? messageType = null)
    {
        var segments = Hl7V2.Normalize(er7).Split('\r', StringSplitOptions.RemoveEmptyEntries).Select(Split).ToList();
        messageType ??= segments.FirstOrDefault(s => s.Name == "MSH") is { } msh ? $"{Comp(Field(msh, 9), 1)}^{Comp(Field(msh, 9), 2)}" : "?";
        if (!_profiles.TryGetValue(messageType, out var profile))
            return new ConformanceResult("(none)", [new ConformanceIssue(ConformanceSeverity.Warning, "MSH-9", $"No conformance profile for {messageType}.")]);

        var issues = new List<ConformanceIssue>();
        foreach (var rule in profile.Segments)
        {
            var found = segments.Where(s => s.Name == rule.Name).ToList();
            if (found.Count < rule.Min && rule.Usage == Usage.R)
                issues.Add(new(ConformanceSeverity.Error, rule.Name, $"Required segment missing (min {rule.Min})."));
            if (rule.Max is { } max && found.Count > max)
                issues.Add(new(ConformanceSeverity.Error, rule.Name, $"{found.Count} occurrences exceed max {max}."));
            if (rule.Usage == Usage.X && found.Count > 0)
                issues.Add(new(ConformanceSeverity.Error, rule.Name, "Segment is not supported by this interface."));
            for (int occ = 0; occ < found.Count; occ++)
                foreach (var f in rule.Fields)
                {
                    string value = Field(found[occ], f.Index);
                    string loc = $"{rule.Name}{(found.Count > 1 ? $"[{occ + 1}]" : "")}-{f.Index}";
                    bool empty = value.Replace("^", "").Replace("&", "").Replace("~", "").Length == 0;
                    if (f.Usage == Usage.R && empty) issues.Add(new(ConformanceSeverity.Error, loc, "Required field is empty."));
                    if (f.Usage == Usage.X && !empty) issues.Add(new(ConformanceSeverity.Error, loc, "Field must not be valued."));
                    if (empty) continue;
                    if (f.MaxLength is { } ml && value.Length > ml) issues.Add(new(ConformanceSeverity.Error, loc, $"Length {value.Length} exceeds {ml}."));
                    if (f.Table is { Count: > 0 } table)
                        foreach (var rep in value.Split('~'))
                            if (Comp(rep, 1) is { Length: > 0 } code && !table.Contains(code))
                                issues.Add(new(ConformanceSeverity.Error, loc, $"'{code}' is not in the allowed table ({string.Join(", ", table)})."));
                }
        }
        var known = profile.Segments.Select(s => s.Name).ToHashSet();
        foreach (var unknown in segments.Select(s => s.Name).Where(n => !known.Contains(n) && !n.StartsWith('Z')).Distinct())
            issues.Add(new(ConformanceSeverity.Warning, unknown, "Segment not described by the profile."));
        return new ConformanceResult(profile.MessageType, issues);
    }

    private sealed record Seg(string Name, string[] Fields);

    private static Seg Split(string line)
    {
        var parts = line.Split('|');
        return new Seg(parts[0], parts);
    }

    /// <summary>1-based field value; MSH-1 is the field separator so MSH field n sits at split index n-1.</summary>
    private static string Field(Seg s, int index)
    {
        if (s.Name == "MSH") return index == 1 ? "|" : index - 1 < s.Fields.Length ? s.Fields[index - 1] : "";
        return index < s.Fields.Length ? s.Fields[index] : "";
    }

    private static string Comp(string field, int component)
    {
        var c = field.Split('^');
        return component - 1 < c.Length ? c[component - 1] : "";
    }
}
