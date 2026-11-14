using Electrical.Core.Network;

namespace Electrical.Visualization;

/// <summary>Options for single-line diagram layout and rendering. Distances are in SVG pixels.</summary>
public sealed record SldOptions
{
    /// <summary>Vertical distance between BFS layers.</summary>
    public double LayerGap { get; init; } = 150;
    /// <summary>Horizontal space per connection on a bus bar.</summary>
    public double SlotWidth { get; init; } = 40;
    public double MinBusWidth { get; init; } = 70;
    /// <summary>Minimum horizontal gap between neighbouring bus bars.</summary>
    public double BusGap { get; init; } = 56;
    public double Margin { get; init; } = 40;
    /// <summary>Barycenter sweeps (down + up) used for crossing reduction.</summary>
    public int CrossingIterations { get; init; } = 12;
    /// <summary>Root bus of the layered layout (default: slack bus, else the first bus).</summary>
    public int? RootBusId { get; init; }
    public string? Title { get; init; }
    public bool ShowLabels { get; init; } = true;
    public bool ShowGenerators { get; init; } = true;
    public bool ShowLoads { get; init; } = true;
    public bool ShowShunts { get; init; } = true;
    /// <summary>Draw flow arrows and MW labels when a power-flow result is supplied.</summary>
    public bool ShowFlows { get; init; } = true;
    /// <summary>Draw the voltage / loading legends when a power-flow result is supplied.</summary>
    public bool ShowLegend { get; init; } = true;
    /// <summary>Paint a background rectangle with <c>--eln-bg</c>.</summary>
    public bool Background { get; init; }
}

/// <summary>Placed bus bar: centre X, bar Y and width [px].</summary>
public sealed record SldBusNode(int BusId, int Layer, int Order, double X, double Y, double Width)
{
    public double Left => X - Width / 2;
    public double Right => X + Width / 2;
}

/// <summary>Routed branch: orthogonal polyline from the <c>From</c> bus attachment point to the <c>To</c> bus attachment point.</summary>
public sealed record SldEdge(int BranchIndex, int FromBus, int ToBus, bool IsTransformer, bool InService, IReadOnlyList<(double X, double Y)> Path);

public enum SldDeviceKind { Generator, Load, Shunt }

/// <summary>Device attached to a bus bar at (X, Y) on the bar; generators are drawn above, loads and shunts below.</summary>
public sealed record SldDevice(SldDeviceKind Kind, int Index, int BusId, double X, double Y);

/// <summary>
/// Automatic layered layout for a single-line diagram: BFS layers from the root (slack) bus, barycenter crossing reduction between
/// adjacent layers, slot-based attachment of branches and devices on horizontal bus bars, orthogonal edge routing.
/// Deterministic for a given network.
/// </summary>
public sealed class SldLayout
{
    private readonly Dictionary<int, SldBusNode> _byId;

    private SldLayout(List<SldBusNode> nodes, List<SldEdge> edges, List<SldDevice> devices, double width, double height, int layers, int initialCrossings, int crossings)
    {
        Nodes = nodes; Edges = edges; Devices = devices; Width = width; Height = height; LayerCount = layers;
        InitialCrossings = initialCrossings; Crossings = crossings;
        _byId = nodes.ToDictionary(n => n.BusId);
    }

    public IReadOnlyList<SldBusNode> Nodes { get; }
    public IReadOnlyList<SldEdge> Edges { get; }
    public IReadOnlyList<SldDevice> Devices { get; }
    /// <summary>Diagram width [px] (without legend).</summary>
    public double Width { get; }
    /// <summary>Diagram height [px] (without legend).</summary>
    public double Height { get; }
    public int LayerCount { get; }
    /// <summary>Edge crossings between adjacent layers in BFS discovery order.</summary>
    public int InitialCrossings { get; }
    /// <summary>Edge crossings after barycenter reduction (≤ <see cref="InitialCrossings"/>).</summary>
    public int Crossings { get; }

    public SldBusNode Node(int busId) => _byId[busId];

