using System.Globalization;
using Hl7.Fhir.Model;

namespace Medical.Fhir.Search;

/// <summary>Raised when an update carries an <c>If-Match</c> version that is not the current one (HTTP 412).</summary>
public sealed class FhirVersionConflictException(string message) : Exception(message);

/// <summary>A versioned FHIR R4 resource repository (the contract behind <c>MapMedicalFhir()</c>).</summary>
public interface IFhirRepository
{
    /// <summary>Number of current (non-deleted) resources.</summary>
    int Count { get; }
    Resource Create(Resource resource);
    /// <summary>Update-or-create by id. <paramref name="ifMatchVersion"/> enforces optimistic locking.</summary>
    Resource Update(Resource resource, string? ifMatchVersion = null);
    bool Delete(string type, string id);
    Resource? Read(string type, string id);
    /// <summary>A specific version (<c>vread</c>), including the state before a delete.</summary>
    Resource? VRead(string type, string id, string versionId);
    /// <summary>True when the resource existed and its latest version is a delete (HTTP 410).</summary>
    bool IsDeleted(string type, string id);
    Bundle Search(string type, IEnumerable<KeyValuePair<string, string>>? parameters = null, string? baseUrl = null);
    /// <summary>Instance, type or system history (newest first).</summary>
    Bundle History(string? type = null, string? id = null, DateTimeOffset? since = null, int count = 100);
    /// <summary>Patient compartment (simplified <c>Patient/$everything</c>).</summary>
    Bundle Everything(string patientId);
    /// <summary>Processes a transaction (atomic) or batch bundle.</summary>
    Bundle Process(Bundle request);
}

/// <summary>
/// FHIR semantics shared by every store: versioning, optimistic locking, search with includes, sorting and paging,
/// history, and transactions with <c>urn:uuid</c> reference resolution and conditional create (<c>ifNoneExist</c>).
/// Back-ends only implement storage primitives.
/// </summary>
public abstract class FhirRepositoryBase : IFhirRepository
{
    protected sealed record StoredVersion(string Type, string Id, int Version, DateTimeOffset LastUpdated, bool Deleted, Resource? Resource);

    private readonly Lock _gate = new();

    protected FhirRepositoryBase(TimeProvider? clock) => Clock = clock ?? TimeProvider.System;

    protected TimeProvider Clock { get; }

    // ------------------------------------------------------------- storage primitives

    protected abstract StoredVersion? Current(string type, string id);
    protected abstract StoredVersion? Version(string type, string id, int version);
    /// <summary>Versions newest first, optionally filtered.</summary>
    protected abstract IEnumerable<StoredVersion> HistoryVersions(string? type, string? id, DateTimeOffset? since);
    /// <summary>Current, non-deleted resources of a type. Implementations may pre-filter with <paramref name="query"/>; results are re-checked.</summary>
    protected abstract IEnumerable<StoredVersion> CurrentOfType(string type, FhirQuery? query);
    /// <summary>Distinct resource types that currently have resources.</summary>
    protected abstract IEnumerable<string> Types();
    protected abstract void Append(StoredVersion version);
    protected abstract T Atomic<T>(Func<T> work);
    public abstract int Count { get; }

    // ------------------------------------------------------------- CRUD

    public Resource Create(Resource resource)
    {
        var copy = (Resource)resource.DeepCopy();
        copy.Id = string.IsNullOrEmpty(copy.Id) || Current(copy.TypeName, copy.Id) is not null ? Guid.NewGuid().ToString("n") : copy.Id;
        return Write(copy, null);
    }

    public Resource Update(Resource resource, string? ifMatchVersion = null)
    {
        if (string.IsNullOrEmpty(resource.Id)) throw new ArgumentException("Resource id is required for update.", nameof(resource));
        return Write((Resource)resource.DeepCopy(), ifMatchVersion);
    }

    private Resource Write(Resource r, string? ifMatch)
    {
        lock (_gate)
        {
            var cur = Current(r.TypeName, r.Id!);
            if (ifMatch is not null)
            {
                string expected = ifMatch.Trim().Replace("W/", "").Trim('"');
                if (cur is null || cur.Deleted || cur.Version.ToString(CultureInfo.InvariantCulture) != expected)
                    throw new FhirVersionConflictException($"{r.TypeName}/{r.Id}: If-Match version {expected} is not current ({cur?.Version.ToString(CultureInfo.InvariantCulture) ?? "none"}).");
            }
            int version = (cur?.Version ?? 0) + 1;
            var now = Clock.GetUtcNow();
            r.Meta ??= new Meta();
            r.Meta.VersionId = version.ToString(CultureInfo.InvariantCulture);
            r.Meta.LastUpdated = now;
            Append(new StoredVersion(r.TypeName, r.Id!, version, now, false, (Resource)r.DeepCopy()));
            return r;
        }
    }

