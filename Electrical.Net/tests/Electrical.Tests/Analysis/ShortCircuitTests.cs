using System.Numerics;
using Electrical.Core.Network;
using Electrical.ShortCircuit;

namespace Electrical.Tests;

public class ShortCircuitTests
{
    // Radial hand-calculation example:
    //   110 kV network feeder S"kQ = 3000 MVA, R/X = 0.1, cQ = 1.1
    //   → transformer 40 MVA, 110/20 kV, uk = 12 %, Dyn (HV = bus 1, LV = bus 2), copper losses neglected
    //   → 20 kV overhead line 5 km, 0.2 + j0.35 Ω/km (bus 2 → bus 3)
    // Impedances referred to 20 kV (IEC 60909-0 eqs. 6, 7, 12):
    //   Z_Qt = 1.1·20²/3000 = 0.146667 Ω → X_Q = Z/√1.01 = 0.145940 Ω, R_Q = 0.014594 Ω
    //   Z_T  = 0.12·20²/40 = 1.2 Ω (pure reactance),  Z_L = 1 + j1.75 Ω
    private static PowerNetwork Radial()
    {
        var b = new NetworkBuilder("radial", 100, 50);
        b.AddBus(1, 110, BusType.Slack).AddBus(2, 20).AddBus(3, 20);
        b.AddTransformer(1, 2, 40, 12, 0, connection: WindingConnection.Dyn);
        b.AddLine(2, 3, 5, 0.2, 0.35);
        return b.Build();
    }

