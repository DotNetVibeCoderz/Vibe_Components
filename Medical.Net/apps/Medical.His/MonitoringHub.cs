using System.Collections.Concurrent;
using IoTCom.Net.Protocols.Hl7;
using Medical.Core;
using Medical.Devices;
using Medical.Hospital;
using Medical.Hospital.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Medical.His;

/// <summary>
/// Live bedside monitoring for every tenant. One MLLP feed receives ORU^R01 from all monitors (here: IoTCom.Net
/// simulators, one per ICU bed, or real monitors on <c>His:MllpPort</c>); device ids are bound to beds and their current
/// patients; each tenant has its own <see cref="MonitoringEngine"/>. Every 15 minutes a complete set is charted to the
/// record as "pending validation", and confirmed NEWS2 alerts are kept for the alert log.
/// </summary>
public sealed class MonitoringHub(DbContextOptions<HospitalDbContext> options, IConfiguration config, ILogger<MonitoringHub> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, MonitoringEngine> _engines = new();
    private readonly ConcurrentDictionary<string, string> _deviceTenant = new();
    private readonly ConcurrentQueue<(string Tenant, EarlyWarningAlert Alert)> _alerts = new();
    private readonly ConcurrentDictionary<string, InfusionPumpStatus> _pumps = new();
    private DateTimeOffset _simNow = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<string, bool> _occupied = new();
    // (tenant, patient) → bound monitor, and the last nurse assessment per (tenant, patient) so a rebind / transfer keeps it.
    private readonly ConcurrentDictionary<(string Tenant, string PatientId), string> _patientDevice = new();
    private readonly ConcurrentDictionary<(string Tenant, string DeviceId), string> _devicePatient = new();
    private readonly ConcurrentDictionary<(string Tenant, string PatientId), ManualAssessment> _assessments = new();

    /// <summary>Nurse-entered NEWS2 parameters the monitor cannot measure (ACVPU consciousness, supplemental oxygen).</summary>
    public sealed record ManualAssessment(Consciousness Consciousness, bool OnOxygen, DateTimeOffset At);

    public event Action<string, BedSnapshot>? Updated;
    public event Action<string, EarlyWarningAlert>? Alerted;

    public MonitoringEngine Engine(string tenant) => _engines.GetOrAdd(tenant, t =>
    {
        var e = new MonitoringEngine();
        e.Updated += s => Updated?.Invoke(t, s);
        e.Alert += a => { _alerts.Enqueue((t, a)); while (_alerts.Count > 200) _alerts.TryDequeue(out _); Alerted?.Invoke(t, a); };
        e.ChartDue += s => _ = ChartAsync(t, s);
        return e;
    });

    public IReadOnlyList<EarlyWarningAlert> Alerts(string tenant) => _alerts.Where(a => a.Tenant == tenant).Select(a => a.Alert).Reverse().Take(30).ToList();

    public IReadOnlyList<InfusionPumpStatus> Pumps(string tenant) => _pumps.Values.Where(p => _deviceTenant.GetValueOrDefault(p.DeviceId) == tenant).OrderBy(p => p.DeviceId).ToList();

    /// <summary>
    /// Applies a nurse assessment (ACVPU, supplemental oxygen) to the monitor bound to the patient, so live NEWS2 stops
    /// assuming Alert on room air. The assessment is remembered per patient and re-applied when the patient is bound to a
    /// monitor later (admission to a monitored bed, transfer). Returns the device it was applied to, or null if the patient
    /// has no monitor right now.
    /// </summary>
    public string? ApplyNurseAssessment(string tenant, string patientId, Consciousness c, bool onOxygen)
    {
        _assessments[(tenant, patientId)] = new ManualAssessment(c, onOxygen, DateTimeOffset.UtcNow);
        if (!_patientDevice.TryGetValue((tenant, patientId), out var device)) return null;
        Engine(tenant).SetManual(device, c, onOxygen);
        return device;
    }

    /// <summary>The nurse assessment in force for the patient currently on this monitor (null = none; NEWS2 assumes Alert, room air).</summary>
    public ManualAssessment? NurseAssessment(string tenant, string deviceId) =>
        _devicePatient.TryGetValue((tenant, deviceId), out var pid) ? _assessments.GetValueOrDefault((tenant, pid)) : null;

    /// <summary>Re-reads bed assignments (after admissions, transfers and discharges).</summary>
    public async Task RebindAsync(string tenant, CancellationToken ct = default)
    {
        await using var db = new HospitalDbContext(options, new FixedTenantContext(tenant, "monitoring"));
        var beds = await db.Beds.Where(b => b.MonitorId != null).ToListAsync(ct);
        var pids = beds.Where(b => b.PatientId != null).Select(b => b.PatientId!).ToList();
        var names = await db.Patients.Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Given + " " + p.Family, ct);
        var engine = Engine(tenant);
        foreach (var key in _patientDevice.Keys.Where(k => k.Tenant == tenant).ToList()) _patientDevice.TryRemove(key, out _);
        foreach (var b in beds)
        {
            string device = b.MonitorId!;
            if (b.PatientId is not null) _occupied[device] = true; else _occupied.TryRemove(device, out _);
            _deviceTenant[device] = tenant;
            string? previous = _devicePatient.GetValueOrDefault((tenant, device));
            engine.Bind(device, b.Code, b.PatientId, b.PatientId is { } pid ? names.GetValueOrDefault(pid) : null, b.VisitId);
            if (b.PatientId is { } patient)
            {
                _patientDevice[(tenant, patient)] = device;
                _devicePatient[(tenant, device)] = patient;
                // A new patient on this monitor (Bind cleared its state): carry over the nurse's last assessment.
                if (previous != patient && _assessments.TryGetValue((tenant, patient), out var a)) engine.SetManual(device, a.Consciousness, a.OnOxygen);
            }
            else _devicePatient.TryRemove((tenant, device), out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var tenants = HospitalSeeder.DemoTenants.Select(t => t.Id).ToList();
        foreach (var t in tenants) await RebindAsync(t, stop);
        await using var feed = Hl7MonitorFeed.InProcess("his-monitors");
        var feeds = new List<IDeviceFeed> { feed };
        Hl7MonitorFeed? tcp = null;
        if (config.GetValue<int?>("His:MllpPort") is { } port) { tcp = Hl7MonitorFeed.Tcp(port); feeds.Add(tcp); }
        var gatewayTasks = feeds.Select(f => Task.Run(async () =>
        {
            await foreach (var r in f.ReadAllAsync(stop))
                if (_deviceTenant.TryGetValue(r.DeviceId, out var t)) Engine(t).Ingest(r);
        }, stop)).ToList();

        // Simulated ICU monitors: one per ICU bed with a monitor id (sepsis, stable, hypoxia, hypertension).
        if (config.GetValue("His:SimulateDevices", true))
        {
            PatientScenario[] scenarios = [PatientScenario.Sepsis, PatientScenario.Stable, PatientScenario.Hypoxia, PatientScenario.Hypertension];
            // Only monitors at occupied beds are switched on; scenarios follow the bed number.
            var monitors = _deviceTenant.Keys.Where(_occupied.ContainsKey).Order()
                .Select(d => (d, d[(d.LastIndexOf('-') + 1)..], scenarios[(int.Parse(d[(d.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture) - 1) % scenarios.Length])).ToList();
            await using var sim = await BedsideSimulation.StartAsync(feed, monitors, 11, stop);
            var step = TimeSpan.FromSeconds(config.GetValue("His:SimulatedSecondsPerTick", 60));
            var tick = TimeSpan.FromSeconds(config.GetValue("His:TickSeconds", 2.0));
            // Warm-up: an hour of history so trends and NEWS2 are populated at start.
            for (int i = 0; i < 60 && !stop.IsCancellationRequested; i++) { _simNow += step; await sim.TickAsync(step, _simNow, stop); }
            var pumps = PumpDemo();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _simNow += step;
                    await sim.TickAsync(step, _simNow, stop);
                    foreach (var p in pumps) _pumps[p.DeviceId] = p.Advance(step, _simNow);
                    await Task.Delay(tick, stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { log.LogWarning(e, "Monitor simulation tick failed"); await Task.Delay(tick, stop); }
            }
        }
        else await Task.Delay(Timeout.Infinite, stop).ContinueWith(_ => { });
        if (tcp is not null) await tcp.DisposeAsync();
    }

    private List<PumpModel> PumpDemo()
    {
        var list = new List<PumpModel>();
        foreach (var (device, tenant) in _deviceTenant.Where(kv => _occupied.ContainsKey(kv.Key) && (kv.Key.EndsWith("-01") || kv.Key.EndsWith("-03"))).ToList())
        {
            string pumpId = device.Replace("MON", "PUMP");
            _deviceTenant[pumpId] = tenant;
            list.Add(device.EndsWith("-01") ? new PumpModel(pumpId, "Norepinephrine 4 mg/50 mL", 6, 50, 38) : new PumpModel(pumpId, "NaCl 0.9% 500 mL", 125, 500, 380));
        }
        return list;
    }

    private sealed class PumpModel(string id, string drug, double rate, double vtbi, double infused)
    {
        public string DeviceId => id;
        public InfusionPumpStatus Advance(TimeSpan dt, DateTimeOffset now)
        {
            infused = Math.Min(vtbi, infused + rate * dt.TotalHours);
            if (infused >= vtbi) infused = 0;   // the nurse hangs a new bag
            double left = vtbi - infused;
            return new InfusionPumpStatus(id, drug, rate, Math.Round(infused, 1), vtbi, left < rate / 6 ? "NEAR_EMPTY" : null, now);
        }
    }

    private async Task ChartAsync(string tenant, BedSnapshot s)
    {
        if (s.PatientId is null || s.Vitals is null) return;
        try
        {
            await using var db = new HospitalDbContext(options, new FixedTenantContext(tenant, "monitor"));
            await new ClinicalService(db).RecordVitalsAsync(s.PatientId, s.VisitId, s.Vitals with { Timestamp = DateTimeOffset.UtcNow }, VitalsSource.Device, s.DeviceId);
        }
        catch (Exception e) { log.LogWarning(e, "Charting device vitals failed"); }
    }
}
