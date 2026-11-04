using System.Collections.Concurrent;
using OpenSceneGraph.Net.Rendering;
using OpenSceneGraph.Net.Scene;

namespace OpenSceneGraph.Net.IO;

/// <summary>
/// Background loader for <see cref="PagedLod"/> and <see cref="ProxyNode"/> children (<c>osgDB::DatabasePager</c>).
/// Requests are prioritised (highest first), de-duplicated, dropped when no longer requested, loaded on worker
/// threads and merged into the scene during the update traversal so the scene graph is never mutated concurrently.
/// </summary>
public sealed class DatabasePager : IDatabaseRequestHandler, IDisposable
{
    private sealed class Request
    {
        public required PagedLod? Lod;
        public required ProxyNode? Proxy;
        public required int ChildIndex;
        public float Priority;
        public double LastRequestTime;
        public bool Loading;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<(Node, int), Request> _pending = [];
    private readonly ConcurrentQueue<(Request Req, Node? Result, Exception? Error)> _completed = new();
    private readonly List<PagedLod> _active = [];
    private readonly HashSet<(Node, int)> _failed = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private double _now;

    public DatabasePager(int threads = 2)
    {
        _workers = new Task[System.Math.Max(1, threads)];
        for (int i = 0; i < _workers.Length; i++) _workers[i] = Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning);
    }

    /// <summary>Custom loader; defaults to <see cref="Registry.ReadNodeFile"/>.</summary>
    public Func<string, CancellationToken, Node>? Loader { get; set; }

    /// <summary>Requests not renewed for this many seconds are discarded (the view moved away).</summary>
    public double RequestTimeout { get; set; } = 1.0;

    /// <summary>Maximum number of merges per frame, to bound frame-time spikes.</summary>
    public int MaxMergesPerFrame { get; set; } = 8;

    public bool ExpiryEnabled { get; set; } = true;

    public int PendingCount { get { lock (_lock) return _pending.Count; } }
    public int LoadedCount { get; private set; }
    public int ExpiredCount { get; private set; }
    public int FailedCount { get; private set; }
    public Exception? LastError { get; private set; }

    public void RequestPagedChild(PagedLod lod, int childIndex, float priority, double time)
    {
        lock (_lock)
        {
            _now = time;
            if (_failed.Contains((lod, childIndex))) return; // don't hammer a broken file every frame
            if (_pending.TryGetValue((lod, childIndex), out var r))
            {
                r.Priority = priority;
                r.LastRequestTime = time;
                return;
            }
            _pending[(lod, childIndex)] = new Request { Lod = lod, Proxy = null, ChildIndex = childIndex, Priority = priority, LastRequestTime = time };
        }
        _signal.Release();
    }

    public void RequestProxy(ProxyNode proxy)
    {
        lock (_lock)
        {
            if (_pending.ContainsKey((proxy, 0))) return;
            _pending[(proxy, 0)] = new Request { Lod = null, Proxy = proxy, ChildIndex = 0, Priority = float.MaxValue, LastRequestTime = double.MaxValue };
        }
        _signal.Release();
    }

    private Request? TakeHighestPriority()
    {
        lock (_lock)
        {
            Request? best = null;
            List<(Node, int)>? stale = null;
            foreach (var (key, r) in _pending)
            {
                if (r.Loading) continue;
                if (_now - r.LastRequestTime > RequestTimeout) { (stale ??= []).Add(key); continue; }
                if (best is null || r.Priority > best.Priority) best = r;
            }
            if (stale is not null) foreach (var k in stale) _pending.Remove(k);
            if (best is not null) best.Loading = true;
            return best;
        }
    }

    private async Task Worker()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var req = TakeHighestPriority();
            if (req is null) continue;
            try
            {
                Node result;
                if (req.Proxy is { } proxy)
                {
                    var g = new Group();
                    foreach (var f in proxy.FileNames)
                        g.AddChild(Load(proxy.DatabasePath is null ? f : Path.Combine(proxy.DatabasePath, f), ct));
                    result = g;
                }
                else
                {
                    var lod = req.Lod!;
                    result = Load(lod.ResolvePath(lod.Files[req.ChildIndex].FileName), ct);
                }
                _completed.Enqueue((req, result, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _completed.Enqueue((req, null, ex));
            }
        }
    }

    private Node Load(string path, CancellationToken ct) => Loader is { } l ? l(path, ct) : Registry.ReadNodeFile(path);

    /// <summary>Merges finished loads into the scene. Called by the viewer at the start of the update traversal.</summary>
    public void MergeCompletedRequests(double time)
    {
        _now = time;
        for (int i = 0; i < MaxMergesPerFrame && _completed.TryDequeue(out var c); i++)
        {
            lock (_lock) _pending.Remove(((Node?)c.Req.Lod ?? c.Req.Proxy!, c.Req.ChildIndex));
            if (c.Error is not null || c.Result is null)
            {
                FailedCount++;
                LastError = c.Error;
                lock (_lock) _failed.Add(((Node?)c.Req.Lod ?? c.Req.Proxy!, c.Req.ChildIndex));
                continue;
            }

            if (c.Req.Proxy is { } proxy)
            {
                foreach (var child in ((Group)c.Result).Children.ToArray()) { ((Group)c.Result).RemoveChild(child); proxy.AddChild(child); }
            }
            else
            {
                var lod = c.Req.Lod!;
                if (lod.ChildCount != c.Req.ChildIndex) continue; // out of order or already loaded
                var (min, max) = lod.Ranges[c.Req.ChildIndex];
                lod.AddChild(c.Result);
                lod.SetRange(c.Req.ChildIndex, min, max);
                lod.Files[c.Req.ChildIndex].LastUsedTime = time;
                if (!_active.Contains(lod)) _active.Add(lod);
            }
            LoadedCount++;
        }
    }

    /// <summary>Removes paged children that have not been visible for <see cref="PagedLod.ExpiryTime"/> seconds.</summary>
    public void ExpireUnused(double time)
    {
        if (!ExpiryEnabled) return;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var lod = _active[i];
            int last = lod.ChildCount - 1;
            // only expire file-backed children, newest first, so child indices stay contiguous
            if (last < 1 || last >= lod.Files.Count || string.IsNullOrEmpty(lod.Files[last].FileName)) { _active.RemoveAt(i); continue; }
            if (time - lod.Files[last].LastUsedTime > lod.ExpiryTime)
            {
                var (min, max) = lod.Ranges[last];
                lod.RemoveChildAt(last);
                lod.SetRange(last, min, max);
                ExpiredCount++;
            }
        }
    }

    /// <summary>Allows previously failed files to be requested again (e.g. after they were regenerated).</summary>
    public void ClearFailed() { lock (_lock) _failed.Clear(); }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
        _signal.Dispose();
    }
}
