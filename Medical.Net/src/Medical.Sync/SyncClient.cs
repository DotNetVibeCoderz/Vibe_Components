using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Medical.Sync;

/// <summary>A record as the client holds it.</summary>
public sealed record LocalRecord(string Type, string Id, JsonObject? Data, long Hlc, bool Deleted, bool Dirty, long BaseSeq);

/// <summary>Outcome of one <see cref="SyncClient.SyncAsync"/> run.</summary>
public sealed record SyncReport(int Pushed, int Pulled, IReadOnlyList<SyncRejection> Conflicts, bool Online, string? Error)
{
    public override string ToString() => Online ? $"pushed {Pushed}, pulled {Pulled}, conflicts {Conflicts.Count}" : $"offline ({Error})";
}

/// <summary>
/// Local SQLite store of an offline-first client: every write lands here first (marked dirty) and survives restarts;
/// <see cref="SyncClient"/> pushes dirty rows and pulls server changes. Works on MAUI (Android, iOS, Windows) through
/// Microsoft.Data.Sqlite.
/// </summary>
public sealed class SqliteSyncStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public SqliteSyncStore(string connectionString = "Data Source=:memory:")
    {
        _db = new SqliteConnection(connectionString);
        _db.Open();
        Exec("""
            CREATE TABLE IF NOT EXISTS records (type TEXT NOT NULL, id TEXT NOT NULL, data TEXT, hlc INTEGER NOT NULL, deleted INTEGER NOT NULL,
              dirty INTEGER NOT NULL, base_seq INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (type, id));
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conflicts (at INTEGER NOT NULL, type TEXT NOT NULL, id TEXT NOT NULL, reason TEXT NOT NULL, mine TEXT);
            """);
    }

    /// <summary>A store in a file (e.g. <c>FileSystem.AppDataDirectory</c> on MAUI).</summary>
    public static SqliteSyncStore Open(string path) => new($"Data Source={path}");

    public void Upsert(LocalRecord r)
    {
        lock (_gate)
            Exec("INSERT INTO records (type, id, data, hlc, deleted, dirty, base_seq) VALUES ($t, $i, $d, $h, $x, $y, $b) " +
                 "ON CONFLICT(type, id) DO UPDATE SET data = $d, hlc = $h, deleted = $x, dirty = $y, base_seq = $b",
                ("$t", r.Type), ("$i", r.Id), ("$d", r.Data?.ToJsonString()), ("$h", r.Hlc), ("$x", r.Deleted ? 1 : 0), ("$y", r.Dirty ? 1 : 0), ("$b", r.BaseSeq));
    }

    public LocalRecord? Get(string type, string id)
    {
        lock (_gate) return Query("SELECT type, id, data, hlc, deleted, dirty, base_seq FROM records WHERE type = $t AND id = $i", ("$t", type), ("$i", id)).FirstOrDefault();
    }

    /// <summary>Live (not deleted) records of a type.</summary>
    public IReadOnlyList<LocalRecord> All(string type)
    {
        lock (_gate) return Query("SELECT type, id, data, hlc, deleted, dirty, base_seq FROM records WHERE type = $t AND deleted = 0 ORDER BY id", ("$t", type));
    }

    public IReadOnlyList<LocalRecord> Dirty()
    {
        lock (_gate) return Query("SELECT type, id, data, hlc, deleted, dirty, base_seq FROM records WHERE dirty = 1 ORDER BY hlc");
    }

    public int DirtyCount
    {
        get { lock (_gate) { using var c = _db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM records WHERE dirty = 1"; return Convert.ToInt32(c.ExecuteScalar()); } }
    }

    public string? GetMeta(string key)
    {
        lock (_gate) { using var c = _db.CreateCommand(); c.CommandText = "SELECT value FROM meta WHERE key = $k"; c.Parameters.AddWithValue("$k", key); return c.ExecuteScalar() as string; }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate) Exec("INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$k", key), ("$v", value));
    }

    public void LogConflict(SyncRejection r)
    {
        lock (_gate) Exec("INSERT INTO conflicts (at, type, id, reason, mine) VALUES ($a, $t, $i, $r, $m)",
            ("$a", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$t", r.Change.Type), ("$i", r.Change.Id), ("$r", r.Reason), ("$m", r.Change.Data?.ToJsonString()));
    }

    public IReadOnlyList<(string Type, string Id, string Reason)> Conflicts()
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT type, id, reason FROM conflicts ORDER BY at";
            using var r = c.ExecuteReader();
            var list = new List<(string, string, string)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            return list;
        }
    }

    private void Exec(string sql, params (string Name, object? Value)[] p)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    private List<LocalRecord> Query(string sql, params (string Name, object? Value)[] p)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = c.ExecuteReader();
        var list = new List<LocalRecord>();
        while (r.Read())
            list.Add(new LocalRecord(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : JsonNode.Parse(r.GetString(2))!.AsObject(),
                r.GetInt64(3), r.GetInt64(4) == 1, r.GetInt64(5) == 1, r.GetInt64(6)));
        return list;
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// Offline-first client: <see cref="Save{T}"/> writes locally with a fresh HLC; <see cref="SyncAsync"/> pushes dirty
/// records and then pulls everything newer than the stored cursor. Rejected changes are logged as conflicts and the
/// server's version replaces the local one, so the client converges. Network failures leave the data dirty for the next try.
/// </summary>
public sealed class SyncClient(SqliteSyncStore store, ISyncTransport transport, string nodeId, HybridLogicalClock? clock = null, IReadOnlyList<string>? types = null)
{
    private readonly HybridLogicalClock _clock = clock ?? new();
    private readonly SemaphoreSlim _run = new(1, 1);

