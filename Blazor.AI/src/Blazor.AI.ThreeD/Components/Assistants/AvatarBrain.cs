namespace Blazor.AI.ThreeD;

/// <summary>Emotional states the guide robot can express.</summary>
public enum AvatarEmotion
{
    Neutral,
    Happy,
    Thinking,
    Sad,
    Surprised,
}

/// <summary>Maps text and emotion to the GuideBot's clips, morphs and visor colour.</summary>
public static class AvatarBrain
{
    public static readonly IReadOnlyList<string> EmotionLabels = ["neutral", "happy", "thinking", "sad", "surprised"];

    public static AvatarEmotion Parse(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "happy" or "joy" or "excited" or "positive" => AvatarEmotion.Happy,
        "thinking" or "curious" or "uncertain" => AvatarEmotion.Thinking,
        "sad" or "sorry" or "negative" or "empathetic" => AvatarEmotion.Sad,
        "surprised" or "surprise" => AvatarEmotion.Surprised,
        _ => AvatarEmotion.Neutral,
    };

    /// <summary>Cheap local guess, used before (or instead of) the model's classification.</summary>
    public static AvatarEmotion Guess(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains('!') && (t.Contains("great") || t.Contains("congrat") || t.Contains("selamat") || t.Contains("awesome") || t.Contains("hebat")))
        {
            return AvatarEmotion.Happy;
        }

        if (t.Contains("sorry") || t.Contains("unfortunately") || t.Contains("maaf") || t.Contains("sayangnya"))
        {
            return AvatarEmotion.Sad;
        }

        if (t.Contains("wow") || t.Contains("really?") || t.Contains("wah"))
        {
            return AvatarEmotion.Surprised;
        }

        if (t.Contains("let me think") || t.Contains("depends") || t.Contains("hmm") || t.Contains('?'))
        {
            return AvatarEmotion.Thinking;
        }

        return t.Contains("thank") || t.Contains("terima kasih") || t.Contains("glad") || t.Contains("senang") ? AvatarEmotion.Happy : AvatarEmotion.Neutral;
    }

    /// <summary>A gesture triggered by what the user said (greeting → wave, agreement → nod, direction → point).</summary>
    public static string? GestureFor(string userText)
    {
        var t = userText.ToLowerInvariant();
        if (t.StartsWith("hi") || t.StartsWith("hello") || t.StartsWith("halo") || t.Contains("selamat pagi") || t.Contains("good morning") || t.StartsWith("hey"))
        {
            return "Wave";
        }

        if (t.Contains("where") || t.Contains("di mana") || t.Contains("dimana") || t.Contains("which way") || t.Contains("show me"))
        {
            return "Point";
        }

        return t.Contains("right?") || t.Contains("correct") || t.Contains("benar") || t.Contains("agree") ? "Nod" : null;
    }

    public static string ClipFor(AvatarEmotion e) => e switch
    {
        AvatarEmotion.Happy => "Happy",
        AvatarEmotion.Thinking => "Think",
        AvatarEmotion.Surprised => "Happy",
        _ => "Talk",
    };

    public static string VisorColor(AvatarEmotion e) => e switch
    {
        AvatarEmotion.Happy => ThreeDPalette.Soga,
        AvatarEmotion.Sad => "#7b8cff",
        AvatarEmotion.Thinking => "#c45ad6",
        AvatarEmotion.Surprised => ThreeDPalette.Warning,
        _ => ThreeDPalette.Nila,
    };

    public static (double Smile, double Sad) Morphs(AvatarEmotion e) => e switch
    {
        AvatarEmotion.Happy or AvatarEmotion.Surprised => (1, 0),
        AvatarEmotion.Sad => (0, 1),
        _ => (0.2, 0),
    };

    /// <summary>Removes markdown so the speech engine reads clean sentences.</summary>
    public static string ForSpeech(string markdown) =>
        System.Text.RegularExpressions.Regex.Replace(markdown, @"[*_#`>\[\]()]|https?://\S+", string.Empty).Replace("\n", " ").Trim();
}
