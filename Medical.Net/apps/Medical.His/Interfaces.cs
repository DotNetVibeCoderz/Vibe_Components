using FellowOakDicom;
using FellowOakDicom.Network;
using Medical.Core;
using Medical.Dicom;
using Medical.Hl7;
using Medical.Hospital;
using Medical.Hospital.Integration;
using Microsoft.EntityFrameworkCore;

namespace Medical.His;

/// <summary>
/// The HIS radiology worklist as a DICOM Modality Worklist source. The tenant is chosen by the called AE title
/// (<c>His:Worklist:{AeTitle}={tenantId}</c>); the default AE <see cref="ModalityWorklistServer.DefaultAeTitle"/> serves
/// <c>His:Worklist:DefaultTenant</c> or the first demo tenant. Orders without a Study Instance UID get one (<see cref="DicomUID.Generate()"/>),
/// persisted on the <see cref="RadiologyOrder"/>, so the images the modality acquires link back to the order.
/// Optional: <c>His:Worklist:Stations:{Modality}={StationAeTitle}</c> and <c>His:Worklist:TimeZone</c> (DICOM dates are local time; default: server time zone).
/// </summary>
public sealed class HisWorklistSource(DbContextOptions<HospitalDbContext> options, IConfiguration config, ILogger<HisWorklistSource>? log = null) : IWorklistSource
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "Port", "TimeZone", "DefaultTenant" };

    private TimeZoneInfo Zone => config["His:Worklist:TimeZone"] is { Length: > 0 } id ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Local;

    /// <summary>The tenant whose worklist the called AE title publishes; null when the AE title is not configured.</summary>
    public async Task<string?> ResolveTenantAsync(string? calledAe, CancellationToken ct = default)
    {
        var map = config.GetSection("His:Worklist").GetChildren().Where(c => c.Value is { Length: > 0 } && !Reserved.Contains(c.Key))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);
        string ae = (calledAe ?? "").Trim();
        if (ae.Length > 0 && map.TryGetValue(ae, out var mapped)) return mapped;
        // The default AE always serves the default tenant; any other AE does so only while no AE titles are mapped explicitly,
        // so a multi-hospital deployment never shows one tenant's worklist under an unknown AE.
        if (ae.Length > 0 && !ae.Equals(ModalityWorklistServer.DefaultAeTitle, StringComparison.OrdinalIgnoreCase) && map.Count > 0) return null;
        if (config["His:Worklist:DefaultTenant"] is { Length: > 0 } configured) return configured;
        await using var root = new HospitalDbContext(options, new FixedTenantContext(""));
        var tenants = await root.Tenants.Where(t => t.Active).Select(t => t.Id).ToListAsync(ct);
        return HospitalSeeder.DemoTenants.Select(t => t.Id).FirstOrDefault(tenants.Contains) ?? tenants.Order(StringComparer.Ordinal).FirstOrDefault();
    }

    public async Task<IReadOnlyList<WorklistItem>> QueryAsync(WorklistQuery q, CancellationToken ct)
    {
        string? tenant = await ResolveTenantAsync(q.CalledAe, ct);
        if (tenant is null)
        {
            log?.LogWarning("Worklist query for unknown AE title {Ae} from {Calling}", q.CalledAe, q.CallingAe);
            return [];
        }
        await using var db = new HospitalDbContext(options, new FixedTenantContext(tenant, $"mwl:{q.CallingAe}", StaffRole.Radiographer));
        var rad = new RadiologyService(db);
        bool exact = q.Modality is { } m && !m.Contains('*') && !m.Contains('?');
        var modalities = exact ? [q.Modality!]
            : await db.RadiologyOrders.Where(o => o.State == OrderState.Ordered || o.State == OrderState.Scheduled).Select(o => o.Modality).Distinct().ToListAsync(ct);
        var rows = new List<(RadiologyOrder Order, HisPatient Patient)>();
        foreach (var modality in modalities) rows.AddRange(await rad.WorklistAsync(modality, ct));
        if (rows.Count == 0) return [];

        var visitIds = rows.Select(r => r.Order.VisitId).Distinct().ToList();
        var doctorOf = await db.Visits.Where(v => visitIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.DoctorId, ct);
        var staff = await db.Staff.ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var zone = Zone;
        var items = new List<WorklistItem>();
        bool assigned = false;
        foreach (var (o, p) in rows)
        {
            var item = ToItem(o, p, doctorOf.GetValueOrDefault(o.VisitId) is { } d ? staff.GetValueOrDefault(d) : null, zone);
            if (!q.Matches(item)) continue;
            if (o.StudyInstanceUid is null)
            {
                o.StudyInstanceUid = DicomUID.Generate().UID;
                assigned = true;
                item = item with { StudyInstanceUid = o.StudyInstanceUid };
            }
            items.Add(item);
        }
        if (assigned) await db.SaveChangesAsync(ct);
        return items;
    }

    private WorklistItem ToItem(RadiologyOrder o, HisPatient p, string? referring, TimeZoneInfo zone) => new()
    {
        PatientName = $"{p.Family}^{p.Given}", PatientId = p.Mrn, PatientBirthDate = p.BirthDate,
        PatientSex = p.Gender switch { Gender.Male => "M", Gender.Female => "F", Gender.Other => "O", _ => null },
        AccessionNumber = o.AccessionNumber, RequestedProcedureId = o.AccessionNumber, RequestedProcedureDescription = o.Procedure,
        RequestedProcedureCode = new(o.ProcedureCode, "99MEDNET", o.Procedure),
        // Requested Procedure Priority (0040,1003): STAT, HIGH, ROUTINE.
        RequestedProcedurePriority = o.Priority switch { OrderPriority.Stat => "STAT", OrderPriority.Urgent => "HIGH", _ => "ROUTINE" },
        Modality = o.Modality, ScheduledStationAeTitle = config[$"His:Worklist:Stations:{o.Modality}"],
        ScheduledStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(o.ScheduledAt ?? o.OrderedAt, DateTimeKind.Utc), zone),
        ScheduledProcedureStepId = o.AccessionNumber, ScheduledProcedureStepDescription = o.Procedure,
        ReferringPhysician = referring, StudyInstanceUid = o.StudyInstanceUid,
    };
}

