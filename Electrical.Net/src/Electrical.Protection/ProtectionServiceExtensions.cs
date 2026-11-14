using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.ShortCircuit;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Protection;

/// <summary>Protection-engineering service resolvable from DI.</summary>
public interface IProtectionService
{
    /// <summary>Grades overcurrent relays along a radial path using IEC 60909 fault currents.</summary>
    GradingResult GradeRadialFeeder(PowerNetwork network, IReadOnlyList<int> busPathFromSource, IReadOnlyList<OvercurrentRelay> relaysFromSource, double ctiS = 0.3);
    /// <summary>IEEE 1584-2002 arc-flash calculation.</summary>
    ArcFlashResult ArcFlash(ArcFlashInput input);
}

internal sealed class ProtectionService : IProtectionService
{
    public GradingResult GradeRadialFeeder(PowerNetwork network, IReadOnlyList<int> busPathFromSource, IReadOnlyList<OvercurrentRelay> relaysFromSource, double ctiS = 0.3) =>
        Coordination.GradeRadialFeeder(network, busPathFromSource, relaysFromSource, ctiS: ctiS);

    public ArcFlashResult ArcFlash(ArcFlashInput input) => Protection.ArcFlash.Calculate(input);
}

public static class ProtectionBuilderExtensions
{
    /// <summary>Registers <see cref="IProtectionService"/> (and the short-circuit module it depends on).</summary>
    public static ElectricalBuilder AddProtection(this ElectricalBuilder b)
    {
        b.AddShortCircuit();
        if (b.TryAddModule("Protection")) b.Services.TryAddSingleton<IProtectionService, ProtectionService>();
        return b;
    }
}
