using System.Collections.Concurrent;
using System.Globalization;
using Hl7.Fhir.Model;

namespace Medical.Fhir;

/// <summary>
/// Thread-safe in-memory FHIR R4 repository with versioning, a practical subset of search parameters and
/// transaction/batch processing. Backs the <c>MapMedicalFhir()</c> REST facade, tests and demos.
/// </summary>
public sealed class InMemoryFhirStore
{
    private sealed record Entry(Resource Resource, int Version, bool Deleted);

    private readonly ConcurrentDictionary<(string Type, string Id), Entry> _items = new();
    private readonly TimeProvider _clock;

    public InMemoryFhirStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public int Count => _items.Count(kv => !kv.Value.Deleted);

    /// <summary>Creates a resource; assigns an id if missing. Returns a copy with meta.versionId/lastUpdated.</summary>
    public Resource Create(Resource resource)
    {
        var copy = (Resource)resource.DeepCopy();
        copy.Id ??= Guid.NewGuid().ToString("n");
        return Put(copy);
    }

    /// <summary>Update-or-create by id (PUT semantics).</summary>
    public Resource Update(Resource resource)
    {
        if (string.IsNullOrEmpty(resource.Id)) throw new ArgumentException("Resource id is required for update.", nameof(resource));
        return Put((Resource)resource.DeepCopy());
    }

    private Resource Put(Resource r)
    {
        var key = (r.TypeName, r.Id!);
        var entry = _items.AddOrUpdate(key,
            _ => new Entry(Stamp(r, 1), 1, false),
            (_, old) => new Entry(Stamp(r, old.Version + 1), old.Version + 1, false));
        return (Resource)entry.Resource.DeepCopy();
    }

    private Resource Stamp(Resource r, int version)
    {
        r.Meta ??= new Meta();
        r.Meta.VersionId = version.ToString(CultureInfo.InvariantCulture);
        r.Meta.LastUpdated = _clock.GetUtcNow();
        return r;
    }

    public Resource? Read(string type, string id) =>
        _items.TryGetValue((type, id), out var e) && !e.Deleted ? (Resource)e.Resource.DeepCopy() : null;

    public T? Read<T>(string id) where T : Resource, new() => Read(new T().TypeName, id) as T;

    public bool Delete(string type, string id)
    {
        if (!_items.TryGetValue((type, id), out var e) || e.Deleted) return false;
        _items[(type, id)] = e with { Deleted = true, Version = e.Version + 1 };
        return true;
    }