    /// <summary>Computes the layout for <paramref name="net"/>.</summary>
    public static SldLayout Compute(PowerNetwork net, SldOptions? options = null)
    {
        var o = options ?? new SldOptions();
        int n = net.BusCount;
        if (n == 0) return new SldLayout([], [], [], 2 * o.Margin, 2 * o.Margin, 0, 0, 0);

        // adjacency over all branches (out-of-service ones still shape the drawing)
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        var branchEnds = new (int f, int t)[net.Branches.Count];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            branchEnds[k] = (f, t);
            if (f == t) continue;
            if (!adj[f].Contains(t)) adj[f].Add(t);
            if (!adj[t].Contains(f)) adj[t].Add(f);
        }
        foreach (var a in adj) a.Sort((x, y) => net.Buses[x].Id.CompareTo(net.Buses[y].Id));

        // BFS layering, one component after another
        var layer = new int[n];
        Array.Fill(layer, -1);
        var discovery = new List<int>(n);
        int root = o.RootBusId is int rid && net.ContainsBus(rid) ? net.IndexOf(rid) : net.SlackBus is { } sb ? net.IndexOf(sb.Id) : 0;
        var starts = new List<int> { root };
        for (int i = 0; i < n; i++) if (i != root) starts.Add(i);
        var q = new Queue<int>();
        foreach (int s in starts)
        {
            if (layer[s] >= 0) continue;
            layer[s] = 0; q.Enqueue(s);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                discovery.Add(u);
                foreach (int v in adj[u]) if (layer[v] < 0) { layer[v] = layer[u] + 1; q.Enqueue(v); }
            }
        }
        int layers = layer.Max() + 1;
        var order = new List<int>[layers];
        for (int l = 0; l < layers; l++) order[l] = [];
        foreach (int u in discovery) order[layer[u]].Add(u);

        // barycenter crossing reduction
        var pos = new int[n];
        void Reindex() { foreach (var lst in order) for (int i = 0; i < lst.Count; i++) pos[lst[i]] = i; }
        Reindex();
        int initial = CountCrossings(order, adj, layer, pos);
        int best = initial;
        var bestOrder = order.Select(l => l.ToList()).ToArray();
        for (int it = 0; it < o.CrossingIterations && best > 0; it++)
        {
            bool down = it % 2 == 0;
            for (int step = 1; step < layers; step++)
            {
                int l = down ? step : layers - 1 - step;
                int refLayer = down ? l - 1 : l + 1;
                var bary = new Dictionary<int, double>();
                foreach (int u in order[l])
                {
                    var nb = adj[u].Where(v => layer[v] == refLayer).ToList();
                    bary[u] = nb.Count == 0 ? pos[u] : nb.Average(v => (double)pos[v]);
                }
                order[l] = order[l].OrderBy(u => bary[u]).ThenBy(u => pos[u]).ToList();
                for (int i = 0; i < order[l].Count; i++) pos[order[l][i]] = i;
            }
            int c = CountCrossings(order, adj, layer, pos);
            if (c < best) { best = c; bestOrder = order.Select(x => x.ToList()).ToArray(); }
        }
        order = bestOrder;
        Reindex();

        // device counts per bus
        var gens = new List<int>[n]; var loads = new List<int>[n];
        for (int i = 0; i < n; i++) { gens[i] = []; loads[i] = []; }
        if (o.ShowGenerators) for (int g = 0; g < net.Generators.Count; g++) if (net.ContainsBus(net.Generators[g].Bus)) gens[net.IndexOf(net.Generators[g].Bus)].Add(g);
        if (o.ShowLoads) for (int d = 0; d < net.Loads.Count; d++) if (net.ContainsBus(net.Loads[d].Bus)) loads[net.IndexOf(net.Loads[d].Bus)].Add(d);
        var up = new int[n]; var dn = new int[n];
        foreach (var (f, t) in branchEnds)
        {
            if (f == t) continue;
            if (layer[f] == layer[t]) { dn[f]++; dn[t]++; }
            else if (layer[f] < layer[t]) { dn[f]++; up[t]++; }
            else { up[f]++; dn[t]++; }
        }
        var width = new double[n];
        for (int i = 0; i < n; i++)
        {
            int shunt = o.ShowShunts && (net.Buses[i].Bs != 0 || net.Buses[i].Gs != 0) ? 1 : 0;
            int slots = Math.Max(up[i] + gens[i].Count, dn[i] + loads[i].Count + shunt);
            // room for "name" (11 px bold) on the left and "1.000 pu ∠-15.2°" (10 px) on the right of the bar
            double labelW = o.ShowLabels ? (net.Buses[i].Name ?? $"Bus {net.Buses[i].Id}").Length * 6.8 + 16 * 5.6 + 14 : 0;
            width[i] = Math.Max(Math.Max(o.MinBusWidth, labelW), (slots + 1) * o.SlotWidth);
        }

        // x coordinates: sequential packing, then a few barycentric alignment passes that respect ordering and spacing
        var x = new double[n];
        foreach (var lst in order)
        {
            double cursor = 0;
            foreach (int u in lst) { x[u] = cursor + width[u] / 2; cursor += width[u] + o.BusGap; }
        }
        for (int pass = 0; pass < 6; pass++)
        {
            bool down = pass % 2 == 0;
            for (int step = 0; step < layers; step++)
            {
                int l = down ? step : layers - 1 - step;
                var lst = order[l];
                var desired = new double[lst.Count];
                for (int i = 0; i < lst.Count; i++)
                {
                    var nb = adj[lst[i]].Where(v => layer[v] != l).ToList();
                    desired[i] = nb.Count == 0 ? x[lst[i]] : nb.Average(v => x[v]);
                }
                // left-to-right then right-to-left constraint resolution, averaged
                var a = new double[lst.Count];
                var b = new double[lst.Count];
                for (int i = 0; i < lst.Count; i++)
                    a[i] = i == 0 ? desired[i] : Math.Max(desired[i], a[i - 1] + (width[lst[i - 1]] + width[lst[i]]) / 2 + o.BusGap);
                for (int i = lst.Count - 1; i >= 0; i--)
                    b[i] = i == lst.Count - 1 ? desired[i] : Math.Min(desired[i], b[i + 1] - (width[lst[i + 1]] + width[lst[i]]) / 2 - o.BusGap);
                for (int i = 0; i < lst.Count; i++) x[lst[i]] = (a[i] + b[i]) / 2;
                for (int i = 1; i < lst.Count; i++)
                {
                    double min = x[lst[i - 1]] + (width[lst[i - 1]] + width[lst[i]]) / 2 + o.BusGap;
                    if (x[lst[i]] < min) x[lst[i]] = min;
                }
            }
        }
        double minLeft = Enumerable.Range(0, n).Min(i => x[i] - width[i] / 2);
        const double genSpace = 70, loadSpace = 90;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = Math.Round(x[i] - minLeft + o.Margin, 2);
            y[i] = o.Margin + (o.Title is null ? 0 : 24) + genSpace + layer[i] * o.LayerGap;
        }

        var nodes = new List<SldBusNode>(n);
        for (int i = 0; i < n; i++) nodes.Add(new SldBusNode(net.Buses[i].Id, layer[i], pos[i], x[i], y[i], width[i]));

        // slot assignment: top = upward branches + generators, bottom = downward + same-layer branches + loads + shunt
        var topItems = new List<(double key, object item)>[n];
        var botItems = new List<(double key, object item)>[n];
        for (int i = 0; i < n; i++) { topItems[i] = []; botItems[i] = []; }
        for (int k = 0; k < branchEnds.Length; k++)
        {
            var (f, t) = branchEnds[k];
            if (f == t) continue;
            if (layer[f] == layer[t]) { botItems[f].Add((x[t], (k, 0))); botItems[t].Add((x[f], (k, 1))); }
            else
            {
                int hi = layer[f] < layer[t] ? f : t, lo = hi == f ? t : f;
                botItems[hi].Add((x[lo], (k, hi == f ? 0 : 1)));
                topItems[lo].Add((x[hi], (k, lo == f ? 0 : 1)));
            }
        }
        var devices = new List<SldDevice>();
        var attach = new Dictionary<(int branch, int end), (double X, double Y)>();
        for (int i = 0; i < n; i++)
        {
            double left = x[i] - width[i] / 2;
            var top = new List<object>();
            top.AddRange(gens[i].Select(g => (object)new SldDevice(SldDeviceKind.Generator, g, net.Buses[i].Id, 0, 0)));
            top.AddRange(topItems[i].OrderBy(e => e.key).Select(e => e.item));
            var bot = new List<object>();
            bot.AddRange(botItems[i].OrderBy(e => e.key).Select(e => e.item));
            bot.AddRange(loads[i].Select(d => (object)new SldDevice(SldDeviceKind.Load, d, net.Buses[i].Id, 0, 0)));
            if (o.ShowShunts && (net.Buses[i].Bs != 0 || net.Buses[i].Gs != 0)) bot.Add(new SldDevice(SldDeviceKind.Shunt, i, net.Buses[i].Id, 0, 0));
            Place(top, y[i]);
            Place(bot, y[i]);
            void Place(List<object> items, double yy)
            {
                for (int s = 0; s < items.Count; s++)
                {
                    double sx = Math.Round(left + (s + 1) * width[i] / (items.Count + 1), 2);
                    if (items[s] is SldDevice d) devices.Add(d with { X = sx, Y = yy });
                    else if (items[s] is ValueTuple<int, int> be) attach[be] = (sx, yy);
                }
            }
        }

        // orthogonal routing with staggered horizontal runs per layer gap
        var edges = new List<SldEdge>(branchEnds.Length);
        var gapCount = new Dictionary<int, int>();
        var gapTotal = new Dictionary<int, int>();
        foreach (var (f, t) in branchEnds)
        {
            if (f == t) continue;
            int key = layer[f] == layer[t] ? -1 - layer[f] : Math.Min(layer[f], layer[t]);
            gapTotal[key] = gapTotal.GetValueOrDefault(key) + 1;
        }
        for (int k = 0; k < branchEnds.Length; k++)
        {
            var (f, t) = branchEnds[k];
            var br = net.Branches[k];
            if (f == t) continue;
            var a = attach[(k, 0)];
            var b = attach[(k, 1)];
            List<(double, double)> path;
            if (layer[f] == layer[t])
            {
                int key = -1 - layer[f];
                int idx = gapCount.GetValueOrDefault(key); gapCount[key] = idx + 1;
                double yr = a.Y + 44 + 8 * (idx % 4);
                path = [a, (a.X, yr), (b.X, yr), b];
            }
            else
            {
                int key = Math.Min(layer[f], layer[t]);
                int idx = gapCount.GetValueOrDefault(key); gapCount[key] = idx + 1;
                double y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                int total = gapTotal[key];
                double ym = Math.Round(y0 + (y1 - y0) * (0.2 + 0.36 * (total <= 1 ? 0.5 : idx / (double)(total - 1))), 2);
                path = Math.Abs(a.X - b.X) < 0.5 ? [a, b] : [a, (a.X, ym), (b.X, ym), b];
            }
            edges.Add(new SldEdge(k, br.From, br.To, br.IsTransformer, br.InService, path));
        }

        double w = nodes.Max(nd => nd.Right) + o.Margin;
        double h = nodes.Max(nd => nd.Y) + loadSpace + o.Margin;
        return new SldLayout(nodes, edges, devices, Math.Round(w, 2), Math.Round(h, 2), layers, initial, best);
    }

    /// <summary>Counts pairwise crossings of edges between adjacent layers for a given ordering.</summary>
    private static int CountCrossings(List<int>[] order, List<int>[] adj, int[] layer, int[] pos)
    {
        int total = 0;
        for (int l = 0; l + 1 < order.Length; l++)
        {
            var e = new List<(int a, int b)>();
            foreach (int u in order[l]) foreach (int v in adj[u]) if (layer[v] == l + 1) e.Add((pos[u], pos[v]));
            for (int i = 0; i < e.Count; i++)
                for (int j = i + 1; j < e.Count; j++)
                    if ((e[i].a - e[j].a) * (e[i].b - e[j].b) < 0) total++;
        }
        return total;
    }
}
