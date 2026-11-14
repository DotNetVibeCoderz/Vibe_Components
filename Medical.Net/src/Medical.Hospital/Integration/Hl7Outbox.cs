using Medical.Hl7;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital.Integration;

/// <summary>Delivery state of an outgoing HL7 message.</summary>
public enum OutboxStatus
{
    /// <summary>Waiting for (re)delivery at <see cref="Hl7OutboxMessage.NextAttemptAt"/>.</summary>
    Pending,
    /// <summary>Accepted by the receiver (MSA-1 AA or CA).</summary>
    Sent,
    /// <summary>Gave up after the maximum number of attempts; needs an operator.</summary>
    Failed,
}

/// <summary>
/// An outgoing HL7 v2 message (transactional outbox): written in the same database as the clinical change, delivered later by
/// <see cref="Hl7OutboxSender"/> in creation order per destination, so a receiver outage never loses or reorders ADT events.
/// </summary>
public sealed class Hl7OutboxMessage : TenantEntity
{
    /// <summary>MSH-9 as "ADT^A01".</summary>
    public string MessageType { get; set; } = "";
    /// <summary>MSH-10 message control id (echoed by the receiver in MSA-2).</summary>
    public string ControlId { get; set; } = "";
    /// <summary>The ER7 ("pipe") message.</summary>
    public string Payload { get; set; } = "";
    /// <summary>Logical destination name (resolved by <see cref="Hl7OutboxSenderOptions.Destinations"/>) or "host:port".</summary>
    public string Destination { get; set; } = "";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    /// <summary>Earliest time of the next delivery attempt (exponential backoff after a failure).</summary>
    public DateTime NextAttemptAt { get; set; }
    /// <summary>The visit the message is about (traceability), when known.</summary>
    public string? VisitId { get; set; }

    /// <summary>Adds an encoded message to the outbox of the context's tenant (saved with the caller's <c>SaveChanges</c>).</summary>
    public static Hl7OutboxMessage Enqueue(HospitalDbContext db, string er7, string destination, DateTime now, string? visitId = null)
    {
        var m = Hl7V2.Parse(er7);
        var row = new Hl7OutboxMessage
        {
            MessageType = m.Type, ControlId = m.ControlId, Payload = m.Raw, Destination = destination, CreatedAt = now, NextAttemptAt = now, VisitId = visitId,
        };
        db.Set<Hl7OutboxMessage>().Add(row);
        return row;
    }
}

/// <summary>Options of <see cref="Hl7OutboxSender"/>.</summary>
public sealed record Hl7OutboxSenderOptions
{
    /// <summary>Logical destination name → MLLP endpoint. Destinations not listed are parsed as "host:port".</summary>
    public IReadOnlyDictionary<string, (string Host, int Port)> Destinations { get; init; } = new Dictionary<string, (string, int)>();
    /// <summary>Attempts before a message is marked <see cref="OutboxStatus.Failed"/>.</summary>
    public int MaxAttempts { get; init; } = 10;
    /// <summary>Delay after the first failure; doubles per attempt up to <see cref="MaxDelay"/>.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long to wait for the ACK of one message.</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Messages per tenant and pass.</summary>
    public int BatchSize { get; init; } = 100;
    /// <summary>Pause between passes in <see cref="Hl7OutboxSender.RunAsync"/>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Result of one delivery pass.</summary>
public sealed record OutboxPassResult(int Sent, int Retried, int Failed);

/// <summary>
/// Delivers <see cref="Hl7OutboxMessage"/>s over MLLP (HL7 v2 Lower Layer Protocol) with <see cref="MllpClient"/>. Original-mode
/// acknowledgement: AA / CA (MSA-1, HL7 v2.5.1 §2.9.2, table 0008) with MSA-2 equal to the control id marks a message sent; AE / AR / CE / CR,
/// a mismatched ACK, a timeout or a connection error schedules a retry with exponential backoff. Messages to one destination are sent
/// in creation order and a failure stops that destination for the pass, so later events never overtake earlier ones.
/// </summary>
public sealed class Hl7OutboxSender(DbContextOptions<HospitalDbContext> options, Hl7OutboxSenderOptions? settings = null, TimeProvider? clock = null)
{
    private readonly Hl7OutboxSenderOptions _o = settings ?? new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Raised after every attempt (message, accepted?, error text).</summary>
    public event Action<Hl7OutboxMessage, bool, string?>? Attempted;

