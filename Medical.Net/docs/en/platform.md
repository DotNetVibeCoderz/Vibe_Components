# Hospital platform (v2.0)

Medical.Net 2.0 adds a multi-tenant hospital information system core, real-time NEWS2 from bedside devices, and
offline-first sync for ward rounds, plus two reference apps built on them: a Blazor Server HIS and a MAUI ward-round
app. · [Bahasa Indonesia](../id/platform.md)

> The HIS is a reference implementation for building your own system, not a certified product. Monitoring alerts
> support — never replace — clinical observation. All demo hospitals, staff and patients are fictional.

## Architecture

| Part | Kind | Contents |
|---|---|---|
| `Medical.Hospital` | NuGet package | EF Core model (SQLite / SQL Server) with shared-schema multi-tenancy; registration and queues, clinical care (SOAP, ICD-10, vitals with NEWS2, admission / transfer / discharge), pharmacy, laboratory, radiology, billing (Umum / BPJS / insurance, INA-CBG claim data), dashboard, `MonitoringEngine`, the server side of ward-round sync, a demo seeder |
| `Medical.Sync` | NuGet package | Hybrid logical clock, per-type conflict policies, `SyncServer`, `SqliteSyncStore` + `SyncClient`, in-process and HTTP transports, ward-round contracts (`Medical.Sync.Rounds`) |
| `Medical.Devices` | NuGet package, **preview** (`2.0.0-preview`) | HL7 v2 / MLLP monitor feed, MQTT + SenML feed for sensors and infusion pumps, bedside and pump simulators, `DeviceGateway` — built on IoTCom.Net |
| `apps/Medical.His` | Reference app | Blazor Server HIS for two demo hospitals: dashboard, registration, outpatient, wards, live monitoring, pharmacy, lab, radiology, billing; sync, FHIR and dashboard APIs |
| `apps/Medical.Rounds.Core` + `apps/Medical.Rounds` | Reference app | MAUI ward-round app (Windows, Android): offline SQLite, ward list by NEWS2, vitals, notes, tasks |

```
bedside monitors ──HL7 ORU^R01 / MLLP──┐
MQTT sensors, pumps ──SenML──────────┤ Medical.Devices ──► MonitoringEngine ──► alerts, bed tiles
                                     │                        └─ every 15 min ──► ClinicalService.RecordVitalsAsync (pending validation)
Blazor HIS ──► Medical.Hospital services ──► HospitalDbContext (TenantId on every row) ──► SQLite / SQL Server
                                                   └─ SyncLog (outbox) ◄──► /api/{tenant}/sync ◄──► Rounds tablets (SQLite)
```

`Medical.Devices` is a preview package because IoTCom.Net 0.22.0-preview.1 (Apache-2.0) is published on NuGet only as a
prerelease. It pins `System.Formats.Cbor` 10.0.15 (fix for CVE-2026-118747; IoTCom.Net brings 10.0.12). `Medical.Hospital`
and `Medical.Sync` do not depend on it.

## Multi-tenancy

One database holds every hospital. Each row carries a `TenantId`; global query filters hide other tenants' rows, and
`SaveChanges` stamps new rows with the current tenant and refuses to add, change or delete another tenant's row.

```csharp
var options = new DbContextOptionsBuilder<HospitalDbContext>().UseSqlite("Data Source=his.db").Options;
await HospitalSeeder.SeedAsync(options);              // schema + "RS Medika Nusantara" and "RSUD Kota Bahari" (idempotent)

await using var jakarta = new HospitalDbContext(options, new FixedTenantContext("rs-nusantara", "admin"));
await using var makassar = new HospitalDbContext(options, new FixedTenantContext("rs-bahari", "admin"));
var p = await jakarta.Patients.FirstAsync();
await makassar.Patients.AnyAsync(x => x.Id == p.Id);   // false — query filter
makassar.Patients.Update(p);
await makassar.SaveChangesAsync();                      // TenantViolationException
long next = await jakarta.NextAsync("mrn");             // per-tenant counter, retried on concurrent updates
```

