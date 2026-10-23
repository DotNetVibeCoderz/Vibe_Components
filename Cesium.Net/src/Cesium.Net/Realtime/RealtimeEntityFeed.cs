using System.Collections.Concurrent;

namespace CesiumNet.Realtime;

/// <summary>
/// Coalesces high-frequency position updates from any source (SignalR, WebSocket, MQTT, REST polling,
/// simulators) and pushes them to the viewer in batches at a fixed rate.
/// </summary>
/// <remarks>
/// Producers call <see cref="Push(EntityPositionUpdate)"/> from any thread. Only the most recent update
/// per entity is kept between flushes, so a burst of 1,000 messages for the same aircraft costs one
/// interop update. The feed never blocks producers.
/// </remarks>
/// <example><code>
/// await using var feed = new RealtimeEntityFeed(cesium, TimeSpan.FromMilliseconds(100));
/// hubConnection.On&lt;string, double, double, double&gt;("pos", (id, lon, lat, alt) =&gt;
///     feed.Push(new EntityPositionUpdate(id, new GeoPoint(lon, lat, alt))));
/// </code></example>
public sealed class RealtimeEntityFeed : IAsyncDisposable
{
    private readonly CesiumController _controller;
    private readonly ConcurrentDictionary<string, EntityPositionUpdate> _pending = new(StringComparer.Ordinal);
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private long _published;

    /// <param name="controller">Target viewer.</param>
    /// <param name="interval">Flush interval (default 100 ms = 10 Hz).</param>
    public RealtimeEntityFeed(CesiumController controller, TimeSpan? interval = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _timer = new PeriodicTimer(interval ?? TimeSpan.FromMilliseconds(100));
        _loop = RunAsync(_cts.Token);
    }

    /// <summary>Total updates sent to the viewer.</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <summary>Number of entities waiting for the next flush.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Raised when a flush fails (e.g. the viewer was disposed).</summary>
    public event EventHandler<Exception>? FlushFailed;

    /// <summary>Queues a position update (thread-safe, lock-free, latest-wins per entity).</summary>
    public void Push(EntityPositionUpdate update) => _pending[update.Id] = update;

    /// <summary>Queues a position update.</summary>
    public void Push(string id, GeoPoint position, double? heading = null) => Push(new EntityPositionUpdate(id, position, heading));

    /// <summary>Sends pending updates immediately.</summary>
    public async ValueTask FlushAsync()
    {
        if (_pending.IsEmpty) return;
        var batch = new List<EntityPositionUpdate>(_pending.Count);
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var u)) batch.Add(u);
        if (batch.Count == 0) return;
        await _controller.Entities.UpdatePositionsAsync(batch).ConfigureAwait(false);
        Interlocked.Add(ref _published, batch.Count);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await FlushAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { FlushFailed?.Invoke(this, ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _timer.Dispose();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        // Deliver the last positions so the map does not freeze one tick behind.
        try { await FlushAsync().ConfigureAwait(false); } catch (Exception ex) { FlushFailed?.Invoke(this, ex); }
        _cts.Dispose();
    }
}
