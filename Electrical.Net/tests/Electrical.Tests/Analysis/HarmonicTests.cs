using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Units;
using Electrical.Harmonic;

namespace Electrical.Tests;

public class HarmonicTests
{
    [Theory]
    [InlineData(64)]   // radix-2
    [InlineData(100)]  // Bluestein
    [InlineData(37)]   // prime length
    public void Fft_MatchesDirectDft_AndInverts(int n)
    {
        var rng = new Random(7);
        var x = Enumerable.Range(0, n).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToArray();
        var f = Fft.Forward(x);
        var d = Fft.Dft(x);
        for (int k = 0; k < n; k++) Assert.True((f[k] - d[k]).Magnitude < 1e-9, $"bin {k}");
        var back = Fft.Inverse(f);
        for (int k = 0; k < n; k++) Assert.True((back[k] - x[k]).Magnitude < 1e-12);
    }

    [Theory]
    [InlineData(6400, 256)]  // 2 cycles, power of two
    [InlineData(5000, 1000)] // 10 cycles, arbitrary length
    public void Spectrum_FromSamples_RecoversHarmonics(double fs, int n)
    {
        // x(t) = 5 + 100·cos(ωt) + 20·cos(5ωt + 30°), f = 50 Hz → RMS1 = 70.711, RMS5 = 14.142, THD = 20 %
        var x = Enumerable.Range(0, n).Select(k =>
        {
            double t = k / fs, w = 2 * Math.PI * 50;
            return 5 + 100 * Math.Cos(w * t) + 20 * Math.Cos(5 * w * t + Math.PI / 6);
        }).ToArray();
        var s = HarmonicSpectrum.FromSamples(x, fs, 50, 25);
        Assert.Equal(5, s[0], 9);
        Assert.Equal(100 / Math.Sqrt(2), s.Fundamental, 9);
        Assert.Equal(20 / Math.Sqrt(2), s[5], 9);
        Assert.Equal(30, s.Phase(5), 6);
        Assert.Equal(20, s.ThdPercent, 6);
        Assert.Equal(0, s[3], 9);
        // round trip through synthesis
        Assert.Equal(x[17], s.Synthesize(fs, 18)[17], 6);
    }

    [Fact]
    public void Indices_CrestFactor_KFactor_Tdd()
    {
        var sine = Enumerable.Range(0, 1000).Select(k => Math.Sin(2 * Math.PI * k / 100)).ToArray();
        Assert.Equal(Math.Sqrt(2), PowerQuality.CrestFactor(sine), 6);
        var s = HarmonicSpectrum.FromPercent(50, 100, [(5, 20, 0)]);
        // K = Σ(Ih·h)²/ΣIh² = (1 + 0.04·25)/(1.04) = 1.923
        Assert.Equal(2 / 1.04, s.KFactor, 9);
        Assert.Equal(10, s.TddPercent(200), 9);               // 20 A / 200 A
    }

    [Fact]
    public void Ieee519_CurrentAndVoltageLimits()
    {
        // Table 2, Isc/IL = 30 (20 ≤ ratio < 50): h<11 7 %, 11≤h<17 3.5 %, even 25 %, TDD 8 %
        Assert.Equal(7.0, Ieee519.CurrentLimitPercent(5, 30), 9);
        Assert.Equal(3.5, Ieee519.CurrentLimitPercent(11, 30), 9);
        Assert.Equal(1.75, Ieee519.CurrentLimitPercent(2, 30), 9);
        Assert.Equal(8.0, Ieee519.TddLimitPercent(30), 9);
        Assert.Equal(2.0, Ieee519.CurrentLimitPercent(5, 10, 115), 9);   // Table 3
        Assert.Equal((3.0, 5.0), Ieee519.VoltageLimits(13.8));
        Assert.Equal((5.0, 8.0), Ieee519.VoltageLimits(0.48));
        var i = HarmonicSpectrum.FromPercent(60, 90, [(5, 100 * 6.0 / 90, 0), (11, 100 * 4.0 / 90, 0)]);
        var r = Ieee519.CheckCurrent(i, shortCircuitCurrentA: 3000, maximumDemandCurrentA: 100);
        Assert.False(r.Compliant);
        Assert.Equal(11, Assert.Single(r.Violations).Order);          // 4 % > 3.5 %
        Assert.Equal(Math.Sqrt(52), r.TotalMeasured, 9);              // TDD 7.21 %
        Assert.True(r.TotalPass);
    }

