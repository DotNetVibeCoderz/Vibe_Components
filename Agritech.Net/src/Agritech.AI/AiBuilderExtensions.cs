using Agritech.AI.Agent;
using Agritech.Core.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.AI;

public static class AiBuilderExtensions
{
    /// <summary>Registers an <see cref="IChatModel"/> (from options, environment, or offline fallback).</summary>
    public static AgritechBuilder AddAI(this AgritechBuilder builder, LlmOptions? llm = null)
    {
        builder.TryAddModule("AI");
        var opts = llm ?? LlmOptions.FromEnvironment();
        builder.Services.TryAddSingleton<IChatModel>(_ => opts is null ? new RuleBasedChatModel() : new OpenAiCompatibleChatModel(opts));
        return builder;
    }
}
