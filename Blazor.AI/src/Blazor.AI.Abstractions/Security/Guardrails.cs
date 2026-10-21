namespace Blazor.AI.Abstractions.Security;

/// <summary>Options for the guardrail middleware wrapped around every chat client.</summary>
public sealed class AiGuardrailOptions
{
    /// <summary>Replace detected PII (emails, phones, card numbers, NIK, IBAN...) with placeholders before it leaves the app.</summary>
    public bool RedactPii { get; set; }

    /// <summary>Block prompts and responses the content moderator flags.</summary>
    public bool ModerateContent { get; set; }

    /// <summary>Record every request and response in the audit trail.</summary>
    public bool Audit { get; set; } = true;

    /// <summary>Enforce role-based access to providers and features.</summary>
    public bool EnforceRbac { get; set; }

    /// <summary>Maximum number of audit entries kept by the in-memory trail.</summary>
    public int AuditCapacity { get; set; } = 2_000;

    /// <summary>Base64 AES-256 key for the encrypted prompt store and key vault. A per-process key is generated when empty.</summary>
    public string? EncryptionKey { get; set; }

    /// <summary>Extra blocked terms for the built-in moderator.</summary>
    public List<string> BlockedTerms { get; set; } = [];

    /// <summary>Feature → roles allowed to use it. A feature that is not listed is open to everyone.</summary>
    public Dictionary<string, List<string>> FeatureRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider → roles allowed to use it. A provider that is not listed is open to everyone.</summary>
    public Dictionary<string, List<string>> ProviderRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum PiiKind
{
    Email,
    Phone,
    CreditCard,
    NationalId,
    Iban,
    IpAddress,
}

/// <summary>A PII occurrence inside a text.</summary>
public readonly record struct PiiMatch(PiiKind Kind, int Start, int Length, string Value);

public interface IPiiDetector
{
    IReadOnlyList<PiiMatch> Detect(string text);

    /// <summary>Returns the text with every match replaced by a placeholder such as <c>[EMAIL]</c>.</summary>
    string Redact(string text);
}

public enum ModerationCategory
{
    None,
    Hate,
    Violence,
    SelfHarm,
    Sexual,
    Harassment,
    PromptInjection,
    Custom,
}

public sealed record ModerationResult(bool Flagged, ModerationCategory Category = ModerationCategory.None, string? Reason = null, double Score = 0)
{
    public static ModerationResult Safe { get; } = new(false);
}

public interface IContentModerator
{
    ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken = default);
}

public enum AuditKind
{
    Request,
    Response,
    Blocked,
    ToolCall,
    Error,
    Info,
}

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    AuditKind Kind,
    string Provider,
    string? User,
    string Summary,
    string? Task = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    TimeSpan? Duration = null);

public interface IAuditTrail
{
    void Record(AuditEntry entry);

    IReadOnlyList<AuditEntry> Snapshot(int max = int.MaxValue);

    void Clear();

    event Action<AuditEntry>? Recorded;
}

/// <summary>Stores and resolves API keys without placing them in source or plain configuration.</summary>
public interface IApiKeyVault
{
    ValueTask SetAsync(string name, string secret, CancellationToken cancellationToken = default);

    ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Role-based access control for AI features and providers.</summary>
public interface IAiAuthorization
{
    bool IsAllowed(string? user, IReadOnlyCollection<string> roles, string feature, string? provider = null);
}

/// <summary>Supplies the current user to guardrails. Replace it with one backed by <c>AuthenticationStateProvider</c>.</summary>
public interface IAiUserContext
{
    string? UserName { get; }

    IReadOnlyCollection<string> Roles { get; }
}

/// <summary>Thrown when a guardrail blocks a request.</summary>
public sealed class AiGuardrailException(string message, ModerationCategory category = ModerationCategory.None) : Exception(message)
{
    public ModerationCategory Category { get; } = category;
}
