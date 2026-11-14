# Platform rumah sakit (v2.0)

Medical.Net 2.0 menambahkan inti sistem informasi manajemen rumah sakit (SIMRS) multi-tenant, NEWS2 waktu nyata dari
perangkat di samping tempat tidur, dan sinkronisasi offline-first untuk visite bangsal. Di atasnya dibangun dua aplikasi
referensi: SIMRS berbasis Blazor Server dan aplikasi visite MAUI. · [English](../en/platform.md)

> SIMRS ini adalah implementasi referensi untuk membangun sistem Anda sendiri, bukan produk bersertifikat. Peringatan
> pemantauan membantu — bukan menggantikan — observasi klinis. Semua rumah sakit, staf dan pasien demo bersifat fiktif.

## Arsitektur

| Bagian | Jenis | Isi |
|---|---|---|
| `Medical.Hospital` | Paket NuGet | Model EF Core (SQLite / SQL Server) dengan multi-tenancy skema bersama; pendaftaran dan antrean, layanan klinis (SOAP, ICD-10, tanda vital dengan NEWS2, rawat inap / pindah / pulang), farmasi, laboratorium, radiologi, kasir (Umum / BPJS / asuransi, data klaim INA-CBG), dasbor, `MonitoringEngine`, sisi server sinkronisasi visite, seeder demo |
| `Medical.Sync` | Paket NuGet | Hybrid logical clock, kebijakan konflik per tipe, `SyncServer`, `SqliteSyncStore` + `SyncClient`, transport dalam proses dan HTTP, kontrak visite (`Medical.Sync.Rounds`) |
| `Medical.Devices` | Paket NuGet, **pratinjau** (`2.0.0-preview`) | Feed monitor HL7 v2 / MLLP, feed MQTT + SenML untuk sensor dan pompa infus, simulator monitor dan pompa, `DeviceGateway` — dibangun di atas IoTCom.Net |
| `apps/Medical.His` | Aplikasi referensi | SIMRS Blazor Server untuk dua rumah sakit demo: dasbor, pendaftaran, rawat jalan, bangsal, monitor langsung, farmasi, lab, radiologi, kasir; API sinkronisasi, FHIR dan dasbor |
| `apps/Medical.Rounds.Core` + `apps/Medical.Rounds` | Aplikasi referensi | Aplikasi visite MAUI (Windows, Android): SQLite luring, daftar pasien bangsal menurut NEWS2, tanda vital, catatan, tugas |

```
monitor pasien ──HL7 ORU^R01 / MLLP──┐
sensor MQTT, pompa ──SenML───────────┤ Medical.Devices ──► MonitoringEngine ──► peringatan, ubin tempat tidur
                                     │                        └─ tiap 15 menit ──► ClinicalService.RecordVitalsAsync (menunggu validasi)
SIMRS Blazor ──► layanan Medical.Hospital ──► HospitalDbContext (TenantId di setiap baris) ──► SQLite / SQL Server
                                                   └─ SyncLog (outbox) ◄──► /api/{tenant}/sync ◄──► tablet visite (SQLite)
```

`Medical.Devices` berstatus pratinjau karena IoTCom.Net 0.22.0-preview.1 (Apache-2.0) hanya tersedia di NuGet sebagai
prarilis. Paket ini mengunci `System.Formats.Cbor` 10.0.15 (perbaikan CVE-2026-118747; IoTCom.Net membawa 10.0.12).
`Medical.Hospital` dan `Medical.Sync` tidak bergantung padanya.

## Multi-tenancy

Satu basis data menampung semua rumah sakit. Setiap baris membawa `TenantId`. Filter kueri global menyembunyikan baris
tenant lain, sedangkan `SaveChanges` memberi tenant aktif pada baris baru dan menolak penambahan, perubahan atau
penghapusan baris milik tenant lain.

```csharp
var options = new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite("Data Source=his.db").Options;
await HospitalSeeder.SeedAsync(options);              // skema + "RS Medika Nusantara" dan "RSUD Kota Bahari" (idempoten)

await using var jakarta = new HospitalDbContext(options, new FixedTenantContext("rs-nusantara", "admin"));
await using var makassar = new HospitalDbContext(options, new FixedTenantContext("rs-bahari", "admin"));
var p = await jakarta.Patients.FirstAsync();
await makassar.Patients.AnyAsync(x => x.Id == p.Id);   // false — filter kueri
makassar.Patients.Update(p);
await makassar.SaveChangesAsync();                      // TenantViolationException
long next = await jakarta.NextAsync("mrn");             // penghitung per tenant, diulang bila terjadi pembaruan bersamaan
```

