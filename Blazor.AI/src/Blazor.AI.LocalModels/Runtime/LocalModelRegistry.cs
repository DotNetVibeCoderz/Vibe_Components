using System.Collections.Concurrent;
using Gravicode.HFNet.GraviHub;
using Gravicode.HFNet.GraviTransformers;
using Gravicode.HFNet.GraviTransformers.Vision;

namespace Blazor.AI.LocalModels;

/// <summary>A model loaded in memory.</summary>
public sealed record LoadedModel(string RepoId, string Kind, DateTimeOffset LoadedAt, TimeSpan LoadTime)
{
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Loads HF.Net models once and shares them across users (they are read-only at inference time).
/// Loading happens off the UI thread; concurrent requests for the same model wait on one load.
/// </summary>
public sealed class LocalModelRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoadedModel> _info = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public LocalModelRegistry(LocalModelOptions options)
    {
        Options = options;
        if (!string.IsNullOrWhiteSpace(options.HuggingFaceToken) || !string.IsNullOrWhiteSpace(options.CacheRoot))
        {
            var hub = new HubOptions();
            hub = hub with
            {
                Token = string.IsNullOrWhiteSpace(options.HuggingFaceToken) ? hub.Token : options.HuggingFaceToken,
                CacheRoot = string.IsNullOrWhiteSpace(options.CacheRoot) ? hub.CacheRoot : options.CacheRoot,
            };
            Hub.Configure(hub);
        }
    }

    public LocalModelOptions Options { get; }

    public IReadOnlyList<LoadedModel> Loaded => _info.Values.OrderBy(m => m.LoadedAt).ToArray();

    public event Action? Changed;

    public bool IsLoaded(string repoId, string kind) => _models.TryGetValue(Key(repoId, kind), out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully;

    public Task<TransformerModel> TextModelAsync(string repoId) => GetAsync(repoId, "text", () => TransformerModel.Load(repoId));

    public Task<CausalLanguageModel> ChatModelAsync(string repoId) => GetAsync(repoId, "causal-lm", () => CausalLanguageModel.Load(repoId));

    public Task<VisionTransformer> VisionModelAsync(string repoId) => GetAsync(repoId, "vision", () => VisionTransformer.Load(repoId));

    public Task<ClipModel> ClipModelAsync(string repoId) => GetAsync(repoId, "clip", () => ClipModel.Load(repoId));

    public Task<TrOcrModel> OcrModelAsync(string repoId) => GetAsync(repoId, "ocr", () => TrOcrModel.Load(repoId));

    /// <summary>Serialises inference per model: HF.Net models keep scratch buffers, so one call at a time each.</summary>
    public async Task<T> RunAsync<T>(string repoId, string kind, Func<T> inference, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(Key(repoId, kind), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_info.TryGetValue(Key(repoId, kind), out var info))
            {
                info.LastUsed = DateTimeOffset.UtcNow;
            }

            return await Task.Run(inference, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Unload(string repoId, string kind)
    {
        var key = Key(repoId, kind);
        if (_models.TryRemove(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully && lazy.Value.Result is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _info.TryRemove(key, out _);
        Changed?.Invoke();
    }

    /// <summary>Unloads models idle for longer than <see cref="LocalModelOptions.IdleUnload"/>.</summary>
    public void UnloadIdle()
    {
        foreach (var model in _info.Values.Where(m => DateTimeOffset.UtcNow - m.LastUsed > Options.IdleUnload).ToList())
        {
            Unload(model.RepoId, model.Kind);
        }
    }

    private async Task<T> GetAsync<T>(string repoId, string kind, Func<T> load)
    {
        var key = Key(repoId, kind);
        var lazy = _models.GetOrAdd(key, _ => new Lazy<Task<object>>(async () =>
        {
            var started = DateTimeOffset.UtcNow;
            var model = await Task.Run(() => (object)load()!).ConfigureAwait(false);
            _info[key] = new LoadedModel(repoId, kind, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - started);
            Changed?.Invoke();
            return model;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return (T)await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Do not cache failures: the next call retries (network blips, wrong repo id fixed by the user).
            _models.TryRemove(key, out _);
            throw;
        }
    }

    private static string Key(string repoId, string kind) => kind + "|" + repoId;

    public void Dispose()
    {
        foreach (var key in _models.Keys.ToList())
        {
            var parts = key.Split('|', 2);
            Unload(parts[1], parts[0]);
        }
    }
}
