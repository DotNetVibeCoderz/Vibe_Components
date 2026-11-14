using Medical.Core;
using Medical.Hospital;
using Medical.Hospital.Monitoring;
using Medical.Sync;
using Medical.Sync.Rounds;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Medical.Tests;

/// <summary>An in-memory SQLite hospital database shared by several contexts (one per tenant / user).</summary>
public sealed class HisFixture : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public DbContextOptions<HospitalDbContext> Options { get; }
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));   // 09:00 WIB

    public HisFixture(bool seed = false)
    {
        _conn.Open();
        Options = new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite(_conn).Options;
        if (seed) HospitalSeeder.SeedAsync(Options, Clock, 24).GetAwaiter().GetResult();
        else
        {
            using var db = Ctx("");
            db.Database.EnsureCreated();
            db.Tenants.Add(new Tenant { Id = "t1", Code = "T1", Name = "RS Satu", City = "Bandung" });
            db.Tenants.Add(new Tenant { Id = "t2", Code = "T2", Name = "RS Dua", City = "Medan" });
            db.SaveChanges();
        }
    }

    public HospitalDbContext Ctx(string tenant, string user = "tester", StaffRole role = StaffRole.Admin) => new(Options, new FixedTenantContext(tenant, user, role));

    public void Dispose() => _conn.Dispose();
}

public class HospitalCoreTests
{
    private static async Task<(Department Clinic, Department Ward, Bed[] Beds)> SetupAsync(HospitalDbContext db)
    {
        var clinic = new Department { Code = "POLI-UMUM", Name = "General", NameId = "Poli Umum", Kind = DepartmentKind.Outpatient, QueuePrefix = "A" };
        var ward = new Department { Code = "MELATI", Name = "Melati", NameId = "Bangsal Melati", Kind = DepartmentKind.Ward };
        db.Departments.AddRange(clinic, ward);
        var beds = new[] { new Bed { WardId = ward.Id, Code = "MEL-01", Class = BedClass.Kelas3 }, new Bed { WardId = ward.Id, Code = "MEL-02", Class = BedClass.Vip } };
        db.Beds.AddRange(beds);
        db.Tariffs.Add(new Tariff { Code = "REG", Category = ChargeCategory.Registration, Price = 50_000 });
        db.Tariffs.Add(new Tariff { Code = "KONS-UMUM", Category = ChargeCategory.Consultation, Price = 75_000 });
        db.Tariffs.Add(new Tariff { Code = "KAMAR-KELAS3", Category = ChargeCategory.Room, Price = 200_000 });
        db.Tariffs.Add(new Tariff { Code = "PERAWATAN", Category = ChargeCategory.Nursing, Price = 100_000 });
        await db.SaveChangesAsync();
        return (clinic, ward, beds);
    }

    private static NewPatient Siti(string? nik = "3171012501900001", string? card = null) =>
        new("Siti", "Rahayu", Gender.Female, new DateOnly(1990, 1, 25), nik, card, Allergies: "penicillin");

    [Fact]
    public async Task TenantsAreIsolated()
    {
        using var fx = new HisFixture();
        string idA;
        await using (var a = fx.Ctx("t1")) idA = (await new RegistrationService(a, fx.Clock).RegisterAsync(Siti())).Id;
        await using (var b = fx.Ctx("t2"))
        {
            Assert.Empty(await b.Patients.ToListAsync());
            var same = await new RegistrationService(b, fx.Clock).RegisterAsync(Siti());   // same NIK, other hospital → a separate record
            Assert.NotEqual(idA, same.Id);
            Assert.Equal("000001", same.Mrn);                                               // MRN sequences are per tenant
        }
        await using (var a = fx.Ctx("t1"))
        {
            var mine = await a.Patients.SingleAsync();
            await using var b = fx.Ctx("t2");
            var foreign = await b.Patients.SingleAsync();
            a.Attach(foreign);
            foreign.Phone = "0812";
            Assert.Throws<TenantViolationException>(() => a.SaveChanges());
            Assert.Equal(idA, mine.Id);
        }
    }