- **Konteks tenant:** implementasikan `ITenantContext` (`TenantId`, `UserName`, `Role`) dari pengguna yang masuk. `HisSession` di aplikasi SIMRS mengambilnya dari klaim cookie; tugas latar memakai `FixedTenantContext`.
- **Penghitung:** `NextAsync(key)` menerbitkan nomor rekam medis (`000001`), nomor antrean harian, nomor order lab / radiologi dan nomor tagihan per tenant. Nilai penghitung menjadi token konkurensi.
- **Yang tidak difilter:** tabel `Tenants` itu sendiri. `IgnoreQueryFilters()` melewati filter baca, jadi jangan dipakai di kode permintaan.
- **Audit:** `db.AuditEvent(...)` menulis baris `HisAudit` (difilter per tenant) untuk login, pasien baru, rawat inap, pulang, override penyerahan obat, konfirmasi nilai kritis dan ekspor FHIR.
- **Event:** layanan menerbitkan `PatientRegistered`, `VisitStarted`, `PatientAdmitted`, `PatientDischarged`, `CriticalLabResult` dan `VitalsCharted` ke `IMedicalEventBus` (opsional).

## Pendaftaran dan antrean

```csharp
var reg = new RegistrationService(db);
HisPatient p = await reg.RegisterAsync(new NewPatient("Siti", "Rahayu", Gender.Female, new DateOnly(1990, 1, 25),
    Nik: "3171015501900001", BpjsNumber: "0001234567890"));    // NIK yang sudah dikenal mengembalikan pasien yang ada
var poli = await db.Departments.FirstAsync(d => d.Code == "POLI-DALAM");
Visit v = await reg.StartVisitAsync(p.Id, poli.Id, VisitKind.Outpatient, PayerType.Bpjs, sepNumber: "0301R0011126V000123");
// v.QueueNumber "B-001": prefiks poli + urutan harian; biaya pendaftaran ditambahkan
var queue = await reg.QueueAsync(poli.Id);                      // yang sedang diperiksa, lalu gawat darurat, lalu urutan datang
var found = await reg.SearchAsync("rahayu");                    // nomor RM, NIK, nomor BPJS atau nama
```

NIK harus 16 digit dan nomor kartu BPJS 13 digit. Kunjungan BPJS memerlukan nomor kartu, dan seorang pasien hanya boleh
punya satu kunjungan terbuka per poli.

## Rawat jalan dan rawat inap

```csharp
var clinical = new ClinicalService(db);
Visit? next = await clinical.CallNextAsync(poli.Id, doctorStaffId);
await clinical.SaveSoapAsync(v.Id, "Demam 3 hari", "T 38,6 °C", "Demam dengue", "Rawat inap");
await clinical.AddDiagnosisAsync(v.Id, "A91");                  // ICD-10 dicek ke terminologi; yang pertama menjadi diagnosis utama
var melati = await db.Departments.FirstAsync(d => d.Code == "MELATI");
Visit stay = await clinical.AdmitAsync(v.Id, melati.Id, BedClass.Kelas3);   // tempat tidur kosong pertama di kelas itu
await clinical.RecordVitalsAsync(p.Id, stay.Id, vitals);        // NEWS2 disimpan; daftar pasien bangsal untuk tablet diperbarui
await clinical.TransferAsync(stay.Id, icuBedId);                // tempat tidur lama → Cleaning
ClinicalDocument summary = await clinical.DischargeAsync(stay.Id, DischargeType.Home, "Kontrol poli 1 minggu");
await clinical.MarkBedCleanAsync(oldBedId);                     // Cleaning → Free
```

- **Menyelesaikan kunjungan rawat jalan:** `FinishVisitAsync` mensyaratkan diagnosis dan membebankan konsultasi (tarif dokter umum, spesialis atau IGD).
- **Rawat inap:** menyalin diagnosis ke episode rawat inap. Pasien JKN (BPJS) dirawat di kelas 1–3, tidak di VIP.
- **Pulang:**
  - Mensyaratkan diagnosis.
  - Kamar dan asuhan keperawatan dibebankan per hari kalender (minimal satu), dan tugas bangsal yang masih terbuka dibatalkan.
  - Hasilnya adalah ringkasan pulang (`ClinicalDocument`, LOINC 18842-5) berisi diagnosis, lab abnormal dan obat yang diserahkan.
