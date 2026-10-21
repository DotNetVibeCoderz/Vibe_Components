namespace Blazor.AI.ThreeD;

/// <summary>A remembered fact about the user, with how often it was recalled.</summary>
public sealed record CompanionMemory(string Text, DateTimeOffset At, string Kind = "fact")
{
    public int Recalls { get; set; }
}

/// <summary>A goal the companion pursues with the user, with progress 0–1.</summary>
public sealed record CompanionGoal(string Title)
{
    public double Progress { get; set; }
}

/// <summary>
/// Per-circuit (per-user) memory, goals and emotional state for <c>AICompanion3D</c>. Mood drifts towards neutral and is
/// pushed by each exchange, so the companion's personality is visible across a conversation.
/// </summary>
public sealed class CompanionMemoryStore
{
    private readonly List<CompanionMemory> _memories = [];
    private readonly List<CompanionGoal> _goals = [];

    public IReadOnlyList<CompanionMemory> Memories => _memories;

    public IReadOnlyList<CompanionGoal> Goals => _goals;

    /// <summary>Valence (−1 sad … +1 happy).</summary>
    public double Valence { get; private set; } = 0.2;

    /// <summary>Arousal (0 calm … 1 excited).</summary>
    public double Arousal { get; private set; } = 0.3;

    /// <summary>Rapport with the user, grows with friendly exchanges (0–1).</summary>
    public double Rapport { get; private set; } = 0.1;

    public event Action? Changed;

    public void Remember(string text, string kind = "fact")
    {
        text = text.Trim();
        if (text.Length == 0 || _memories.Any(m => string.Equals(m.Text, text, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _memories.Add(new CompanionMemory(text, DateTimeOffset.UtcNow, kind));
        if (_memories.Count > 50)
        {
            _memories.RemoveAt(0);
        }

        Changed?.Invoke();
    }

    public void AddGoal(string title)
    {
        if (!string.IsNullOrWhiteSpace(title) && !_goals.Any(g => string.Equals(g.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            _goals.Add(new CompanionGoal(title.Trim()));
            Changed?.Invoke();
        }
    }

    public void Progress(string title, double delta)
    {
        var g = _goals.FirstOrDefault(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
        if (g is not null)
        {
            g.Progress = Math.Clamp(g.Progress + delta, 0, 1);
            Changed?.Invoke();
        }
    }

    /// <summary>Applies the emotional effect of one exchange (sentiment −1…1, intensity 0…1).</summary>
    public void Feel(double sentiment, double intensity)
    {
        Valence = Math.Clamp(Valence * 0.7 + sentiment * 0.5, -1, 1);
        Arousal = Math.Clamp(Arousal * 0.6 + intensity * 0.5, 0, 1);
        Rapport = Math.Clamp(Rapport + (sentiment > 0 ? 0.06 : sentiment < -0.3 ? -0.03 : 0.02), 0, 1);
        Changed?.Invoke();
    }

    /// <summary>Memories most related to <paramref name="text"/> by shared words.</summary>
    public IReadOnlyList<CompanionMemory> Recall(string text, int max = 4)
    {
        var words = Words(text);
        var hits = _memories.Select(m => (m, score: Words(m.Text).Intersect(words).Count()))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenByDescending(x => x.m.At).Take(max).Select(x => x.m).ToList();
        foreach (var m in hits)
        {
            m.Recalls++;
        }

        return hits;
    }

    public AvatarEmotion Mood => Valence switch
    {
        > 0.45 => AvatarEmotion.Happy,
        < -0.3 => AvatarEmotion.Sad,
        _ when Arousal > 0.7 => AvatarEmotion.Surprised,
        _ => AvatarEmotion.Neutral,
    };

    public void Clear()
    {
        _memories.Clear();
        _goals.Clear();
        Valence = 0.2;
        Arousal = 0.3;
        Rapport = 0.1;
        Changed?.Invoke();
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split([' ', ',', '.', '!', '?', ';', ':', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToHashSet();
}
