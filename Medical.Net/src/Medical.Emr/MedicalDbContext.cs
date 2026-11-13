using Medical.Emr.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Medical.Emr;

/// <summary>EF Core context of the Medical.Net EMR schema (provider-agnostic: SQL Server, SQLite, in-memory…).</summary>
public sealed class MedicalDbContext(DbContextOptions<MedicalDbContext> options) : DbContext(options)
{
    public DbSet<PatientEntity> Patients => Set<PatientEntity>();
    public DbSet<EncounterEntity> Encounters => Set<EncounterEntity>();
    public DbSet<VitalsEntity> Vitals => Set<VitalsEntity>();
    public DbSet<ObservationEntity> Observations => Set<ObservationEntity>();
    public DbSet<ConditionEntity> Conditions => Set<ConditionEntity>();
    public DbSet<MedicationEntity> Medications => Set<MedicationEntity>();
    public DbSet<AllergyEntity> Allergies => Set<AllergyEntity>();
    public DbSet<AuditEntity> AuditEvents => Set<AuditEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Portable, sortable timestamps on every provider (SQLite has no native DateTimeOffset ordering).
        b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        b.Properties<string>().HaveMaxLength(256);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<PatientEntity>(e =>
        {
            e.ToTable("Patients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.HasIndex(x => x.Mrn).IsUnique();
            e.HasIndex(x => x.Nik);
            e.HasIndex(x => new { x.Family, x.Given });
            e.Property(x => x.Gender).HasConversion<string>().HasMaxLength(16);
            e.HasMany(x => x.Encounters).WithOne().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Vitals).WithOne().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Observations).WithOne().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Conditions).WithOne().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Medications).WithOne().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Allergies).WithOne().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<EncounterEntity>(e =>
        {
            e.ToTable("Encounters");
            e.HasKey(x => x.Id);
            e.Property(x => x.Class).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
        m.Entity<VitalsEntity>(e =>
        {
            e.ToTable("VitalSigns");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.Timestamp });
            e.Property(x => x.Consciousness).HasConversion<string>().HasMaxLength(16);
        });
        m.Entity<ObservationEntity>(e =>
        {
            e.ToTable("Observations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.Code, x.Effective });
            e.Property(x => x.Flag).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Display).HasMaxLength(512);
        });
        m.Entity<ConditionEntity>(e =>
        {
            e.ToTable("Conditions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.Code });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Display).HasMaxLength(512);
        });
        m.Entity<MedicationEntity>(e =>
        {
            e.ToTable("MedicationOrders");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.Generic });
            e.Property(x => x.Display).HasMaxLength(512);
        });
        m.Entity<AllergyEntity>(e =>
        {
            e.ToTable("Allergies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
        });
        m.Entity<AuditEntity>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PatientId, x.Timestamp });
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
        });
    }
}