- **Tanda vital dari perangkat:** entri `VitalsSource.Device` belum tervalidasi sampai perawat memanggil `ValidateVitalsAsync`. Hanya tanda vital tervalidasi yang ikut diekspor ke FHIR.

## Farmasi

```csharp
var pharmacy = new PharmacyService(db);            // atau berikan IDrugInteractionSource, DrugKnowledge, OrganDosing Anda sendiri
Prescription rx = await pharmacy.PrescribeAsync(stay.Id,
    [new PrescriptionLine("simvastatin", 20, 1, 30), new PrescriptionLine("clarithromycin", 500, 2, 7)]);
PharmacyReview review = await pharmacy.ReviewAsync(rx.Id);   // review.Blocking bila ada temuan Stop
await pharmacy.DispenseAsync(rx.Id, overrideReason: review.Blocking ? "Dikonsultasikan dengan DPJP" : null);
IReadOnlyList<StockAlert> alerts = await pharmacy.StockAlertsAsync(expiringWithinDays: 90);
```

- **Interaksi:** obat baru dan obat yang sudah diserahkan dalam rangkaian kunjungan dicek melalui `CompositeInteractionSource.Default()`, kecuali Anda memberikan sumber lain.
- **Alergi:** alergi pasien dicek terhadap reaksi silang (`DrugKnowledge.AllergyConflict`).
- **Dosis ginjal:** dihitung dari eGFR (CKD-EPI 2021) hasil kreatinin terakhir (LOINC 2160-0).
- **Stok:** yang dicek hanya stok yang belum kedaluwarsa.
- **Verifikasi:** resep tanpa temuan Stop langsung terverifikasi. Penyerahan resep yang belum terverifikasi membutuhkan alasan override, yang ditambahkan ke hasil telaah dan diaudit.
- **Penyerahan:** memakai FEFO (first expiry, first out) dan hanya dari batch yang belum kedaluwarsa. Obat dibebankan sesuai harga batch.

## Laboratorium

```csharp
var lab = new LabService(db);
LabOrder o = await lab.OrderAsync(stay.Id, ["2823-3", "2160-0"], OrderPriority.Stat);   // nomor order LAByyMMdd####
string specimen = await lab.CollectAsync(o.Id);
LabResult k = await lab.EnterResultAsync(o.Id, "2823-3", 6.9);   // diberi flag; nilai kritis memicu CriticalLabResult
await lab.EnterResultAsync(o.Id, "2160-0", 1.4);
await lab.NotifyCriticalAsync(k.Id, "dr. Sari Wulandari, Sp.PD"); // konfirmasi baca ulang, diaudit
await lab.VerifyAsync(o.Id);                                     // ditolak selama nilai kritis belum dilaporkan
string oml = await lab.ToHl7Async(o.Id);                         // OML^O21 untuk LIS
int imported = await lab.ImportHl7ResultsAsync(oruFromLis);      // ORU^R01, order dicocokkan dengan nomor placer (OBR-2), tes dengan LOINC
```

Flag rujukan dan kritis berasal dari `LabInterpreter` (Medical.Clinical). Setiap tes dibebankan sesuai tarif tenant
(`LAB-<loinc>`).

## Radiologi

```csharp
var rad = new RadiologyService(db);
RadiologyOrder r = await rad.OrderAsync(stay.Id, "87.44", "Foto toraks PA", "CR", OrderPriority.Urgent, "Sesak napas");
await rad.ScheduleAsync(r.Id, DateTime.UtcNow.AddHours(1));
var worklist = await rad.WorklistAsync("CR");                    // stat → urgent → rutin, lalu jadwal
await rad.PerformedAsync(r.Id, studyInstanceUid);
await rad.ReportAsync(r.Id, findings: "…", impression: "…");     // hanya setelah pemeriksaan dilakukan
```

Nomor akses berformat `RADyyMMdd####` per tenant. Worklist berisi data untuk DICOM modality worklist; layanan C-FIND
diserahkan ke PACS / broker Anda.

## Kasir dan klaim BPJS

