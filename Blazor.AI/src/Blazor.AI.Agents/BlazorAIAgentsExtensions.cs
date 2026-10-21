using Blazor.AI.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIAgentsExtensions
{
    /// <summary>
    /// Registers the agent runtime. Catalog, tools, memory and MCP connections are scoped (one set per user circuit);
    /// register your own tools with <paramref name="configureTools"/>.
    /// </summary>
    public static IServiceCollection AddBlazorAIAgents(this IServiceCollection services, Action<ToolRegistry>? configureTools = null)
    {
        services.TryAddScoped(_ =>
        {
            var registry = new ToolRegistry();
            configureTools?.Invoke(registry);
            return registry;
        });
        services.TryAddScoped<AgentCatalog>();
        services.TryAddScoped<AgentMemory>();
        services.TryAddScoped<AgentRuntime>();
        services.TryAddScoped<MultiAgentOrchestrator>();
        services.TryAddScoped<WorkflowExecutor>();
        services.TryAddScoped<AgentTaskBoardState>();
        services.TryAddScoped<McpConnector>();
        return services;
    }
}
