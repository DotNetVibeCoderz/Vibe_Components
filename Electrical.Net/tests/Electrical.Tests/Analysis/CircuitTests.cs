using System.Numerics;
using Electrical.Circuit;
using Electrical.Core.Units;

namespace Electrical.Tests;

public class CircuitTests
{
    [Fact]
    public void VoltageDivider_DcOperatingPoint()
    {
        var c = new ElectricCircuit()
            .AddVoltageSource("V1", "in", "0", 10)
            .AddResistor("R1", "in", "out", 1000)
            .AddResistor("R2", "out", "0", 3000);
        var op = c.OperatingPoint();
        Assert.Equal(7.5, op.Voltage("out"), 6);               // 10·3k/(1k + 3k)
        Assert.Equal(-2.5e-3, op.Current("V1"), 9);            // SPICE sign: delivering source → negative
        Assert.Equal(2.5e-3, op.Current("R2"), 9);
    }

    [Theory]
    [InlineData(IntegrationMethod.Trapezoidal, 1e-4)]
    [InlineData(IntegrationMethod.BackwardEuler, 5e-3)]
    public void RcStepResponse_TimeConstant(IntegrationMethod method, double tol)
    {
        // R = 1 kΩ, C = 1 µF → τ = 1 ms; v_C(t) = 1 − e^(−t/τ): v(τ) = 0.63212, v(3τ) = 0.95021
        var c = new ElectricCircuit()
            .AddVoltageSource("V1", "in", "0", 1)
            .AddResistor("R", "in", "out", 1000)
            .AddCapacitor("C", "out", "0", 1e-6, initialVoltage: 0);
        var tr = c.Transient(5e-3, 1e-6, method, useInitialConditions: true);
        var v = tr.Voltage("out");
        Assert.Equal(1 - Math.Exp(-1), tr.At(v, 1e-3), tol);
        Assert.Equal(1 - Math.Exp(-3), tr.At(v, 3e-3), tol);
        Assert.Equal(Math.Exp(-1) * 1e-3, tr.At(tr.Current("C"), 1e-3), tol * 1e-3 * 5);
    }

    [Fact]
    public void RcPulse_FromOperatingPoint()
    {
        var c = Netlist.Parse("""
            * RC low-pass driven by a pulse
            V1 in 0 PULSE(0 2 0.1m 1n 1n 10m)
            R1 in out 2k
            C1 out 0 500n
            .tran 1u 3m
            .end
            """);
        var tr = c.Transient(3e-3, 1e-6);
        var v = tr.Voltage("out");
        Assert.Equal(0, v[0], 9);                                          // OP: pulse low
        Assert.Equal(2 * (1 - Math.Exp(-1)), tr.At(v, 0.1e-3 + 1e-3), 3);  // τ = 2k·500n = 1 ms
    }

    [Fact]
    public void RlCurrentRise_And_Inductor_ShortAtDc()
    {
        // L = 10 mH, R = 10 Ω → τ = 1 ms; i(τ) = 0.632·V/R
        var c = new ElectricCircuit()
            .AddVoltageSource("V1", "a", "0", 5)
            .AddResistor("R", "a", "b", 10)
            .AddInductor("L", "b", "0", 10e-3, initialCurrent: 0);
        var tr = c.Transient(3e-3, 1e-6, useInitialConditions: true);
        Assert.Equal(0.5 * (1 - Math.Exp(-1)), tr.At(tr.Current("L"), 1e-3), 4);
        Assert.Equal(0.5, c.OperatingPoint().Current("L"), 9);
    }

    [Fact]
    public void SeriesRlc_ResonanceAndBandwidth()
    {
        // R = 10 Ω, L = 10 mH, C = 1 µF: f0 = 1/(2π√LC) = 1591.55 Hz, Q = (1/R)·√(L/C) = 10
        var c = new ElectricCircuit()
            .AddVoltageSource("V1", "in", "0", 0, acMagnitude: 1)
            .AddResistor("R", "in", "a", 10)
            .AddInductor("L", "a", "b", 10e-3)
            .AddCapacitor("C", "b", "0", 1e-6);
        double f0 = ElectricalFormulas.ResonantFrequency(10e-3, 1e-6);
        var at = c.AcAnalysis([f0, f0 * 0.5, f0 * 2]);
        var i = at.Current("R");
        Assert.Equal(0.1, i[0].Magnitude, 6);                    // |I| = V/R at resonance
        Assert.Equal(0, i[0].Phase, 6);                          // purely resistive
        Assert.Equal(10, at.Voltage("b")[0].Magnitude, 4);       // capacitor voltage = Q·V
        var sweep = c.AcSweep(100, 100_000, 200);
        var peak = sweep.Bode("a", "in").Zip(sweep.Current("R")).MaxBy(p => p.Second.Magnitude);
        Assert.Equal(f0, peak.First.FrequencyHz, f0 * 0.01);
    }

    [Fact]
    public void Diode_OperatingPointAndHalfWaveRectifier()
    {
        var dc = new ElectricCircuit().AddVoltageSource("V", "a", "0", 5).AddResistor("R", "a", "k", 1000).AddDiode("D", "k", "0");
        var op = dc.OperatingPoint();
        double vd = op.Voltage("k");
        Assert.InRange(vd, 0.6, 0.75);
        // Shockley consistency: (5 − Vd)/R = Is·(e^(Vd/VT) − 1)
        Assert.Equal((5 - vd) / 1000, 1e-14 * (Math.Exp(vd / 0.025852) - 1), 7);

        var rect = Netlist.Parse("""
            V1 in 0 SIN(0 10 50)
            D1 in out IS=1e-14
            RL out 0 1k
            """);
        var tr = rect.Transient(0.04, 2e-5);
        var vo = tr.Voltage("out");
        Assert.InRange(vo.Max(), 9.2, 9.45);                     // peak minus one diode drop
        Assert.InRange(vo.Min(), -1e-3, 1e-3);                   // blocks the negative half-cycle
    }

