using System.Numerics;

namespace Electrical.Distribution;

/// <summary>Transformer connection of the IEEE 4-node test feeder.</summary>
public enum Ieee4NodeConnection { GrYGrY, DeltaGrY }

/// <summary>Published phase voltage (magnitude and angle) of a test feeder node.</summary>
public readonly record struct PublishedVoltage(double Magnitude, double AngleDeg);

/// <summary>
/// IEEE PES distribution test feeders (IEEE Distribution System Analysis Subcommittee, "Radial Distribution Test Feeders",
/// IEEE PES Winter Meeting 2001 / Trans. Power Systems 6(3), 1991; data and results at the PES Test Feeder Working Group site).
/// </summary>
public static class DistributionTestCases
{
    private static Complex C(double r, double x) => new(r, x);

    // -------------------------------------------------------------------------------------------------- IEEE 4-node

    /// <summary>
    /// IEEE 4-node test feeder (Kersting, IEEE PES 2001): infinite 12.47 kV bus 1, 2000 ft line 1-2, 6000 kVA 12.47/4.16 kV transformer
    /// (Z = 1 + j6 %) 2-3, 2500 ft line 3-4 and a wye PQ load at node 4: balanced 1800 kW at 0.9 pf per phase, or unbalanced
    /// 1275 kW @ 0.85 / 1800 kW @ 0.9 / 2375 kW @ 0.95. Lines: 336,400 26/7 ACSR phases and 4/0 6/1 ACSR neutral on the Kersting
    /// Example 4.1 pole (impedance by modified Carson's equations, ρ = 100 Ω·m).
    /// </summary>
    public static DistributionNetwork Ieee4Node(Ieee4NodeConnection connection = Ieee4NodeConnection.GrYGrY, bool unbalanced = false)
    {
        var net = new DistributionNetwork { Name = $"IEEE 4-node {connection} step-down {(unbalanced ? "unbalanced" : "balanced")}", FrequencyHz = 60 };
        net.AddBus("1", 12.47); net.AddBus("2", 12.47); net.AddBus("3", 4.16); net.AddBus("4", 4.16);
        net.SetSource("1");
        var code = Ieee4NodeLine().ToLineCode("ieee4-line", 530);
        net.AddLine("1-2", "1", "2", code, 2000, LengthUnit.Foot);
        net.AddTransformer("T1", "2", "3", connection == Ieee4NodeConnection.GrYGrY ? TransformerConnection.WyeGWyeG : TransformerConnection.DeltaWyeG,
            6000, 12.47, 4.16, 1, 6);
        net.AddLine("3-4", "3", "4", code, 2500, LengthUnit.Foot);
        static Complex S(double kw, double pf) => new(kw, kw * Math.Tan(Math.Acos(pf)));
        if (unbalanced) net.AddWyeLoad("L4", "4", LoadModel.ConstantPower, S(1275, 0.85), S(1800, 0.9), S(2375, 0.95));
        else net.AddWyeLoad("L4", "4", LoadModel.ConstantPower, S(1800, 0.9), S(1800, 0.9), S(1800, 0.9));
        return net;
    }

    /// <summary>Line impedance of the IEEE 4-node feeder (Kersting Example 4.1 geometry).</summary>
    public static LineImpedance Ieee4NodeLine() =>
        LineConstants.Overhead(OverheadConfiguration.Spacing500(ConductorLibrary.Acsr336_4_26_7, ConductorLibrary.Acsr4_0_6_1));

