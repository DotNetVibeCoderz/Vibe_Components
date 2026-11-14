using System.Text.Json;
using System.Text.Json.Nodes;
using IoTCom.Net.Protocols.Hl7;
using Medical.Core;
using Medical.Devices;
using Medical.Hospital;
using Medical.Hospital.Monitoring;
using Medical.Sync;
using Medical.Sync.Rounds;
using Medical.Visualization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Medical.Gallery.Shared;

public sealed record HisView(IReadOnlyList<Tenant> Tenants, string TenantId, HospitalDashboard Dashboard, IReadOnlyList<Department> Wards, string WardId, IReadOnlyList<BedTile> Board);

public sealed record MonitorFrame(int Minute, IReadOnlyList<BedSnapshot> Beds, IReadOnlyList<EarlyWarningAlert> Alerts, string Chart, int Messages);

public sealed record SyncNodeRow(string Type, string Id, string Summary, bool Dirty);

public sealed record SyncStep(int Index, Localized Title, Localized Explanation, IReadOnlyList<SyncNodeRow> Server, IReadOnlyList<SyncNodeRow> TabletA, IReadOnlyList<SyncNodeRow> TabletB,
    bool AOnline, bool BOnline, IReadOnlyList<string> Log);

public static partial class Scenarios
{
    // ------------------------------------------------------------------ multi-tenant HIS

    private static readonly DateTimeOffset HisNow = new(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<(SqliteConnection Conn, DbContextOptions<HospitalDbContext> Options)> HisDb = new(() =>
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite(conn).Options;
        HospitalSeeder.SeedAsync(opts, new HisClock(HisNow), 40).GetAwaiter().GetResult();
        return (conn, opts);
    });



    private sealed class HisClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static readonly SemaphoreSlim HisGate = new(1, 1);

    /// <summary>The dashboard and bed board of one tenant of the seeded two-hospital installation.</summary>
    public static async Task<HisView> His(string? tenantId = null, string? wardCode = null)
    {
        await HisGate.WaitAsync();
        try
        {
            var opts = HisDb.Value.Options;
            List<Tenant> tenants;
            await using (var root = new HospitalDbContext(opts, new FixedTenantContext(""))) tenants = await root.Tenants.OrderBy(t => t.Name).ToListAsync();
            string tid = tenantId ?? tenants[0].Id;
            await using var db = new HospitalDbContext(opts, new FixedTenantContext(tid, "gallery"));
            var clock = new HisClock(HisNow);
            var dash = await new DashboardService(db, clock).SnapshotAsync();
            var wards = await db.Departments.Where(d => d.Kind == DepartmentKind.Ward || d.Kind == DepartmentKind.Icu).OrderBy(d => d.Kind).ThenBy(d => d.Code).ToListAsync();
            var ward = wards.FirstOrDefault(w => w.Code == wardCode) ?? wards.First(w => dash.Wards.Any(o => o.WardId == w.Id && o.Occupied > 0));
            var board = await new DashboardService(db, clock).BedBoardAsync(ward.Id);
            return new HisView(tenants, tid, dash, wards, ward.Id, board);
        }
        finally { HisGate.Release(); }
    }

    // ------------------------------------------------------------------ live monitoring (IoTCom.Net simulators → MLLP → NEWS2 engine)

    private static readonly Lazy<Task<IReadOnlyList<MonitorFrame>>> MonitorRun = new(RunMonitorsAsync);

    public const int MonitorMinutes = 240;

    /// <summary>The ICU after <paramref name="minute"/> minutes of simulated time (frames every 10 minutes).</summary>
    public static async Task<MonitorFrame> Monitoring(int minute = 180)
    {
        var frames = await MonitorRun.Value;
        return frames.Last(f => f.Minute <= Math.Clamp(minute, 10, MonitorMinutes));
    }

