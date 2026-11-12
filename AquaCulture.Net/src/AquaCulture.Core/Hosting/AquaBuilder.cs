using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AquaCulture.Core.Hosting;

/// <summary>
/// Fluent registration of AquaCulture.Net modules: <c>services.AddAquaCulture(b => b.AddWaterQuality().AddGrowth())</c>.
/// Each module provides an <c>AddX(this AquaBuilder)</c> extension that calls <see cref="TryAddModule"/> once.
/// </summary>
public sealed class AquaBuilder
{
    private readonly HashSet<string> _modules = new(StringComparer.Ordinal);

    internal AquaBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    public IReadOnlyCollection<string> Modules => _modules;

    /// <summary>Runs <paramref name="register"/> the first time a module name is seen.</summary>
    public AquaBuilder TryAddModule(string name, Action<IServiceCollection> register)
    {
        if (_modules.Add(name)) register(Services);
        return this;
    }
}

/// <summary>Registered module names, resolvable from DI for diagnostics.</summary>
public sealed record AquaModules(IReadOnlyCollection<string> Names);

public static class AquaServiceCollectionExtensions
{
    /// <summary>Registers the core services (event bus, clock) and any modules configured by <paramref name="configure"/>.</summary>
    public static IServiceCollection AddAquaCulture(this IServiceCollection services, Action<AquaBuilder>? configure = null)
    {
        var builder = new AquaBuilder(services);
        builder.TryAddModule("Core", s =>
        {
            s.TryAddSingleton<IAquaEventBus, InMemoryEventBus>();
            s.TryAddSingleton(TimeProvider.System);
        });
        configure?.Invoke(builder);
        services.TryAddSingleton(new AquaModules(builder.Modules));
        return services;
    }
}
