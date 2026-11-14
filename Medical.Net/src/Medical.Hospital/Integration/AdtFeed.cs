using System.Threading.Channels;
using Medical.Core;
using Medical.Hl7;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital.Integration;

/// <summary>Options of the <see cref="AdtFeed"/>.</summary>
public sealed record AdtFeedOptions
{
    /// <summary>Outbox destination written on every message (see <see cref="Hl7OutboxSenderOptions.Destinations"/>).</summary>
    public string Destination { get; init; } = "ADT";
    /// <summary>MSH-3/4 sender; MSH-4 defaults to the tenant code when <see cref="Hl7Endpoint.Facility"/> is empty.</summary>
    public Hl7Endpoint Sender { get; init; } = new("MEDICALNET-HIS", "");
    /// <summary>MSH-5/6 receiver.</summary>
    public Hl7Endpoint Receiver { get; init; } = new("ADT-RECEIVER", "RS");
    /// <summary>Send A04 for outpatient / emergency registrations (<see cref="VisitStarted"/>).</summary>
    public bool RegisterOutpatients { get; init; } = true;
}

/// <summary>
/// HL7 v2.5.1 ADT feed out of the HIS. Builds ADT^A01 (admission), A02 (transfer), A03 (discharge) and A04 (outpatient /
/// emergency registration) from the hospital database and stores them in the <see cref="Hl7OutboxMessage"/> outbox, from where
/// <see cref="Hl7OutboxSender"/> delivers them over MLLP. <see cref="Start"/> subscribes to <see cref="PatientAdmitted"/>,
/// <see cref="PatientDischarged"/> and <see cref="VisitStarted"/> on the event bus; transfers raise no event yet, so the app
/// calls <see cref="TransferAsync"/> after <c>ClinicalService.TransferAsync</c>.
/// </summary>
public sealed class AdtFeed(DbContextOptions<HospitalDbContext> options, IMedicalEventBus bus, AdtFeedOptions? settings = null, TimeProvider? clock = null)
    : IAsyncDisposable
{
    private readonly AdtFeedOptions _o = settings ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Channel<(HospitalEvent Event, TaskCompletionSource Done)> _queue = Channel.CreateUnbounded<(HospitalEvent, TaskCompletionSource)>(new() { SingleReader = true });
    private readonly List<IDisposable> _subscriptions = [];
    private Task _last = Task.CompletedTask;
    private Task? _worker;
    private CancellationTokenSource? _cts;

    /// <summary>Raised for every message put in the outbox.</summary>
    public event Action<Hl7OutboxMessage>? Enqueued;
    /// <summary>Raised when an event could not be turned into a message (the event is skipped).</summary>
    public event Action<HospitalEvent, Exception>? Failed;

    /// <summary>Subscribes to the hospital events and starts the background worker (events are handled one at a time, in order).</summary>
    public void Start()
    {
        if (_worker is not null) return;
        _cts = new CancellationTokenSource();
        _subscriptions.Add(bus.Subscribe<PatientAdmitted>(Post));
        _subscriptions.Add(bus.Subscribe<PatientDischarged>(Post));
        if (_o.RegisterOutpatients) _subscriptions.Add(bus.Subscribe<VisitStarted>(Post));
        _worker = Task.Run(() => WorkAsync(_cts.Token));
    }

    /// <summary>Completes when every event received so far has been handled.</summary>
    public Task WhenIdleAsync() => Volatile.Read(ref _last);

    private void Post(HospitalEvent e)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _last, done.Task);
        if (!_queue.Writer.TryWrite((e, done))) done.TrySetResult();
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (e, done) in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await using var db = new HospitalDbContext(options, new FixedTenantContext(e.TenantId, "adt-feed"));
                    var row = e switch
                    {
                        PatientAdmitted a => await AdmitAsync(db, a.VisitId, _o, _clock, ct),
                        PatientDischarged d => await DischargeAsync(db, d.VisitId, _o, _clock, ct),
                        VisitStarted v => await RegisterAsync(db, v.VisitId, _o, _clock, ct),
                        _ => null,
                    };
                    if (row is not null) Enqueued?.Invoke(row);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Failed?.Invoke(e, ex); }
                finally { done.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _queue.Writer.TryComplete();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _cts?.Dispose();
    }

    // ------------------------------------------------------------------ message construction (also usable without the bus)

    /// <summary>ADT^A01 for an inpatient stay created by <c>ClinicalService.AdmitAsync</c>.</summary>
    public static Task<Hl7OutboxMessage> AdmitAsync(HospitalDbContext db, string stayId, AdtFeedOptions? o = null, TimeProvider? clock = null, CancellationToken ct = default) =>
        BuildAsync(db, AdtEvent.Admit, stayId, null, null, o, clock, ct);

    /// <summary>ADT^A03 for a discharged stay (PV1-45 = visit end, PV1-36 from the discharge type).</summary>
    public static Task<Hl7OutboxMessage> DischargeAsync(HospitalDbContext db, string stayId, AdtFeedOptions? o = null, TimeProvider? clock = null, CancellationToken ct = default) =>
        BuildAsync(db, AdtEvent.Discharge, stayId, null, null, o, clock, ct);

    /// <summary>ADT^A04 for an outpatient / emergency visit; null for inpatient visits (those are announced by A01).</summary>
    public static async Task<Hl7OutboxMessage?> RegisterAsync(HospitalDbContext db, string visitId, AdtFeedOptions? o = null, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == visitId, ct) ?? throw new KeyNotFoundException($"Visit '{visitId}' not found.");
        return v.Kind == VisitKind.Inpatient ? null : await BuildAsync(db, AdtEvent.Register, visitId, null, null, o, clock, ct);
    }

    /// <summary>
    /// ADT^A02 for a bed transfer: PV1-6 = <paramref name="fromBed"/>, PV1-3 = <paramref name="toBed"/> (bed ids or bed codes, each as
    /// ward^^bed^facility). Call it after <c>ClinicalService.TransferAsync</c>, which raises no event.
    /// </summary>
    public static Task<Hl7OutboxMessage> TransferAsync(HospitalDbContext db, string visitId, string fromBed, string toBed, AdtFeedOptions? o = null,
        TimeProvider? clock = null, CancellationToken ct = default) =>
        BuildAsync(db, AdtEvent.Transfer, visitId, fromBed, toBed, o, clock, ct);

    private static async Task<Hl7OutboxMessage> BuildAsync(HospitalDbContext db, AdtEvent ev, string visitId, string? fromBed, string? toBed,
        AdtFeedOptions? o, TimeProvider? clock, CancellationToken ct)
    {
        o ??= new();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == visitId, ct) ?? throw new KeyNotFoundException($"Visit '{visitId}' not found.");
        var p = await db.Patients.FirstOrDefaultAsync(x => x.Id == v.PatientId, ct) ?? throw new KeyNotFoundException($"Patient '{v.PatientId}' not found.");
        string tenantId = db.Tenant.TenantId;
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        string facility = tenant?.Code ?? tenantId;
        var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == v.DepartmentId, ct);
        var doctor = v.DoctorId is null ? null : await db.Staff.FirstOrDefaultAsync(s => s.Id == v.DoctorId, ct);
        var parent = v.ParentVisitId is null ? null : await db.Visits.FirstOrDefaultAsync(x => x.Id == v.ParentVisitId, ct);
        var dx = (await db.Diagnoses.Where(d => d.VisitId == v.Id).ToListAsync(ct)).OrderByDescending(d => d.Primary).FirstOrDefault();

        async Task<Hl7Location?> BedLocation(string? bedRef)
        {
            if (bedRef is null) return null;
            var bed = await db.Beds.FirstOrDefaultAsync(b => b.Id == bedRef || b.Code == bedRef, ct) ?? throw new KeyNotFoundException($"Bed '{bedRef}' not found.");
            var ward = await db.Departments.FirstOrDefaultAsync(d => d.Id == bed.WardId, ct);
            return new Hl7Location(ward?.Code ?? bed.WardId, null, bed.Code, facility);
        }

        var location = ev == AdtEvent.Transfer ? await BedLocation(toBed) : v.BedId is not null ? await BedLocation(v.BedId) : dept is null ? null : new Hl7Location(dept.Code, null, null, facility);
        var details = new AdtDetails
        {
            Location = location,
            PriorLocation = ev == AdtEvent.Transfer ? await BedLocation(fromBed) : null,
            // Table 0007 admission type: E emergency, R routine.
            AdmissionType = v.Kind == VisitKind.Emergency || parent?.Kind == VisitKind.Emergency ? "E" : "R",
            AttendingDoctorId = doctor?.Licence ?? doctor?.UserName,
            AttendingDoctorName = doctor?.Name,
            HospitalService = dept?.Code,
            // Table 0023 admit source: 7 emergency room, 1 physician referral.
            AdmitSource = ev == AdtEvent.Admit ? parent?.Kind == VisitKind.Emergency ? "7" : "1" : null,
            FinancialClass = v.Payer switch { PayerType.Bpjs => "BPJS", PayerType.Insurance => "ASURANSI", _ => "UMUM" },
            DischargeDisposition = ev == AdtEvent.Discharge ? Disposition(v.Discharge) : null,
            AdmitReason = dx is null ? null : new ClinicalCode(CodeSystems.Icd10, dx.Icd10, dx.Display),
            EventOccurred = now,
        };
        static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
        var encounter = new Encounter
        {
            Id = v.Id, PatientId = p.Id, Start = Utc(v.Start), End = v.End is { } end ? Utc(end) : null, Practitioner = doctor?.Licence ?? doctor?.UserName,
            Class = v.Kind switch { VisitKind.Inpatient => EncounterClass.Inpatient, VisitKind.Emergency => EncounterClass.Emergency, _ => EncounterClass.Ambulatory },
            Location = dept?.Code,
        };
        var sender = o.Sender.Facility.Length == 0 ? o.Sender with { Facility = facility } : o.Sender;
        string controlId = $"{now:yyMMddHHmmss}{Guid.NewGuid():N}"[..20];
        var msg = Hl7V2.BuildAdt(ev, HisRecords.ToDomain(p), encounter, sender, o.Receiver, details, controlId, now);
        var row = Hl7OutboxMessage.Enqueue(db, Hl7V2.Encode(msg), o.Destination, now.UtcDateTime, v.Id);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>PV1-36 discharge disposition, HL7 user table 0112 (UB-04 patient status codes).</summary>
    public static string Disposition(DischargeType? type) => type switch
    {
        DischargeType.Home => "01",
        DischargeType.Referred or DischargeType.Transferred => "02",
        DischargeType.AgainstAdvice => "07",
        DischargeType.Died => "20",
        _ => "01",
    };
}
