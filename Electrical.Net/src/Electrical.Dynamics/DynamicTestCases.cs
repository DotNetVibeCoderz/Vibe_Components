using Electrical.Core.Network;

namespace Electrical.Dynamics;

/// <summary>A network with its dynamic data.</summary>
public sealed record DynamicCase(string Name, PowerNetwork Network, DynamicModelSet Models, string Description)
{
    /// <summary>Builds and initializes the dynamic system.</summary>
    public DynamicSystem Build(DynamicSystemOptions? options = null) => DynamicSystem.Build(Network, Models, options);
}

/// <summary>Excitation variant of the Kundur two-area system.</summary>
public enum KundurExcitation
{
    /// <summary>Manual excitation (constant field voltage), no PSS.</summary>
    ConstantField,
    /// <summary>High-gain thyristor exciter (ESST1A, KA = 200, TR = 0.01 s) without PSS.</summary>
    StaticExciter,
    /// <summary>Thyristor exciter with Kundur's PSS (K = 20, TW = 10 s, T1 = 0.05, T2 = 0.02, T3 = 3, T4 = 5.4 s).</summary>
    StaticExciterWithPss,
}

/// <summary>Dynamic test systems.</summary>
public static class DynamicTestCases
{
    /// <summary>
    /// Kundur two-area four-machine system (P. Kundur, <i>Power System Stability and Control</i>, Example 12.6; Klein, Rogers &amp; Kundur 1991):
    /// 20/230 kV, 60 Hz, four 900 MVA GENROU machines (Xd 1.8, Xq 1.7, X'd 0.3, X'q 0.55, X''d 0.25, Xl 0.2, Ra 0.0025, T'do 8, T'qo 0.4,
    /// T''do 0.03, T''qo 0.05 s, H 6.5/6.175 s, KD 0; saturation Asat 0.015, Bsat 9.6, ψT1 0.9 fitted as S(1.0) = 0.039, S(1.2) = 0.223),
    /// step-up transformers j0.15 pu on 900 MVA, 230 kV lines r = 0.0001, xL = 0.001, bC = 0.00175 pu/km on 100 MVA (25 + 10 km per area,
    /// 2 × 220 km tie), loads 967 MW + 100 Mvar (bus 7) and 1767 MW + 100 Mvar (bus 9) with 200 / 350 Mvar shunt capacitors.
    /// G1, G2, G4 dispatch 700 MW (V = 1.03, 1.01, 1.01 pu), G3 is the slack (1.03 pu ∠ −6.8°). Loads are constant impedance.
    /// </summary>
    public static DynamicCase KundurTwoArea(KundurExcitation excitation = KundurExcitation.StaticExciterWithPss)
    {
        var net = KundurNetwork();
        string dyr = KundurDyr(excitation);
        var models = DyrReader.Parse(dyr, net);
        return new DynamicCase("kundur2area", net, models, $"Kundur two-area system, {excitation}");
    }

