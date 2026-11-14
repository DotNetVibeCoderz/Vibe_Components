using Electrical.Core.Hosting;
using Electrical.SCADA.Dnp3;
using Electrical.SCADA.Iec104;
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

    /// <summary>
    /// Registers DNP3 outstation building blocks (after <see cref="AddScada"/>): <see cref="Dnp3OutstationOptions"/>, a
    /// <see cref="Dnp3PointMap"/> and a <see cref="Dnp3Database"/> built from that map and the <see cref="TagDatabase"/> (populate the map
    /// before the database is first resolved). Create the <see cref="Dnp3Outstation"/> with the port of your choice.
    /// </summary>
    public static ElectricalBuilder AddDnp3(this ElectricalBuilder b, Dnp3OutstationOptions? options = null, int eventBufferSize = 200)
    {
        b.AddScada();
        if (!b.TryAddModule("DNP3")) return b;
        b.Services.TryAddSingleton(options ?? new Dnp3OutstationOptions());
        b.Services.TryAddSingleton<Dnp3PointMap>();
        b.Services.TryAddSingleton(sp => sp.GetRequiredService<Dnp3PointMap>().CreateDatabase(sp.GetRequiredService<TagDatabase>(), eventBufferSize));
        return b;
    }

    /// <summary>
    /// Registers IEC 60870-5-104 building blocks (after <see cref="AddScada"/>): <see cref="Iec104ServerOptions"/> and an
    /// <see cref="Iec104PointMap"/>. Create the <see cref="Iec104Server"/> (tags + map) with the port of your choice.
    /// </summary>
    public static ElectricalBuilder AddIec104(this ElectricalBuilder b, Iec104ServerOptions? options = null)
    {
        b.AddScada();
        if (!b.TryAddModule("IEC104")) return b;
        b.Services.TryAddSingleton(options ?? new Iec104ServerOptions());
        b.Services.TryAddSingleton<Iec104PointMap>();
        return b;
    }
}
