using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agritech.Core.Hosting;

/// <summary>Fluent builder used by modules to register themselves: <c>services.AddAgritech(b =&gt; b.AddCrops().AddWeather())</c>.</summary>
public sealed class AgritechBuilder
{
    private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);

    internal AgritechBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }
    public IReadOnlyCollection<string> Modules => _modules;

    /// <summary>Registers a module once; returns false if it was already registered.</summary>
    public bool TryAddModule(string name) => _modules.Add(name);
}

/// <summary>Registry of loaded modules, resolvable from DI.</summary>
public sealed class AgritechModuleRegistry(IEnumerable<string> modules)
{
    public IReadOnlyList<string> Modules { get; } = modules.ToList();
}

public static class AgritechServiceCollectionExtensions
{
    public static IServiceCollection AddAgritech(this IServiceCollection services, Action<AgritechBuilder>? configure = null)
    {
        var builder = new AgritechBuilder(services);
        builder.TryAddModule("Core");
        services.TryAddSingleton<IAgriEventBus, InMemoryEventBus>();
        services.TryAddSingleton(TimeProvider.System);
        configure?.Invoke(builder);
        services.AddSingleton(new AgritechModuleRegistry(builder.Modules));
        return services;
    }
}
