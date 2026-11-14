using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IoTCom.Net.Protocols.Hl7;
using Medical.Clinical;
using Medical.Core;
using Medical.Devices;
using Medical.Fhir;
using Medical.Hospital;
using Medical.Hospital.Monitoring;
using Medical.Sync;
using Medical.Sync.Rounds;
using Microsoft.EntityFrameworkCore;

namespace Medical.Cli;

/// <summary>v2.0 commands: multi-tenant HIS (seed, dashboard, patient), live ICU monitoring and the offline-sync walkthrough.</summary>
internal static class PlatformCommands
{
    private static readonly CultureInfo IdId = CultureInfo.GetCultureInfo("id-ID");

    private static string T(string lang, string en, string id) => lang == "id" ? id : en;

    private static DbContextOptions<HospitalDbContext> Options(Args a) =>
        new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite($"Data Source={a.Opt("db") ?? "his.db"}").Options;

    private static string Rupiah(decimal v) => "Rp " + v.ToString("N0", IdId);

    // ------------------------------------------------------------------ medical his seed|dashboard|patient

    public static async Task<int> His(Args a, string lang) => a.Positional(0, "sub-command") switch
    {
        "seed" => await Seed(a, lang),
        "dashboard" => await Dashboard(a, lang),
        "patient" => await Patient(a, lang),
        _ => throw new ArgumentException("his seed|dashboard|patient"),
    };

    private static async Task<int> Seed(Args a, string lang)
    {
        var options = Options(a);
        int patients = (int)(a.OptNum("patients") ?? 40);
        await HospitalSeeder.SeedAsync(options, TimeProvider.System, patients);
        await using var root = new HospitalDbContext(options, new FixedTenantContext(""));
        Console.WriteLine(T(lang, $"HIS database {a.Opt("db") ?? "his.db"} ready (seeding is idempotent).", $"Basis data SIMRS {a.Opt("db") ?? "his.db"} siap (seeding idempoten)."));
        foreach (var t in await root.Tenants.OrderBy(t => t.Id).ToListAsync())
        {
            await using var db = new HospitalDbContext(options, new FixedTenantContext(t.Id, "cli"));
            int pts = await db.Patients.CountAsync(), beds = await db.Beds.CountAsync(), staff = await db.Staff.CountAsync();
            Console.WriteLine($"  {t.Id,-14} {t.Code,-5} {t.Name,-22} {t.City,-10} {T(lang, "class", "kelas")} {t.Class}  · {pts} {T(lang, "patients", "pasien")} · {beds} {T(lang, "beds", "tempat tidur")} · {staff} {T(lang, "staff", "staf")}");
        }
        Console.WriteLine(T(lang, "Next: medical his dashboard --tenant rs-nusantara", "Berikutnya: medical his dashboard --tenant rs-nusantara"));
        return 0;
    }

    private static async Task<HospitalDbContext> Open(Args a)
    {
        var options = Options(a);
        string tenant = a.Opt("tenant") ?? HospitalSeeder.DemoTenants[0].Id;
        var path = a.Opt("db") ?? "his.db";
        if (!File.Exists(path)) throw new FileNotFoundException($"{path} not found — run 'medical his seed --db {path}' first.");
        var db = new HospitalDbContext(options, new FixedTenantContext(tenant, "cli"));
        if (!await db.Tenants.AnyAsync(t => t.Id == tenant))
        {
            await db.DisposeAsync();
            throw new ArgumentException($"Unknown tenant '{tenant}'. Known: {string.Join(", ", HospitalSeeder.DemoTenants.Select(t => t.Id))}.");
        }
        return db;
    }