    /// <summary>
    /// Published IEEE 4-node results (phase-to-neutral volts and degrees; for the Δ-GrY case node 2 is line-to-line V_ab, V_bc, V_ca)
    /// for nodes 2, 3, 4. Source: IEEE 4 Node Test Feeder results, as reproduced in the LBNL Modelica Buildings library validation
    /// (Buildings.Electrical.AC.ThreePhasesUnbalanced.Validation.IEEETests.Test4NodesFeeder).
    /// </summary>
    public static IReadOnlyDictionary<string, PublishedVoltage[]> Ieee4NodePublished(Ieee4NodeConnection connection, bool unbalanced) =>
        (connection, unbalanced) switch
        {
            (Ieee4NodeConnection.GrYGrY, false) => new Dictionary<string, PublishedVoltage[]>
            {
                ["2"] = [new(7107, -0.3), new(7140, -120.3), new(7121, 119.6)],
                ["3"] = [new(2247, -3.7), new(2269, -123.5), new(2256, 116.4)],
                ["4"] = [new(1918, -9.1), new(2061, -128.3), new(1981, 110.9)],
            },
            (Ieee4NodeConnection.GrYGrY, true) => new Dictionary<string, PublishedVoltage[]>
            {
                ["2"] = [new(7164, -0.1), new(7110, -120.2), new(7082, 119.3)],
                ["3"] = [new(2305, -2.3), new(2255, -123.6), new(2203, 114.8)],
                ["4"] = [new(2175, -4.1), new(1930, -126.8), new(1833, 102.8)],
            },
            (Ieee4NodeConnection.DeltaGrY, false) => new Dictionary<string, PublishedVoltage[]>
            {
                ["2"] = [new(12340, 29.7), new(12349, -90.4), new(12318, 149.6)],
                ["3"] = [new(2249, -33.7), new(2263, -153.4), new(2259, 86.4)],
                ["4"] = [new(1920, -39.1), new(2054, -158.3), new(1986, 80.9)],
            },
            _ => new Dictionary<string, PublishedVoltage[]>
            {
                ["2"] = [new(12350, 29.6), new(12314, -90.4), new(12333, 149.8)],
                ["3"] = [new(2290, -32.4), new(2261, -153.8), new(2214, 85.2)],
                ["4"] = [new(2157, -34.2), new(1936, -157.0), new(1849, 73.4)],
            },
        };

    // -------------------------------------------------------------------------------------------------- IEEE 13-node

    /// <summary>IEEE 13-node line configurations 601–607: phase impedance [Ω/mile] and susceptance [µS/mile] in the conductor order of the
    /// data sheet (601/602/606: a, b, c; 603: c, b; 604: a, c; 605: c; 607: a).</summary>
    public static LineCode Ieee13Config(int id) => id switch
    {
        601 => LineCode.FromOhmPerMile("601", new[,]
            {
                { C(0.3465, 1.0179), C(0.1560, 0.5017), C(0.1580, 0.4236) },
                { C(0.1560, 0.5017), C(0.3375, 1.0478), C(0.1535, 0.3849) },
                { C(0.1580, 0.4236), C(0.1535, 0.3849), C(0.3414, 1.0348) },
            }, new[,] { { 6.2998, -1.9958, -1.2595 }, { -1.9958, 5.9597, -0.7417 }, { -1.2595, -0.7417, 5.6386 } }),
        602 => LineCode.FromOhmPerMile("602", new[,]
            {
                { C(0.7526, 1.1814), C(0.1580, 0.4236), C(0.1560, 0.5017) },
                { C(0.1580, 0.4236), C(0.7475, 1.1983), C(0.1535, 0.3849) },
                { C(0.1560, 0.5017), C(0.1535, 0.3849), C(0.7436, 1.2112) },
            }, new[,] { { 5.6990, -1.0817, -1.6905 }, { -1.0817, 5.1795, -0.6588 }, { -1.6905, -0.6588, 5.4246 } }),
        603 => LineCode.FromOhmPerMile("603", new[,]
            {
                { C(1.3238, 1.3569), C(0.2066, 0.4591) },
                { C(0.2066, 0.4591), C(1.3294, 1.3471) },
            }, new[,] { { 4.6658, -0.8999 }, { -0.8999, 4.7097 } }),
        604 => LineCode.FromOhmPerMile("604", new[,]
            {
                { C(1.3238, 1.3569), C(0.2066, 0.4591) },
                { C(0.2066, 0.4591), C(1.3294, 1.3471) },
            }, new[,] { { 4.6658, -0.8999 }, { -0.8999, 4.7097 } }),
        605 => LineCode.FromOhmPerMile("605", new[,] { { C(1.3292, 1.3475) } }, new[,] { { 4.5193 } }),
        606 => LineCode.FromOhmPerMile("606", new[,]
            {
                { C(0.7982, 0.4463), C(0.3192, 0.0328), C(0.2849, -0.0143) },
                { C(0.3192, 0.0328), C(0.7891, 0.4041), C(0.3192, 0.0328) },
                { C(0.2849, -0.0143), C(0.3192, 0.0328), C(0.7982, 0.4463) },
            }, new[,] { { 96.8897, 0, 0 }, { 0, 96.8897, 0 }, { 0, 0, 96.8897 } }),
        607 => LineCode.FromOhmPerMile("607", new[,] { { C(1.3425, 0.5124) } }, new[,] { { 88.9912 } }),
        _ => throw new ArgumentOutOfRangeException(nameof(id), "IEEE 13-node configurations are 601..607."),
    };