- **Tenant context:** implement `ITenantContext` (`TenantId`, `UserName`, `Role`) from the signed-in user. The HIS app's `HisSession` does this from cookie claims; background jobs use `FixedTenantContext`.
- **Counters:** `NextAsync(key)` issues MRNs (`000001`), daily queue numbers, lab / radiology accession numbers and invoice numbers per tenant. The counter value is a concurrency token.
- **What is not filtered:** the `Tenants` table itself. `IgnoreQueryFilters()` bypasses the read filter, so keep it out of request code.
- **Audit:** `db.AuditEvent(...)` writes `HisAudit` rows (tenant-filtered) for sign-in, patient creation, admission, discharge, dispensing overrides, critical-value read-back and FHIR export.
- **Events:** services publish `PatientRegistered`, `VisitStarted`, `PatientAdmitted`, `PatientDischarged`, `CriticalLabResult` and `VitalsCharted` on an optional `IMedicalEventBus`.

## Registration and queues

```csharp
var reg = new RegistrationService(db);
HisPatient p = await reg.RegisterAsync(new NewPatient("Siti", "Rahayu", Gender.Female, new DateOnly(1990, 1, 25),
    Nik: "3171015501900001", BpjsNumber: "0001234567890"));    // a known NIK returns the existing patient
var poli = await db.Departments.FirstAsync(d => d.Code == "POLI-DALAM");
Visit v = await reg.StartVisitAsync(p.Id, poli.Id, VisitKind.Outpatient, PayerType.Bpjs, sepNumber: "0301R0011126V000123");
// v.QueueNumber "B-001": clinic prefix + daily sequence; a registration charge is added
var queue = await reg.QueueAsync(poli.Id);                      // in consultation, then emergency, then arrival order
var found = await reg.SearchAsync("rahayu");                    // MRN, NIK, BPJS number or name
```

The NIK must be 16 digits and the BPJS card number 13 digits. A BPJS visit needs a card number, and a patient can have
only one open visit per clinic.

## Outpatient and inpatient care

```csharp
var clinical = new ClinicalService(db);
Visit? next = await clinical.CallNextAsync(poli.Id, doctorStaffId);
await clinical.SaveSoapAsync(v.Id, "Demam 3 hari", "T 38,6 °C", "Demam dengue", "Rawat inap");
await clinical.AddDiagnosisAsync(v.Id, "A91");                  // ICD-10 checked against the terminology; first one is primary
var melati = await db.Departments.FirstAsync(d => d.Code == "MELATI");
Visit stay = await clinical.AdmitAsync(v.Id, melati.Id, BedClass.Kelas3);   // first free bed of the class
await clinical.RecordVitalsAsync(p.Id, stay.Id, vitals);        // NEWS2 stored; the ward list for tablets is refreshed
await clinical.TransferAsync(stay.Id, icuBedId);                // old bed → Cleaning
ClinicalDocument summary = await clinical.DischargeAsync(stay.Id, DischargeType.Home, "Kontrol poli 1 minggu");
await clinical.MarkBedCleanAsync(oldBedId);                     // Cleaning → Free
```

- **Finishing an outpatient visit:** `FinishVisitAsync` requires a diagnosis and charges the consultation (general, specialist or emergency tariff).
- **Admission:** copies the diagnoses to the inpatient stay. JKN (BPJS) patients are admitted to class 1–3, not VIP.
- **Discharge:** requires a diagnosis. Room and nursing are charged per calendar day (minimum one), open ward tasks are cancelled, and the result is a discharge summary (`ClinicalDocument`, LOINC 18842-5) with diagnoses, abnormal labs and dispensed drugs.
- **Device vitals:** `VitalsSource.Device` entries stay unvalidated until a nurse calls `ValidateVitalsAsync`. Only validated vitals reach the FHIR export.

## Pharmacy

```csharp
var pharmacy = new PharmacyService(db);            // or pass your own IDrugInteractionSource, DrugKnowledge, OrganDosing
Prescription rx = await pharmacy.PrescribeAsync(stay.Id,
    [new PrescriptionLine("simvastatin", 20, 1, 30), new PrescriptionLine("clarithromycin", 500, 2, 7)]);
PharmacyReview review = await pharmacy.ReviewAsync(rx.Id);   // review.Blocking when any finding is Stop
await pharmacy.DispenseAsync(rx.Id, overrideReason: review.Blocking ? "Dikonsultasikan dengan DPJP" : null);
IReadOnlyList<StockAlert> alerts = await pharmacy.StockAlertsAsync(expiringWithinDays: 90);
```