    private static async Task<int> Dashboard(Args a, string lang)
    {
        await using var db = await Open(a);
        var d = await new DashboardService(db).SnapshotAsync();
        Console.WriteLine($"{d.Tenant} — {T(lang, "dashboard", "dasbor")} {DateTime.Now.ToString(lang == "id" ? "dd MMM yyyy HH:mm" : "yyyy-MM-dd HH:mm", lang == "id" ? IdId : CultureInfo.InvariantCulture)}");
        Console.WriteLine();
        Console.WriteLine(T(lang, "Census", "Sensus"));
        Console.WriteLine($"  {T(lang, "Outpatients today", "Rawat jalan hari ini"),-26} {d.OutpatientsToday,5}");
        Console.WriteLine($"  {T(lang, "Emergency today", "IGD hari ini"),-26} {d.EmergencyToday,5}");
        Console.WriteLine($"  {T(lang, "Inpatients", "Rawat inap"),-26} {d.Inpatients,5}");
        Console.WriteLine($"  {T(lang, "Admissions / discharges", "Masuk / pulang hari ini"),-26} {d.AdmissionsToday,2} / {d.DischargesToday}");
        Console.WriteLine($"  {"BOR",-26} {d.BedOccupancyRate,5:0.0} %");
        Console.WriteLine($"  {"ALOS",-26} {d.AverageLengthOfStay,5:0.0} {T(lang, "days", "hari")}");
        Console.WriteLine();
        Console.WriteLine(T(lang, "Queues", "Antrean"));
        Console.WriteLine($"  {T(lang, "Clinic", "Poli"),-26} {T(lang, "Waiting", "Menunggu"),9} {T(lang, "With doctor", "Diperiksa"),12}");
        foreach (var q in d.Queues.OrderBy(q => q.Clinic, StringComparer.Ordinal)) Console.WriteLine($"  {q.Clinic,-26} {q.Waiting,9} {q.InConsultation,12}");
        Console.WriteLine();
        Console.WriteLine(T(lang, "Wards", "Ruang rawat"));
        Console.WriteLine($"  {T(lang, "Ward", "Ruang"),-26} {T(lang, "Beds", "TT"),5} {T(lang, "Occupied", "Terisi"),9} {T(lang, "Cleaning", "Dibersihkan"),12} {T(lang, "Free", "Kosong"),7}");
        foreach (var w in d.Wards) Console.WriteLine($"  {w.Ward,-26} {w.Beds,5} {w.Occupied,9} {w.Cleaning,12} {w.Free,7}");
        Console.WriteLine();
        Console.WriteLine(T(lang, "Pending work", "Pekerjaan tertunda"));
        Console.WriteLine($"  {T(lang, "Lab orders", "Order laboratorium"),-34} {d.PendingLab,4}");
        Console.WriteLine($"  {T(lang, "Critical values not acknowledged", "Nilai kritis belum dilaporkan"),-34} {d.CriticalUnacknowledged,4}");
        Console.WriteLine($"  {T(lang, "Radiology orders", "Order radiologi"),-34} {d.PendingRadiology,4}");
        Console.WriteLine($"  {T(lang, "Prescriptions to review", "Resep perlu ditelaah"),-34} {d.PrescriptionsToReview,4}");
        Console.WriteLine();
        Console.WriteLine(T(lang, $"NEWS2 watch list (≥ 5): {d.HighNews2.Count}", $"Daftar pantau NEWS2 (≥ 5): {d.HighNews2.Count}"));
        foreach (var w in d.HighNews2) Console.WriteLine($"  {w.Bed,-8} {w.Name,-26} NEWS2 {w.News2,2}  {w.At.ToLocalTime():HH:mm}");
        Console.WriteLine();
        Console.WriteLine($"{T(lang, "Revenue today", "Pendapatan hari ini")}: {Rupiah(d.RevenueToday)}");
        return 0;
    }

