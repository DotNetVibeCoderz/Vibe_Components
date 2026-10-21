using Blazor.AI.Analytics;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIAnalyticsExtensions
{
    /// <summary>Registers the per-user analytics and reinforcement-learning workspaces.</summary>
    public static IServiceCollection AddBlazorAIAnalytics(this IServiceCollection services)
    {
        services.TryAddScoped<AnalyticsWorkspace>();
        services.TryAddScoped<RlWorkspace>();
        return services;
    }
}