- **Interactions:** checks the new drugs and those already dispensed in the visit chain, through `CompositeInteractionSource.Default()` unless you pass another source.
- **Allergies:** checks the patient's allergies for cross-reactivity (`DrugKnowledge.AllergyConflict`).
- **Renal dosing:** uses eGFR (CKD-EPI 2021) from the latest creatinine result (LOINC 2160-0).
- **Stock:** checks unexpired stock.
- **Review outcome:** a review without a Stop finding verifies the prescription. Dispensing an unverified prescription needs an override reason, which is appended to the review and audited.
- **Dispensing:** FEFO (first expiry, first out) from unexpired batches only. Drugs are charged at batch cost.

## Laboratory

```csharp
var lab = new LabService(db);
LabOrder o = await lab.OrderAsync(stay.Id, ["2823-3", "2160-0"], OrderPriority.Stat);   // placer number LAByyMMdd####
string specimen = await lab.CollectAsync(o.Id);
LabResult k = await lab.EnterResultAsync(o.Id, "2823-3", 6.9);   // flagged; a critical value raises CriticalLabResult
await lab.EnterResultAsync(o.Id, "2160-0", 1.4);
await lab.NotifyCriticalAsync(k.Id, "dr. Sari Wulandari, Sp.PD"); // read-back, audited
await lab.VerifyAsync(o.Id);                                     // refused while a critical value is not read back
string oml = await lab.ToHl7Async(o.Id);                         // OML^O21 for the LIS
int imported = await lab.ImportHl7ResultsAsync(oruFromLis);      // ORU^R01, order matched by placer number (OBR-2), tests by LOINC
```

Reference and critical flags come from `LabInterpreter` (Medical.Clinical). Each test is charged from the tenant's tariff (`LAB-<loinc>`).

## Radiology

```csharp
var rad = new RadiologyService(db);
RadiologyOrder r = await rad.OrderAsync(stay.Id, "87.44", "Foto toraks PA", "CR", OrderPriority.Urgent, "Sesak napas");
await rad.ScheduleAsync(r.Id, DateTime.UtcNow.AddHours(1));
var worklist = await rad.WorklistAsync("CR");                    // stat → urgent → routine, then scheduled time
await rad.PerformedAsync(r.Id, studyInstanceUid);
await rad.ReportAsync(r.Id, findings: "…", impression: "…");     // only after the study is performed
```

Accession numbers are `RADyyMMdd####` per tenant. The worklist holds the data for a DICOM modality worklist; serving
C-FIND is left to your PACS / broker.

## Billing and BPJS claims

```csharp
var billing = new BillingService(db);
BillSummary bill = await billing.SummaryAsync(stay.Id);          // uninvoiced charges of the visit chain (clinic → ward), by category
var (claim, issues) = await billing.BpjsClaimAsync(stay.Id, coderNik: "3171000000000001");
Invoice inv = await billing.IssueAsync(stay.Id);                 // INV/yyyyMM/#####
if (inv.PatientAmount > inv.Paid) await billing.PayAsync(inv.Id, inv.PatientAmount - inv.Paid, "qris");
var revenue = await billing.RevenueAsync(fromUtc, toUtc);       // by charge category
```

| Payer | Patient pays | Payer amount |
|---|---|---|
| Umum | everything | 0 |
| BPJS | only a VIP room upgrade | the hospital tariff, claimed through INA-CBG |
| Insurance | 0 | everything |

`BpjsClaimAsync` builds an `InaCbgClaim` (Medical.Bpjs) with the hospital tariff split as E-Klaim expects: consultation, nursing, lab, radiology, drugs,
room or intensive care, procedures and BMHP. It also sets the care class, discharge status, ICU days and the tenant's
`KodeTarif`, and runs `InaCbgClaimBuilder.Validate` for pre-grouping checks. The package rate itself is decided by the
E-Klaim grouper.