    public string NodeId => nodeId;
    public SqliteSyncStore Store => store;
    public DateTimeOffset? LastSync => store.GetMeta("last_sync") is { } s ? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(s, System.Globalization.CultureInfo.InvariantCulture)) : null;

    public void Save<T>(string type, string id, T value)
    {
        var data = JsonSerializer.SerializeToNode(value, SyncJson.Options)!.AsObject();
        long baseSeq = store.Get(type, id)?.BaseSeq ?? 0;
        store.Upsert(new LocalRecord(type, id, data, _clock.Now(), false, true, baseSeq));
    }

    public void Delete(string type, string id)
    {
        long baseSeq = store.Get(type, id)?.BaseSeq ?? 0;
        store.Upsert(new LocalRecord(type, id, null, _clock.Now(), true, true, baseSeq));
    }

    public T? Get<T>(string type, string id) => store.Get(type, id) is { Deleted: false, Data: { } d } ? d.Deserialize<T>(SyncJson.Options) : default;

    public IReadOnlyList<T> All<T>(string type) => store.All(type).Where(r => r.Data is not null).Select(r => r.Data!.Deserialize<T>(SyncJson.Options)!).ToList();

    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        await _run.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int pushed = 0, pulled = 0;
            var conflicts = new List<SyncRejection>();
            var dirty = store.Dirty();
            if (dirty.Count > 0)
            {
                var res = await transport.PushAsync(new SyncPushRequest(nodeId,
                    dirty.Select(d => new SyncChange(d.Type, d.Id, d.Hlc, nodeId, d.Data, d.Deleted) { BaseSeq = d.BaseSeq }).ToList()), ct).ConfigureAwait(false);
                _clock.Receive(res.ServerHlc);
                var ok = res.Accepted.ToHashSet();
                foreach (var d in dirty.Where(d => ok.Contains(d.Id))) { store.Upsert(d with { Dirty = false }); pushed++; }
                foreach (var r in res.Rejected)
                {
                    conflicts.Add(r);
                    store.LogConflict(r);
                    if (r.Current is { } cur) store.Upsert(new LocalRecord(cur.Type, cur.Id, cur.Data, cur.Hlc, cur.Deleted, false, cur.Seq));
                    else if (store.Get(r.Change.Type, r.Change.Id) is { } mine) store.Upsert(mine with { Dirty = false });
                }
            }
            long cursor = long.Parse(store.GetMeta("cursor") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            while (true)
            {
                var page = await transport.PullAsync(new SyncPullRequest(nodeId, cursor, 500, types), ct).ConfigureAwait(false);
                _clock.Receive(page.ServerHlc);
                foreach (var c in page.Changes)
                {
                    var local = store.Get(c.Type, c.Id);
                    if (local is { Dirty: true } && local.Hlc > c.Hlc) continue;   // a newer local edit will be pushed next time
                    store.Upsert(new LocalRecord(c.Type, c.Id, c.Data, c.Hlc, c.Deleted, false, c.Seq));
                    pulled++;
                }
                cursor = page.Cursor;
                store.SetMeta("cursor", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!page.HasMore) break;
            }
            store.SetMeta("last_sync", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new SyncReport(pushed, pulled, conflicts, true, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SyncReport(0, 0, [], false, e.Message);
        }
        finally { _run.Release(); }
    }
}
