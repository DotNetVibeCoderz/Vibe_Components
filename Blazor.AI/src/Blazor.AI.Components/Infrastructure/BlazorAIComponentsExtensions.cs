using Blazor.AI.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIComponentsExtensions
{
    /// <summary>Registers the services the Blazor.AI Razor components need (JS interop, theme state).</summary>
    /// <remarks>Call after <c>AddBlazorAI()</c>.</remarks>
    public static IServiceCollection AddBlazorAIComponents(this IServiceCollection services)
    {
        services.TryAddScoped<BlazorAIJs>();
        services.TryAddScoped<AiThemeState>();
        return services;
    }
}
