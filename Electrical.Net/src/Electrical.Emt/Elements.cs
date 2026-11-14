namespace Electrical.Emt;

/// <summary>Base type of EMT network elements. Node names "0", "gnd" and "ground" (case-insensitive) denote the reference.</summary>
public abstract record EmtElement(string Name);

/// <summary>
/// Series R-L-C branch between <paramref name="From"/> and <paramref name="To"/>: R (Ω), L (H), C (F). L = 0 removes the
/// inductor, C = 0 removes the capacitor (a short, i.e. infinite capacitance), so the record covers R, L, C, RL, RC and RLC branches.
/// Positive current flows From → To through the branch. Initial conditions: inductor current (A) and capacitor voltage (V, From side positive).
/// </summary>
public sealed record RlcBranch(string Name, string From, string To, double R, double L, double C,
    double InitialCurrent = 0, double InitialCapacitorVoltage = 0) : EmtElement(Name)
{
    internal bool IsResistor => L == 0 && C == 0;
}

/// <summary>
/// Ideal (two-state resistive) switch: R_on (Ω) when closed, R_off (Ω) when open. Time-controlled: it closes at
/// <paramref name="CloseTime"/> and opens at <paramref name="OpenTime"/> (s, +∞ = never). With <paramref name="OpenAtCurrentZero"/>
/// an open command is executed at the first current zero (sign change) after <paramref name="OpenTime"/>, like a circuit breaker.
/// </summary>
public sealed record EmtSwitch(string Name, string A, string B, bool InitiallyClosed = false,
    double CloseTime = double.PositiveInfinity, double OpenTime = double.PositiveInfinity, bool OpenAtCurrentZero = false,
    double Ron = 1e-5, double Roff = 1e7) : EmtElement(Name);

/// <summary>
/// Ideal diode as a two-state switch (R_on/R_off in Ω): turns on when the anode–cathode voltage becomes positive and off when its
/// current becomes negative. A <paramref name="Gate"/> function turns it into a thyristor: it can only turn on while the gate is true.
/// </summary>
public sealed record EmtDiode(string Name, string Anode, string Cathode, double Ron = 1e-3, double Roff = 1e7,
    Func<double, bool>? Gate = null) : EmtElement(Name);

/// <summary>
/// Voltage source from <paramref name="Node"/> to ground with optional series internal impedance R (Ω) + L (H).
/// With R = L = 0 the node voltage is imposed (ideal source, handled by node partitioning).
/// </summary>
public sealed record EmtVoltageSource(string Name, string Node, EmtWaveform Waveform, double R = 0, double L = 0) : EmtElement(Name);

/// <summary>Current source (A) driving current from <paramref name="From"/> through the source into <paramref name="To"/>.</summary>
public sealed record EmtCurrentSource(string Name, string From, string To, EmtWaveform Waveform) : EmtElement(Name);

/// <summary>
/// Multiphase coupled series R-L branch (n×n matrices R in Ω, L in H) between terminal sets <paramref name="From"/> and <paramref name="To"/>.
/// Used for the series part of π sections, mutually coupled reactors and source impedances.
/// </summary>
public sealed record CoupledRlBranch(string Name, string[] From, string[] To, double[,] R, double[,] L) : EmtElement(Name);

/// <summary>Multiphase shunt capacitance matrix C (F, n×n Maxwell form) from <paramref name="Nodes"/> to ground.</summary>
public sealed record ShuntCapacitance(string Name, string[] Nodes, double[,] C) : EmtElement(Name);

/// <summary>
/// Single-phase two-winding transformer: ideal ratio <paramref name="Ratio"/> = V_primary / V_secondary in series with a leakage
/// R (Ω) + L (H) referred to the secondary. Primary terminals P1–P2, secondary S1–S2 (dot on P1 and S1). Magnetizing branches can be
/// added as separate <see cref="RlcBranch"/> elements. L must be positive or R positive.
/// </summary>
public sealed record EmtTransformer(string Name, string P1, string P2, string S1, string S2, double Ratio, double R, double L) : EmtElement(Name);

/// <summary>Modal parameters of a travelling-wave (Bergeron) line mode: surge impedance Z_c (Ω), travel time τ (s), total series resistance R (Ω).</summary>
public readonly record struct BergeronMode(double SurgeImpedance, double TravelTime, double Resistance = 0);

/// <summary>
/// Bergeron travelling-wave line (lossless propagation with the lumped-loss approximation R/4 – R/2 – R/4 per mode) between terminal sets
/// <paramref name="From"/> (k end) and <paramref name="To"/> (m end). Phase quantities are related to modal ones by the real, orthonormal
/// transformation <paramref name="Transformation"/> (v_phase = T·v_mode); for transposed three-phase lines this is the Clarke matrix.
/// </summary>
public sealed record BergeronLine(string Name, string[] From, string[] To, BergeronMode[] Modes, double[,] Transformation) : EmtElement(Name)
{
    /// <summary>Single-phase line from per-length parameters (Ω/km, H/km, F/km) and length (km).</summary>
    public static BergeronLine SinglePhase(string name, string from, string to, double rPerKm, double lPerKm, double cPerKm, double lengthKm) =>
        new(name, [from], [to], [Mode(rPerKm, lPerKm, cPerKm, lengthKm)], new double[,] { { 1 } });

    /// <summary>Single-phase line from surge impedance Z_c (Ω), travel time τ (s) and total resistance R (Ω).</summary>
    public static BergeronLine SinglePhase(string name, string from, string to, double surgeImpedance, double travelTime, double resistance = 0) =>
        new(name, [from], [to], [new BergeronMode(surgeImpedance, travelTime, resistance)], new double[,] { { 1 } });

    /// <summary>
    /// Transposed three-phase line from positive- (R1, L1, C1) and zero-sequence (R0, L0, C0) per-km data (Ω/km, H/km, F/km) and length (km).
    /// Modes: ground (zero sequence) and two aerial modes (positive sequence) via the power-invariant Clarke transformation.
    /// </summary>
    public static BergeronLine ThreePhaseTransposed(string name, string[] from, string[] to,
        double r1PerKm, double l1PerKm, double c1PerKm, double r0PerKm, double l0PerKm, double c0PerKm, double lengthKm)
    {
        if (from.Length != 3 || to.Length != 3) throw new ArgumentException("A three-phase line needs three terminals at each end.");
        var aerial = Mode(r1PerKm, l1PerKm, c1PerKm, lengthKm);
        return new(name, from, to, [Mode(r0PerKm, l0PerKm, c0PerKm, lengthKm), aerial, aerial], Clarke());
    }

    /// <summary>Mode from per-length data: Z_c = √(L/C), τ = ℓ·√(L·C), R = R'·ℓ.</summary>
    public static BergeronMode Mode(double rPerKm, double lPerKm, double cPerKm, double lengthKm)
    {
        if (lPerKm <= 0 || cPerKm <= 0 || lengthKm <= 0) throw new ArgumentException("L, C and length must be positive.");
        return new(Math.Sqrt(lPerKm / cPerKm), lengthKm * Math.Sqrt(lPerKm * cPerKm), rPerKm * lengthKm);
    }

    /// <summary>Power-invariant Clarke matrix (columns: 0, α, β modes); orthonormal so T⁻¹ = Tᵀ.</summary>
    public static double[,] Clarke()
    {
        double a = 1 / Math.Sqrt(3), b = 1 / Math.Sqrt(6), c = 1 / Math.Sqrt(2);
        return new double[,] { { a, 2 * b, 0 }, { a, -b, c }, { a, -b, -c } };
    }
}
