using System.Net;
using System.Text.Json;
using Medical.His;
using Medical.Hospital;
using Medical.Rounds;
using Medical.Sync;
using Medical.Sync.Rounds;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Medical.Tests;

/// <summary>The HIS reference app hosted in-process with a temporary SQLite database and no device simulation.</summary>
public sealed class HisAppFactory : WebApplicationFactory<MonitoringHub>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"his-test-{Guid.NewGuid():n}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:His", $"Data Source={_db}");
        builder.UseSetting("His:SimulateDevices", "false");
        builder.UseSetting("His:SeedPatients", "24");
        builder.UseSetting("His:DeviceKeys:rs-nusantara", "test-key");
    }

    public HttpClient SignedIn(string tenant, string user)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var r = c.GetAsync($"/account/demo?tenant={tenant}&user={user}").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }
}

public class HisAppTests(HisAppFactory app) : IClassFixture<HisAppFactory>
{
    [Fact]
    public async Task PagesRequireSignInAndApisAreTenantScoped()
    {
        var anon = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health")).StatusCode);
        var page = await anon.GetAsync("/rs-nusantara/pharmacy");
        Assert.True(page.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        Assert.Contains("/login", page.Headers.Location!.ToString());

        var nurse = app.SignedIn("rs-nusantara", "ns.dewi");
        var dash = JsonDocument.Parse(await nurse.GetStringAsync("/api/rs-nusantara/dashboard")).RootElement;
        Assert.Equal("RS Medika Nusantara", dash.GetProperty("tenant").GetString());
        Assert.True(dash.GetProperty("inpatients").GetInt32() > 0);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/api/rs-bahari/dashboard")).StatusCode);   // another hospital

        string pid;
        using (var scope = app.Services.CreateScope())
        {
            var opts = scope.ServiceProvider.GetRequiredService<DbContextOptions<HospitalDbContext>>();
            await using var db = new HospitalDbContext(opts, new FixedTenantContext("rs-nusantara"));
            pid = (await db.Beds.FirstAsync(b => b.PatientId != null)).PatientId!;
        }
        var ips = await nurse.GetAsync($"/api/rs-nusantara/fhir/Patient/{pid}/$summary");
        Assert.Equal("application/fhir+json", ips.Content.Headers.ContentType!.MediaType);
        var bundle = JsonDocument.Parse(await ips.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Bundle", bundle.GetProperty("resourceType").GetString());
        Assert.Equal("document", bundle.GetProperty("type").GetString());

        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync("/account/demo?tenant=rs-nusantara&user=nobody")).StatusCode);   // unknown user → login?error
    }

