using Electrical.Distribution;

namespace Electrical.Tests;

public class DistributionOpenDssTests
{
    /// <summary>EPRI OpenDSS test case "4Bus-YY-Bal.DSS" (IEEE 4-bus, Y-Y step-down, balanced), using WireData + LineGeometry.</summary>
    private const string FourBusYyBal = """
        clear
        Set DefaultBaseFrequency=60
        ! IEEE 4-bus test case   Y-Y Stepdown Balanced
        new circuit.4BusYYbal basekV=12.47 phases=3
        ~ mvasc3=200000 200000
        set earthmodel=carson
        new wiredata.conductor Runits=mi Rac=0.306 GMRunits=ft GMRac=0.0244  Radunits=in Diam=0.721 normamps=530
        new wiredata.neutral   Runits=mi Rac=0.592 GMRunits=ft GMRac=0.00814 Radunits=in Diam=0.563 normamps=340
        new linegeometry.4wire nconds=4 nphases=3 reduce=yes
        ~ cond=1 wire=conductor units=ft x=-4   h=28
        ~ cond=2 wire=conductor units=ft x=-1.5 h=28
        ~ cond=3 wire=conductor units=ft x=3    h=28
        ~ cond=4 wire=neutral   units=ft x=0    h=24
        new line.line1 geometry=4wire length=2000 units=ft bus1=sourcebus bus2=n2
        new transformer.t1 xhl=6
        ~ wdg=1 bus=n2 conn=wye kV=12.47 kVA=6000 %r=0.5
        ~ wdg=2 bus=n3 conn=wye kV=4.16  kVA=6000 %r=0.5
        new line.line2 bus1=n3 bus2=n4 geometry=4wire length=2500 units=ft
        new load.load1 phases=3 bus1=n4 conn=wye kV=4.16 kW=5400 pf=0.9  model=1
        ~ vminpu=0.75
        set voltagebases=[12.47, 4.16]
        calcvoltagebases
        solve
        """;

    [Fact]
    public void FourBus_WireDataAndGeometry_MatchesPublished()
    {
        var net = OpenDssReader.Parse(FourBusYyBal);
        Assert.Equal(4, net.Buses.Count);
        Assert.Equal(4.16, net.Bus("n4").BaseKvLL);
        Assert.Equal(12.47, net.Bus("n2").BaseKvLL);
        Assert.Contains(net.Warnings, w => w.Contains("ideal"));
        var r = UnbalancedPowerFlow.Solve(net);
        Assert.True(r.Converged);
        // IEEE 4 Node Test Feeder, Gr.Y-Gr.Y step-down balanced: V4 = 1918 / 2061 / 1981 V
        var v4 = r.Bus("n4").VoltageV;
        Assert.Equal(1918, v4[0].Magnitude, 1918 * 0.005);
        Assert.Equal(2061, v4[1].Magnitude, 2061 * 0.005);
        Assert.Equal(1981, v4[2].Magnitude, 1981 * 0.005);
        Assert.Equal(5400, r.LoadKva.Real, 1e-6);
    }

    [Fact]
    public void Ieee13Dss_FixedTaps_MatchPublishedAndCodeModel()
    {
        // Same script with the "alternate solution" of the OpenDSS distribution: taps forced to the published 10/8/11 and controls off.
        var script = DistributionTestCases.Ieee13NodeDss + """

            Transformer.Reg1.Taps=[1.0 1.0625]
            Transformer.Reg2.Taps=[1.0 1.0500]
            Transformer.Reg3.Taps=[1.0 1.06875]
            Set Controlmode=OFF
            """;
        var net = OpenDssReader.Parse(script);
        Assert.Empty(net.Controllers);
        Assert.Equal(3, net.Regulators.Count);
        Assert.Equal(new[] { 10, 8, 11 }, net.Regulators.Select(r => r.Taps[r.Phases.Indices()[0]]));
        Assert.Equal(115, net.Bus("sourcebus").BaseKvLL);
        Assert.Equal(0.48, net.Bus("634").BaseKvLL);
        Assert.Equal(PhaseSet.BC, net.Line("632645").Phases);
        Assert.Equal(new[] { 2, 1 }, net.Line("632645").PhaseOrder);
        var dss = UnbalancedPowerFlow.Solve(net);
        var code = UnbalancedPowerFlow.Solve(DistributionTestCases.Ieee13Node());
        Assert.True(dss.Converged);
        foreach (var (bus, pub) in DistributionTestCases.Ieee13NodePublished)
            for (int p = 0; p < 3; p++)
            {
                if (double.IsNaN(pub[p].Magnitude)) continue;
                var b = dss.Bus(bus);
                Assert.True(Math.Abs(b.VmPu[p] - pub[p].Magnitude) / pub[p].Magnitude < 0.005, $"{bus}.{p}: {b.VmPu[p]:0.0000} vs {pub[p].Magnitude}");
                Assert.True(Math.Abs(Math.IEEERemainder(b.VaDeg[p] - pub[p].AngleDeg, 360)) < 0.5, $"{bus}.{p} angle {b.VaDeg[p]:0.00}");
                Assert.Equal(code.Bus(bus).VmPu[p], b.VmPu[p], 3);
            }
    }

