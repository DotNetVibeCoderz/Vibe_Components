using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Blazor.AI.Abstractions;
using Blazor.AI.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Services;

public enum SummaryStyle
{
    Paragraph,
    Bullets,
    TlDr,
    Executive,
}

/// <summary>Result of <see cref="AiTextService.ClassifyAsync"/>.</summary>
public sealed record ClassificationResult(string Label, double Confidence, IReadOnlyDictionary<string, double> Scores, string? Reasoning = null);

/// <summary>High-level text operations used by most Blazor.AI components.</summary>
/// <remarks>Every method takes an optional provider name, so a component can target a specific model.</remarks>
public sealed class AiTextService(IAiClientFactory factory)
{
    public IAiClientFactory Factory { get; } = factory;

    public async Task<string> CompleteAsync(string? system, string user, string task = AiTasks.Chat, string? provider = null, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await Factory.GetChatClient(provider)
            .GetResponseAsync(Messages(system, user), (options ?? new ChatOptions()).WithTask(task), cancellationToken)
            .ConfigureAwait(false);
        return response.Text.Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(string? system, string user, string task = AiTasks.Chat, string? provider = null, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in Factory.GetChatClient(provider)
            .GetStreamingResponseAsync(Messages(system, user), (options ?? new ChatOptions()).WithTask(task), cancellationToken)
            .ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    public Task<string> SummarizeAsync(string text, SummaryStyle style = SummaryStyle.Paragraph, int maxWords = 120, string? language = null, string? provider = null, CancellationToken cancellationToken = default) =>
        CompleteAsync(SummarySystemPrompt(style, maxWords, language), PromptTags.Wrap(PromptTags.Input, text), AiTasks.Summarize, provider, new ChatOptions { Temperature = 0.2f }, cancellationToken);

    /// <summary>The system prompt used for summaries (shared by streaming components).</summary>
    public static string SummarySystemPrompt(SummaryStyle style, int maxWords, string? language)
    {
        var shape = style switch
        {
            SummaryStyle.Bullets => "as 3-6 concise markdown bullet points",
            SummaryStyle.TlDr => "as a single TL;DR sentence",
            SummaryStyle.Executive => "as an executive summary with a one-line headline, key points and recommended next steps",
            _ => "as one clear paragraph",
        };
        return $"You summarize text faithfully. Never invent facts. Write {shape}, at most {maxWords} words" +
               (string.IsNullOrEmpty(language) ? ", in the language of the input." : $", in {language}.");
    }

    /// <summary>The system prompt used for translations (shared by streaming components).</summary>
    public static string TranslationSystemPrompt(string targetLanguage, string? sourceLanguage) =>
        "You are a professional translator. Translate the input" +
        (string.IsNullOrEmpty(sourceLanguage) ? string.Empty : $" from {sourceLanguage}") +
        $" to {targetLanguage}. Preserve meaning, tone, formatting and markdown. Reply with the translation only.";

    public Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? provider = null, CancellationToken cancellationToken = default)
    {
        var system = TranslationSystemPrompt(targetLanguage, sourceLanguage);
        var user = PromptTags.Wrap(PromptTags.Target, targetLanguage) + "\n" + PromptTags.Wrap(PromptTags.Input, text);
        return CompleteAsync(system, user, AiTasks.Translate, provider, new ChatOptions { Temperature = 0.1f }, cancellationToken);
    }

    public Task<string> RewriteAsync(string text, string instruction, string? provider = null, CancellationToken cancellationToken = default)
    {
        var system = $"Rewrite the input. Instruction: {instruction}. Reply with the rewritten text only.";
        return CompleteAsync(system, PromptTags.Wrap(PromptTags.Input, text), AiTasks.Rewrite, provider, new ChatOptions { Temperature = 0.5f }, cancellationToken);
    }

    public async Task<ClassificationResult> ClassifyAsync(string text, IReadOnlyList<string> labels, string? instructions = null, string? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(labels.Count);
        var system = "You are a precise text classifier. " + (instructions ?? string.Empty) +
                     " Reply with JSON only: {\"label\": one of the labels, \"confidence\": 0..1, \"scores\": {label: probability}, \"reasoning\": short reason}.";
        var user = PromptTags.Wrap(PromptTags.Labels, string.Join(" | ", labels)) + "\n" + PromptTags.Wrap(PromptTags.Input, text);
        var raw = await CompleteAsync(system, user, AiTasks.Classify, provider, new ChatOptions { Temperature = 0f }, cancellationToken).ConfigureAwait(false);
        return ParseClassification(raw, labels);
    }

    /// <summary>Asks for JSON and returns the parsed node, or <c>null</c> if the model did not return valid JSON.</summary>
    public async Task<JsonNode?> GenerateJsonAsync(string system, string user, string task = AiTasks.Json, string? provider = null, CancellationToken cancellationToken = default)
    {
        var raw = await CompleteAsync(system + "\nReply with valid JSON only, no prose.", user, task, provider, new ChatOptions { Temperature = 0.1f }, cancellationToken).ConfigureAwait(false);
        return LlmJson.ParseNode(raw);
    }

    internal static ClassificationResult ParseClassification(string raw, IReadOnlyList<string> labels)
    {
        var node = LlmJson.ParseNode(raw) as JsonObject;
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (node?["scores"] is JsonObject scoreNode)
        {
            foreach (var (key, value) in scoreNode)
            {
                if (value is JsonValue v && v.TryGetValue<double>(out var d))
                {
                    scores[Canonical(key, labels) ?? key] = d;
                }
            }
        }

        var label = Canonical(node?["label"]?.ToString() ?? raw, labels)
                    ?? scores.OrderByDescending(s => s.Value).Select(s => s.Key).FirstOrDefault()
                    ?? labels[0];
        var confidence = node?["confidence"] is JsonValue c && c.TryGetValue<double>(out var conf) ? conf : scores.GetValueOrDefault(label, 0.5);
        foreach (var l in labels)
        {
            scores.TryAdd(l, l == label ? confidence : 0);
        }

        return new ClassificationResult(label, Math.Clamp(confidence, 0, 1), scores, node?["reasoning"]?.ToString());
    }

    private static string? Canonical(string value, IReadOnlyList<string> labels)
    {
        var trimmed = value.Trim().Trim('"', '.', '*');
        return labels.FirstOrDefault(l => l.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
               ?? labels.FirstOrDefault(l => trimmed.Contains(l, StringComparison.OrdinalIgnoreCase));
    }

    internal static List<ChatMessage> Messages(string? system, string user)
    {
        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ChatMessage(ChatRole.System, system));
        }

        messages.Add(new ChatMessage(ChatRole.User, user));
        return messages;
    }
}