```csharp
var billing = new BillingService(db);
BillSummary bill = await billing.SummaryAsync(stay.Id);          // biaya yang belum ditagih dalam rangkaian kunjungan (poli → bangsal), per kategori
var (claim, issues) = await billing.BpjsClaimAsync(stay.Id, coderNik: "3171000000000001");
Invoice inv = await billing.IssueAsync(stay.Id);                 // INV/yyyyMM/#####
if (inv.PatientAmount > inv.Paid) await billing.PayAsync(inv.Id, inv.PatientAmount - inv.Paid, "qris");
var revenue = await billing.RevenueAsync(fromUtc, toUtc);       // per kategori biaya
```

| Penjamin | Dibayar pasien | Dibayar penjamin |
|---|---|---|
| Umum | seluruhnya | 0 |
| BPJS | hanya selisih naik kelas ke VIP | tarif rumah sakit, diklaim melalui INA-CBG |
| Asuransi | 0 | seluruhnya |

`BpjsClaimAsync` menyusun `InaCbgClaim` (Medical.Bpjs) dengan tarif rumah sakit yang dipecah sesuai format E-Klaim:
konsultasi, keperawatan, laboratorium, radiologi, obat, kamar atau rawat intensif, prosedur dan BMHP. Fungsi ini juga
mengisi kelas rawat, status pulang, lama rawat ICU dan `KodeTarif` tenant, lalu menjalankan `InaCbgClaimBuilder.Validate`
untuk pemeriksaan sebelum grouping. Tarif paket ditentukan oleh grouper E-Klaim.

## Dasbor dan ekspor FHIR

```csharp
var dash = new DashboardService(db);
HospitalDashboard d = await dash.SnapshotAsync();
// d.BedOccupancyRate (BOR: terisi / tempat tidur yang tidak diblokir, %), d.AverageLengthOfStay (ALOS pasien pulang 30 hari terakhir),
// d.Queues, d.Wards, d.HighNews2 (NEWS2 terakhir ≥ 5), d.CriticalUnacknowledged, d.PrescriptionsToReview, d.RevenueToday
IReadOnlyList<BedTile> board = await dash.BedBoardAsync(melati.Id);

PatientRecord record = await HisRecords.ToPatientRecordAsync(db, p.Id);   // kunjungan, tanda vital tervalidasi, lab, diagnosis, obat diserahkan, alergi
var ips = new FhirMapper().ToPatientSummary(record);                       // FHIR R4 International Patient Summary
```

## Pemantauan waktu nyata (`Medical.Devices`, pratinjau)

```csharp
var engine = new MonitoringEngine();                          // MonitoringOptions: umur data, konfirmasi, histeresis, interval pencatatan
engine.Bind("RSMN-MON-01", "ICU-01", patientId, "Budi Santoso", visitId);   // saat masuk rawat / pindah
engine.SetManual("RSMN-MON-01", Consciousness.Alert, onOxygen: true);        // AVPU dan oksigen diisi perawat
engine.Alert += a => Notify(a.BedCode, a.Raised, a.Level, a.Message);
engine.ChartDue += s => _ = clinical.RecordVitalsAsync(s.PatientId!, s.VisitId, s.Vitals!, VitalsSource.Device, s.DeviceId);

await using var monitors = Hl7MonitorFeed.Tcp(2575);                   // ORU^R01 lewat MLLP; id perangkat = MSH-3
await using var sensors = MqttSenMLFeed.Connect("broker.local", 1883);  // topik medical/+/telemetry
sensors.PumpStatus += p => Show(p.DeviceId, p.Drug, p.RateMlPerHour, p.MinutesRemaining, p.Alarm);
var gateway = new DeviceGateway(engine, monitors, sensors);
gateway.FeedError += (feed, e) => log.LogWarning(e, "{Feed} gagal", feed.Name);
await gateway.RunAsync(stoppingToken);
```

- **Feed:**
  - `Hl7MonitorFeed` membaca tanda vital OBX menurut LOINC (nadi 8867-4, napas 9279-1, SpO₂ 59408-5 / 2708-6, tekanan darah 8480-6 / 8462-4, suhu 8310-5 / 8331-1, °F dikonversi).
  - `MqttSenMLFeed` membaca rekaman SenML `hr rr spo2 sbp dbp temp o2`. Rekaman pompa `rate vi vtbi alarm drug` memicu `InfusionPumpStatus` lengkap dengan `MinutesRemaining`.