    /// <summary>
    /// IEEE 13-node test feeder built in code from the IEEE data sheets: source (node 650) at 1.0 pu, three single-phase step regulators
    /// 650→RG60 (taps 10/8/11 fixed, or automatic with the published LDC settings: 122 V, 2 V band, PT 20, CT 700 A, R' 3 V, X' 9 V),
    /// XFM-1 4.16/0.48 kV GrY-GrY 500 kVA (1.1 + j2 %), spot loads, the distributed load of 632-671 lumped at node 670 one third from 632,
    /// capacitors at 675 (3 × 200 kvar) and 611 (100 kvar) and the closed switch 671-692.
    /// </summary>
    public static DistributionNetwork Ieee13Node(bool automaticRegulator = false)
    {
        var net = new DistributionNetwork { Name = "IEEE 13-node", FrequencyHz = 60 };
        foreach (var b in new[] { "650", "rg60", "632", "633", "645", "646", "670", "671", "680", "684", "611", "652", "692", "675" })
            net.AddBus(b, 4.16);
        net.AddBus("634", 0.48);
        net.SetSource("650", 1.0, 0);
        var reg = net.AddRegulator("Reg", "650", "rg60", 2.4, PhaseSet.ABC);
        if (automaticRegulator)
            net.Controllers.Add(new VoltageRegulatorController(reg)
            {
                VoltageLevel = 122, Bandwidth = 2, PtRatio = 20, CtPrimary = 700, R = 3, X = 9,
            });
        else { reg.SetTap(0, 10); reg.SetTap(1, 8); reg.SetTap(2, 11); }

        LineCode c601 = Ieee13Config(601), c602 = Ieee13Config(602), c603 = Ieee13Config(603), c604 = Ieee13Config(604),
            c605 = Ieee13Config(605), c606 = Ieee13Config(606), c607 = Ieee13Config(607);
        net.AddLine("650632", "rg60", "632", c601, 2000, LengthUnit.Foot);
        net.AddLine("632670", "632", "670", c601, 667, LengthUnit.Foot);
        net.AddLine("670671", "670", "671", c601, 1333, LengthUnit.Foot);
        net.AddLine("671680", "671", "680", c601, 1000, LengthUnit.Foot);
        net.AddLine("632633", "632", "633", c602, 500, LengthUnit.Foot);
        net.AddLine("632645", "632", "645", c603, 500, LengthUnit.Foot, "cb");
        net.AddLine("645646", "645", "646", c603, 300, LengthUnit.Foot, "cb");
        net.AddLine("692675", "692", "675", c606, 500, LengthUnit.Foot);
        net.AddLine("671684", "671", "684", c604, 300, LengthUnit.Foot, "ac");
        net.AddLine("684611", "684", "611", c605, 300, LengthUnit.Foot, "c");
        net.AddLine("684652", "684", "652", c607, 800, LengthUnit.Foot, "a");
        net.AddSwitch("671692", "671", "692");
        net.AddTransformer("XFM1", "633", "634", TransformerConnection.WyeGWyeG, 500, 4.16, 0.48, 1.1, 2);

        var pq = LoadModel.ConstantPower;
        net.AddWyeLoad("634", "634", pq, C(160, 110), C(120, 90), C(120, 90));
        net.AddWyeLoad("645", "645", pq, 0, C(170, 125), 0);
        net.AddDeltaLoad("646", "646", LoadModel.ConstantImpedance, 0, C(230, 132), 0);
        net.AddWyeLoad("652", "652", LoadModel.ConstantImpedance, C(128, 86), 0, 0);
        net.AddDeltaLoad("671", "671", pq, C(385, 220), C(385, 220), C(385, 220));
        net.AddWyeLoad("675", "675", pq, C(485, 190), C(68, 60), C(290, 212));
        net.AddDeltaLoad("692", "692", LoadModel.ConstantCurrent, 0, 0, C(170, 151));
        net.AddWyeLoad("611", "611", LoadModel.ConstantCurrent, 0, 0, C(170, 80));
        net.AddWyeLoad("670", "670", pq, C(17, 10), C(66, 38), C(117, 68));
        net.AddCapacitor("cap675", "675", 200, 200, 200, LoadConnection.Wye, 2.4);
        net.AddCapacitor("cap611", "611", 0, 0, 100, LoadConnection.Wye, 2.4);
        return net;
    }

