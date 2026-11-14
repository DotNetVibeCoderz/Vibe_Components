using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Medical.Sync;

/// <summary>
/// Hybrid logical clock (Kulkarni et al., 2014): physical milliseconds in the high 48 bits and a logical counter in the
/// low 16 bits, packed into one comparable <see cref="long"/>. Timestamps never go backwards on a node and always exceed
/// every timestamp the node has received, even when device clocks are skewed.
/// </summary>
public sealed class HybridLogicalClock(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private long _last;

    public static long Pack(long physicalMs, int counter) => (physicalMs << 16) | (uint)(counter & 0xFFFF);
    public static long PhysicalMs(long hlc) => hlc >> 16;
    public static int Counter(long hlc) => (int)(hlc & 0xFFFF);
    public static DateTimeOffset ToTime(long hlc) => DateTimeOffset.FromUnixTimeMilliseconds(PhysicalMs(hlc));

    /// <summary>A timestamp for a local event.</summary>
    public long Now()
    {
        lock (_gate)
        {
            long pt = _time.GetUtcNow().ToUnixTimeMilliseconds();
            long lp = PhysicalMs(_last);
            _last = pt > lp ? Pack(pt, 0) : Pack(lp, Counter(_last) + 1);
            return _last;
        }
    }

    /// <summary>Merges a remote timestamp and returns a timestamp later than both.</summary>
    public long Receive(long remote)
    {
        lock (_gate)
        {
            long pt = _time.GetUtcNow().ToUnixTimeMilliseconds();
            long lp = PhysicalMs(_last), rp = PhysicalMs(remote);
            long p = Math.Max(pt, Math.Max(lp, rp));
            int c = p == lp && p == rp ? Math.Max(Counter(_last), Counter(remote)) + 1
                  : p == lp ? Counter(_last) + 1
                  : p == rp ? Counter(remote) + 1
                  : 0;
            _last = Pack(p, c);
            return _last;
        }
    }
}

/// <summary>How concurrent changes to one record type are resolved.</summary>
public enum ConflictPolicy
{
    /// <summary>Records are immutable once written (vital signs, notes): a second write with another payload is rejected.</summary>
    AppendOnly,
    /// <summary>The change with the higher HLC wins (ties broken by node id).</summary>
    LastWriterWins,
    /// <summary>A client change is accepted only if the server has not changed the record since the client last saw it.</summary>
    ServerWins,
    /// <summary>Only the server writes (e.g. the ward census); every client change is rejected and the client gets the server version.</summary>
    ReadOnly,
}

/// <summary>One record version travelling between nodes.</summary>
public sealed record SyncChange(string Type, string Id, long Hlc, string NodeId, JsonObject? Data, bool Deleted = false)
{
    /// <summary>For <see cref="ConflictPolicy.ServerWins"/>: the server sequence of the version this change was based on.</summary>
    public long BaseSeq { get; init; }
    /// <summary>Server-assigned order; pull cursors use it, so a late push from an offline device is never skipped.</summary>
    public long Seq { get; init; }
}

public sealed record SyncRejection(SyncChange Change, string Reason, SyncChange? Current);

public sealed record SyncPushRequest(string NodeId, IReadOnlyList<SyncChange> Changes);

public sealed record SyncPushResult(IReadOnlyList<string> Accepted, IReadOnlyList<SyncRejection> Rejected, long ServerHlc);

public sealed record SyncPullRequest(string NodeId, long Since, int Limit = 500, IReadOnlyList<string>? Types = null);

public sealed record SyncPullResult(IReadOnlyList<SyncChange> Changes, long Cursor, bool HasMore, long ServerHlc);

/// <summary>Storage behind a <see cref="SyncServer"/> (in memory, a database table, or an adapter over domain entities).</summary>
public interface ISyncServerStore
{
    SyncChange? Get(string type, string id);
    /// <summary>Stores a version and returns its new server sequence number.</summary>
    long Put(SyncChange change);
    IReadOnlyList<SyncChange> Since(long seq, int limit, IReadOnlyCollection<string>? types, string? scope);
}

/// <summary>Thread-safe in-memory server store.</summary>
public sealed class InMemorySyncServerStore : ISyncServerStore
{
    private readonly Dictionary<(string, string), SyncChange> _current = [];
    private readonly List<SyncChange> _log = [];
    private readonly Lock _gate = new();

    public SyncChange? Get(string type, string id) { lock (_gate) return _current.GetValueOrDefault((type, id)); }

    public long Put(SyncChange change)
    {
        lock (_gate)
        {
            var stored = change with { Seq = _log.Count + 1 };
            _log.Add(stored);
            _current[(change.Type, change.Id)] = stored;
            return stored.Seq;
        }
    }

    public IReadOnlyList<SyncChange> Since(long seq, int limit, IReadOnlyCollection<string>? types, string? scope)
    {
        lock (_gate)
            return _log.Where(c => c.Seq > seq && (types is null || types.Contains(c.Type)) && _current[(c.Type, c.Id)].Seq == c.Seq)
                .Take(limit).ToList();
    }
}

