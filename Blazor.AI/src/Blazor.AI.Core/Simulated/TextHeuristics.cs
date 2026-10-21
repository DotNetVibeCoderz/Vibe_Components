using System.Text.RegularExpressions;

namespace Blazor.AI.Core.Simulated;

/// <summary>Lightweight NLP heuristics shared by the simulated provider and offline fallbacks.</summary>
public static partial class TextHeuristics
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "of", "to", "in", "on", "for", "with", "at", "by", "from", "is",
        "are", "was", "were", "be", "been", "it", "this", "that", "these", "those", "as", "we", "you", "they", "i", "he",
        "she", "our", "your", "their", "its", "not", "no", "so", "do", "does", "did", "can", "could", "will", "would",
        "should", "has", "have", "had", "about", "into", "than", "also", "there", "what", "which", "who", "how", "when",
        "where", "why", "me", "my", "please", "dan", "yang", "di", "ke", "dari", "untuk", "dengan", "ini", "itu", "atau",
        "pada", "adalah", "akan", "tidak", "juga", "saya", "kami", "kita", "anda", "ada", "sebagai", "dalam", "oleh",
    };

    private static readonly HashSet<string> Positive = new(StringComparer.OrdinalIgnoreCase)
    {
        "good", "great", "excellent", "amazing", "love", "loved", "happy", "fast", "helpful", "perfect", "awesome", "best",
        "fantastic", "satisfied", "recommend", "easy", "wonderful", "nice", "thanks", "thank", "bagus", "mantap", "puas",
        "senang", "cepat", "suka", "terima", "kasih", "hebat", "keren", "ramah",
    };

    private static readonly HashSet<string> Negative = new(StringComparer.OrdinalIgnoreCase)
    {
        "bad", "terrible", "awful", "hate", "slow", "broken", "angry", "worst", "poor", "disappointed", "refund", "problem",
        "issue", "bug", "crash", "late", "never", "useless", "expensive", "rude", "error", "fail", "failed", "buruk",
        "jelek", "lambat", "kecewa", "rusak", "mahal", "marah", "gagal", "masalah", "lama",
    };

    [GeneratedRegex(@"(?<=[\.\!\?])\s+(?=[A-Z0-9""“])|\n{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-']*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    public static IReadOnlyList<string> Sentences(string text) =>
        SentenceBoundary().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    public static IEnumerable<string> Words(string text) => WordPattern().Matches(text).Select(m => m.Value);

    public static IEnumerable<string> ContentWords(string text) =>
        Words(text).Where(w => w.Length > 2 && !StopWords.Contains(w)).Select(w => w.ToLowerInvariant());

    /// <summary>Top keywords by frequency (ties broken by first appearance).</summary>
    public static IReadOnlyList<string> Keywords(string text, int count = 6) =>
        ContentWords(text)
            .Select((w, i) => (w, i))
            .GroupBy(x => x.w)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.First().i)
            .Take(count)
            .Select(g => g.Key)
            .ToArray();

    /// <summary>Extractive summary: scores sentences by content-word frequency, keeps the best in original order.</summary>
    public static string Summarize(string text, int maxSentences = 3)
    {
        var sentences = Sentences(text);
        if (sentences.Count <= maxSentences)
        {
            return string.Join(' ', sentences);
        }

        var frequency = ContentWords(text).GroupBy(w => w).ToDictionary(g => g.Key, g => (double)g.Count());
        var max = frequency.Count == 0 ? 1 : frequency.Values.Max();

        return string.Join(' ', sentences
            .Select((s, i) =>
            {
                var words = ContentWords(s).ToList();
                var score = words.Count == 0 ? 0 : words.Sum(w => frequency.GetValueOrDefault(w) / max) / Math.Sqrt(words.Count);
                // Lead bias: news-style text puts key facts first.
                return (Sentence: s, Index: i, Score: score + (i == 0 ? 0.35 : 0));
            })
            .OrderByDescending(x => x.Score)
            .Take(maxSentences)
            .OrderBy(x => x.Index)
            .Select(x => x.Sentence));
    }

    /// <summary>Lexicon sentiment in [-1, 1].</summary>
    public static double Sentiment(string text)
    {
        int pos = 0, neg = 0;
        string? previous = null;
        foreach (var word in Words(text))
        {
            var negated = previous is not null && (previous.Equals("not", StringComparison.OrdinalIgnoreCase)
                                                   || previous.Equals("tidak", StringComparison.OrdinalIgnoreCase)
                                                   || previous.Equals("never", StringComparison.OrdinalIgnoreCase));
            if (Positive.Contains(word))
            {
                if (negated) neg++; else pos++;
            }
            else if (Negative.Contains(word))
            {
                if (negated) pos++; else neg++;
            }

            previous = word;
        }

        var total = pos + neg;
        return total == 0 ? 0 : (pos - neg) / (double)total;
    }

    /// <summary>Scores each label by sentiment cues and vocabulary overlap; returns a normalised distribution.</summary>
    public static Dictionary<string, double> ScoreLabels(string text, IReadOnlyList<string> labels)
    {
        var words = ContentWords(text).ToHashSet();
        var sentiment = Sentiment(text);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in labels)
        {
            var lower = label.ToLowerInvariant();
            var score = 0.1;
            if (lower.Contains("positi") || lower is "happy" or "praise" or "senang")
            {
                score += Math.Max(0, sentiment) * 2;
            }
            else if (lower.Contains("negati") || lower is "angry" or "complaint" or "keluhan" or "marah")
            {
                score += Math.Max(0, -sentiment) * 2;
            }
            else if (lower.Contains("neutral") || lower.Contains("netral"))
            {
                score += 1 - Math.Abs(sentiment);
            }

            foreach (var token in ContentWords(label))
            {
                if (words.Contains(token))
                {
                    score += 1.5;
                }
                else if (words.Any(w => w.Length > 3 && (w.StartsWith(token[..Math.Min(4, token.Length)], StringComparison.Ordinal))))
                {
                    score += 0.6;
                }
            }

            foreach (var (topic, cues) in TopicCues)
            {
                if (lower.Contains(topic, StringComparison.Ordinal))
                {
                    score += cues.Count(words.Contains) * 0.9;
                }
            }

            scores[label] = score;
        }

        var sum = scores.Values.Sum();
        foreach (var key in scores.Keys.ToList())
        {
            scores[key] = Math.Round(scores[key] / sum, 4);
        }

        return scores;
    }

    private static readonly (string Topic, string[] Cues)[] TopicCues =
    [
        ("billing", ["invoice", "charge", "charged", "payment", "refund", "price", "bill", "tagihan", "bayar", "card"]),
        ("technical", ["error", "bug", "crash", "login", "install", "server", "app", "update", "password", "loading"]),
        ("shipping", ["delivery", "package", "shipping", "courier", "arrived", "tracking", "kirim", "paket"]),
        ("sales", ["buy", "pricing", "quote", "demo", "plan", "upgrade", "purchase", "discount"]),
        ("sport", ["match", "goal", "team", "player", "league", "score", "coach"]),
        ("tech", ["software", "ai", "computer", "app", "device", "cloud", "data", "chip"]),
        ("finance", ["stock", "market", "bank", "loan", "interest", "revenue", "profit", "investment"]),
        ("health", ["doctor", "patient", "hospital", "symptom", "fever", "pain", "medicine", "treatment"]),
        ("urgent", ["asap", "urgent", "immediately", "now", "critical", "down", "segera"]),
        ("spam", ["winner", "free", "prize", "click", "offer", "lottery", "congratulations"]),
    ];
}