    /// <summary>Kundur two-area network (power-flow data only).</summary>
    public static PowerNetwork KundurNetwork()
    {
        var net = new PowerNetwork { Name = "kundur2area", BaseMva = 100, FrequencyHz = 60 };
        for (int i = 1; i <= 11; i++)
            net.Buses.Add(new Bus { Id = i, Name = $"Bus {i}", BaseKv = i <= 4 ? 20 : 230, Area = i is 1 or 2 or 5 or 6 or 7 ? 1 : 2, Vmax = 1.1, Vmin = 0.9 });
        net.GetBus(3).Type = BusType.Slack; net.GetBus(3).VaDeg = -6.8;
        foreach (int b in new[] { 1, 2, 4 }) net.GetBus(b).Type = BusType.PV;
        (int bus, double p, double v, double h)[] g = [(1, 700, 1.03, 6.5), (2, 700, 1.01, 6.5), (3, 719, 1.03, 6.175), (4, 700, 1.01, 6.175)];
        for (int k = 0; k < 4; k++)
            net.Generators.Add(new Generator
            {
                Bus = g[k].bus, Name = $"G{k + 1}", P = g[k].p, Vset = g[k].v, MBase = 900, Pmax = 900, Qmax = 9999, Qmin = -9999,
                H = g[k].h, D = 0, Xd = 1.8, XdPrime = 0.3, XdSubtransient = 0.25, Ra = 0.0025,
            });
        net.Loads.Add(new Load { Bus = 7, P = 967, Q = 100 });
        net.Loads.Add(new Load { Bus = 9, P = 1767, Q = 100 });
        net.GetBus(7).Bs = 200;
        net.GetBus(9).Bs = 350;
        foreach (var (f, t) in new[] { (1, 5), (2, 6), (3, 11), (4, 10) })
            net.Branches.Add(new Branch { From = f, To = t, X = 0.15 / 9, Kind = BranchKind.Transformer, Tap = 1, RateA = 900 });
        void Line(int f, int t, double km, int circuits)
        {
            for (int c = 0; c < circuits; c++)
                net.Branches.Add(new Branch { From = f, To = t, R = 0.0001 * km, X = 0.001 * km, B = 0.00175 * km, LengthKm = km, Name = $"{f}-{t}({c + 1})" });
        }
        Line(5, 6, 25, 1); Line(6, 7, 10, 1); Line(7, 8, 110, 2); Line(8, 9, 110, 2); Line(9, 10, 10, 1); Line(10, 11, 25, 1);
        return net;
    }

