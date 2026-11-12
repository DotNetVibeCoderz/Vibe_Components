using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AquaCulture.IoT;

/// <summary>A reading in the outbox with its gateway sequence number (1, 2, 3, … never reused).</summary>
public readonly record struct OutboxRecord(long Seq, SensorReading Reading);

/// <summary>
/// Durable store-and-forward queue for an edge gateway: readings are appended to JSON-lines segment files with a monotonic
/// sequence number, and only removed after the cloud acknowledged them (<see cref="Ack"/>). Survives restarts and power loss
/// (a torn last line is discarded on open). When the queue exceeds <see cref="MaxBytes"/> the oldest segments are dropped
/// (counted in <see cref="Dropped"/>) so a long outage cannot fill the SD card.
/// </summary>
public sealed class EdgeOutbox : IDisposable
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly List<(long First, string Path)> _segments = [];
    private FileStream? _writer;
    private long _writerBytes;

    /// <param name="directory">Folder for the segments and the <c>acked</c> watermark (created if missing).</param>
    public EdgeOutbox(string directory)
    {
        _dir = Directory.CreateDirectory(directory).FullName;
        string ackPath = Path.Combine(_dir, "acked");
        Acked = File.Exists(ackPath) && long.TryParse(File.ReadAllText(ackPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long a) ? a : 0;
        foreach (var f in Directory.GetFiles(_dir, "seg-*.jsonl"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(f)[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long first))
                _segments.Add((first, f));
        _segments.Sort((x, y) => x.First.CompareTo(y.First));
        LastSeq = Math.Max(Acked, _segments.Count == 0 ? 0 : RepairAndFindLast(_segments[^1].Path, _segments[^1].First - 1));
    }

    /// <summary>Highest sequence number written.</summary>
    public long LastSeq { get; private set; }
    /// <summary>Highest sequence number acknowledged by the cloud.</summary>
    public long Acked { get; private set; }
    /// <summary>Readings waiting for upload.</summary>
    public long Pending => LastSeq - Acked;
    /// <summary>Readings discarded because the queue exceeded <see cref="MaxBytes"/>.</summary>
    public long Dropped { get; private set; }
    /// <summary>Segment size before a new file is started (default 1 MB).</summary>
    public long SegmentBytes { get; init; } = 1 << 20;
    /// <summary>Queue size cap (default 256 MB, about 2 million readings).</summary>
    public long MaxBytes { get; init; } = 256L << 20;
    /// <summary>Flush to the device on every append (slower; protects against power loss). Default true.</summary>
    public bool Durable { get; init; } = true;

    /// <summary>Appends readings and returns the sequence number of the last one.</summary>
    public long Append(IEnumerable<SensorReading> readings)
    {
        lock (_gate)
        {
            var buffer = new MemoryStream();
            foreach (var r in readings)
            {
                if (_writer is null || _writerBytes + buffer.Length > SegmentBytes)
                {
                    FlushBuffer(buffer);
                    StartSegment(LastSeq + 1);
                }
                WriteLine(buffer, ++LastSeq, r);
            }
            FlushBuffer(buffer);
            EnforceCap();
            return LastSeq;
        }
    }

    public long Append(SensorReading reading) => Append([reading]);

    /// <summary>Up to <paramref name="max"/> unacknowledged records, oldest first.</summary>
    public IReadOnlyList<OutboxRecord> Peek(int max = 500)
    {
        lock (_gate)
        {
            _writer?.Flush();
            var result = new List<OutboxRecord>(Math.Min(max, (int)Math.Min(Pending, int.MaxValue)));
            for (int i = 0; i < _segments.Count && result.Count < max; i++)
            {
                long nextFirst = i + 1 < _segments.Count ? _segments[i + 1].First : long.MaxValue;
                if (nextFirst <= Acked + 1) continue;               // fully acknowledged
                using var fs = new FileStream(_segments[i].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                while (result.Count < max && sr.ReadLine() is { } line)
                    if (TryParseLine(line, out var rec) && rec.Seq > Acked) result.Add(rec);
            }
            return result;
        }
    }

    /// <summary>Marks everything up to <paramref name="seq"/> as delivered and deletes fully delivered segments.</summary>
    public void Ack(long seq)
    {
        lock (_gate)
        {
            seq = Math.Min(seq, LastSeq);
            if (seq <= Acked) return;
            Acked = seq;
            string tmp = Path.Combine(_dir, "acked.tmp");
            File.WriteAllText(tmp, seq.ToString(CultureInfo.InvariantCulture));
            File.Move(tmp, Path.Combine(_dir, "acked"), overwrite: true);
            while (_segments.Count > 1 && _segments[1].First <= Acked + 1) RemoveOldest();
        }
    }

    private void EnforceCap()
    {
        long total = _segments.Sum(s => new FileInfo(s.Path).Length);
        while (total > MaxBytes && _segments.Count > 1)
        {
            long first = _segments[0].First, next = _segments[1].First;
            total -= new FileInfo(_segments[0].Path).Length;
            if (next - 1 > Acked)
            {
                Dropped += next - 1 - Math.Max(Acked, first - 1);
                Acked = next - 1;
                File.WriteAllText(Path.Combine(_dir, "acked"), Acked.ToString(CultureInfo.InvariantCulture));
            }
            RemoveOldest();
        }
    }

    private void RemoveOldest()
    {
        File.Delete(_segments[0].Path);
        _segments.RemoveAt(0);
    }

    private void StartSegment(long first)
    {
        _writer?.Dispose();
        string path = Path.Combine(_dir, $"seg-{first.ToString("D12", CultureInfo.InvariantCulture)}.jsonl");
        _writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writerBytes = _writer.Length;
        if (_segments.Count == 0 || _segments[^1].Path != path) _segments.Add((first, path));
    }

    private void FlushBuffer(MemoryStream buffer)
    {
        if (buffer.Length == 0 || _writer is null) return;
        buffer.Position = 0;
        buffer.CopyTo(_writer);
        _writerBytes += buffer.Length;
        if (Durable) _writer.Flush(flushToDisk: true); else _writer.Flush();
        buffer.SetLength(0);
    }

    private static void WriteLine(Stream s, long seq, SensorReading r)
    {
        using (var w = new Utf8JsonWriter(s))
        {
            w.WriteStartObject();
            w.WriteNumber("s", seq);
            WriteReading(w, r);
            w.WriteEndObject();
        }
        s.WriteByte((byte)'\n');
    }

    internal static void WriteReading(Utf8JsonWriter w, SensorReading r)
    {
        w.WriteString("d", r.DeviceId);
        w.WriteString("p", r.PondId);
        w.WriteString("k", r.Kind.ToString());
        w.WriteNumber("v", r.Value);
        w.WriteNumber("t", new DateTimeOffset(r.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds());
        if (r.Quality != 100) w.WriteNumber("q", r.Quality);
    }

    internal static bool TryReadReading(JsonElement e, out SensorReading reading)
    {
        reading = null!;
        if (!e.TryGetProperty("k", out var k) || !Enum.TryParse<SensorKind>(k.GetString(), out var kind)) return false;
        if (!e.TryGetProperty("v", out var v) || !e.TryGetProperty("t", out var t)) return false;
        byte q = e.TryGetProperty("q", out var qq) ? qq.GetByte() : (byte)100;
        reading = new SensorReading(e.TryGetProperty("d", out var d) ? d.GetString() ?? "" : "", e.TryGetProperty("p", out var p) ? p.GetString() ?? "" : "",
            kind, v.GetDouble(), DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).UtcDateTime, SensorReading.DefaultUnit(kind), q);
        return true;
    }

    private static bool TryParseLine(string line, out OutboxRecord rec)
    {
        rec = default;
        if (line.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("s", out var s) || !TryReadReading(doc.RootElement, out var r)) return false;
            rec = new OutboxRecord(s.GetInt64(), r);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Finds the last complete record of a segment and truncates a torn final line (power cut mid-write).</summary>
    private static long RepairAndFindLast(string path, long fallback)
    {
        long last = fallback, goodLength = 0;
        var bytes = File.ReadAllBytes(path);
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            string line = Encoding.UTF8.GetString(bytes, start, i - start);
            if (TryParseLine(line, out var rec)) { last = rec.Seq; goodLength = i + 1; }
            else break;
            start = i + 1;
        }
        if (goodLength < bytes.Length)
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) fs.SetLength(goodLength);
        return last;
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}

/// <summary>Wire format of a gateway → cloud batch (also used by the cloud to parse it).</summary>
public static class SyncWire
{
    public const string MediaType = "application/vnd.aquaculture.sync+json";

    /// <summary><c>{"gateway":"gw-1","readings":[{"s":1,"d":…,"p":…,"k":"DissolvedOxygen","v":5.1,"t":1767225600000}, …]}</c></summary>
    public static byte[] WriteBatch(string gatewayId, IReadOnlyList<OutboxRecord> records)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("gateway", gatewayId);
            w.WriteStartArray("readings");
            foreach (var rec in records)
            {
                w.WriteStartObject();
                w.WriteNumber("s", rec.Seq);
                EdgeOutbox.WriteReading(w, rec.Reading);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Parses a batch; throws <see cref="FormatException"/> on a malformed body or unknown sensor kind.</summary>
    public static (string Gateway, IReadOnlyList<OutboxRecord> Records) ParseBatch(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var doc = JsonDocument.ParseValue(ref reader);
            string gw = doc.RootElement.GetProperty("gateway").GetString() ?? throw new FormatException("Missing gateway.");
            var list = new List<OutboxRecord>();
            foreach (var e in doc.RootElement.GetProperty("readings").EnumerateArray())
            {
                if (!e.TryGetProperty("s", out var s) || !EdgeOutbox.TryReadReading(e, out var r))
                    throw new FormatException($"Malformed reading: {e.GetRawText()}");
                list.Add(new OutboxRecord(s.GetInt64(), r));
            }
            return (gw, list);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Malformed sync batch: " + e.Message, e);
        }
    }

    /// <summary>Gateway ids: 1–64 characters of <c>[A-Za-z0-9._-]</c>.</summary>
    public static bool IsValidGatewayId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>Outcome of one sync pass.</summary>
public sealed record SyncResult(int Sent, long Acked, long Pending, bool Online, string? Error = null);

/// <summary>Configuration pushed from the cloud to a gateway (opaque JSON plus its version / ETag).</summary>
public sealed record GatewayConfig(string Version, JsonObject Values)
{
    public double? Number(string key) => Values[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;
}

/// <summary>
/// Offline-first sync between an <see cref="EdgeOutbox"/> and the <c>aqua-cloud</c> tenant API:
/// <c>POST /t/{tenant}/sync/{gateway}/readings</c> (idempotent: the cloud keeps a per-gateway watermark and answers
/// <c>{"acked": seq}</c>) and <c>GET /t/{tenant}/sync/{gateway}/config</c> with <c>If-None-Match</c>.
/// The gateway keeps running on its own when the link is down; <see cref="RunAsync"/> retries with exponential backoff.
/// </summary>
public sealed class CloudSyncClient(HttpClient http, Uri baseUri, string tenant, string apiKey, string gatewayId)
{
    private string? _etag;

    public string GatewayId { get; } = SyncWire.IsValidGatewayId(gatewayId) ? gatewayId : throw new ArgumentException("Gateway id must be 1–64 characters of [A-Za-z0-9._-].", nameof(gatewayId));
    public int BatchSize { get; init; } = 500;
    /// <summary>Upper bound of readings sent per pass (keeps a pass short on a slow link).</summary>
    public int MaxPerPass { get; init; } = 20_000;
    /// <summary>Most recent configuration received from the cloud.</summary>
    public GatewayConfig? Config { get; private set; }
    /// <summary>Raised when a new configuration arrives.</summary>
    public event Action<GatewayConfig>? ConfigChanged;

    private Uri Url(string tail) => new(baseUri, $"t/{Uri.EscapeDataString(tenant)}/sync/{Uri.EscapeDataString(GatewayId)}/{tail}");

    /// <summary>Uploads pending readings in batches until the outbox is empty, a pass limit is hit or the cloud fails.</summary>
    public async Task<SyncResult> SyncOnceAsync(EdgeOutbox outbox, CancellationToken ct = default)
    {
        int sent = 0;
        try
        {
            while (outbox.Pending > 0 && sent < MaxPerPass)
            {
                var batch = outbox.Peek(BatchSize);
                if (batch.Count == 0) break;
                using var req = new HttpRequestMessage(HttpMethod.Post, Url("readings"));
                req.Headers.Add("X-Api-Key", apiKey);
                req.Content = new ByteArrayContent(SyncWire.WriteBatch(GatewayId, batch));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(SyncWire.MediaType);
                using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
                string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    return new SyncResult(sent, outbox.Acked, outbox.Pending, true, $"HTTP {(int)res.StatusCode}: {Truncate(body)}");
                long acked = JsonNode.Parse(body)?["acked"]?.GetValue<long>() ?? throw new FormatException("Response has no 'acked'.");
                if (acked < batch[^1].Seq && acked <= outbox.Acked)
                    return new SyncResult(sent, outbox.Acked, outbox.Pending, true, $"Cloud acknowledged {acked}, expected {batch[^1].Seq}.");
                outbox.Ack(acked);
                sent += batch.Count;
            }
            await PullConfigAsync(ct).ConfigureAwait(false);
            return new SyncResult(sent, outbox.Acked, outbox.Pending, true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or FormatException or JsonException)
        {
            return new SyncResult(sent, outbox.Acked, outbox.Pending, e is FormatException or JsonException, e.Message);
        }
    }

    /// <summary>Fetches the gateway configuration if it changed. Returns true when a new version arrived.</summary>
    public async Task<bool> PullConfigAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url("config"));
        req.Headers.Add("X-Api-Key", apiKey);
        if (_etag is not null) req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound) return false;
        res.EnsureSuccessStatusCode();
        _etag = res.Headers.ETag?.Tag;
        var values = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? [];
        Config = new GatewayConfig(_etag ?? "", values);
        ConfigChanged?.Invoke(Config);
        return true;
    }

    /// <summary>Syncs every <paramref name="interval"/> until cancelled; after a failure waits 2ⁿ × 5 s (capped at 5 minutes).</summary>
    public async Task RunAsync(EdgeOutbox outbox, TimeSpan interval, Action<SyncResult>? onResult = null, CancellationToken ct = default)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var r = await SyncOnceAsync(outbox, ct).ConfigureAwait(false);
            onResult?.Invoke(r);
            failures = r.Error is null ? 0 : failures + 1;
            var wait = failures == 0 ? interval : TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, failures - 1)));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
