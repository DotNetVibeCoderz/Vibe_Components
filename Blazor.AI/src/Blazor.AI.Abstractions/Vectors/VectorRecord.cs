namespace Blazor.AI.Abstractions.Vectors;

/// <summary>A piece of text plus its embedding and metadata, as stored in an <see cref="IVectorIndex"/>.</summary>
public sealed record VectorRecord(
    string Id,
    ReadOnlyMemory<float> Vector,
    string Text,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public string? GetMetadata(string key) =>
        Metadata is not null && Metadata.TryGetValue(key, out var value) ? value : null;
}

/// <summary>A search hit with its similarity score (cosine, higher is better).</summary>
public readonly record struct VectorSearchHit(VectorRecord Record, double Score);

/// <summary>Search options for <see cref="IVectorIndex.SearchAsync"/>.</summary>
public sealed record VectorSearchRequest
{
    public int Top { get; init; } = 5;

    /// <summary>Hits scoring below this are dropped.</summary>
    public double MinScore { get; init; } = double.NegativeInfinity;

    /// <summary>Optional metadata filter. All key/value pairs must match.</summary>
    public IReadOnlyDictionary<string, string>? Filter { get; init; }
}
