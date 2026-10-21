namespace Blazor.AI.Abstractions.Vectors;

/// <summary>A minimal vector index used by RAG, semantic search and agent memory.</summary>
/// <remarks>
/// Blazor.AI ships a SIMD in-memory implementation and an adapter for any
/// <c>Microsoft.Extensions.VectorData</c> store (Qdrant, Azure AI Search, Postgres, Redis...).
/// </remarks>
public interface IVectorIndex
{
    int Count { get; }

    ValueTask UpsertAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> query, VectorSearchRequest? request = null, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Enumerates stored records (used by explorers and debuggers).</summary>
    IAsyncEnumerable<VectorRecord> EnumerateAsync(CancellationToken cancellationToken = default);
}
