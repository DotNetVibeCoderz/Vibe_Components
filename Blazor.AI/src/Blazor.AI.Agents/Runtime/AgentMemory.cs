using System.ComponentModel;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Vectors;
using Blazor.AI.Core.Vectors;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Agents;

/// <summary>A long-term memory entry.</summary>
public sealed record MemoryItem(string Id, string Agent, string Text, DateTimeOffset CreatedAt, string Source);

/// <summary>
/// Per-agent long-term memory: facts are embedded and stored in a vector index, then recalled by meaning.
/// Exposed to agents as the "remember" and "recall" tools.
/// </summary>
public sealed class AgentMemory(IAiClientFactory factory)
{
    private readonly InMemoryVectorIndex _index = new();
    private readonly List<MemoryItem> _items = [];
    private readonly Lock _gate = new();

    public event Action? Changed;

    public IReadOnlyList<MemoryItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.OrderByDescending(i => i.CreatedAt).ToArray();
            }
        }
    }

    public async Task<MemoryItem> RememberAsync(string agent, string text, string source = "agent", CancellationToken cancellationToken = default)
    {
        var item = new MemoryItem(Guid.NewGuid().ToString("N")[..10], agent, text.Trim(), DateTimeOffset.Now, source);
        var vector = await factory.GetEmbeddingGenerator().GenerateVectorAsync(text, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _index.UpsertAsync([new VectorRecord(item.Id, vector, item.Text, new Dictionary<string, string> { ["agent"] = agent })], cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _items.Add(item);
        }

        Changed?.Invoke();
        return item;
    }

    public async Task<IReadOnlyList<(MemoryItem Item, double Score)>> RecallAsync(string agent, string query, int top = 4, CancellationToken cancellationToken = default)
    {
        if (_index.Count == 0)
        {
            return [];
        }

        var vector = await factory.GetEmbeddingGenerator().GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var hits = await _index.SearchAsync(vector, new VectorSearchRequest { Top = top, MinScore = 0.05, Filter = new Dictionary<string, string> { ["agent"] = agent } }, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return hits.Select(h => (_items.First(i => i.Id == h.Record.Id), h.Score)).ToArray();
        }
    }

    public async Task ForgetAsync(string id)
    {
        await _index.DeleteAsync(id).ConfigureAwait(false);
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == id);
        }

        Changed?.Invoke();
    }

    public async Task ClearAsync(string? agent = null)
    {
        List<MemoryItem> removed;
        lock (_gate)
        {
            removed = _items.Where(i => agent is null || i.Agent == agent).ToList();
        }

        foreach (var item in removed)
        {
            await ForgetAsync(item.Id).ConfigureAwait(false);
        }
    }

    /// <summary>The "remember"/"recall" tools bound to one agent.</summary>
    public IEnumerable<AIFunction> ToolsFor(string agent)
    {
        yield return AIFunctionFactory.Create(
            async ([Description("A durable fact worth keeping, e.g. a user preference or decision")] string fact, CancellationToken ct) =>
            {
                await RememberAsync(agent, fact, "agent", ct).ConfigureAwait(false);
                return "Saved to memory.";
            },
            "remember",
            "Stores a fact in long-term memory for future conversations.");

        yield return AIFunctionFactory.Create(
            async ([Description("What to look up")] string query, CancellationToken ct) =>
            {
                var hits = await RecallAsync(agent, query, 4, ct).ConfigureAwait(false);
                return hits.Count == 0 ? "No relevant memories." : string.Join("\n", hits.Select(h => "- " + h.Item.Text));
            },
            "recall",
            "Searches long-term memory for facts related to the query.");
    }
}
