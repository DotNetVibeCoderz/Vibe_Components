using System.Collections.Concurrent;
using System.Text.Json;
using Blazor.AI.Abstractions.Prompts;
using Blazor.AI.Abstractions.Security;

namespace Blazor.AI.Core.Security;

/// <summary>Where encrypted stores persist their blob. The default keeps it in memory; use <see cref="FileSecureBlobStorage"/> on servers.</summary>
public interface ISecureBlobStorage
{
    ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default);
}

public sealed class InMemorySecureBlobStorage : ISecureBlobStorage
{
    private readonly ConcurrentDictionary<string, string> _blobs = new();

    public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult(_blobs.GetValueOrDefault(name));

    public ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        _blobs[name] = content;
        return ValueTask.CompletedTask;
    }
}

public sealed class FileSecureBlobStorage(string directory) : ISecureBlobStorage
{
    public async ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, name + ".bin");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".bin");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Prompt library persisted as a single AES-GCM encrypted JSON blob.</summary>
public sealed class EncryptedPromptStore(AesGcmProtector protector, ISecureBlobStorage storage) : IPromptStore
{
    private const string BlobName = "blazorai-prompts";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PromptTemplate>? _cache;

    public event Action? Changed;

    public async ValueTask<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async ValueTask<PromptTemplate?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);

    public async ValueTask SaveAsync(PromptTemplate prompt, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            list.RemoveAll(p => p.Id == prompt.Id);
            list.Add(prompt with { UpdatedAt = DateTimeOffset.UtcNow });
            await PersistAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        bool removed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            removed = list.RemoveAll(p => p.Id == id) > 0;
            if (removed)
            {
                await PersistAsync(list, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    private async ValueTask<List<PromptTemplate>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<List<PromptTemplate>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var blob = await storage.ReadAsync(BlobName, cancellationToken).ConfigureAwait(false);
        _cache = blob is null
            ? DefaultPrompts.Create()
            : JsonSerializer.Deserialize(protector.Unprotect(blob), CoreJsonContext.Default.ListPromptTemplate) ?? [];
        return _cache;
    }

    private async ValueTask PersistAsync(List<PromptTemplate> list, CancellationToken cancellationToken)
    {
        _cache = list;
        var json = JsonSerializer.Serialize(list, CoreJsonContext.Default.ListPromptTemplate);
        await storage.WriteAsync(BlobName, protector.Protect(json), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>API key vault: each secret is AES-GCM encrypted; keys never leave the server unencrypted at rest.</summary>
public sealed class EncryptedApiKeyVault(AesGcmProtector protector, ISecureBlobStorage storage) : IApiKeyVault
{
    private const string BlobName = "blazorai-keys";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cache;

    public async ValueTask SetAsync(string name, string secret, CancellationToken cancellationToken = default)
    {
        await Mutate(map => map[name] = protector.Protect(secret), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return map.TryGetValue(name, out var value) ? protector.Unprotect(value) : null;
    }

    public async ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        var removed = false;
        await Mutate(map => removed = map.Remove(name), cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken).ConfigureAwait(false)).Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private async ValueTask<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var blob = await storage.ReadAsync(BlobName, cancellationToken).ConfigureAwait(false);
        _cache = blob is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(JsonSerializer.Deserialize(blob, CoreJsonContext.Default.DictionaryStringString) ?? [], StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private async ValueTask Mutate(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            change(map);
            await storage.WriteAsync(BlobName, JsonSerializer.Serialize(map, CoreJsonContext.Default.DictionaryStringString), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class DefaultPrompts
{
    public static List<PromptTemplate> Create() =>
    [
        new()
        {
            Id = "summarize-meeting",
            Name = "Meeting summary",
            Category = "Productivity",
            Description = "Turn raw meeting notes into decisions and action items.",
            SystemPrompt = "You are an executive assistant.",
            Template = "Summarize these meeting notes for {{audience}}. List decisions, action items (owner, due date) and open questions.\n\n{{notes}}",
            Tags = ["meetings", "summary"],
            IsFavorite = true,
        },
        new()
        {
            Id = "product-description",
            Name = "Product description",
            Category = "Marketing",
            Description = "SEO-friendly product copy.",
            Template = "Write a {{tone}} product description for {{product}} targeting {{audience}}. Highlight: {{features}}. Max {{words}} words.",
            Tags = ["marketing", "ecommerce"],
        },
        new()
        {
            Id = "code-explainer",
            Name = "Explain code",
            Category = "Coding",
            Description = "Explain code to a junior developer.",
            SystemPrompt = "You are a patient senior software engineer.",
            Template = "Explain what this {{language}} code does, step by step, then suggest one improvement:\n\n```{{language}}\n{{code}}\n```",
            Tags = ["code", "learning"],
        },
        new()
        {
            Id = "customer-reply",
            Name = "Customer reply",
            Category = "Support",
            Description = "Empathetic customer support reply.",
            Template = "Write an empathetic reply in {{language}} to this customer message. Offer a concrete next step.\n\nCustomer: {{message}}",
            Tags = ["support", "email"],
        },
        new()
        {
            Id = "ringkas-dokumen",
            Name = "Ringkas dokumen (ID)",
            Category = "Productivity",
            Description = "Ringkasan dokumen dalam Bahasa Indonesia.",
            Template = "Ringkas dokumen berikut dalam {{jumlah}} poin penting dengan bahasa yang mudah dipahami:\n\n{{dokumen}}",
            Tags = ["indonesia", "ringkasan"],
        },
    ];
}