    private static async Task<IReadOnlyList<MonitorFrame>> RunMonitorsAsync()
    {
        var t0 = HisNow;
        await using var feed = Hl7MonitorFeed.InProcess("gallery-monitors");
        var engine = new MonitoringEngine();
        var alerts = new List<EarlyWarningAlert>();
        var history = new Dictionary<string, List<(double Minute, double News2)>>();
        engine.Alert += a => { lock (alerts) alerts.Add(a); };
        (string Dev, string Bed, PatientScenario S, string Name)[] beds =
        [
            ("ICU-MON-01", "ICU-01", PatientScenario.Sepsis, "Budi Santoso"), ("ICU-MON-02", "ICU-02", PatientScenario.Stable, "Sari Wulandari"),
            ("ICU-MON-03", "ICU-03", PatientScenario.Hypoxia, "Agus Hartono"), ("ICU-MON-04", "ICU-04", PatientScenario.Hypertension, "Dewi Pratiwi"),
        ];
        foreach (var b in beds) { engine.Bind(b.Dev, b.Bed, b.Dev, b.Name, null); history[b.Dev] = []; }
        using var cts = new CancellationTokenSource();
        var gateway = new DeviceGateway(engine, feed);
        var run = gateway.RunAsync(cts.Token);
        await using var sim = await BedsideSimulation.StartAsync(feed, beds.Select(b => (b.Dev, b.Bed[^2..], b.S)), seed: 5);
        var frames = new List<MonitorFrame>();
        long expected = 0;
        for (int m = 1; m <= MonitorMinutes; m++)
        {
            await sim.TickAsync(TimeSpan.FromMinutes(1), t0.AddMinutes(m));
            expected += beds.Length * 6;
            for (int i = 0; i < 200 && gateway.Readings < expected; i++) await Task.Delay(2);
            foreach (var s in engine.Snapshots) if (s.News2 is { } n) history[s.DeviceId].Add((m, n.Score));
            if (m % 10 == 0)
            {
                string[] colors = [SvgTheme.Red, SvgTheme.Teal, SvgTheme.Amber, SvgTheme.Muted];
                var series = beds.Select((b, i) => new XySeries(b.Bed, history[b.Dev].Where(p => p.Minute % 2 == 0).ToList(), colors[i])).ToList();
                series.Add(new XySeries("5", [(0, 5), (MonitorMinutes, 5)], SvgTheme.Amber, Dashed: true));    // urgent review
                series.Add(new XySeries("7", [(0, 7), (MonitorMinutes, 7)], SvgTheme.Red, Dashed: true));      // emergency response
                var chart = Charts.XyChart(series, 0, MonitorMinutes, 0, 20, "minutes", "NEWS2", null, 620, 230);
                lock (alerts) frames.Add(new MonitorFrame(m, engine.Snapshots, alerts.AsEnumerable().Reverse().ToList(), chart, (int)feed.MessagesReceived));
            }
        }
        cts.Cancel();
        await run;
        return frames;
    }

    // ------------------------------------------------------------------ offline-first sync

    private static readonly Lazy<IReadOnlyList<SyncStep>> SyncRun = new(RunSync);

    public static SyncStep OfflineSync(int step) => SyncRun.Value[Math.Clamp(step, 0, SyncRun.Value.Count - 1)];
    public static int SyncSteps => SyncRun.Value.Count;

    private static IReadOnlyList<SyncStep> RunSync()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(HisNow);
        var server = new SyncServer(new InMemorySyncServerStore(), RoundsTypes.Policies, new HybridLogicalClock(clock));
        var net = new InProcessSyncTransport(server);
        var netA = new SwitchableTransport(net);
        var netB = new SwitchableTransport(net);
        var storeA = new SqliteSyncStore();
        var storeB = new SqliteSyncStore();
        var a = new SyncClient(storeA, netA, "tablet-A", new HybridLogicalClock(clock));
        var b = new SyncClient(storeB, netB, "tablet-B", new HybridLogicalClock(clock));
        var log = new List<string>();
        var steps = new List<SyncStep>();
        JsonObject J<T>(T v) => JsonSerializer.SerializeToNode(v, SyncJson.Options)!.AsObject();
        void Put<T>(string type, string id, T v) { clock.Advance(TimeSpan.FromSeconds(30)); server.Store.Put(new SyncChange(type, id, new HybridLogicalClock(clock).Now(), "server", J(v))); }
        static string Sum(string type, JsonObject? d) => d is null ? "(deleted)" : type switch
        {
            RoundsTypes.Patient => $"{d["bed"]} {d["name"]} · NEWS2 {d["news2"]}",
            RoundsTypes.Vitals => $"RR {d["respiratoryRate"]} SpO₂ {d["spO2"]} HR {d["heartRate"]} → NEWS2 {d["news2"]} ({d["recordedBy"]})",
            RoundsTypes.Task => $"{d["title"]} · {((bool?)d["done"] == true ? "✓ " + d["doneBy"] : "open")}",
            RoundsTypes.Note => $"{d["author"]}: {d["text"]}",
            _ => d.ToJsonString(),
        };
        List<SyncNodeRow> ServerRows() => server.Store.Since(0, 1000, null, null).Select(c => new SyncNodeRow(c.Type, c.Id, Sum(c.Type, c.Data), false)).ToList();
        static List<SyncNodeRow> Rows(SqliteSyncStore s) => new[] { RoundsTypes.Patient, RoundsTypes.Task, RoundsTypes.Vitals, RoundsTypes.Note }
            .SelectMany(t => s.All(t)).Select(r => new SyncNodeRow(r.Type, r.Id, Sum(r.Type, r.Data), r.Dirty)).ToList();
        void Snap(Localized title, Localized why) => steps.Add(new SyncStep(steps.Count, title, why, ServerRows(), Rows(storeA), Rows(storeB), netA.Online, netB.Online, log.ToList()));
        async Task Sync(SyncClient c, string name) { clock.Advance(TimeSpan.FromSeconds(20)); var r = await c.SyncAsync(); log.Add($"{name}: {r}" + (r.Conflicts.Count > 0 ? " — " + string.Join("; ", r.Conflicts.Select(x => $"{x.Change.Type} {x.Reason}")) : "")); }

