using Blazor.AI.RAG;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIRagExtensions
{
    /// <summary>
    /// Registers the knowledge base (scoped: one per user circuit, backed by the registered <c>IVectorIndex</c>),
    /// the knowledge-graph extractor and the spreadsheet analyst.
    /// </summary>
    public static IServiceCollection AddBlazorAIRag(this IServiceCollection services, Action<KnowledgeBase>? configure = null)
    {
        services.TryAddScoped(sp =>
        {
            var kb = ActivatorUtilities.CreateInstance<KnowledgeBase>(sp);
            configure?.Invoke(kb);
            return kb;
        });
        services.TryAddSingleton<KnowledgeGraphExtractor>();
        services.TryAddSingleton<SpreadsheetAnalyst>();
        return services;
    }
}
