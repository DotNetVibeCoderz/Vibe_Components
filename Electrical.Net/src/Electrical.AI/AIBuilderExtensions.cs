using Electrical.Core.Hosting;
using Electrical.DigitalTwin;
using Electrical.Forecasting;
using Electrical.Optimization;
using Electrical.PowerFlow;
using Electrical.Renewable;
using Electrical.ShortCircuit;
using Electrical.Stability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.AI;

public static class AIBuilderExtensions
{
    /// <summary>
    /// Registers the grid agent: <see cref="IChatModel"/> (from <see cref="LlmOptions.FromEnvironment"/>, else the offline
    /// <see cref="RuleBasedChatModel"/>), a default <see cref="GridAgentContext"/> (case14) and a transient <see cref="GridAgent"/>,
    /// plus the analysis modules its tools use (PowerFlow, ShortCircuit, Optimization, Stability, Renewable, Forecasting, DigitalTwin).
    /// </summary>
    public static ElectricalBuilder AddAI(this ElectricalBuilder b, LlmOptions? llm = null)
    {
        b.AddPowerFlow().AddShortCircuit().AddOptimization().AddStability().AddRenewable().AddForecasting().AddDigitalTwin();
        if (b.TryAddModule("AI"))
        {
            b.Services.TryAddSingleton<IChatModel>(_ => llm is not null ? new OpenAiCompatibleChatModel(llm) : GridAgent.CreateDefaultModel());
            b.Services.TryAddSingleton<GridAgentContext>();
            b.Services.TryAddTransient(sp => new GridAgent(sp.GetRequiredService<GridAgentContext>(), sp.GetRequiredService<IChatModel>()));
        }
        return b;
    }
}
