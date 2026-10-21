using Blazor.AI.Agents;
using Blazor.AI.Bio;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIBioExtensions
{
    /// <summary>
    /// Enables the biology components. Requires <c>AddBlazorAIAgents()</c>; the Biotech.Net tools are added to every
    /// user's <see cref="ToolRegistry"/> so agents (and the Bio copilot) can call them.
    /// </summary>
    public static IServiceCollection AddBlazorAIBio(this IServiceCollection services)
    {
        services.AddScoped<BioToolsActivator>();
        return services;
    }
}
