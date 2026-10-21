using Blazor.AI.Abstractions.Vectors;

namespace Blazor.AI.RAG;

/// <summary>A document stored in a <see cref="KnowledgeBase"/>.</summary>
public sealed record KnowledgeDocument
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>File type, e.g. <c>pdf</c>, <c>md</c>, <c>csv</c>.</summary>
    public required string Kind { get; init; }

    public required string Text { get; init; }

    public int Pages { get; init; } = 1;

    public int Chunks { get; init; }

    public long Bytes { get; init; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>A retrieved chunk with its source.</summary>
public sealed record KnowledgeHit(VectorRecord Chunk, double Score)
{
    public string DocumentId => Chunk.GetMetadata(KnowledgeBase.DocumentIdKey) ?? string.Empty;

    public string DocumentName => Chunk.GetMetadata(KnowledgeBase.DocumentNameKey) ?? "Document";

    public int? Page => int.TryParse(Chunk.GetMetadata(KnowledgeBase.PageKey), out var page) ? page : null;

    public string Text => Chunk.Text;

    public string SourceLabel => Page is { } p ? $"{DocumentName}, p. {p}" : DocumentName;
}

/// <summary>A cited answer: <see cref="Answer"/> references sources as [1], [2]... in <see cref="Citations"/> order.</summary>
public sealed record RagAnswer(string Answer, IReadOnlyList<KnowledgeHit> Citations);

/// <summary>Progress reported while ingesting.</summary>
public readonly record struct IngestionProgress(string Document, string Stage, double Fraction);