    [Fact]
    public async Task RegistrationQueuesAndOutpatientBilling()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "pendaftaran");
        var (clinic, _, _) = await SetupAsync(db);
        var reg = new RegistrationService(db, fx.Clock);
        var p = await reg.RegisterAsync(Siti());
        Assert.Same(p, await reg.RegisterAsync(Siti()));                                     // NIK de-duplication
        await Assert.ThrowsAsync<ArgumentException>(() => reg.RegisterAsync(Siti(nik: "123")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reg.StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Bpjs));   // no card
        var p2 = await reg.RegisterAsync(Siti(nik: "3171012501900002") with { Given = "Ani" });
        var v1 = await reg.StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum);
        var v2 = await reg.StartVisitAsync(p2.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum);
        Assert.Equal(["A-001", "A-002"], new[] { v1.QueueNumber!, v2.QueueNumber! });
        await Assert.ThrowsAsync<InvalidOperationException>(() => reg.StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum));   // already queued

        var clin = new ClinicalService(db, fx.Clock);
        var called = await clin.CallNextAsync(clinic.Id, "dr.andi");
        Assert.Equal(v1.Id, called!.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => clin.FinishVisitAsync(v1.Id));   // diagnosis required
        await Assert.ThrowsAsync<ArgumentException>(() => clin.AddDiagnosisAsync(v1.Id, "XYZ"));
        var dx = await clin.AddDiagnosisAsync(v1.Id, "i10");
        Assert.True(dx.Primary);
        Assert.Equal("I10", dx.Icd10);
        await clin.FinishVisitAsync(v1.Id);

        var bill = new BillingService(db, fx.Clock);
        var s = await bill.SummaryAsync(v1.Id);
        Assert.Equal(125_000m, s.Total);
        var inv = await bill.IssueAsync(v1.Id);
        Assert.Equal(125_000m, inv.PatientAmount);
        Assert.Equal(InvoiceState.Issued, inv.State);
        Assert.Equal(InvoiceState.Paid, (await bill.PayAsync(inv.Id, 125_000m)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bill.IssueAsync(v1.Id));   // nothing left to invoice
    }

    [Fact]
    public async Task AdmissionTransferAndDischarge()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "dr.igd");
        var (clinic, ward, beds) = await SetupAsync(db);
        var reg = new RegistrationService(db, fx.Clock);
        var clin = new ClinicalService(db, fx.Clock);
        var p = await reg.RegisterAsync(Siti(card: "0001234567890"));
        var v = await reg.StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Bpjs, "0301R0011126V000123");
        await clin.AddDiagnosisAsync(v.Id, "A91");
        await Assert.ThrowsAsync<InvalidOperationException>(() => clin.AdmitAsync(v.Id, ward.Id, bedId: beds[1].Id));   // JKN → no VIP
        var stay = await clin.AdmitAsync(v.Id, ward.Id, BedClass.Kelas3);
        Assert.Equal(BedStatus.Occupied, (await db.Beds.FindAsync(beds[0].Id))!.Status);
        Assert.Equal("A91", (await db.Diagnoses.SingleAsync(d => d.VisitId == stay.Id)).Icd10);   // diagnoses follow the admission
        await Assert.ThrowsAsync<InvalidOperationException>(() => clin.AdmitAsync(v.Id, ward.Id));

        fx.Clock.Advance(TimeSpan.FromDays(3));
        var doc = await clin.DischargeAsync(stay.Id, DischargeType.Home, "Kontrol 1 minggu");
        Assert.Contains("A91", doc.Text);
        Assert.Contains("Kontrol 1 minggu", doc.Text);
        Assert.Equal(BedStatus.Cleaning, (await db.Beds.FindAsync(beds[0].Id))!.Status);
        var room = await db.Charges.SingleAsync(c => c.VisitId == stay.Id && c.Category == ChargeCategory.Room);
        Assert.Equal(3m, room.Quantity);
        await clin.MarkBedCleanAsync(beds[0].Id);
        Assert.Equal(BedStatus.Free, (await db.Beds.FindAsync(beds[0].Id))!.Status);

        var bill = new BillingService(db, fx.Clock);
        var inv = await bill.IssueAsync(stay.Id);                                 // includes the outpatient registration
        Assert.Equal(50_000m + 3 * 200_000m + 3 * 100_000m, inv.Total);
        Assert.Equal(0m, inv.PatientAmount);                                      // JKN: claimed through INA-CBG
        Assert.Equal(InvoiceState.Paid, inv.State);
        var (claim, issues) = await bill.BpjsClaimAsync(stay.Id, "3171012501900009");
        Assert.Equal(600_000m, claim.Tariff.Kamar);
        Assert.Equal(["A91"], claim.Diagnoses);
        Assert.Equal(Bpjs.InaCbg.CareType.Inpatient, claim.JenisRawat);
        Assert.DoesNotContain(issues, i => i.Severity == Bpjs.InaCbg.ClaimIssueSeverity.Error && i.Field.Contains("diag", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PharmacyReviewAndFefoDispensing()
    {
        using var fx = new HisFixture();
        await using var db = fx.Ctx("t1", "apt.fitri", StaffRole.Pharmacist);
        var (clinic, _, _) = await SetupAsync(db);
        var today = DateOnly.FromDateTime(fx.Clock.GetUtcNow().UtcDateTime);
        db.Stock.AddRange(
            new StockBatch { Generic = "simvastatin", Batch = "OLD", Expiry = today.AddDays(-1), Quantity = 100, UnitPrice = 500 },     // expired
            new StockBatch { Generic = "simvastatin", Batch = "SOON", Expiry = today.AddDays(30), Quantity = 10, UnitPrice = 600 },
            new StockBatch { Generic = "simvastatin", Batch = "LATE", Expiry = today.AddDays(300), Quantity = 100, UnitPrice = 700 },
            new StockBatch { Generic = "clarithromycin", Batch = "C1", Expiry = today.AddDays(100), Quantity = 50, UnitPrice = 6_000 },
            new StockBatch { Generic = "amoxicillin", Batch = "A1", Expiry = today.AddDays(100), Quantity = 50, UnitPrice = 900 },
            new StockBatch { Generic = "metformin", Batch = "M1", Expiry = today.AddDays(100), Quantity = 500, UnitPrice = 400 });
        await db.SaveChangesAsync();
        var p = await new RegistrationService(db, fx.Clock).RegisterAsync(Siti());
        var v = await new RegistrationService(db, fx.Clock).StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum);
        var ph = new PharmacyService(db, fx.Clock);

        var rx = await ph.PrescribeAsync(v.Id, [new("Simvastatin", 20, 1, 15), new("clarithromycin", 500, 2, 7)]);
        var review = await ph.ReviewAsync(rx.Id);
        Assert.True(review.Blocking);
        Assert.Contains(review.Findings, f => f.Kind == "interaction" && f.Severity == ReviewSeverity.Stop);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ph.DispenseAsync(rx.Id));
        await ph.DispenseAsync(rx.Id, "Simvastatin ditunda sesuai DPJP; pasien diedukasi");      // override is audited
        var simva = await db.Stock.Where(s => s.Generic == "simvastatin").ToDictionaryAsync(s => s.Batch);
        Assert.Equal(100, simva["OLD"].Quantity);     // expired untouched
        Assert.Equal(0, simva["SOON"].Quantity);      // first expiry first out
        Assert.Equal(95, simva["LATE"].Quantity);
        var charge = await db.Charges.SingleAsync(c => c.Description.StartsWith("simvastatin"));
        Assert.Equal(15 * ((10 * 600m + 5 * 700m) / 15), charge.Amount, 0);
        Assert.Contains("[Override]", (await db.Prescriptions.FindAsync(rx.Id))!.Review);

        var allergy = await ph.ReviewAsync((await ph.PrescribeAsync(v.Id, [new("amoxicillin", 500, 3, 5)])).Id);
        Assert.Contains(allergy.Findings, f => f.Kind == "allergy" && f.Severity == ReviewSeverity.Stop);

        // Renal: eGFR from the latest creatinine.
        var lab = new LabService(db, fx.Clock);
        var o = await lab.OrderAsync(v.Id, ["2160-0"]);
        await lab.CollectAsync(o.Id);
        await lab.EnterResultAsync(o.Id, "2160-0", 3.5);
        var renal = await ph.ReviewAsync((await ph.PrescribeAsync(v.Id, [new("metformin", 500, 2, 30)])).Id);
        Assert.Contains(renal.Findings, f => f.Kind == "renal" && f.Severity == ReviewSeverity.Stop);
        var alerts = await ph.StockAlertsAsync();
        Assert.Contains(alerts, a => a.Generic == "clarithromycin" && a.Low && a.Quantity == 36);   // 50 − 14 dispensed
        Assert.DoesNotContain(alerts, a => a.Generic == "simvastatin");                             // the soon-expiring batch was used first
    }

    [Fact]
    public async Task LaboratoryCriticalValuesAndHl7()
    {
        using var fx = new HisFixture();
        var bus = new InMemoryMedicalEventBus();
        var critical = new List<CriticalLabResult>();
        bus.Subscribe<CriticalLabResult>(critical.Add);
        await using var db = fx.Ctx("t1", "analis", StaffRole.LabAnalyst);
        var (clinic, _, _) = await SetupAsync(db);
        var p = await new RegistrationService(db, fx.Clock).RegisterAsync(Siti());
        var v = await new RegistrationService(db, fx.Clock).StartVisitAsync(p.Id, clinic.Id, VisitKind.Outpatient, PayerType.Umum);
        var lab = new LabService(db, fx.Clock, bus);
        var o = await lab.OrderAsync(v.Id, ["2823-3", "718-7"], OrderPriority.Stat);
        Assert.Matches(@"^LAB\d{6}0001$", o.PlacerNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.EnterResultAsync(o.Id, "2823-3", 4));   // not collected
        Assert.StartsWith("S", await lab.CollectAsync(o.Id));
        var k = await lab.EnterResultAsync(o.Id, "2823-3", 7.1);
        Assert.True(k.Critical);
        Assert.Equal(ResultFlag.CriticalHigh, k.Flag);
        Assert.Single(critical);
        var hb = await lab.EnterResultAsync(o.Id, "718-7", 11.0);
        Assert.Equal(ResultFlag.Low, hb.Flag);       // female range 12.0–15.5
        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.VerifyAsync(o.Id));   // critical value not yet read back
        await lab.NotifyCriticalAsync(k.Id, "dr. Andi");
        await lab.VerifyAsync(o.Id);

        var oml = await lab.ToHl7Async(o.Id);
        Assert.Contains("OML^O21", oml);
        Assert.Contains($"{o.PlacerNumber}-1", oml);

        // Results arriving from the LIS as ORU^R01, matched by placer number.
        var o2 = await lab.OrderAsync(v.Id, ["2345-7"]);
        var oru = Hl7.Hl7V2.Encode(Hl7.Hl7V2.BuildResults(HisRecords.ToDomain(p),
            [new ClinicalObservation { PatientId = p.Id, Code = Loinc.Glucose, Value = 512, Unit = "mg/dL", Flag = ResultFlag.CriticalHigh }],
            LabService.Lis, LabService.His, orderId: $"{o2.PlacerNumber}-1"));
        Assert.Equal(1, await lab.ImportHl7ResultsAsync(oru));
        Assert.True((await db.LabResults.SingleAsync(r => r.LabOrderId == o2.Id)).Critical);
    }

    [Fact]
    public async Task SeededHospitalDashboardAndRecords()
    {
        using var fx = new HisFixture(seed: true);
        await using var db = fx.Ctx("rs-nusantara");
        var d = await new DashboardService(db, fx.Clock).SnapshotAsync();
        Assert.Equal("RS Medika Nusantara", d.Tenant);
        Assert.True(d.Inpatients > 0);
        Assert.InRange(d.BedOccupancyRate, 1, 100);
        Assert.Contains(d.Queues, q => q.Waiting > 0);
        Assert.True(d.RevenueToday > 0);
        var ward = d.Wards.First(w => w.Occupied > 0);
        var board = await new DashboardService(db, fx.Clock).BedBoardAsync(ward.WardId);
        Assert.Equal(ward.Beds, board.Count);
        var tile = board.First(b => b.Status == BedStatus.Occupied);
        Assert.NotNull(tile.News2);
        var pid = (await db.Beds.FindAsync(tile.BedId))!.PatientId!;
        var record = await HisRecords.ToPatientRecordAsync(db, pid);
        Assert.NotEmpty(record.Vitals);
        Assert.NotEmpty(record.Observations);
        Assert.Contains(record.Encounters, e => e.Class == EncounterClass.Inpatient);
        var ips = new Fhir.FhirMapper().ToPatientSummary(record);                // the HIS record exports as FHIR IPS
        Assert.NotEmpty(ips.Entry);
        // The other tenant has its own, independent data.
        await using var other = fx.Ctx("rs-bahari");
        Assert.DoesNotContain(await other.Patients.Select(p => p.Id).ToListAsync(), id => id == pid);
    }
}

