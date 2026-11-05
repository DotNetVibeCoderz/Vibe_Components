using System.Collections.Concurrent;

namespace Agritech.Core.Hosting;

/// <summary>Marker for domain events published between modules.</summary>
public interface IAgriEvent
{
    DateTimeOffset Timestamp { get; }
}

public interface IAgriEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IAgriEvent;
    void Publish<TEvent>(TEvent evt) where TEvent : IAgriEvent;
}

/// <summary>Thread-safe in-process event bus. Handlers run synchronously on the publisher's thread.</summary>
public sealed class InMemoryEventBus : IAgriEventBus
{
    private readonly ConcurrentDictionary<Type, ImmutableHandlers> _handlers = new();

    private sealed class ImmutableHandlers(Delegate[] items)
    {
        public readonly Delegate[] Items = items;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IAgriEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.AddOrUpdate(typeof(TEvent), _ => new([handler]), (_, h) => new([.. h.Items, handler]));
        return new Subscription(() =>
            _handlers.AddOrUpdate(typeof(TEvent), _ => new([]), (_, h) => new(h.Items.Where(d => !ReferenceEquals(d, handler)).ToArray())));
    }

    public void Publish<TEvent>(TEvent evt) where TEvent : IAgriEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var hs)) return;
        foreach (var d in hs.Items) ((Action<TEvent>)d)(evt);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
