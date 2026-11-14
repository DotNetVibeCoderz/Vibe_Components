using Medical.Core;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

/// <summary>
/// Deterministic demo data for two fictional hospitals: departments, beds (ICU beds with monitors), staff for every
/// role, tariffs, a pharmacy stock with low and expiring batches, and synthetic patients in clinics, emergency and wards
/// with vitals, labs, radiology, prescriptions and invoices. Used by the reference app, the gallery, the CLI and tests.
/// </summary>
public static class HospitalSeeder
{
    public static readonly (string Id, string Code, string Name, string City, FacilityClass Class)[] DemoTenants =
    [
        ("rs-nusantara", "RSMN", "RS Medika Nusantara", "Jakarta", FacilityClass.B),
        ("rs-bahari", "RSKB", "RSUD Kota Bahari", "Makassar", FacilityClass.C),
    ];

    /// <summary>Creates the schema and seeds both demo tenants (idempotent).</summary>
    public static async Task SeedAsync(DbContextOptions<HospitalDbContext> options, TimeProvider? clock = null, int patientsPerTenant = 40, CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;
        await HospitalDatabase.MigrateAsync(options, ct);   // creates or upgrades the schema (and every tenant database)
        await using (var boot = new HospitalDbContext(options, new FixedTenantContext("")))
        {
            if (await boot.Tenants.AnyAsync(ct)) return;
            foreach (var t in DemoTenants)
                boot.Tenants.Add(new Tenant { Id = t.Id, Code = t.Code, Name = t.Name, City = t.City, Class = t.Class, KodeTarif = t.Class == FacilityClass.B ? "BP" : "CP" });
            await boot.SaveChangesAsync(ct);
        }
        int index = 0;
        foreach (var t in DemoTenants)
        {
            await using var db = new HospitalDbContext(options, new FixedTenantContext(t.Id, "seed"));
            await SeedTenantAsync(db, t, clock, patientsPerTenant, index++ * 1000, ct);
        }
    }

