using Medical.Core;
using Medical.Emr.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Emr;

/// <summary>Domain ↔ entity mapping.</summary>
public static class EmrMapping
{
    public static PatientEntity ToEntity(PatientRecord r, DateTimeOffset now)
    {
        var p = r.Patient;
        return new PatientEntity
        {
            Id = p.Id, Mrn = p.Mrn, Nik = p.Nik, Given = p.Name.Given, Family = p.Name.Family, Gender = p.Gender, BirthDate = p.BirthDate,
            Phone = p.Phone, Email = p.Email, Address = p.Address, City = p.City, WeightKg = r.WeightKg, HeightCm = r.HeightCm, UpdatedAt = now,
            Encounters = r.Encounters.Select(e => new EncounterEntity
            {
                Id = e.Id, PatientId = p.Id, Class = e.Class, Status = e.Status, Start = e.Start, End = e.End, Practitioner = e.Practitioner, Location = e.Location,
                ReasonSystem = e.Reason?.System, ReasonCode = e.Reason?.Code, ReasonDisplay = e.Reason?.Display,
            }).ToList(),
            Vitals = r.Vitals.Select(v => ToEntity(p.Id, v)).ToList(),
            Observations = r.Observations.Select(ToEntity).ToList(),
            Conditions = r.Conditions.Select(c => new ConditionEntity
            {
                Id = c.Id, PatientId = p.Id, System = c.Code.System, Code = c.Code.Code, Display = c.Code.Display, Status = c.Status, Onset = c.Onset,
            }).ToList(),
            Medications = r.Medications.Select(m => new MedicationEntity
            {
                Id = m.Id, PatientId = p.Id, System = m.Medication.System, Code = m.Medication.Code, Display = m.Medication.Display, Generic = m.Generic,
                DoseMg = m.DoseMg, Route = m.Route, FrequencyPerDay = m.FrequencyPerDay, Start = m.Start, Instructions = m.Instructions,
            }).ToList(),
            Allergies = r.Allergies.Select(a => new AllergyEntity { Id = a.Id, PatientId = p.Id, Substance = a.Substance, Reaction = a.Reaction, Severity = a.Severity }).ToList(),
        };
    }

    public static VitalsEntity ToEntity(string patientId, VitalSigns v) => new()
    {
        PatientId = patientId, Timestamp = v.Timestamp, RespiratoryRate = v.RespiratoryRate, SpO2 = v.SpO2, OnSupplementalOxygen = v.OnSupplementalOxygen,
        SystolicBp = v.SystolicBp, DiastolicBp = v.DiastolicBp, HeartRate = v.HeartRate, TemperatureC = v.TemperatureC, Consciousness = v.Consciousness,
    };

    public static ObservationEntity ToEntity(ClinicalObservation o) => new()
    {
        Id = o.Id, PatientId = o.PatientId, EncounterId = o.EncounterId, System = o.Code.System, Code = o.Code.Code, Display = o.Code.Display,
        Value = o.Value, Unit = o.Unit, ValueText = o.ValueText, ReferenceLow = o.ReferenceLow, ReferenceHigh = o.ReferenceHigh,
        Flag = o.Flag, Status = o.Status, Effective = o.Effective,
    };

    public static Patient ToDomain(PatientEntity e) => new()
    {
        Id = e.Id, Mrn = e.Mrn, Nik = e.Nik, Name = new HumanName(e.Given, e.Family), Gender = e.Gender, BirthDate = e.BirthDate,
        Phone = e.Phone, Email = e.Email, Address = e.Address, City = e.City,
    };

    public static VitalSigns ToDomain(VitalsEntity v) => new()
    {
        Timestamp = v.Timestamp, RespiratoryRate = v.RespiratoryRate, SpO2 = v.SpO2, OnSupplementalOxygen = v.OnSupplementalOxygen,
        SystolicBp = v.SystolicBp, DiastolicBp = v.DiastolicBp, HeartRate = v.HeartRate, TemperatureC = v.TemperatureC, Consciousness = v.Consciousness,
    };