## Dashboard and FHIR export

```csharp
var dash = new DashboardService(db);
HospitalDashboard d = await dash.SnapshotAsync();
// d.BedOccupancyRate (occupied / non-blocked beds, %), d.AverageLengthOfStay (discharges of the last 30 days),
// d.Queues, d.Wards, d.HighNews2 (latest NEWS2 ≥ 5), d.CriticalUnacknowledged, d.PrescriptionsToReview, d.RevenueToday
IReadOnlyList<BedTile> board = await dash.BedBoardAsync(melati.Id);

PatientRecord record = await HisRecords.ToPatientRecordAsync(db, p.Id);   // visits, validated vitals, labs, diagnoses, dispensed drugs, allergies
var ips = new FhirMapper().ToPatientSummary(record);                       // FHIR R4 International Patient Summary
```

## Real-time monitoring (`Medical.Devices`, preview)

```csharp
var engine = new MonitoringEngine();                          // MonitoringOptions: ages, confirmation, hysteresis, chart interval
engine.Bind("RSMN-MON-01", "ICU-01", patientId, "Budi Santoso", visitId);   // on admission / transfer
engine.SetManual("RSMN-MON-01", Consciousness.Alert, onOxygen: true);        // AVPU and oxygen come from the nurse
engine.Alert += a => Notify(a.BedCode, a.Raised, a.Level, a.Message);
engine.ChartDue += s => _ = clinical.RecordVitalsAsync(s.PatientId!, s.VisitId, s.Vitals!, VitalsSource.Device, s.DeviceId);

await using var monitors = Hl7MonitorFeed.Tcp(2575);                   // ORU^R01 over MLLP; device id = MSH-3
await using var sensors = MqttSenMLFeed.Connect("broker.local", 1883);  // topic medical/+/telemetry
sensors.PumpStatus += p => Show(p.DeviceId, p.Drug, p.RateMlPerHour, p.MinutesRemaining, p.Alarm);
var gateway = new DeviceGateway(engine, monitors, sensors);
gateway.FeedError += (feed, e) => log.LogWarning(e, "{Feed} failed", feed.Name);
await gateway.RunAsync(stoppingToken);
```

- **Feeds:**
  - `Hl7MonitorFeed` reads OBX vital signs by LOINC (HR 8867-4, RR 9279-1, SpO₂ 59408-5 / 2708-6, BP 8480-6 / 8462-4, temperature 8310-5 / 8331-1, °F converted).
  - `MqttSenMLFeed` reads SenML records `hr rr spo2 sbp dbp temp o2`; pump records `rate vi vtbi alarm drug` raise `InfusionPumpStatus` with `MinutesRemaining`.
- **Simulators:**
  - `BedsideSimulation` drives IoTCom.Net's `PatientMonitorSimulator` (scenarios `Sepsis`, `Stable`, `Hypoxia`, `Hypertension`) and sends real ORU^R01 messages over an in-process MLLP connection.
  - `InfusionPumpSimulator` publishes SenML over MQTT.

| Engine step | Rule |
|---|---|
| Plausibility | HR 20–300, RR 2–80, SpO₂ 50–100, SBP 40–300, DBP 15–200, temperature 30–44 °C; rejected readings are counted (`BedSnapshot.Rejected`) |
| Filtering | median of the last 3 values for HR, RR and SpO₂ |
| Freshness | RR, SpO₂, SBP and HR no older than 10 min, temperature 60 min; otherwise listed in `Missing` and no NEWS2 |
| Escalation | NEWS2 with the RCP 2017 response: Low 1–4, LowMedium (a single parameter scoring 3), Medium 5–6, High ≥ 7 |
| Alerts | LowMedium and above, only for a device bound to a patient; raised after 2 consecutive evaluations at the level, cleared after 3 below it |
| Charting | `ChartDue` every 15 min of reading time; the HIS stores the set as device vitals pending nurse validation |