public class MonitoringEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);

    private static void Feed(MonitoringEngine e, string dev, DateTimeOffset t, double rr, double spo2, double sbp, double hr, double temp)
    {
        e.Ingest(new(dev, VitalParameter.RespiratoryRate, rr, t));
        e.Ingest(new(dev, VitalParameter.SpO2, spo2, t));
        e.Ingest(new(dev, VitalParameter.SystolicBp, sbp, t));
        e.Ingest(new(dev, VitalParameter.HeartRate, hr, t));
        e.Ingest(new(dev, VitalParameter.TemperatureC, temp, t));
    }

    [Fact]
    public void News2EscalationWithConfirmationAndHysteresis()
    {
        var e = new MonitoringEngine(new MonitoringOptions { MedianWindow = 1 });
        var alerts = new List<EarlyWarningAlert>();
        e.Alert += alerts.Add;
        e.Bind("MON-1", "ICU-01", "p1", "Budi", "v1");
        Feed(e, "MON-1", T0, 16, 97, 120, 80, 37);
        var s = e.Snapshots.Single();
        Assert.Equal(0, s.News2!.Score);
        Assert.Equal(EscalationLevel.None, s.Level);

        // Deteriorating: RR 26 (3), SpO2 91 (3), SBP 95 (2), HR 125 (2), T 39.2 (2) → 12 → High; raised on the second evaluation.
        Feed(e, "MON-1", T0.AddMinutes(1), 26, 91, 95, 125, 39.2);
        Assert.Equal(EscalationLevel.High, e.Snapshots.Single().Level);
        Assert.Single(alerts);                      // confirmation: the first High evaluation already followed one at High within this batch
        Assert.True(alerts[0].Raised);
        Assert.Contains("emergency", alerts[0].Message.En);

        // Recovery: needs three consecutive lower evaluations before the alert clears.
        Feed(e, "MON-1", T0.AddMinutes(2), 16, 97, 120, 80, 37);
        Assert.Single(alerts, a => !a.Raised);
        Assert.Equal(EscalationLevel.None, alerts[^1].Level);
    }

    [Fact]
    public void ArtefactsStaleDataAndCharting()
    {
        var e = new MonitoringEngine(new MonitoringOptions { MedianWindow = 3, ChartEvery = TimeSpan.FromMinutes(15) });
        var charted = new List<BedSnapshot>();
        e.ChartDue += charted.Add;
        e.Bind("MON-2", "ICU-02", "p2", "Ani", "v2");
        Feed(e, "MON-2", T0, 16, 97, 120, 80, 37);
        Assert.Single(charted);
        // A single motion artefact (HR 180) is suppressed by the median filter; an impossible SpO2 is rejected.
        e.Ingest(new("MON-2", VitalParameter.HeartRate, 80, T0.AddSeconds(10)));
        e.Ingest(new("MON-2", VitalParameter.HeartRate, 180, T0.AddSeconds(20)));
        Assert.Equal(80, e.Snapshots.Single().Latest[VitalParameter.HeartRate].Value);
        e.Ingest(new("MON-2", VitalParameter.SpO2, 0, T0.AddSeconds(30)));
        Assert.Equal(1, e.Snapshots.Single().Rejected);
        // Nurse sets supplemental oxygen; NEWS2 adds 2.
        e.SetManual("MON-2", Consciousness.Alert, onOxygen: true);
        e.Ingest(new("MON-2", VitalParameter.SpO2, 97, T0.AddSeconds(40)));
        Assert.Equal(2, e.Snapshots.Single().News2!.Score);
        // Eleven minutes without RR: NEWS2 cannot be computed, RR is reported missing.
        e.Ingest(new("MON-2", VitalParameter.HeartRate, 82, T0.AddMinutes(11)));
        Assert.Null(e.Snapshots.Single().News2);
        Assert.Contains(VitalParameter.RespiratoryRate, e.Snapshots.Single().Missing);
        Feed(e, "MON-2", T0.AddMinutes(16), 16, 97, 120, 80, 37);
        Assert.Equal(2, charted.Count);             // next chart after 15 minutes
        Assert.False(MonitoringEngine.Plausible(VitalParameter.TemperatureC, 50));
    }
}

