namespace Blazor.AI.Abstractions.Prompts;

/// <summary>A reusable prompt with <c>{{variable}}</c> placeholders.</summary>
public sealed record PromptTemplate
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Category { get; init; } = "General";

    public string? Description { get; init; }

    public string? SystemPrompt { get; init; }

    public required string Template { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsFavorite { get; init; }
}

/// <summary>Persists prompt templates. The default implementation encrypts them at rest (AES-GCM).</summary>
public interface IPromptStore
{
    ValueTask<IReadOnlyList<PromptTemplate>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<PromptTemplate?> GetAsync(string id, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(PromptTemplate prompt, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    event Action? Changed;
}
