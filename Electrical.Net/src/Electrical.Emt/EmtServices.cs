using Electrical.Core.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Emt;

/// <summary>EMT simulation service resolvable from DI.</summary>
public interface IEmtService
{
    /// <summary>Runs an electromagnetic-transients simulation (SI units: V, A, Ω, H, F, s).</summary>
    EmtResult Simulate(EmtNetwork network, EmtOptions? options = null);
}

internal sealed class EmtService : IEmtService
{
    public EmtResult Simulate(EmtNetwork network, EmtOptions? options = null) => EmtSimulation.Simulate(network, options);
}

public static class EmtBuilderExtensions
{
    /// <summary>Registers <see cref="IEmtService"/> (module "Emt").</summary>
    public static ElectricalBuilder AddEmt(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Emt")) b.Services.TryAddSingleton<IEmtService, EmtService>();
        return b;
    }
}
