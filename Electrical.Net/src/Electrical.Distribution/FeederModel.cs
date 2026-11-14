using System.Numerics;

namespace Electrical.Distribution;

/// <summary>Compiled phase-frame model: nodes (bus, phase), element admittances, loads and capacitors.</summary>
internal sealed class FeederModel
{
    private readonly DistributionNetwork _net;
    private readonly int[,] _node;           // [bus, phase] → node or −1
    private readonly PhaseSet[] _busPhases;
    private readonly double[] _busBaseV;
    private readonly List<string> _warnings = [];
    private readonly List<(DistributionLoad Load, int[] Nodes, int[] Elem, double V0)> _loads = [];
    private readonly List<(ShuntCapacitor Cap, int[] Nodes, int[] Elem, double V0)> _caps = [];

    public List<BranchModel> Branches { get; } = [];
    public int NodeCount { get; }
    public int BusCount => _net.Buses.Count;
    public int SourceBus { get; }
    public int[] SourceNodes { get; }
    public double[] NodeBaseV { get; }

    private FeederModel(DistributionNetwork net)
    {
        _net = net;
        var errors = net.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid distribution network: " + string.Join(" ", errors));
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < net.Buses.Count; i++) index[net.Buses[i].Name] = i;
        int nb = net.Buses.Count;
        _busPhases = new PhaseSet[nb];
        _busBaseV = net.Buses.Select(b => b.BaseVoltsLN).ToArray();
        SourceBus = index[net.Source!.Bus];
        _busPhases[SourceBus] = PhaseSet.ABC;

        foreach (var l in net.Lines.Where(l => l.Closed))
            AddBranch(ElementModels.Line(l, index[l.From], index[l.To]));
        foreach (var t in net.Transformers.Where(t => t.InService))
            AddBranch(ElementModels.Transformer(t, index[t.From], index[t.To]));
        foreach (var r in net.Regulators)
            AddBranch(ElementModels.Regulator(r, index[r.From], index[r.To]));

        _node = new int[nb, 3];
        int n = 0;
        var baseV = new List<double>();
        for (int b = 0; b < nb; b++)
            for (int p = 0; p < 3; p++)
            {
                if (_busPhases[b].Has(p)) { _node[b, p] = n++; baseV.Add(_busBaseV[b]); }
                else _node[b, p] = -1;
            }
        NodeCount = n;
        NodeBaseV = [.. baseV];
        SourceNodes = [_node[SourceBus, 0], _node[SourceBus, 1], _node[SourceBus, 2]];