    public static PatientRecord ToRecord(PatientEntity? e) => e is null ? throw new ArgumentNullException(nameof(e)) : new PatientRecord
    {
        Patient = ToDomain(e),
        WeightKg = e.WeightKg,
        HeightCm = e.HeightCm,
        Encounters = e.Encounters.OrderBy(x => x.Start).Select(x => new Encounter
        {
            Id = x.Id, PatientId = x.PatientId, Class = x.Class, Status = x.Status, Start = x.Start, End = x.End, Practitioner = x.Practitioner, Location = x.Location,
            Reason = x.ReasonCode is null ? null : new ClinicalCode(x.ReasonSystem ?? "", x.ReasonCode, x.ReasonDisplay),
        }).ToArray(),
        Vitals = e.Vitals.OrderBy(v => v.Timestamp).Select(ToDomain).ToArray(),
        Observations = e.Observations.OrderBy(o => o.Effective).Select(o => new ClinicalObservation
        {
            Id = o.Id, PatientId = o.PatientId, EncounterId = o.EncounterId, Code = new ClinicalCode(o.System, o.Code, o.Display), Value = o.Value, Unit = o.Unit,
            ValueText = o.ValueText, ReferenceLow = o.ReferenceLow, ReferenceHigh = o.ReferenceHigh, Flag = o.Flag, Status = o.Status, Effective = o.Effective,
        }).ToArray(),
        Conditions = e.Conditions.OrderBy(c => c.Code, StringComparer.Ordinal).Select(c => new Condition { Id = c.Id, PatientId = c.PatientId, Code = new ClinicalCode(c.System, c.Code, c.Display), Status = c.Status, Onset = c.Onset }).ToArray(),
        Medications = e.Medications.OrderBy(m => m.Generic, StringComparer.Ordinal).Select(m => new MedicationOrder
        {
            Id = m.Id, PatientId = m.PatientId, Medication = new ClinicalCode(m.System, m.Code, m.Display), Generic = m.Generic, DoseMg = m.DoseMg,
            Route = m.Route, FrequencyPerDay = m.FrequencyPerDay, Start = m.Start, Instructions = m.Instructions,
        }).ToArray(),
        Allergies = e.Allergies.Select(a => new Allergy { Id = a.Id, PatientId = a.PatientId, Substance = a.Substance, Reaction = a.Reaction, Severity = a.Severity }).ToArray(),
    };
}

public enum EmrProvider { Sqlite, SqlServer, InMemory }

/// <summary>EMR registration options.</summary>
public sealed class EmrOptions
{
    public EmrProvider Provider { get; set; } = EmrProvider.Sqlite;
    /// <summary>Connection string (SQLite default: <c>Data Source=medical.db</c>; in-memory: database name).</summary>
    public string ConnectionString { get; set; } = "Data Source=medical.db";
    /// <summary>Redis configuration (e.g. <c>localhost:6379</c>); when null an in-process memory cache is used.</summary>
    public string? Redis { get; set; }
    /// <summary>Persist audit events in the EMR database instead of memory.</summary>
    public bool PersistAudit { get; set; } = true;
}

public static class EmrBuilderExtensions
{
    /// <summary>Registers <see cref="MedicalDbContext"/> (pooled factory + scoped context), <see cref="PatientRepository"/>, cache and audit log.</summary>
    public static MedicalBuilder AddEmr(this MedicalBuilder builder, Action<EmrOptions>? configure = null) =>
        builder.TryAddModule("emr", s =>
        {
            var o = new EmrOptions();
            configure?.Invoke(o);
            void Options(DbContextOptionsBuilder b)
            {
                _ = o.Provider switch
                {
                    EmrProvider.SqlServer => b.UseSqlServer(o.ConnectionString, x => x.EnableRetryOnFailure()),
                    EmrProvider.InMemory => b.UseInMemoryDatabase(o.ConnectionString),
                    _ => b.UseSqlite(o.ConnectionString),
                };
            }
            s.AddPooledDbContextFactory<MedicalDbContext>(Options);
            s.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<MedicalDbContext>>().CreateDbContext());
            if (o.Redis is { } redis) s.AddStackExchangeRedisCache(r => { r.Configuration = redis; r.InstanceName = "mednet:"; });
            else s.AddDistributedMemoryCache();
            if (o.PersistAudit) s.Replace(ServiceDescriptor.Singleton<IAuditLog, EfAuditLog>());
            s.TryAddScoped(sp => new PatientRepository(sp.GetRequiredService<MedicalDbContext>(), sp.GetRequiredService<IAuditLog>(),
                sp.GetService<IDistributedCache>(), sp.GetService<TimeProvider>()));
        });

    /// <summary>Creates the schema if missing (use EF migrations for production deployments).</summary>
    public static async Task EnsureEmrDatabaseAsync(this IServiceProvider sp, CancellationToken ct = default)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<MedicalDbContext>>().CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
    }
}
