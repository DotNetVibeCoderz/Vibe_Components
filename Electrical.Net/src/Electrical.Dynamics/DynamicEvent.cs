using System.Numerics;
using Electrical.Stability;

namespace Electrical.Dynamics;

/// <summary>Kind of a dynamic-simulation event.</summary>
public enum DynamicEventKind
{
    /// <summary>Three-phase fault at a bus through an impedance.</summary>
    BusFault,
    /// <summary>Removes the fault at a bus.</summary>
    ClearFault,
    /// <summary>Opens a branch.</summary>
    TripBranch,
    /// <summary>Closes a branch.</summary>
    CloseBranch,
    /// <summary>Disconnects a generator's dynamic unit.</summary>
    TripGenerator,
    /// <summary>Multiplies the static load at a bus.</summary>
    LoadScale,
    /// <summary>Adds ΔP/ΔQ to the static load at a bus (with the bus' load characteristic).</summary>
    LoadStep,
    /// <summary>Changes a controller reference (Vref, Pref, Qref, Efd, Pm, …).</summary>
    ReferenceStep,
}

/// <summary>A switching or set-point event at <see cref="TimeS"/> [s].</summary>
public sealed record DynamicEvent(double TimeS, DynamicEventKind Kind)
{
    /// <summary>Bus id (faults, load events).</summary>
    public int BusId { get; init; }
    /// <summary>Branch index (<c>PowerNetwork.Branches</c>) or generator index (<c>PowerNetwork.Generators</c>).</summary>
    public int Index { get; init; } = -1;
    /// <summary>Fault impedance [pu on the system base]; zero = bolted (1e-5 pu).</summary>
    public Complex Impedance { get; init; }
    /// <summary>Load scale factor, or new / incremental reference value [pu].</summary>
    public double Value { get; init; } = 1;
    public double DeltaPMw { get; init; }
    public double DeltaQMvar { get; init; }
    /// <summary>Reference name for <see cref="DynamicEventKind.ReferenceStep"/>.</summary>
    public string? Reference { get; init; }
    /// <summary>True when <see cref="Value"/> is added to the current reference.</summary>
    public bool Relative { get; init; }

    public static DynamicEvent Fault(double t, int busId, Complex? impedancePu = null) => new(t, DynamicEventKind.BusFault) { BusId = busId, Impedance = impedancePu ?? Complex.Zero };
    public static DynamicEvent Clear(double t, int busId) => new(t, DynamicEventKind.ClearFault) { BusId = busId };
    public static DynamicEvent TripBranch(double t, int branchIndex) => new(t, DynamicEventKind.TripBranch) { Index = branchIndex };
    public static DynamicEvent CloseBranch(double t, int branchIndex) => new(t, DynamicEventKind.CloseBranch) { Index = branchIndex };
    public static DynamicEvent TripGenerator(double t, int generatorIndex) => new(t, DynamicEventKind.TripGenerator) { Index = generatorIndex };
    public static DynamicEvent ScaleLoad(double t, int busId, double factor) => new(t, DynamicEventKind.LoadScale) { BusId = busId, Value = factor };
    /// <summary>Load step of ΔP [MW] and ΔQ [Mvar] at a bus.</summary>
    public static DynamicEvent LoadStep(double t, int busId, double deltaPMw, double deltaQMvar = 0) =>
        new(t, DynamicEventKind.LoadStep) { BusId = busId, DeltaPMw = deltaPMw, DeltaQMvar = deltaQMvar };
    /// <summary>Reference step on the unit of a generator (e.g. "Vref" +0.05 pu with <paramref name="relative"/> = true).</summary>
    public static DynamicEvent ReferenceStep(double t, int generatorIndex, string reference, double value, bool relative = true) =>
        new(t, DynamicEventKind.ReferenceStep) { Index = generatorIndex, Reference = reference, Value = value, Relative = relative };

    /// <summary>Converts a classical-stability event.</summary>
    public static DynamicEvent From(StabilityEvent e) => e.Kind switch
    {
        StabilityEventKind.ApplyFault => Fault(e.TimeS, e.BusId, e.FaultImpedancePu),
        StabilityEventKind.ClearFault => Clear(e.TimeS, e.BusId),
        StabilityEventKind.TripBranch => TripBranch(e.TimeS, e.Index),
        StabilityEventKind.CloseBranch => CloseBranch(e.TimeS, e.Index),
        StabilityEventKind.TripGenerator => TripGenerator(e.TimeS, e.Index),
        StabilityEventKind.ScaleLoad => ScaleLoad(e.TimeS, e.BusId, e.Value),
        _ => throw new NotSupportedException(e.Kind.ToString()),
    };
}
