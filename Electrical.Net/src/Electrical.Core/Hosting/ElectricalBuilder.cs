using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Core.Hosting;

/// <summary>Fluent builder used by modules to register themselves: <c>services.AddElectrical(b =&gt; b.AddPowerFlow().AddShortCircuit())</c>.</summary>
public sealed class ElectricalBuilder
{
    private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);

    internal ElectricalBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }
    public IReadOnlyCollection<string> Modules => _modules;

    /// <summary>Registers a module once; returns false if it was already registered.</summary>
    public bool TryAddModule(string name) => _modules.Add(name);
}

/// <summary>Registry of loaded modules, resolvable from DI.</summary>
public sealed class ElectricalModuleRegistry(IEnumerable<string> modules)
{
    public IReadOnlyList<string> Modules { get; } = modules.ToList();
}

public static class ElectricalServiceCollectionExtensions
{
    public static IServiceCollection AddElectrical(this IServiceCollection services, Action<ElectricalBuilder>? configure = null)
    {
        var builder = new ElectricalBuilder(services);
        builder.TryAddModule("Core");
        services.TryAddSingleton<IGridEventBus, InMemoryGridEventBus>();
        services.TryAddSingleton(TimeProvider.System);
        configure?.Invoke(builder);
        services.AddSingleton(new ElectricalModuleRegistry(builder.Modules));
        return services;
    }
}

/// <summary>Marker for domain events published between modules (alarms, measurements, switching).</summary>
public interface IGridEvent
{
    DateTimeOffset Timestamp { get; }
}

public interface IGridEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IGridEvent;
    void Publish<TEvent>(TEvent evt) where TEvent : IGridEvent;
}

/// <summary>Thread-safe in-process event bus; handlers run synchronously on the publisher's thread.</summary>
public sealed class InMemoryGridEventBus : IGridEventBus
{
    private readonly ConcurrentDictionary<Type, Delegate[]> _handlers = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IGridEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.AddOrUpdate(typeof(TEvent), _ => [handler], (_, h) => [.. h, handler]);
        return new Subscription(() =>
            _handlers.AddOrUpdate(typeof(TEvent), _ => [], (_, h) => h.Where(d => !ReferenceEquals(d, handler)).ToArray()));
    }

    public void Publish<TEvent>(TEvent evt) where TEvent : IGridEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var hs)) return;
        foreach (var d in hs) ((Action<TEvent>)d)(evt);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
