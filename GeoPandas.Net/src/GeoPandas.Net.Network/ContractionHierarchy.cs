namespace GeoPandas.Net.Network;

/// <summary>
/// Contraction Hierarchies (Geisberger et al. 2008). Nodes are contracted in order of a lazy-updated
/// priority (edge difference + contracted neighbours); shortcuts preserve shortest-path distances.
/// Queries run a bidirectional Dijkstra on the upward graphs and unpack shortcuts to original edges.
/// Typical speed-ups versus Dijkstra are 100–1000× on road networks.
/// </summary>
public sealed class ContractionHierarchy
{
    private readonly NetworkGraph _graph;
    private readonly int _n;
    private readonly int[] _rank;
    // upward forward graph (u -> v with rank[v] > rank[u]) and upward backward graph (v <- u stored at v)
    private readonly int[] _fOff, _fTo, _fArc; private readonly double[] _fCost;
    private readonly int[] _bOff, _bTo, _bArc; private readonly double[] _bCost;
    // arc table: from, to, cost, middle node (-1 original), original edge id
    private readonly List<(int From, int To, double Cost, int Mid, int Edge)> _arcs;
    private readonly Dictionary<(int, int), int> _bestArc = new();

    public int ShortcutCount { get; }
    public TimeSpan BuildTime { get; }

    internal ContractionHierarchy(NetworkGraph graph, int[] offsets, int[] targets, double[] costs, int[] arcEdge)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _graph = graph;
        _n = graph.NodeCount;
        _arcs = [];
        var outAdj = new Dictionary<int, int>[_n];
        var inAdj = new Dictionary<int, int>[_n];
        for (int i = 0; i < _n; i++) { outAdj[i] = []; inAdj[i] = []; }

        void AddArc(int u, int v, double c, int mid, int edge)
        {
            if (u == v) return;
            if (outAdj[u].TryGetValue(v, out int existing) && _arcs[existing].Cost <= c) return;
            int id = _arcs.Count;
            _arcs.Add((u, v, c, mid, edge));
            outAdj[u][v] = id;
            inAdj[v][u] = id;
        }

        for (int u = 0; u < _n; u++)
            for (int a = offsets[u]; a < offsets[u + 1]; a++) AddArc(u, targets[a], costs[a], -1, arcEdge[a]);
        int originalArcs = _arcs.Count;

        var contracted = new bool[_n];
        var deletedNeighbors = new int[_n];
        _rank = new int[_n];
        var pq = new PriorityQueue<int, int>();

        // witness search: is there a path u -> w avoiding 'via' with cost <= limit?
        var wDist = new Dictionary<int, double>();
        bool HasWitness(int u, int w, int via, double limit, int maxSettled = 60)
        {
            wDist.Clear();
            var q = new PriorityQueue<int, double>();
            wDist[u] = 0; q.Enqueue(u, 0);
            int settled = 0;
            while (q.TryDequeue(out int x, out double dx))
            {
                if (dx > wDist[x]) continue;
                if (x == w) return dx <= limit;
                if (dx > limit || ++settled > maxSettled) return false;
                foreach (var (y, arc) in outAdj[x])
                {
                    if (y == via || contracted[y]) continue;
                    double nd = dx + _arcs[arc].Cost;
                    if (nd <= limit && (!wDist.TryGetValue(y, out var old) || nd < old)) { wDist[y] = nd; q.Enqueue(y, nd); }
                }
            }
            return false;
        }

        List<(int U, int W, double Cost)> Shortcuts(int v)
        {
            var list = new List<(int, int, double)>();
            foreach (var (u, inArc) in inAdj[v])
            {
                if (contracted[u]) continue;
                foreach (var (w, outArc) in outAdj[v])
                {
                    if (contracted[w] || w == u) continue;
                    double c = _arcs[inArc].Cost + _arcs[outArc].Cost;
                    if (!HasWitness(u, w, v, c)) list.Add((u, w, c));
                }
            }
            return list;
        }

        int Priority(int v)
        {
            int degree = inAdj[v].Keys.Count(x => !contracted[x]) + outAdj[v].Keys.Count(x => !contracted[x]);
            return Shortcuts(v).Count * 2 - degree + deletedNeighbors[v];
        }

        for (int v = 0; v < _n; v++) pq.Enqueue(v, Priority(v));
        int order = 0;
        while (pq.TryDequeue(out int v, out int prio))
        {
            if (contracted[v]) continue;
            int fresh = Priority(v);
            if (pq.Count > 0 && pq.TryPeek(out _, out int next) && fresh > next) { pq.Enqueue(v, fresh); continue; }
            foreach (var (u, w, c) in Shortcuts(v)) AddArc(u, w, c, v, -1);
            contracted[v] = true;
            _rank[v] = order++;
            foreach (var u in inAdj[v].Keys) if (!contracted[u]) deletedNeighbors[u]++;
            foreach (var w in outAdj[v].Keys) if (!contracted[w]) deletedNeighbors[w]++;
        }
        ShortcutCount = _arcs.Count - originalArcs;