- **Simulator:**
  - `BedsideSimulation` menjalankan `PatientMonitorSimulator` dari IoTCom.Net (skenario `Sepsis`, `Stable`, `Hypoxia`, `Hypertension`) dan mengirim pesan ORU^R01 sungguhan lewat koneksi MLLP dalam proses.
  - `InfusionPumpSimulator` menerbitkan SenML lewat MQTT.

| Langkah mesin | Aturan |
|---|---|
| Plausibilitas | nadi 20–300, napas 2–80, SpO₂ 50–100, sistolik 40–300, diastolik 15–200, suhu 30–44 °C; bacaan yang ditolak dihitung (`BedSnapshot.Rejected`) |
| Penyaringan | median 3 nilai terakhir untuk nadi, napas dan SpO₂ |
| Kesegaran | napas, SpO₂, sistolik dan nadi paling lama 10 menit, suhu 60 menit; bila tidak, dicantumkan di `Missing` dan NEWS2 tidak dihitung |
| Eskalasi | NEWS2 dengan respons klinis RCP 2017: Low 1–4, LowMedium (satu parameter bernilai 3), Medium 5–6, High ≥ 7 |
| Peringatan | LowMedium ke atas, hanya untuk perangkat yang terikat ke pasien; muncul setelah 2 evaluasi berturut-turut di level itu, berakhir setelah 3 evaluasi di bawahnya |
| Pencatatan | `ChartDue` tiap 15 menit waktu bacaan; SIMRS menyimpan set itu sebagai tanda vital perangkat yang menunggu validasi perawat |

**Yang disimulasikan.** Di aplikasi SIMRS, galeri dan CLI, monitor pasien adalah simulator IoTCom.Net. Aplikasi SIMRS
menjalankan satu monitor per tempat tidur ICU yang terisi (skenario menurut nomor tempat tidur), 60 detik simulasi per
detak 2 detik, dengan pemanasan satu jam. Pompa infusnya adalah model di dalam aplikasi, bukan MQTT. Parsing HL7 dan
SenML-nya nyata, tetapi belum ada monitor atau pompa vendor yang diuji atau disertifikasi. Vendor yang mengirim kode
IEEE 11073 MDC alih-alih kode LOINC di atas memerlukan pemetaan. Tanpa `SetManual`, mesin menilai AVPU sebagai Alert dan
udara ruangan; aplikasi SIMRS tidak memanggilnya.

## Sinkronisasi luring (`Medical.Sync`)

Aplikasi visite menulis semuanya ke SQLite lokal lebih dulu dan menyinkronkan saat jaringan tersedia. Setiap tipe data
punya kebijakan konflik:

| Tipe (`RoundsTypes`) | Kebijakan | Arti |
|---|---|---|
| `patient` (daftar pasien bangsal) | `ReadOnly` | hanya server yang menulis; perubahan dari perangkat ditolak dan perangkat menerima versi server |
| `vitals`, `note` | `AppendOnly` | tidak berubah setelah ditulis; pengiriman ulang dengan isi sama diterima (idempoten), isi berbeda ditolak |
| `task` | `LastWriterWins` | stempel hybrid logical clock yang lebih besar menang; bila sama, diputuskan oleh id node |
| (tipe Anda) | `ServerWins` | diterima hanya bila server belum mengubah rekaman sejak klien membacanya (`BaseSeq`) |

```csharp
// server, per tenant: SyncServer di atas basis data rumah sakit
SyncServer server = RoundsSync.Server(db);                       // HospitalSyncStore + RoundsTypes.Policies

// klien
using var store = SqliteSyncStore.Open(Path.Combine(FileSystem.AppDataDirectory, "rounds.db"));
http.DefaultRequestHeaders.Add("X-Device-Key", deviceKey);
var client = new SyncClient(store, new HttpSyncTransport(http, $"{his}/api/rs-nusantara/sync"), nodeId: "tablet-melati-01");
client.Save(RoundsTypes.Vitals, vitals.Id, vitals);              // tetap jalan tanpa jaringan; baris "dirty" sampai diterima
SyncReport r = await client.SyncAsync();                         // "pushed 1, pulled 12, conflicts 0" atau "offline (…)"
var ward = client.All<RoundsPatient>(RoundsTypes.Patient);
```