    /// <summary>PSS/E .dyr data of the Kundur two-area system for the given excitation variant.</summary>
    public static string KundurDyr(KundurExcitation excitation)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// Kundur two-area system, Example 12.6 (machine data on 900 MVA)");
        double[] h = [6.5, 6.5, 6.175, 6.175];
        for (int k = 0; k < 4; k++)
        {
            int bus = k + 1;
            sb.AppendLine($"{bus} 'GENROU' 1  8.0 0.03 0.4 0.05 {h[k]} 0.0");
            sb.AppendLine("     1.8 1.7 0.3 0.55 0.25 0.2 0.039 0.223 /");
            if (excitation != KundurExcitation.ConstantField)
                sb.AppendLine($"{bus} 'ESST1A' 1  1 1  0.01 99 -99 0 0 0 0 200 0 9 -9 9 -9 0 0 1 0 0 /  thyristor exciter KA = 200");
            if (excitation == KundurExcitation.StaticExciterWithPss)
                sb.AppendLine($"{bus} 'IEEEST' 1  1 0  0 0 0 0 0 0  0.05 0.02 3.0 5.4 10 10  20 0.2 -0.2 0 0 /  Kundur PSS");
        }
        return sb.ToString();
    }

    /// <summary>
    /// WSCC 3-machine 9-bus system (Anderson &amp; Fouad operating point, <c>TestCases.Case9</c>) with detailed machines: G1 GENSAL (hydro),
    /// G2/G3 GENROU with Anderson &amp; Fouad Table 2.1 reactances and open-circuit time constants (pu on 100 MVA); subtransient data are typical
    /// assumptions (T''do 0.03, T''qo 0.05 s, X''d = 0.82·X'd), no saturation. Exciters IEEET1 with A&amp;F data (KA 20, TA 0.2, KE 1, TE 0.314,
    /// KF 0.063, TF 0.35, VR ±5), no exciter saturation.
    /// </summary>
    public static DynamicCase Case9()
    {
        var net = TestCases.Case9();
        var m = new DynamicModelSet();
        m.Add(0, new Gensal(8.96, 0.03, 0.05, 23.64, 0, 0.146, 0.0969, 0.0608, 0.05, 0.0336), Exciter());
        m.Add(1, new Genrou(6.0, 0.03, 0.535, 0.05, 6.4, 0, 0.8958, 0.8645, 0.1198, 0.1969, 0.1, 0.0521), Exciter());
        m.Add(2, new Genrou(5.89, 0.03, 0.6, 0.05, 3.01, 0, 1.3125, 1.2578, 0.1813, 0.25, 0.15, 0.0742), Exciter());
        return new DynamicCase("case9", net, m, "WSCC 9-bus, GENSAL/GENROU + IEEET1");
        static Ieeet1 Exciter() => new(0, 20, 0.2, 5, -5, 1.0, 0.314, 0.063, 0.35);
    }

    /// <summary>
    /// Inverter-based resource behind a line: bus 1 is an infinite bus (GENCLS, H = 0, X'd = 0.02 pu), line 1–2 r = 0.01, x = 0.1 pu,
    /// 80 MW REGC_A + REEC_A unit (100 MVA, unity power factor) at bus 2. REEC_A: Q priority, KQV = 2, dip below 0.9 pu, IMAX 1.1;
    /// REGC_A: RRPWR = 2 pu/s active-current recovery ramp, LVPL 0.4–0.9 pu, LVACM 0.4–0.8 pu.
    /// </summary>
    public static DynamicCase InverterBehindLine(bool plantController = false)
    {
        var net = new PowerNetwork { Name = "ibr2bus", BaseMva = 100, FrequencyHz = 60 };
        net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack, BaseKv = 230 });
        net.Buses.Add(new Bus { Id = 2, Type = BusType.PV, BaseKv = 230 });
        net.Branches.Add(new Branch { From = 1, To = 2, R = 0.01, X = 0.1 });
        net.Generators.Add(new Generator { Bus = 1, Name = "GRID", Vset = 1.0, MBase = 100, H = 0, XdPrime = 0.02, Kind = GeneratorKind.Grid });
        net.Generators.Add(new Generator { Bus = 2, Name = "PV1", P = 80, Vset = 1.0, MBase = 100, Kind = GeneratorKind.Solar });
        var m = new DynamicModelSet();
        m.Add(0, new Gencls(0, 0));
        m.Add(1, new Regca(tg: 0.02, rrpwr: 2), new Reeca(qFlag: false) { Kqv = 2, Imax = 1.1, Iqh1 = 1.05, Iql1 = -1.05, Qmax = 0.5, Qmin = -0.5 });
        if (plantController) m.Add(1, new Repca(fFlag: true) { RefFlag = true, Kp = 1, Ki = 20 });
        return new DynamicCase("ibr2bus", net, m, "REGC_A/REEC_A behind a line");
    }

    /// <summary>
    /// Two grid-forming droop inverters (100 MVA each, XF 0.15 pu) at buses 1 and 2 feeding a 100 MW + 20 Mvar load at bus 3 through
    /// x = 0.05 pu lines; P–f droops MP = 0.05 (unit 1) and 0.025 pu (unit 2), so a load step is shared 1 : 2.
    /// </summary>
    public static DynamicCase GridFormingPair(GridFormingMode mode = GridFormingMode.Droop)
    {
        var net = new PowerNetwork { Name = "gfm3bus", BaseMva = 100, FrequencyHz = 50 };
        net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack, BaseKv = 20 });
        net.Buses.Add(new Bus { Id = 2, Type = BusType.PV, BaseKv = 20 });
        net.Buses.Add(new Bus { Id = 3, Type = BusType.PQ, BaseKv = 20 });
        net.Branches.Add(new Branch { From = 1, To = 3, R = 0.005, X = 0.05 });
        net.Branches.Add(new Branch { From = 2, To = 3, R = 0.005, X = 0.05 });
        net.Loads.Add(new Load { Bus = 3, P = 100, Q = 20 });
        net.Generators.Add(new Generator { Bus = 1, Name = "GFM1", Vset = 1.0, MBase = 100, Kind = GeneratorKind.Battery });
        net.Generators.Add(new Generator { Bus = 2, Name = "GFM2", P = 50, Vset = 1.0, MBase = 100, Kind = GeneratorKind.Battery });
        var m = new DynamicModelSet();
        m.Add(0, new GridFormingInverter(mode, xf: 0.15, mp: 0.05, mq: 0.05, tp: 0.02, imax: 1.5, h: 1));
        m.Add(1, new GridFormingInverter(mode, xf: 0.15, mp: 0.025, mq: 0.05, tp: 0.02, imax: 1.5, h: 2));
        return new DynamicCase("gfm3bus", net, m, "two grid-forming inverters sharing a load");
    }
}
