using Electrical.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Pmu;

/// <summary>Options registered by <see cref="PmuBuilderExtensions.AddPmu"/>.</summary>
public sealed record PmuOptions
{
    /// <summary>Settings of the <see cref="PhasorEstimator"/> singleton.</summary>
    public PhasorEstimatorOptions Estimator { get; init; } = new();
    /// <summary>Stream IDCODEs aligned by the <see cref="PdcAlignmentBuffer"/> singleton (empty = no buffer registered).</summary>
    public IReadOnlyList<ushort> PdcStreams { get; init; } = [];
    /// <summary>PDC wait time for late frames.</summary>
    public TimeSpan PdcWaitTime { get; init; } = TimeSpan.FromMilliseconds(100);
}

public static class PmuBuilderExtensions
{
    /// <summary>
    /// Registers the synchrophasor services: <see cref="PmuOptions"/>, a <see cref="PhasorEstimator"/> and, when
    /// <see cref="PmuOptions.PdcStreams"/> is not empty, a <see cref="PdcAlignmentBuffer"/> (all singletons).
    /// </summary>
    public static ElectricalBuilder AddPmu(this ElectricalBuilder b, PmuOptions? options = null)
    {
        if (!b.TryAddModule("PMU")) return b;
        var o = options ?? new PmuOptions();
        b.Services.TryAddSingleton(o);
        b.Services.TryAddSingleton(sp => new PhasorEstimator(sp.GetRequiredService<PmuOptions>().Estimator));
        if (o.PdcStreams.Count > 0)
            b.Services.TryAddSingleton(sp => { var po = sp.GetRequiredService<PmuOptions>(); return new PdcAlignmentBuffer(po.PdcStreams, po.PdcWaitTime); });
        return b;
    }
}
