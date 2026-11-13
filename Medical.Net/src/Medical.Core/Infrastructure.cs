using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.Core;

/// <summary>Fluent builder returned by <see cref="MedicalServiceCollectionExtensions.AddMedical"/>; each module adds an <c>AddX()</c> extension.</summary>
public sealed class MedicalBuilder(IServiceCollection services)
{
    private readonly HashSet<string> _modules = [];

    public IServiceCollection Services { get; } = services;

    public IReadOnlyCollection<string> Modules => _modules;

    /// <summary>Runs <paramref name="register"/> once per module name (idempotent module registration).</summary>
    public MedicalBuilder TryAddModule(string name, Action<IServiceCollection> register)
    {
        if (_modules.Add(name)) register(Services);
        return this;
    }
}

public static class MedicalServiceCollectionExtensions
{
    /// <summary>Registers Medical.Net core services (event bus, audit log, clock) and lets modules plug in.</summary>
    public static MedicalBuilder AddMedical(this IServiceCollection services, Action<MedicalBuilder>? configure = null)
    {
        services.TryAddSingleton<IMedicalEventBus, InMemoryMedicalEventBus>();
        services.TryAddSingleton<IAuditLog, InMemoryAuditLog>();
        services.TryAddSingleton(TimeProvider.System);
        var builder = new MedicalBuilder(services);
        configure?.Invoke(builder);
        return builder;
    }
}

/// <summary>In-process publish/subscribe used to decouple modules (e.g. HL7 listener → rules → alerts).</summary>
public interface IMedicalEventBus
{
    IDisposable Subscribe<T>(Action<T> handler);
    void Publish<T>(T message);
}

public sealed class InMemoryMedicalEventBus : IMedicalEventBus
{
    private readonly ConcurrentDictionary<Type, ImmutableHandlers> _handlers = new();

    private sealed record ImmutableHandlers(Delegate[] Items);

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        _handlers.AddOrUpdate(typeof(T), _ => new([handler]), (_, h) => new([.. h.Items, handler]));
        return new Unsubscriber(() => _handlers.AddOrUpdate(typeof(T), _ => new([]), (_, h) => new(h.Items.Where(d => d != (Delegate)handler).ToArray())));
    }

    public void Publish<T>(T message)
    {
        if (!_handlers.TryGetValue(typeof(T), out var h)) return;
        foreach (var d in h.Items) ((Action<T>)d)(message);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
    }
}

public enum AuditAction { Create, Read, Update, Delete, Export, Login, Query, Deidentify, Alert }

/// <summary>An audit-trail entry (who did what to which patient's data, when, and why). Modelled after FHIR <c>AuditEvent</c>.</summary>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string User,
    AuditAction Action,
    string Resource,
    string? PatientId = null,
    string? Purpose = null,
    bool Success = true);

/// <summary>Append-only audit trail required for health-data access (HIPAA §164.312(b), Permenkes 24/2022).</summary>
public interface IAuditLog
{
    void Record(AuditEvent e);
    IReadOnlyList<AuditEvent> Query(string? patientId = null, DateTimeOffset? since = null);
}

public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEvent> _events = [];
    private readonly Lock _gate = new();

    public void Record(AuditEvent e) { lock (_gate) _events.Add(e); }

    public IReadOnlyList<AuditEvent> Query(string? patientId = null, DateTimeOffset? since = null)
    {
        lock (_gate)
            return _events.Where(e => (patientId is null || e.PatientId == patientId) && (since is null || e.Timestamp >= since)).ToArray();
    }
}