    /// <summary>
    /// Published IEEE 13-node voltages [pu, degrees] (IEEE PES Distribution Test Feeders, "IEEE 13 Node Test Feeder", radial flow summary);
    /// NaN for absent phases.
    /// </summary>
    public static IReadOnlyDictionary<string, PublishedVoltage[]> Ieee13NodePublished { get; } = new Dictionary<string, PublishedVoltage[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["650"] = [new(1.0000, 0.00), new(1.0000, -120.00), new(1.0000, 120.00)],
        ["rg60"] = [new(1.0625, 0.00), new(1.0500, -120.00), new(1.0687, 120.00)],
        ["632"] = [new(1.0210, -2.49), new(1.0420, -121.72), new(1.0174, 117.83)],
        ["633"] = [new(1.0180, -2.56), new(1.0401, -121.77), new(1.0148, 117.82)],
        ["634"] = [new(0.9940, -3.23), new(1.0218, -122.22), new(0.9960, 117.34)],
        ["645"] = [new(double.NaN, double.NaN), new(1.0329, -121.90), new(1.0155, 117.86)],
        ["646"] = [new(double.NaN, double.NaN), new(1.0311, -121.98), new(1.0134, 117.90)],
        ["671"] = [new(0.9900, -5.30), new(1.0529, -122.34), new(0.9778, 116.02)],
        ["680"] = [new(0.9900, -5.30), new(1.0529, -122.34), new(0.9778, 116.02)],
        ["684"] = [new(0.9881, -5.32), new(double.NaN, double.NaN), new(0.9758, 115.92)],
        ["611"] = [new(double.NaN, double.NaN), new(double.NaN, double.NaN), new(0.9738, 115.78)],
        ["652"] = [new(0.9825, -5.25), new(double.NaN, double.NaN), new(double.NaN, double.NaN)],
        ["692"] = [new(0.9900, -5.31), new(1.0529, -122.34), new(0.9777, 116.02)],
        ["675"] = [new(0.9835, -5.56), new(1.0553, -122.52), new(0.9758, 116.03)],
    };

