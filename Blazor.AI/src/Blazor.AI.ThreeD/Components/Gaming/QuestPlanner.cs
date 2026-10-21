namespace Blazor.AI.ThreeD;

/// <summary>Deterministic quest chain generator used offline and as a fallback for the model.</summary>
public static class QuestPlanner
{
    public static AIQuestGenerator.Quest Local(string theme)
    {
        var rng = new Random(Math.Abs(theme.GetHashCode(StringComparison.Ordinal)));
        var subject = Subject(theme);
        string Pick(params string[] options) => options[rng.Next(options.Length)];
        var place = new[] { "Desa Pesisir", "Pasar Rempah", "Hutan Larangan", "Gua Kelelawar", "Candi Awan", "Pelabuhan Tua" }.OrderBy(_ => rng.Next()).ToArray();
        List<AIQuestGenerator.QuestStep> steps =
        [
            new("A rumour at the harbour", $"Ask the harbour master about {subject}.", place[0], 100, null, null),
            new("Old maps", $"Trade spices for a map that marks {subject}.", place[1], 150, Pick("Bronze compass", "Pouch of cloves", "Merchant's favour"), "Steal the map instead (faster, but the guild remembers)."),
            new("Into the forbidden forest", "Follow the river without waking the guardian tigers.", place[2], 250, null, null),
            new("The echoing cave", $"Solve the bat-wing riddle to open the vault of {subject}.", place[3], 300, Pick("Ancient key", "Moonstone"), null),
            new("Temple at the clouds", $"Return {subject} to the temple before the eclipse.", place[4], 500, Pick("Title: Guardian of the Isles", "Kris of light"), "Keep it for yourself and become the new pirate king."),
        ];
        return new AIQuestGenerator.Quest($"The quest for {subject}", $"Whispers say {subject} has surfaced again, and every faction wants it first.", steps);
    }

    private static string Subject(string theme)
    {
        var t = theme.Trim().TrimEnd('.');
        var lower = t.ToLowerInvariant();
        var idx = lower.IndexOf("the ", StringComparison.Ordinal);
        var core = idx >= 0 ? t[idx..] : t;
        var cut = core.IndexOfAny([',', ';']);
        if (cut > 0) core = core[..cut];
        var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6);
        var s = string.Join(' ', words);
        return s.Length == 0 ? "the lost relic" : char.ToLowerInvariant(s[0]) + s[1..];
    }
}