- **Mengapa kursor memakai nomor urut server:**
  - Tablet yang luring selama satu jam mengirim perubahan berstempel waktu lama. Bila klien menarik "semua setelah stempel T", tablet lain sudah melewati T dan tidak akan pernah melihat perubahan itu.
  - Karena itu server memberi nomor urut yang terus naik (`SyncChange.Seq`) pada setiap versi yang disimpan, dan tarikan meminta `Seq > kursor`. Dengan begitu kiriman yang terlambat tetap muncul pada tarikan berikutnya di semua perangkat.
- **Isi tarikan:** setiap tarikan hanya mengembalikan versi terbaru tiap rekaman, dengan halaman bawaan hingga 500.
- **Jam:** `HybridLogicalClock` (Kulkarni 2014) mengemas milidetik fisik dan penghitung dalam satu `long`. Jam ini tidak pernah mundur dan selalu melampaui stempel yang pernah diterimanya, sehingga jam perangkat yang meleset tidak mengacaukan urutan perubahan.
- **Konvergensi:** perubahan yang ditolak dicatat di perangkat (`SqliteSyncStore.Conflicts()`) dan diganti versi server. Perubahan yang ditarik tidak menimpa suntingan lokal yang lebih baru dan belum terkirim. Kegagalan jaringan membiarkan data tetap "dirty" untuk percobaan berikutnya.
- **Sisi rumah sakit:**
  - `HospitalSyncStore` menyimpan log sinkronisasi (outbox) di basis data rumah sakit.
  - Rawat inap, pindah, tanda vital dan pulang memperbarui daftar pasien bangsal; `RoundsSync.AddTaskAsync` menerbitkan tugas.
  - Tanda vital yang dikirim menjadi baris `VitalsEntry` (`VitalsSource.MobileApp`). Server menghitung ulang NEWS2; nilai dari perangkat hanya sebagai saran.
  - Catatan menjadi `NursingNote`, dan penyelesaian tugas memperbarui `WardTask`.
  - Tarikan dibatasi per bangsal.

## Aplikasi referensi SIMRS (`apps/Medical.His`)

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project apps/Medical.His --urls http://localhost:5300
# PowerShell: $env:ASPNETCORE_ENVIRONMENT="Development"; dotnet run --project apps/Medical.His --urls http://localhost:5300
```

Buka `http://localhost:5300`, lalu pilih rumah sakit dan pengguna demo. Jalankan dalam lingkungan Development, atau
publish aplikasinya (`dotnet publish apps/Medical.His -c Release`). Aset web statis tidak ditemukan bila file
executable di `bin` dijalankan dalam lingkungan Production. Saat pertama dijalankan, aplikasi membuat
`App_Data/his.db` dan mengisi data kedua rumah sakit.

| Rumah sakit (id tenant) | Kota | Kelas |
|---|---|---|
| RS Medika Nusantara (`rs-nusantara`) | Jakarta | B |
| RSUD Kota Bahari (`rs-bahari`) | Makassar | C |

Setiap rumah sakit punya tiga poli dan IGD (prefiks antrean A, B, C, E), Bangsal Melati, Bangsal Mawar dan ICU, 24 tempat
tidur (4 tempat tidur ICU dengan monitor), 14 akun staf, serta tarif dan stok obatnya sendiri.

| Kunci konfigurasi | Bawaan | Fungsi |
|---|---|---|
| `His:Provider` | `Sqlite` | `SqlServer` untuk memakai SQL Server |
| `ConnectionStrings:His` | `Data Source=App_Data/his.db` | koneksi basis data |
| `His:SeedPatients` | `40` | pasien sintetis per rumah sakit saat pertama dijalankan |
| `His:DemoLogin` | `true` | mengaktifkan `/account/demo` |
| `His:DeviceKeys:{tenant}` | — (`demo-device-key` hanya selama `His:DemoLogin` aktif) | kunci yang dikirim klien sinkronisasi di header `X-Device-Key` |
| `His:SimulateDevices` | `true` | monitor ICU dan pompa tersimulasi |
| `His:MllpPort` | (kosong) | juga menerima ORU^R01 dari monitor sungguhan di port TCP ini; MSH-3 harus sama dengan id monitor tempat tidur (mis. `RSMN-MON-01`) |
| `His:TickSeconds` | `2` | detik nyata antar-detak simulasi |
| `His:SimulatedSecondsPerTick` | `60` | waktu simulasi per detak |

