using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Medical.His;
using Medical.Hospital;
using Medical.Sync;
using Medical.Sync.Rounds;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Medical.Tests;

/// <summary>v2.1 platform: per-device sync tokens with ward enforcement, database-per-tenant, migrations, sign-in mapping.</summary>
public class V21PlatformTests(HisAppFactory app) : IClassFixture<HisAppFactory>
{
    [Fact]
    public async Task DeviceTokensAreHashedWardBoundAndRevocable()
    {
        using var f = new HisFixture(seed: true);
        await using var db = f.Ctx("rs-nusantara");
        var reg = new DeviceRegistry(db, f.Clock);
        var e = await reg.EnrollAsync("tablet-1", "Tablet 1", "MELATI");
        Assert.StartsWith("mdn_", e.Token);
        Assert.Equal("MELATI", e.WardCode);
        Assert.DoesNotContain(e.Token, e.Device.TokenHash);                     // only the hash is stored
        Assert.Equal(e.Device.Id, (await reg.AuthenticateAsync(e.Token))?.Id);
        Assert.NotNull(e.Device.LastSeenAt);
        Assert.Null(await reg.AuthenticateAsync(e.Token[..^2] + "xx"));
        Assert.Null(await reg.AuthenticateAsync("mdn_short"));
        Assert.Null(await reg.AuthenticateAsync(null));

        var again = await reg.EnrollAsync("tablet-1", "Tablet 1", "MAWAR");      // re-enrolment replaces the old token
        Assert.Null(await reg.AuthenticateAsync(e.Token));
        Assert.NotNull(await reg.AuthenticateAsync(again.Token));
        await reg.RevokeAsync(again.Device.Id);
        Assert.Null(await reg.AuthenticateAsync(again.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => reg.EnrollAsync("tablet-2", "x", "NOPE"));

        await using var other = f.Ctx("rs-bahari");                             // tokens do not cross hospitals
        var mine = await reg.EnrollAsync("tablet-3", "Tablet 3", "MELATI");
        Assert.Null(await new DeviceRegistry(other, f.Clock).AuthenticateAsync(mine.Token));
    }

    [Fact]
    public async Task EnrolledTabletIsConfinedToItsWard()
    {
        var nurse = app.SignedIn("rs-nusantara", "ns.dewi");
        var enrol = await nurse.PostAsJsonAsync("/api/rs-nusantara/devices", new DeviceEnrolmentRequest("tablet-mel", "MELATI", "Tablet Melati"));
        Assert.Equal(HttpStatusCode.OK, enrol.StatusCode);
        string token = JsonDocument.Parse(await enrol.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var anon = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.NotEqual(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/rs-nusantara/devices", new DeviceEnrolmentRequest("x", "MELATI", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.PostAsJsonAsync("/api/rs-bahari/devices", new DeviceEnrolmentRequest("x", "MELATI", null))).StatusCode);

        var tablet = app.CreateClient();
        tablet.DefaultRequestHeaders.Add("X-Device-Token", token);
        HttpContent Pull() => JsonContent.Create(new SyncPullRequest("tablet-mel", 0), options: SyncJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, (await tablet.PostAsync("/api/rs-nusantara/sync/pull?ward=ICU", Pull())).StatusCode);
        var pulled = await (await tablet.PostAsync("/api/rs-nusantara/sync/pull?ward=MELATI", Pull())).Content.ReadFromJsonAsync<SyncPullResult>(SyncJson.Options);
        var patients = pulled!.Changes.Where(c => c.Type == RoundsTypes.Patient).Select(c => c.Data!.Deserialize<RoundsPatient>(SyncJson.Options)!).ToList();
        Assert.NotEmpty(patients);
        Assert.All(patients, p => Assert.Equal("Bangsal Melati", p.Ward));
        // Without ?ward the device still gets only its own ward.
        var unscoped = await (await tablet.PostAsync("/api/rs-nusantara/sync/pull", Pull())).Content.ReadFromJsonAsync<SyncPullResult>(SyncJson.Options);
        Assert.All(unscoped!.Changes.Where(c => c.Type == RoundsTypes.Patient), c => Assert.Equal("Bangsal Melati", c.Data!["ward"]!.GetValue<string>()));

        string icuVisit, icuPatient;
        using (var scope = app.Services.CreateScope())
        {
            await using var db = new HospitalDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<HospitalDbContext>>(), new FixedTenantContext("rs-nusantara"));
            var icu = await db.Departments.FirstAsync(d => d.Code == "ICU");
            var bed = await db.Beds.FirstAsync(b => b.WardId == icu.Id && b.PatientId != null);
            var visit = await db.Visits.FirstAsync(v => v.BedId == bed.Id && v.PatientId == bed.PatientId);
            (icuVisit, icuPatient) = (visit.Id, visit.PatientId);
        }
        var mel = patients[0];
        SyncChange Vitals(string visit, string patient) => new(RoundsTypes.Vitals, Guid.NewGuid().ToString("n"), 1, "tablet-mel",
            JsonSerializer.SerializeToNode(new RoundsVitals(Guid.NewGuid().ToString("n"), patient, visit, DateTime.UtcNow, 18, 97, false, 120, 80, 80, 37, "Alert", 0, "ns.dewi"), SyncJson.Options)!.AsObject());
        var push = await tablet.PostAsJsonAsync("/api/rs-nusantara/sync/push",
            new SyncPushRequest("tablet-mel", [Vitals(mel.Id, mel.PatientId), Vitals(icuVisit, icuPatient), Vitals(mel.Id, icuPatient)]), SyncJson.Options);
        var result = (await push.Content.ReadFromJsonAsync<SyncPushResult>(SyncJson.Options))!;
        Assert.Single(result.Accepted);
        Assert.Equal(2, result.Rejected.Count);                                  // another ward; patient does not match the admission
        Assert.Contains(result.Rejected, r => r.Reason.Contains("another ward"));

        // A token speaks only for its own device id; a wrong token is 401.
        Assert.Equal(HttpStatusCode.Forbidden, (await tablet.PostAsJsonAsync("/api/rs-nusantara/sync/push", new SyncPushRequest("tablet-other", []), SyncJson.Options)).StatusCode);
        var forged = app.CreateClient();
        forged.DefaultRequestHeaders.Add("X-Device-Token", token[..^3] + "abc");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.PostAsync("/api/rs-nusantara/sync/pull?ward=MELATI", Pull())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.PostAsync("/api/rs-bahari/sync/pull?ward=MELATI", Pull())).StatusCode);   // other hospital
    }