/// <summary>
/// The server half of offline-first sync: validates pushed changes against each type's <see cref="ConflictPolicy"/>,
/// stamps them with the server clock and a sequence number, and serves pulls by sequence. Only the latest version of a
/// record is returned by a pull.
/// </summary>
public sealed class SyncServer(ISyncServerStore store, IReadOnlyDictionary<string, ConflictPolicy> policies, HybridLogicalClock? clock = null)
{
    private readonly HybridLogicalClock _clock = clock ?? new();
    private readonly Lock _gate = new();

    public ISyncServerStore Store => store;

    public SyncPushResult Push(SyncPushRequest req)
    {
        var accepted = new List<string>();
        var rejected = new List<SyncRejection>();
        lock (_gate)
        {
            foreach (var ch in req.Changes)
            {
                if (!policies.TryGetValue(ch.Type, out var policy)) { rejected.Add(new(ch, $"Type '{ch.Type}' is not synchronised.", null)); continue; }
                _clock.Receive(ch.Hlc);
                var cur = store.Get(ch.Type, ch.Id);
                string? reason = cur is null ? null : policy switch
                {
                    ConflictPolicy.AppendOnly when !ch.Deleted && JsonNode.DeepEquals(cur.Data, ch.Data) => "duplicate",
                    ConflictPolicy.AppendOnly => "Record is append-only and already exists.",
                    ConflictPolicy.LastWriterWins when ch.Hlc < cur.Hlc || (ch.Hlc == cur.Hlc && string.CompareOrdinal(ch.NodeId, cur.NodeId) <= 0) => "A newer version exists.",
                    ConflictPolicy.ServerWins when cur.Seq != ch.BaseSeq => "The record changed on the server since it was read.",
                    _ => null,
                };
                if (policy == ConflictPolicy.ReadOnly) reason = "Read-only on clients.";
                if (reason == "duplicate") { accepted.Add(ch.Id); continue; }     // idempotent retry
                if (reason is not null) { rejected.Add(new(ch, reason, cur)); continue; }
                try { store.Put(ch with { NodeId = ch.NodeId.Length == 0 ? req.NodeId : ch.NodeId }); }
                catch (InvalidOperationException e) { rejected.Add(new(ch, e.Message, cur)); continue; }   // refused by the store (authorisation, integrity)
                accepted.Add(ch.Id);
            }
        }
        return new SyncPushResult(accepted, rejected, _clock.Now());
    }

    public SyncPullResult Pull(SyncPullRequest req, string? scope = null)
    {
        int limit = Math.Clamp(req.Limit, 1, 5000);
        var page = store.Since(req.Since, limit + 1, req.Types?.ToHashSet(), scope);
        bool more = page.Count > limit;
        var changes = more ? page.Take(limit).ToList() : page;
        long cursor = changes.Count > 0 ? changes.Max(c => c.Seq) : req.Since;
        return new SyncPullResult(changes, cursor, more, _clock.Now());
    }
}

/// <summary>Moves push / pull requests between a client and a server.</summary>
public interface ISyncTransport
{
    Task<SyncPushResult> PushAsync(SyncPushRequest request, CancellationToken ct = default);
    Task<SyncPullResult> PullAsync(SyncPullRequest request, CancellationToken ct = default);
}

/// <summary>Calls a <see cref="SyncServer"/> directly (tests, single-process demos); <see cref="Online"/> simulates a network outage.</summary>
public sealed class InProcessSyncTransport(SyncServer server) : ISyncTransport
{
    public bool Online { get; set; } = true;

    public Task<SyncPushResult> PushAsync(SyncPushRequest request, CancellationToken ct = default) =>
        Online ? Task.FromResult(server.Push(Roundtrip(request))) : Task.FromException<SyncPushResult>(new HttpRequestException("offline"));

    public Task<SyncPullResult> PullAsync(SyncPullRequest request, CancellationToken ct = default) =>
        Online ? Task.FromResult(Roundtrip(server.Pull(request))) : Task.FromException<SyncPullResult>(new HttpRequestException("offline"));

    private static T Roundtrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SyncJson.Options), SyncJson.Options)!;
}

/// <summary>HTTP transport: <c>POST {base}/push</c> and <c>POST {base}/pull</c> with JSON bodies.</summary>
public sealed class HttpSyncTransport(HttpClient http, string baseUrl) : ISyncTransport
{
    public async Task<SyncPushResult> PushAsync(SyncPushRequest request, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(baseUrl.TrimEnd('/') + "/push", request, SyncJson.Options, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<SyncPushResult>(SyncJson.Options, ct).ConfigureAwait(false))!;
    }

    public async Task<SyncPullResult> PullAsync(SyncPullRequest request, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(baseUrl.TrimEnd('/') + "/pull", request, SyncJson.Options, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<SyncPullResult>(SyncJson.Options, ct).ConfigureAwait(false))!;
    }
}

public static class SyncJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