| Peran (pengguna demo) | Modul |
|---|---|
| Admin (`admin`) | semua |
| Pendaftaran (`pendaftaran`) | dasbor, pendaftaran |
| Dokter (`dr.andi`, `dr.sari`, `dr.budi`, `dr.igd`) | dasbor, pendaftaran, rawat jalan, bangsal, monitor, farmasi, lab, radiologi |
| Perawat (`ns.dewi`, `ns.eko`) | dasbor, pendaftaran, rawat jalan, bangsal, monitor |
| Apoteker (`apt.fitri`) | dasbor, farmasi |
| Analis lab (`analis`) | dasbor, lab |
| Radiografer / radiolog (`radiografer`, `dr.rad`) | dasbor, radiologi |
| Kasir / koder (`kasir`, `koder`) | dasbor, kasir |

| Endpoint | Akses |
|---|---|
| `GET /account/demo?tenant=&user=&lang=&returnUrl=` · `POST /account/login` · `GET /account/logout` | login demo (tanpa kata sandi); keduanya mengembalikan 404 bila `His:DemoLogin=false` |
| `POST /api/{tenant}/sync/push` · `POST /api/{tenant}/sync/pull?ward=MELATI` | header `X-Device-Key` |
| `GET /api/{tenant}/sync/wards` | header `X-Device-Key` (kode dan nama bangsal untuk pengaturan perangkat) |
| `GET /api/{tenant}/fhir/Patient/{id}/$summary` | pengguna yang masuk di tenant yang sama; FHIR IPS, diaudit |
| `GET /api/{tenant}/dashboard` | pengguna yang masuk di tenant yang sama; JSON `HospitalDashboard` |
| `GET /health` | anonim |

Halaman: `/login`, `/{tenant}` (dasbor), `/{tenant}/registration`, `outpatient`, `wards`, `monitoring`, `pharmacy`, `lab`,
`radiology`, `billing`. Antarmuka mengikuti bahasa pengguna (bawaan bahasa Indonesia, bahasa Inggris dapat dipilih saat
masuk).

**Catatan keamanan:**

- **Login:** `/account/demo` dan `/account/login` hanya memakai nama pengguna, untuk demo. `His:DemoLogin=false` mematikan keduanya; tambahkan OpenID Connect (penyedia identitas Anda) yang menerbitkan klaim `tenant` dan peran. `returnUrl` hanya menerima path lokal (tanpa pengalihan `//host`).
- **Kunci perangkat:**
  - Isi `His:DeviceKeys:{tenant}` dengan rahasia acak yang panjang per tenant dari penyimpan rahasia.
  - Bila kunci ini kosong, aplikasi menerima `demo-device-key` hanya dalam mode demo; dengan `His:DemoLogin=false` tenant tanpa kunci menolak semua perangkat. Kunci dibandingkan dalam waktu konstan.
  - Kunci dipakai bersama oleh semua perangkat satu tenant. Rotasi kunci secara berkala, atau gunakan kredensial per perangkat (mutual TLS atau token) di produksi.
- **Cakupan bangsal:** setiap tarikan wajib menyebut bangsal (`?ward=MELATI`); kode yang kosong atau tidak dikenal ditolak dengan 400, sehingga perangkat tidak pernah menerima pasien bangsal lain.
- **HTTPS:** di luar Development aplikasi mengalihkan ke HTTPS dan mengirim HSTS. Siapkan sertifikat (atau akhiri TLS di reverse proxy dengan forwarded headers), dan lindungi basis data serta `App_Data`.

## Aplikasi visite (`apps/Medical.Rounds`)

`Medical.Rounds.Core` berisi model yang tidak bergantung platform (`RoundsApp`):

- **Penyimpanan luring:** satu file SQLite per tenant dan bangsal (`rounds-{tenant}-{ward}.db`).
- **Daftar pasien bangsal:** diurutkan menurut NEWS2. Nilai dari perangkat dipakai bila lebih baru daripada nilai server, lengkap dengan tugas terbuka dan penanda belum tersinkron.
- **Tanda vital:** input dengan pratinjau NEWS2 di perangkat (`RoundsApp.Preview`); server menghitung ulang skornya.
- **Catatan dan tugas:** catatan keperawatan dan penyelesaian tugas.

Aplikasi MAUI menyinkronkan setiap 30 detik.

