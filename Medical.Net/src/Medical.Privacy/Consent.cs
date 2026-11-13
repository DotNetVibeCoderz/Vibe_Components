using System.Collections.Concurrent;
using Medical.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Privacy;

/// <summary>Purposes of processing that a patient can consent to (UU PDP No. 27/2022, GDPR Art. 6/9 style).</summary>
public enum ConsentPurpose { Treatment, SatuSehatExchange, Research, Teleconsultation, Marketing, AiTraining }

public sealed record ConsentRecord(string PatientId, ConsentPurpose Purpose, bool Granted, DateTimeOffset RecordedAt, DateTimeOffset? Expires = null, string? Witness = null)
{
    public bool IsActive(DateTimeOffset at) => Granted && (Expires is null || at < Expires);
}

/// <summary>Latest-decision-wins consent registry. Every check is written to the audit log.</summary>
public sealed class ConsentRegistry(IAuditLog? audit = null, TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<(string, ConsentPurpose), ConsentRecord> _latest = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Record(ConsentRecord c)
    {
        _latest.AddOrUpdate((c.PatientId, c.Purpose), c, (_, old) => c.RecordedAt >= old.RecordedAt ? c : old);
        audit?.Record(new AuditEvent(_clock.GetUtcNow(), c.Witness ?? "system", AuditAction.Update, $"Consent/{c.Purpose}", c.PatientId, c.Granted ? "granted" : "withdrawn"));
    }

    /// <summary>True if the patient has an active consent for <paramref name="purpose"/>. Treatment defaults to allowed (implied consent).</summary>
    public bool IsPermitted(string patientId, ConsentPurpose purpose, string user = "system")
    {
        var now = _clock.GetUtcNow();
        bool ok = _latest.TryGetValue((patientId, purpose), out var c) ? c.IsActive(now) : purpose == ConsentPurpose.Treatment;
        audit?.Record(new AuditEvent(now, user, AuditAction.Query, $"Consent/{purpose}", patientId, "consent-check", ok));
        return ok;
    }

    public IReadOnlyList<ConsentRecord> For(string patientId) => _latest.Values.Where(c => c.PatientId == patientId).OrderBy(c => c.Purpose).ToArray();
}

public static class PrivacyBuilderExtensions
{
    /// <summary>Registers <see cref="ConsentRegistry"/> and a <see cref="TextDeidentifier"/> keyed with <paramref name="pseudonymSecret"/>.</summary>
    public static MedicalBuilder AddPrivacy(this MedicalBuilder builder, string? pseudonymSecret = null) =>
        builder.TryAddModule("privacy", s =>
        {
            s.TryAddSingleton(sp => new ConsentRegistry(sp.GetService<IAuditLog>(), sp.GetService<TimeProvider>()));
            s.TryAddSingleton(new TextDeidentifier(pseudonymSecret));
        });
}
