using System.Numerics;
using Electrical.Core.Units;

namespace Electrical.ShortCircuit;

/// <summary>Fault (short-circuit) type.</summary>
public enum FaultType
{
    /// <summary>Balanced three-phase fault (I″k3).</summary>
    ThreePhase,
    /// <summary>Single-line-to-ground fault on phase a (I″k1).</summary>
    SingleLineToGround,
    /// <summary>Line-to-line fault between phases b and c (I″k2).</summary>
    LineToLine,
    /// <summary>Double-line-to-ground fault on phases b and c (I″k2E / I″kE2E).</summary>
    DoubleLineToGround,
}

/// <summary>Method used to obtain R/X for the peak factor κ (IEC 60909-0 §8.1).</summary>
public enum PeakCurrentMethod
{
    /// <summary>Method A — uniform ratio R/X: smallest R/X of all network branches and sources.</summary>
    UniformRatio,
    /// <summary>Method B — R/X of the short-circuit impedance at the fault location, κ multiplied by 1.15 (capped at 1.8 LV / 2.0 HV).</summary>
    ImpedanceAtFault,
    /// <summary>Method C — equivalent frequency f_c = 20 Hz (50 Hz systems) / 24 Hz (60 Hz systems). Most accurate for meshed networks.</summary>
    EquivalentFrequency,
}

/// <summary>External network feeder (network infeed) characterised by its initial symmetrical short-circuit power.</summary>
public sealed class ExternalGrid
{
    /// <summary>Connection bus id.</summary>
    public int Bus { get; set; }
    public string? Name { get; set; }
    /// <summary>Initial symmetrical three-phase short-circuit power S″kQ at the connection point [MVA].</summary>
    public double SkMva { get; set; } = 1000;
    /// <summary>R/X ratio of the feeder impedance (IEC: 0.1 when unknown).</summary>
    public double RxRatio { get; set; } = 0.1;
    /// <summary>Voltage factor c_Q used to derive the impedance Z_Q = c_Q·U_nQ² / S″kQ.</summary>
    public double C { get; set; } = 1.1;
    /// <summary>Ratio Z0/Z1 of the feeder; NaN = no zero-sequence path (isolated/ungrounded infeed).</summary>
    public double Z0OverZ1 { get; set; } = 1.0;
}

/// <summary>Options for an IEC 60909 short-circuit calculation.</summary>
public sealed record ShortCircuitOptions
{
    /// <summary>true = maximum short-circuit currents (c_max), false = minimum (c_min).</summary>
    public bool Maximum { get; init; } = true;
    /// <summary>Overrides the voltage factor c (otherwise taken from IEC 60909-0 Table 1 by bus voltage).</summary>
    public double? VoltageFactorOverride { get; init; }
    public LowVoltageTolerance LowVoltageTolerance { get; init; } = LowVoltageTolerance.SixPercent;
    /// <summary>Peak-current method (default: equivalent frequency, method C).</summary>
    public PeakCurrentMethod PeakMethod { get; init; } = PeakCurrentMethod.EquivalentFrequency;
    /// <summary>Apply the generator impedance correction factor K_G (IEC 60909-0 eq. 18).</summary>
    public bool ApplyGeneratorCorrection { get; init; }
    /// <summary>Rated power factor cos φ_rG used for K_G.</summary>
    public double GeneratorRatedPowerFactor { get; init; } = 0.85;
    /// <summary>Generator neutrals solidly grounded (zero-sequence reactance X0 connected); false = no zero-sequence path through generators.</summary>
    public bool GeneratorNeutralGrounded { get; init; } = true;
    /// <summary>External grid infeeds. Generators located at a bus with an external grid are ignored (the grid model replaces them).</summary>
    public IReadOnlyList<ExternalGrid> ExternalGrids { get; init; } = [];
    /// <summary>Include line charging and bus shunts (IEC 60909 neglects them: default false).</summary>
    public bool IncludeShunts { get; init; }
    /// <summary>Use transformer off-nominal taps and phase shifts (IEC 60909 uses rated ratios: default false).</summary>
    public bool UseTransformerTaps { get; init; }
    /// <summary>Minimum time delay t_min [s] for the breaking current and DC component.</summary>
    public double MinimumTimeDelayS { get; init; } = 0.1;
    /// <summary>Short-circuit duration T_k [s] for the thermal equivalent current I_th.</summary>
    public double FaultDurationS { get; init; } = 1.0;
    /// <summary>Fault impedance Z_f [Ω] (per phase; for ground faults 3·Z_f appears in the zero-sequence loop).</summary>
    public Complex FaultImpedanceOhm { get; init; }

    public static ShortCircuitOptions Default { get; } = new();
}

