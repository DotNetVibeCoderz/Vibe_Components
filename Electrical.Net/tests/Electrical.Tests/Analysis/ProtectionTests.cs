using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Units;
using Electrical.Protection;
using Electrical.ShortCircuit;

namespace Electrical.Tests;

public class ProtectionTests
{
    [Theory]
    // IEC 60255-151 at M = 10, TMS = 1: SI 0.14/(10^0.02 − 1) = 2.971 s, VI 13.5/9 = 1.5 s, EI 80/99 = 0.808 s, LTI 120/9 = 13.33 s
    [InlineData(CurveType.IecStandardInverse, 10, 2.9706)]
    [InlineData(CurveType.IecVeryInverse, 10, 1.5)]
    [InlineData(CurveType.IecExtremelyInverse, 10, 0.80808)]
    [InlineData(CurveType.IecLongTimeInverse, 10, 13.3333)]
    // IEEE C37.112 at M = 5, TD = 1: MI 0.0515/(5^0.02 − 1) + 0.114 = 1.6883 s, VI 19.61/24 + 0.491 = 1.3081 s, EI 28.2/24 + 0.1217 = 1.2967 s
    [InlineData(CurveType.IeeeModeratelyInverse, 5, 1.68833)]
    [InlineData(CurveType.IeeeVeryInverse, 5, 1.30808)]
    [InlineData(CurveType.IeeeExtremelyInverse, 5, 1.29670)]
    public void StandardCurves_MatchPublishedFormulae(CurveType curve, double multiple, double expected)
    {
        Assert.Equal(expected, InverseTimeCurves.OperatingTime(curve, multiple, 1.0), 3);
    }

    [Fact]
    public void Relay_PickupInstantaneousAndDefiniteTime()
    {
        var r = new OvercurrentRelay("51", 400, CurveType.IecStandardInverse, 0.2) { InstantaneousPickupA = 4000, InstantaneousDelayS = 0.05 };
        Assert.True(double.IsPositiveInfinity(r.OperatingTime(390)));
        Assert.Equal(0.2 * 2.9706, r.OperatingTime(4000 - 1e-6 + 0), 2); // just below high-set: inverse stage at M ≈ 10
        Assert.Equal(0.05, r.OperatingTime(5000), 9);
        var dt = new OvercurrentRelay("DT", 100, CurveType.DefiniteTime, 0.5);
        Assert.Equal(0.5, dt.OperatingTime(150), 9);
    }

    [Fact]
    public void GradeTimeMultipliers_MeetsCtiWithMinimalSettings()
    {
        var r1 = new OvercurrentRelay("Load", 200);
        var r2 = new OvercurrentRelay("Mid", 400);
        var r3 = new OvercurrentRelay("Source", 600);
        var res = Coordination.GradeTimeMultipliers(
        [
            new GradingStage(r1, 3000, 0),
            new GradingStage(r2, 5000, 3000),
            new GradingStage(r3, 8000, 5000),
        ], ctiS: 0.3, minimumTms: 0.05);
        Assert.True(res.AllCoordinated);
        Assert.Equal(0.05, r1.TimeMultiplier, 9);
        // Mid relay: t_down = 0.05·0.14/(15^0.02 − 1) = 0.1257 s → TMS ≥ 0.4257/(0.14/(7.5^0.02 − 1)) = 0.4257/3.403 = 0.1251 → 0.13
        Assert.Equal(0.13, r2.TimeMultiplier, 9);
        // minimality: one step less violates the CTI
        Assert.False(Coordination.Check(r1, new OvercurrentRelay("x", 400, CurveType.IecStandardInverse, 0.12), 3000).Coordinated);
        Assert.True(Coordination.Check(r1, r2, 3000).Coordinated);
        Assert.True(r3.TimeMultiplier > r2.TimeMultiplier);
    }