        Put(RoundsTypes.Patient, "visit-7", new RoundsPatient("visit-7", "pat-7", "000007", "Budi Santoso", 66, "L", "Bangsal Melati", "MEL-03", "J18.9 Pneumonia", null, HisNow.UtcDateTime.AddDays(-1), 3, HisNow.UtcDateTime.AddHours(-4), "Bpjs"));
        Put(RoundsTypes.Task, "task-1", new RoundsTask("task-1", "pat-7", "visit-7", "Ambil sampel darah ulang", HisNow.UtcDateTime.AddHours(2), false, null, null));
        log.Add("HIS: ward census and a task published to the Melati ward");
        Snap(new("The HIS publishes the ward", "SIMRS menerbitkan daftar bangsal"), new("The server holds the census (read-only on devices) and a task (last-writer-wins).", "Server menyimpan daftar pasien (hanya-baca di perangkat) dan satu tugas (penulis terakhir menang)."));

        Sync(a, "tablet-A").GetAwaiter().GetResult();
        Sync(b, "tablet-B").GetAwaiter().GetResult();
        Snap(new("Both tablets sync", "Kedua tablet sinkron"), new("Each tablet now has its own SQLite copy of the ward.", "Setiap tablet kini punya salinan SQLite bangsal sendiri."));

        netA.Online = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        a.Save(RoundsTypes.Vitals, "vit-A1", new RoundsVitals("vit-A1", "pat-7", "visit-7", clock.GetUtcNow().UtcDateTime, 25, 91, false, 98, 60, 118, 38.9, "Alert", 10, "ns.dewi"));
        clock.Advance(TimeSpan.FromMinutes(1));
        a.Save(RoundsTypes.Task, "task-1", new RoundsTask("task-1", "pat-7", "visit-7", "Ambil sampel darah ulang", HisNow.UtcDateTime.AddHours(2), true, "ns.dewi", clock.GetUtcNow().UtcDateTime));
        Sync(a, "tablet-A").GetAwaiter().GetResult();
        Snap(new("Tablet A goes offline and keeps working", "Tablet A luring dan tetap bekerja"), new("Vitals (NEWS2 10) and a completed task are stored locally, marked dirty, and survive a restart.", "Tanda vital (NEWS2 10) dan tugas selesai disimpan lokal, ditandai belum tersinkron, dan aman bila aplikasi ditutup."));

        clock.Advance(TimeSpan.FromMinutes(2));
        b.Save(RoundsTypes.Task, "task-1", new RoundsTask("task-1", "pat-7", "visit-7", "Ambil sampel darah ulang", HisNow.UtcDateTime.AddHours(2), false, null, null));
        b.Save(RoundsTypes.Note, "note-B1", new RoundsNote("note-B1", "pat-7", "visit-7", clock.GetUtcNow().UtcDateTime, "ns.eko", "Sampel ditunda, pasien sedang nebulisasi."));
        b.Save(RoundsTypes.Patient, "visit-7", new RoundsPatient("visit-7", "pat-7", "000007", "Budi S.", 66, "L", "Bangsal Melati", "MEL-03", null, null, HisNow.UtcDateTime, null, null, "Bpjs"));
        Sync(b, "tablet-B").GetAwaiter().GetResult();
        Snap(new("Tablet B edits the same task later", "Tablet B mengubah tugas yang sama belakangan"), new("B's later edit reopens the task (newer HLC) and adds a note; its attempt to rename the patient is refused — the census is read-only.", "Perubahan B yang lebih baru membuka kembali tugas (HLC lebih baru) dan menambah catatan; upaya mengganti nama pasien ditolak — daftar pasien hanya-baca."));

        netA.Online = true;
        Sync(a, "tablet-A").GetAwaiter().GetResult();
        Sync(b, "tablet-B").GetAwaiter().GetResult();
        Snap(new("A reconnects — everyone converges", "A kembali daring — semua konvergen"), new("A's vitals are accepted (append-only); its older task edit loses to B's and is logged as a conflict. Pulls go by server sequence, so B receives A's late vitals.", "Tanda vital A diterima (append-only); perubahan tugas A yang lebih lama kalah dari B dan dicatat sebagai konflik. Pull memakai urutan server sehingga B menerima tanda vital A yang terlambat."));
        storeA.Dispose(); storeB.Dispose();
        return steps;
    }

    private sealed class SwitchableTransport(ISyncTransport inner) : ISyncTransport
    {
        public bool Online { get; set; } = true;
        public Task<SyncPushResult> PushAsync(SyncPushRequest r, CancellationToken ct = default) => Online ? inner.PushAsync(r, ct) : Task.FromException<SyncPushResult>(new HttpRequestException("offline"));
        public Task<SyncPullResult> PullAsync(SyncPullRequest r, CancellationToken ct = default) => Online ? inner.PullAsync(r, ct) : Task.FromException<SyncPullResult>(new HttpRequestException("offline"));
    }
}