    private static async Task<int> Patient(Args a, string lang)
    {
        string mrn = a.Positional(1, "mrn");
        await using var db = await Open(a);
        var p = await db.Patients.FirstOrDefaultAsync(x => x.Mrn == mrn)
            ?? throw new KeyNotFoundException(T(lang, $"No patient with MRN {mrn} in this tenant.", $"Tidak ada pasien dengan No. RM {mrn} di RS ini."));
        int age = (int)((DateTime.Today - p.BirthDate.ToDateTime(TimeOnly.MinValue)).TotalDays / 365.25);
        Console.WriteLine($"{p.FullName} · {T(lang, "MRN", "No. RM")} {p.Mrn} · {(lang == "id" ? p.Gender switch { Gender.Male => "Laki-laki", Gender.Female => "Perempuan", _ => "Tidak diketahui" } : p.Gender.ToString())} · {p.BirthDate:yyyy-MM-dd} ({age} {T(lang, "y", "th")})");
        if (p.BpjsNumber is not null) Console.WriteLine($"  BPJS {p.BpjsNumber}");
        if (!string.IsNullOrEmpty(p.Allergies)) Console.WriteLine($"  {T(lang, "Allergies", "Alergi")}: {p.Allergies}");

        var depts = await db.Departments.ToDictionaryAsync(x => x.Id, x => x.NameId);
        var beds = await db.Beds.ToDictionaryAsync(x => x.Id, x => x.Code);
        var visits = (await db.Visits.Where(v => v.PatientId == p.Id).ToListAsync()).OrderByDescending(v => v.Start).ToList();
        var visitIds = visits.Select(v => v.Id).ToList();
        var dx = (await db.Diagnoses.Where(x => visitIds.Contains(x.VisitId)).ToListAsync()).ToLookup(x => x.VisitId);
        Console.WriteLine();
        Console.WriteLine($"{T(lang, "Visits", "Kunjungan")} ({visits.Count}, {T(lang, "local time", "waktu lokal")})");
        foreach (var v in visits)
        {
            string where = depts.GetValueOrDefault(v.DepartmentId, "?") + (v.BedId is { } b && beds.TryGetValue(b, out var code) ? $" {code}" : "");
            Console.WriteLine($"  {v.Start.ToLocalTime():yyyy-MM-dd HH:mm}  {v.Kind,-11} {v.Status,-14} {where,-24} {v.Payer}{(v.QueueNumber is { } q ? $" · {T(lang, "queue", "antrean")} {q}" : "")}");
            if (v.ChiefComplaint is { } cc) Console.WriteLine($"      {T(lang, "complaint", "keluhan")}: {cc}");
            foreach (var d in dx[v.Id].OrderByDescending(d => d.Primary)) Console.WriteLine($"      {(d.Primary ? "*" : " ")} {d.Icd10,-7} {d.Display}");
        }
        var latest = (await db.Vitals.Where(x => x.PatientId == p.Id && x.Validated).ToListAsync()).OrderByDescending(x => x.Time).FirstOrDefault();
        Console.WriteLine();
        if (latest is null) Console.WriteLine(T(lang, "No vitals recorded.", "Belum ada tanda vital."));
        else
        {
            Console.WriteLine($"{T(lang, "Latest vitals", "Tanda vital terakhir")} ({latest.Time.ToLocalTime():yyyy-MM-dd HH:mm}, {latest.Source})");
            Console.WriteLine($"  RR {latest.RespiratoryRate:0} · SpO₂ {latest.SpO2:0} %{(latest.OnOxygen ? " O₂" : "")} · {T(lang, "BP", "TD")} {latest.SystolicBp:0}/{latest.DiastolicBp:0} · HR {latest.HeartRate:0} · {latest.TemperatureC:0.0} °C · {latest.Consciousness}");
            Console.WriteLine($"  NEWS2 {latest.News2}");
        }
        if (a.Opt("fhir") is { } outPath)
        {
            var bundle = new FhirMapper().ToPatientSummary(await HisRecords.ToPatientRecordAsync(db, p.Id));
            var json = FhirJson.Serialize(bundle);
            await File.WriteAllTextAsync(outPath, json);
            Console.WriteLine();
            Console.WriteLine(T(lang, $"Wrote FHIR patient summary (IPS) {outPath}: {bundle.Entry.Count} entries, {json.Length:N0} chars",
                $"Ringkasan pasien FHIR (IPS) ditulis ke {outPath}: {bundle.Entry.Count} entri, {json.Length:N0} karakter"));
        }
        return 0;
    }

    // ------------------------------------------------------------------ medical monitor

    public static async Task<int> Monitor(Args a, string lang)
    {
        int minutes = Math.Max(1, (int)(a.OptNum("minutes") ?? 120));
        int step = Math.Max(1, (int)(a.OptNum("step") ?? 5));
        var now = TimeProvider.System.GetUtcNow();
        var t0 = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);

