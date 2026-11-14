using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Units;

namespace Electrical.Protection;

/// <summary>A mho (circular, through-origin) distance zone. Reach in Ω (primary unless stated) at the maximum torque angle.</summary>
public sealed record DistanceZone(int Zone, Complex ReachOhm, double DelayS)
{
    public double ReachMagnitudeOhm => ReachOhm.Magnitude;
    /// <summary>Maximum torque (characteristic) angle [degrees].</summary>
    public double AngleDeg => ReachOhm.AngleDeg();

    /// <summary>True when <paramref name="apparentImpedanceOhm"/> lies inside the mho circle |Z − Zr/2| ≤ |Zr|/2.</summary>
    public bool Contains(Complex apparentImpedanceOhm) =>
        (apparentImpedanceOhm - ReachOhm / 2).Magnitude <= ReachOhm.Magnitude / 2 + 1e-12;

    /// <summary>Points of the mho circle (R, X) [Ω] for plotting on the R–X plane.</summary>
    public IReadOnlyList<(double R, double X)> Circle(int points = 72)
    {
        var c = ReachOhm / 2; double r = ReachOhm.Magnitude / 2;
        return Enumerable.Range(0, points + 1).Select(k =>
        {
            double a = 2 * Math.PI * k / points;
            return (c.Real + r * Math.Cos(a), c.Imaginary + r * Math.Sin(a));
        }).ToList();
    }
}

/// <summary>Three-zone mho distance relay (ANSI 21).</summary>
public sealed class DistanceRelay
{
    public DistanceRelay(string name, Complex lineImpedanceOhm, IReadOnlyList<DistanceZone> zones)
    {
        Name = name; LineImpedanceOhm = lineImpedanceOhm; Zones = zones;
    }

    public string Name { get; }
    /// <summary>Positive-sequence impedance of the protected line [Ω].</summary>
    public Complex LineImpedanceOhm { get; }
    public IReadOnlyList<DistanceZone> Zones { get; }

    /// <summary>
    /// Conventional mho settings: Z1 = 80 % of the line; Z2 = max(120 % of the line, line + 50 % of the shortest adjacent line);
    /// Z3 = 120 % of (line + longest adjacent line) (or 200 % of the line when no adjacent line is given).
    /// </summary>
    /// <param name="lineOhm">Protected line positive-sequence impedance [Ω].</param>
    /// <param name="shortestNextLineOhm">Shortest line leaving the remote bus [Ω].</param>
    /// <param name="longestNextLineOhm">Longest line leaving the remote bus [Ω].</param>
    public static DistanceRelay Mho(string name, Complex lineOhm, Complex? shortestNextLineOhm = null, Complex? longestNextLineOhm = null,
        double zone1Fraction = 0.8, double zone2Fraction = 1.2, double zone2DelayS = 0.3, double zone3DelayS = 0.8)
    {
        var z1 = zone1Fraction * lineOhm;
        var z2 = zone2Fraction * lineOhm;
        if (shortestNextLineOhm is Complex s && (lineOhm + 0.5 * s).Magnitude > z2.Magnitude) z2 = lineOhm + 0.5 * s;
        var z3 = longestNextLineOhm is Complex l ? 1.2 * (lineOhm + l) : 2.0 * lineOhm;
        if (z3.Magnitude < z2.Magnitude * 1.1) z3 = z2 * 1.2;
        return new DistanceRelay(name, lineOhm, [new DistanceZone(1, z1, 0), new DistanceZone(2, z2, zone2DelayS), new DistanceZone(3, z3, zone3DelayS)]);
    }

    /// <summary>Settings for a relay at the from end of branch <paramref name="branchIndex"/>, using adjacent lines at the remote bus.</summary>
    public static DistanceRelay ForBranch(PowerNetwork network, int branchIndex, double zone2DelayS = 0.3, double zone3DelayS = 0.8)
    {
        var br = network.Branches[branchIndex];
        double zb = new PerUnitBase(network.BaseMva, network.GetBus(br.From).BaseKv).BaseOhm;
        var zl = new Complex(br.R, br.X) * zb;
        var next = network.Branches.Where((b, k) => k != branchIndex && b.InService && (b.From == br.To || b.To == br.To))
            .Select(b => new Complex(b.R, b.X) * new PerUnitBase(network.BaseMva, network.GetBus(br.To).BaseKv).BaseOhm)
            .OrderBy(z => z.Magnitude).ToList();
        return Mho(br.Name ?? $"21 {br.From}-{br.To}", zl, next.Count > 0 ? next[0] : null, next.Count > 0 ? next[^1] : null,
            zone2DelayS: zone2DelayS, zone3DelayS: zone3DelayS);
    }

    /// <summary>Fastest zone containing the apparent impedance, or null when outside all zones.</summary>
    public DistanceZone? OperatingZone(Complex apparentImpedanceOhm) =>
        Zones.Where(z => z.Contains(apparentImpedanceOhm)).OrderBy(z => z.DelayS).FirstOrDefault();

