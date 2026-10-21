using Gravicode.HFNet.GraviHub;

namespace Blazor.AI.LocalModels;

/// <summary>Hub search, model cards and downloads through HF.Net GraviHub.</summary>
public sealed class HuggingFaceHubService : IDisposable
{
    private readonly HubClient _client;

    public HuggingFaceHubService(LocalModelOptions options)
    {
        var hub = new HubOptions();
        _client = new HubClient(hub with
        {
            Token = string.IsNullOrWhiteSpace(options.HuggingFaceToken) ? hub.Token : options.HuggingFaceToken,
            CacheRoot = string.IsNullOrWhiteSpace(options.CacheRoot) ? hub.CacheRoot : options.CacheRoot,
        });
    }

    public bool IsAuthenticated => _client.IsAuthenticated;

    public string CacheRoot => _client.Options.CacheRoot ?? string.Empty;

    public async Task<IReadOnlyList<RepoInfo>> SearchAsync(string query, string? task = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        var results = await _client.SearchAsync(query, RepoKind.Model, limit, task, cancellationToken).ConfigureAwait(false);
        return results.OrderByDescending(r => r.Downloads).ToArray();
    }

    public Task<RepoInfo> InfoAsync(string repoId, CancellationToken cancellationToken = default) =>
        _client.GetRepoInfoAsync(repoId, RepoKind.Model, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<RepoFile>> FilesAsync(string repoId, CancellationToken cancellationToken = default) =>
        _client.ListFilesAsync(repoId, RepoKind.Model, cancellationToken: cancellationToken);

    /// <summary>Downloads the files needed to run a model (weights in safetensors, configs, tokenizer) into the cache.</summary>
    public Task<string> DownloadAsync(string repoId, IProgress<TransferProgress>? progress = null, bool weightsOnly = true, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string>? allow = weightsOnly ? ["*.json", "*.safetensors", "*.txt", "*.model", "tokenizer*", "vocab*", "merges*"] : null;
        return _client.SnapshotAsync(repoId, RepoKind.Model, allowPatterns: allow, progress: progress, cancellationToken: cancellationToken);
    }

    public string? ReadmeUrl(string repoId) => _client.ResolveUrl(repoId, "README.md");

    public void Dispose() => _client.Dispose();
}