    private static async Task SeedTenantAsync(HospitalDbContext db, (string Id, string Code, string Name, string City, FacilityClass Class) t, TimeProvider clock,
        int patients, int offset, CancellationToken ct)
    {
        string code = t.Code;
        // With database-per-tenant the tenant's own database needs its tenant row as well (shared schema: already there).
        if (!await db.Tenants.AnyAsync(x => x.Id == t.Id, ct))
        {
            db.Tenants.Add(new Tenant { Id = t.Id, Code = t.Code, Name = t.Name, City = t.City, Class = t.Class, KodeTarif = t.Class == FacilityClass.B ? "BP" : "CP" });
            await db.SaveChangesAsync(ct);
        }
        var r = new SeededRandom(0xC0FFEE + (ulong)offset);
        Department D(string c, string en, string id, DepartmentKind k, string? q = null) { var d = new Department { Code = c, Name = en, NameId = id, Kind = k, QueuePrefix = q }; db.Departments.Add(d); return d; }
        var umum = D("POLI-UMUM", "General clinic", "Poli Umum", DepartmentKind.Outpatient, "A");
        var dalam = D("POLI-DALAM", "Internal medicine clinic", "Poli Penyakit Dalam", DepartmentKind.Outpatient, "B");
        var anak = D("POLI-ANAK", "Paediatric clinic", "Poli Anak", DepartmentKind.Outpatient, "C");
        var igd = D("IGD", "Emergency department", "IGD", DepartmentKind.Emergency, "E");
        var melati = D("MELATI", "Melati ward", "Bangsal Melati", DepartmentKind.Ward);
        var mawar = D("MAWAR", "Mawar ward", "Bangsal Mawar", DepartmentKind.Ward);
        var icu = D("ICU", "Intensive care unit", "ICU", DepartmentKind.Icu);
        D("LAB", "Laboratory", "Laboratorium", DepartmentKind.Laboratory);
        D("RAD", "Radiology", "Radiologi", DepartmentKind.Radiology);
        D("FARMASI", "Pharmacy", "Farmasi", DepartmentKind.Pharmacy);
        D("KASIR", "Cashier", "Kasir", DepartmentKind.Cashier);

        var beds = new List<Bed>();
        for (int i = 1; i <= 12; i++) beds.Add(new Bed { WardId = melati.Id, Code = $"MEL-{i:D2}", Class = i <= 6 ? BedClass.Kelas3 : BedClass.Kelas2 });
        for (int i = 1; i <= 8; i++) beds.Add(new Bed { WardId = mawar.Id, Code = $"MAW-{i:D2}", Class = i <= 6 ? BedClass.Kelas1 : BedClass.Vip });
        for (int i = 1; i <= 4; i++) beds.Add(new Bed { WardId = icu.Id, Code = $"ICU-{i:D2}", Class = BedClass.Icu, MonitorId = $"{code}-MON-{i:D2}" });
        beds[5].Status = BedStatus.Blocked;
        db.Beds.AddRange(beds);

        (string User, string Name, StaffRole Role, Department? Dept, string? Spec)[] staff =
        [
            ("admin", "Admin Sistem", StaffRole.Admin, null, null),
            ("pendaftaran", "Rina Pendaftaran", StaffRole.Registration, null, null),
            ("dr.andi", "dr. Andi Pratama", StaffRole.Doctor, umum, "Dokter umum"),
            ("dr.sari", "dr. Sari Wulandari, Sp.PD", StaffRole.Doctor, dalam, "Penyakit dalam"),
            ("dr.budi", "dr. Budi Santoso, Sp.A", StaffRole.Doctor, anak, "Anak"),
            ("dr.igd", "dr. Maya Kusuma", StaffRole.Doctor, igd, "Gawat darurat"),
            ("ns.dewi", "Ns. Dewi Lestari", StaffRole.Nurse, melati, null),
            ("ns.eko", "Ns. Eko Saputra", StaffRole.Nurse, icu, null),
            ("apt.fitri", "apt. Fitri Handayani", StaffRole.Pharmacist, null, null),
            ("analis", "Gita Analis", StaffRole.LabAnalyst, null, null),
            ("radiografer", "Hadi Radiografer", StaffRole.Radiographer, null, null),
            ("dr.rad", "dr. Indra, Sp.Rad", StaffRole.Radiologist, null, "Radiologi"),
            ("kasir", "Joko Kasir", StaffRole.Cashier, null, null),
            ("koder", "Kiki Koder", StaffRole.Coder, null, null),
        ];
        var staffRows = staff.Select(s => new Staff { UserName = s.User, Name = s.Name, Role = s.Role, DepartmentId = s.Dept?.Id, Specialty = s.Spec, Nik = $"31710{offset / 1000}{r.Next(10_000_000, 99_999_999)}{r.Next(100, 999)}"[..16] }).ToList();
        db.Staff.AddRange(staffRows);

        decimal m = code == "RSMN" ? 1.2m : 1m;   // class B charges more
        (string C, string N, ChargeCategory Cat, decimal P)[] tariffs =
        [
            ("REG", "Pendaftaran", ChargeCategory.Registration, 50_000), ("REG-IGD", "Pendaftaran IGD", ChargeCategory.Registration, 75_000),
            ("KONS-UMUM", "Konsultasi dokter umum", ChargeCategory.Consultation, 75_000), ("KONS-SP", "Konsultasi spesialis", ChargeCategory.Consultation, 200_000),
            ("KONS-IGD", "Pemeriksaan IGD", ChargeCategory.Consultation, 150_000), ("PERAWATAN", "Asuhan keperawatan per hari", ChargeCategory.Nursing, 100_000),
            ("KAMAR-KELAS3", "Kamar kelas 3", ChargeCategory.Room, 200_000), ("KAMAR-KELAS2", "Kamar kelas 2", ChargeCategory.Room, 350_000),
            ("KAMAR-KELAS1", "Kamar kelas 1", ChargeCategory.Room, 500_000), ("KAMAR-VIP", "Kamar VIP", ChargeCategory.Room, 1_200_000),
            ("KAMAR-ICU", "ICU per hari", ChargeCategory.Room, 2_500_000),
            ("LAB-718-7", "Hemoglobin", ChargeCategory.Laboratory, 40_000), ("LAB-777-3", "Trombosit", ChargeCategory.Laboratory, 40_000),
            ("LAB-6690-2", "Leukosit", ChargeCategory.Laboratory, 40_000), ("LAB-2160-0", "Kreatinin", ChargeCategory.Laboratory, 60_000),
            ("LAB-2823-3", "Kalium", ChargeCategory.Laboratory, 70_000), ("LAB-2345-7", "Glukosa", ChargeCategory.Laboratory, 35_000),
            ("LAB-2524-7", "Laktat", ChargeCategory.Laboratory, 150_000),
            ("RAD-87.44", "Foto toraks", ChargeCategory.Radiology, 180_000), ("RAD-87.03", "CT kepala", ChargeCategory.Radiology, 900_000),
        ];
        db.Tariffs.AddRange(tariffs.Select(x => new Tariff { Code = x.C, Name = x.N, Category = x.Cat, Price = x.P * m }));

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        (string G, int Qty, decimal Price, int ExpDays)[] stock =
        [
            ("paracetamol", 2000, 300, 400), ("amoxicillin", 1200, 900, 300), ("ceftriaxone", 300, 18_000, 200), ("metformin", 1500, 400, 500),
            ("amlodipine", 1000, 600, 450), ("omeprazole", 800, 1_200, 350), ("simvastatin", 600, 700, 380), ("clarithromycin", 150, 6_500, 60),
            ("warfarin", 200, 1_500, 250), ("furosemide", 40, 500, 420), ("ondansetron", 300, 4_000, 30), ("salbutamol", 400, 800, 270),
        ];
        foreach (var s in stock)
        {
            db.Stock.Add(new StockBatch { Generic = s.G, Batch = $"B{r.Next(10000, 99999)}", Expiry = today.AddDays(s.ExpDays), Quantity = s.Qty, UnitPrice = s.Price, ReorderLevel = 100 });
            db.Stock.Add(new StockBatch { Generic = s.G, Batch = $"X{r.Next(10000, 99999)}", Expiry = today.AddDays(-10), Quantity = 20, UnitPrice = s.Price, ReorderLevel = 100 });   // expired, must not be dispensed
        }
        await db.SaveChangesAsync(ct);

        var now = clock.GetUtcNow();
        var reg = new RegistrationService(db, clock);
        var clin = new ClinicalService(db, clock);
        var lab = new LabService(db, clock);
        var pharm = new PharmacyService(db, clock);
        string[] complaints = ["Demam 3 hari", "Batuk berdahak", "Kontrol hipertensi", "Kontrol diabetes", "Nyeri perut", "Sesak napas", "Pusing", "Diare"];
        string[] diagnoses = ["A91", "J18.9", "I10", "E11.9", "K29.7", "J45.9", "R51", "A09.9"];
        var departments = new[] { umum, dalam, anak };
        var wardBeds = beds.Where(b => b.Status == BedStatus.Free).ToList();
        int admittedCount = 0;
        for (int i = 0; i < patients; i++)
        {
            var sp = SyntheticData.Patient(offset + i + 1);
            bool bpjs = r.Chance(0.7);
            var p = await reg.RegisterAsync(new NewPatient(sp.Name.Given, sp.Name.Family, sp.Gender, sp.BirthDate, sp.Nik, bpjs ? $"000{r.Next(1_000_000, 9_999_999)}{r.Next(100, 999)}" : null,
                sp.Phone, sp.Address, sp.City, i % 9 == 0 ? "penicillin" : null), ct);
            int k = i % complaints.Length;
            var dept = i < patients / 4 ? igd : departments[i % departments.Length];
            var visit = await reg.StartVisitAsync(p.Id, dept.Id, dept == igd ? VisitKind.Emergency : VisitKind.Outpatient, bpjs ? PayerType.Bpjs : PayerType.Umum,
                bpjs ? $"0301R{r.Next(100, 999)}{now:MMyy}V{r.Next(100000, 999999)}" : null, complaint: complaints[k], ct: ct);
            if (i % 3 == 0) continue;   // still waiting in the queue
            visit.Status = VisitStatus.InConsultation;
            visit.DoctorId = staffRows.First(s => s.Role == StaffRole.Doctor && s.DepartmentId == dept.Id).Id;
            await clin.SaveSoapAsync(visit.Id, complaints[k], "Kesadaran baik", diagnoses[k], "Terapi dan edukasi", ct);
            await clin.AddDiagnosisAsync(visit.Id, diagnoses[k], ct: ct);
            double sick = dept == igd ? r.Uniform(0.3, 1.0) : r.Uniform(0, 0.3);
            await clin.RecordVitalsAsync(p.Id, visit.Id, Vitals(r, sick, now.AddMinutes(-30)), ct: ct);
            if (dept == igd && admittedCount < wardBeds.Count - 2)
            {
                // The first three emergency admissions are the sickest and go to the ICU (monitored beds); the rest to the wards.
                if (admittedCount < 3) sick = Math.Max(sick, 0.75);
                var target = admittedCount < 3 ? icu : (admittedCount - 3) % 2 == 0 ? melati : mawar;
                Visit stay;
                try { stay = await clin.AdmitAsync(visit.Id, target.Id, ct: ct); } catch (InvalidOperationException) { continue; }
                admittedCount++;
                stay.Start = now.UtcDateTime.AddDays(-r.Next(0, 5)).AddHours(-r.Next(1, 20));
                for (int h = 24; h >= 0; h -= 4)
                    await clin.RecordVitalsAsync(p.Id, stay.Id, Vitals(r, Math.Clamp(sick + (24 - h) / 60.0 * (sick > 0.6 ? 1 : -1), 0, 1), now.AddHours(-h)), ct: ct);
                var order = await lab.OrderAsync(stay.Id, ["718-7", "777-3", "6690-2", "2160-0"], OrderPriority.Urgent, ct);
                await lab.CollectAsync(order.Id, ct);
                await lab.EnterResultAsync(order.Id, "718-7", Math.Round(r.Uniform(9, 15), 1), ct: ct);
                await lab.EnterResultAsync(order.Id, "777-3", Math.Round(diagnoses[k] == "A91" ? r.Uniform(15, 80) : r.Uniform(150, 350)), ct: ct);
                await lab.EnterResultAsync(order.Id, "6690-2", Math.Round(r.Uniform(4, 18), 1), ct: ct);
                await lab.EnterResultAsync(order.Id, "2160-0", Math.Round(r.Uniform(0.7, 2.6), 2), ct: ct);
                if (admittedCount == 2)
                {
                    var k2 = await lab.OrderAsync(stay.Id, ["2823-3"], OrderPriority.Stat, ct);   // a critical potassium waiting for read-back
                    await lab.CollectAsync(k2.Id, ct);
                    await lab.EnterResultAsync(k2.Id, "2823-3", 6.9, ct: ct);
                }
                if (diagnoses[k] is "J18.9" or "A91" or "J45.9" or "K29.7")
                {
                    var rad = new RadiologyService(db, clock);
                    var ro = await rad.OrderAsync(stay.Id, "87.44", "Foto toraks PA", "CR", OrderPriority.Urgent, complaints[k], ct);
                    if (admittedCount % 2 == 0) await rad.PerformedAsync(ro.Id, $"1.2.826.0.1.3680043.10.1427.{offset + i}", ct);
                }
                var rx = await pharm.PrescribeAsync(stay.Id, [new("paracetamol", 500, 3, 3), new(diagnoses[k] == "J18.9" ? "ceftriaxone" : "omeprazole", diagnoses[k] == "J18.9" ? 1000 : 20, diagnoses[k] == "J18.9" ? 2 : 1, 3)], ct);
                if (admittedCount % 2 == 0) { await pharm.ReviewAsync(rx.Id, ct); if (rx.State == PrescriptionState.Verified) await pharm.DispenseAsync(rx.Id, ct: ct); }
                await RoundsSync.AddTaskAsync(db, stay, "Observasi tanda vital tiap 4 jam", now.UtcDateTime.AddHours(2), ct);
                if (admittedCount % 3 == 0) await RoundsSync.AddTaskAsync(db, stay, "Ambil sampel darah ulang", now.UtcDateTime.AddHours(6), ct);
                await RoundsSync.PublishPatientAsync(db, stay, ct);
            }
            else if (i % 4 == 1)
            {
                await clin.FinishVisitAsync(visit.Id, ct);
                var inv = await new BillingService(db, clock).IssueAsync(visit.Id, ct);
                if (inv.PatientAmount > 0 && i % 8 == 1) await new BillingService(db, clock).PayAsync(inv.Id, inv.PatientAmount, ct: ct);
            }
        }
        // One patient per clinic is with the doctor; the others still wait.
        foreach (var g in (await db.Visits.Where(v => v.Status == VisitStatus.InConsultation).ToListAsync(ct)).GroupBy(v => v.DepartmentId))
            foreach (var v in g.OrderBy(v => v.QueueNumber).Skip(1)) v.Status = VisitStatus.Waiting;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Vital signs for a patient with illness severity 0 (well) … 1 (critically ill).</summary>
    public static VitalSigns Vitals(SeededRandom r, double sick, DateTimeOffset at) => new()
    {
        Timestamp = at,
        RespiratoryRate = Math.Round(14 + 14 * sick + r.Gaussian(0, 1.5)),
        SpO2 = Math.Round(Math.Min(100, 98 - 9 * sick + r.Gaussian(0, 1))),
        OnSupplementalOxygen = sick > 0.7,
        SystolicBp = Math.Round(125 - 35 * sick + r.Gaussian(0, 5)),
        DiastolicBp = Math.Round(78 - 20 * sick + r.Gaussian(0, 4)),
        HeartRate = Math.Round(78 + 45 * sick + r.Gaussian(0, 4)),
        TemperatureC = Math.Round(36.8 + 1.8 * sick + r.Gaussian(0, 0.2), 1),
        Consciousness = sick > 0.9 ? Consciousness.Confusion : Consciousness.Alert,
    };
}