        for (int i = 0; i < _arcs.Count; i++)
        {
            var (f, t, c, _, _) = _arcs[i];
            if (!_bestArc.TryGetValue((f, t), out int b) || _arcs[b].Cost > c) _bestArc[(f, t)] = i;
        }

        var fwd = new List<(int, int, double, int)>(); var bwd = new List<(int, int, double, int)>();
        foreach (var (key, id) in _bestArc)
        {
            var (u, v) = key;
            if (_rank[u] < _rank[v]) fwd.Add((u, v, _arcs[id].Cost, id));
            else bwd.Add((v, u, _arcs[id].Cost, id)); // stored at the higher node v, pointing to lower u
        }
        (_fOff, _fTo, _fCost, _fArc) = Csr(fwd);
        (_bOff, _bTo, _bCost, _bArc) = Csr(bwd);
        BuildTime = sw.Elapsed;
    }

    private (int[], int[], double[], int[]) Csr(List<(int From, int To, double Cost, int Arc)> arcs)
    {
        var off = new int[_n + 1];
        foreach (var a in arcs) off[a.From + 1]++;
        for (int i = 0; i < _n; i++) off[i + 1] += off[i];
        var pos = off.ToArray();
        var to = new int[arcs.Count]; var cost = new double[arcs.Count]; var arc = new int[arcs.Count];
        foreach (var a in arcs) { int p = pos[a.From]++; to[p] = a.To; cost[p] = a.Cost; arc[p] = a.Arc; }
        return (off, to, cost, arc);
    }

    /// <summary>Shortest-path cost only (fastest).</summary>
    public double Distance(int source, int target) => Query(source, target, out _, out _, out _);

    /// <summary>Full route with unpacked original edges and geometry.</summary>
    public Route Route(int source, int target)
    {
        double d = Query(source, target, out int meet, out var pf, out var pb);
        if (double.IsPositiveInfinity(d)) return Network.Route.NotFound;
        var upArcs = new List<int>();
        for (int v = meet; v != source && pf.TryGetValue(v, out int a); v = _arcs[a].From) upArcs.Add(a);
        upArcs.Reverse();
        for (int v = meet; v != target && pb.TryGetValue(v, out int a); v = _arcs[a].To) upArcs.Add(a);
        var steps = new List<(int Edge, int To)>();
        foreach (var a in upArcs) Unpack(a, steps);
        return _graph.BuildRoute(source, steps, d);
    }

    private void Unpack(int arc, List<(int Edge, int To)> steps)
    {
        var (f, t, _, mid, edge) = _arcs[arc];
        if (mid < 0) { steps.Add((edge, t)); return; }
        Unpack(_bestArc[(f, mid)], steps);
        Unpack(_bestArc[(mid, t)], steps);
    }

    private double Query(int s, int t, out int meet, out Dictionary<int, int> parentF, out Dictionary<int, int> parentB)
    {
        meet = -1;
        parentF = []; parentB = [];
        if (s == t) { meet = s; return 0; }
        var df = new Dictionary<int, double> { [s] = 0 };
        var db = new Dictionary<int, double> { [t] = 0 };
        var qf = new PriorityQueue<int, double>(); qf.Enqueue(s, 0);
        var qb = new PriorityQueue<int, double>(); qb.Enqueue(t, 0);
        double best = double.PositiveInfinity;
        while (qf.Count > 0 || qb.Count > 0)
        {
            bool progressed = false;
            if (qf.TryPeek(out _, out double tf) && tf < best)
            {
                qf.TryDequeue(out int u, out double du);
                progressed = true;
                if (du <= df[u])
                {
                    if (db.TryGetValue(u, out double bu) && du + bu < best) { best = du + bu; meet = u; }
                    for (int k = _fOff[u]; k < _fOff[u + 1]; k++)
                    {
                        int v = _fTo[k]; double nd = du + _fCost[k];
                        if (!df.TryGetValue(v, out var old) || nd < old) { df[v] = nd; parentF[v] = _fArc[k]; qf.Enqueue(v, nd); }
                    }
                }
            }
            if (qb.TryPeek(out _, out double tb) && tb < best)
            {
                qb.TryDequeue(out int u, out double du);
                progressed = true;
                if (du <= db[u])
                {
                    if (df.TryGetValue(u, out double fu) && du + fu < best) { best = du + fu; meet = u; }
                    for (int k = _bOff[u]; k < _bOff[u + 1]; k++)
                    {
                        int v = _bTo[k]; double nd = du + _bCost[k];
                        if (!db.TryGetValue(v, out var old) || nd < old) { db[v] = nd; parentB[v] = _bArc[k]; qb.Enqueue(v, nd); }
                    }
                }
            }
            if (!progressed) break;
        }
        return best;
    }
}
