using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AquaCulture.ComputerVision;

/// <summary>Model metadata from the Hugging Face Hub.</summary>
public sealed record HubModelInfo(string RepoId, string? License, string? PipelineTag, IReadOnlyList<string> Files, bool Gated, DateTime? LastModified)
{
    public IEnumerable<string> OnnxFiles => Files.Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Licences that allow use inside closed farm software without obligations beyond attribution (Apache-2.0, MIT, BSD, CC-BY, OpenRAIL).
    /// AGPL/GPL weights oblige you to publish the whole service that uses them; NC licences forbid commercial farms.
    /// </summary>
    public bool IsPermissive => License?.ToLowerInvariant() switch
    {
        null => false,
        var l when l.Contains("nc", StringComparison.Ordinal) || l.Contains("gpl", StringComparison.Ordinal) => false,
        var l => l.StartsWith("apache", StringComparison.Ordinal) || l is "mit" || l.StartsWith("bsd", StringComparison.Ordinal) || l.StartsWith("cc-by", StringComparison.Ordinal)
            || l.Contains("openrail", StringComparison.Ordinal) || l is "cc0-1.0" || l is "unlicense",
    };
}

/// <summary>
/// Minimal Hugging Face Hub client for vision models: model info (licence, files), cached downloads with an ETag check, and an
/// <see cref="OnnxDetector"/> straight from a repository. Uses <c>HF_TOKEN</c> for gated or private repositories and <c>HF_ENDPOINT</c> for mirrors.
/// </summary>
public sealed class ModelHub(string? cacheDirectory = null, string? token = null, string? endpoint = null, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

    public string CacheDirectory { get; } = cacheDirectory ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AquaCulture.Net", "hub");
    public string Endpoint { get; } = (endpoint ?? System.Environment.GetEnvironmentVariable("HF_ENDPOINT") ?? "https://huggingface.co").TrimEnd('/');
    private string? Token { get; } = token ?? System.Environment.GetEnvironmentVariable("HF_TOKEN");

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var r = new HttpRequestMessage(method, url);
        if (Token is { Length: > 0 }) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        r.Headers.UserAgent.ParseAdd("AquaCulture.Net-ModelHub/1.2");
        return r;
    }

    public async Task<HubModelInfo> GetModelInfoAsync(string repoId, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"{Endpoint}/api/models/{repoId}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Hub: {repoId} → {(int)resp.StatusCode}");
        var j = JsonNode.Parse(text)!;
        string? license = j["cardData"]?["license"]?.GetValue<string>()
            ?? (j["tags"] as JsonArray)?.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t?.StartsWith("license:", StringComparison.Ordinal) == true)?["license:".Length..];
        var files = (j["siblings"] as JsonArray)?.Select(s => s?["rfilename"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
        bool gated = j["gated"] is JsonValue g && (g.TryGetValue<bool>(out var b) ? b : g.TryGetValue<string>(out var s) && s is "auto" or "manual");
        DateTime? modified = j["lastModified"]?.GetValue<string>() is { } m && DateTime.TryParse(m, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        return new HubModelInfo(repoId, license, j["pipeline_tag"]?.GetValue<string>(), files, gated, modified);
    }

    /// <summary>
    /// Downloads <paramref name="file"/> of <paramref name="repoId"/> at <paramref name="revision"/> into the cache (a readable tree:
    /// <c>cache/repo--name/revision/file</c>) and returns its path. A cached copy is reused when the server's ETag is unchanged or the hub is unreachable.
    /// </summary>
    public async Task<string> DownloadAsync(string repoId, string file, string revision = "main", IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string local = Path.Combine(CacheDirectory, repoId.Replace('/', Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar.ToString(), "--"), revision,
            file.Replace('/', Path.DirectorySeparatorChar));
        string etagFile = local + ".etag";
        string url = $"{Endpoint}/{repoId}/resolve/{revision}/{file}";
        string? cachedEtag = File.Exists(local) && File.Exists(etagFile) ? File.ReadAllText(etagFile) : null;
        try
        {
            using var req = Request(HttpMethod.Get, url);
            if (cachedEtag is not null) req.Headers.IfNoneMatch.ParseAdd(cachedEtag);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && File.Exists(local)) return local;
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? $"Hub: {repoId} is gated or private; set HF_TOKEN and accept its terms on the website."
                    : $"Hub: {url} → {(int)resp.StatusCode}");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            string tmp = local + ".part";
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            File.Move(tmp, local, true);
            var etag = resp.Headers.ETag?.Tag;
            if (etag is not null) File.WriteAllText(etagFile, etag); else if (File.Exists(etagFile)) File.Delete(etagFile);
            return local;
        }
        catch (HttpRequestException) when (File.Exists(local) && cachedEtag is not null)
        {
            return local;     // offline: use the cached copy
        }
    }

    /// <summary>
    /// Downloads an ONNX YOLO detector from the Hub and loads it. Refuses non-permissive licences unless <paramref name="acceptLicense"/> is set,
    /// so that AGPL or non-commercial weights are never pulled into a farm product by accident.
    /// </summary>
    public async Task<OnnxDetector> LoadDetectorAsync(string repoId, string onnxFile, string[] labels, int inputSize = 640, bool acceptLicense = false,
        string revision = "main", CancellationToken ct = default)
    {
        var info = await GetModelInfoAsync(repoId, ct).ConfigureAwait(false);
        if (!info.IsPermissive && !acceptLicense)
            throw new InvalidOperationException($"{repoId} is licensed '{info.License ?? "unknown"}'. Review the licence and pass acceptLicense: true to use it.");
        string path = await DownloadAsync(repoId, onnxFile, revision, null, ct).ConfigureAwait(false);
        return new OnnxDetector(path, labels, inputSize);
    }
}
