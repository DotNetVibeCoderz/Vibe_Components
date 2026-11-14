using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.ShortCircuit;

/// <summary>Short-circuit service resolvable from DI.</summary>
public interface IShortCircuitService
{
    /// <summary>Computes one IEC 60909 fault.</summary>
    FaultResult Calculate(PowerNetwork network, int busId, FaultType type, ShortCircuitOptions? options = null);
    /// <summary>All-bus short-circuit scan.</summary>
    IReadOnlyList<BusShortCircuitSummary> ScanAllBuses(PowerNetwork network, ShortCircuitOptions? options = null);
}

internal sealed class ShortCircuitService : IShortCircuitService
{
    public FaultResult Calculate(PowerNetwork network, int busId, FaultType type, ShortCircuitOptions? options = null) =>
        ShortCircuitAnalysis.Calculate(network, busId, type, options);

    public IReadOnlyList<BusShortCircuitSummary> ScanAllBuses(PowerNetwork network, ShortCircuitOptions? options = null) =>
        ShortCircuitAnalysis.ScanAllBuses(network, options);
}

public static class ShortCircuitBuilderExtensions
{
    /// <summary>Registers <see cref="IShortCircuitService"/>.</summary>
    public static ElectricalBuilder AddShortCircuit(this ElectricalBuilder b)
    {
        if (b.TryAddModule("ShortCircuit")) b.Services.TryAddSingleton<IShortCircuitService, ShortCircuitService>();
        return b;
    }
}
