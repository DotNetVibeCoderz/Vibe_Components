using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blazor.AI.ThreeD;

/// <summary>Personality traits of a companion, each 0–1.</summary>
public sealed class CompanionPersonality
{
    public double Warmth { get; set; } = 0.8;
    public double Humor { get; set; } = 0.4;
    public double Curiosity { get; set; } = 0.7;
    public double Formality { get; set; } = 0.2;

    public string Describe() =>
        $"{Level(Warmth, "cool", "friendly", "very warm")}, {Level(Humor, "serious", "lightly playful", "funny")}, " +
        $"{Level(Curiosity, "rarely asks questions", "sometimes asks follow-up questions", "always curious and asks a follow-up question")}, " +
        $"{Level(Formality, "casual", "polite", "formal")}";

    private static string Level(double v, string low, string mid, string high) => v < 0.34 ? low : v < 0.67 ? mid : high;
}

/// <summary>What the companion learned from one user message.</summary>
public sealed record CompanionInsight(IReadOnlyList<string> Facts, string? Goal, double Sentiment, double Intensity);

/// <summary>Local, deterministic extraction of facts, goals and sentiment (English and Bahasa Indonesia).</summary>
public static partial class CompanionMind
{
    public const string Schema = """{"facts":["short fact about the user, third person"],"goal":"a goal the user wants help with, or empty","sentiment":0.0,"intensity":0.0}""";

    [GeneratedRegex(@"\b(?:my name is|i am called|call me|nama (?:saya|aku|ku) (?:adalah )?|panggil (?:saya|aku) )\s*([A-Z\p{Lu}][\p{L}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\b(?:i (?:really )?(?:like|love|enjoy)|saya (?:suka|senang)|aku (?:suka|senang)|hobi (?:saya|aku|ku) (?:adalah )?)\s+([^.,!?\n]{2,50})", RegexOptions.IgnoreCase)]
    private static partial Regex LikePattern();

    [GeneratedRegex(@"\b(?:i (?:live|work|study) (?:in|at)|saya (?:tinggal|kerja|bekerja|kuliah) di|aku (?:tinggal|kerja|kuliah) di)\s+([^.,!?\n]{2,40})", RegexOptions.IgnoreCase)]
    private static partial Regex PlacePattern();

    [GeneratedRegex(@"\b(?:i want to|i'd like to|i plan to|my goal is to|help me|saya ingin|aku ingin|aku mau|saya mau|target (?:saya|aku) )\s*([^.!?\n]{4,70})", RegexOptions.IgnoreCase)]
    private static partial Regex GoalPattern();

    [GeneratedRegex(@"\b(?:i am a|i'm a|saya (?:seorang )?|aku (?:seorang )?)(teacher|student|developer|engineer|doctor|nurse|farmer|designer|guru|mahasiswa|pelajar|programmer|dokter|perawat|petani|desainer|pengusaha)\b", RegexOptions.IgnoreCase)]
    private static partial Regex JobPattern();

    private static readonly string[] Positive = ["love", "great", "happy", "thanks", "thank", "awesome", "good", "excited", "senang", "suka", "bagus", "mantap", "terima kasih", "makasih", "hebat", "seru", "bahagia"];
    private static readonly string[] Negative = ["sad", "tired", "angry", "bad", "hate", "stress", "worried", "lonely", "sedih", "capek", "lelah", "marah", "benci", "kecewa", "khawatir", "bosan", "kesepian"];

    public static CompanionInsight Local(string message)
    {
        var facts = new List<string>();
        if (NamePattern().Match(message) is { Success: true } n)
        {
            facts.Add($"The user's name is {Capitalise(n.Groups[1].Value)}.");
        }

        foreach (Match m in LikePattern().Matches(message))
        {
            facts.Add($"The user likes {m.Groups[1].Value.Trim()}.");
        }

        if (PlacePattern().Match(message) is { Success: true } p)
        {
            facts.Add($"The user lives or works in {p.Groups[1].Value.Trim()}.");
        }

        if (JobPattern().Match(message) is { Success: true } j)
        {
            facts.Add($"The user is a {j.Groups[1].Value.ToLowerInvariant()}.");
        }

        var goal = GoalPattern().Match(message) is { Success: true } g ? g.Groups[1].Value.Trim() : null;
        var lower = message.ToLowerInvariant();
        var pos = Positive.Count(lower.Contains);
        var neg = Negative.Count(lower.Contains);
        var sentiment = pos + neg == 0 ? 0.05 : Math.Clamp((pos - neg) / (double)(pos + neg), -1, 1);
        var intensity = Math.Clamp(0.25 + (pos + neg) * 0.2 + message.Count(c => c == '!') * 0.15, 0, 1);
        return new CompanionInsight(facts, goal, sentiment, intensity);
    }

    /// <summary>Merges the model's JSON (when grounded in the message) with the local result.</summary>
    public static CompanionInsight Merge(CompanionInsight local, JsonNode? json, string message)
    {
        if (json is null)
        {
            return local;
        }

        var words = message.ToLowerInvariant().Split([' ', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToHashSet();
        var facts = local.Facts.ToList();
        if (json["facts"] is JsonArray arr)
        {
            foreach (var f in arr.Select(x => x?.GetValue<string>()?.Trim()).OfType<string>())
            {
                // Keep facts that share words with the message (rejects template echoes and hallucinations).
                if (f.Length is > 6 and < 140 && f.ToLowerInvariant().Split(' ').Count(words.Contains) >= 1 && !f.Contains("short fact", StringComparison.OrdinalIgnoreCase)
                    && !facts.Any(x => x.Equals(f, StringComparison.OrdinalIgnoreCase)))
                {
                    facts.Add(f);
                }
            }
        }

        var goal = json["goal"]?.GetValue<string>() is { Length: > 4 and < 100 } g && !g.Contains("a goal the user", StringComparison.OrdinalIgnoreCase) && g.ToLowerInvariant().Split(' ').Any(words.Contains)
            ? g
            : local.Goal;
        var sentiment = json["sentiment"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? Math.Clamp(json["sentiment"]!.GetValue<double>(), -1, 1) : local.Sentiment;
        if (Math.Abs(sentiment) > 1 || double.IsNaN(sentiment))
        {
            sentiment = local.Sentiment;
        }

        return new CompanionInsight(facts.Take(4).ToList(), goal, (sentiment + local.Sentiment) / 2, local.Intensity);
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
