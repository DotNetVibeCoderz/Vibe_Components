using System.Numerics;
using Electrical.Core.Network;
using Electrical.Dynamics;

namespace Electrical.Tests;

public class DynamicModelTests
{
    private const string Case9Dyr = """
        // WSCC 9-bus dynamic data (Anderson & Fouad reactances, pu on 100 MVA)
        @! PSS/E style comment line
        1 'GENSAL' 1  8.96 0.03 0.05 23.64 0  0.146 0.0969 0.0608 0.05 0.0336 0 0 /  hydro unit
        2 'GENROU' 1  6.0 0.03 0.535 0.05 6.4 0
                      0.8958 0.8645 0.1198 0.1969 0.1 0.0521
                      0.05 0.2 /                    multi-line record with saturation
        3 'GENROU' '1' 5.89 0.03 0.6 0.05 3.01 0 1.3125 1.2578 0.1813 0.25 0.15 0.0742 0 0 /
        1 'SEXS' 1  0.1 10 100 0.1 0 5 /
        2 'ESAC4A' 1  0 1 -1 1 10 200 0.015 5.64 -4.53 0 /
        3 'IEEET1' 1  0 20 0.2 5 -5 1 0.314 0.063 0.35 0 0 0 0 0 /
        1 'HYGOV' 1  0.05 0.3 5 0.05 0.5 0.2 2 0 1 1.2 0.5 0.08 /
        2 'TGOV1' 1  0.05 0.5 5 0 1 3 0 /
        3 'GAST' 1  0.05 0.4 0.1 3 2 2 2 0 0 /
        2 'PSS2B' 1  1 0 3 0 5 1  10 10 0 10 0 10 0.49 1 0.5 0.1 20 0.05 0.02 3 5.4 0 0 2 -2 2 -2 0.2 -0.2 /
        3 'IEEEST' 1  1 0  0 0 0 0 0 0  0.05 0.02 3 5.4 10 10  10 0.1 -0.1 0 0 /
        2 'USRMDL' 1  1 2 3 /
        99 'TGOV1' 1  0.05 0.5 1 0 1 3 0 /
        """;

    [Fact]
    public void DyrReader_ParsesRecords_SkipsUnknown_AndInitializesAllModels()
    {
        var net = TestCases.Case9();
        var set = DyrReader.Parse(Case9Dyr, net);
        Assert.Equal(2, set.Warnings.Count);
        Assert.Contains(set.Warnings, w => w.Contains("USRMDL"));
        Assert.Contains(set.Warnings, w => w.Contains("bus 99"));
        Assert.IsType<Gensal>(set.Generators[0].Machine);
        var g2 = Assert.IsType<Genrou>(set.Generators[1].Machine);
        Assert.Equal(0.2, g2.S12);
        Assert.IsType<Esac4a>(set.Generators[1].Exciter);
        Assert.IsType<Pss2b>(set.Generators[1].Stabilizer);
        Assert.IsType<Hygov>(set.Generators[0].Governor);
        Assert.IsType<Gast>(set.Generators[2].Governor);
        Assert.IsType<Ieeest>(set.Generators[2].Stabilizer);
        var sys = DynamicSystem.Build(net, set);
        var (d, mm) = sys.InitializationResidual();
        Assert.True(d < 1e-9 && mm < 1e-9, $"{d:E2} {mm:E2}");
        var flat = DynamicSimulation.Run(sys, [], new SimulationOptions { EndTimeS = 5 });
        Assert.True(sys.InitialState.Select((x, i) => Math.Abs(flat.FinalState[i] - x)).Max() < 1e-7);
        Assert.True(DynamicSmallSignal.Analyze(sys).IsStable());
        Assert.Contains("GENROU", DynamicModelLibrary.Names);
        Assert.True(DynamicModelLibrary.TryGetInfo("REGC_A", out var info) && info!.Category == ModelCategory.Converter);
    }

