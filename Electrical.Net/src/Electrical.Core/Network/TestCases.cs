namespace Electrical.Core.Network;

/// <summary>Standard benchmark networks (data as published in MATPOWER and the literature) and synthetic grids.</summary>
public static class TestCases
{
    /// <summary>Names accepted by <see cref="ByName"/>.</summary>
    public static readonly string[] Names = ["case9", "case14", "case33bw", "synthetic"];

    public static PowerNetwork ByName(string name) => name.ToLowerInvariant() switch
    {
        "case9" or "ieee9" or "wscc9" => Case9(),
        "case14" or "ieee14" => Case14(),
        "case33bw" or "case33" or "ieee33" => Case33BW(),
        "synthetic" => SyntheticGrid(100),
        _ when name.StartsWith("synthetic", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[9..], out int n) => SyntheticGrid(n),
        _ => throw new ArgumentException($"Unknown test case '{name}'. Known: {string.Join(", ", Names)}"),
    };

    /// <summary>WSCC 3-machine 9-bus system with the Anderson &amp; Fouad operating point (V set-points 1.04/1.025/1.025 pu), MATPOWER case9 costs and classical machine data.</summary>
    public static PowerNetwork Case9()
    {
        var net = Matpower.Parse(Case9Text, "case9");
        net.FrequencyHz = 60;
        // Anderson & Fouad machine data (pu on 100 MVA... converted to machine base)
        (double h, double xd, double xdp)[] m = [(23.64, 0.146, 0.0608), (6.4, 0.8958, 0.1198), (3.01, 1.3125, 0.1813)];
        for (int i = 0; i < 3; i++)
        {
            var g = net.Generators[i];
            g.MBase = 100; g.H = m[i].h; g.Xd = m[i].xd; g.XdPrime = m[i].xdp; g.XdSubtransient = m[i].xdp * 0.8; g.D = 0;
            g.Name = $"G{i + 1}";
        }
        net.Generators[0].Kind = GeneratorKind.Hydro;
        net.Generators[1].Kind = GeneratorKind.Thermal;
        net.Generators[2].Kind = GeneratorKind.Thermal;
        foreach (var b in net.Buses) b.Name = $"Bus {b.Id}";
        return net;
    }

    /// <summary>IEEE 14-bus test system (MATPOWER case14).</summary>
    public static PowerNetwork Case14()
    {
        var net = Matpower.Parse(Case14Text, "case14");
        net.FrequencyHz = 60;
        foreach (var b in net.Buses) b.BaseKv = b.Id <= 5 ? 138 : (b.Id is 7 or 8 ? (b.Id == 8 ? 18 : 14) : 13.8);
        for (int i = 0; i < net.Generators.Count; i++) net.Generators[i].Name = $"G{i + 1}";
        net.Generators[0].Kind = GeneratorKind.Thermal;
        net.Generators[1].Kind = GeneratorKind.Thermal;
        for (int i = 2; i < net.Generators.Count; i++) net.Generators[i].Kind = GeneratorKind.Other; // synchronous condensers
        return net;
    }

    /// <summary>
    /// Baran &amp; Wu 33-bus radial distribution feeder, 12.66 kV, 3.715 MW + 2.3 Mvar
    /// (base-case losses ≈ 202.7 kW, minimum voltage ≈ 0.913 pu at bus 18).
    /// </summary>
    public static PowerNetwork Case33BW()
    {
        // from, to, R [Ω], X [Ω], P_to [kW], Q_to [kvar]
        (int f, int t, double r, double x, double p, double q)[] d =
        [
            (1, 2, 0.0922, 0.0470, 100, 60), (2, 3, 0.4930, 0.2511, 90, 40), (3, 4, 0.3660, 0.1864, 120, 80),
            (4, 5, 0.3811, 0.1941, 60, 30), (5, 6, 0.8190, 0.7070, 60, 20), (6, 7, 0.1872, 0.6188, 200, 100),
            (7, 8, 0.7114, 0.2351, 200, 100), (8, 9, 1.0300, 0.7400, 60, 20), (9, 10, 1.0440, 0.7400, 60, 20),
            (10, 11, 0.1966, 0.0650, 45, 30), (11, 12, 0.3744, 0.1238, 60, 35), (12, 13, 1.4680, 1.1550, 60, 35),
            (13, 14, 0.5416, 0.7129, 120, 80), (14, 15, 0.5910, 0.5260, 60, 10), (15, 16, 0.7463, 0.5450, 60, 20),
            (16, 17, 1.2890, 1.7210, 60, 20), (17, 18, 0.7320, 0.5740, 90, 40), (2, 19, 0.1640, 0.1565, 90, 40),
            (19, 20, 1.5042, 1.3554, 90, 40), (20, 21, 0.4095, 0.4784, 90, 40), (21, 22, 0.7089, 0.9373, 90, 40),
            (3, 23, 0.4512, 0.3083, 90, 50), (23, 24, 0.8980, 0.7091, 420, 200), (24, 25, 0.8960, 0.7011, 420, 200),
            (6, 26, 0.2030, 0.1034, 60, 25), (26, 27, 0.2842, 0.1447, 60, 25), (27, 28, 1.0590, 0.9337, 60, 20),
            (28, 29, 0.8042, 0.7006, 120, 70), (29, 30, 0.5075, 0.2585, 200, 600), (30, 31, 0.9744, 0.9630, 150, 70),
            (31, 32, 0.3105, 0.3619, 210, 100), (32, 33, 0.3410, 0.5302, 60, 40),
        ];
        var b = new NetworkBuilder("case33bw", baseMva: 10, frequencyHz: 50);
        b.AddBus(1, 12.66, BusType.Slack, name: "Substation");
        for (int i = 2; i <= 33; i++) b.AddBus(i, 12.66);
        foreach (var e in d)
        {
            b.AddLineOhm(e.f, e.t, e.r, e.x);
            b.AddLoad(e.t, e.p / 1000, e.q / 1000);
        }
        b.AddGenerator(1, 0, vset: 1.0, kind: GeneratorKind.Grid, name: "Grid");
        return b.Build();
    }

