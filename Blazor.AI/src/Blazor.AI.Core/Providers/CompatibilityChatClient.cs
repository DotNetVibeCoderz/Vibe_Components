using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Providers;

/// <summary>
/// Smooths over provider differences so components can always pass sampling options:
/// reasoning models (gpt-5, o1/o3/o4) reject <c>temperature</c>/<c>top_p</c>, so those are dropped up front,
/// and any provider that answers 400 to a sampling parameter is retried once without it.
/// </summary>
internal sealed class CompatibilityChatClient(IChatClient inner, string? modelId) : DelegatingChatClient(inner)
{
    private volatile bool _samplingUnsupported = IsReasoningModel(modelId);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        try
        {
            return await base.GetResponseAsync(list, Adjust(options), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_samplingUnsupported && HasSampling(options) && IsSamplingRejection(ex))
        {
            _samplingUnsupported = true;
            return await base.GetResponseAsync(list, Adjust(options), cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        ChatResponseUpdate? first = null;

        // The rejection surfaces on the first MoveNext, before any token is produced, so it is safe to retry there.
        try
        {
            enumerator = base.GetStreamingResponseAsync(list, Adjust(options), cancellationToken).GetAsyncEnumerator(cancellationToken);
            if (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                first = enumerator.Current;
            }
        }
        catch (Exception ex) when (!_samplingUnsupported && HasSampling(options) && IsSamplingRejection(ex))
        {
            _samplingUnsupported = true;
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            enumerator = base.GetStreamingResponseAsync(list, Adjust(options), cancellationToken).GetAsyncEnumerator(cancellationToken);
            if (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                first = enumerator.Current;
            }
        }

        await using (enumerator!.ConfigureAwait(false))
        {
            if (first is null)
            {
                yield break;
            }

            yield return first;
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }
        }
    }

    private ChatOptions? Adjust(ChatOptions? options)
    {
        if (options is null || !_samplingUnsupported || !HasSampling(options))
        {
            return options;
        }

        var clone = options.Clone();
        clone.Temperature = null;
        clone.TopP = null;
        clone.TopK = null;
        return clone;
    }

    private static bool HasSampling(ChatOptions? options) => options is not null && (options.Temperature is not null || options.TopP is not null || options.TopK is not null);

    internal static bool IsReasoningModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return false;
        }

        var m = model.ToLowerInvariant();
        return m.StartsWith("gpt-5", StringComparison.Ordinal) || m.StartsWith("o1", StringComparison.Ordinal)
               || m.StartsWith("o3", StringComparison.Ordinal) || m.StartsWith("o4", StringComparison.Ordinal);
    }

    private static bool IsSamplingRejection(Exception ex)
    {
        var message = ex.Message;
        var badRequest = ex is ClientResultException { Status: 400 } || message.Contains("BadRequest", StringComparison.OrdinalIgnoreCase) || message.Contains("400", StringComparison.Ordinal);
        return badRequest && (message.Contains("temperature", StringComparison.OrdinalIgnoreCase)
                              || message.Contains("top_p", StringComparison.OrdinalIgnoreCase)
                              || message.Contains("unsupported_value", StringComparison.OrdinalIgnoreCase)
                              || message.Contains("unsupported_parameter", StringComparison.OrdinalIgnoreCase));
    }
}
