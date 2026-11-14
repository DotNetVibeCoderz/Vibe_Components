using Electrical.Dynamics;
using Electrical.PowerFlow;

namespace Electrical.Tests;

/// <summary>
/// Kundur two-area system (P. Kundur, Power System Stability and Control, McGraw-Hill 1994, Example 12.6).
/// Reference values: load flow (Table/Fig. E12.x) G1 700 MW 185 Mvar 1.03∠20.2°, G2 700 MW 235 Mvar 1.01∠10.5°, G3 719 MW 176 Mvar
/// 1.03∠−6.8°, G4 700 MW 202 Mvar 1.01∠−17.0°. Small signal with manual excitation: inter-area ≈ 0.545 Hz (ζ ≈ 3 %), local modes
/// ≈ 1.09 / 1.12 Hz (ζ ≈ 7 %); with the KA = 200 thyristor exciter the inter-area mode loses its damping (Kundur: ζ ≈ −0.8 %, slightly
/// unstable); Kundur's PSS restores ζ well above 5 %. Kundur's loads are constant current (P) / constant impedance (Q); ours are constant
/// impedance, which explains small differences in the damping of the inter-area mode.
/// </summary>
public class KundurTests
{
    private static readonly Lazy<DynamicSmallSignalResult> Manual = new(() => DynamicSmallSignal.Analyze(DynamicTestCases.KundurTwoArea(KundurExcitation.ConstantField).Build()));
    private static readonly Lazy<DynamicSmallSignalResult> Static = new(() => DynamicSmallSignal.Analyze(DynamicTestCases.KundurTwoArea(KundurExcitation.StaticExciter).Build()));
    private static readonly Lazy<DynamicSmallSignalResult> WithPss = new(() => DynamicSmallSignal.Analyze(DynamicTestCases.KundurTwoArea(KundurExcitation.StaticExciterWithPss).Build()));

    [Fact]
    public void PowerFlow_MatchesTextbook()
    {
        var pf = PowerFlowSolver.Solve(DynamicTestCases.KundurNetwork());
        Assert.True(pf.Converged);
        double[] q = [185, 235, 176, 202], va = [20.2, 10.5, -6.8, -17.0];
        for (int g = 0; g < 4; g++)
        {
            Assert.Equal(q[g], pf.Generators[g].Q, 0);          // within 0.5 Mvar (ours 185.0 / 234.6 / 176.0 / 202.1)
            Assert.InRange(pf.Buses[g].VaDeg, va[g] - 0.5, va[g] + 0.5); // ours 20.27 / 10.51 / −6.80 / −16.99
        }
        Assert.Equal(719, pf.Generators[2].P, 0);
    }

    [Fact]
    public void ConstantField_InterAreaAndLocalModes()
    {
        var em = Manual.Value.ElectromechanicalModes();
        Assert.Equal(3, em.Count);
        var inter = em[0];
        Assert.InRange(inter.FrequencyHz, 0.50, 0.60);   // Kundur 0.545 Hz; ours 0.542 Hz
        Assert.InRange(inter.DampingRatio, 0.0, 0.06);   // Kundur ≈ 3 %; ours 3.8 %
        foreach (var local in em.Skip(1))
        {
            Assert.InRange(local.FrequencyHz, 1.0, 1.2); // Kundur 1.09 / 1.12 Hz; ours 1.077 / 1.112 Hz
            Assert.InRange(local.DampingRatio, 0.04, 0.12);
        }
        // inter-area mode shape: area 1 (G1, G2) swings against area 2 (G3, G4)
        var s = inter.SpeedShape;
        double Phase(string g) => s[g].Phase;
        Assert.True(Math.Cos(Phase("G1") - Phase("G2")) > 0.9);
        Assert.True(Math.Cos(Phase("G3") - Phase("G4")) > 0.9);
        Assert.True(Math.Cos(Phase("G1") - Phase("G3")) < -0.8);
        // a local mode of area 1 has G1 against G2 and little participation of area 2
        var local1 = em.Skip(1).First(m => m.Dominant.Take(3).Any(d => d.State.StartsWith("G1") || d.State.StartsWith("G2")));
        Assert.True(Math.Cos(local1.SpeedShape["G1"].Phase - local1.SpeedShape["G2"].Phase) < -0.8);
    }

    [Fact]
    public void StaticExciter_WithoutPss_InterAreaModeLosesDamping()
    {
        var inter = Static.Value.ElectromechanicalModes()[0];
        Assert.InRange(inter.FrequencyHz, 0.55, 0.65);   // Kundur ≈ 0.6 Hz; ours 0.607 Hz
        Assert.True(inter.DampingRatio < 0.01, $"ζ = {inter.DampingRatio:P2}"); // Kundur −0.8 %; ours ≈ 0.0 %
        Assert.True(inter.DampingRatio < Manual.Value.ElectromechanicalModes()[0].DampingRatio);
    }

    [Fact]
    public void Pss_DampsInterAreaMode()
    {
        var r = WithPss.Value;
        Assert.True(r.IsStable(), "all eigenvalues in the left half plane");
        var em = r.ElectromechanicalModes();
        Assert.Equal(3, em.Count);
        Assert.InRange(em[0].FrequencyHz, 0.5, 0.7);      // ours 0.609 Hz
        Assert.True(em[0].DampingRatio > 0.05, $"ζ = {em[0].DampingRatio:P2}"); // ours 18.6 %
        Assert.All(em, m => Assert.True(m.DampingRatio > 0.05));
        Assert.Contains("ζ", r.ModeTable());
    }

    [Fact]
    public void TimeDomain_PssDampsTieLineOscillation_AndRunsFast()
    {
        // 3-phase fault at bus 8 (tie-line midpoint) through j0.01 pu for 100 ms, then 10 s at a half-cycle step (60 Hz → 8.33 ms)
        var events = new[] { DynamicEvent.Fault(0.5, 8, new System.Numerics.Complex(0, 0.01)), DynamicEvent.Clear(0.6, 8) };
        double Amplitude(KundurExcitation ex, out DynamicSimulationResult r)
        {
            var sys = DynamicTestCases.KundurTwoArea(ex).Build();
            var res = DynamicSimulation.Run(sys, events, new SimulationOptions { EndTimeS = 10 });
            r = res;
            var d = res.AngleDeg[0].Zip(res.AngleDeg[2], (a, b) => a - b).ToArray();
            var late = d.Where((_, k) => res.Time[k] > 7).ToArray();
            return late.Max() - late.Min();
        }
        double noPss = Amplitude(KundurExcitation.StaticExciter, out var r1);
        double pss = Amplitude(KundurExcitation.StaticExciterWithPss, out var r2);
        Assert.True(r1.Stable && r2.Stable);
        Assert.Equal(0, r2.ConvergenceFailures);
        Assert.True(pss < 0.1 * noPss, $"δ13 swing 7–10 s: {pss:0.000}° with PSS vs {noPss:0.000}° without");
        Assert.Equal(1201, r2.Time.Length);
        Assert.True(r2.Elapsed.TotalSeconds < 1.0, $"10 s simulation took {r2.Elapsed.TotalMilliseconds:0} ms");
    }
}