**What is simulated.** In the HIS app, the gallery and the CLI, the monitors are IoTCom.Net simulators. The HIS app runs one
per occupied ICU bed (scenario by bed number), 60 simulated seconds per 2-second tick, with an hour of warm-up. Its
infusion pumps are an in-app model, not MQTT. The HL7 and SenML parsing is real, but no vendor monitor or pump has been
tested or certified. Vendors that send IEEE 11073 MDC codes instead of the LOINC codes above need a mapping. Unless
`SetManual` is called, the engine scores AVPU as Alert and room air; the HIS app does not call it.

## Offline sync (`Medical.Sync`)

The ward-round app writes everything to a local SQLite store first and syncs when the network is there. Each record
type has a conflict policy:

| Type (`RoundsTypes`) | Policy | Meaning |
|---|---|---|
| `patient` (ward census) | `ReadOnly` | only the server writes; a device change is rejected and the device gets the server version |
| `vitals`, `note` | `AppendOnly` | immutable once written; a retry with the same payload is accepted (idempotent), a different payload is rejected |
| `task` | `LastWriterWins` | the higher hybrid-logical-clock timestamp wins, ties broken by node id |
| (your types) | `ServerWins` | accepted only if the server has not changed the record since the client read it (`BaseSeq`) |

```csharp
// server, per tenant: SyncServer over the hospital database
SyncServer server = RoundsSync.Server(db);                       // HospitalSyncStore + RoundsTypes.Policies

// client
using var store = SqliteSyncStore.Open(Path.Combine(FileSystem.AppDataDirectory, "rounds.db"));
http.DefaultRequestHeaders.Add("X-Device-Key", deviceKey);
var client = new SyncClient(store, new HttpSyncTransport(http, $"{his}/api/rs-nusantara/sync"), nodeId: "tablet-melati-01");
client.Save(RoundsTypes.Vitals, vitals.Id, vitals);              // works offline; the row is dirty until accepted
SyncReport r = await client.SyncAsync();                         // "pushed 1, pulled 12, conflicts 0" or "offline (…)"
var ward = client.All<RoundsPatient>(RoundsTypes.Patient);
```

- **Why server sequence cursors:** a tablet that was offline for an hour pushes changes stamped with old clock values. If clients pulled "everything after timestamp T", the other tablets would already have moved past T and never see those changes. The server therefore gives every stored version an increasing sequence number (`SyncChange.Seq`), and pulls ask for `Seq > cursor`, so a late push shows up in the next pull of every device.
- **Pull contents:** a pull returns only the latest version of each record, in pages of up to 500 by default.
- **Clocks:** `HybridLogicalClock` (Kulkarni 2014) packs physical milliseconds and a counter into one `long`. It never goes backwards and always exceeds every timestamp it has received, so skewed device clocks cannot reorder edits.
- **Convergence:** a rejected change is logged locally (`SqliteSyncStore.Conflicts()`) and replaced by the server version. A pulled change does not overwrite a newer local edit that is still dirty. A network failure leaves the data dirty for the next run.
- **Hospital side:**
  - `HospitalSyncStore` keeps the sync log (outbox) in the hospital database. Admission, transfer, vitals and discharge publish the ward census; `RoundsSync.AddTaskAsync` publishes tasks.
  - Pushed vitals become `VitalsEntry` rows (`VitalsSource.MobileApp`). The server recomputes NEWS2 and treats the device's value as advisory. Notes become `NursingNote` rows, and task completions update `WardTask`.
  - Pulls are scoped to a ward.

## The HIS reference app (`apps/Medical.His`)

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project apps/Medical.His --urls http://localhost:5300
# PowerShell: $env:ASPNETCORE_ENVIRONMENT="Development"; dotnet run --project apps/Medical.His --urls http://localhost:5300
```

Open `http://localhost:5300` and choose a hospital and a demo user. Run it in Development, or publish it
(`dotnet publish apps/Medical.His -c Release`). Static web assets don't resolve when the `bin` executable runs in Production.
The first start creates `App_Data/his.db` and seeds both hospitals.

| Hospital (tenant id) | City | Class |
|---|---|---|
| RS Medika Nusantara (`rs-nusantara`) | Jakarta | B |
| RSUD Kota Bahari (`rs-bahari`) | Makassar | C |