    [Fact]
    public void DyrReader_KundurData_MatchesCodeBuiltModels()
    {
        var c = DynamicTestCases.KundurTwoArea(KundurExcitation.StaticExciterWithPss);
        Assert.Empty(c.Models.Warnings);
        var g = c.Models.Generators[2];
        var m = Assert.IsType<Genrou>(g.Machine);
        Assert.Equal(6.175, m.H);
        Assert.Equal(0.25, m.XdSubtransient);
        Assert.Equal(200, Assert.IsType<Esst1a>(g.Exciter).Ka);
        Assert.Equal(40, c.Build().StateCount); // 4 × (6 GENROU + 1 ESST1A + 3 IEEEST)
    }

    public static TheoryData<string> Exciters => ["SEXS", "ESAC4A", "IEEET1", "ESST1A"];

    [Theory]
    [MemberData(nameof(Exciters))]
    public void Exciters_InitializeAndFollowVrefStep(string name)
    {
        ExciterModel ex = name switch
        {
            "SEXS" => new Sexs(0.1, 10, 100, 0.1, 0, 5),
            "ESAC4A" => new Esac4a(0, 1, -1, 1, 10, 200, 0.015, 5.64, -4.53, 0),
            "IEEET1" => new Ieeet1(0, 20, 0.2, 5, -5, 1, 0.314, 0.063, 0.35),
            _ => new Esst1a(0.01, 99, -99, 0, 0, 0, 0, 200, 0, 9, -9, 9, -9),
        };
        var c = DynamicTestCases.Case9();
        c.Models.Add(1, ex);
        var sys = c.Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-9);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.ReferenceStep(0.2, 1, "Vref", 0.02)], new SimulationOptions { EndTimeS = 8 });
        Assert.Equal(0, r.ConvergenceFailures);
        double dv = r.TerminalVoltagePu[1][^1] - r.TerminalVoltagePu[1][0];
        Assert.InRange(dv, 0.004, 0.025);
        Assert.True(r.ReactivePowerMvar[1][^1] > r.ReactivePowerMvar[1][0] + 5);
    }

    public static TheoryData<string> Governors => ["TGOV1", "HYGOV", "GAST", "IEEEG1"];

    [Theory]
    [MemberData(nameof(Governors))]
    public void Governors_InitializeAndPickUpLoad(string name)
    {
        GovernorModel gov = name switch
        {
            "TGOV1" => new Tgov1(0.05, 0.5, 5, 0, 1, 3, 0),
            "HYGOV" => new Hygov(0.05, 0.1, 2, 0.05, 0.5, 0.2, 2, 0, 1, 1.2, 0, 0.08),
            "GAST" => new Gast(0.05, 0.4, 0.1, 3, 2, 2, 2, 0, 0),
            _ => new Ieeeg1(20, 0, 0, 0.1, 1, -1, 2, 0, 0.3, 0.3, 0, 7, 0.4, 0, 0.6, 0.3, 0, 0, 0, 0),
        };
        var c = DynamicTestCases.Case9();
        c.Models.Add(1, gov);
        var sys = c.Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-9);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.LoadStep(0.5, 5, 10)], new SimulationOptions { EndTimeS = 40, TimeStepS = 0.02 });
        Assert.True(r.Stable);
        // the governed unit alone restores the power balance: ΔPm ≈ 0.1 pu (constant-Z load: slightly less), speed settles on the
        // droop line Δω = −R_eff·ΔPm with R_eff = R (TGOV1, GAST), 1/K (IEEEG1, ΣK = 1) and R/At (HYGOV, gate-to-power gain At)
        double dpm = r.MechanicalPowerPu[1][^1] - r.MechanicalPowerPu[1][0];
        Assert.InRange(dpm, 0.07, 0.11);
        double reff = name == "HYGOV" ? 0.05 / 1.2 : 0.05;
        double dw = r.SpeedPu[1][^1] - 1;
        Assert.True(Math.Abs(dw + reff * dpm) < 0.03 * reff * dpm, $"Δω = {dw:E3}, droop line {-reff * dpm:E3}"); // HYGOV still creeping (−0.9 %) at 40 s
    }

    [Fact]
    public void Pss2b_DampsKundurInterAreaMode()
    {
        var c = DynamicTestCases.KundurTwoArea(KundurExcitation.StaticExciter);
        for (int g = 0; g < 4; g++)
            c.Models.Add(g, new Pss2b(StabilizerInput.SpeedDeviation, StabilizerInput.ElectricalPower, 5, 1, 10, 10, 0, 10, 0, 10, 10 / (2 * 6.5), 1,
                0.5, 0.1, 20, 0.05, 0.02, 3, 5.4, 0, 0, 0.2, -0.2));
        var sys = c.Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-9);
        var ss = DynamicSmallSignal.Analyze(sys);
        Assert.True(ss.IsStable());
        var inter = ss.ElectromechanicalModes()[0];
        Assert.True(inter.DampingRatio > 0.05, $"inter-area ζ with PSS2B = {inter.DampingRatio:P1} at {inter.FrequencyHz:0.00} Hz");
    }

    [Fact]
    public void Ibr_FaultRideThrough_InjectsReactiveCurrent_AndRampsActivePower()
    {
        var c = DynamicTestCases.InverterBehindLine();
        var sys = c.Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-12);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.Fault(0.1, 2, new Complex(0, 0.08)), DynamicEvent.Clear(0.25, 2)],
            new SimulationOptions { EndTimeS = 1.5 });
        Assert.Equal(0, r.ConvergenceFailures);
        int u = r.UnitIndex("PV1");
        double At(double[] ch, double t) => r.At(ch, t);
        // during the dip (V ≈ 0.45 pu): Iqinj = KQV·(1 − V − 0.05) ≈ 1.0 pu, Q priority leaves Ip ≤ √(IMAX² − Iq²), LVACM cuts Ip further
        Assert.InRange(At(r.TerminalVoltagePu[u], 0.2), 0.35, 0.55);
        Assert.InRange(At(r.ReactiveCurrentPu[u], 0.2), 0.85, 1.05);
        Assert.True(At(r.ActiveCurrentPu[u], 0.2) < 0.1);
        Assert.True(At(r.ReactivePowerMvar[u], 0.2) > 30);
        // recovery: active current ramps at RRPWR = 2 pu/s → ≈ 10 MW per 50 ms, full output after ≈ 0.4 s
        double p1 = At(r.ActivePowerMw[u], 0.40), p2 = At(r.ActivePowerMw[u], 0.50);
        Assert.InRange((p2 - p1) / 0.1, 0.9 * 200, 1.1 * 200); // MW/s on 100 MVA
        Assert.True(At(r.ActivePowerMw[u], 0.45) < 0.75 * 80);
        Assert.Equal(80, r.ActivePowerMw[u][^1], 1);
        Assert.Equal(1.0, r.TerminalVoltagePu[u][^1], 3);
    }

    [Fact]
    public void Repca_VoltageControl_TracksReferenceStep()
    {
        var c = DynamicTestCases.InverterBehindLine(plantController: true);
        var sys = c.Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-12);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.ReferenceStep(0.5, 1, "Vref", 0.02)], new SimulationOptions { EndTimeS = 6 });
        int u = r.UnitIndex("PV1");
        double v0 = r.TerminalVoltagePu[u][0];
        Assert.Equal(v0 + 0.02, r.TerminalVoltagePu[u][^1], 3);          // PI removes the error
        Assert.InRange(r.ReactivePowerMvar[u][^1] - r.ReactivePowerMvar[u][0], 10, 25); // ΔQ ≈ ΔV/X_th ≈ 0.02/0.12 pu
        Assert.Equal(80, r.ActivePowerMw[u][^1], 1);
    }

    [Fact]
    public void Repca_FrequencyDroop_InIslandFormedByGridFormingInverter()
    {
        // GFM1 (droop MP = 0.05) sets the island frequency; PV2 (REGC/REEC/REPC, Q control, FFLAG = 1, DUP = 20, dead band 0.0006 pu)
        // follows it: Δf = −MP·ΔP1 and ΔP2 = DUP·(|Δf| − 0.0006).
        var c = DynamicTestCases.GridFormingPair();
        var m = new DynamicModelSet();
        m.Add(0, new GridFormingInverter(GridFormingMode.Droop, xf: 0.15, mp: 0.05, mq: 0.05, tp: 0.02, imax: 1.5));
        m.Add(1, new Regca(), new Reeca(), new Repca(fFlag: true) { RefFlag = false, Kp = 0.5, Ki = 2, Ddn = 20, Dup = 20, Kpg = 0.5, Kig = 5 });
        var sys = DynamicSystem.Build(c.Network, m);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.LoadStep(1, 3, 10)], new SimulationOptions { EndTimeS = 12 });
        Assert.Equal(0, r.ConvergenceFailures);
        double dp1 = (r.ActivePowerMw[0][^1] - r.ActivePowerMw[0][0]) / 100, dp2 = (r.ActivePowerMw[1][^1] - r.ActivePowerMw[1][0]) / 100;
        double df = r.SpeedPu[0][^1] - 1;
        Assert.Equal(-0.05 * dp1, df, 6);
        Assert.Equal(20 * (-df - 0.0006), dp2, 4);       // ours: ΔP1 = 5.41 MW, ΔP2 = 4.21 MW, Δf = −0.0027 pu
        Assert.Equal(df, r.SpeedPu[1][^1] - 1, 6);        // REPC frequency measurement agrees with the GFM frequency
    }

    [Theory]
    [InlineData(GridFormingMode.Droop)]
    [InlineData(GridFormingMode.VirtualSynchronousMachine)]
    public void GridForming_LoadStep_SharedInverselyToDroop(GridFormingMode mode)
    {
        var sys = DynamicTestCases.GridFormingPair(mode).Build();
        Assert.True(sys.InitializationResidual().MaxDerivative < 1e-9);
        var r = DynamicSimulation.Run(sys, [DynamicEvent.LoadStep(1, 3, 10)], new SimulationOptions { EndTimeS = 8 });
        Assert.Equal(0, r.ConvergenceFailures);
        double d1 = r.ActivePowerMw[0][^1] - r.ActivePowerMw[0][0], d2 = r.ActivePowerMw[1][^1] - r.ActivePowerMw[1][0];
        Assert.InRange(d1 + d2, 9, 10.5);                  // constant-impedance load at V ≈ 0.98 pu
        Assert.Equal(0.5, d1 / d2, 2);                    // MP1 = 0.05, MP2 = 0.025 → 1 : 2 sharing
        Assert.Equal(1 - 0.05 * d1 / 100, r.SpeedPu[0][^1], 5); // common frequency on the droop line
        Assert.Equal(r.SpeedPu[0][^1], r.SpeedPu[1][^1], 6);
    }

    [Fact]
    public void GridForming_CurrentLimit_HoldsDuringFault()
    {
        var sys = DynamicTestCases.GridFormingPair().Build();
        var r = DynamicSimulation.Run(sys, [DynamicEvent.Fault(0.5, 3, new Complex(0, 0.02)), DynamicEvent.Clear(0.6, 3)], new SimulationOptions { EndTimeS = 3 });
        Assert.Equal(0, r.ConvergenceFailures);
        Assert.True(r.FallbackSteps <= 2, $"{r.FallbackSteps} fallback steps"); // current-limit switching at fault clearing
        int k = Array.FindIndex(r.Time, t => t >= 0.55);
        for (int u = 0; u < 2; u++)
        {
            double i = Math.Sqrt(Math.Pow(r.ActiveCurrentPu[u][k], 2) + Math.Pow(r.ReactiveCurrentPu[u][k], 2));
            Assert.Equal(1.5, i, 3); // IMAX
        }
        Assert.Equal(r.ActivePowerMw[0][0], r.ActivePowerMw[0][^1], 1); // recovers to the pre-fault operating point
    }
}
