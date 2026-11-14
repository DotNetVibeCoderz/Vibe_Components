using Medical.Core;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

/// <summary>The signed-in user and the hospital (tenant) they work in.</summary>
public interface ITenantContext
{
    string TenantId { get; }
    string UserName { get; }
    StaffRole Role { get; }
}

/// <summary>A fixed tenant context (background jobs, tests, seeding).</summary>
public sealed record FixedTenantContext(string TenantId, string UserName = "system", StaffRole Role = StaffRole.Admin) : ITenantContext;

/// <summary>Thrown when code tries to read or write another tenant's data.</summary>
public sealed class TenantViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// The hospital database. Multi-tenancy is a shared schema with a <c>TenantId</c> on every row: global query filters
/// hide other tenants' rows, and <c>SaveChanges</c> stamps new rows and refuses cross-tenant writes. Works on
/// SQLite and SQL Server (dates are stored as UTC <see cref="DateTime"/>, money as <see cref="decimal"/>).
/// </summary>
public class HospitalDbContext(DbContextOptions<HospitalDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public ITenantContext Tenant => tenant;
    /// <summary>Used by the query filters (EF re-reads it per context instance).</summary>
    public string CurrentTenant => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Bed> Beds => Set<Bed>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<HisPatient> Patients => Set<HisPatient>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Diagnosis> Diagnoses => Set<Diagnosis>();
    public DbSet<VitalsEntry> Vitals => Set<VitalsEntry>();
    public DbSet<LabOrder> LabOrders => Set<LabOrder>();
    public DbSet<LabResult> LabResults => Set<LabResult>();
    public DbSet<RadiologyOrder> RadiologyOrders => Set<RadiologyOrder>();
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<PrescriptionItem> PrescriptionItems => Set<PrescriptionItem>();
    public DbSet<StockBatch> Stock => Set<StockBatch>();
    public DbSet<Tariff> Tariffs => Set<Tariff>();
    public DbSet<Charge> Charges => Set<Charge>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WardTask> Tasks => Set<WardTask>();
    public DbSet<NursingNote> Notes => Set<NursingNote>();
    public DbSet<Counter> Counters => Set<Counter>();
    public DbSet<SyncLogEntry> SyncLog => Set<SyncLogEntry>();
    public DbSet<HisAudit> Audit => Set<HisAudit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>().HasKey(t => t.Id);
        b.Entity<Tenant>().HasIndex(t => t.Code).IsUnique();
        Filter<Department>(b); Filter<Bed>(b); Filter<Staff>(b); Filter<HisPatient>(b); Filter<Visit>(b); Filter<Diagnosis>(b);
        Filter<VitalsEntry>(b); Filter<LabOrder>(b); Filter<LabResult>(b); Filter<RadiologyOrder>(b); Filter<Prescription>(b);
        Filter<PrescriptionItem>(b); Filter<StockBatch>(b); Filter<Tariff>(b); Filter<Charge>(b); Filter<Invoice>(b); Filter<Payment>(b);
        Filter<WardTask>(b); Filter<NursingNote>(b); Filter<Counter>(b);
        // Integration entities (outboxes, claim records, device registrations…) declared anywhere in this assembly get the
        // same key + tenant filter without editing this file.
        var filter = typeof(HospitalDbContext).GetMethod(nameof(Filter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var t in typeof(TenantEntity).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(TenantEntity)) && !t.IsAbstract && b.Model.FindEntityType(t) is null))
            filter.MakeGenericMethod(t).Invoke(this, [b]);

        b.Entity<HisPatient>().HasIndex(p => new { p.TenantId, p.Mrn }).IsUnique();
        b.Entity<HisPatient>().HasIndex(p => new { p.TenantId, p.Nik });
        b.Entity<HisPatient>().Ignore(p => p.FullName);
        b.Entity<Visit>().HasIndex(v => new { v.TenantId, v.Status, v.DepartmentId });
        b.Entity<VitalsEntry>().HasIndex(v => new { v.TenantId, v.PatientId, v.Time });
        b.Entity<Counter>().HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
        b.Entity<Counter>().Property(c => c.Value).IsConcurrencyToken();
        b.Entity<Tariff>().HasIndex(t => new { t.TenantId, t.Code }).IsUnique();
        b.Entity<Charge>().Ignore(c => c.Amount);
        b.Entity<LabOrder>().HasMany(o => o.Results).WithOne().HasForeignKey(r => r.LabOrderId);
        b.Entity<Prescription>().HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PrescriptionId);

        b.Entity<SyncLogEntry>().HasKey(s => s.Seq);
        b.Entity<SyncLogEntry>().Property(s => s.Seq).ValueGeneratedOnAdd();
        b.Entity<SyncLogEntry>().HasIndex(s => new { s.TenantId, s.Type, s.RecordId, s.Latest });
        b.Entity<SyncLogEntry>().HasQueryFilter(s => s.TenantId == CurrentTenant);
        b.Entity<HisAudit>().HasKey(a => a.Id);
        b.Entity<HisAudit>().HasQueryFilter(a => a.TenantId == CurrentTenant);

        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            p.SetPrecision(18); // rupiah amounts
    }

    private void Filter<T>(ModelBuilder b) where T : TenantEntity
    {
        b.Entity<T>().HasKey(e => e.Id);
        b.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenant);
        b.Entity<T>().HasIndex(e => e.TenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Stamp()
    {
        foreach (var e in ChangeTracker.Entries())
        {
            string? owner = e.Entity switch { TenantEntity t => t.TenantId, SyncLogEntry s => s.TenantId, HisAudit a => a.TenantId, _ => null };
            if (owner is null) continue;
            if (e.State == EntityState.Added && owner.Length == 0)
            {
                switch (e.Entity)
                {
                    case TenantEntity t: t.TenantId = tenant.TenantId; break;
                    case SyncLogEntry s: s.TenantId = tenant.TenantId; break;
                    case HisAudit a: a.TenantId = tenant.TenantId; break;
                }
                continue;
            }
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && owner != tenant.TenantId)
                throw new TenantViolationException($"{e.Entity.GetType().Name} belongs to tenant '{owner}', not '{tenant.TenantId}'.");
        }
    }

    /// <summary>Next value of a per-tenant counter (MRN, queue number, invoice…), safe under concurrent use.</summary>
    public async Task<long> NextAsync(string key, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            var c = await Counters.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (c is null) { c = new Counter { Key = key, Value = 0 }; Counters.Add(c); }
            c.Value++;
            try { await SaveChangesAsync(ct); return c.Value; }
            catch (DbUpdateException) when (attempt < 5)
            {
                Entry(c).State = EntityState.Detached;
            }
        }
    }

    public void AuditEvent(AuditAction action, string resource, string? patientId = null, string? detail = null) =>
        Audit.Add(new HisAudit { At = DateTime.UtcNow, User = tenant.UserName, Action = action, Resource = resource, PatientId = patientId, Detail = detail });
}