    [Fact]
    public void GradeRadialFeeder_UsesShortCircuitCurrentsThroughTransformer()
    {
        var b = new NetworkBuilder("feeder", 100, 50);
        b.AddBus(1, 110, BusType.Slack).AddBus(2, 20).AddBus(3, 20).AddBus(4, 20);
        b.AddTransformer(1, 2, 40, 12);
        b.AddLine(2, 3, 4, 0.2, 0.35).AddLine(3, 4, 6, 0.3, 0.38);
        var net = b.Build();
        var sc = new ShortCircuitOptions { ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 3000 }] };
        var relays = new[] { new OvercurrentRelay("R-110kV", 250), new OvercurrentRelay("R-2", 800), new OvercurrentRelay("R-3", 400) };
        var res = Coordination.GradeRadialFeeder(net, [1, 2, 3, 4], relays, sc, ctiS: 0.3);
        Assert.True(res.AllCoordinated);
        Assert.Equal(["R-110kV", "R-2", "R-3"], res.Rows.Select(r => r.Relay));
        // The 110 kV relay sees the 20 kV fault current divided by the ratio 110/20
        var f2 = ShortCircuitAnalysis.Calculate(net, 2, FaultType.ThreePhase, sc);
        Assert.Equal(f2.IkKa * 1000 * 20 / 110, res.Rows[0].CoordinationCurrentA!.Value, 0);
        foreach (var row in res.Rows.Where(r => r.MarginS is not null)) Assert.InRange(row.MarginS!.Value, 0.3 - 1e-6, 0.45);
    }

    [Fact]
    public void Fuse_LogLogInterpolationAndI2t()
    {
        var f = Fuse.FromI2t("63A gG", 63, 30_000);
        Assert.Equal(30_000.0 / (1000 * 1000), f.OperatingTime(1000), 4);     // t = I²t / I²
        Assert.True(double.IsPositiveInfinity(f.OperatingTime(90)));          // below 1.6·In
        var table = new Fuse("table", 100, [(200, 100), (1000, 1), (5000, 0.01)]);
        // log-log midpoint between (200,100) and (1000,1): √(200·1000) = 447.2 A → √(100·1) = 10 s
        Assert.Equal(10, table.OperatingTime(Math.Sqrt(200 * 1000)), 6);
        var tcc = Tcc.Curve(table, 100, 10_000, 50);
        Assert.All(tcc.Zip(tcc.Skip(1)), p => Assert.True(p.Second.TimeS <= p.First.TimeS));
        // fuse–relay coordination
        var relay = new OvercurrentRelay("upstream", 300, CurveType.IecVeryInverse, 0.3);
        Assert.True(Coordination.Check(f, relay, 2000, ctiS: 0.2).Coordinated);
    }

    [Fact]
    public void Distance_MhoZones_GroundLoopMeasuresFaultDistance()
    {
        var zl = Phasor.FromPolarDeg(10, 80);
        var relay = DistanceRelay.Mho("21", zl, shortestNextLineOhm: Phasor.FromPolarDeg(6, 80), longestNextLineOhm: Phasor.FromPolarDeg(12, 80));
        Assert.Equal(8, relay.Zones[0].ReachMagnitudeOhm, 9);   // 80 %
        Assert.Equal(13, relay.Zones[1].ReachMagnitudeOhm, 9);  // max(12, 10 + 3)
        Assert.Equal(26.4, relay.Zones[2].ReachMagnitudeOhm, 9); // 1.2·(10 + 12)
        // SLG at 50 % with Z0 = 3·Z1: V_a = d·(Z1·(I1 + I2) + Z0·I0); compensated loop gives d·Z1
        var z0 = 3 * zl; var i0 = new Complex(1000, -300);
        var va = 0.5 * (zl * 2 * i0 + z0 * i0);
        var k0 = DistanceRelay.ResidualCompensation(zl, z0);
        var zapp = DistanceRelay.ApparentImpedanceGround(va, 3 * i0, 3 * i0, k0);
        Assert.Equal(5, zapp.Magnitude, 9);
        Assert.Equal(1, relay.OperatingZone(zapp)!.Zone);
        Assert.Equal(2, relay.OperatingZone(0.95 * zl)!.Zone);
        Assert.Null(relay.OperatingZone(new Complex(-5, 0)));                  // reverse fault
        Assert.Equal(0.3, relay.OperatingTime(1.1 * zl), 9);
        var c9 = DistanceRelay.ForBranch(TestCases.Case9(), 4);
        Assert.True(c9.Zones[2].ReachMagnitudeOhm > c9.Zones[1].ReachMagnitudeOhm);
    }

    [Fact]
    public void Differential_ThroughFaultRestrainsInternalFaultTrips()
    {
        var r = new PercentDifferentialRelay { MinimumPickupPu = 0.3, Slope1 = 0.25, Slope2 = 0.6, BreakpointPu = 3 };
        // External fault: 10 pu through, 5 % CT mismatch → Id = 0.5, Ir = 9.75; threshold = 0.75 + 0.6·6.75 = 4.8 → restrain
        var ext = r.Evaluate(new Complex(10, 0), new Complex(-9.5, 0));
        Assert.False(ext.Trip);
        Assert.Equal(4.8, ext.ThresholdPu, 9);
        // Internal fault fed from both sides
        Assert.True(r.Evaluate(new Complex(3, 0), new Complex(1, 0)).Trip);
        // Inrush (energising one side) with 30 % second harmonic is blocked; same without harmonics trips
        Assert.False(r.Evaluate(new Complex(2, 0), Complex.Zero, 0.30).Trip);
        Assert.True(r.Evaluate(new Complex(2, 0), Complex.Zero).Trip);
        Assert.True(r.Evaluate(new Complex(9, 0), Complex.Zero, 0.30).Unrestrained);
        // 40 MVA at 20 kV: In = 1154.7 A
        Assert.Equal(1, PercentDifferentialRelay.ToPerUnit(1154.7, 40, 20).Magnitude, 3);
    }

    [Fact]
    public void BreakerDuty_ComparesWithIec60909Currents()
    {
        var b = new NetworkBuilder("bd", 100, 50);
        b.AddBus(1, 20, BusType.Slack).AddBus(2, 20);
        b.AddLine(1, 2, 1, 0.1, 0.3);
        var study = new ShortCircuitStudy(b.Build(), new ShortCircuitOptions { ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 500 }] });
        var res = BreakerDuty.CheckAll(study, [(2, new BreakerRating("CB-ok", 24, 25)), (2, new BreakerRating("CB-small", 24, 8))]);
        Assert.True(res[0].Adequate);
        Assert.False(res[1].Adequate);
        var f3 = study.Calculate(2, FaultType.ThreePhase);
        Assert.Equal(100 * f3.IpKa / (2.5 * 25), res[0].MakingDutyPercent, 3);
        Assert.False(BreakerDuty.Check(new BreakerRating("LV", 12, 50), f3).VoltageOk);
    }

    [Fact]
    public void ArcFlash_Ieee1584_2002_HandCalculation()
    {
        // 480 V switchgear, Ibf = 30 kA, G = 32 mm, grounded, t = 0.1 s, D = 610 mm (IEEE 1584-2002 eqs. 1, 3, 6):
        // lg Ia = −0.097 + 0.662·lg30 + 0.0966·0.48 + 0.000526·32 + 0.5588·0.48·lg30 − 0.00304·32·lg30 = 1.19656 → Ia = 15.72 kA
        // lg En = −0.555 − 0.113 + 1.081·1.19656 + 0.0011·32 → En = 4.578 J/cm²; E = 4.184·1.5·En·0.5 = 14.37 J/cm² = 3.434 cal/cm²
        var r = ArcFlash.Calculate(new ArcFlashInput(0.48, 30, 0.1, 610, ArcEquipment.Switchgear));
        Assert.Equal(15.724, r.ArcingCurrentKa, 2);
        Assert.Equal(3.4335, r.IncidentEnergyCalCm2, 3);
        Assert.Equal(1245.3, r.ArcFlashBoundaryMm, 0);                     // where E = 1.2 cal/cm² (x = 1.473)
        Assert.Equal(1, r.PpeCategory);
        // 13.8 kV switchgear, 20 kA, 0.2 s, 910 mm: Ia = 19.18 kA, E = 5.23 cal/cm² → category 2
        var mv = ArcFlash.Calculate(new ArcFlashInput(13.8, 20, 0.2, 910));
        Assert.Equal(19.18, mv.ArcingCurrentKa, 2);
        Assert.Equal(5.2255, mv.IncidentEnergyCalCm2, 3);
        Assert.Equal(2, mv.PpeCategory);
        // Energy is proportional to arc duration
        Assert.Equal(2 * mv.IncidentEnergyCalCm2, ArcFlash.Calculate(new ArcFlashInput(13.8, 20, 0.4, 910)).IncidentEnergyCalCm2, 9);
    }

    [Fact]
    public void ArcFlash_WithDevice_Uses85PercentCheck()
    {
        var relay = new OvercurrentRelay("51", 1000, CurveType.IecExtremelyInverse, 0.3);
        var input = new ArcFlashInput(0.48, 30, 0, 455, ArcEquipment.MotorControlCenter);
        var r = ArcFlash.Calculate(input, relay, breakerOpeningS: 0.05);
        double ia = ArcFlash.ArcingCurrentKa(0.48, 30, ArcEquipment.MotorControlCenter);
        // EI curve: 85 % arcing current clears in 0.169 s instead of 0.136 s, so the reduced-current energy governs
        Assert.Equal(0.85 * ia, r.ArcingCurrentKa, 6);
        Assert.Equal(relay.OperatingTime(0.85 * ia * 1000) + 0.05, r.ArcDurationS, 9);
        Assert.Equal(-1, ArcFlash.PpeCategory(50));
    }
}
