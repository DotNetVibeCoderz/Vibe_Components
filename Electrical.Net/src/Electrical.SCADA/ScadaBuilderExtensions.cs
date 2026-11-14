using Electrical.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.SCADA;

public static class ScadaBuilderExtensions
{
    /// <summary>
    /// Registers the SCADA services as singletons: <see cref="TagDatabase"/>, <see cref="AlarmManager"/> and <see cref="Historian"/>
    /// (sharing the <see cref="IGridEventBus"/> and <see cref="TimeProvider"/>). The alarm manager and historian are attached to the tag database.
    /// </summary>
    public static ElectricalBuilder AddScada(this ElectricalBuilder b, HistorianTagOptions? historianDefaults = null)
    {
        if (!b.TryAddModule("SCADA")) return b;
        b.Services.TryAddSingleton(sp => new TagDatabase(sp.GetService<IGridEventBus>(), sp.GetService<TimeProvider>()));
        b.Services.TryAddSingleton(sp =>
        {
            var m = new AlarmManager(sp.GetService<IGridEventBus>(), sp.GetService<TimeProvider>());
            m.Attach(sp.GetRequiredService<TagDatabase>());
            return m;
        });
        b.Services.TryAddSingleton(sp =>
        {
            var h = new Historian(historianDefaults);
            h.Attach(sp.GetRequiredService<TagDatabase>());
            return h;
        });
        return b;
    }
}
