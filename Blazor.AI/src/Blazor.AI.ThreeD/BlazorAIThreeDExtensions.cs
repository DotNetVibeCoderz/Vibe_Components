using Blazor.AI.ThreeD;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIThreeDExtensions
{
    /// <summary>
    /// Enables the 3D components. They build on <c>AddBlazorAIComponents()</c>; components that reuse analytics, agents,
    /// decisions or biology need those packages registered too (<c>AddBlazorAIAnalytics()</c>, <c>AddBlazorAIAgents()</c>,
    /// <c>AddBlazorAIDecision()</c>, <c>AddBlazorAIBio()</c>). Add <c>_content/Blazor.AI.ThreeD/blazor-ai-3d.css</c> to the page.
    /// </summary>
    public static IServiceCollection AddBlazorAIThreeD(this IServiceCollection services)
    {
        services.AddScoped<CompanionMemoryStore>();
        return services;
    }
}
