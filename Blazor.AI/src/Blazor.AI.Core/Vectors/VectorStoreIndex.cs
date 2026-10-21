using System.Runtime.CompilerServices;
using System.Text.Json;
using Blazor.AI.Abstractions.Vectors;
using Microsoft.Extensions.VectorData;

namespace Blazor.AI.Core.Vectors;

/// <summary>
/// Adapts any <c>Microsoft.Extensions.VectorData</c> <see cref="VectorStore"/> (Qdrant, Azure AI Search, Postgres,
/// Redis, SQLite, in-memory...) to <see cref="IVectorIndex"/> using a dynamic collection, so no record type is needed.
/// </summary>
public sealed class VectorStoreIndex : IVectorIndex
{
    private const string KeyField = "Id";
    private const string TextField = "Text";
    private const string MetadataField = "Metadata";
    private const string VectorField = "Embedding";

    private readonly VectorStoreCollection<object, Dictionary<string, object?>> _collection;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _ready;
    private int _count;

    public VectorStoreIndex(VectorStore store, string collectionName, int dimensions, string distanceFunction = DistanceFunction.CosineSimilarity)
    {
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(KeyField, typeof(string)),
                new VectorStoreDataProperty(TextField, typeof(string)) { IsFullTextIndexed = true },
                new VectorStoreDataProperty(MetadataField, typeof(string)),
                new VectorStoreVectorProperty(VectorField, typeof(ReadOnlyMemory<float>), dimensions) { DistanceFunction = distanceFunction },
            ],
        };

        _collection = store.GetDynamicCollection(collectionName, definition);
    }

    /// <summary>Best-effort count; remote stores do not expose it, so this tracks upserts made through this instance.</summary>
    public int Count => Math.Max(Volatile.Read(ref _count), 0);

    public async ValueTask UpsertAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        var batch = records.Select(ToDynamic).ToList();
        await _collection.UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _count, batch.Count);
    }

    public async ValueTask<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> query, VectorSearchRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new VectorSearchRequest();
        await EnsureAsync(cancellationToken).ConfigureAwait(false);

        // Over-fetch when a metadata filter must be applied client-side (filters are store-specific).
        var top = request.Filter is { Count: > 0 } ? request.Top * 4 : request.Top;
        var hits = new List<VectorSearchHit>(request.Top);
        await foreach (var result in _collection.SearchAsync(query, top, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var record = FromDynamic(result.Record);
            var score = result.Score ?? 0;
            if (score < request.MinScore || !MatchesFilter(record, request.Filter))
            {
                continue;
            }

            hits.Add(new VectorSearchHit(record, score));
            if (hits.Count == request.Top)
            {
                break;
            }
        }

        return hits;
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        await _collection.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        Interlocked.Decrement(ref _count);
        return true;
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await _collection.EnsureCollectionDeletedAsync(cancellationToken).ConfigureAwait(false);
        _ready = false;
        _count = 0;
    }

    public async IAsyncEnumerable<VectorRecord> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var item in _collection.GetAsync(_ => true, 1_000, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return FromDynamic(item);
        }
    }

    private async ValueTask EnsureAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        await _init.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
            {
                await _collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
                _ready = true;
            }
        }
        finally
        {
            _init.Release();
        }
    }

    private static Dictionary<string, object?> ToDynamic(VectorRecord record) => new()
    {
        [KeyField] = record.Id,
        [TextField] = record.Text,
        [MetadataField] = record.Metadata is null ? null : JsonSerializer.Serialize(record.Metadata, CoreJsonContext.Default.IReadOnlyDictionaryStringString),
        [VectorField] = record.Vector,
    };

    private static VectorRecord FromDynamic(Dictionary<string, object?> item)
    {
        var metadataJson = item.GetValueOrDefault(MetadataField) as string;
        var metadata = string.IsNullOrEmpty(metadataJson)
            ? null
            : JsonSerializer.Deserialize(metadataJson, CoreJsonContext.Default.IReadOnlyDictionaryStringString);
        var vector = item.GetValueOrDefault(VectorField) switch
        {
            ReadOnlyMemory<float> memory => memory,
            float[] array => array,
            _ => ReadOnlyMemory<float>.Empty,
        };
        return new VectorRecord(Convert.ToString(item[KeyField], System.Globalization.CultureInfo.InvariantCulture)!, vector, item.GetValueOrDefault(TextField) as string ?? string.Empty, metadata);
    }

    private static bool MatchesFilter(VectorRecord record, IReadOnlyDictionary<string, string>? filter) =>
        filter is null || filter.All(kv => string.Equals(record.GetMetadata(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase));
}