        await using var feed = Hl7MonitorFeed.InProcess("cli-monitors");
        var engine = new MonitoringEngine();
        var pending = new ConcurrentQueue<EarlyWarningAlert>();
        engine.Alert += pending.Enqueue;
        (string Dev, string Bed, PatientScenario S, string Name)[] beds =
        [
            ("ICU-MON-01", "ICU-01", PatientScenario.Sepsis, "Budi Santoso"), ("ICU-MON-02", "ICU-02", PatientScenario.Stable, "Sari Wulandari"),
            ("ICU-MON-03", "ICU-03", PatientScenario.Hypoxia, "Agus Hartono"), ("ICU-MON-04", "ICU-04", PatientScenario.Hypertension, "Dewi Pratiwi"),
        ];
        foreach (var b in beds) engine.Bind(b.Dev, b.Bed, b.Dev, b.Name, null);
        using var cts = new CancellationTokenSource();
        var gateway = new DeviceGateway(engine, feed);
        var run = gateway.RunAsync(cts.Token);
        await using var sim = await BedsideSimulation.StartAsync(feed, beds.Select(b => (b.Dev, b.Bed[^2..], b.S)), seed: 5);

        Console.WriteLine(T(lang,
            $"Simulated ICU: {beds.Length} HL7 v2 monitors → MLLP (in-process) → NEWS2 engine, {minutes} min, 1 ORU per monitor per minute.",
            $"ICU simulasi: {beds.Length} monitor HL7 v2 → MLLP (in-process) → mesin NEWS2, {minutes} menit, 1 ORU per monitor per menit."));
        Console.WriteLine("  " + string.Join("   ", beds.Select(b => $"{b.Bed} {b.Name} ({b.S})")));
        Console.WriteLine();
        bool high = false;
        long expected = 0;
        for (int m = 1; m <= minutes; m++)
        {
            await sim.TickAsync(TimeSpan.FromMinutes(1), t0.AddMinutes(m));
            expected += beds.Length * 6;
            for (int i = 0; i < 200 && gateway.Readings < expected; i++) await Task.Delay(2);
            while (pending.TryDequeue(out var al))
            {
                if (al.Raised && al.Level == EscalationLevel.High) high = true;
                string tag = al.Raised ? T(lang, "ALERT  ", "PERINGATAN") : T(lang, "cleared", "selesai   ");
                Console.WriteLine($"  t+{m,3} {(al.Raised ? "!!" : "--")} {tag} {al.BedCode} {al.PatientName} [{al.Level}] {al.Message[lang]}");
            }
            if (m % step == 0)
            {
                var cells = engine.Snapshots.Select(s => s.News2 is { } n ? $"{s.BedCode} {n.Score,2} {Short(s.Level),-4}" : $"{s.BedCode}  — {T(lang, "wait", "tunggu"),-4}");
                Console.WriteLine($"t+{m,3} min  NEWS2  " + string.Join("  │  ", cells));
            }
        }
        cts.Cancel();
        await run;
        Console.WriteLine();
        Console.WriteLine(T(lang, $"{feed.MessagesReceived} HL7 messages, {gateway.Readings} readings.", $"{feed.MessagesReceived} pesan HL7, {gateway.Readings} pembacaan."));
        if (high) Console.WriteLine(T(lang, "A high (≥ 7) NEWS2 alert was raised — exit code 3.", "Peringatan NEWS2 tinggi (≥ 7) muncul — kode keluar 3."));
        return high ? 3 : 0;
    }

    private static string Short(EscalationLevel l) => l switch
    {
        EscalationLevel.High => "HIGH", EscalationLevel.Medium => "MED", EscalationLevel.LowMedium => "L-M", EscalationLevel.Low => "low", _ => "",
    };

    // ------------------------------------------------------------------ medical sync demo

    public static async Task<int> Sync(Args a, string lang)
    {
        if (a.Positional(0, "sub-command") != "demo") throw new ArgumentException("sync demo");
        var t0 = new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(t0);
        var server = new SyncServer(new InMemorySyncServerStore(), RoundsTypes.Policies, new HybridLogicalClock(clock));
        var net = new InProcessSyncTransport(server);
        var netA = new SwitchableTransport(net);
        var netB = new SwitchableTransport(net);
        using var storeA = new SqliteSyncStore();
        using var storeB = new SqliteSyncStore();
        var tabA = new SyncClient(storeA, netA, "tablet-A", new HybridLogicalClock(clock));
        var tabB = new SyncClient(storeB, netB, "tablet-B", new HybridLogicalClock(clock));
        int n = 0;

        void Put<TV>(string type, string id, TV v)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            server.Store.Put(new SyncChange(type, id, new HybridLogicalClock(clock).Now(), "server", JsonSerializer.SerializeToNode(v, SyncJson.Options)!.AsObject()));
        }
        static string Sum(string type, JsonObject? d) => d is null ? "(deleted)" : type switch
        {
            RoundsTypes.Patient => $"{d["bed"]} {d["name"]} · NEWS2 {d["news2"]}",
            RoundsTypes.Vitals => $"RR {d["respiratoryRate"]} SpO₂ {d["spO2"]} HR {d["heartRate"]} → NEWS2 {d["news2"]} ({d["recordedBy"]})",
            RoundsTypes.Task => $"{d["title"]} · {((bool?)d["done"] == true ? "✓ " + d["doneBy"] : "open")}",
            RoundsTypes.Note => $"{d["author"]}: {d["text"]}",
            _ => d.ToJsonString(),
        };
        void Step(string en, string id, string whyEn, string whyId)
        {
            Console.WriteLine();
            Console.WriteLine($"{++n}. {T(lang, en, id)}");
            Console.WriteLine($"   {T(lang, whyEn, whyId)}");
        }
        void Show(string node, SqliteSyncStore s)
        {
            var rows = new[] { RoundsTypes.Patient, RoundsTypes.Task, RoundsTypes.Vitals, RoundsTypes.Note }.SelectMany(t => s.All(t)).ToList();
            Console.WriteLine($"   {node}: {rows.Count} {T(lang, "records", "rekaman")}");
            foreach (var r in rows) Console.WriteLine($"     {(r.Dirty ? "*" : " ")} {r.Type,-8} {r.Id,-8} {Sum(r.Type, r.Data)}");
        }
        void ShowServer()
        {
            var rows = server.Store.Since(0, 1000, null, null);
            Console.WriteLine($"   server: {rows.Count} {T(lang, "records", "rekaman")}");
            foreach (var c in rows) Console.WriteLine($"       {c.Type,-8} {c.Id,-8} {Sum(c.Type, c.Data)}");
        }
        async Task DoSync(SyncClient c)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            var r = await c.SyncAsync();
            Console.WriteLine($"   sync {c.NodeId}: " + (lang != "id" ? r.ToString()
                : r.Online ? $"dikirim {r.Pushed}, diterima {r.Pulled}, konflik {r.Conflicts.Count}" : $"luring ({r.Error})"));
            foreach (var x in r.Conflicts) Console.WriteLine($"     {T(lang, "conflict", "konflik")}: {x.Change.Type} {x.Change.Id} — {x.Reason}");
        }

        Console.WriteLine(T(lang, "Offline-first ward rounds: one sync server, two SQLite tablets. (* = not yet synced)",
            "Visite bangsal offline-first: satu server sinkronisasi, dua tablet SQLite. (* = belum tersinkron)"));

        var utc = t0.UtcDateTime;
        Put(RoundsTypes.Patient, "visit-7", new RoundsPatient("visit-7", "pat-7", "000007", "Budi Santoso", 66, "L", "Bangsal Melati", "MEL-03", "J18.9 Pneumonia", null, utc.AddDays(-1), 3, utc.AddHours(-4), "Bpjs"));
        Put(RoundsTypes.Task, "task-1", new RoundsTask("task-1", "pat-7", "visit-7", "Ambil sampel darah ulang", utc.AddHours(2), false, null, null));
        Step("The HIS publishes the ward", "SIMRS menerbitkan daftar bangsal",
            "The server holds the census (read-only on devices) and a task (last-writer-wins).", "Server menyimpan daftar pasien (hanya-baca di perangkat) dan satu tugas (penulis terakhir menang).");
        ShowServer();

        Step("Both tablets sync", "Kedua tablet sinkron", "Each tablet now has its own SQLite copy of the ward.", "Setiap tablet kini punya salinan SQLite bangsal sendiri.");
        await DoSync(tabA);
        await DoSync(tabB);
        Show("tablet-A", storeA);

        Step("Tablet A goes offline and keeps working", "Tablet A luring dan tetap bekerja",
            "Vitals (NEWS2 10) and a completed task are stored locally, marked dirty, and survive a restart.",
            "Tanda vital (NEWS2 10) dan tugas selesai disimpan lokal, ditandai belum tersinkron, dan aman bila aplikasi ditutup.");
        netA.Online = false;
        clock.Advance(TimeSpan.FromMinutes(5));
        tabA.Save(RoundsTypes.Vitals, "vit-A1", new RoundsVitals("vit-A1", "pat-7", "visit-7", clock.GetUtcNow().UtcDateTime, 25, 91, false, 98, 60, 118, 38.9, "Alert", 10, "ns.dewi"));
        clock.Advance(TimeSpan.FromMinutes(1));
        tabA.Save(RoundsTypes.Task, "task-1", new RoundsTask("task-1", "pat-7", "visit-7", "Ambil sampel darah ulang", utc.AddHours(2), true, "ns.dewi", clock.GetUtcNow().UtcDateTime));
        await DoSync(tabA);
        Show("tablet-A", storeA);

        Step("Tablet B edits the same task later", "Tablet B mengubah tugas yang sama belakangan",
            "B's later edit reopens the task (newer HLC) and adds a note; its attempt to rename the patient is refused — the census is read-only.",
            "Perubahan B yang lebih baru membuka kembali tugas (HLC lebih baru) dan menambah catatan; upaya mengganti nama pasien ditolak — daftar pasien hanya-baca.");
        clock.Advance(TimeSpan.FromMinutes(2));
        tabB.Save(RoundsTypes.Task, "task-1", new RoundsTask("task-1", "pat-7", "visit-7", "Ambil sampel darah ulang", utc.AddHours(2), false, null, null));
        tabB.Save(RoundsTypes.Note, "note-B1", new RoundsNote("note-B1", "pat-7", "visit-7", clock.GetUtcNow().UtcDateTime, "ns.eko", "Sampel ditunda, pasien sedang nebulisasi."));
        tabB.Save(RoundsTypes.Patient, "visit-7", new RoundsPatient("visit-7", "pat-7", "000007", "Budi S.", 66, "L", "Bangsal Melati", "MEL-03", null, null, utc, null, null, "Bpjs"));
        await DoSync(tabB);
        Show("tablet-B", storeB);

        Step("A reconnects — everyone converges", "A kembali daring — semua konvergen",
            "A's vitals are accepted (append-only); its older task edit loses to B's and is logged as a conflict. Pulls go by server sequence, so B receives A's late vitals.",
            "Tanda vital A diterima (append-only); perubahan tugas A yang lebih lama kalah dari B dan dicatat sebagai konflik. Pull memakai urutan server sehingga B menerima tanda vital A yang terlambat.");
        netA.Online = true;
        await DoSync(tabA);
        await DoSync(tabB);
        ShowServer();
        Show("tablet-A", storeA);
        Show("tablet-B", storeB);
        return 0;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class SwitchableTransport(ISyncTransport inner) : ISyncTransport
    {
        public bool Online { get; set; } = true;
        public Task<SyncPushResult> PushAsync(SyncPushRequest r, CancellationToken ct = default) => Online ? inner.PushAsync(r, ct) : Task.FromException<SyncPushResult>(new HttpRequestException("offline"));
        public Task<SyncPullResult> PullAsync(SyncPullRequest r, CancellationToken ct = default) => Online ? inner.PullAsync(r, ct) : Task.FromException<SyncPullResult>(new HttpRequestException("offline"));
    }
}