    /// <summary>Backoff before attempt <paramref name="attempts"/> + 1: BaseDelay × 2^(attempts − 1), capped at MaxDelay.</summary>
    public TimeSpan Backoff(int attempts) =>
        TimeSpan.FromTicks(Math.Min(_o.MaxDelay.Ticks, _o.BaseDelay.Ticks * (1L << Math.Clamp(attempts - 1, 0, 30))));

    /// <summary>One pass over every tenant's due messages.</summary>
    public async Task<OutboxPassResult> SendPendingAsync(CancellationToken ct = default)
    {
        List<string> tenants;
        await using (var root = new HospitalDbContext(options, new FixedTenantContext("")))
            tenants = await root.Tenants.Select(t => t.Id).ToListAsync(ct);
        int sent = 0, retried = 0, failed = 0;
        foreach (var tenant in tenants)
        {
            await using var db = new HospitalDbContext(options, new FixedTenantContext(tenant, "hl7-outbox"));
            var now = _clock.GetUtcNow().UtcDateTime;
            var due = (await db.Set<Hl7OutboxMessage>().Where(m => m.Status == OutboxStatus.Pending).ToListAsync(ct))
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.ControlId, StringComparer.Ordinal).Take(_o.BatchSize).ToList();
            foreach (var group in due.GroupBy(m => m.Destination))
            {
                MllpClient? client = null;
                try
                {
                    foreach (var m in group)
                    {
                        if (m.NextAttemptAt > now) break;   // keep order: an earlier message is still backing off
                        string? error;
                        try
                        {
                            client ??= Connect(m.Destination);
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeout.CancelAfter(_o.AckTimeout);
                            error = CheckAck(m, await client.SendAsync(m.Payload, timeout.Token));
                        }
                        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or InvalidOperationException or FormatException
                                                      or NHapi.Base.HL7Exception && !ct.IsCancellationRequested)
                        {
                            error = $"{e.GetType().Name}: {e.Message}";
                            if (client is not null) { await client.DisposeAsync(); client = null; }
                        }
                        m.Attempts++;
                        if (error is null)
                        {
                            (m.Status, m.SentAt, m.LastError) = (OutboxStatus.Sent, _clock.GetUtcNow().UtcDateTime, null);
                            sent++;
                        }
                        else
                        {
                            m.LastError = error.Length > 500 ? error[..500] : error;
                            if (m.Attempts >= _o.MaxAttempts) { m.Status = OutboxStatus.Failed; failed++; }
                            else { m.NextAttemptAt = _clock.GetUtcNow().UtcDateTime + Backoff(m.Attempts); retried++; }
                        }
                        await db.SaveChangesAsync(ct);
                        Attempted?.Invoke(m, error is null, error);
                        if (error is not null && m.Status == OutboxStatus.Pending) break;   // retry this one first
                    }
                }
                finally
                {
                    if (client is not null) await client.DisposeAsync();
                }
            }
        }
        return new(sent, retried, failed);
    }

    /// <summary>Runs passes until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SendPendingAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (DbUpdateException) { }   // another sender instance touched the row; next pass re-reads
            try { await Task.Delay(_o.PollInterval, _clock, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private MllpClient Connect(string destination)
    {
        if (_o.Destinations.TryGetValue(destination, out var ep)) return new MllpClient(ep.Host, ep.Port);
        int colon = destination.LastIndexOf(':');
        if (colon > 0 && int.TryParse(destination[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port))
            return new MllpClient(destination[..colon], port);
        throw new InvalidOperationException($"Unknown HL7 destination '{destination}'.");
    }

    /// <summary>Null when the ACK accepts the message, otherwise the reason.</summary>
    internal static string? CheckAck(Hl7OutboxMessage m, string ackEr7)
    {
        var ack = Hl7V2.Parse(ackEr7);
        string code = ack.Get("MSA-1"), id = ack.Get("MSA-2");
        if (id != m.ControlId) return $"ACK for '{id}', expected '{m.ControlId}'.";
        if (code is "AA" or "CA") return null;
        string text = ack.Get("MSA-3");
        if (text.Length == 0) text = ack.Get("ERR-8");
        return $"{(code.Length == 0 ? "no MSA-1" : code)}{(text.Length > 0 ? ": " + text : "")}";
    }
}