Each hospital has three clinics and an emergency department (queue prefixes A, B, C, E), the Melati, Mawar and ICU wards, 24 beds (4 ICU beds with
monitors), 14 staff accounts and its own tariffs and stock.

| Configuration key | Default | Purpose |
|---|---|---|
| `His:Provider` | `Sqlite` | `SqlServer` to use SQL Server |
| `ConnectionStrings:His` | `Data Source=App_Data/his.db` | database connection |
| `His:SeedPatients` | `40` | synthetic patients per hospital on first start |
| `His:DemoLogin` | `true` | enables `/account/demo` |
| `His:DeviceKeys:{tenant}` | — (`demo-device-key` only while `His:DemoLogin` is on) | key that sync clients send in `X-Device-Key` |
| `His:SimulateDevices` | `true` | simulated ICU monitors and pumps |
| `His:MllpPort` | (unset) | also listen for real monitors' ORU^R01 on this TCP port; MSH-3 must equal the bed's monitor id (e.g. `RSMN-MON-01`) |
| `His:TickSeconds` | `2` | real seconds between simulation ticks |
| `His:SimulatedSecondsPerTick` | `60` | simulated time per tick |

| Role (demo user) | Modules |
|---|---|
| Admin (`admin`) | all |
| Registration (`pendaftaran`) | dashboard, registration |
| Doctor (`dr.andi`, `dr.sari`, `dr.budi`, `dr.igd`) | dashboard, registration, outpatient, wards, monitoring, pharmacy, lab, radiology |
| Nurse (`ns.dewi`, `ns.eko`) | dashboard, registration, outpatient, wards, monitoring |
| Pharmacist (`apt.fitri`) | dashboard, pharmacy |
| Lab analyst (`analis`) | dashboard, lab |
| Radiographer / radiologist (`radiografer`, `dr.rad`) | dashboard, radiology |
| Cashier / coder (`kasir`, `koder`) | dashboard, billing |

| Endpoint | Access |
|---|---|
| `GET /account/demo?tenant=&user=&lang=&returnUrl=` · `POST /account/login` · `GET /account/logout` | demo sign-in (no password); both return 404 when `His:DemoLogin=false` |
| `POST /api/{tenant}/sync/push` · `POST /api/{tenant}/sync/pull?ward=MELATI` | `X-Device-Key` header |
| `GET /api/{tenant}/sync/wards` | `X-Device-Key` header (ward codes and names for device setup) |
| `GET /api/{tenant}/fhir/Patient/{id}/$summary` | signed-in user of the same tenant; FHIR IPS, audited |
| `GET /api/{tenant}/dashboard` | signed-in user of the same tenant; `HospitalDashboard` JSON |
| `GET /health` | anonymous |

Pages: `/login`, `/{tenant}` (dashboard), `/{tenant}/registration`, `outpatient`, `wards`, `monitoring`, `pharmacy`, `lab`,
`radiology`, `billing`. The UI follows the user's language (Indonesian by default, English selectable at sign-in).

**Security notes:**

- **Sign-in:** `/account/demo` and `/account/login` sign in by user name only, for demos. `His:DemoLogin=false` switches both off; add OpenID Connect (your identity provider) that issues the `tenant` and role claims. `returnUrl` accepts local paths only (no `//host` redirects).
- **Device keys:**
  - Set `His:DeviceKeys:{tenant}` to a long random secret per tenant, from a secret store.
  - When the key is unset, the app accepts `demo-device-key` in demo mode only; with `His:DemoLogin=false` an unconfigured tenant refuses every device. Keys are compared in constant time.
  - The key is shared by all devices of a tenant. Rotate it, or use per-device credentials (mutual TLS or tokens) in production.
- **Ward scope:** every pull must name a ward (`?ward=MELATI`); a missing or unknown ward code is refused with 400, so a device never receives other wards' patients.
- **HTTPS:** outside Development the app redirects to HTTPS and sends HSTS. Configure a certificate (or terminate TLS at a reverse proxy with forwarded headers), and protect the database and `App_Data`.

## The ward-round app (`apps/Medical.Rounds`)

`Medical.Rounds.Core` holds the platform-independent model (`RoundsApp`):

