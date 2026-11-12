using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AquaCulture.AI.Agent;

/// <summary>A remembered fact or event ("pond 3 had white feces on 2 Nov", "the farmer prefers Bahasa Indonesia").</summary>
public sealed record MemoryNote(string Id, DateTime TimeUtc, string Text, string? Pond = null);

/// <summary>
/// Long-term memory of the AquaAgent: notes per farm or pond, persisted as JSON, retrieved with BM25 over a bilingual tokenizer
/// (Indonesian and English stop words removed, common Indonesian affixes stripped). No embedding model or network is needed.
/// </summary>
public sealed class AgentMemory
{
    private readonly List<MemoryNote> _notes = [];
    private readonly object _gate = new();

    public AgentMemory(string? path = null)
    {
        Path = path;
        if (path is not null && File.Exists(path)) Load(File.ReadAllText(path));
    }

    /// <summary>JSON file the notes are saved to after every change (null = in memory only).</summary>
    public string? Path { get; }
    public IReadOnlyList<MemoryNote> Notes { get { lock (_gate) return [.. _notes]; } }

    public MemoryNote Remember(string text, string? pond = null, DateTime? timeUtc = null)
    {
        var n = new MemoryNote(Guid.NewGuid().ToString("N")[..12], timeUtc ?? DateTime.UtcNow, text.Trim(), pond);
        lock (_gate) _notes.Add(n);
        Save();
        return n;
    }

    public bool Forget(string id)
    {
        bool removed;
        lock (_gate) removed = _notes.RemoveAll(n => n.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>Top <paramref name="k"/> notes for a query (BM25), optionally restricted to a pond plus farm-wide notes; newer notes win ties.</summary>
    public IReadOnlyList<(MemoryNote Note, double Score)> Recall(string query, int k = 3, string? pond = null)
    {
        List<MemoryNote> notes;
        lock (_gate) notes = [.. _notes.Where(n => pond is null || n.Pond is null || n.Pond.Equals(pond, StringComparison.OrdinalIgnoreCase))];
        var q = Tokens(query).Distinct().ToList();
        if (q.Count == 0 || notes.Count == 0) return [];
        var docs = notes.Select(n => Tokens(n.Text + " " + n.Pond).ToList()).ToList();
        double avg = docs.Average(d => (double)Math.Max(1, d.Count));
        const double k1 = 1.4, b = 0.75;
        var scored = new List<(MemoryNote, double)>();
        for (int i = 0; i < notes.Count; i++)
        {
            double s = 0;
            foreach (var term in q)
            {
                int df = docs.Count(d => d.Contains(term));
                if (df == 0) continue;
                int tf = docs[i].Count(t => t == term);
                if (tf == 0) continue;
                double idf = Math.Log(1 + (notes.Count - df + 0.5) / (df + 0.5));
                s += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * docs[i].Count / avg));
            }
            if (s > 0) scored.Add((notes[i], s));
        }
        return scored.OrderByDescending(x => x.Item2).ThenByDescending(x => x.Item1.TimeUtc).Take(k).Select(x => (x.Item1, Math.Round(x.Item2, 3))).ToList();
    }

    private static readonly HashSet<string> Stop =
    [
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "at", "is", "are", "was", "were", "be", "it", "this", "that", "for", "with", "my", "our", "what", "how", "when", "which",
        "yang", "dan", "di", "ke", "dari", "ini", "itu", "untuk", "dengan", "pada", "ada", "apa", "berapa", "bagaimana", "kapan", "saya", "kami", "kita", "atau", "juga", "sudah", "akan", "tolong", "ya",
    ];