    /// <summary>
    /// Search with the common parameters: <c>_id</c>, <c>patient</c>/<c>subject</c> (Patient/x or x), <c>identifier</c>
    /// (system|value or value), <c>code</c> (system|code or code), <c>name</c>/<c>family</c> (prefix, case-insensitive),
    /// <c>category</c>, <c>status</c> and <c>_count</c>. Returns a searchset bundle.
    /// </summary>
    public Bundle Search(string type, IEnumerable<KeyValuePair<string, string>>? parameters = null, string? baseUrl = null)
    {
        var ps = (parameters ?? []).ToList();
        int count = ps.FirstOrDefault(p => p.Key == "_count").Value is { } c && int.TryParse(c, out var n) ? n : 50;
        var matches = _items.Where(kv => kv.Key.Type == type && !kv.Value.Deleted).Select(kv => kv.Value.Resource)
            .Where(r => ps.Where(p => !p.Key.StartsWith('_') || p.Key == "_id").All(p => Matches(r, p.Key, p.Value)))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = matches.Count, Timestamp = _clock.GetUtcNow() };
        foreach (var r in matches.Take(count))
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"{baseUrl?.TrimEnd('/')}{(baseUrl is null ? "" : "/")}{r.TypeName}/{r.Id}",
                Resource = (Resource)r.DeepCopy(),
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
            });
        return bundle;
    }

    /// <summary>All non-deleted resources that reference <c>Patient/{patientId}</c> (Patient $everything, simplified).</summary>
    public Bundle Everything(string patientId)
    {
        var b = new Bundle { Type = Bundle.BundleType.Searchset, Timestamp = _clock.GetUtcNow() };
        foreach (var r in _items.Values.Where(e => !e.Deleted).Select(e => e.Resource)
                     .Where(r => (r is Patient && r.Id == patientId) || Matches(r, "patient", patientId)).OrderBy(r => r.TypeName != "Patient").ThenBy(r => r.TypeName))
            b.Entry.Add(new Bundle.EntryComponent { FullUrl = $"{r.TypeName}/{r.Id}", Resource = (Resource)r.DeepCopy() });
        b.Total = b.Entry.Count;
        return b;
    }

    /// <summary>Processes a transaction or batch bundle (POST/PUT/DELETE/GET entries) and returns the response bundle.</summary>
    public Bundle Process(Bundle request)
    {
        bool tx = request.Type == Bundle.BundleType.Transaction;
        var response = new Bundle { Type = tx ? Bundle.BundleType.TransactionResponse : Bundle.BundleType.BatchResponse, Timestamp = _clock.GetUtcNow() };
        var snapshot = tx ? new Dictionary<(string, string), Entry>(_items) : null;
        try
        {
            foreach (var e in request.Entry)
            {
                var method = e.Request?.Method ?? Bundle.HTTPVerb.POST;
                string url = e.Request?.Url ?? e.Resource?.TypeName ?? "";
                var parts = url.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
                Resource? result = null;
                string status;
                switch (method)
                {
                    case Bundle.HTTPVerb.POST:
                        result = Create(e.Resource ?? throw new InvalidOperationException("POST entry without resource."));
                        status = "201 Created";
                        break;
                    case Bundle.HTTPVerb.PUT:
                        var res = (Resource)(e.Resource ?? throw new InvalidOperationException("PUT entry without resource.")).DeepCopy();
                        if (parts.Length >= 2) res.Id = parts[1];
                        bool existed = res.Id is not null && Read(res.TypeName, res.Id) is not null;
                        result = Update(res);
                        status = existed ? "200 OK" : "201 Created";
                        break;
                    case Bundle.HTTPVerb.DELETE:
                        status = parts.Length >= 2 && Delete(parts[0], parts[1]) ? "204 No Content" : "404 Not Found";
                        break;
                    default:
                        result = parts.Length >= 2 ? Read(parts[0], parts[1]) : Search(parts[0]);
                        status = result is null ? "404 Not Found" : "200 OK";
                        break;
                }
                if (tx && status.StartsWith('4')) throw new InvalidOperationException($"Transaction entry failed: {method} {url} → {status}");
                response.Entry.Add(new Bundle.EntryComponent
                {
                    Resource = result,
                    Response = new Bundle.ResponseComponent
                    {
                        Status = status,
                        Location = result is null ? null : $"{result.TypeName}/{result.Id}/_history/{result.Meta?.VersionId}",
                        Etag = result?.Meta?.VersionId is { } v ? $"W/\"{v}\"" : null,
                        LastModified = result?.Meta?.LastUpdated,
                    },
                });
            }
        }
        catch when (tx)
        {
            _items.Clear();
            foreach (var kv in snapshot!) _items[kv.Key] = kv.Value;
            throw;
        }
        return response;
    }

    private static bool Matches(Resource r, string key, string value)
    {
        switch (key)
        {
            case "_id":
                return r.Id == value;
            case "patient" or "subject":
                string target = value.Contains('/') ? value : $"Patient/{value}";
                return r switch
                {
                    Observation o => o.Subject?.Reference == target,
                    Condition c => c.Subject?.Reference == target,
                    Encounter e => e.Subject?.Reference == target,
                    MedicationRequest m => m.Subject?.Reference == target,
                    AllergyIntolerance a => a.Patient?.Reference == target,
                    DiagnosticReport d => d.Subject?.Reference == target,
                    ImagingStudy i => i.Subject?.Reference == target,
                    Composition cp => cp.Subject?.Reference == target,
                    _ => false,
                };
            case "identifier":
                var (sys, val) = SplitToken(value);
                IEnumerable<Identifier> ids = r switch { Patient p => p.Identifier, Encounter e => e.Identifier, Observation o => o.Identifier, _ => [] };
                return ids.Any(i => i.Value == val && (sys is null || i.System == sys));
            case "code":
                var (csys, code) = SplitToken(value);
                CodeableConcept? cc = r switch
                {
                    Observation o => o.Code, Condition c => c.Code, AllergyIntolerance a => a.Code,
                    MedicationRequest m => m.Medication as CodeableConcept, _ => null,
                };
                return cc?.Coding.Any(x => x.Code == code && (csys is null || x.System == csys)) ?? false;
            case "name" or "family":
                return r is Patient pt && pt.Name.Any(n =>
                    (n.Family?.StartsWith(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (key == "name" && n.Given.Any(g => g is not null && g.StartsWith(value, StringComparison.OrdinalIgnoreCase))));
            case "category":
                return r is Observation ob && ob.Category.Any(c => c.Coding.Any(x => x.Code == value));
            case "status":
                return r switch
                {
                    Observation o => o.Status?.ToString().Equals(value, StringComparison.OrdinalIgnoreCase) ?? false,
                    Encounter e => e.Status?.ToString().Equals(value.Replace("-", ""), StringComparison.OrdinalIgnoreCase) ?? false,
                    _ => true,
                };
            default:
                return true;
        }
    }

    private static (string? System, string Value) SplitToken(string token)
    {
        int bar = token.IndexOf('|');
        return bar < 0 ? (null, token) : (bar == 0 ? null : token[..bar], token[(bar + 1)..]);
    }
}