    /// <summary>Tripping delay [s] for an apparent impedance (∞ when outside all zones).</summary>
    public double OperatingTime(Complex apparentImpedanceOhm) => OperatingZone(apparentImpedanceOhm)?.DelayS ?? double.PositiveInfinity;

    /// <summary>Residual compensation factor k0 = (Z0 − Z1) / (3·Z1).</summary>
    public static Complex ResidualCompensation(Complex z1, Complex z0) => (z0 - z1) / (3 * z1);

    /// <summary>Phase-to-ground loop impedance Z = V_a / (I_a + k0·3·I0) [Ω for V in V and I in A, or any consistent units].</summary>
    public static Complex ApparentImpedanceGround(Complex va, Complex ia, Complex residualCurrent3I0, Complex k0) => va / (ia + k0 * residualCurrent3I0);

    /// <summary>Phase-to-phase loop impedance Z = (V_b − V_c) / (I_b − I_c).</summary>
    public static Complex ApparentImpedancePhase(Complex vb, Complex vc, Complex ib, Complex ic) => (vb - vc) / (ib - ic);

    /// <summary>Converts a primary impedance to relay secondary ohms: Z_sec = Z_pri · CT ratio / VT ratio.</summary>
    public static Complex ToSecondary(Complex primaryOhm, double ctRatio, double vtRatio) => primaryOhm * ctRatio / vtRatio;
}

/// <summary>Outcome of a differential relay evaluation (currents in pu of the relay reference current).</summary>
public sealed record DifferentialDecision(double DifferentialPu, double RestraintPu, double ThresholdPu, bool Trip, bool Unrestrained, bool HarmonicBlocked);

/// <summary>
/// Dual-slope percentage-biased differential relay (ANSI 87T/87G). Operating current I_d = |ΣI|, restraint I_r = (Σ|I|)/2;
/// trips when I_d exceeds max(I_min, slope1·I_r) up to the breakpoint, then slope 2 above it; unrestrained stage above <see cref="UnrestrainedPu"/>.
/// </summary>
public sealed class PercentDifferentialRelay
{
    /// <summary>Minimum operating current [pu].</summary>
    public double MinimumPickupPu { get; set; } = 0.3;
    /// <summary>Slope 1 (fraction, e.g. 0.25 = 25 %).</summary>
    public double Slope1 { get; set; } = 0.25;
    /// <summary>Slope 2 (fraction).</summary>
    public double Slope2 { get; set; } = 0.5;
    /// <summary>Restraint current where slope 2 starts [pu].</summary>
    public double BreakpointPu { get; set; } = 3.0;
    /// <summary>Unrestrained (instantaneous) differential pickup [pu].</summary>
    public double UnrestrainedPu { get; set; } = 8.0;
    /// <summary>Second-harmonic blocking ratio I2/I1 (inrush), e.g. 0.15.</summary>
    public double SecondHarmonicBlock { get; set; } = 0.15;

    /// <summary>Operating threshold [pu] at restraint current <paramref name="restraintPu"/>.</summary>
    public double Threshold(double restraintPu)
    {
        double t = restraintPu <= BreakpointPu ? Slope1 * restraintPu : Slope1 * BreakpointPu + Slope2 * (restraintPu - BreakpointPu);
        return Math.Max(MinimumPickupPu, t);
    }

    /// <summary>Evaluates terminal currents (all measured into the protected zone, already ratio- and vector-group-compensated) [pu].</summary>
    /// <param name="secondHarmonicRatio">Measured I(2f)/I(f) in the differential current (0 when unknown).</param>
    public DifferentialDecision Evaluate(IReadOnlyList<Complex> terminalCurrentsPu, double secondHarmonicRatio = 0)
    {
        Complex sum = Complex.Zero; double mag = 0;
        foreach (var i in terminalCurrentsPu) { sum += i; mag += i.Magnitude; }
        double id = sum.Magnitude, ir = mag / 2, th = Threshold(ir);
        bool unrestrained = id >= UnrestrainedPu;
        bool blocked = secondHarmonicRatio >= SecondHarmonicBlock && !unrestrained;
        return new DifferentialDecision(id, ir, th, unrestrained || (id > th && !blocked), unrestrained, blocked && id > th);
    }

    public DifferentialDecision Evaluate(Complex i1Pu, Complex i2Pu, double secondHarmonicRatio = 0) => Evaluate([i1Pu, i2Pu], secondHarmonicRatio);

    /// <summary>Per-unit relay current for a primary current [A] given the winding rated current [A] (I_n = S / (√3·U)).</summary>
    public static Complex ToPerUnit(Complex primaryCurrentA, double ratedMva, double windingKv) =>
        primaryCurrentA / (ratedMva / (Math.Sqrt(3) * windingKv) * 1000);
}