    /// <summary>
    /// Deterministic synthetic meshed transmission grid (ring-of-rings with chords) for scaling studies and benchmarks.
    /// About 1 generator per 5 buses; total generation capacity ≈ 1.5 × load.
    /// </summary>
    public static PowerNetwork SyntheticGrid(int buses, int seed = 42)
    {
        if (buses < 4) throw new ArgumentOutOfRangeException(nameof(buses), "At least 4 buses.");
        var rng = new Random(seed);
        var net = new PowerNetwork { Name = $"synthetic{buses}", BaseMva = 100, FrequencyHz = 50 };
        int side = (int)Math.Ceiling(Math.Sqrt(buses));
        for (int i = 0; i < buses; i++)
        {
            net.Buses.Add(new Bus
            {
                Id = i + 1, BaseKv = 150, Type = i == 0 ? BusType.Slack : BusType.PQ, Name = $"B{i + 1}",
                Location = new GeoPoint(-6.2 + (i / side) * 0.05, 106.8 + (i % side) * 0.05),
            });
        }
        void Line(int a, int c)
        {
            double km = 10 + rng.NextDouble() * 40;
            net.Branches.Add(new Branch { From = a, To = c, R = 0.00012 * km, X = 0.0012 * km, B = 0.0002 * km, RateA = 300, LengthKm = Math.Round(km, 2) });
        }
        for (int i = 0; i < buses; i++)
        {
            int r = i / side, c = i % side;
            if (c + 1 < side && i + 1 < buses) Line(i + 1, i + 2);
            if (r + 1 < side && i + side < buses) Line(i + 1, i + side + 1);
        }
        double totalLoad = 0;
        for (int i = 1; i < buses; i++)
        {
            if (i % 5 == 0) continue;
            double p = Math.Round(10 + rng.NextDouble() * 40, 2);
            totalLoad += p;
            net.Loads.Add(new Load { Bus = i + 1, P = p, Q = Math.Round(p * 0.3, 2) });
        }
        var genBuses = Enumerable.Range(0, buses).Where(i => i % 5 == 0).ToList();
        double share = totalLoad * 1.5 / genBuses.Count;
        foreach (int i in genBuses)
        {
            if (i != 0) net.Buses[i].Type = BusType.PV;
            net.Generators.Add(new Generator
            {
                Bus = i + 1, P = i == 0 ? 0 : Math.Round(share / 1.5, 2), Pmax = Math.Round(share, 2), Pmin = 0, Vset = 1.02,
                Qmax = share, Qmin = -share / 2, Cost = new PolynomialCost(0.01 + rng.NextDouble() * 0.02, 15 + rng.NextDouble() * 20, 100),
                Name = $"G{i + 1}",
            });
        }
        return net;
    }

    private const string Case9Text = """
        function mpc = case9
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        	1	3	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	2	2	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	3	2	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	4	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	5	1	90	30	0	0	1	1	0	345	1	1.1	0.9;
        	6	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	7	1	100	35	0	0	1	1	0	345	1	1.1	0.9;
        	8	1	0	0	0	0	1	1	0	345	1	1.1	0.9;
        	9	1	125	50	0	0	1	1	0	345	1	1.1	0.9;
        ];
        mpc.gen = [
        	1	72.3	27.03	300	-300	1.04	100	1	250	10;
        	2	163	6.54	300	-300	1.025	100	1	300	10;
        	3	85	-10.95	300	-300	1.025	100	1	270	10;
        ];
        mpc.branch = [
        	1	4	0	0.0576	0	250	250	250	0	0	1	-360	360;
        	4	5	0.017	0.092	0.158	250	250	250	0	0	1	-360	360;
        	5	6	0.039	0.17	0.358	150	150	150	0	0	1	-360	360;
        	3	6	0	0.0586	0	300	300	300	0	0	1	-360	360;
        	6	7	0.0119	0.1008	0.209	150	150	150	0	0	1	-360	360;
        	7	8	0.0085	0.072	0.149	250	250	250	0	0	1	-360	360;
        	8	2	0	0.0625	0	250	250	250	0	0	1	-360	360;
        	8	9	0.032	0.161	0.306	250	250	250	0	0	1	-360	360;
        	9	4	0.01	0.085	0.176	250	250	250	0	0	1	-360	360;
        ];
        mpc.gencost = [
        	2	1500	0	3	0.11	5	150;
        	2	2000	0	3	0.085	1.2	600;
        	2	3000	0	3	0.1225	1	335;
        ];
        """;

