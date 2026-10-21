using Blazor.AI.Decision;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIDecisionExtensions
{
    /// <summary>Registers the rule engine and the TypeSafe decision service (simulator when no API key is set).</summary>
    public static IServiceCollection AddBlazorAIDecision(this IServiceCollection services, Action<TypeSafeDecisionOptions>? configure = null)
    {
        var options = new TypeSafeDecisionOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<RuleEngine>();
        services.TryAddSingleton(sp => new TypeSafeDecisionService(sp.GetRequiredService<TypeSafeDecisionOptions>()));
        return services;
    }
}