    [Fact]
    public async Task SyncPullsAreWardScopedAndRedirectsStayLocal()
    {
        var device = app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", "test-key");
        var body = System.Net.Http.Json.JsonContent.Create(new SyncPullRequest("tablet-x", 0), options: SyncJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsync("/api/rs-nusantara/sync/pull", body)).StatusCode);                  // no ward
        Assert.Equal(HttpStatusCode.BadRequest, (await device.PostAsync("/api/rs-nusantara/sync/pull?ward=NOPE", body)).StatusCode);        // unknown ward
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsync("/api/rs-nusantara/sync/pull?ward=MELATI", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/rs-nusantara/sync/wards")).StatusCode);            // no key
        Assert.Contains("MELATI", await device.GetStringAsync("/api/rs-nusantara/sync/wards"));

        var anon = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var evil in new[] { "//evil.example/x", "/\\evil.example" })
        {
            var r = await anon.GetAsync($"/account/demo?tenant=rs-nusantara&user=admin&returnUrl={Uri.EscapeDataString(evil)}");
            Assert.Equal("/rs-nusantara", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task WardRoundsTabletSyncsOverHttp()
    {
        var settings = RoundsSettings.Demo with { DeviceKey = "test-key", DeviceId = "tablet-test" };
        using var tablet = new RoundsApp(settings, ":memory:", RoundsApp.HttpTransport(settings, app.Server.CreateHandler()));
        var first = await tablet.SyncAsync();
        Assert.True(first.Online, first.Error);
        var ward = tablet.Ward();
        Assert.NotEmpty(ward);
        Assert.All(ward, r => Assert.Equal("Bangsal Melati", r.Patient.Ward));
        Assert.True(ward.Zip(ward.Skip(1)).All(p => (p.First.News2 ?? -1) >= (p.Second.News2 ?? -1)));   // sickest first

        var row = ward[^1];
        var preview = RoundsApp.Preview(new VitalsInput(26, 91, false, 95, 60, 125, 39.2));
        Assert.True(preview.Score >= 7);
        var v = tablet.RecordVitals(row.Patient.Id, new VitalsInput(26, 91, false, 95, 60, 125, 39.2));
        tablet.AddNote(row.Patient.Id, "Akral dingin, CRT 3 detik. Lapor DPJP.");
        var chart = tablet.Chart(row.Patient.Id)!;
        if (chart.Tasks.FirstOrDefault(t => !t.Done) is { } task) tablet.CompleteTask(task.Id);
        Assert.True(tablet.PendingChanges >= 2);
        Assert.Equal(row.Patient.Id, tablet.Ward()[0].Patient.Id);           // now the sickest, from the device's own NEWS2
        Assert.True(tablet.Ward()[0].PendingSync);

        var report = await tablet.SyncAsync();
        Assert.True(report.Online);
        Assert.Empty(report.Conflicts);
        Assert.Equal(0, tablet.PendingChanges);
        using var scope = app.Services.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<DbContextOptions<HospitalDbContext>>();
        await using var db = new HospitalDbContext(opts, new FixedTenantContext("rs-nusantara"));
        var charted = await db.Vitals.SingleAsync(x => x.Id == v.Id);
        Assert.Equal(VitalsSource.MobileApp, charted.Source);
        Assert.Equal(preview.Score, charted.News2);
        Assert.Equal("ns.dewi", charted.RecordedBy);
        Assert.Contains(await db.Notes.ToListAsync(), n => n.Text.StartsWith("Akral dingin"));

        // A wrong device key is refused; the change stays queued.
        var bad = settings with { DeviceKey = "wrong" };
        using var rogue = new RoundsApp(bad, ":memory:", RoundsApp.HttpTransport(bad, app.Server.CreateHandler()));
        var refused = await rogue.SyncAsync();
        Assert.False(refused.Online);
        Assert.Contains("401", refused.Error);
    }

    [Fact]
    public async Task TabletWorksOfflineAndCatchesUp()
    {
        var server = new SyncServer(new InMemorySyncServerStore(), RoundsTypes.Policies);
        var net = new InProcessSyncTransport(server);
        var p = new RoundsPatient("visit-9", "pat-9", "000009", "Budi Santoso", 66, "L", "Bangsal Melati", "MEL-03", "J18.9 Pneumonia", null, DateTime.UtcNow, 3, DateTime.UtcNow.AddHours(-4), "Bpjs");
        server.Store.Put(new SyncChange(RoundsTypes.Patient, p.Id, 1, "server", JsonSerializer.SerializeToNode(p, SyncJson.Options)!.AsObject()));
        var path = Path.Combine(Path.GetTempPath(), $"rounds-{Guid.NewGuid():n}.db");
        try
        {
            using (var tablet = new RoundsApp(RoundsSettings.Demo, path, net))
            {
                await tablet.SyncAsync();
                net.Online = false;
                tablet.RecordVitals(p.Id, new VitalsInput(22, 94, true, 105, 70, 104, 38.3));
                var off = await tablet.SyncAsync();
                Assert.False(off.Online);
                Assert.False(tablet.Online);
                Assert.Equal(1, tablet.PendingChanges);
            }
            // The app is closed and reopened (e.g. battery swap): the queued vitals survive in SQLite.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var reopened = new RoundsApp(RoundsSettings.Demo, path, net);
            Assert.Equal(1, reopened.PendingChanges);
            Assert.Equal(8, reopened.Ward()[0].News2);                         // RR 22 (2) + SpO2 94 on O2 (1+2) + SBP 105 (1) + HR 104 (1) + T 38.3 (1)
            net.Online = true;
            var back = await reopened.SyncAsync();
            Assert.True(back.Online);
            Assert.Equal(1, back.Pushed);
            Assert.Equal(0, reopened.PendingChanges);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