    private const string Case14Text = """
        function mpc = case14
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [
        	1	3	0	0	0	0	1	1.06	0	0	1	1.06	0.94;
        	2	2	21.7	12.7	0	0	1	1.045	-4.98	0	1	1.06	0.94;
        	3	2	94.2	19	0	0	1	1.01	-12.72	0	1	1.06	0.94;
        	4	1	47.8	-3.9	0	0	1	1.019	-10.33	0	1	1.06	0.94;
        	5	1	7.6	1.6	0	0	1	1.02	-8.78	0	1	1.06	0.94;
        	6	2	11.2	7.5	0	0	1	1.07	-14.22	0	1	1.06	0.94;
        	7	1	0	0	0	0	1	1.062	-13.37	0	1	1.06	0.94;
        	8	2	0	0	0	0	1	1.09	-13.36	0	1	1.06	0.94;
        	9	1	29.5	16.6	0	19	1	1.056	-14.94	0	1	1.06	0.94;
        	10	1	9	5.8	0	0	1	1.051	-15.1	0	1	1.06	0.94;
        	11	1	3.5	1.8	0	0	1	1.057	-14.79	0	1	1.06	0.94;
        	12	1	6.1	1.6	0	0	1	1.055	-15.07	0	1	1.06	0.94;
        	13	1	13.5	5.8	0	0	1	1.05	-15.16	0	1	1.06	0.94;
        	14	1	14.9	5	0	0	1	1.036	-16.04	0	1	1.06	0.94;
        ];
        mpc.gen = [
        	1	232.4	-16.9	10	0	1.06	100	1	332.4	0;
        	2	40	42.4	50	-40	1.045	100	1	140	0;
        	3	0	23.4	40	0	1.01	100	1	100	0;
        	6	0	12.2	24	-6	1.07	100	1	100	0;
        	8	0	17.4	24	-6	1.09	100	1	100	0;
        ];
        mpc.branch = [
        	1	2	0.01938	0.05917	0.0528	0	0	0	0	0	1	-360	360;
        	1	5	0.05403	0.22304	0.0492	0	0	0	0	0	1	-360	360;
        	2	3	0.04699	0.19797	0.0438	0	0	0	0	0	1	-360	360;
        	2	4	0.05811	0.17632	0.034	0	0	0	0	0	1	-360	360;
        	2	5	0.05695	0.17388	0.0346	0	0	0	0	0	1	-360	360;
        	3	4	0.06701	0.17103	0.0128	0	0	0	0	0	1	-360	360;
        	4	5	0.01335	0.04211	0	0	0	0	0	0	1	-360	360;
        	4	7	0	0.20912	0	0	0	0	0.978	0	1	-360	360;
        	4	9	0	0.55618	0	0	0	0	0.969	0	1	-360	360;
        	5	6	0	0.25202	0	0	0	0	0.932	0	1	-360	360;
        	6	11	0.09498	0.1989	0	0	0	0	0	0	1	-360	360;
        	6	12	0.12291	0.25581	0	0	0	0	0	0	1	-360	360;
        	6	13	0.06615	0.13027	0	0	0	0	0	0	1	-360	360;
        	7	8	0	0.17615	0	0	0	0	0	0	1	-360	360;
        	7	9	0	0.11001	0	0	0	0	0	0	1	-360	360;
        	9	10	0.03181	0.0845	0	0	0	0	0	0	1	-360	360;
        	9	14	0.12711	0.27038	0	0	0	0	0	0	1	-360	360;
        	10	11	0.08205	0.19207	0	0	0	0	0	0	1	-360	360;
        	12	13	0.22092	0.19988	0	0	0	0	0	0	1	-360	360;
        	13	14	0.17093	0.34802	0	0	0	0	0	0	1	-360	360;
        ];
        mpc.gencost = [
        	2	0	0	3	0.0430293	20	0;
        	2	0	0	3	0.25	20	0;
        	2	0	0	3	0.01	40	0;
        	2	0	0	3	0.01	40	0;
        	2	0	0	3	0.01	40	0;
        ];
        """;
}
