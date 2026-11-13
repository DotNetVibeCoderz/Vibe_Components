using Medical.Fhir.Search;

namespace Medical.Fhir;

/// <summary>
/// Thread-safe in-memory FHIR R4 repository: full version history, search with <c>_include</c>/<c>_revinclude</c>,
/// sorting and paging, optimistic locking and atomic transactions. Backs tests, demos, edge gateways and
/// <c>MapMedicalFhir()</c> by default; use <c>EfFhirRepository</c> (Medical.Emr) for durable storage.
/// </summary>
public sealed class InMemoryFhirStore(TimeProvider? clock = null) : FhirRepositoryBase(clock)
{
    private readonly Dictionary<(string Type, string Id), List<StoredVersion>> _versions = [];
    private readonly Lock _data = new();

    public override int Count
    {
        get { lock (_data) return _versions.Values.Count(v => !v[^1].Deleted); }
    }

    protected override StoredVersion? Current(string type, string id)
    {
        lock (_data) return _versions.TryGetValue((type, id), out var l) ? l[^1] : null;
    }

    protected override StoredVersion? Version(string type, string id, int version)
    {
        lock (_data) return _versions.TryGetValue((type, id), out var l) ? l.FirstOrDefault(v => v.Version == version) : null;
    }

    protected override IEnumerable<StoredVersion> HistoryVersions(string? type, string? id, DateTimeOffset? since)
    {
        lock (_data)
            return _versions.Where(kv => (type is null || kv.Key.Type == type) && (id is null || kv.Key.Id == id))
                .SelectMany(kv => kv.Value)
                .Where(v => since is null || v.LastUpdated >= since)
                .OrderByDescending(v => v.LastUpdated).ThenByDescending(v => v.Version).ToList();
    }

    protected override IEnumerable<StoredVersion> CurrentOfType(string type, FhirQuery? query)
    {
        lock (_data) return _versions.Where(kv => kv.Key.Type == type).Select(kv => kv.Value[^1]).Where(v => !v.Deleted).ToList();
    }

    protected override IEnumerable<string> Types()
    {
        lock (_data) return _versions.Where(kv => !kv.Value[^1].Deleted).Select(kv => kv.Key.Type).Distinct().ToList();
    }

    protected override void Append(StoredVersion version)
    {
        lock (_data)
        {
            if (!_versions.TryGetValue((version.Type, version.Id), out var l)) _versions[(version.Type, version.Id)] = l = [];
            l.Add(version);
        }
    }

    protected override T Atomic<T>(Func<T> work)
    {
        Dictionary<(string, string), List<StoredVersion>> snapshot;
        lock (_data) snapshot = _versions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        try { return work(); }
        catch
        {
            lock (_data)
            {
                _versions.Clear();
                foreach (var kv in snapshot) _versions[kv.Key] = kv.Value;
            }
            throw;
        }
    }
}
