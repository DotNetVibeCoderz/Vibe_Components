using System.Text.Json.Nodes;
using Blazor.AI.Core;

namespace Blazor.AI.Components;

public sealed record ReviewFinding(string Severity, int? Line, string Title, string? Detail, string? Suggestion);

/// <summary>A parsed code review. When the model replies in prose instead of JSON, <see cref="Markdown"/> holds it.</summary>
public sealed record ReviewResult(string? Summary, int? Score, IReadOnlyList<ReviewFinding> Findings, string? Markdown = null)
{
    private static readonly string[] SeverityOrder = ["critical", "major", "minor", "info"];

    public static ReviewResult Parse(string raw)
    {
        if (LlmJson.ParseNode(raw) is not JsonObject obj)
        {
            return new ReviewResult(null, null, [], raw);
        }

        var findings = new List<ReviewFinding>();
        if (obj["findings"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var severity = (item["severity"]?.ToString() ?? "info").ToLowerInvariant();
                int? line = item["line"] is JsonValue v && v.TryGetValue<int>(out var l) ? l : null;
                findings.Add(new ReviewFinding(
                    Array.IndexOf(SeverityOrder, severity) >= 0 ? severity : "info",
                    line,
                    item["title"]?.ToString() ?? "Finding",
                    item["detail"]?.ToString(),
                    item["suggestion"]?.ToString()));
            }
        }

        int? score = obj["score"] is JsonValue s && s.TryGetValue<double>(out var d) ? (int)Math.Round(Math.Clamp(d, 0, 100)) : null;
        return new ReviewResult(
            obj["summary"]?.ToString(),
            score,
            findings.OrderBy(f => Array.IndexOf(SeverityOrder, f.Severity)).ThenBy(f => f.Line ?? int.MaxValue).ToArray());
    }
}
