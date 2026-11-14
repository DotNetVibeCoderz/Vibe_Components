namespace Electrical.Core.Network;

/// <summary>
/// Bus-branch power network model (positive-sequence, balanced). Element collections are mutable;
/// bus lookups are cached and rebuilt automatically when the bus list changes.
/// </summary>
public sealed class PowerNetwork
{
    private Dictionary<int, int>? _index;
    private int _indexedCount = -1;

    public string Name { get; set; } = "network";
    /// <summary>System MVA base.</summary>
    public double BaseMva { get; set; } = 100;
    public double FrequencyHz { get; set; } = 50;

    public List<Bus> Buses { get; } = [];
    public List<Load> Loads { get; } = [];
    public List<Generator> Generators { get; } = [];
    public List<Branch> Branches { get; } = [];

    public int BusCount => Buses.Count;

    /// <summary>Zero-based position of a bus id in <see cref="Buses"/>.</summary>
    public int IndexOf(int busId)
    {
        var idx = Index();
        return idx.TryGetValue(busId, out int i) ? i : throw new KeyNotFoundException($"Bus {busId} not found in '{Name}'.");
    }

    public bool ContainsBus(int busId) => Index().ContainsKey(busId);

    public Bus GetBus(int busId) => Buses[IndexOf(busId)];

    /// <summary>Forces the bus index to rebuild (call after changing bus ids in place).</summary>
    public void InvalidateIndex() => _index = null;

    private Dictionary<int, int> Index()
    {
        if (_index is null || _indexedCount != Buses.Count)
        {
            var d = new Dictionary<int, int>(Buses.Count);
            for (int i = 0; i < Buses.Count; i++)
                if (!d.TryAdd(Buses[i].Id, i)) throw new InvalidOperationException($"Duplicate bus id {Buses[i].Id}.");
            _index = d;
            _indexedCount = Buses.Count;
        }
        return _index;
    }

    public Bus? SlackBus => Buses.FirstOrDefault(b => b.Type == BusType.Slack);

    public double TotalLoadMw => Loads.Where(l => l.InService).Sum(l => l.P);
    public double TotalLoadMvar => Loads.Where(l => l.InService).Sum(l => l.Q);
    public double TotalGenerationCapacityMw => Generators.Where(g => g.InService).Sum(g => g.Pmax);

    /// <summary>Deep copy (elements are cloned) so studies can modify a scenario without touching the base case.</summary>
    public PowerNetwork Clone()
    {
        var n = new PowerNetwork { Name = Name, BaseMva = BaseMva, FrequencyHz = FrequencyHz };
        foreach (var b in Buses) n.Buses.Add((Bus)MemberwiseCloneOf(b));
        foreach (var l in Loads) n.Loads.Add((Load)MemberwiseCloneOf(l));
        foreach (var g in Generators) n.Generators.Add((Generator)MemberwiseCloneOf(g));
        foreach (var br in Branches) n.Branches.Add((Branch)MemberwiseCloneOf(br));
        return n;
    }

    private static object MemberwiseCloneOf(object o) => o switch
    {
        Bus b => new Bus
        {
            Id = b.Id, Name = b.Name, Type = b.Type, BaseKv = b.BaseKv, Vm = b.Vm, VaDeg = b.VaDeg, Vmax = b.Vmax, Vmin = b.Vmin,
            Gs = b.Gs, Bs = b.Bs, Area = b.Area, Zone = b.Zone, Location = b.Location,
        },
        Load l => new Load { Bus = l.Bus, Name = l.Name, P = l.P, Q = l.Q, InService = l.InService, Model = l.Model, Category = l.Category },
        Generator g => new Generator
        {
            Bus = g.Bus, Name = g.Name, Kind = g.Kind, P = g.P, Q = g.Q, Qmax = g.Qmax, Qmin = g.Qmin, Vset = g.Vset, Pmax = g.Pmax,
            Pmin = g.Pmin, MBase = g.MBase, InService = g.InService, Cost = g.Cost, RampRate = g.RampRate, Xd = g.Xd, XdPrime = g.XdPrime,
            XdSubtransient = g.XdSubtransient, X2 = g.X2, X0 = g.X0, Ra = g.Ra, H = g.H, D = g.D,
        },
        Branch r => new Branch
        {
            From = r.From, To = r.To, Name = r.Name, Kind = r.Kind, R = r.R, X = r.X, B = r.B, RateA = r.RateA, Tap = r.Tap,
            ShiftDeg = r.ShiftDeg, InService = r.InService, R0 = r.R0, X0 = r.X0, B0 = r.B0, Connection = r.Connection, LengthKm = r.LengthKm,
        },
        _ => throw new NotSupportedException(),
    };

    /// <summary>Checks the model for common data errors. Returns human-readable issues (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        try { Index(); } catch (InvalidOperationException e) { issues.Add(e.Message); return issues; }
        if (Buses.Count == 0) issues.Add("Network has no buses.");
        if (!Buses.Any(b => b.Type == BusType.Slack)) issues.Add("No slack (reference) bus.");
        foreach (var br in Branches)
        {
            if (!ContainsBus(br.From) || !ContainsBus(br.To)) issues.Add($"{br}: unknown terminal bus.");
            if (br.R == 0 && br.X == 0) issues.Add($"{br}: zero impedance.");
        }
        foreach (var l in Loads) if (!ContainsBus(l.Bus)) issues.Add($"Load at unknown bus {l.Bus}.");
        foreach (var g in Generators)
        {
            if (!ContainsBus(g.Bus)) issues.Add($"Generator at unknown bus {g.Bus}.");
            if (g.Pmin > g.Pmax) issues.Add($"Generator at bus {g.Bus}: Pmin > Pmax.");
        }
        foreach (var island in Topology.Islands(this).Skip(1))
            issues.Add($"Island without connection to the main grid: buses {string.Join(",", island.Take(10))}{(island.Count > 10 ? "…" : "")}.");
        return issues;
    }

    public override string ToString() =>
        $"{Name}: {Buses.Count} buses, {Branches.Count} branches, {Generators.Count} generators, {Loads.Count} loads (base {BaseMva} MVA)";
}
