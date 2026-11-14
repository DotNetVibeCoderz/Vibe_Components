using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Stability;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Dynamics;

/// <summary>Dynamics facade for dependency injection.</summary>
public interface IDynamicsService
{
    /// <summary>Builds and initializes a dynamic system (GENCLS for generators without models).</summary>
    DynamicSystem Build(PowerNetwork network, DynamicModelSet? models = null, DynamicSystemOptions? options = null);
    /// <summary>Runs a time-domain simulation.</summary>
    DynamicSimulationResult Simulate(DynamicSystem system, IEnumerable<DynamicEvent>? events = null, SimulationOptions? options = null);
    /// <summary>Small-signal analysis of the detailed model.</summary>
    DynamicSmallSignalResult SmallSignal(DynamicSystem system);
    /// <summary>Parses PSS/E .dyr text against a network.</summary>
    DynamicModelSet ParseDyr(string text, PowerNetwork network);
}

internal sealed class DynamicsService : IDynamicsService
{
    public DynamicSystem Build(PowerNetwork network, DynamicModelSet? models = null, DynamicSystemOptions? options = null) =>
        DynamicSystem.Build(network, models, options);
    public DynamicSimulationResult Simulate(DynamicSystem system, IEnumerable<DynamicEvent>? events = null, SimulationOptions? options = null) =>
        DynamicSimulation.Run(system, events, options);
    public DynamicSmallSignalResult SmallSignal(DynamicSystem system) => DynamicSmallSignal.Analyze(system);
    public DynamicModelSet ParseDyr(string text, PowerNetwork network) => DyrReader.Parse(text, network);
}

public static class DynamicsBuilderExtensions
{
    /// <summary>Registers <see cref="IDynamicsService"/> (and the stability and power-flow modules).</summary>
    public static ElectricalBuilder AddDynamics(this ElectricalBuilder b)
    {
        b.AddStability();
        if (b.TryAddModule("Dynamics")) b.Services.TryAddSingleton<IDynamicsService, DynamicsService>();
        return b;
    }
}
