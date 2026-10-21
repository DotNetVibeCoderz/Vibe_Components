using System.Text.RegularExpressions;
using Blazor.AI.Abstractions.Security;

namespace Blazor.AI.Core.Security;

/// <summary>
/// Fast, local first-line moderation: prompt-injection patterns plus category term lists and custom blocked terms.
/// For high-stakes apps, chain it with a model-based moderator by registering your own <see cref="IContentModerator"/>.
/// </summary>
public sealed partial class KeywordContentModerator(AiGuardrailOptions options) : IContentModerator
{
    [GeneratedRegex(@"ignore (all )?(previous|prior|above) (instructions|prompts)|disregard (the )?(system|previous) prompt|you are now (DAN|in developer mode)|reveal (your|the) (system prompt|instructions)|abaikan (semua )?instruksi sebelumnya", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Injection();

    private static readonly (ModerationCategory Category, string[] Terms)[] Categories =
    [
        (ModerationCategory.Violence, ["build a bomb", "make a bomb", "kill someone", "how to murder", "mass shooting", "membuat bom"]),
        (ModerationCategory.SelfHarm, ["kill myself", "suicide method", "self-harm", "bunuh diri"]),
        (ModerationCategory.Hate, ["ethnic cleansing", "racial slur", "inferior race"]),
        (ModerationCategory.Harassment, ["dox", "doxx", "home address of"]),
    ];

    public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.FromResult(ModerationResult.Safe);
        }

        if (Injection().Match(text) is { Success: true } injection)
        {
            return ValueTask.FromResult(new ModerationResult(true, ModerationCategory.PromptInjection, $"Prompt-injection pattern: \"{injection.Value}\"", 0.95));
        }

        foreach (var (category, terms) in Categories)
        {
            foreach (var term in terms)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return ValueTask.FromResult(new ModerationResult(true, category, $"Matched \"{term}\"", 0.9));
                }
            }
        }

        foreach (var term in options.BlockedTerms)
        {
            if (!string.IsNullOrWhiteSpace(term) && text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(new ModerationResult(true, ModerationCategory.Custom, $"Blocked term \"{term}\"", 1));
            }
        }

        return ValueTask.FromResult(ModerationResult.Safe);
    }
}
