using System.Numerics;
using Electrical.Circuit;
using Electrical.Emt;

namespace Electrical.Tests;

/// <summary>
/// EMT validation against closed-form solutions: first/second-order circuit responses, LC inrush (Greenwood, "Electrical
/// Transients in Power Systems", ch. 3 &amp; 6), travelling waves on lossless lines (Bewley lattice; reflection coefficient
/// Γ = (R − Z_c)/(R + Z_c)), switching overvoltage on open-ended line energization (≈ 2 pu, Greenwood ch. 9), CDA (Marti &amp; Lin 1989)
/// and steady state against phasor solutions (long-line ABCD equations; Electrical.Circuit AC analysis).
/// </summary>
public class EmtValidationTests
{
    private const double W50 = 2 * Math.PI * 50;

    [Fact]
    public void RlCircuit_DcEnergization_MatchesExponential()
    {
        const double v = 100, r = 10, l = 0.1, tau = l / r;
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Dc(v));
        net.AddSeriesRL("rl", "s", "0", r, l);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 10e-6, StopTime = 0.05 });
        var i = res.Current("rl");
        foreach (double t in new[] { 0.002, 0.005, 0.01, 0.02, 0.05 })
            Assert.True(Math.Abs(i.At(t) - v / r * (1 - Math.Exp(-t / tau))) < 2e-3 * v / r, $"t={t}: {i.At(t)}");
        Assert.Equal(1, res.Factorizations);
    }

    [Fact]
    public void RlCircuit_AcEnergization_ContainsDecayingDcOffset()
    {
        const double vm = 100, r = 1, l = 0.01, tau = l / r;
        var z = new Complex(r, W50 * l);
        double phi = z.Phase, im = vm / z.Magnitude;
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Cosine(vm, 50, phi * 180 / Math.PI));   // θ = φ: maximum DC offset
        net.AddSeriesRL("rl", "s", "0", r, l);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 20e-6, StopTime = 0.06 });
        var i = res.Current("rl");
        double maxErr = 0;
        for (int k = 0; k < i.Count; k++)
        {
            double t = i.TimeAt(k);
            double exact = im * (Math.Cos(W50 * t) - Math.Exp(-t / tau));
            maxErr = Math.Max(maxErr, Math.Abs(i[k] - exact));
        }
        Assert.True(maxErr < 0.01 * im, $"max error {maxErr} A of {im} A");
        // asymmetric first peak (DC offset) exceeds the steady-state amplitude
        Assert.True(i.Peak(0, 0.012) > 1.25 * im);
        Assert.Equal(im, i.Peak(0.05, 0.06), 0.01 * im);
    }

    [Fact]
    public void RcCircuit_Charging_MatchesExponential()
    {
        const double v = 10, r = 1000, c = 10e-6, tau = r * c;
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Dc(v));
        net.AddResistor("R", "s", "c", r);
        net.AddCapacitor("C", "c", "0", c);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 10e-6, StopTime = 0.05 });
        var vc = res.Voltage("c");
        foreach (double t in new[] { 0.005, 0.01, 0.02, 0.04 })
            Assert.True(Math.Abs(vc.At(t) - v * (1 - Math.Exp(-t / tau))) < 2e-3 * v, $"t={t}");
        Assert.Equal(v / r * Math.Exp(-0.01 / tau), res.Current("R").At(0.01), 2e-3 * v / r);
    }

    [Fact]
    public void SeriesRlc_Ringing_FrequencyAndDampingMatchTheory()
    {
        const double v = 10, r = 2, l = 1e-3, c = 10e-6;
        double alpha = r / (2 * l), wd = Math.Sqrt(1 / (l * c) - alpha * alpha);
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Dc(v));
        net.AddSeriesRLC("rlc", "s", "0", r, l, c);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 1e-6, StopTime = 3e-3 });
        var i = res.Current("rlc");
        double peak = v / (wd * l), maxErr = 0;
        for (int k = 0; k < i.Count; k++)
        {
            double t = i.TimeAt(k);
            maxErr = Math.Max(maxErr, Math.Abs(i[k] - v / (wd * l) * Math.Exp(-alpha * t) * Math.Sin(wd * t)));
        }
        Assert.True(maxErr < 0.015 * peak, $"max error {maxErr / peak:P2}");
        var zc = i.ZeroCrossings(1e-4, 3e-3);
        double halfPeriod = (zc[^1] - zc[0]) / (zc.Count - 1);
        Assert.Equal(Math.PI / wd, halfPeriod, 1e-3 * Math.PI / wd);
        // envelope: successive positive peaks decay by exp(−α·T)
        double p1 = i.Peak(0, 2 * Math.PI / wd * 0.5), p2 = i.Peak(2 * Math.PI / wd, 2 * Math.PI / wd * 1.5);
        Assert.Equal(Math.Exp(-alpha * 2 * Math.PI / wd), p2 / p1, 0.01);
    }

    [Fact]
    public void CapacitorBankEnergization_InrushPeakAndFrequency()
    {
        double vpk = 11e3 * Math.Sqrt(2.0 / 3.0);
        const double ls = 2e-3, rs = 0.05, c = 50e-6, tc = 0.02; // close at the phase-voltage peak
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "bus", EmtWaveform.Cosine(vpk, 50), rs, ls);
        net.AddSwitch("brk", "bus", "cap", closeTime: tc);
        net.AddCapacitor("C", "cap", "0", c);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 5e-6, StopTime = 0.03 });
        var i = res.Current("brk");
        double surge = vpk * Math.Sqrt(c / ls), f0 = 1 / (2 * Math.PI * Math.Sqrt(ls * c));
        double peak = i.Peak(tc, tc + 0.004);
        Assert.InRange(peak / surge, 0.95, 1.12);
        // natural component = i − steady-state; its zero crossings give the ring frequency
        var iss = new Complex(vpk, 0) / new Complex(rs, W50 * ls - 1 / (W50 * c));
        var nat = new double[i.Count];
        for (int k = 0; k < i.Count; k++) nat[k] = i[k] - (iss * Complex.Exp(new Complex(0, W50 * i.TimeAt(k)))).Real;
        var zc = new EmtChannel("nat", "A", 0, i.TimeStep, nat).ZeroCrossings(tc + 2e-4, tc + 6e-3);
        double f = (zc.Count - 1) / (2 * (zc[^1] - zc[0]));
        Assert.Equal(f0, f, 0.01 * f0);
        Assert.Equal(2, res.Factorizations);
    }

    [Fact]
    public void BackToBackCapacitorSwitching_InrushMatchesSeriesCapacitance()
    {
        const double v0 = 10e3, c1 = 10e-6, c2 = 10e-6, l = 50e-6;
        double ceq = c1 * c2 / (c1 + c2);
        var net = new EmtNetwork();
        net.AddCapacitor("C1", "a", "0", c1, initialVoltage: v0);
        net.AddSwitch("brk", "a", "x", closeTime: 10e-6);
        net.AddSeriesRL("L", "x", "b", 0.01, l);
        net.AddCapacitor("C2", "b", "0", c2);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 0.2e-6, StopTime = 0.4e-3 });
        var i = res.Current("L");
        Assert.Equal(v0 * Math.Sqrt(ceq / l), i.Peak(), 0.01 * v0 * Math.Sqrt(ceq / l));
        var zc = i.ZeroCrossings(20e-6, 0.4e-3);
        double f = (zc.Count - 1) / (2 * (zc[^1] - zc[0])), f0 = 1 / (2 * Math.PI * Math.Sqrt(l * ceq));
        Assert.Equal(f0, f, 0.01 * f0);
        // charge conservation: v1 + v2 = v0 for equal capacitors
        Assert.Equal(v0, res.Voltage("a").Values[^1] + res.Voltage("b").Values[^1], 1e-3 * v0);
    }

    private static EmtResult LineStep(Action<EmtNetwork> terminate, double tau = 1e-3)
    {
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Dc(1.0), r: 400);   // matched source Rs = Zc
        net.Add(BergeronLine.SinglePhase("ln", "s", "e", 400, tau));
        terminate(net);
        return EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 50e-6, StopTime = 6e-3 });
    }

    [Fact]
    public void BergeronLine_OpenEnd_StepDoublesAfterTravelTime()
    {
        const double tau = 1e-3, dt = 50e-6;
        var res = LineStep(_ => { });
        var ve = res.Voltage("e"); var vs = res.Voltage("s");
        Assert.Equal(0, ve.Peak(0, tau - dt), 12);                     // nothing arrives before τ
        foreach (double t in new[] { tau + 2 * dt, 2e-3, 2.9e-3, 5e-3 }) Assert.Equal(1.0, ve.At(t), 9);   // 2 × V/2 at the open end
        Assert.Equal(0.5, vs.At(1.5e-3), 9);                            // launched wave V·Zc/(Rs+Zc)
        Assert.Equal(1.0, vs.At(2 * tau + 2 * dt), 9);                  // reflection returns after 2τ and is absorbed
        // non-integer travel time (20.5 Δt): arrival is interpolated
        var r2 = LineStep(_ => { }, 1.025e-3);
        Assert.Equal(0.5, r2.Voltage("e").At(1.05e-3), 9);
        Assert.Equal(1.0, r2.Voltage("e").At(1.10e-3), 9);
    }

    [Theory]
    [InlineData(400.0, 0.5)]     // matched: Γ = 0, no reflection
    [InlineData(1200.0, 0.75)]   // R = 3 Zc: Γ = 0.5
    [InlineData(1e-6, 0.0)]      // short: Γ = −1 (inversion)
    public void BergeronLine_ReflectionCoefficients(double rLoad, double expectedEnd)
    {
        var res = LineStep(n => n.AddResistor("RL", "e", "0", rLoad));
        double gamma = (rLoad - 400) / (rLoad + 400);
        Assert.Equal(0.5 * (1 + gamma), expectedEnd, 5);
        Assert.Equal(expectedEnd, res.Voltage("e").At(1.5e-3), 5);
        // source end: V/2 until the reflected wave Γ·V/2 returns at 2τ
        Assert.Equal(0.5, res.Voltage("s").At(1.5e-3), 9);
        Assert.Equal(0.5 * (1 + gamma), res.Voltage("s").At(2.5e-3), 5);
        if (rLoad < 1) Assert.Equal(1.0 / 400, res.Current("RL").At(1.5e-3), 6);   // doubled current at the short
    }

    [Fact]
    public void ThreePhaseLineEnergization_OpenEndOvervoltageAbout2pu()
    {
        double vpk = 230e3 * Math.Sqrt(2.0 / 3.0);
        const double tc = 0.02, len = 150;
        var net = new EmtNetwork();
        net.AddThreePhaseSource("G", "src", 230e3, 50, 0, r: 0.1, l: 5e-3);
        net.AddThreePhaseSwitch("CB", "src", "send", closeTime: tc);
        net.AddThreePhaseLine("L1", "send", "recv", 0.03, 0.93e-3, 12.4e-9, 0.3, 2.8e-3, 7.8e-9, len);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 20e-6, StopTime = 0.045 });
        double tau1 = len * Math.Sqrt(0.93e-3 * 12.4e-9);
        var va = res.Voltage("recv.a");
        Assert.True(va.Peak(0, tc + tau1 - 40e-6) < 0.01 * vpk);
        double pu = va.Peak(tc, 0.045) / vpk;
        Assert.InRange(pu, 1.7, 2.3);
        Assert.True(res.Voltage("recv.b").Peak(tc, 0.045) / vpk > 1.2);
    }

    [Fact]
    public void InductiveCurrentInterruption_CdaRemovesNumericalOscillation()
    {
        const double l = 0.1, dt = 50e-6, tOpen = 0.013;
        EmtChannel Run(bool cda)
        {
            var net = new EmtNetwork();
            net.AddVoltageSource("V", "s", EmtWaveform.Cosine(10e3, 50));
            net.AddSwitch("sw", "s", "x", initiallyClosed: true, openTime: tOpen);
            net.AddInductor("L", "x", "0", l);
            return EmtSimulation.Simulate(net, new EmtOptions { TimeStep = dt, StopTime = 0.02, CriticalDampingAdjustment = cda }).Voltage("x");
        }
        double i0 = 10e3 / (W50 * l) * Math.Abs(Math.Sin(W50 * tOpen));
        double spike = 2 * l / dt * i0;
        var tr = Run(false); var cdaV = Run(true);
        int n = (int)Math.Round(tOpen / dt);
        double trOsc = 0, cdaOsc = 0;
        for (int k = n + 1; k <= n + 40; k++) { trOsc = Math.Max(trOsc, Math.Abs(tr[k])); cdaOsc = Math.Max(cdaOsc, Math.Abs(cdaV[k])); }
        Assert.True(trOsc > 0.5 * spike, $"trapezoidal oscillation {trOsc:g3} V");          // sustained ±(2L/Δt)·I0 alternation
        Assert.True(cdaOsc < 1e-3 * spike, $"CDA residual {cdaOsc:g3} V vs spike {spike:g3} V");
        // the trapezoidal oscillation alternates sign every step
        Assert.True(tr[n + 10] * tr[n + 11] < 0);
    }

    private static EmtNetwork SourceLineLoad(bool bergeron)
    {
        var net = new EmtNetwork();
        net.AddThreePhaseSource("G", "src", 115e3, 50, 0, r: 0.5, l: 0.02);
        if (bergeron) net.AddThreePhaseLine("L1", "src", "load", 0.05, 1.0e-3, 11.6e-9, 0.25, 3.0e-3, 7.5e-9, 100);
        else net.AddThreePhasePiSection("L1", "src", "load", 0.05, 1.0e-3, 11.6e-9, 0.25, 3.0e-3, 7.5e-9, 100);
        net.AddThreePhaseLoad("LD", "load", 150, 0.2);
        return net;
    }

    [Fact]
    public void ThreePhaseSteadyState_BergeronLine_MatchesLongLinePhasor()
    {
        var res = EmtSimulation.Simulate(SourceLineLoad(true), new EmtOptions { TimeStep = 20e-6, StopTime = 0.3 });
        // exact distributed-parameter (ABCD) phasor solution, positive sequence
        double vph = 115e3 / Math.Sqrt(3);
        var zs = new Complex(0.5, W50 * 0.02);
        var z = new Complex(0.05, W50 * 1.0e-3); var y = new Complex(0, W50 * 11.6e-9);
        var gl = Complex.Sqrt(z * y) * 100; var zc = Complex.Sqrt(z / y);
        Complex a = Complex.Cosh(gl), b = zc * Complex.Sinh(gl), c = Complex.Sinh(gl) / zc;
        var zl = new Complex(150, W50 * 0.2);
        var vr = vph / (a + b / zl + zs * (c + a / zl));
        foreach (var ph in new[] { "a", "b", "c" })
        {
            double rms = res.Voltage("load." + ph).RmsLastCycles(50, 2);
            Assert.True(Math.Abs(rms / vr.Magnitude - 1) < 0.005, $"phase {ph}: EMT {rms:F1} V vs phasor {vr.Magnitude:F1} V");
        }
        double irms = res.Current("LD.a").RmsLastCycles(50, 2);
        Assert.True(Math.Abs(irms / (vr / zl).Magnitude - 1) < 0.005);
    }

    [Fact]
    public void ThreePhaseSteadyState_PiSection_MatchesCircuitAcAnalysis()
    {
        var res = EmtSimulation.Simulate(SourceLineLoad(false), new EmtOptions { TimeStep = 20e-6, StopTime = 0.3 });
        // per-phase positive-sequence equivalent in the Electrical.Circuit phasor solver
        double vpk = 115e3 * Math.Sqrt(2.0 / 3.0), cLine = 11.6e-9 * 100;
        var ckt = new ElectricCircuit();
        ckt.AddVoltageSource("V", "s", "0", 0, acMagnitude: vpk);
        ckt.AddResistor("Rs", "s", "s1", 0.5).AddInductor("Ls", "s1", "a", 0.02);
        ckt.AddCapacitor("C1", "a", "0", cLine / 2).AddResistor("Rl", "a", "a1", 5).AddInductor("Ll", "a1", "b", 0.1).AddCapacitor("C2", "b", "0", cLine / 2);
        ckt.AddResistor("Rload", "b", "b1", 150).AddInductor("Lload", "b1", "0", 0.2);
        double vb = ckt.AcAnalysis([50]).Voltage("b")[0].Magnitude;
        foreach (var ph in new[] { "a", "b", "c" })
        {
            double emt = res.Voltage("load." + ph).RmsLastCycles(50, 2) * Math.Sqrt(2);
            Assert.True(Math.Abs(emt / vb - 1) < 0.005, $"phase {ph}: EMT {emt:F1} V vs AC {vb:F1} V");
        }
    }
}
