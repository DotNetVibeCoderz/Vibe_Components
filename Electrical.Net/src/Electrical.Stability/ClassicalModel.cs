using System.Numerics;
using Electrical.Core.Network;
using Electrical.Core.Numerics;
using Electrical.PowerFlow;

namespace Electrical.Stability;

/// <summary>Classical machine: constant EMF E′ behind the transient reactance X′d. Quantities in pu on the system base.</summary>
public sealed record ClassicalMachine(
    int GeneratorIndex, int BusId, string Name, double H, double D, double XdPrime, double Ra, double E, double Delta0Rad, double Pm, bool Infinite)
{
    /// <summary>Initial rotor angle [degrees].</summary>
    public double Delta0Deg => Delta0Rad * Scalar.Rad2Deg;
}

/// <summary>
/// Classical multi-machine model built from a solved power flow: machines as E′∠δ behind X′d, loads as constant admittances,
/// network reduced to the machine internal nodes (Kron reduction) for each switching configuration.
/// </summary>
public sealed class ClassicalModel
{
    /// <summary>Default fault impedance for bolted faults [pu].</summary>
    public const double BoltedFaultImpedance = 1e-5;

    private readonly PowerNetwork _net;
    private readonly Complex[] _loadY;  // constant-admittance loads per bus index [pu]

    private ClassicalModel(PowerNetwork net, IReadOnlyList<ClassicalMachine> machines, Complex[] loadY, PowerFlowResult pf)
    {
        _net = net; Machines = machines; _loadY = loadY; PowerFlow = pf;
    }

    public IReadOnlyList<ClassicalMachine> Machines { get; }
    public PowerFlowResult PowerFlow { get; }
    public PowerNetwork Network => _net;
    public double FrequencyHz => _net.FrequencyHz > 0 ? _net.FrequencyHz : 50;
    public double OmegaS => 2 * Math.PI * FrequencyHz;

    /// <summary>
    /// Builds the model. Generators with H ≤ 0 or H ≥ 10⁵ s are treated as infinite buses (constant angle).
    /// The machine damping <see cref="Generator.D"/> [pu power / pu speed on the machine base] is converted to the system base.
    /// </summary>
    public static ClassicalModel FromPowerFlow(PowerNetwork network, PowerFlowResult? powerFlow = null)
    {
        var net = network.Clone();
        var pf = powerFlow ?? PowerFlowSolver.Solve(net);
        if (!pf.Converged) throw new InvalidOperationException("Initial power flow did not converge.");
        double sb = net.BaseMva;
        var machines = new List<ClassicalMachine>();
        foreach (var gr in pf.Generators)
        {
            var g = net.Generators[gr.Index];
            if (!g.InService) continue;
            int i = net.IndexOf(g.Bus);
            var v = pf.Voltages[i];
            var s = new Complex(gr.P, gr.Q) / sb;
            var cur = Complex.Conjugate(s / v);
            double x = g.XdPrime * sb / g.MBase, ra = g.Ra * sb / g.MBase;
            var e = v + new Complex(ra, x) * cur;
            double h = g.H * g.MBase / sb;
            bool inf = g.H <= 0 || g.H >= 1e5;
            machines.Add(new ClassicalMachine(gr.Index, g.Bus, g.Name ?? $"G{gr.Index + 1}", h, g.D * g.MBase / sb, x, ra, e.Magnitude, e.Phase,
                (e * Complex.Conjugate(cur)).Real, inf)); // Pm = Pe(0) = P_g + R_a·|I|²
        }
        if (machines.Count == 0) throw new InvalidOperationException("No in-service generators.");
        var loadY = new Complex[net.BusCount];
        for (int i = 0; i < net.BusCount; i++)
        {
            var b = pf.Buses[i];
            double vm = pf.Voltages[i].Magnitude;
            loadY[i] = new Complex(b.PLoad, -b.QLoad) / sb / (vm * vm);
        }
        return new ClassicalModel(net, machines, loadY, pf);
    }