/// <summary>Phase-voltage magnitudes at a bus during the fault [pu of nominal phase-to-neutral voltage].</summary>
public sealed record BusFaultVoltage(int BusId, double BaseKv, double Va, double Vb, double Vc)
{
    /// <summary>Phase-to-neutral voltage of phase a [kV].</summary>
    public double VaKv => Va * BaseKv / ElectricalFormulas.Sqrt3;
    public double VbKv => Vb * BaseKv / ElectricalFormulas.Sqrt3;
    public double VcKv => Vc * BaseKv / ElectricalFormulas.Sqrt3;
    public double Min => Math.Min(Va, Math.Min(Vb, Vc));
}

/// <summary>Fault current through a branch, measured at its from end (positive = flowing from <see cref="From"/> towards <see cref="To"/>) [kA at the from-bus voltage].</summary>
public sealed record BranchFaultCurrent(int Index, int From, int To, string? Name, double IaKa, double IbKa, double IcKa, double ResidualKa)
{
    /// <summary>Largest phase current [kA].</summary>
    public double MaxKa => Math.Max(IaKa, Math.Max(IbKa, IcKa));
}

/// <summary>Short-circuit current contributed by a source (generator or external grid) [kA at its bus voltage].</summary>
public sealed record SourceContribution(string Name, int BusId, bool IsExternalGrid, double IaKa, double IbKa, double IcKa)
{
    public double MaxKa => Math.Max(IaKa, Math.Max(IbKa, IcKa));
}

/// <summary>Result of one fault calculation. Currents in kA (RMS unless stated), impedances in pu on the system base.</summary>
public sealed class FaultResult
{
    public required int BusId { get; init; }
    public required FaultType Type { get; init; }
    /// <summary>Nominal voltage of the faulted bus [kV].</summary>
    public required double BaseKv { get; init; }
    /// <summary>Voltage factor c used for the equivalent source c·U_n/√3.</summary>
    public required double C { get; init; }
    /// <summary>Thevenin sequence impedances at the fault location [pu].</summary>
    public required Complex Z1 { get; init; }
    public required Complex Z2 { get; init; }
    public required Complex Z0 { get; init; }
    /// <summary>Sequence fault currents (phase a reference) [pu].</summary>
    public required SequenceSet SequenceCurrentsPu { get; init; }
    /// <summary>Phase fault-current phasors [kA].</summary>
    public required ThreePhase PhaseCurrentsKa { get; init; }
    /// <summary>Initial symmetrical short-circuit current I″k: largest faulted-phase current [kA].</summary>
    public required double IkKa { get; init; }
    /// <summary>Earth (residual) current |3·I0| [kA] (I″kE2E for double-line-to-ground faults).</summary>
    public required double EarthCurrentKa { get; init; }
    /// <summary>R/X ratio used for κ (after the method's frequency correction).</summary>
    public required double RxRatio { get; init; }
    /// <summary>Peak factor κ.</summary>
    public required double Kappa { get; init; }
    /// <summary>Peak short-circuit current i_p = κ·√2·I″k [kA peak].</summary>
    public required double IpKa { get; init; }
    /// <summary>Symmetrical breaking current I_b [kA] (far-from-generator: I_b = I″k).</summary>
    public required double IbKa { get; init; }
    /// <summary>DC component at t_min [kA].</summary>
    public required double IdcKa { get; init; }
    /// <summary>Asymmetrical breaking current √(I_b² + i_DC²) [kA].</summary>
    public required double IbAsymKa { get; init; }
    /// <summary>Thermal equivalent short-circuit current I_th for T_k [kA].</summary>
    public required double IthKa { get; init; }
    /// <summary>Bus voltages during the fault.</summary>
    public required IReadOnlyList<BusFaultVoltage> Voltages { get; init; }
    /// <summary>Branch fault currents (out-of-service branches omitted).</summary>
    public required IReadOnlyList<BranchFaultCurrent> Branches { get; init; }
    /// <summary>Source contributions.</summary>
    public required IReadOnlyList<SourceContribution> Sources { get; init; }

    /// <summary>Initial symmetrical short-circuit power S″k = √3·U_n·I″k [MVA].</summary>
    public double SkMva => ElectricalFormulas.Sqrt3 * BaseKv * IkKa;

    public BusFaultVoltage Voltage(int busId) => Voltages.First(v => v.BusId == busId);

    public override string ToString() =>
        $"{Type} fault at bus {BusId} ({BaseKv} kV): I\"k = {IkKa:0.###} kA, ip = {IpKa:0.###} kA, Ib = {IbKa:0.###} kA, Ith = {IthKa:0.###} kA, S\"k = {SkMva:0.#} MVA";
}

/// <summary>One row of an all-bus short-circuit scan.</summary>
public sealed record BusShortCircuitSummary(
    int BusId, string? Name, double BaseKv, double Ik3Ka, double Ik2Ka, double Ik1Ka, double Ik2EKa, double IpKa, double Sk3Mva, double RxRatio,
    Complex Z1, Complex Z0);