    [Fact]
    public void Iec61000_Tables()
    {
        // IEC 61000-2-2 Table 1
        Assert.Equal(6.0, Iec61000.VoltageCompatibilityLevelPercent(5));
        Assert.Equal(5.0, Iec61000.VoltageCompatibilityLevelPercent(3));
        Assert.Equal(2.0, Iec61000.VoltageCompatibilityLevelPercent(2));
        Assert.Equal(0.2, Iec61000.VoltageCompatibilityLevelPercent(27));
        Assert.Equal(0.2 + 1.3 * 25 / 29.0, Iec61000.VoltageCompatibilityLevelPercent(29), 9);
        // IEC 61000-3-2 Class A
        Assert.Equal(2.30, Iec61000.ClassACurrentLimitA(3));
        Assert.Equal(0.15, Iec61000.ClassACurrentLimitA(15), 9);
        Assert.Equal(0.23, Iec61000.ClassACurrentLimitA(8), 9);
        var led = TypicalSpectra.LedDriver().ToSpectrum(50, 0.5);    // 0.5 A LED lamp: 3rd = 0.375 A < 2.30 A
        Assert.True(Iec61000.CheckClassA(led).Compliant);
        var v = HarmonicSpectrum.FromPercent(50, 230, [(5, 7, 0)]);
        Assert.False(Iec61000.CheckVoltage(v).Compliant);
    }

    [Fact]
    public void TypicalSpectra_IdealSixPulseThd()
    {
        // Ideal 6-pulse: THD (infinite series) = √(π²/9 − 1) = 31.08 %; truncated at h = 49 slightly lower
        var s = TypicalSpectra.SixPulseIdeal();
        Assert.InRange(s.ThdPercent, 29.5, 31.08);
        Assert.Equal(20, s.Components.Single(c => c.Order == 5).MagnitudePercent, 9);
        Assert.DoesNotContain(s.Components, c => c.Order % 3 == 0);
        Assert.All(TypicalSpectra.TwelvePulseIdeal().Components, c => Assert.True(c.Order % 12 is 1 or 11));
    }

    private static PowerNetwork CapacitorBankSystem(double qcMvar)
    {
        // 13.8 kV, source X"d = 0.2 pu on 100 MVA → S_sc = 500 MVA at bus 1; capacitor bank at bus 1
        var net = new PowerNetwork { BaseMva = 100, FrequencyHz = 60 };
        net.Buses.Add(new Bus { Id = 1, BaseKv = 13.8, Type = BusType.Slack, Bs = qcMvar });
        net.Generators.Add(new Generator { Bus = 1, XdSubtransient = 0.2, MBase = 100 });
        return net;
    }

    [Fact]
    public void FrequencyScan_CapacitorResonanceMatchesSqrtSscOverQc()
    {
        // h_r = √(S_sc/Q_c) = √(500/20) = 5.0
        var scan = FrequencyScan.DrivingPoint(CapacitorBankSystem(20), 1, 1, 10, 0.01);
        var peak = FrequencyScan.FindResonances(scan).Where(r => r.Parallel).MaxBy(r => r.ImpedanceOhm)!;
        Assert.Equal(FrequencyScan.ParallelResonanceOrder(500, 20), peak.Order, 2);
        Assert.Equal(300, peak.FrequencyHz, 0);
        // below resonance the impedance is inductive (source reactance h·Xs): |Z(1)| ≈ Xs/(1 − Xs·Bc) = 0.2/0.96 pu
        Assert.Equal(0.2 / 0.96 * 13.8 * 13.8 / 100, scan[0].ImpedanceOhm, 6);
    }