    /// <summary>Switching state of the network.</summary>
    public sealed class Configuration
    {
        public HashSet<int> TrippedBranches { get; } = [];
        public HashSet<int> ClosedBranches { get; } = [];
        public Dictionary<int, Complex> Faults { get; } = []; // bus id → fault impedance [pu]
        public HashSet<int> TrippedMachines { get; } = [];     // machine positions
        public Dictionary<int, double> LoadScale { get; } = [];  // bus id → factor

        public Configuration Clone()
        {
            var c = new Configuration();
            c.TrippedBranches.UnionWith(TrippedBranches);
            c.ClosedBranches.UnionWith(ClosedBranches);
            foreach (var kv in Faults) c.Faults[kv.Key] = kv.Value;
            c.TrippedMachines.UnionWith(TrippedMachines);
            foreach (var kv in LoadScale) c.LoadScale[kv.Key] = kv.Value;
            return c;
        }
    }

    /// <summary>Reduced admittance matrix at the machine internal nodes [pu] for a configuration.</summary>
    public DenseMatrix<Complex> ReducedAdmittance(Configuration? config = null)
    {
        config ??= new Configuration();
        var net = _net.Clone();
        foreach (int k in config.TrippedBranches) net.Branches[k].InService = false;
        foreach (int k in config.ClosedBranches) net.Branches[k].InService = true;
        var y = AdmittanceMatrix.Build(net).Y;
        int n = net.BusCount, m = Machines.Count;
        var b = new SparseBuilder<Complex>(n, n);
        for (int i = 0; i < n; i++)
        {
            var idx = y.RowIndices(i); var val = y.RowValues(i);
            for (int q = 0; q < idx.Length; q++) b.Add(i, idx[q], val[q]);
            double scale = config.LoadScale.TryGetValue(net.Buses[i].Id, out var f) ? f : 1.0;
            b.Add(i, i, _loadY[i] * scale);
        }
        foreach (var (bus, zf) in config.Faults) b.Add(net.IndexOf(bus), net.IndexOf(bus), 1 / (zf == Complex.Zero ? BoltedFaultImpedance : zf));
        var yg = new Complex[m];
        for (int k = 0; k < m; k++)
        {
            if (config.TrippedMachines.Contains(k)) continue;
            yg[k] = 1 / new Complex(Machines[k].Ra, Machines[k].XdPrime);
            b.Add(net.IndexOf(Machines[k].BusId), net.IndexOf(Machines[k].BusId), yg[k]);
        }
        var lu = SparseLU<Complex>.Factorize(b.ToCsr());
        var red = new DenseMatrix<Complex>(m, m);
        for (int j = 0; j < m; j++)
        {
            if (yg[j] == Complex.Zero) continue;
            var e = new Complex[n];
            e[net.IndexOf(Machines[j].BusId)] = 1;
            var z = lu.Solve(e);
            for (int i = 0; i < m; i++)
                if (yg[i] != Complex.Zero) red[i, j] = (i == j ? yg[i] : Complex.Zero) - yg[i] * z[net.IndexOf(Machines[i].BusId)] * yg[j];
        }
        return red;
    }

    /// <summary>Electrical power output of each machine P_e,i = Re(E_i·conj(Σ_j Y_ij·E_j)) [pu].</summary>
    public static void ElectricalPower(DenseMatrix<Complex> yred, ReadOnlySpan<double> e, ReadOnlySpan<double> delta, Span<double> pe)
    {
        int m = e.Length;
        Span<Complex> ev = m <= 256 ? stackalloc Complex[m] : new Complex[m];
        for (int i = 0; i < m; i++) ev[i] = Complex.FromPolarCoordinates(e[i], delta[i]);
        for (int i = 0; i < m; i++)
        {
            Complex s = 0;
            var row = yred.Row(i);
            for (int j = 0; j < m; j++) s += row[j] * ev[j];
            pe[i] = (ev[i] * Complex.Conjugate(s)).Real;
        }
    }
}
