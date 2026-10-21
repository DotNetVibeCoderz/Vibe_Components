namespace Blazor.AI.Core.Text;

/// <summary>A chunk of a larger document, with its character offset for citations.</summary>
public readonly record struct TextChunk(int Index, int Start, string Text);

/// <summary>Options for <see cref="TextChunker"/>. Sizes are in characters (≈ 4 characters per token).</summary>
public sealed record ChunkingOptions
{
    public int MaxChunkSize { get; init; } = 1_200;

    public int Overlap { get; init; } = 150;
}

/// <summary>
/// Splits text on paragraph, then sentence, then word boundaries so chunks never cut a sentence
/// unless a single sentence is longer than <see cref="ChunkingOptions.MaxChunkSize"/>.
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<TextChunk> Chunk(string text, ChunkingOptions? options = null)
    {
        options ??= new ChunkingOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxChunkSize, 50);
        var overlap = Math.Clamp(options.Overlap, 0, options.MaxChunkSize / 2);

        var chunks = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return chunks;
        }

        var span = text.AsSpan();
        var start = SkipWhitespace(span, 0);
        while (start < span.Length)
        {
            var remaining = span.Length - start;
            int end;
            if (remaining <= options.MaxChunkSize)
            {
                end = span.Length;
            }
            else
            {
                var window = span.Slice(start, options.MaxChunkSize);
                end = start + BestBreak(window, options.MaxChunkSize / 2);
            }

            var chunk = span[start..end].Trim();
            if (!chunk.IsEmpty)
            {
                chunks.Add(new TextChunk(chunks.Count, start, chunk.ToString()));
            }

            if (end >= span.Length)
            {
                break;
            }

            var next = Math.Max(start + 1, end - overlap);
            if (overlap > 0)
            {
                // Start the overlap on a word boundary.
                var space = span[next..end].IndexOf(' ');
                if (space >= 0)
                {
                    next += space + 1;
                }
            }

            start = SkipWhitespace(span, next);
        }

        return chunks;
    }

    private static int BestBreak(ReadOnlySpan<char> window, int minimum)
    {
        var paragraph = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (paragraph >= minimum)
        {
            return paragraph + 2;
        }

        for (var i = window.Length - 1; i >= minimum; i--)
        {
            if (window[i] is '.' or '!' or '?' or '。' && (i + 1 == window.Length || char.IsWhiteSpace(window[i + 1])))
            {
                return i + 1;
            }
        }

        var newline = window.LastIndexOf('\n');
        if (newline >= minimum)
        {
            return newline + 1;
        }

        var space = window.LastIndexOf(' ');
        return space >= minimum ? space + 1 : window.Length;
    }

    private static int SkipWhitespace(ReadOnlySpan<char> span, int index)
    {
        while (index < span.Length && char.IsWhiteSpace(span[index]))
        {
            index++;
        }

        return index;
    }
}