    [Fact]
    public void CoupledInductors_And_IdealTransformer()
    {
        // k ≈ 1, L1 = 100 mH, L2 = 1 mH → voltage ratio √(L2/L1) = 0.1 (open circuit)
        var k = Netlist.Parse("""
            V1 p 0 AC 1
            L1 p 0 100m
            L2 s 0 1m
            K1 L1 L2 0.999999
            RL s 0 1meg
            """);
        var ac = k.AcAnalysis([50]);
        Assert.Equal(0.1, ac.Voltage("s")[0].Magnitude, 4);

        var x = Netlist.Parse("""
            V1 p 0 DC 230
            X1 p 0 s 0 10
            RL s 0 23
            """);
        var op = x.OperatingPoint();
        Assert.Equal(23, op.Voltage("s"), 6);                    // 230 V / 10
        Assert.Equal(0.1, op.Current("X1"), 6);                  // I_p = I_s / n = 1 A / 10
    }

    [Fact]
    public void ControlledSources_And_Switch()
    {
        // non-inverting amplifier with an ideal VCVS (gain 1e6) and feedback divider 9k/1k → closed-loop gain ≈ 10
        var amp = new ElectricCircuit()
            .AddVoltageSource("Vin", "in", "0", 0.1)
            .AddVcvs("E1", "out", "0", "in", "fb", 1e6)
            .AddResistor("Rf", "out", "fb", 9000)
            .AddResistor("Rg", "fb", "0", 1000);
        Assert.Equal(1.0, amp.OperatingPoint().Voltage("out"), 4);
        var g = new ElectricCircuit().AddVoltageSource("V", "c", "0", 2).AddVccs("G1", "0", "o", "c", "0", 1e-3).AddResistor("R", "o", "0", 1000);
        Assert.Equal(2, g.OperatingPoint().Voltage("o"), 6);    // gm·Vc·R

        // capacitor charged to 5 V discharges through 1 kΩ when the switch closes at 1 ms (τ = 1 ms)
        var sw = Netlist.Parse("""
            C1 a 0 1u IC=5
            S1 a b OFF T=1m
            R1 b 0 1k
            """);
        var tr = sw.Transient(4e-3, 1e-6, useInitialConditions: true);
        var v = tr.Voltage("a");
        Assert.Equal(5, tr.At(v, 0.9e-3), 3);
        Assert.Equal(5 * Math.Exp(-1), tr.At(v, 2e-3), 2);
    }

    [Fact]
    public void Netlist_ValueSuffixes()
    {
        Assert.Equal(4.7e3, Netlist.Value("4.7k"), 9);
        Assert.Equal(10e6, Netlist.Value("10meg"), 9);
        Assert.Equal(2.2, Netlist.Value("2.2uF") * 1e6, 9);
        Assert.Equal(1e-3, Netlist.Value("1m"), 12);
        Assert.Equal(1.5, Netlist.Value("1.5p") * 1e12, 9);
        Assert.Equal(-3e-2, Netlist.Value("-3e-2"), 12);
        Assert.Throws<FormatException>(() => Netlist.Parse("Q1 a b c 1"));
    }

    [Fact]
    public void ThreePhase_StarDeltaAndUnbalancedLoads()
    {
        var z = new Complex(10, 5);
        var (zab, zbc, zca) = ThreePhaseCircuits.StarToDelta(z, z, z);
        Assert.Equal(3 * z, zab);                                 // balanced: Z_Δ = 3·Z_Y
        var (za, zb, zc) = ThreePhaseCircuits.DeltaToStar(new Complex(30, 0), new Complex(0, 60), new Complex(90, -20));
        var back = ThreePhaseCircuits.StarToDelta(za, zb, zc);
        Assert.Equal(60, back.Zbc.Imaginary, 9);

        var v = ThreePhase.Balanced(230);
        var bal = ThreePhaseCircuits.SolveStarLoad(v, 23, 23, 23);
        Assert.Equal(0, bal.NeutralShiftVoltage.Magnitude, 9);
        Assert.Equal(3 * 230 * 230 / 23.0, bal.TotalPower.Real, 6);   // 6.9 kW
        Assert.Equal(230 * Math.Sqrt(3), ThreePhaseCircuits.LineVoltages(v).A.Magnitude, 9);

        var unb = ThreePhaseCircuits.SolveStarLoad(v, 10, 20, new Complex(0, 30));
        Assert.True(unb.NeutralShiftVoltage.Magnitude > 10);
        Assert.Equal(0, unb.NeutralCurrent.Magnitude, 9);             // floating star: no neutral current
        Assert.True(unb.CurrentUnbalance > 0.1);
        var fourWire = ThreePhaseCircuits.SolveStarLoad(v, 10, 20, new Complex(0, 30), Complex.Zero);
        Assert.True(fourWire.NeutralCurrent.Magnitude > 1);

        var delta = ThreePhaseCircuits.SolveDeltaLoad(v, 69, 69, 69);
        Assert.Equal(bal.TotalPower.Real, delta.TotalPower.Real, 3);  // 69 Ω Δ ≡ 23 Ω Y
        Assert.Equal(ThreePhaseCircuits.BalancedPower(398.37, 10, 0.8).Real, Math.Sqrt(3) * 398.37 * 10 * 0.8, 9);
    }
}