    [Fact]
    public async Task DatabasePerTenantRoutesEachHospitalToItsOwnFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"his-pt-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        string Cs(string n) => $"Data Source={Path.Combine(dir, n + ".db")}";
        try
        {
            var opts = HospitalDatabase.Options(HospitalProvider.Sqlite, Cs("catalog"), new Dictionary<string, string> { ["t1"] = Cs("t1"), ["t2"] = Cs("t2") });
            await HospitalDatabase.MigrateAsync(opts);
            await using (var db = new HospitalDbContext(opts, new FixedTenantContext("t1")))
            {
                db.Staff.Add(new Staff { UserName = "ns.satu", Name = "Ners Satu", Role = StaffRole.Nurse });
                await db.SaveChangesAsync();
            }
            await using (var db = new HospitalDbContext(opts, new FixedTenantContext("t2")))
                Assert.Empty(await db.Staff.ToListAsync());

            int Count(string file)
            {
                using var c = new SqliteConnection(Cs(file));
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Staff";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            Assert.Equal(1, Count("t1"));
            Assert.Equal(0, Count("t2"));
            Assert.Equal(0, Count("catalog"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LegacyEnsureCreatedDatabaseIsBaselinedThenMigrated()
    {
        string file = Path.Combine(Path.GetTempPath(), $"his-legacy-{Guid.NewGuid():n}.db");
        var opts = HospitalDatabase.Options(HospitalProvider.Sqlite, $"Data Source={file}");
        try
        {
            // Recreate exactly what v2.0 left behind: the V20 schema with data and no migrations history.
            await using (var db = HospitalDatabase.MigrationContext(opts))
            {
                var first = db.Database.GetMigrations().First();
                await db.GetService<IMigrator>().MigrateAsync(first);
                db.Tenants.Add(new Tenant { Id = "old", Code = "OLD", Name = "RS Lama", City = "Solo" });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
            }
            await HospitalDatabase.MigrateAsync(opts);
            await using (var db = HospitalDatabase.MigrationContext(opts))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Equal("RS Lama", (await db.Tenants.SingleAsync()).Name);   // data kept
                Assert.Empty(await db.Set<DeviceRegistration>().IgnoreQueryFilters().ToListAsync()); // v2.1 tables added
            }
            await HospitalDatabase.MigrateAsync(opts);                          // idempotent
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task SignInMapsStaffRecordAndRejectsStrangers()
    {
        using var f = new HisFixture(seed: true);
        var p = await HisAuth.PrincipalAsync(f.Options, "rs-nusantara", "ns.dewi", "en", "oidc");
        Assert.NotNull(p);
        Assert.Equal("Nurse", p!.FindFirst(System.Security.Claims.ClaimTypes.Role)!.Value);
        Assert.Equal("rs-nusantara", p.FindFirst("tenant")!.Value);
        Assert.Equal("oidc", p.FindFirst("amr")!.Value);
        Assert.Equal("en", p.FindFirst("lang")!.Value);
        Assert.Null(await HisAuth.PrincipalAsync(f.Options, "rs-nusantara", "nobody", null, "oidc"));
        Assert.Null(await HisAuth.PrincipalAsync(f.Options, "nope", "ns.dewi", null, "oidc"));
        Assert.Null(await HisAuth.PrincipalAsync(f.Options, "", "ns.dewi", null, "oidc"));
        await using var db = f.Ctx("rs-nusantara");
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Detail != null && a.Detail.Contains("ns.dewi (oidc)"));

        Assert.Equal("/x", HisAuth.LocalOr("/x", "/f"));
        Assert.Equal("/f", HisAuth.LocalOr("//evil", "/f"));
        Assert.Equal("/f", HisAuth.LocalOr("/\\evil", "/f"));
        Assert.Equal("/f", HisAuth.LocalOr("https://evil", "/f"));
        Assert.Equal("/f", HisAuth.LocalOr(null, "/f"));
    }
}