/// <summary>Runs the Modality Worklist SCP on <c>His:Worklist:Port</c> (not started when the port is not configured).</summary>
public sealed class HisWorklistService(HisWorklistSource source, IConfiguration config, ILogger<HisWorklistService> log) : IHostedService, IDisposable
{
    private IDicomServer? _server;

    public Task StartAsync(CancellationToken ct)
    {
        if (config.GetValue<int?>("His:Worklist:Port") is not { } port || port <= 0) return Task.CompletedTask;
        _server = ModalityWorklistServer.Start(port, source);
        log.LogInformation("DICOM Modality Worklist SCP listening on port {Port} (AE {Ae})", port, ModalityWorklistServer.DefaultAeTitle);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _server?.Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => _server?.Dispose();
}

/// <summary>
/// The HL7 ADT feed: <see cref="AdtFeed"/> turns admissions, discharges and registrations on the <see cref="IMedicalEventBus"/> into outbox
/// messages, and <see cref="Hl7OutboxSender"/> delivers them over MLLP to <c>His:Adt:Host</c>:<c>His:Adt:Port</c>
/// (disabled when either is missing). Optional: <c>His:Adt:ReceivingApplication</c>, <c>His:Adt:ReceivingFacility</c>.
/// </summary>
public sealed class HisAdtService(IServiceProvider services, DbContextOptions<HospitalDbContext> options, AdtFeedOptions feedOptions,
    Hl7OutboxSenderOptions senderOptions, IConfiguration config, ILogger<HisAdtService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!HisInterfaces.AdtConfigured(config)) return;
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var sender = new Hl7OutboxSender(options, senderOptions, clock);
        sender.Attempted += (m, ok, error) =>
        {
            if (!ok) log.LogWarning("HL7 {Type} {ControlId} not delivered (attempt {Attempt}): {Error}", m.MessageType, m.ControlId, m.Attempts, error);
        };
        AdtFeed? feed = null;
        if (services.GetService<IMedicalEventBus>() is { } bus)
        {
            feed = new AdtFeed(options, bus, feedOptions, clock);
            feed.Failed += (e, ex) => log.LogError(ex, "ADT message for {Event} could not be built", e.GetType().Name);
            feed.Start();
        }
        else log.LogWarning("No IMedicalEventBus registered: the ADT feed only delivers messages put in the outbox explicitly (e.g. transfers).");
        log.LogInformation("HL7 ADT feed → {Host}:{Port}", config["His:Adt:Host"], config["His:Adt:Port"]);
        try { await sender.RunAsync(stoppingToken); }
        finally { if (feed is not null) await feed.DisposeAsync(); }
    }
}

public static class HisInterfaces
{
    internal static bool AdtConfigured(IConfiguration c) => c["His:Adt:Host"] is { Length: > 0 } && c.GetValue<int?>("His:Adt:Port") is > 0;

    /// <summary>
    /// Registers the HIS interfaces: the DICOM Modality Worklist SCP (<c>His:Worklist:Port</c>) and the HL7 ADT feed (<c>His:Adt:Host</c>/<c>Port</c>).
    /// Needs the <see cref="DbContextOptions{HospitalDbContext}"/> singleton; the ADT feed also uses an <see cref="IMedicalEventBus"/> singleton when present.
    /// <see cref="AdtFeedOptions"/> is registered so pages can call <see cref="AdtFeed.TransferAsync"/> after a bed transfer.
    /// </summary>
    public static IServiceCollection AddHisInterfaces(this IServiceCollection s, IConfiguration c)
    {
        s.AddSingleton<HisWorklistSource>();
        s.AddHostedService<HisWorklistService>();
        s.AddSingleton(new AdtFeedOptions
        {
            Destination = "ADT",
            Receiver = new Hl7Endpoint(c["His:Adt:ReceivingApplication"] ?? "ADT-RECEIVER", c["His:Adt:ReceivingFacility"] ?? "RS"),
        });
        var destinations = new Dictionary<string, (string Host, int Port)>();
        if (AdtConfigured(c)) destinations["ADT"] = (c["His:Adt:Host"]!, c.GetValue<int>("His:Adt:Port"));
        s.AddSingleton(new Hl7OutboxSenderOptions { Destinations = destinations });
        s.AddHostedService<HisAdtService>();
        return s;
    }
}