```bash
dotnet workload install maui-windows            # maui-android untuk Android
dotnet build Medical.Rounds.slnx                # Windows
dotnet run --project apps/Medical.Rounds -f net10.0-windows10.0.19041.0
dotnet build apps/Medical.Rounds -f net10.0-android -p:IncludeAndroid=true   # perlu Android SDK
```

- **Solusi:** aplikasi ini tidak termasuk `Medical.Net.slnx` karena runner CI tidak punya workload MAUI. Build dengan `Medical.Rounds.slnx`.
- **Pengaturan:** URL server, tenant, bangsal, perawat, kunci perangkat dan id perangkat. Server bawaan `http://localhost:5300`, atau `http://10.0.2.2:5300` di emulator Android.
- **Tangkapan layar:** dengan SIMRS berjalan, `Medical.Rounds.exe --capture docs/images en|id` melakukan sinkronisasi, merender halaman daftar pasien, pasien, tanda vital dan menunggu-sinkron ke PNG, lalu keluar.

## CLI

```bash
medical his seed --db his.db --patients 40               # dua rumah sakit demo
medical his dashboard --db his.db --tenant rs-bahari --lang id
medical his patient 000001 --db his.db --fhir ringkasan.json
medical monitor --minutes 120 --step 5                   # monitor ICU tersimulasi → MLLP → peringatan NEWS2; kode keluar 3 bila ada peringatan High
medical sync demo --lang id                              # dua tablet, perubahan luring, konflik, konvergensi
```

## Galeri

Kelompok **Platform rumah sakit** menambahkan tiga demo, di web dan desktop dalam bahasa Indonesia dan Inggris, sehingga
totalnya menjadi 32:

| Slug | Demo |
|---|---|
| `his` | SIMRS multi-tenant: dua rumah sakit dalam satu instalasi, terisolasi dengan filter kueri dan penjaga tulis |
| `live-monitoring` | Monitor pasien langsung: monitor ICU tersimulasi lewat HL7 / MLLP, penyaringan artefak, peringatan NEWS2 terkonfirmasi |
| `offline-sync` | Visite luring: dua tablet dan SIMRS, langkah demi langkah sampai konvergen |

## Tangkapan layar

| SIMRS | |
|---|---|
| ![](../images/his-login.png) | ![](../images/his-dashboard.png) |
| ![](../images/his-registration.png) | ![](../images/his-outpatient.png) |
| ![](../images/his-wards.png) | ![](../images/his-monitoring.png) |
| ![](../images/his-pharmacy.png) | ![](../images/his-lab.png) |
| ![](../images/his-radiology.png) | ![](../images/his-billing.png) |

| Visite (MAUI) | | | |
|---|---|---|---|
| ![](../images/rounds-ward-id.png) | ![](../images/rounds-patient-id.png) | ![](../images/rounds-vitals-id.png) | ![](../images/rounds-ward-pending-id.png) |

| Galeri: desktop | Galeri: web |
|---|---|
| ![](../images/desktop-his-id.png) | ![](../images/web-his.png) |
| ![](../images/desktop-live-monitoring-id.png) | ![](../images/web-live-monitoring.png) |
| ![](../images/desktop-offline-sync-id.png) | ![](../images/web-offline-sync.png) |

## Keterbatasan yang diketahui

- **Monitor tersimulasi:** monitor dan pompa pada demo disimulasikan, dan belum ada perangkat sungguhan yang disertifikasi atau diuji. Validasi keluaran HL7 / SenML dan perilaku alarm tiap vendor sebelum dipakai secara klinis.
- **Grouping INA-CBG:** grouping dan tarif paket berasal dari E-Klaim. `BpjsClaimAsync` hanya menyiapkan dan memeriksa data klaim, tidak melakukan grouping.
- **`Medical.Devices` masih pratinjau:** statusnya tetap pratinjau selama IoTCom.Net masih prarilis, dan API-nya dapat berubah.
- **MAUI Android di CI:** build Android tidak dijalankan di CI (tidak ada workload MAUI). Windows dan Android di-build secara lokal, dan iOS belum disiapkan.
- **Login demo:** lihat catatan keamanan. OpenID Connect belum disertakan.
- **Satu basis data:** multi-tenancy memakai skema bersama dalam satu basis data. Opsi satu basis data per tenant belum tersedia.
- **Skema basis data:** skema dibuat dengan `EnsureCreated`, bukan migrasi. Tambahkan migrasi EF Core sebelum mengembangkan basis data produksi.