        foreach (var l in net.Loads.Where(l => l.InService))
        {
            int b = index[l.Bus];
            var (nodes, elem) = Elements(b, l.Connection, i => l.PowerKva[i] != Complex.Zero, $"Load {l.Name}");
            double v0 = double.IsNaN(l.NominalKv)
                ? (l.Connection == LoadConnection.Wye ? _busBaseV[b] : _busBaseV[b] * Math.Sqrt(3))
                : l.NominalKv * 1000;
            _loads.Add((l, nodes, elem, v0));
        }
        foreach (var c in net.Capacitors)
        {
            int b = index[c.Bus];
            var (nodes, elem) = Elements(b, c.Connection, i => c.Kvar[i] != 0, $"Capacitor {c.Name}");
            double v0 = double.IsNaN(c.RatedKv)
                ? (c.Connection == LoadConnection.Wye ? _busBaseV[b] : _busBaseV[b] * Math.Sqrt(3))
                : c.RatedKv * 1000;
            _caps.Add((c, nodes, elem, v0));
        }
    }

    public static FeederModel Compile(DistributionNetwork net) => new(net);

    public string BusName(int b) => _net.Buses[b].Name;
    public bool BusHasNodes(int b) => _busPhases[b] != PhaseSet.None;

    private void AddBranch(BranchModel br)
    {
        _busPhases[br.FromBus] |= PhaseSetExtensions.FromIndices(br.FromPhases);
        _busPhases[br.ToBus] |= PhaseSetExtensions.FromIndices(br.ToPhases);
        Branches.Add(br);
    }

    /// <summary>Element terminals: for wye element i the node of phase i (second = −1 = ground); for delta the pair (i, i+1).</summary>
    private (int[] Nodes, int[] Elem) Elements(int bus, LoadConnection conn, Func<int, bool> used, string what)
    {
        var nodes = new List<int>();
        var elem = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            if (!used(i)) continue;
            int a = _node[bus, i];
            int b = conn == LoadConnection.Delta ? _node[bus, (i + 1) % 3] : -1;
            if (a < 0 || (conn == LoadConnection.Delta && b < 0))
            {
                _warnings.Add($"{what}: phase {(conn == LoadConnection.Delta ? "ABC"[i] + "" + "ABC"[(i + 1) % 3] : "ABC"[i] + "")} not present at bus {_net.Buses[bus].Name}; ignored.");
                continue;
            }
            nodes.Add(a); nodes.Add(b); elem.Add(i);
        }
        return ([.. nodes], [.. elem]);
    }

    public int[] LocalNodes(BranchModel br)
    {
        var r = new int[br.Nf + br.Nt];
        for (int i = 0; i < br.Nf; i++) r[i] = _node[br.FromBus, br.FromPhases[i]];
        for (int i = 0; i < br.Nt; i++) r[br.Nf + i] = _node[br.ToBus, br.ToPhases[i]];
        return r;
    }

    public void SetSourceVoltages(Complex[] v)
    {
        var s = _net.Source!;
        double mag = s.VoltagePu * _busBaseV[SourceBus];
        for (int p = 0; p < 3; p++)
            v[SourceNodes[p]] = Complex.FromPolarCoordinates(mag, (s.AngleDeg - 120 * p) * Math.PI / 180);
    }

    /// <summary>Adds load and capacitor current injections (into the network) at voltages <paramref name="v"/> [A].</summary>
    public void AddShuntInjections(Complex[] v, Complex[] inj, double multiplier)
    {
        foreach (var (l, nodes, elem, v0) in _loads)
            for (int k = 0; k < elem.Length; k++)
            {
                int a = nodes[2 * k], b = nodes[2 * k + 1];
                var va = v[a] - (b >= 0 ? v[b] : Complex.Zero);
                var i = LoadCurrent(l.PowerKva[elem[k]] * 1000 * multiplier, l.Model, va, v0);
                inj[a] -= i;
                if (b >= 0) inj[b] += i;
            }
        foreach (var (c, nodes, elem, v0) in _caps)
        {
            if (!c.IsOn) continue;
            for (int k = 0; k < elem.Length; k++)
            {
                int a = nodes[2 * k], b = nodes[2 * k + 1];
                var va = v[a] - (b >= 0 ? v[b] : Complex.Zero);
                var y = new Complex(0, c.Kvar[elem[k]] * 1000 / (v0 * v0));
                var i = y * va;
                inj[a] -= i;
                if (b >= 0) inj[b] += i;
            }
        }
    }

    /// <summary>Current drawn by a load element with nominal power S0 [VA] at voltage V across it, rated voltage V0 [V].</summary>
    private static Complex LoadCurrent(Complex s0, LoadModel model, Complex v, double v0)
    {
        double vm = v.Magnitude;
        if (vm < 1e-9 * v0) return Complex.Zero;
        var s = model switch
        {
            LoadModel.ConstantImpedance => s0 * (vm / v0) * (vm / v0),
            LoadModel.ConstantCurrent => s0 * (vm / v0),
            _ => s0,
        };
        return Complex.Conjugate(s / v);
    }

    public DistributionPowerFlowResult BuildResult(Complex[] v, bool converged, int iterations, DistributionSolverMethod method, double multiplier)
    {
        var buses = new List<DistributionBusResult>(_net.Buses.Count);
        for (int b = 0; b < _net.Buses.Count; b++)
        {
            var vb = new Complex[3];
            for (int p = 0; p < 3; p++) if (_node[b, p] >= 0) vb[p] = v[_node[b, p]];
            buses.Add(new DistributionBusResult(_net.Buses[b].Name, _net.Buses[b].BaseKvLL, _busPhases[b], vb));
        }
        var nodeCurrent = new Complex[NodeCount];  // current leaving each node into series elements
        var flows = new List<DistributionBranchFlow>(Branches.Count);
        foreach (var br in Branches)
        {
            var nodes = LocalNodes(br);
            var vl = nodes.Select(n => v[n]).ToArray();
            var il = CMat.Mul(br.Y, vl);
            for (int i = 0; i < nodes.Length; i++) nodeCurrent[nodes[i]] += il[i];
            var iFrom = new Complex[3]; var iTo = new Complex[3]; var sFrom = new Complex[3]; var sTo = new Complex[3];
            for (int i = 0; i < br.Nf; i++)
            {
                int p = br.FromPhases[i];
                iFrom[p] = il[i];
                sFrom[p] = vl[i] * Complex.Conjugate(il[i]) / 1000;
            }
            for (int i = 0; i < br.Nt; i++)
            {
                int p = br.ToPhases[i];
                iTo[p] = -il[br.Nf + i];
                sTo[p] = vl[br.Nf + i] * Complex.Conjugate(-il[br.Nf + i]) / 1000;
            }
            flows.Add(new DistributionBranchFlow
            {
                Name = br.Name, Kind = br.Kind, From = _net.Buses[br.FromBus].Name, To = _net.Buses[br.ToBus].Name,
                Phases = PhaseSetExtensions.FromIndices(br.FromPhases), CurrentFromA = iFrom, CurrentToA = iTo, PowerFromKva = sFrom, PowerToKva = sTo,
            });
        }
        var inj = new Complex[NodeCount];
        AddShuntInjections(v, inj, multiplier);
        var src = new Complex[3];
        for (int p = 0; p < 3; p++)
        {
            int n = SourceNodes[p];
            src[p] = v[n] * Complex.Conjugate(nodeCurrent[n] - inj[n]) / 1000;
        }
        // load consumption only (no capacitors)
        var loadInj = new Complex[NodeCount];
        Complex load = 0;
        foreach (var (l, nodes, elem, v0) in _loads)
            for (int k = 0; k < elem.Length; k++)
            {
                int a = nodes[2 * k], b = nodes[2 * k + 1];
                var va = v[a] - (b >= 0 ? v[b] : Complex.Zero);
                load += va * Complex.Conjugate(LoadCurrent(l.PowerKva[elem[k]] * 1000 * multiplier, l.Model, va, v0)) / 1000;
            }
        var taps = _net.Regulators.ToDictionary(r => r.Name, r => (int[])r.Taps.Clone(), StringComparer.OrdinalIgnoreCase);
        return new DistributionPowerFlowResult(converged, iterations, method, buses, flows, src, load, taps, _warnings);
    }
}