    private static ShortCircuitOptions RadialOptions(PeakCurrentMethod m = PeakCurrentMethod.EquivalentFrequency) => new()
    {
        ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000, RxRatio = 0.1, C = 1.1, Z0OverZ1 = 1 }],
        PeakMethod = m,
    };

    private static readonly double Xq = 0.146667 / Math.Sqrt(1.01), Rq = 0.1 * Xq;
    private static readonly Complex ZtotalBus3 = new(Rq + 1.0, Xq + 1.2 + 1.75); // Ω at 20 kV

    [Fact]
    public void ThreePhase_RadialFeeder_MatchesHandCalculation()
    {
        var r = ShortCircuitAnalysis.Calculate(Radial(), 3, FaultType.ThreePhase, RadialOptions());
        // I"k3 = c·Un/(√3·|Zk|) = 1.1·20/(√3·3.2580) = 3.899 kA
        double expected = 1.1 * 20 / (Math.Sqrt(3) * ZtotalBus3.Magnitude);
        Assert.Equal(expected, r.IkKa, 3);
        Assert.Equal(1.1, r.C, 6);
        // S"k = √3·Un·I"k
        Assert.Equal(Math.Sqrt(3) * 20 * expected, r.SkMva, 2);
        // Bolted 3φ fault: faulted bus voltage 0; bus 2 retains c·|ZL|/|Zk|
        Assert.Equal(0, r.Voltage(3).Va, 6);
        Assert.Equal(1.1 * new Complex(1, 1.75).Magnitude / ZtotalBus3.Magnitude, r.Voltage(2).Va, 3);
    }

    [Fact]
    public void Peak_RadialNetwork_MethodsCandBAgreeWithKappaFormula()
    {
        // Radial network: method C reproduces R/X of Zk exactly; method B multiplies κ by 1.15 (IEC 60909-0 §8.1.3).
        double rx = ZtotalBus3.Real / ZtotalBus3.Imaginary;
        double kappa = 1.02 + 0.98 * Math.Exp(-3 * rx);
        var c = ShortCircuitAnalysis.Calculate(Radial(), 3, FaultType.ThreePhase, RadialOptions());
        Assert.Equal(rx, c.RxRatio, 4);
        Assert.Equal(kappa, c.Kappa, 4);
        Assert.Equal(kappa * Math.Sqrt(2) * c.IkKa, c.IpKa, 3);
        var b = ShortCircuitAnalysis.Calculate(Radial(), 3, FaultType.ThreePhase, RadialOptions(PeakCurrentMethod.ImpedanceAtFault));
        Assert.Equal(Math.Min(1.15 * kappa, 2.0), b.Kappa, 4);
    }

    [Fact]
    public void SingleLineToGround_EqualsThreeEOverZ1Z2Z0()
    {
        var net = Radial();
        var study = new ShortCircuitStudy(net, RadialOptions());
        var (z1, z2, z0) = study.TheveninImpedance(2);
        // Dyn transformer: the LV zero-sequence path is the transformer leakage impedance only.
        Assert.Equal(0.12 * 100 / 40, z0.Imaginary, 4);
        var r = study.Calculate(2, FaultType.SingleLineToGround);
        double ibase = 100 / (Math.Sqrt(3) * 20);
        Assert.Equal(3 * 1.1 / (z1 + z2 + z0).Magnitude * ibase, r.IkKa, 4);
        Assert.Equal(r.IkKa, r.EarthCurrentKa, 6);
        Assert.Equal(0, r.Voltage(2).Va, 6);                 // faulted phase collapses
        Assert.True(r.Voltage(2).Vb > 0.9 && r.Voltage(2).Vc > 0.9);
        Assert.Equal(0, r.PhaseCurrentsKa.B.Magnitude, 6);    // healthy phases carry no fault current
    }

    [Fact]
    public void LineToLine_IsRootThreeOverTwoOfThreePhase_WhenZ2EqualsZ1()
    {
        var study = new ShortCircuitStudy(Radial(), RadialOptions());
        var f3 = study.Calculate(3, FaultType.ThreePhase);
        var f2 = study.Calculate(3, FaultType.LineToLine);
        Assert.Equal(Math.Sqrt(3) / 2 * f3.IkKa, f2.IkKa, 4); // IEC 60909-0 eq. 50
        Assert.Equal(0, f2.EarthCurrentKa, 9);
    }

    [Fact]
    public void DoubleLineToGround_EarthCurrent_MatchesIecFormula()
    {
        var study = new ShortCircuitStudy(Radial(), RadialOptions());
        var (z1, _, z0) = study.TheveninImpedanceOhm(3);
        var r = study.Calculate(3, FaultType.DoubleLineToGround);
        // IEC 60909-0 eq. 54 (Z2 = Z1): I"kE2E = √3·c·Un / |Z1 + 2·Z0|
        Assert.Equal(Math.Sqrt(3) * 1.1 * 20 / (z1 + 2 * z0).Magnitude, r.EarthCurrentKa, 4);
    }

    [Fact]
    public void BranchContributions_RadialCurrentScalesWithTurnsRatio()
    {
        var r = ShortCircuitAnalysis.Calculate(Radial(), 3, FaultType.ThreePhase, RadialOptions());
        var line = r.Branches.Single(b => b.From == 2 && b.To == 3);
        var trafo = r.Branches.Single(b => b.From == 1 && b.To == 2);
        Assert.Equal(r.IkKa, line.IaKa, 4);
        Assert.Equal(r.IkKa * 20 / 110, trafo.IaKa, 4);       // seen on the 110 kV side
        var grid = Assert.Single(r.Sources);
        Assert.Equal(r.IkKa * 20 / 110, grid.IaKa, 4);
    }

    [Fact]
    public void Iec60909_Factors()
    {
        Assert.Equal(1.10, Iec60909.VoltageFactor(20), 9);
        Assert.Equal(1.00, Iec60909.VoltageFactor(20, maximum: false), 9);
        Assert.Equal(1.05, Iec60909.VoltageFactor(0.4), 9);
        Assert.Equal(1.10, Iec60909.VoltageFactor(0.4, true, LowVoltageTolerance.TenPercent), 9);
        // κ(R/X = 0) = 2.0; κ(R/X = 0.1) = 1.02 + 0.98·e^-0.3 = 1.7460
        Assert.Equal(2.0, Iec60909.Kappa(0), 9);
        Assert.Equal(1.7460, Iec60909.Kappa(0.1), 4);
        // K_G = cmax/(1 + x"d·sinφ) = 1.1/(1 + 0.2·0.5268) = 0.99516
        Assert.Equal(0.99516, Iec60909.GeneratorCorrectionFactor(1.1, 0.2, 0.85), 4);
        // μ for t_min = 0.1 s, I"kG/IrG = 5: 0.62 + 0.72·e^-1.6 = 0.76537
        Assert.Equal(0.76537, Iec60909.DecayFactorMu(5, 0.1), 4);
        // Thermal: m → 0 for κ → 1; Ith ≥ I"k
        Assert.True(Iec60909.ThermalEquivalentCurrent(10, 1.8, 50, 0.1) > 10);
    }

    [Fact]
    public void Case14_Scan_ConsistentRelationships()
    {
        var net = TestCases.Case14();
        foreach (var g in net.Generators) g.X2 = g.XdSubtransient; // Z2 = Z1
        var rows = ShortCircuitAnalysis.ScanAllBuses(net);
        Assert.Equal(14, rows.Count);
        foreach (var r in rows)
        {
            Assert.True(r.Ik3Ka > 0 && double.IsFinite(r.Ik3Ka));
            Assert.Equal(Math.Sqrt(3) / 2 * r.Ik3Ka, r.Ik2Ka, 6);
            Assert.InRange(r.IpKa / (Math.Sqrt(2) * r.Ik3Ka), 1.02, 2.0);
        }
        // Generator buses have the highest fault levels in the 138 kV area
        var hv = rows.Where(r => r.BaseKv == 138).OrderByDescending(r => r.Sk3Mva).First();
        Assert.Contains(hv.BusId, new[] { 1, 2 });
    }

    [Fact]
    public void FaultImpedance_ReducesCurrent()
    {
        var study = new ShortCircuitStudy(Radial(), RadialOptions());
        var bolted = study.Calculate(3, FaultType.SingleLineToGround);
        var high = study.Calculate(3, FaultType.SingleLineToGround, new Complex(10, 0));
        Assert.True(high.IkKa < bolted.IkKa);
        Assert.True(high.Voltage(3).Va > 0.1);
    }
}
