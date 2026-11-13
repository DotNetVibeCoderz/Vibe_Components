
using Medical.Fhir;
using Medical.Fhir.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Medical.Emr.Fhir;

/// <summary>One stored version of a FHIR resource (JSON), including deletes.</summary>
public sealed class FhirResourceRow
{
    public long Pk { get; set; }
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public bool Deleted { get; set; }
    public bool IsCurrent { get; set; }
    public string? Json { get; set; }
}

/// <summary>Search index of the current version (rebuilt on every write).</summary>
public sealed class FhirIndexRow
{
    public long Pk { get; set; }
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Param { get; set; } = "";
    public string? System { get; set; }
    public string Value { get; set; } = "";
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
}

/// <summary>EF Core context for durable FHIR storage (versions + search index). Works on SQL Server, SQLite and in-memory.</summary>
public sealed class FhirDbContext(DbContextOptions<FhirDbContext> options) : DbContext(options)
{
    public DbSet<FhirResourceRow> Resources => Set<FhirResourceRow>();
    public DbSet<FhirIndexRow> Index => Set<FhirIndexRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
        b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<FhirResourceRow>(e =>
        {
            e.ToTable("FhirResources");
            e.HasKey(x => x.Pk);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Id).HasMaxLength(64);
            e.HasIndex(x => new { x.Type, x.Id, x.Version }).IsUnique();
            e.HasIndex(x => new { x.Type, x.IsCurrent });
            e.HasIndex(x => x.LastUpdated);
        });
        m.Entity<FhirIndexRow>(e =>
        {
            e.ToTable("FhirSearchIndex");
            e.HasKey(x => x.Pk);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Param).HasMaxLength(64);
            e.Property(x => x.System).HasMaxLength(256);
            e.Property(x => x.Value).HasMaxLength(512);
            e.HasIndex(x => new { x.Type, x.Param, x.Value });
            e.HasIndex(x => new { x.Type, x.Id });
        });
    }
}

/// <summary>
/// Durable <see cref="IFhirRepository"/> on EF Core: every version is kept as JSON (history, vread), the current version is
/// indexed for search (token and reference parameters are pre-filtered in SQL, then all criteria are re-checked), and
/// transactions run inside a database transaction on relational providers.
/// </summary>
public sealed class EfFhirRepository(IDbContextFactory<FhirDbContext> factory, TimeProvider? clock = null) : FhirRepositoryBase(clock)
{
    private readonly AsyncLocal<FhirDbContext?> _ambient = new();

    private T Use<T>(Func<FhirDbContext, T> work)
    {
        if (_ambient.Value is { } shared) return work(shared);
        using var db = factory.CreateDbContext();
        return work(db);
    }

    private static StoredVersion ToStored(FhirResourceRow r) =>
        new(r.Type, r.Id, r.Version, r.LastUpdated, r.Deleted, r.Json is null ? null : FhirJson.Parse(r.Json));

    public override int Count => Use(db => db.Resources.Count(r => r.IsCurrent && !r.Deleted));

    protected override StoredVersion? Current(string type, string id) =>
        Use(db => db.Resources.AsNoTracking().Where(r => r.Type == type && r.Id == id && r.IsCurrent).AsEnumerable().Select(ToStored).FirstOrDefault());

    protected override StoredVersion? Version(string type, string id, int version) =>
        Use(db => db.Resources.AsNoTracking().Where(r => r.Type == type && r.Id == id && r.Version == version).AsEnumerable().Select(ToStored).FirstOrDefault());

    protected override IEnumerable<StoredVersion> HistoryVersions(string? type, string? id, DateTimeOffset? since) => Use(db =>
    {
        var q = db.Resources.AsNoTracking().AsQueryable();
        if (type is not null) q = q.Where(r => r.Type == type);
        if (id is not null) q = q.Where(r => r.Id == id);
        if (since is { } s) q = q.Where(r => r.LastUpdated >= s);
        return q.OrderByDescending(r => r.LastUpdated).ThenByDescending(r => r.Version).AsEnumerable().Select(ToStored).ToList();
    });

    protected override IEnumerable<StoredVersion> CurrentOfType(string type, FhirQuery? query) => Use(db =>
    {
        var rows = db.Resources.AsNoTracking().Where(r => r.Type == type && r.IsCurrent && !r.Deleted);
        var supported = FhirSearchIndex.ParametersFor(type);
        foreach (var c in query?.Criteria ?? [])
        {
            if (c.Modifier is not null || !supported.TryGetValue(c.Param, out var kind) || kind is not (FhirSearchKind.Token or FhirSearchKind.Reference)) continue;
            // Pre-filter on the code / reference value only; systems, modifiers and the rest are verified by the base class.
            var values = c.Values.Select(v => kind == FhirSearchKind.Token ? v[(v.IndexOf('|') + 1)..] : v.Contains('/') ? FhirSearchIndex.Normalize(v) : v)
                .Where(v => v.Length > 0).ToList();
            if (values.Count == 0) continue;
            string param = c.Param;
            var ids = kind == FhirSearchKind.Reference && values.All(v => !v.Contains('/'))
                ? db.Index.Where(i => i.Type == type && i.Param == param && values.Any(v => i.Value.EndsWith("/" + v))).Select(i => i.Id)
                : db.Index.Where(i => i.Type == type && i.Param == param && values.Contains(i.Value)).Select(i => i.Id);
            rows = rows.Where(r => ids.Contains(r.Id));
        }
        return rows.AsEnumerable().Select(ToStored).ToList();
    });

    protected override IEnumerable<string> Types() =>
        Use(db => db.Resources.Where(r => r.IsCurrent && !r.Deleted).Select(r => r.Type).Distinct().ToList());

    protected override void Append(StoredVersion v) => Use(db =>
    {
        foreach (var prev in db.Resources.Where(r => r.Type == v.Type && r.Id == v.Id && r.IsCurrent)) prev.IsCurrent = false;
        db.Index.RemoveRange(db.Index.Where(i => i.Type == v.Type && i.Id == v.Id));
        db.Resources.Add(new FhirResourceRow
        {
            Type = v.Type, Id = v.Id, Version = v.Version, LastUpdated = v.LastUpdated, Deleted = v.Deleted, IsCurrent = true,
            Json = v.Resource is null ? null : FhirJson.Serialize(v.Resource, false),
        });
        if (v.Resource is { } res)
            db.Index.AddRange(FhirSearchIndex.Extract(res).Select(e => new FhirIndexRow
            {
                Type = v.Type, Id = v.Id, Param = e.Param, System = e.System,
                Value = e.Value.Length > 512 ? e.Value[..512] : e.Value, Start = e.Start, End = e.End,
            }));
        db.SaveChanges();
        return 0;
    });

    protected override T Atomic<T>(Func<T> work)
    {
        using var db = factory.CreateDbContext();
        _ambient.Value = db;
        try
        {
            if (!db.Database.IsRelational()) return work();
            using var tx = db.Database.BeginTransaction();
            var result = work();
            tx.Commit();
            return result;
        }
        finally { _ambient.Value = null; }
    }

    /// <summary>Creates the FHIR tables if missing (use EF migrations in production).</summary>
    public void EnsureCreated()
    {
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
    }
}