public class SyncTests
{
    private static (SyncServer Server, InProcessSyncTransport Net) Server(FakeTimeProvider? t = null)
    {
        var server = new SyncServer(new InMemorySyncServerStore(), RoundsTypes.Policies, new HybridLogicalClock(t));
        return (server, new InProcessSyncTransport(server));
    }

    [Fact]
    public void HybridLogicalClockIsMonotonicUnderSkew()
    {
        var t = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var c = new HybridLogicalClock(t);
        long a = c.Now(), b = c.Now();
        Assert.True(b > a);
        Assert.Equal(HybridLogicalClock.PhysicalMs(a), HybridLogicalClock.PhysicalMs(b));
        long remote = HybridLogicalClock.Pack(HybridLogicalClock.PhysicalMs(a) + 60_000, 5);   // a device clock one minute ahead
        long r = c.Receive(remote);
        Assert.True(r > remote);
        t.Advance(TimeSpan.FromSeconds(1));
        Assert.True(c.Now() > r);                                                             // never goes backwards
    }

    [Fact]
    public async Task OfflineClientsConvergeWithPerTypePolicies()
    {
        var (server, net) = Server();
        server.Store.Put(new SyncChange(RoundsTypes.Patient, "visit-1", 1, "server", System.Text.Json.Nodes.JsonNode.Parse("""{"name":"Budi"}""")!.AsObject()));
        server.Store.Put(new SyncChange(RoundsTypes.Task, "task-1", 1, "server", System.Text.Json.Nodes.JsonNode.Parse("""{"done":false}""")!.AsObject()));
        using var storeA = new SqliteSyncStore();
        using var storeB = new SqliteSyncStore();
        var a = new SyncClient(storeA, net, "tablet-A");
        var b = new SyncClient(storeB, net, "tablet-B");
        Assert.Equal(2, (await a.SyncAsync()).Pulled);
        Assert.Equal(2, (await b.SyncAsync()).Pulled);

        net.Online = false;
        a.Save(RoundsTypes.Vitals, "vit-A1", new { rr = 18 });
        a.Save(RoundsTypes.Task, "task-1", new { done = true, by = "A" });
        var offline = await a.SyncAsync();
        Assert.False(offline.Online);
        Assert.Equal(2, storeA.DirtyCount);                                 // kept for later
        b.Save(RoundsTypes.Task, "task-1", new { done = false, by = "B" });   // a later edit on the other tablet
        b.Save(RoundsTypes.Patient, "visit-1", new { name = "hacked" });     // the ward list is server-owned
        net.Online = true;

        var rb = await b.SyncAsync();
        Assert.Equal(1, rb.Pushed);
        Assert.Single(rb.Conflicts, c => c.Change.Type == RoundsTypes.Patient);
        Assert.Equal("Budi", storeB.Get(RoundsTypes.Patient, "visit-1")!.Data!["name"]!.GetValue<string>());   // server version restored
        var ra = await a.SyncAsync();
        Assert.Equal(1, ra.Pushed);                                         // vitals accepted
        Assert.Single(ra.Conflicts, c => c.Change.Type == RoundsTypes.Task); // A's task edit is older than B's (HLC) → loses
        Assert.Equal("B", storeA.Get(RoundsTypes.Task, "task-1")!.Data!["by"]!.GetValue<string>());
        await b.SyncAsync();
        Assert.NotNull(storeB.Get(RoundsTypes.Vitals, "vit-A1"));           // B receives A's late vitals (cursor by server sequence)
        Assert.Equal(storeA.Get(RoundsTypes.Task, "task-1")!.Data!.ToJsonString(), storeB.Get(RoundsTypes.Task, "task-1")!.Data!.ToJsonString());
        Assert.Contains(storeB.Conflicts(), c => c.Type == RoundsTypes.Patient);

        // Append-only: an identical retry is idempotent, a different payload is rejected.
        var dup = server.Push(new SyncPushRequest("tablet-A", [new SyncChange(RoundsTypes.Vitals, "vit-A1", 99, "tablet-A", System.Text.Json.Nodes.JsonNode.Parse("""{"rr":18}""")!.AsObject())]));
        Assert.Single(dup.Accepted);
        var changed = server.Push(new SyncPushRequest("tablet-A", [new SyncChange(RoundsTypes.Vitals, "vit-A1", 100, "tablet-A", System.Text.Json.Nodes.JsonNode.Parse("""{"rr":30}""")!.AsObject())]));
        Assert.Single(changed.Rejected);
    }

