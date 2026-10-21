using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Gravicode.HFNet.GraviTransformers;
using Microsoft.Extensions.AI;

namespace Blazor.AI.LocalModels;

/// <summary>An <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> over a local HF.Net sentence-transformer (e.g. all-MiniLM-L6-v2).</summary>
public sealed class LocalEmbeddingGenerator(LocalModelRegistry registry, string? model = null) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly string _model = model ?? registry.Options.EmbeddingModel;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var transformer = await registry.TextModelAsync(_model).ConfigureAwait(false);
        var matrix = await registry.RunAsync(_model, "text", () => transformer.EmbedBatch(texts), cancellationToken).ConfigureAwait(false);
        var result = new GeneratedEmbeddings<Embedding<float>>();
        for (var i = 0; i < texts.Count; i++)
        {
            result.Add(new Embedding<float>(matrix.Row(i).ToArray().Select(v => (float)v).ToArray()) { ModelId = _model });
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("hfnet-local", null, _model) : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>
/// An <see cref="IChatClient"/> over a local HF.Net causal language model (Llama, Qwen, Mistral, SmolLM, GPT-2 families).
/// Messages are formatted with the ChatML template most small instruct models use; override <see cref="Template"/> for others.
/// </summary>
public sealed class LocalChatClient(LocalModelRegistry registry, string? model = null) : IChatClient
{
    private readonly string _model = model ?? registry.Options.ChatModel;

    /// <summary>Formats the conversation into the model's prompt.</summary>
    public Func<IReadOnlyList<ChatMessage>, string> Template { get; set; } = ChatMl;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            text.Append(update.Text);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString().Trim())) { ModelId = _model };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lm = await registry.ChatModelAsync(_model).ConfigureAwait(false);
        var prompt = Template(messages.ToList());
        var settings = new GenerationSettings(
            MaxNewTokens: options?.MaxOutputTokens ?? 256,
            Sample: (options?.Temperature ?? 0.7f) > 0.01f,
            Temperature: options?.Temperature ?? 0.7,
            TopP: options?.TopP ?? 0.9,
            RepetitionPenalty: 1.1);

        // Generation is synchronous CPU work: run it on the thread pool and stream tokens through a channel.
        var channel = Channel.CreateUnbounded<string>();
        var producer = registry.RunAsync(_model, "causal-lm", () =>
        {
            try
            {
                foreach (var token in lm.Stream(prompt, settings))
                {
                    if (cancellationToken.IsCancellationRequested || token.Contains("<|im_end|>", StringComparison.Ordinal))
                    {
                        break;
                    }

                    channel.Writer.TryWrite(token);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }

            return true;
        }, cancellationToken);

        await foreach (var token in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, token) { ModelId = _model };
        }

        await producer.ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("hfnet-local", null, _model) : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    public static string ChatMl(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.System ? "system" : message.Role == ChatRole.Assistant ? "assistant" : "user";
            builder.Append("<|im_start|>").Append(role).Append('\n').Append(message.Text).Append("<|im_end|>\n");
        }

        return builder.Append("<|im_start|>assistant\n").ToString();
    }
}
