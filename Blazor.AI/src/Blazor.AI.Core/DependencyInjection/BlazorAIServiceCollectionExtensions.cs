using Blazor.AI.Abstractions.Prompts;
using Blazor.AI.Abstractions.Providers;
using Blazor.AI.Abstractions.Security;
using Blazor.AI.Abstractions.Smart;
using Blazor.AI.Abstractions.Vectors;
using Blazor.AI.Core;
using Blazor.AI.Core.Providers;
using Blazor.AI.Core.Security;
using Blazor.AI.Core.Services;
using Blazor.AI.Core.Vectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIServiceCollectionExtensions
{
    /// <summary>Registers the Blazor.AI core services. With no provider configured, the offline simulated provider is used.</summary>
    /// <param name="configuration">Optional configuration root; the <c>BlazorAI</c> section is bound when present.</param>
    public static BlazorAIBuilder AddBlazorAI(this IServiceCollection services, IConfiguration? configuration = null, Action<BlazorAIOptions>? configure = null)
    {
        var options = services.AddOptions<BlazorAIOptions>();
        if (configuration is not null)
        {
            var section = configuration.GetSection(BlazorAIOptions.SectionName);
            options.Configure(o => Bind(section, o));
        }

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<IAiClientFactory>(sp =>
        {
            var factory = ActivatorUtilities.CreateInstance<AiClientFactory>(sp);
            foreach (var custom in sp.GetServices<CustomClientRegistration>())
            {
                factory.Register(custom.Name, custom.ChatClient(sp), custom.Embeddings?.Invoke(sp), custom.Kind);
            }

            return factory;
        });

        services.TryAddSingleton<AiTextService>();
        services.TryAddSingleton<ISmartPasteService, SmartPasteService>();
        services.TryAddSingleton<ISmartTextAreaService, SmartTextAreaService>();
        services.TryAddSingleton<ISemanticMatcher, SemanticMatcher>();
        services.TryAddTransient<IVectorIndex, InMemoryVectorIndex>();

        services.TryAddSingleton<IPiiDetector, RegexPiiDetector>();
        services.TryAddSingleton<IContentModerator>(sp => new KeywordContentModerator(Guardrails(sp)));
        services.TryAddSingleton<IAuditTrail>(sp => new InMemoryAuditTrail(Guardrails(sp).AuditCapacity));
        services.TryAddSingleton<IAiAuthorization, RoleBasedAiAuthorization>();
        services.TryAddSingleton<IAiUserContext, AnonymousAiUserContext>();
        services.TryAddSingleton(sp => new AesGcmProtector(Guardrails(sp).EncryptionKey));
        services.TryAddSingleton<ISecureBlobStorage, InMemorySecureBlobStorage>();
        services.TryAddSingleton<IPromptStore, EncryptedPromptStore>();
        services.TryAddSingleton<IApiKeyVault, EncryptedApiKeyVault>();

        return new BlazorAIBuilder(services);
    }

    private static AiGuardrailOptions Guardrails(IServiceProvider sp) =>
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BlazorAIOptions>>().Value.Guardrails;

    // Manual binding keeps the library trimming-safe (no reflection-based ConfigurationBinder).
    private static void Bind(IConfigurationSection section, BlazorAIOptions options)
    {
        if (section["DefaultProvider"] is { Length: > 0 } defaultProvider)
        {
            options.DefaultProvider = defaultProvider;
        }

        if (int.TryParse(section["OfflineEmbeddingDimensions"], out var dims))
        {
            options.OfflineEmbeddingDimensions = dims;
        }

        foreach (var child in section.GetSection("Providers").GetChildren())
        {
            var provider = new AiProviderOptions
            {
                Name = child["Name"] ?? child.Key,
                Kind = Enum.TryParse<AiProviderKind>(child["Kind"], ignoreCase: true, out var kind) ? kind : AiProviderKind.Custom,
                ApiKey = child["ApiKey"],
                Endpoint = child["Endpoint"],
                Model = child["Model"],
                EmbeddingModel = child["EmbeddingModel"],
                DisplayName = child["DisplayName"],
                MaxOutputTokens = int.TryParse(child["MaxOutputTokens"], out var max) ? max : null,
            };

            foreach (var setting in child.GetSection("Settings").GetChildren())
            {
                provider.Settings[setting.Key] = setting.Value ?? string.Empty;
            }

            // Skip cloud providers whose key is missing, so a half-filled appsettings.json does not break startup.
            if (ProviderDefaults.RequiresApiKey(provider.Kind) && string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                continue;
            }

            options.Providers.RemoveAll(p => p.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase));
            options.Providers.Add(provider);
        }

        var guard = section.GetSection("Guardrails");
        if (guard.Exists())
        {
            var g = options.Guardrails;
            g.RedactPii = bool.TryParse(guard["RedactPii"], out var b1) ? b1 : g.RedactPii;
            g.ModerateContent = bool.TryParse(guard["ModerateContent"], out var b2) ? b2 : g.ModerateContent;
            g.Audit = bool.TryParse(guard["Audit"], out var b3) ? b3 : g.Audit;
            g.EnforceRbac = bool.TryParse(guard["EnforceRbac"], out var b4) ? b4 : g.EnforceRbac;
            g.AuditCapacity = int.TryParse(guard["AuditCapacity"], out var cap) ? cap : g.AuditCapacity;
            g.EncryptionKey = guard["EncryptionKey"] ?? g.EncryptionKey;
            g.BlockedTerms.AddRange(guard.GetSection("BlockedTerms").GetChildren().Select(c => c.Value!).Where(v => !string.IsNullOrWhiteSpace(v)));
        }
    }
}
