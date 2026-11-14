using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Medical.Hospital;

/// <summary>Database engines with shipped migrations.</summary>
public enum HospitalProvider { Sqlite, SqlServer }

/// <summary>SQLite flavour of the hospital context: owns the SQLite migrations (Migrations/Sqlite).</summary>
public sealed class SqliteHospitalDbContext(DbContextOptions<HospitalDbContext> options, ITenantContext tenant) : HospitalDbContext(options, tenant);

/// <summary>SQL Server flavour of the hospital context: owns the SQL Server migrations (Migrations/SqlServer).</summary>
public sealed class SqlServerHospitalDbContext(DbContextOptions<HospitalDbContext> options, ITenantContext tenant) : HospitalDbContext(options, tenant);

/// <summary>Used by <c>dotnet ef</c> to create the SQLite context at design time.</summary>
public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteHospitalDbContext>
{
    public SqliteHospitalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite("Data Source=design.db").Options, new FixedTenantContext(""));
}

/// <summary>Used by <c>dotnet ef</c> to create the SQL Server context at design time.</summary>
public sealed class SqlServerDesignFactory : IDesignTimeDbContextFactory<SqlServerHospitalDbContext>
{
    public SqlServerHospitalDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HospitalDbContext>().UseSqlServer("Server=.;Database=MedicalNetDesign;Trusted_Connection=True;TrustServerCertificate=True").Options, new FixedTenantContext(""));
}

/// <summary>
/// Routes a tenant's connections to its own database (database-per-tenant). Contexts whose tenant has an entry use that
/// connection string; others (and the empty "catalog" tenant used for the tenant list) keep the configured one. The
/// shared-schema tenant filters stay active as a second line of defence.
/// </summary>
public sealed class TenantDatabaseInterceptor(IReadOnlyDictionary<string, string> connectionStrings) : DbConnectionInterceptor
{
    public IReadOnlyDictionary<string, string> ConnectionStrings => connectionStrings;

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Route(connection, eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Route(connection, eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Route(DbConnection connection, DbContext? context)
    {
        if (context is HospitalDbContext h && connectionStrings.TryGetValue(h.CurrentTenant, out var cs) && connection.ConnectionString != cs)
            connection.ConnectionString = cs;
    }
}

/// <summary>Creates options and applies migrations for the supported providers, shared-schema or database-per-tenant.</summary>
public static class HospitalDatabase
{
    /// <param name="connectionString">The shared database (or, with <paramref name="perTenant"/>, the catalog holding the tenant list).</param>
    /// <param name="perTenant">tenant id → connection string of that tenant's own database.</param>
    public static DbContextOptions<HospitalDbContext> Options(HospitalProvider provider, string connectionString, IReadOnlyDictionary<string, string>? perTenant = null)
    {
        var b = new DbContextOptionsBuilder<HospitalDbContext>();
        _ = provider == HospitalProvider.SqlServer ? b.UseSqlServer(connectionString) : b.UseSqlite(connectionString);
        if (perTenant is { Count: > 0 }) b.AddInterceptors(new TenantDatabaseInterceptor(perTenant));
        return b.Options;
    }

    public static HospitalProvider ProviderOf(DbContextOptions<HospitalDbContext> options) =>
        options.Extensions.Any(e => e.GetType().Name.Contains("SqlServer", StringComparison.Ordinal)) ? HospitalProvider.SqlServer : HospitalProvider.Sqlite;

    /// <summary>The context class that owns the migrations of the provider in <paramref name="options"/>.</summary>
    public static HospitalDbContext MigrationContext(DbContextOptions<HospitalDbContext> options, string tenantId = "") =>
        ProviderOf(options) == HospitalProvider.SqlServer
            ? new SqlServerHospitalDbContext(options, new FixedTenantContext(tenantId, "migrations"))
            : new SqliteHospitalDbContext(options, new FixedTenantContext(tenantId, "migrations"));

    /// <summary>
    /// Applies pending migrations to the shared database, or to the catalog and every tenant database. Databases created
    /// by v2.0 with <c>EnsureCreated</c> (no migrations history) are baselined first so their tables are not recreated.
    /// </summary>
    public static async Task MigrateAsync(DbContextOptions<HospitalDbContext> options, CancellationToken ct = default)
    {
        var tenants = options.FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<TenantDatabaseInterceptor>().FirstOrDefault()?.ConnectionStrings.Keys ?? [];
        foreach (var t in tenants.Prepend(""))
        {
            await using var db = MigrationContext(options, t);
            await BaselineLegacyAsync(db, ct);
            await db.Database.MigrateAsync(ct);
        }
    }

    private static async Task BaselineLegacyAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (!await db.Database.CanConnectAsync(ct)) return;
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        if (applied.Any()) return;
        bool legacy;
        try { legacy = await db.Tenants.AnyAsync(ct) || true; }    // the Tenants table exists → created by EnsureCreated
        catch (Exception) { legacy = false; }
        if (!legacy) return;
        var first = db.Database.GetMigrations().First();
        string product = typeof(DbContext).Assembly.GetName().Version!.ToString(3);
        string create = db.Database.IsSqlServer()
            ? "IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL PRIMARY KEY, [ProductVersion] nvarchar(32) NOT NULL);"
            : "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);";
        await db.Database.ExecuteSqlRawAsync(create, ct);
        await db.Database.ExecuteSqlRawAsync(db.Database.IsSqlServer()
            ? "INSERT INTO [__EFMigrationsHistory] VALUES ({0}, {1})"
            : "INSERT INTO \"__EFMigrationsHistory\" VALUES ({0}, {1})", [first, product], ct);
    }
}
