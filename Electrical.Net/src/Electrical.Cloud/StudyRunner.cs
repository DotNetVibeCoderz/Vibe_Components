using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Electrical.Core.Network;

namespace Electrical.Cloud;

public enum StudyStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>A queued study (power flow, contingency, short circuit, …) for a tenant.</summary>
public sealed class StudyJob
{
    public required string Id { get; init; }
    public required string Tenant { get; init; }
    public required string Kind { get; init; }
    public required PowerNetwork Network { get; init; }
    public JsonObject Parameters { get; init; } = [];
    public StudyStatus Status { get; internal set; } = StudyStatus.Queued;
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; internal set; }
    public JsonNode? Result { get; internal set; }
    public string? Error { get; internal set; }

    public JsonObject Summary() => new()
    {
        ["id"] = Id, ["tenant"] = Tenant, ["kind"] = Kind, ["network"] = Network.Name, ["status"] = Status.ToString(),
        ["created"] = Created.ToString("O"), ["finished"] = Finished?.ToString("O"), ["error"] = Error,
    };
}

/// <summary>Executes a study kind and returns a JSON result.</summary>
public delegate JsonNode StudyHandler(PowerNetwork network, JsonObject parameters);

/// <summary>
/// In-process background study runner with a bounded queue and a fixed number of workers. Study kinds are pluggable
/// (<see cref="Register"/>); <see cref="StudyCatalog.Default"/> registers power flow, DC power flow, N-1 and short circuit.
/// </summary>
public sealed class StudyRunner : IAsyncDisposable
{
    private readonly Channel<StudyJob> _queue;
    private readonly ConcurrentDictionary<string, StudyJob> _jobs = new();
    private readonly ConcurrentDictionary<string, StudyHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private long _counter;

    public StudyRunner(int workers = 2, int capacity = 1000)
    {
        _queue = Channel.CreateBounded<StudyJob>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
        _workers = Enumerable.Range(0, Math.Max(1, workers)).Select(_ => Task.Run(WorkAsync)).ToArray();
    }

    public IReadOnlyCollection<string> Kinds => _handlers.Keys.ToList();

    public StudyRunner Register(string kind, StudyHandler handler)
    {
        _handlers[kind] = handler;
        return this;
    }

    public async ValueTask<StudyJob> SubmitAsync(string tenant, string kind, PowerNetwork network, JsonObject? parameters = null, CancellationToken ct = default)
    {
        if (!_handlers.ContainsKey(kind)) throw new ArgumentException($"Unknown study kind '{kind}'. Known: {string.Join(", ", Kinds)}", nameof(kind));
        var job = new StudyJob
        {
            Id = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _counter):D5}", Tenant = tenant, Kind = kind,
            Network = network, Parameters = parameters ?? [],
        };
        _jobs[job.Id] = job;
        await _queue.Writer.WriteAsync(job, ct);
        return job;
    }

    public StudyJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<StudyJob> List(string? tenant = null) =>
        _jobs.Values.Where(j => tenant is null || j.Tenant == tenant).OrderByDescending(j => j.Created);

    /// <summary>Waits until the job leaves the queued/running states.</summary>
    public async Task<StudyJob> WaitAsync(string id, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var job = Get(id) ?? throw new KeyNotFoundException(id);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (job.Status is StudyStatus.Queued or StudyStatus.Running)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Study {id} did not finish in time.");
            await Task.Delay(10, ct);
        }
        return job;
    }

    /// <summary>Runs a study synchronously on the caller's thread (no queue).</summary>
    public JsonNode RunNow(string kind, PowerNetwork network, JsonObject? parameters = null) =>
        _handlers.TryGetValue(kind, out var h) ? h(network, parameters ?? []) : throw new ArgumentException($"Unknown study kind '{kind}'.");

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                job.Status = StudyStatus.Running;
                try
                {
                    job.Result = _handlers[job.Kind](job.Network, job.Parameters);
                    job.Status = StudyStatus.Completed;
                }
                catch (Exception e)
                {
                    job.Error = e.Message;
                    job.Status = StudyStatus.Failed;
                }
                job.Finished = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync();
        try { await Task.WhenAll(_workers); } catch (OperationCanceledException) { }
        foreach (var j in _jobs.Values.Where(j => j.Status == StudyStatus.Queued)) j.Status = StudyStatus.Cancelled;
        _cts.Dispose();
    }
}

/// <summary>Tenant-scoped storage of named scenarios (networks).</summary>
public interface IScenarioStore
{
    void Save(string tenant, string name, PowerNetwork network);
    PowerNetwork? Load(string tenant, string name);
    IReadOnlyList<string> List(string tenant);
    bool Delete(string tenant, string name);
}

/// <summary>Thread-safe in-memory scenario store (networks are cloned on save and load).</summary>
public sealed class InMemoryScenarioStore : IScenarioStore
{
    private readonly ConcurrentDictionary<(string, string), PowerNetwork> _items = new();
    public void Save(string tenant, string name, PowerNetwork network) => _items[(tenant, name)] = network.Clone();
    public PowerNetwork? Load(string tenant, string name) => _items.TryGetValue((tenant, name), out var n) ? n.Clone() : null;
    public IReadOnlyList<string> List(string tenant) => _items.Keys.Where(k => k.Item1 == tenant).Select(k => k.Item2).Order().ToList();
    public bool Delete(string tenant, string name) => _items.TryRemove((tenant, name), out _);
}

/// <summary>Stores scenarios as JSON files under <c>root/tenant/name.json</c>.</summary>
public sealed class FileScenarioStore(string root) : IScenarioStore
{
    private string PathOf(string tenant, string name)
    {
        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return System.IO.Path.Combine(root, Safe(tenant), Safe(name) + ".json");
    }

    public void Save(string tenant, string name, PowerNetwork network)
    {
        var p = PathOf(tenant, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, NetworkJson.Serialize(network, indented: true));
    }

    public PowerNetwork? Load(string tenant, string name) => File.Exists(PathOf(tenant, name)) ? NetworkJson.Parse(File.ReadAllText(PathOf(tenant, name))) : null;

    public IReadOnlyList<string> List(string tenant)
    {
        var dir = System.IO.Path.GetDirectoryName(PathOf(tenant, "x"))!;
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).Order().ToList() : [];
    }

    public bool Delete(string tenant, string name)
    {
        var p = PathOf(tenant, name);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }
}