    /// <summary>
    /// The IEEE 13-node feeder as an OpenDSS script (after the EPRI OpenDSS distribution "IEEE13Nodeckt.dss", with the original Kersting
    /// configuration 606 and IEEE shunt capacitances in the line codes; the sub-transformer impedance is divided by 1000 as in that script so
    /// that node 650 sits at 1.0 pu).
    /// </summary>
    public const string Ieee13NodeDss = """
        Clear
        Set DefaultBaseFrequency=60
        ! IEEE 13 node test feeder
        new circuit.IEEE13Nodeckt
        ~ basekv=115 pu=1.0001 phases=3 bus1=SourceBus
        ~ Angle=30                       ! advance angle 30 deg so results agree with published angles
        ~ MVAsc3=20000 MVASC1=21000      // stiff source

        New Transformer.Sub Phases=3 Windings=2   XHL=(8 1000 /)
        ~ wdg=1 bus=SourceBus   conn=delta  kv=115  kva=5000   %r=(.5 1000 /)
        ~ wdg=2 bus=650         conn=wye    kv=4.16  kva=5000   %r=(.5 1000 /)

        ! feeder 1-phase voltage regulators
        New Transformer.Reg1 phases=1 bank=reg1 XHL=0.01 kVAs=[1666 1666]
        ~ Buses=[650.1 RG60.1] kVs=[2.4  2.4] %LoadLoss=0.01
        new regcontrol.Reg1  transformer=Reg1 winding=2  vreg=122  band=2  ptratio=20 ctprim=700  R=3   X=9
        New Transformer.Reg2 phases=1 bank=reg1 XHL=0.01 kVAs=[1666 1666]
        ~ Buses=[650.2 RG60.2] kVs=[2.4  2.4] %LoadLoss=0.01
        new regcontrol.Reg2  transformer=Reg2 winding=2  vreg=122  band=2  ptratio=20 ctprim=700  R=3   X=9
        New Transformer.Reg3 phases=1 bank=reg1 XHL=0.01 kVAs=[1666 1666]
        ~ Buses=[650.3 RG60.3] kVs=[2.4  2.4] %LoadLoss=0.01
        new regcontrol.Reg3  transformer=Reg3 winding=2  vreg=122  band=2  ptratio=20 ctprim=700  R=3   X=9

        New Transformer.XFM1  Phases=3   Windings=2  XHL=2
        ~ wdg=1 bus=633       conn=Wye kv=4.16    kva=500    %r=.55
        ~ wdg=2 bus=634       conn=Wye kv=0.480   kva=500    %r=.55

        /* line codes: IEEE configurations (Ω/mile, nF/mile) */
        New linecode.mtx601 nphases=3 BaseFreq=60
        ~ rmatrix = (0.3465 | 0.1560 0.3375 | 0.1580 0.1535 0.3414 )
        ~ xmatrix = (1.0179 | 0.5017 1.0478 | 0.4236 0.3849 1.0348 )
        ~ cmatrix = (16.711 | -5.294 15.808 | -3.341 -1.967 14.957 )
        ~ units=mi
        New linecode.mtx602 nphases=3 BaseFreq=60
        ~ rmatrix = (0.7526 | 0.1580 0.7475 | 0.1560 0.1535 0.7436 )
        ~ xmatrix = (1.1814 | 0.4236 1.1983 | 0.5017 0.3849 1.2112 )
        ~ cmatrix = (15.117 | -2.869 13.739 | -4.484 -1.748 14.389 )
        ~ units=mi
        New linecode.mtx603 nphases=2 BaseFreq=60
        ~ rmatrix = (1.3238 | 0.2066 1.3294 )
        ~ xmatrix = (1.3569 | 0.4591 1.3471 )
        ~ cmatrix = (12.376 | -2.387 12.493 )
        ~ units=mi
        New linecode.mtx604 nphases=2 BaseFreq=60
        ~ rmatrix = (1.3238 | 0.2066 1.3294 )
        ~ xmatrix = (1.3569 | 0.4591 1.3471 )
        ~ cmatrix = (12.376 | -2.387 12.493 )
        ~ units=mi
        New linecode.mtx605 nphases=1 BaseFreq=60
        ~ rmatrix = (1.3292 )
        ~ xmatrix = (1.3475 )
        ~ cmatrix = (11.988 )
        ~ units=mi
        New linecode.mtx606 nphases=3 BaseFreq=60
        ~ rmatrix = (0.7982 | 0.3192 0.7891 | 0.2849 0.3192 0.7982 )
        ~ xmatrix = (0.4463 | 0.0328 0.4041 | -0.0143 0.0328 0.4463 )
        ~ Cmatrix = [257 | 0 257 | 0 0 257]
        ~ units=mi
        New linecode.mtx607 nphases=1 BaseFreq=60
        ~ rmatrix = (1.3425 )
        ~ xmatrix = (0.5124 )
        ~ cmatrix = [236]
        ~ units=mi

        ! loads
        New Load.671 Bus1=671.1.2.3  Phases=3 Conn=Delta Model=1 kV=4.16   kW=1155 kvar=660
        New Load.634a Bus1=634.1     Phases=1 Conn=Wye  Model=1 kV=0.277  kW=160   kvar=110
        New Load.634b Bus1=634.2     Phases=1 Conn=Wye  Model=1 kV=0.277  kW=120   kvar=90
        New Load.634c Bus1=634.3     Phases=1 Conn=Wye  Model=1 kV=0.277  kW=120   kvar=90
        New Load.645 Bus1=645.2       Phases=1 Conn=Wye  Model=1 kV=2.4      kW=170   kvar=125
        New Load.646 Bus1=646.2.3    Phases=1 Conn=Delta Model=2 kV=4.16    kW=230   kvar=132
        New Load.692 Bus1=692.3.1    Phases=1 Conn=Delta Model=5 kV=4.16    kW=170   kvar=151
        New Load.675a Bus1=675.1    Phases=1 Conn=Wye  Model=1 kV=2.4  kW=485   kvar=190
        New Load.675b Bus1=675.2    Phases=1 Conn=Wye  Model=1 kV=2.4  kW=68   kvar=60
        New Load.675c Bus1=675.3    Phases=1 Conn=Wye  Model=1 kV=2.4  kW=290   kvar=212
        New Load.611 Bus1=611.3      Phases=1 Conn=Wye  Model=5 kV=2.4  kW=170   kvar=80
        New Load.652 Bus1=652.1      Phases=1 Conn=Wye  Model=2 kV=2.4  kW=128   kvar=86
        New Load.670a Bus1=670.1    Phases=1 Conn=Wye  Model=1 kV=2.4  kW=17    kvar=10
        New Load.670b Bus1=670.2    Phases=1 Conn=Wye  Model=1 kV=2.4  kW=66    kvar=38
        New Load.670c Bus1=670.3    Phases=1 Conn=Wye  Model=1 kV=2.4  kW=117  kvar=68

        New Capacitor.Cap1 Bus1=675 phases=3 kVAR=600 kV=4.16
        New Capacitor.Cap2 Bus1=611.3 phases=1 kVAR=100 kV=2.4

        ! bus 670 is the concentrated point load of the distributed load on line 632-671, 1/3 of the way from 632
        New Line.650632    Phases=3 Bus1=RG60.1.2.3   Bus2=632.1.2.3  LineCode=mtx601 Length=2000 units=ft
        New Line.632670    Phases=3 Bus1=632.1.2.3    Bus2=670.1.2.3  LineCode=mtx601 Length=667  units=ft
        New Line.670671    Phases=3 Bus1=670.1.2.3    Bus2=671.1.2.3  LineCode=mtx601 Length=1333 units=ft
        New Line.671680    Phases=3 Bus1=671.1.2.3    Bus2=680.1.2.3  LineCode=mtx601 Length=1000 units=ft
        New Line.632633    Phases=3 Bus1=632.1.2.3    Bus2=633.1.2.3  LineCode=mtx602 Length=500  units=ft
        New Line.632645    Phases=2 Bus1=632.3.2      Bus2=645.3.2    LineCode=mtx603 Length=500  units=ft
        New Line.645646    Phases=2 Bus1=645.3.2      Bus2=646.3.2    LineCode=mtx603 Length=300  units=ft
        New Line.692675    Phases=3 Bus1=692.1.2.3    Bus2=675.1.2.3  LineCode=mtx606 Length=500  units=ft
        New Line.671684    Phases=2 Bus1=671.1.3      Bus2=684.1.3    LineCode=mtx604 Length=300  units=ft
        New Line.684611    Phases=1 Bus1=684.3        Bus2=611.3      LineCode=mtx605 Length=300  units=ft
        New Line.684652    Phases=1 Bus1=684.1        Bus2=652.1      LineCode=mtx607 Length=800  units=ft

        New Line.671692    Phases=3 Bus1=671   Bus2=692  Switch=y  r1=1e-4 r0=1e-4 x1=0.000 x0=0.000 c1=0.000 c0=0.000

        Set Voltagebases=[115, 4.16, .48]
        calcv
        Solve
        """;
}
