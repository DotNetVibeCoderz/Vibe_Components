using System.Diagnostics;
using System.Runtime.CompilerServices;
using Blazor.AI.Abstractions;
using Blazor.AI.Abstractions.Security;
using Blazor.AI.Core.Services;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Security;

/// <summary>
/// Middleware applied to every provider client: RBAC → moderation → PII redaction → call → moderation → audit.
/// It is a <see cref="DelegatingChatClient"/>, so it composes with the rest of the Microsoft.Extensions.AI pipeline.
/// </summary>
public sealed class GuardrailChatClient(
    IChatClient inner,
    string providerName,
    AiGuardrailOptions options,
    IPiiDetector pii,
    IContentModerator moderator,
    IAuditTrail? audit,
    IAiAuthorization? authorization,
    IAiUserContext? user) : DelegatingChatClient(inner)
{
    public string ProviderName { get; } = providerName;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
    {
        var task = chatOptions.GetTask() ?? AiTasks.Chat;
        var prepared = await PrepareAsync(messages, task, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(prepared, chatOptions, cancellationToken).ConfigureAwait(false);
            await CheckOutputAsync(response.Text, task, cancellationToken).ConfigureAwait(false);
            Audit(AuditKind.Response, task, Truncate(response.Text), response.Usage, stopwatch.Elapsed);
            foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            {
                Audit(AuditKind.ToolCall, task, call.Name, null, null);
            }

            return response;
        }
        catch (Exception ex) when (ex is not AiGuardrailException and not OperationCanceledException)
        {
            Audit(AuditKind.Error, task, ex.Message, null, stopwatch.Elapsed);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var task = chatOptions.GetTask() ?? AiTasks.Chat;
        var prepared = await PrepareAsync(messages, task, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var text = new System.Text.StringBuilder();
        UsageDetails? usage = null;

        await foreach (var update in base.GetStreamingResponseAsync(prepared, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            text.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details ?? usage;
            foreach (var call in update.Contents.OfType<FunctionCallContent>())
            {
                Audit(AuditKind.ToolCall, task, call.Name, null, null);
            }

            yield return update;
        }

        await CheckOutputAsync(text.ToString(), task, cancellationToken).ConfigureAwait(false);
        Audit(AuditKind.Response, task, Truncate(text.ToString()), usage, stopwatch.Elapsed);
    }

    private async Task<IEnumerable<ChatMessage>> PrepareAsync(IEnumerable<ChatMessage> messages, string task, CancellationToken cancellationToken)
    {
        if (options.EnforceRbac && authorization is not null
            && !authorization.IsAllowed(user?.UserName, user?.Roles ?? [], task, ProviderName))
        {
            Audit(AuditKind.Blocked, task, $"RBAC denied '{task}' on '{ProviderName}'.", null, null);
            throw new AiGuardrailException($"You are not allowed to use '{task}' with provider '{ProviderName}'.");
        }

        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        if (options.ModerateContent)
        {
            var verdict = await moderator.ModerateAsync(lastUser, cancellationToken).ConfigureAwait(false);
            if (verdict.Flagged)
            {
                Audit(AuditKind.Blocked, task, $"Input blocked ({verdict.Category}): {verdict.Reason}", null, null);
                throw new AiGuardrailException($"The request was blocked by content moderation ({verdict.Category}).", verdict.Category);
            }
        }

        Audit(AuditKind.Request, task, Truncate(lastUser), null, null);

        if (!options.RedactPii)
        {
            return list;
        }

        var redacted = new List<ChatMessage>(list.Count);
        foreach (var message in list)
        {
            if (message.Role == ChatRole.System || message.Contents.Any(c => c is not TextContent))
            {
                redacted.Add(message);
                continue;
            }

            var clean = pii.Redact(message.Text ?? string.Empty);
            redacted.Add(ReferenceEquals(clean, message.Text) ? message : new ChatMessage(message.Role, clean) { AuthorName = message.AuthorName });
        }

        return redacted;
    }

    private async Task CheckOutputAsync(string text, string task, CancellationToken cancellationToken)
    {
        if (!options.ModerateContent)
        {
            return;
        }

        var verdict = await moderator.ModerateAsync(text, cancellationToken).ConfigureAwait(false);
        if (verdict.Flagged && verdict.Category != ModerationCategory.PromptInjection)
        {
            Audit(AuditKind.Blocked, task, $"Output blocked ({verdict.Category}): {verdict.Reason}", null, null);
            throw new AiGuardrailException($"The response was blocked by content moderation ({verdict.Category}).", verdict.Category);
        }
    }

    private void Audit(AuditKind kind, string task, string summary, UsageDetails? usage, TimeSpan? duration)
    {
        if (!options.Audit || audit is null)
        {
            return;
        }

        audit.Record(new AuditEntry(DateTimeOffset.UtcNow, kind, ProviderName, user?.UserName, summary, task, usage?.InputTokenCount, usage?.OutputTokenCount, duration));
    }

    private static string Truncate(string text) => text.Length <= 240 ? text : string.Concat(text.AsSpan(0, 237), "...");
}
