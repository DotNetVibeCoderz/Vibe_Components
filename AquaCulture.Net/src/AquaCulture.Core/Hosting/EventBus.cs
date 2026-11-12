using System.Collections.Concurrent;

namespace AquaCulture.Core.Hosting;

/// <summary>Marker for events published on the bus (alerts, readings, decisions…).</summary>
public interface IAquaEvent
{
    DateTime Timestamp { get; }
}

/// <summary>Lightweight in-process publish/subscribe bus used by IoT, alerts and the digital twin.</summary>
public interface IAquaEventBus
{
    void Publish<T>(T evt) where T : IAquaEvent;
    IDisposable Subscribe<T>(Action<T> handler) where T : IAquaEvent;
}

/// <summary>Thread-safe synchronous event bus. Handler exceptions are isolated so one subscriber cannot break another.</summary>
public sealed class InMemoryEventBus : IAquaEventBus
{
    private readonly ConcurrentDictionary<Type, ImmutableHandlers> _handlers = new();

    public event Action<Exception>? HandlerFailed;

    public void Publish<T>(T evt) where T : IAquaEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        foreach (var h in list.Items)
        {
            try { ((Action<T>)h)(evt); }
            catch (Exception ex) { HandlerFailed?.Invoke(ex); }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : IAquaEvent
    {
        _handlers.AddOrUpdate(typeof(T), _ => new([handler]), (_, l) => new([.. l.Items, handler]));
        return new Subscription(() => _handlers.AddOrUpdate(typeof(T), _ => new([]), (_, l) => new(l.Items.Where(x => !ReferenceEquals(x, handler)).ToArray())));
    }

    private sealed record ImmutableHandlers(Delegate[] Items);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
