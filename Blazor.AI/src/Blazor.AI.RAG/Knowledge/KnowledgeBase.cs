using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Blazor.AI.Abstractions;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Vectors;
using Blazor.AI.Core.Services;
using Blazor.AI.Core.Text;
using Microsoft.Extensions.AI;

namespace Blazor.AI.RAG;

/// <summary>
/// Ingests documents (load → chunk → embed → index) and answers questions with citations.
/// Works with any <see cref="IVectorIndex"/>: in-memory by default, or a Microsoft.Extensions.VectorData store.
/// </summary>
public sealed class KnowledgeBase(IAiClientFactory factory, IVectorIndex index)
{
    public const string DocumentIdKey = "docId";
    public const string DocumentNameKey = "docName";
    public const string PageKey = "page";
    public const string ChunkKey = "chunk";

    private const int EmbeddingBatchSize = 32;
    private readonly ConcurrentDictionary<string, KnowledgeDocument> _documents = new();

    /// <summary>Embedding provider. Use the same one for ingestion and search.</summary>
    public string? EmbeddingProvider { get; set; }

    public ChunkingOptions Chunking { get; set; } = new();

    public IVectorIndex Index => index;

    public IReadOnlyList<KnowledgeDocument> Documents => _documents.Values.OrderBy(d => d.AddedAt).ToArray();

    public int ChunkCount => index.Count;

    public event Action? Changed;

    public async Task<KnowledgeDocument> IngestAsync(LoadedDocument loaded, IProgress<IngestionProgress>? progress = null, IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        progress?.Report(new IngestionProgress(loaded.Name, "Chunking", 0.05));

        var pieces = new List<(string Text, int Page, int Index)>();
        for (var p = 0; p < loaded.Pages.Count; p++)
        {
            foreach (var chunk in TextChunker.Chunk(loaded.Pages[p], Chunking))
            {
                pieces.Add((chunk.Text, p + 1, pieces.Count));
            }
        }

        var generator = factory.GetEmbeddingGenerator(EmbeddingProvider);
        var records = new List<VectorRecord>(pieces.Count);
        for (var start = 0; start < pieces.Count; start += EmbeddingBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pieces.Skip(start).Take(EmbeddingBatchSize).ToList();
            // Prefix chunks with the document name: it helps retrieval for questions that name the document.
            var embeddings = await generator.GenerateAsync(batch.Select(b => $"{loaded.Name}\n{b.Text}"), cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                var metadata = new Dictionary<string, string>(tags ?? new Dictionary<string, string>())
                {
                    [DocumentIdKey] = id,
                    [DocumentNameKey] = loaded.Name,
                    [PageKey] = batch[i].Page.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [ChunkKey] = batch[i].Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                records.Add(new VectorRecord($"{id}:{batch[i].Index}", embeddings[i].Vector, batch[i].Text, metadata));
            }

            progress?.Report(new IngestionProgress(loaded.Name, "Embedding", 0.1 + 0.8 * Math.Min(1, (start + batch.Count) / (double)pieces.Count)));
        }

        await index.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
        var document = new KnowledgeDocument
        {
            Id = id,
            Name = loaded.Name,
            Kind = loaded.Kind,
            Text = loaded.Text,
            Pages = loaded.Pages.Count,
            Chunks = records.Count,
            Bytes = loaded.Bytes,
            Tags = tags ?? new Dictionary<string, string>(),
        };
        _documents[id] = document;
        progress?.Report(new IngestionProgress(loaded.Name, "Ready", 1));
        Changed?.Invoke();
        return document;
    }

    public Task<KnowledgeDocument> IngestTextAsync(string name, string text, CancellationToken cancellationToken = default) =>
        IngestAsync(DocumentLoader.FromText(name, text), cancellationToken: cancellationToken);

    public async Task RemoveAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryRemove(documentId, out var document))
        {
            return;
        }

        for (var i = 0; i < document.Chunks; i++)
        {
            await index.DeleteAsync($"{documentId}:{i}", cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await index.ClearAsync(cancellationToken).ConfigureAwait(false);
        _documents.Clear();
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int top = 5, string? documentId = null, double minScore = 0.05, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || index.Count == 0)
        {
            return [];
        }

        var embedding = await factory.GetEmbeddingGenerator(EmbeddingProvider).GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var hits = await index.SearchAsync(embedding, new VectorSearchRequest
        {
            Top = top,
            MinScore = minScore,
            Filter = documentId is null ? null : new Dictionary<string, string> { [DocumentIdKey] = documentId },
        }, cancellationToken).ConfigureAwait(false);
        return hits.Select(h => new KnowledgeHit(h.Record, h.Score)).ToArray();
    }

    public async IAsyncEnumerable<VectorRecord> ChunksOfAsync(string documentId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chunks = new List<VectorRecord>();
        await foreach (var record in index.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.GetMetadata(DocumentIdKey) == documentId)
            {
                chunks.Add(record);
            }
        }

        foreach (var chunk in chunks.OrderBy(c => int.TryParse(c.GetMetadata(ChunkKey), out var i) ? i : 0))
        {
            yield return chunk;
        }
    }

    /// <summary>Retrieves context and answers, streaming tokens. The citations are known before the first token.</summary>
    public async Task<(IReadOnlyList<KnowledgeHit> Citations, IAsyncEnumerable<string> Tokens)> AskStreamingAsync(
        string question,
        IReadOnlyList<ChatMessage>? history = null,
        int top = 5,
        string? documentId = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var hits = await SearchAsync(question, top, documentId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (hits, Stream(question, hits, history, provider, cancellationToken));
    }

    public async Task<RagAnswer> AskAsync(string question, int top = 5, string? documentId = null, string? provider = null, CancellationToken cancellationToken = default)
    {
        var (citations, tokens) = await AskStreamingAsync(question, null, top, documentId, provider, cancellationToken).ConfigureAwait(false);
        var answer = new StringBuilder();
        await foreach (var token in tokens.ConfigureAwait(false))
        {
            answer.Append(token);
        }

        return new RagAnswer(answer.ToString().Trim(), citations);
    }

    private async IAsyncEnumerable<string> Stream(string question, IReadOnlyList<KnowledgeHit> hits, IReadOnlyList<ChatMessage>? history, string? provider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            yield return _documents.IsEmpty
                ? "There are no documents yet. Add some, then ask again."
                : "I could not find anything relevant in the documents. Try different words, or add a document that covers this.";
            yield break;
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
        if (history is not null)
        {
            messages.AddRange(history.TakeLast(6));
        }

        messages.Add(new ChatMessage(ChatRole.User, BuildContext(hits) + "\n" + PromptTags.Wrap(PromptTags.Input, question)));
        await foreach (var update in factory.GetChatClient(provider)
                           .GetStreamingResponseAsync(messages, AiChatOptions.ForTask(AiTasks.Rag, temperature: 0.1f), cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    /// <summary>Instructions for grounded answers.</summary>
    public string SystemPrompt { get; set; } =
        "Answer using ONLY the numbered sources in <context>. Cite every claim with its source number in square brackets, e.g. [1] or [2][3]. " +
        "If the sources do not contain the answer, say so plainly instead of guessing. Answer in the language of the question. Be concise.";

    internal static string BuildContext(IReadOnlyList<KnowledgeHit> hits)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            builder.Append('[').Append(i + 1).Append("] ").AppendLine(hits[i].SourceLabel).AppendLine(hits[i].Text).AppendLine();
        }

        return PromptTags.Wrap(PromptTags.Context, builder.ToString().TrimEnd());
    }
}