    /// <summary>Lower-case word tokens without stop words; Indonesian -nya/-kan/-an/-i and di-/ke-/me-/ber- affixes are stripped for matching.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        foreach (var w in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Stop.Contains(w)) continue;
            yield return Stem(w);
        }
    }

    private static string Stem(string w)
    {
        if (w.Length <= 4 || w.Any(char.IsDigit)) return w;
        foreach (var suf in new[] { "nya", "kan", "lah", "an", "i" })
            if (w.EndsWith(suf, StringComparison.Ordinal) && w.Length - suf.Length >= 4) { w = w[..^suf.Length]; break; }
        foreach (var pre in new[] { "meng", "mem", "men", "ber", "di", "ke", "me" })
            if (w.StartsWith(pre, StringComparison.Ordinal) && w.Length - pre.Length >= 4) { w = w[pre.Length..]; break; }
        return w;
    }

    public string ToJson()
    {
        lock (_gate)
            return new JsonArray(_notes.Select(n => (JsonNode)new JsonObject
            {
                ["id"] = n.Id, ["time"] = n.TimeUtc.ToString("O", CultureInfo.InvariantCulture), ["text"] = n.Text, ["pond"] = n.Pond,
            }).ToArray()).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private void Load(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray a) return;
        foreach (var n in a.OfType<JsonObject>())
            _notes.Add(new MemoryNote(n["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N")[..12],
                DateTime.Parse(n["time"]?.GetValue<string>() ?? "2000-01-01", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                n["text"]?.GetValue<string>() ?? "", n["pond"]?.GetValue<string>()));
    }

    private void Save()
    {
        if (Path is null) return;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, ToJson());
    }
}

/// <summary>Speech-to-text and text-to-speech for farmers who prefer to talk (Indonesian by default).</summary>
public interface IVoiceService
{
    Task<string> TranscribeAsync(byte[] audio, string fileName = "audio.wav", string? language = "id", CancellationToken ct = default);
    Task<byte[]> SpeakAsync(string text, string? voice = null, CancellationToken ct = default);
}

/// <summary>
/// Voice over OpenAI-compatible audio endpoints (<c>/audio/transcriptions</c> and <c>/audio/speech</c>): OpenAI, Azure OpenAI v1 (deployment
/// names as models), or a local server such as a Whisper/Kokoro OpenAI-compatible gateway. Uses the same <see cref="LlmOptions"/> endpoint and key.
/// </summary>
public sealed class OpenAiVoiceService(LlmOptions options, string transcriptionModel = "gpt-4o-mini-transcribe", string speechModel = "gpt-4o-mini-tts",
    string defaultVoice = "alloy", HttpClient? http = null) : IVoiceService
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

    private string Url(string path) => options.Provider == LlmProvider.AzureOpenAI
        ? $"{options.Endpoint.TrimEnd('/')}/openai/v1/audio/{path}"
        : $"{options.Endpoint.TrimEnd('/')}/audio/{path}";

    private void Auth(HttpRequestMessage req)
    {
        if (options.ApiKey is null) return;
        if (options.Provider == LlmProvider.AzureOpenAI) req.Headers.Add("api-key", options.ApiKey);
        else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    public async Task<string> TranscribeAsync(byte[] audio, string fileName = "audio.wav", string? language = "id", CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg"
            : fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg" : fileName.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ? "audio/webm" : "audio/wav");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(transcriptionModel), "model");
        if (language is not null) form.Add(new StringContent(language), "language");
        form.Add(new StringContent("Percakapan budidaya udang dan ikan: kolam, pakan, DO, amonia, kincir, panen, vaname, nila."), "prompt");
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("transcriptions")) { Content = form };
        Auth(req);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Transcription failed ({(int)resp.StatusCode}): {OpenAiCompatibleChatModel.Truncate(body, 300)}");
        return JsonNode.Parse(body)?["text"]?.GetValue<string>()?.Trim() ?? throw new InvalidOperationException("Malformed transcription response.");
    }

    public async Task<byte[]> SpeakAsync(string text, string? voice = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = speechModel, ["input"] = text, ["voice"] = voice ?? defaultVoice, ["response_format"] = "mp3" };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("speech")) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        Auth(req);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Speech synthesis failed ({(int)resp.StatusCode}): {OpenAiCompatibleChatModel.Truncate(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), 300)}");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Result of a spoken question: what was heard, the agent's answer, and the answer as speech (MP3).</summary>
public sealed record VoiceAnswer(string Transcript, AgentAnswer Answer, byte[] Speech);