    private static PowerNetwork Feeder()
    {
        var b = new NetworkBuilder("hlf", 100, 60);
        b.AddBus(1, 13.8, BusType.Slack).AddBus(2, 13.8);
        b.AddLineOhm(1, 2, 0.05, 0.4);
        b.AddLoad(2, 8, 3);
        b.AddShunt(2, 6);                                    // 6 Mvar capacitor bank
        var net = b.Build(validate: false);
        net.Generators.Add(new Generator { Bus = 1, XdSubtransient = 0.2, MBase = 100, Vset = 1.0 });
        return net;
    }

    [Fact]
    public void HarmonicLoadFlow_ConsistentWithScan_AndFilterReducesThd()
    {
        var net = Feeder();
        var src = new HarmonicInjection(2, TypicalSpectra.SixPulseRectifier());
        var opt = new HarmonicOptions { IncludeLoads = false };
        var r = HarmonicLoadFlow.Run(net, [src], opt);
        Assert.True(r.Fundamental.Converged);
        // |V5| = |Z22(5)|·|I5|, I5 = 19.1 % of the load current |S/V|
        var z5 = FrequencyScan.DrivingPoint(net, 2, 5, 5, 1, opt)[0].ImpedancePu;
        double i1 = new Complex(0.08, 0.03).Magnitude / r.Fundamental.Bus(2).Vm;
        Assert.Equal(z5.Magnitude * 0.191 * i1, r.Voltage(2, 5).Magnitude, 9);
        double thd = r.ThdPercent(2);
        Assert.True(thd > r.ThdPercent(1));
        Assert.Equal(2, r.WorstBus.BusId);

        var withFilter = Feeder();
        SingleTunedFilter.Design(13.8, 6, 4.7, 30, 60).AddTo(withFilter, 2, 3);
        withFilter.Buses.Single(b => b.Id == 2).Bs = 0;     // the 6 Mvar bank is replaced by a 6 Mvar 4.7th filter
        var rf = HarmonicLoadFlow.Run(withFilter, [src], opt);
        Assert.True(rf.ThdPercent(2) < 0.6 * thd, $"{rf.ThdPercent(2):0.00} vs {thd:0.00}");
        Assert.True(rf.BranchCurrent(1, 5).Magnitude > 0.5 * 0.191 * i1);   // most 5th-harmonic current flows into the filter
    }

    [Fact]
    public void SingleTunedFilter_Design()
    {
        // 13.8 kV, 5 Mvar, h = 4.7: X_eff = 13.8²/5 = 38.088 Ω, X_C = X_eff·h²/(h² − 1) = 39.894 Ω, X_L = X_C/h² = 1.806 Ω
        var f = SingleTunedFilter.Design(13.8, 5, 4.7, 30, 60);
        Assert.Equal(39.894, f.CapacitorReactanceOhm, 3);
        Assert.Equal(1.806, f.ReactorReactanceOhm, 3);
        Assert.Equal(0, f.Impedance(4.7).Imaginary, 9);
        Assert.Equal(5, f.FundamentalMvar, 9);
        Assert.Equal(4.7 * f.ReactorReactanceOhm / 30, f.ResistanceOhm, 12);
        Assert.Equal(22.09 / 21.09, f.CapacitorVoltageRise, 9);
        Assert.Equal(1e6 / (2 * Math.PI * 60 * f.CapacitorReactanceOhm), f.CapacitanceMicroF, 9);
    }

    [Fact]
    public void Unbalance_VufAndNema()
    {
        var v = new ThreePhase(Phasor.FromPolarDeg(1, 0), Phasor.FromPolarDeg(0.9, -120), Phasor.FromPolarDeg(1, 120));
        var s = SymmetricalComponents.FromPhases(v);
        Assert.Equal(100 * s.Negative.Magnitude / s.Positive.Magnitude, PowerQuality.VoltageUnbalancePercent(v), 12);
        Assert.InRange(PowerQuality.VoltageUnbalancePercent(v), 3, 4);   // ≈ 3.45 %
        Assert.Equal(2.5, PowerQuality.NemaUnbalancePercent(400, 390, 410), 9);
        Assert.Equal(0.95, PowerQuality.NemaDerating(2), 9);
    }
}
