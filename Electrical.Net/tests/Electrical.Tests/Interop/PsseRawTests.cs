using Electrical.Core.Network;
using Electrical.Interop;
using Electrical.PowerFlow;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class PsseRawTests(ITestOutputHelper output)
{
    // MATPOWER case14 reference: total losses 13.393 MW (runpf, NR without Q limits).
    private static readonly PowerFlowResult Ref = PowerFlowSolver.Solve(TestCases.Case14());

    [Fact]
    public void V33Sample_ParsesAndReproducesCase14PowerFlow()
    {
        var r = PsseRaw.Read(InteropSamples.Ieee14RawV33);
        var net = r.Network;
        Assert.Equal(NetworkFormat.PsseRaw, r.Format);
        Assert.Empty(r.Warnings);
        Assert.Equal("IEEE 14 BUS TEST CASE", net.Name);
        Assert.Equal((100.0, 60.0), (net.BaseMva, net.FrequencyHz));
        Assert.Equal((14, 11, 5, 20), (net.Buses.Count, net.Loads.Count, net.Generators.Count, net.Branches.Count));
        Assert.Equal(3, net.Branches.Count(b => b.Kind == BranchKind.Transformer));
        Assert.Equal(19, net.GetBus(9).Bs);
        Assert.Equal(18, net.GetBus(8).BaseKv);
        Assert.Equal("BUS 1 HV", net.GetBus(1).Name);
        Assert.Equal(BusType.Slack, net.GetBus(1).Type);
        Assert.Equal(332.4, net.Generators[0].Pmax);
        Assert.Equal(0.978, net.Branches.Single(b => b.From == 4 && b.To == 7).Tap, 12);

        var pf = PowerFlowSolver.Solve(net);
        output.WriteLine(pf.ToString());
        Assert.True(pf.Converged);
        Assert.Equal(13.393, pf.LossesMw, 3);
        Assert.Equal(Ref.LossesMw, pf.LossesMw, 6);
        foreach (var b in Ref.Buses) Assert.Equal(b.Vm, pf.Bus(b.BusId).Vm, 9);
    }

    [Fact]
    public void V35Sample_HeadersThreeWindingAndSwitchedShunt()
    {
        var r = PsseRaw.Read(InteropSamples.Ieee14RawV35);
        var net = r.Network;
        foreach (var w in r.Warnings) output.WriteLine(w);
        Assert.Contains(r.Warnings, w => w.Contains("Three-winding transformer 4-9-8") && w.Contains("internal bus 15"));
        Assert.Contains(r.Warnings, w => w.Contains("Switched shunt at bus 9") && w.Contains("19 Mvar"));
        Assert.Equal(14, net.Buses.Count); // 13 real buses + star point (bus 7 omitted in the file)
        var star = net.GetBus(15);
        Assert.Equal(1.062, star.Vm);
        Assert.Equal("BUS 1, HV/G1", net.GetBus(1).Name);                 // quoted comma and slash
        var l12 = net.Branches.First(b => b.From == 1 && b.To == 2);
        Assert.Equal("LINE 1-2 / NORTH", l12.Name);
        Assert.Equal(12.5, l12.LengthKm);
        // star equivalent: Z1 = (Z12 + Z31 − Z23) / 2 etc. reproduces MATPOWER's 4-7, 7-9, 7-8 branches
        Assert.Equal(0.20912, net.Branches.Single(b => b.From == 4 && b.To == 15).X, 12);
        Assert.Equal(0.11001, net.Branches.Single(b => b.From == 9 && b.To == 15).X, 12);
        Assert.Equal(0.17615, net.Branches.Single(b => b.From == 8 && b.To == 15).X, 12);
        Assert.Equal(0.978, net.Branches.Single(b => b.From == 4 && b.To == 15).Tap, 12);
        Assert.Equal(0.55618, net.Branches.Single(b => b.From == 4 && b.To == 9).X, 12);   // CZ = 2 on 50 MVA
        var t56 = net.Branches.Single(b => b.From == 5 && b.To == 6);                        // CW = 2 (kV), CZ = 3
        Assert.Equal(0.932, t56.Tap, 12);
        Assert.Equal(0.25202, t56.X, 12);
        Assert.Equal(19, net.GetBus(9).Bs);
        Assert.Equal(1.06, net.Generators[0].Vset);           // generators parsed with the v35 NREG offset
        Assert.Equal(332.4, net.Generators[0].Pmax);
        Assert.Equal(100, net.Generators[0].MBase);

        var pf = PowerFlowSolver.Solve(net);
        Assert.True(pf.Converged);
        Assert.Equal(Ref.LossesMw, pf.LossesMw, 6);
        Assert.Equal(Ref.Bus(14).Vm, pf.Bus(14).Vm, 9);
        Assert.Equal(Ref.Bus(7).Vm, pf.Bus(15).Vm, 9); // star point = MATPOWER bus 7
    }

    [Theory]
    [InlineData(33)]
    [InlineData(35)]
    public void Case14_WriteRead_GivesIdenticalPowerFlow(int version)
    {
        var raw = PsseRaw.Write(TestCases.Case14(), version);
        var back = PsseRaw.Read(raw);
        Assert.Empty(back.Warnings);
        var pf = PowerFlowSolver.Solve(back.Network);
        output.WriteLine($"v{version}: losses {pf.LossesMw:0.######} MW (ref {Ref.LossesMw:0.######})");
        Assert.True(Math.Abs(pf.LossesMw - Ref.LossesMw) < 1e-6);
        foreach (var b in Ref.Buses)
        {
            Assert.Equal(b.Vm, pf.Bus(b.BusId).Vm, 10);
            Assert.Equal(b.VaDeg, pf.Bus(b.BusId).VaDeg, 8);
        }
        Assert.Equal(TestCases.Case14().GetBus(7).BaseKv, back.Network.GetBus(7).BaseKv);
    }

    [Theory]
    [InlineData("case9", 33)]
    [InlineData("case14", 35)]
    [InlineData("case33bw", 33)]
    [InlineData("case33bw", 35)]
    public void RoundTrip_IsStable(string name, int version)
    {
        var net = TestCases.ByName(name);
        var t1 = PsseRaw.Write(net, version);
        var t2 = PsseRaw.Write(PsseRaw.Read(t1).Network, version);
        Assert.Equal(t1, t2);
        var t3 = PsseRaw.Write(PsseRaw.Read(t2).Network, version);
        Assert.Equal(t2, t3);
        var a = PowerFlowSolver.Solve(net);
        var b = PowerFlowSolver.Solve(PsseRaw.Read(t1).Network);
        Assert.Equal(a.LossesMw, b.LossesMw, 9);
        if (version == 35) Assert.Contains("@!", t1); else Assert.DoesNotContain("@!", t1);
        Assert.EndsWith("Q\n", t1);
    }

    [Fact]
    public void Conversions_ZipLoadLineShuntsMagnetizingAndStatus()
    {
        const string raw = """
            0, 50.0, 33, 0, 1, 50.0 / tiny case
            T1
            T2
            1,'A', 20.0, 3, 1, 1, 1, 1.0, 0.0
            2,'B', 20.0, 1, 1, 1, 1, 1.0, 0.0
            3,'C', 0.4, 1, 2, 3, 1, 1.0, 0.0
            0 / END OF BUS DATA, BEGIN LOAD DATA
            2,'1',1,1,1, 5.0, 2.0, 3.0, 1.0, 2.0, -1.0
            3,'1',0,1,1, 1.0, 0.5
            0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
            3,'1',0, 1.0, 9.0
            0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
            1,'1', 10.0, 0.0, 99.0, -99.0, 1.02, 0, 60.0, 0.0, 0.25, 0, 0, 1, 1, 100, 50, 0
            2,'1', 1.0, 0.0, 1.0, -1.0, 1.0, 0, 10.0, 0.0, 0.2, 0, 0, 1, 0, 100, 5, 0
            0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
            1, 2,'1', 0.01, 0.1, 0.02, 30, 30, 30, 0.001, 0.002, 0.0, 0.004, 1, 1, 7.5
            0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
            2, 3, 0,'1',1,1,1, 0.0, -0.01,2,'TX',1
            0.01, 0.05, 50.0
            1.025, 0.0, 10.0, 25.0
            1.0, 0.0
            0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
            0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
            1, 1, 1.0, 1.0
            0 / END OF TWO-TERMINAL DC DATA
            Q
            """;
        var r = PsseRaw.Read(raw);
        var net = r.Network;
        Assert.Equal((50.0, 50.0), (net.BaseMva, net.FrequencyHz));
        var load = net.Loads[0];
        Assert.Equal(10.0, load.P, 12);           // PL + IP + YP
        Assert.Equal(4.0, load.Q, 12);            // QL + IQ − YQ
        Assert.Equal(new ZipModel(0.2, 0.3, 0.5), load.Model);
        Assert.False(net.Loads[1].InService);
        Assert.Equal(0, net.GetBus(3).Bs);        // shunt out of service
        Assert.Equal(0.004 * 50, net.GetBus(2).Bs - (-0.01 * 50), 12); // BJ line shunt + MAG2 at winding-1 bus
        Assert.Equal(0.002 * 50, net.GetBus(1).Bs, 12);
        Assert.Equal(0.001 * 50, net.GetBus(1).Gs, 12);
        Assert.Equal(7.5, net.Branches[0].LengthKm);
        Assert.Equal(30, net.Branches[0].RateA);
        var tx = net.Branches[1];
        Assert.Equal((1.025, 10.0, 25.0, "TX"), (tx.Tap, tx.ShiftDeg, tx.RateA, tx.Name));
        Assert.True(tx.IsTransformer);
        Assert.False(net.Generators[1].InService);
        Assert.Equal(0.25, net.Generators[0].XdSubtransient);
        Assert.Equal(60, net.Generators[0].MBase);
        Assert.Contains(r.Warnings, w => w.Contains("magnetizing"));
        Assert.Contains(r.Warnings, w => w.Contains("TWO-TERMINAL DC"));
        var pf = PowerFlowSolver.Solve(net);
        output.WriteLine(pf.ToString());
        Assert.True(pf.Converged);
    }

    [Fact]
    public void Malformed_Throws()
    {
        Assert.Throws<FormatException>(() => PsseRaw.Parse("hello world\n"));
        Assert.Throws<FormatException>(() => PsseRaw.Parse(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => PsseRaw.Write(TestCases.Case9(), 34));
    }
}