    [Fact]
    public async Task PullPagesThroughLargeBacklogs()
    {
        var (server, net) = Server();
        for (int i = 0; i < 1234; i++) server.Store.Put(new SyncChange(RoundsTypes.Note, $"n{i}", i, "server", new System.Text.Json.Nodes.JsonObject { ["i"] = i }));
        using var store = new SqliteSyncStore();
        var c = new SyncClient(store, net, "tablet");
        Assert.Equal(1234, (await c.SyncAsync()).Pulled);
        Assert.Equal(0, (await c.SyncAsync()).Pulled);
        Assert.Equal(1234, store.All(RoundsTypes.Note).Count);
    }

    [Fact]
    public async Task WardRoundsAgainstTheHospitalDatabase()
    {
        using var fx = new HisFixture(seed: true);
        await using var db = fx.Ctx("rs-nusantara", "ns.dewi", StaffRole.Nurse);
        var ward = await db.Departments.FirstAsync(d => d.Code == "MELATI");
        var server = RoundsSync.Server(db);
        using var store = new SqliteSyncStore();
        var tablet = new SyncClient(store, new ScopedTransport(server, ward.Id), "tablet-melati");
        var first = await tablet.SyncAsync();
        var patients = tablet.All<RoundsPatient>(RoundsTypes.Patient);
        Assert.NotEmpty(patients);
        Assert.All(patients, p => Assert.Equal("Bangsal Melati", p.Ward));     // only this ward's census
        var tasks = tablet.All<RoundsTask>(RoundsTypes.Task);
        Assert.NotEmpty(tasks);

        var pt = patients[0];
        fx.Clock.Advance(TimeSpan.FromMinutes(5));                             // later than the seeded observations
        var vit = new RoundsVitals(Guid.NewGuid().ToString("n"), pt.PatientId, pt.Id, fx.Clock.GetUtcNow().UtcDateTime, 25, 92, false, 98, 60, 118, 38.9, "Alert", 0, "ns.dewi");
        tablet.Save(RoundsTypes.Vitals, vit.Id, vit);
        tablet.Save(RoundsTypes.Note, "note-1", new RoundsNote("note-1", pt.PatientId, pt.Id, vit.Time, "ns.dewi", "Pasien tampak sesak."));
        var task = tasks[0] with { Done = true, DoneBy = "ns.dewi", DoneAt = vit.Time };
        tablet.Save(RoundsTypes.Task, task.Id, task);
        var report = await tablet.SyncAsync();
        Assert.Equal(3, report.Pushed);
        Assert.Empty(report.Conflicts);

        var charted = await db.Vitals.SingleAsync(v => v.Id == vit.Id);
        Assert.Equal(VitalsSource.MobileApp, charted.Source);
        Assert.Equal(10, charted.News2);                                      // recomputed on the server (device sent 0)
        Assert.Equal(WardTaskState.Done, (await db.Tasks.FindAsync(task.Id))!.State);
        Assert.Equal("Pasien tampak sesak.", (await db.Notes.FindAsync("note-1"))!.Text);
        Assert.Equal(10, tablet.Get<RoundsPatient>(RoundsTypes.Patient, pt.Id)!.News2);   // the census row now carries the new NEWS2

        // Discharge removes the patient from the tablet's list on the next sync.
        var clin = new ClinicalService(db, fx.Clock);
        await clin.DischargeAsync(pt.Id, DischargeType.Home);
        await tablet.SyncAsync();
        Assert.DoesNotContain(tablet.All<RoundsPatient>(RoundsTypes.Patient), p => p.Id == pt.Id);
    }

    /// <summary>What the HIS endpoint does: pulls are scoped to the device's ward.</summary>
    private sealed class ScopedTransport(SyncServer server, string ward) : ISyncTransport
    {
        public Task<SyncPushResult> PushAsync(SyncPushRequest r, CancellationToken ct = default) => Task.FromResult(server.Push(r));
        public Task<SyncPullResult> PullAsync(SyncPullRequest r, CancellationToken ct = default) => Task.FromResult(server.Pull(r, ward));
    }
}