- **Offline storage:** a SQLite file per tenant and ward (`rounds-{tenant}-{ward}.db`).
- **Ward list:** sorted by NEWS2, using the device's value when it is newer than the server's, with open tasks and a pending-sync marker.
- **Vital signs:** entry with an on-device NEWS2 preview (`RoundsApp.Preview`); the server recomputes the score.
- **Notes and tasks:** nursing notes and task completion.

The MAUI app syncs every 30 seconds.

```bash
dotnet workload install maui-windows            # maui-android for Android
dotnet build Medical.Rounds.slnx                # Windows
dotnet run --project apps/Medical.Rounds -f net10.0-windows10.0.19041.0
dotnet build apps/Medical.Rounds -f net10.0-android -p:IncludeAndroid=true   # needs the Android SDK
```

- **Solution:** the app is not in `Medical.Net.slnx` because CI runners have no MAUI workloads. Build it with `Medical.Rounds.slnx`.
- **Settings:** server URL, tenant, ward, nurse, device key and device id. The default server is `http://localhost:5300`, or `http://10.0.2.2:5300` on the Android emulator.
- **Screenshots:** with the HIS running, `Medical.Rounds.exe --capture docs/images en|id` syncs, renders the ward list, patient, vital-signs and pending-sync pages to PNG, and exits.

## CLI

```bash
medical his seed --db his.db --patients 40               # the two demo hospitals
medical his dashboard --db his.db --tenant rs-bahari --lang id
medical his patient 000001 --db his.db --fhir summary.json
medical monitor --minutes 120 --step 5                   # simulated ICU monitors → MLLP → NEWS2 alerts; exit code 3 on a high alert
medical sync demo                                        # two tablets, offline edits, conflicts, convergence
```

## Gallery

The **Hospital platform** group adds three demos, on web and desktop in English and Indonesian, for 32 in total:

| Slug | Demo |
|---|---|
| `his` | Multi-tenant HIS: two hospitals on one installation, isolated by query filters and write guards |
| `live-monitoring` | Live bedside monitoring: simulated ICU monitors over HL7 / MLLP, artefact filtering, confirmed NEWS2 alerts |
| `offline-sync` | Offline ward rounds: two tablets and the HIS, step by step to convergence |

## Screenshots

| HIS | |
|---|---|
| ![](../images/his-login.png) | ![](../images/his-dashboard-en.png) |
| ![](../images/his-registration.png) | ![](../images/his-outpatient.png) |
| ![](../images/his-wards.png) | ![](../images/his-monitoring-en.png) |
| ![](../images/his-pharmacy.png) | ![](../images/his-lab.png) |
| ![](../images/his-radiology.png) | ![](../images/his-billing.png) |

| Ward rounds (MAUI) | | | |
|---|---|---|---|
| ![](../images/rounds-ward.png) | ![](../images/rounds-patient.png) | ![](../images/rounds-vitals.png) | ![](../images/rounds-ward-pending.png) |

| Gallery: desktop | Gallery: web |
|---|---|
| ![](../images/desktop-his.png) | ![](../images/web-his.png) |
| ![](../images/desktop-live-monitoring.png) | ![](../images/web-live-monitoring.png) |
| ![](../images/desktop-offline-sync.png) | ![](../images/web-offline-sync.png) |

## Known limitations

- **Simulated monitors:** the demo monitors and pumps are simulated, and no real device has been certified or tested. Validate each vendor's HL7 / SenML output and alarm behaviour before clinical use.
- **INA-CBG grouping:** grouping and the package rate come from E-Klaim. `BpjsClaimAsync` prepares and checks the claim data, but doesn't group.
- **`Medical.Devices` is a preview:** it stays a preview while IoTCom.Net is prerelease, and its API may change.
- **MAUI Android in CI:** the Android build is not run in CI (no MAUI workloads). Windows and Android are built locally, and iOS is not set up.
- **Demo sign-in:** see the security notes. OpenID Connect is not included.
- **Single database:** multi-tenancy is a shared schema in one database. A database-per-tenant option is not included.
- **Seeding:** the schema is created with `EnsureCreated`, not migrations. Add EF Core migrations before you evolve a production database.
