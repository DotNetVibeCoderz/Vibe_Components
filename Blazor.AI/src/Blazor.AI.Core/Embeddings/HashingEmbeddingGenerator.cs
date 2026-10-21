using System.Numerics.Tensors;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Embeddings;

/// <summary>
/// Offline embeddings built from word unigrams, word bigrams and character trigrams with signed feature hashing.
/// </summary>
/// <remarks>
/// No model download and no network. Quality is well below neural embeddings, but semantically related text
/// that shares vocabulary scores high, which is enough for demos, tests, WebAssembly and air-gapped deployments.
/// Vectors are L2-normalised, so the dot product equals cosine similarity.
/// </remarks>
public sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int MaxWordLength = 64;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public HashingEmbeddingGenerator(int dimensions = 384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 32);
        Dimensions = dimensions;
        _metadata = new EmbeddingGeneratorMetadata("blazorai-hashing", defaultModelId: "hashing-v1", defaultModelDimensions: dimensions);
    }

    public int Dimensions { get; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new Embedding<float>(Embed(value)) { ModelId = "hashing-v1", CreatedAt = DateTimeOffset.UtcNow });
        }

        return Task.FromResult(result);
    }

    /// <summary>Embeds a single text synchronously.</summary>
    public float[] Embed(ReadOnlySpan<char> text)
    {
        var vector = new float[Dimensions];
        var previousWordHash = 0u;
        var hasPrevious = false;

        Span<char> buffer = stackalloc char[MaxWordLength];

        foreach (var range in Tokenize(text))
        {
            var word = text[range];
            if (word.Length > MaxWordLength)
            {
                word = word[..MaxWordLength];
            }

            var lower = buffer[..word.Length];
            word.ToLowerInvariant(lower);
            var stem = Stem(lower);

            var wordHash = Fnv(stem, 0x9E3779B9u);
            Add(vector, wordHash, 1.0f);

            if (hasPrevious)
            {
                Add(vector, (previousWordHash * 31) ^ wordHash, 0.6f);
            }

            // Character trigrams of the padded word give robustness to typos and inflections.
            if (stem.Length >= 3)
            {
                for (var i = 0; i + 3 <= stem.Length; i++)
                {
                    Add(vector, Fnv(stem.Slice(i, 3), 0x85EBCA6Bu), 0.35f);
                }
            }

            previousWordHash = wordHash;
            hasPrevious = true;
        }

        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }

        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private void Add(float[] vector, uint hash, float weight)
    {
        var index = (int)(hash % (uint)vector.Length);
        vector[index] += (hash & 0x8000_0000u) == 0 ? weight : -weight;
    }

    private static ReadOnlySpan<char> Stem(ReadOnlySpan<char> word)
    {
        // Light English/Indonesian suffix stripping keeps "invoices" ≈ "invoice" and "pembayaran" ≈ "pembayar".
        if (word.Length > 5)
        {
            foreach (var suffix in Suffixes)
            {
                if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 3)
                {
                    return word[..^suffix.Length];
                }
            }
        }

        return word.Length > 3 && word[^1] == 's' && word[^2] != 's' ? word[..^1] : word;
    }

    private static readonly string[] Suffixes = ["ing", "ed", "ly", "ies", "es", "kan", "nya", "an"];

    private static uint Fnv(ReadOnlySpan<char> value, uint seed)
    {
        var hash = 2166136261u ^ seed;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    internal static List<Range> Tokenize(ReadOnlySpan<char> text)
    {
        var ranges = new List<Range>(Math.Max(4, text.Length / 5));
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                ranges.Add(start..i);
                start = -1;
            }
        }

        if (start >= 0)
        {
            ranges.Add(start..text.Length);
        }

        return ranges;
    }
}
