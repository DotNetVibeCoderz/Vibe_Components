using System.Numerics;
using Electrical.Core.Hosting;
using Electrical.Core.Units;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Circuit;

/// <summary>Solution of a three-phase load connected to a three-phase source (phasors in V, A, VA).</summary>
public sealed record ThreePhaseLoadResult(
    ThreePhase LineCurrents, ThreePhase LoadVoltages, Complex NeutralShiftVoltage, Complex NeutralCurrent, Complex TotalPower, ThreePhase PhasePowers)
{
    /// <summary>Current unbalance factor (negative/positive sequence) of the line currents.</summary>
    public double CurrentUnbalance => LineCurrents.UnbalanceFactor();
}

/// <summary>Balanced and unbalanced three-phase circuit helpers (Y/Δ conversion, power, Millman neutral shift).</summary>
public static class ThreePhaseCircuits
{
    /// <summary>Star (Y) → delta (Δ): Z_ab = (Z_aZ_b + Z_bZ_c + Z_cZ_a)/Z_c, etc.</summary>
    public static (Complex Zab, Complex Zbc, Complex Zca) StarToDelta(Complex za, Complex zb, Complex zc)
    {
        var s = za * zb + zb * zc + zc * za;
        return (s / zc, s / za, s / zb);
    }

    /// <summary>Delta (Δ) → star (Y): Z_a = Z_abZ_ca/ΣZ, Z_b = Z_abZ_bc/ΣZ, Z_c = Z_bcZ_ca/ΣZ.</summary>
    public static (Complex Za, Complex Zb, Complex Zc) DeltaToStar(Complex zab, Complex zbc, Complex zca)
    {
        var s = zab + zbc + zca;
        return (zab * zca / s, zab * zbc / s, zbc * zca / s);
    }

    /// <summary>Line-to-line voltages (V_ab, V_bc, V_ca) from phase-to-neutral voltages.</summary>
    public static ThreePhase LineVoltages(ThreePhase phaseVoltages) =>
        new(phaseVoltages.A - phaseVoltages.B, phaseVoltages.B - phaseVoltages.C, phaseVoltages.C - phaseVoltages.A);

    /// <summary>Total complex power S = Σ V·I* [VA] (phase-to-neutral voltages and line currents).</summary>
    public static Complex TotalPower(ThreePhase phaseVoltages, ThreePhase lineCurrents) =>
        Phasor.Power(phaseVoltages.A, lineCurrents.A) + Phasor.Power(phaseVoltages.B, lineCurrents.B) + Phasor.Power(phaseVoltages.C, lineCurrents.C);

    /// <summary>Balanced three-phase power S = √3·V_LL·I_L·(pf + j·sin φ) [VA for V, A]; lagging power factor gives positive Q.</summary>
    public static Complex BalancedPower(double lineVoltage, double lineCurrent, double powerFactor, bool lagging = true)
    {
        double s = Math.Sqrt(3) * lineVoltage * lineCurrent;
        double q = s * Math.Sqrt(Math.Max(0, 1 - powerFactor * powerFactor));
        return new Complex(s * powerFactor, lagging ? q : -q);
    }

    /// <summary>
    /// Star-connected load Z_a, Z_b, Z_c on a source with phase voltages <paramref name="sourcePhaseVoltages"/>. The load star point is
    /// connected to the source neutral through <paramref name="neutralImpedance"/> (0 = solid 4-wire, null = floating 3-wire).
    /// Uses Millman's theorem for the neutral shift.
    /// </summary>
    public static ThreePhaseLoadResult SolveStarLoad(ThreePhase sourcePhaseVoltages, Complex za, Complex zb, Complex zc, Complex? neutralImpedance = null)
    {
        var v = sourcePhaseVoltages;
        Complex vn;
        if (neutralImpedance is Complex zn && zn == Complex.Zero) vn = Complex.Zero;
        else
        {
            var yn = neutralImpedance is Complex z ? 1 / z : Complex.Zero;
            vn = (v.A / za + v.B / zb + v.C / zc) / (1 / za + 1 / zb + 1 / zc + yn);
        }
        var load = new ThreePhase(v.A - vn, v.B - vn, v.C - vn);
        var i = new ThreePhase(load.A / za, load.B / zb, load.C / zc);
        var sp = new ThreePhase(Phasor.Power(load.A, i.A), Phasor.Power(load.B, i.B), Phasor.Power(load.C, i.C));
        return new ThreePhaseLoadResult(i, load, vn, i.A + i.B + i.C, sp.A + sp.B + sp.C, sp);
    }

    /// <summary>Delta-connected load Z_ab, Z_bc, Z_ca; returns line currents and per-branch (ab, bc, ca) powers.</summary>
    public static ThreePhaseLoadResult SolveDeltaLoad(ThreePhase sourcePhaseVoltages, Complex zab, Complex zbc, Complex zca)
    {
        var vl = LineVoltages(sourcePhaseVoltages);
        Complex iab = vl.A / zab, ibc = vl.B / zbc, ica = vl.C / zca;
        var il = new ThreePhase(iab - ica, ibc - iab, ica - ibc);
        var sp = new ThreePhase(Phasor.Power(vl.A, iab), Phasor.Power(vl.B, ibc), Phasor.Power(vl.C, ica));
        return new ThreePhaseLoadResult(il, vl, Complex.Zero, Complex.Zero, sp.A + sp.B + sp.C, sp);
    }
}

/// <summary>Circuit-simulation service resolvable from DI.</summary>
public interface ICircuitService
{
    ElectricCircuit Parse(string netlist);
    DcResult OperatingPoint(ElectricCircuit circuit);
    TransientResult Transient(ElectricCircuit circuit, double stopTimeS, double stepS);
    AcResult AcSweep(ElectricCircuit circuit, double startHz, double stopHz, int pointsPerDecade = 20);
}

internal sealed class CircuitService : ICircuitService
{
    public ElectricCircuit Parse(string netlist) => Netlist.Parse(netlist);
    public DcResult OperatingPoint(ElectricCircuit circuit) => circuit.OperatingPoint();
    public TransientResult Transient(ElectricCircuit circuit, double stopTimeS, double stepS) => circuit.Transient(stopTimeS, stepS);
    public AcResult AcSweep(ElectricCircuit circuit, double startHz, double stopHz, int pointsPerDecade = 20) => circuit.AcSweep(startHz, stopHz, pointsPerDecade);
}

public static class CircuitBuilderExtensions
{
    /// <summary>Registers <see cref="ICircuitService"/>.</summary>
    public static ElectricalBuilder AddCircuit(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Circuit")) b.Services.TryAddSingleton<ICircuitService, CircuitService>();
        return b;
    }
}
