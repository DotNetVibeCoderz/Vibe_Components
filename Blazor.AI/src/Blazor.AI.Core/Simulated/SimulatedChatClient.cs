using System.Runtime.CompilerServices;
using Blazor.AI.Core.Services;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Simulated;

/// <summary>
/// An offline, deterministic <see cref="IChatClient"/>. It uses heuristics (extractive summaries, regex field extraction,
/// keyword classification, simulated tool calls) so every Blazor.AI component works without an API key:
/// in demos, tests, WebAssembly previews and air-gapped environments.
/// </summary>
public sealed class SimulatedChatClient : IChatClient
{
    private readonly ChatClientMetadata _metadata;

    public SimulatedChatClient(string modelId = "blazorai-sim-1", TimeSpan? tokenDelay = null)
    {
        _metadata = new ChatClientMetadata("blazorai-simulated", new Uri("local://simulated"), modelId);
        TokenDelay = tokenDelay ?? TimeSpan.FromMilliseconds(12);
    }

    /// <summary>Delay between streamed tokens, to mimic a real model in the UI.</summary>
    public TimeSpan TokenDelay { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var message = SimulatedResponder.Respond(list, options);
        var response = new ChatResponse(message)
        {
            ModelId = options?.ModelId ?? _metadata.DefaultModelId,
            FinishReason = message.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = Usage(list, message),
            CreatedAt = DateTimeOffset.UtcNow,
            ResponseId = Guid.NewGuid().ToString("N"),
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var message = SimulatedResponder.Respond(list, options);
        var responseId = Guid.NewGuid().ToString("N");
        var modelId = options?.ModelId ?? _metadata.DefaultModelId;

        var calls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (calls.Count > 0)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [.. calls]) { ResponseId = responseId, ModelId = modelId, FinishReason = ChatFinishReason.ToolCalls };
            yield break;
        }

        var text = message.Text;
        var start = 0;
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = NextTokenEnd(text, start);
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[start..end]) { ResponseId = responseId, ModelId = modelId };
            start = end;
            if (TokenDelay > TimeSpan.Zero)
            {
                await Task.Delay(TokenDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ResponseId = responseId,
            ModelId = modelId,
            FinishReason = ChatFinishReason.Stop,
            Contents = [new UsageContent(Usage(list, message))],
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private static int NextTokenEnd(string text, int start)
    {
        var i = start;
        // Emit leading whitespace with the following word, like BPE tokenizers do.
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return Math.Max(i, start + 1);
    }

    private static UsageDetails Usage(IList<ChatMessage> input, ChatMessage output)
    {
        var inputChars = input.Sum(m => m.Text?.Length ?? 0);
        var outputChars = output.Text?.Length ?? 0;
        return new UsageDetails
        {
            InputTokenCount = Math.Max(1, inputChars / 4),
            OutputTokenCount = Math.Max(1, outputChars / 4),
            TotalTokenCount = Math.Max(1, inputChars / 4) + Math.Max(1, outputChars / 4),
        };
    }
}
