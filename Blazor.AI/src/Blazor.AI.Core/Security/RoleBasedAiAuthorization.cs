using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Security;
using Microsoft.Extensions.Options;

namespace Blazor.AI.Core.Security;

/// <summary>RBAC driven by <see cref="AiGuardrailOptions.FeatureRoles"/> and <see cref="AiGuardrailOptions.ProviderRoles"/>.</summary>
public sealed class RoleBasedAiAuthorization(IOptions<BlazorAIOptions> options) : IAiAuthorization
{
    private readonly AiGuardrailOptions _guardrails = options.Value.Guardrails;

    public bool IsAllowed(string? user, IReadOnlyCollection<string> roles, string feature, string? provider = null) =>
        Allowed(_guardrails.FeatureRoles, feature, roles) && (provider is null || Allowed(_guardrails.ProviderRoles, provider, roles));

    private static bool Allowed(Dictionary<string, List<string>> map, string key, IReadOnlyCollection<string> roles) =>
        !map.TryGetValue(key, out var allowed) || allowed.Count == 0 || allowed.Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Default user context: anonymous with no roles. Override it with one backed by your authentication.</summary>
public sealed class AnonymousAiUserContext : IAiUserContext
{
    public string? UserName => null;

    public IReadOnlyCollection<string> Roles => [];
}