    public bool Delete(string type, string id)
    {
        lock (_gate)
        {
            var cur = Current(type, id);
            if (cur is null || cur.Deleted) return false;
            Append(new StoredVersion(type, id, cur.Version + 1, Clock.GetUtcNow(), true, null));
            return true;
        }
    }

    public Resource? Read(string type, string id) => Current(type, id) is { Deleted: false, Resource: { } r } ? (Resource)r.DeepCopy() : null;

    public T? Read<T>(string id) where T : Resource, new() => Read(new T().TypeName, id) as T;

    public Resource? VRead(string type, string id, string versionId) =>
        int.TryParse(versionId, out var v) && Version(type, id, v) is { Resource: { } r } ? (Resource)r.DeepCopy() : null;

    public bool IsDeleted(string type, string id) => Current(type, id) is { Deleted: true };

    // ------------------------------------------------------------- search

    public Bundle Search(string type, IEnumerable<KeyValuePair<string, string>>? parameters = null, string? baseUrl = null)
    {
        var ps = (parameters ?? []).ToList();
        var q = FhirQuery.Parse(type, ps);
        var matches = CurrentOfType(type, q)
            .Where(v => q.Matches(type, v.Id, v.LastUpdated, FhirSearchIndex.Extract(v.Resource!)))
            .ToList();
        matches = Sort(matches, q.Sort);

        var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = matches.Count, Timestamp = Clock.GetUtcNow() };
        string Url(Resource r) => baseUrl is null ? $"{r.TypeName}/{r.Id}" : $"{baseUrl.TrimEnd('/')}/{r.TypeName}/{r.Id}";
        string Self(int offset) => $"{baseUrl?.TrimEnd('/')}{(baseUrl is null ? "" : "/")}{type}?" +
            string.Join('&', ps.Where(p => p.Key is not ("_offset")).Append(new("_offset", offset.ToString(CultureInfo.InvariantCulture)))
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        bundle.Link.Add(new Bundle.LinkComponent { Relation = "self", Url = Self(q.Offset) });
        if (q.SummaryCount) return bundle;

        var page = matches.Skip(q.Offset).Take(q.Count).Select(v => v.Resource!).ToList();
        if (q.Offset + q.Count < matches.Count) bundle.Link.Add(new Bundle.LinkComponent { Relation = "next", Url = Self(q.Offset + q.Count) });
        if (q.Offset > 0) bundle.Link.Add(new Bundle.LinkComponent { Relation = "previous", Url = Self(Math.Max(0, q.Offset - q.Count)) });
        foreach (var r in page)
            bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = Url(r), Resource = (Resource)r.DeepCopy(), Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match } });

        var seen = page.Select(r => $"{r.TypeName}/{r.Id}").ToHashSet();
        foreach (var inc in q.Includes)
            foreach (var r in page.Where(r => inc.SourceType == "*" || inc.SourceType == r.TypeName))
                foreach (var e in FhirSearchIndex.Extract(r).Where(e => e.Type == FhirSearchKind.Reference && (inc.Param == "*" || e.Param == inc.Param)))
                {
                    var parts = e.Value.Split('/');
                    if (parts.Length != 2 || (inc.TargetType is not null && parts[0] != inc.TargetType) || !seen.Add(e.Value)) continue;
                    if (Read(parts[0], parts[1]) is { } target) AddInclude(bundle, target, Url(target));
                }
        foreach (var rev in q.RevIncludes.Where(r => r.SourceType != "*"))
        {
            var targets = page.Select(r => $"{r.TypeName}/{r.Id}").ToHashSet();
            foreach (var v in CurrentOfType(rev.SourceType, null))
            {
                bool points = FhirSearchIndex.Extract(v.Resource!).Any(e => e.Type == FhirSearchKind.Reference && (rev.Param == "*" || e.Param == rev.Param) && targets.Contains(e.Value));
                if (points && seen.Add($"{v.Type}/{v.Id}")) AddInclude(bundle, v.Resource!, Url(v.Resource!));
            }
        }
        if (q.Ignored.Count > 0)
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                Resource = new OperationOutcome { Issue = [new() { Severity = OperationOutcome.IssueSeverity.Warning, Code = OperationOutcome.IssueType.NotSupported, Diagnostics = $"Ignored unsupported parameters: {string.Join(", ", q.Ignored)}" }] },
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Outcome },
            });
        return bundle;
    }

    private static void AddInclude(Bundle b, Resource r, string url) =>
        b.Entry.Add(new Bundle.EntryComponent { FullUrl = url, Resource = (Resource)r.DeepCopy(), Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Include } });

    private static List<StoredVersion> Sort(List<StoredVersion> items, List<(string Param, bool Descending)> sort)
    {
        IOrderedEnumerable<StoredVersion>? ordered = null;
        foreach (var (param, desc) in sort)
        {
            Func<StoredVersion, IComparable?> key = param switch
            {
                "_id" => v => v.Id,
                "_lastUpdated" => v => v.LastUpdated,
                _ => v => FhirSearchIndex.Extract(v.Resource!).Where(e => e.Param == param).Select(e => (IComparable?)(e.Start ?? (object)e.Value)).Min(),
            };
            ordered = ordered is null
                ? desc ? items.OrderByDescending(key) : items.OrderBy(key)
                : desc ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        }
        return (ordered ?? items.OrderBy(v => v.Id, StringComparer.Ordinal)).ToList();
    }

    // ------------------------------------------------------------- history / everything

    public Bundle History(string? type = null, string? id = null, DateTimeOffset? since = null, int count = 100)
    {
        var versions = HistoryVersions(type, id, since).Take(count).ToList();
        var b = new Bundle { Type = Bundle.BundleType.History, Total = versions.Count, Timestamp = Clock.GetUtcNow() };
        foreach (var v in versions)
        {
            string url = $"{v.Type}/{v.Id}";
            b.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = url,
                Resource = v.Resource is null ? null : (Resource)v.Resource.DeepCopy(),
                Request = new Bundle.RequestComponent
                {
                    Method = v.Deleted ? Bundle.HTTPVerb.DELETE : v.Version == 1 ? Bundle.HTTPVerb.POST : Bundle.HTTPVerb.PUT,
                    Url = v.Version == 1 && !v.Deleted ? v.Type : url,
                },
                Response = new Bundle.ResponseComponent
                {
                    Status = v.Deleted ? "204 No Content" : v.Version == 1 ? "201 Created" : "200 OK",
                    Etag = $"W/\"{v.Version}\"",
                    LastModified = v.LastUpdated,
                },
            });
        }
        return b;
    }

    public Bundle Everything(string patientId)
    {
        var b = new Bundle { Type = Bundle.BundleType.Searchset, Timestamp = Clock.GetUtcNow() };
        if (Read("Patient", patientId) is { } p) b.Entry.Add(new Bundle.EntryComponent { FullUrl = $"Patient/{patientId}", Resource = p });
        foreach (var t in Types().Where(t => t != "Patient").Order(StringComparer.Ordinal))
            foreach (var v in CurrentOfType(t, null).Where(v => FhirSearchIndex.Extract(v.Resource!).Any(e => e.Param is "patient" && e.Value == $"Patient/{patientId}")))
                b.Entry.Add(new Bundle.EntryComponent { FullUrl = $"{v.Type}/{v.Id}", Resource = (Resource)v.Resource!.DeepCopy() });
        b.Total = b.Entry.Count;
        return b;
    }

    // ------------------------------------------------------------- transactions

    public Bundle Process(Bundle request)
    {
        bool tx = request.Type == Bundle.BundleType.Transaction;
        return tx ? Atomic(() => ProcessCore(request, true)) : ProcessCore(request, false);
    }

    private Bundle ProcessCore(Bundle request, bool tx)
    {
        var response = new Bundle { Type = tx ? Bundle.BundleType.TransactionResponse : Bundle.BundleType.BatchResponse, Timestamp = Clock.GetUtcNow() };
        var entries = request.Entry.ToList();
        var results = new Bundle.EntryComponent[entries.Count];

        // 1. urn:uuid placeholders → server ids (POST) or URL ids (PUT), then rewrite references everywhere.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var prepared = new Resource?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Resource is null) continue;
            var res = (Resource)e.Resource.DeepCopy();
            var method = e.Request?.Method ?? Bundle.HTTPVerb.POST;
            if (method == Bundle.HTTPVerb.POST) res.Id = Guid.NewGuid().ToString("n");
            else if (method == Bundle.HTTPVerb.PUT && UrlParts(e.Request?.Url) is { Length: >= 2 } up) res.Id = up[1];
            if (e.FullUrl is { } fu && fu.StartsWith("urn:", StringComparison.Ordinal) && res.Id is not null) map[fu] = $"{res.TypeName}/{res.Id}";
            prepared[i] = res;
        }
        if (map.Count > 0) foreach (var r in prepared) if (r is not null) RewriteReferences(r, map);

        // 2. FHIR processing order: DELETE, POST, PUT, GET.
        var order = Enumerable.Range(0, entries.Count).OrderBy(i => (entries[i].Request?.Method ?? Bundle.HTTPVerb.POST) switch
        {
            Bundle.HTTPVerb.DELETE => 0, Bundle.HTTPVerb.POST => 1, Bundle.HTTPVerb.PUT => 2, _ => 3,
        }).ToList();
        foreach (int i in order)
        {
            var e = entries[i];
            var method = e.Request?.Method ?? Bundle.HTTPVerb.POST;
            string url = e.Request?.Url ?? e.Resource?.TypeName ?? "";
            Resource? result = null;
            string status;
            try
            {
                switch (method)
                {
                    case Bundle.HTTPVerb.POST:
                        if (e.Request?.IfNoneExist is { Length: > 0 } cond &&
                            Search(prepared[i]!.TypeName, ParseQueryString(cond)) is { Total: > 0 } existing)
                        {
                            if (existing.Total > 1) throw new InvalidOperationException($"ifNoneExist '{cond}' matched {existing.Total} resources.");
                            result = existing.Entry[0].Resource;
                            status = "200 OK";
                            if (e.FullUrl is { } fu && fu.StartsWith("urn:", StringComparison.Ordinal))
                                RemapPlaceholder(prepared, map.TryGetValue(fu, out var provisional) ? provisional : fu, $"{result!.TypeName}/{result.Id}");
                            break;
                        }
                        result = Write(prepared[i]!, null);
                        status = "201 Created";
                        break;
                    case Bundle.HTTPVerb.PUT:
                        bool existed = Read(prepared[i]!.TypeName, prepared[i]!.Id!) is not null;
                        result = Update(prepared[i]!, e.Request?.IfMatch);
                        status = existed ? "200 OK" : "201 Created";
                        break;
                    case Bundle.HTTPVerb.DELETE:
                        var dp = UrlParts(url);
                        status = dp.Length >= 2 && Delete(dp[0], dp[1]) ? "204 No Content" : "404 Not Found";
                        break;
                    default:
                        var gp = UrlParts(url);
                        result = gp.Length >= 2 ? Read(gp[0], gp[1]) : gp.Length == 1 ? Search(gp[0], ParseQueryString(url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : "")) : null;
                        status = result is null ? "404 Not Found" : "200 OK";
                        break;
                }
            }
            catch (FhirVersionConflictException ex) when (!tx)
            {
                status = "412 Precondition Failed";
                result = Outcome(ex.Message);
            }
            if (tx && (status.StartsWith('4') || status.StartsWith('5'))) throw new InvalidOperationException($"Transaction entry failed: {method} {url} → {status}");
            results[i] = new Bundle.EntryComponent
            {
                FullUrl = result is null || result is OperationOutcome ? null : $"{result.TypeName}/{result.Id}",
                Resource = result,
                Response = new Bundle.ResponseComponent
                {
                    Status = status,
                    Location = result?.Meta?.VersionId is { } lv ? $"{result.TypeName}/{result.Id}/_history/{lv}" : null,
                    Etag = result?.Meta?.VersionId is { } ev ? $"W/\"{ev}\"" : null,
                    LastModified = result?.Meta?.LastUpdated,
                },
            };
        }
        response.Entry.AddRange(results);
        return response;
    }

    private static void RemapPlaceholder(Resource?[] prepared, string placeholder, string target)
    {
        var map = new Dictionary<string, string> { [placeholder] = target };
        foreach (var r in prepared) if (r is not null) RewriteReferences(r, map);
    }

    /// <summary>Replaces <c>urn:uuid:…</c> references with their resolved <c>Type/id</c> anywhere in the resource.</summary>
    public static void RewriteReferences(Base node, IReadOnlyDictionary<string, string> map)
    {
        if (node is ResourceReference rr && rr.Reference is { } v && map.TryGetValue(v, out var target)) rr.Reference = target;
        foreach (var (_, value) in node.EnumerateElements())
        {
            if (value is Base b) RewriteReferences(b, map);
            else if (value is System.Collections.IEnumerable list and not string)
                foreach (var item in list) if (item is Base bi) RewriteReferences(bi, map);
        }
    }

    protected static OperationOutcome Outcome(string message) =>
        new() { Issue = [new() { Severity = OperationOutcome.IssueSeverity.Error, Code = OperationOutcome.IssueType.Conflict, Diagnostics = message }] };

    private static string[] UrlParts(string? url) => (url ?? "").Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static List<KeyValuePair<string, string>> ParseQueryString(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), p.Length > 1 ? Uri.UnescapeDataString(p[1]) : ""))
            .ToList();
}