    [Fact]
    public void Ieee13Dss_RegControlsBecomeRegulatorControllers()
    {
        var net = OpenDssReader.Parse(DistributionTestCases.Ieee13NodeDss);
        var ctrls = net.Controllers.OfType<VoltageRegulatorController>().ToList();
        Assert.Equal(3, ctrls.Count);
        Assert.All(ctrls, c => { Assert.Equal(122, c.VoltageLevel); Assert.Equal(700, c.CtPrimary); Assert.Equal(9, c.X); });
        var r = UnbalancedPowerFlow.Solve(net);
        Assert.True(r.Converged);
        var code = UnbalancedPowerFlow.Solve(DistributionTestCases.Ieee13Node(automaticRegulator: true));
        Assert.Equal(code.RegulatorTaps["Reg"][0], r.RegulatorTaps["reg1"][0]);
        Assert.Equal(code.RegulatorTaps["Reg"][1], r.RegulatorTaps["reg2"][1]);
        Assert.Equal(code.RegulatorTaps["Reg"][2], r.RegulatorTaps["reg3"][2]);
        // Delta-wye substation transformer shifts the 115 kV source (angle 30°) to 0° at node 650
        Assert.Equal(0, r.Bus("650").VaDeg[0], 1);
        Assert.Equal(1.0001, r.Bus("650").VmPu[0], 3);
    }

    [Fact]
    public void Syntax_RedirectCommentsRpnAndContinuation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eln-dss-" + Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(dir, "codes"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "codes", "lc.dss"), """
                // sequence line code, Ω and nF per km
                New LineCode.seq nphases=3 r1=0.2 x1=0.4 r0=0.6 x0=1.2 c1=10 c0=5 units=km
                """);
            File.WriteAllText(Path.Combine(dir, "master.dss"), """
                Clear
                New Circuit.test basekv=11 pu=1.02 bus1=src   ! comment after a command
                /* block
                   comment */
                Redirect codes/lc.dss
                New Line.l1 bus1=src.1.2.3 bus2=b1.1.2.3 linecode=seq length=(1 2 +) units=km
                more phases=3
                New Load.ld bus1=b1 phases=3 conn=delta kV=11 kVA=1000 pf=0.95 model=2
                New Capacitor.c1 bus1=b1 phases=3 kvar=300 kv=11
                New Energymeter.m1 element=line.l1
                Edit Load.ld kW=800 kvar=300
                Set voltagebases=[11]
                Calcv
                """);
            var net = OpenDssReader.ParseFile(Path.Combine(dir, "master.dss"));
            Assert.Equal(3.0, net.Line("l1").LengthKm, 12);
            var z = net.Line("l1").Code.ZOhmPerKm;
            Assert.Equal((2 * 0.2 + 0.6) / 3, z[0, 0].Real, 12);
            Assert.Equal((1.2 - 0.4) / 3, z[0, 1].Imaginary, 12);
            var ld = net.Load("ld");
            Assert.Equal(LoadConnection.Delta, ld.Connection);
            Assert.Equal(LoadModel.ConstantImpedance, ld.Model);
            Assert.Equal(800, ld.TotalKva.Real, 9);
            Assert.Equal(300, ld.TotalKva.Imaginary, 9);
            Assert.Equal(100, net.Capacitor("c1").Kvar[1], 9);
            Assert.Contains(net.Warnings, w => w.Contains("energymeter"));
            Assert.Equal(1.02, net.Source!.VoltagePu);
            var r = UnbalancedPowerFlow.Solve(net);
            Assert.True(r.Converged);
            Assert.InRange(r.Bus("b1").VmPu[0], 0.95, 1.02);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ieee13Dss_SubstationTransformerAddsNoSpuriousLoss()
    {
        // The DSS script includes the 115/4.16 kV 'Sub' transformer (%R 0.0005, XHL 0.008 written in RPN). Its copper loss is a
        // few watts, so total losses must match the code-built feeder (no substation transformer). A real-valued reference shunt
        // on the delta winding used to absorb ~20.7 kW here.
        var code = UnbalancedPowerFlow.Solve(DistributionTestCases.Ieee13Node(true));
        var dss = UnbalancedPowerFlow.Solve(OpenDssReader.Parse(DistributionTestCases.Ieee13NodeDss));
        Assert.True(dss.Converged);
        Assert.InRange(dss.LossesKw - code.LossesKw, -1.0, 1.0);
        var sub = dss.Branches.First(b => b.From.Equals("sourcebus", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(sub.LossesKva.Real, 0, 0.1);
    }
}
