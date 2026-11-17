using System.Globalization;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Network;

/// <summary>An edge of the network (one direction of travel is an arc).</summary>
public sealed record NetworkEdge(int Id, int From, int To, double Cost, double Length, LineString Geometry, int SourceRow, bool Oneway);

/// <summary>A route result.</summary>
public sealed record Route(bool Found, int[] Nodes, int[] Edges, double Cost, double Length, LineString? Geometry)
{
    public static Route NotFound { get; } = new(false, [], [], double.PositiveInfinity, double.PositiveInfinity, null);
}

public enum PathAlgorithm { Dijkstra, AStar, BidirectionalDijkstra }

/// <summary>
/// Routable graph built from line geometries. Nodes are line end points snapped on a tolerance grid;
/// adjacency is stored in CSR (compressed sparse row) arrays for cache-friendly traversal.
/// </summary>
public sealed class NetworkGraph
{
    private readonly int[] _offsets;   // node -> first arc
    private readonly int[] _targets;   // arc -> target node
    private readonly double[] _costs;  // arc -> cost
    private readonly int[] _arcEdge;   // arc -> edge id
    private readonly int[] _rOffsets, _rTargets, _rArcEdge; // reverse graph
    private readonly double[] _rCosts;
    private readonly double _minCostPerLength;
    private KdTree? _nodeIndex;

    private NetworkGraph(double[] nx, double[] ny, List<NetworkEdge> edges, CoordinateReferenceSystem? crs)
    {
        NodeX = nx; NodeY = ny; Edges = edges; Crs = crs;
        int n = nx.Length;
        var arcs = new List<(int From, int To, double Cost, int Edge)>();
        foreach (var e in edges)
        {
            arcs.Add((e.From, e.To, e.Cost, e.Id));
            if (!e.Oneway) arcs.Add((e.To, e.From, e.Cost, e.Id));
        }
        (_offsets, _targets, _costs, _arcEdge) = BuildCsr(n, arcs);
        (_rOffsets, _rTargets, _rCosts, _rArcEdge) = BuildCsr(n, arcs.Select(a => (a.To, a.From, a.Cost, a.Edge)).ToList());
        _minCostPerLength = edges.Where(e => e.Length > 0).Select(e => e.Cost / e.Length).DefaultIfEmpty(0).Min();
        ArcCount = arcs.Count;
    }

    private static (int[], int[], double[], int[]) BuildCsr(int n, List<(int From, int To, double Cost, int Edge)> arcs)
    {
        var offsets = new int[n + 1];
        foreach (var a in arcs) offsets[a.From + 1]++;
        for (int i = 0; i < n; i++) offsets[i + 1] += offsets[i];
        var pos = offsets.ToArray();
        var t = new int[arcs.Count]; var c = new double[arcs.Count]; var e = new int[arcs.Count];
        foreach (var a in arcs) { int p = pos[a.From]++; t[p] = a.To; c[p] = a.Cost; e[p] = a.Edge; }
        return (offsets, t, c, e);
    }

    public double[] NodeX { get; }
    public double[] NodeY { get; }
    public IReadOnlyList<NetworkEdge> Edges { get; }
    public CoordinateReferenceSystem? Crs { get; }
    public int NodeCount => NodeX.Length;
    public int ArcCount { get; }

    /// <summary>
    /// Builds a graph from a line GeoDataFrame. <paramref name="costColumn"/> defaults to geometric length;
    /// <paramref name="onewayColumn"/> marks directed edges (true / 1 / "yes" / "y" / "T").
    /// Lines should be noded at intersections (as in typical road / pipe datasets).
    /// </summary>
    public static NetworkGraph FromLines(GeoDataFrame lines, string? costColumn = null, string? onewayColumn = null, bool directed = false, double snapTolerance = 1e-6)
    {
        var nodes = new Dictionary<(long, long), int>();
        var nx = new List<double>(); var ny = new List<double>();
        var edges = new List<NetworkEdge>();
        int NodeOf(Coordinate c)
        {
            var key = ((long)Math.Round(c.X / snapTolerance), (long)Math.Round(c.Y / snapTolerance));
            if (!nodes.TryGetValue(key, out int id)) { id = nx.Count; nodes[key] = id; nx.Add(c.X); ny.Add(c.Y); }
            return id;
        }
        var cost = costColumn is null ? null : lines[costColumn];
        var oneway = onewayColumn is null ? null : lines[onewayColumn];
        for (int r = 0; r < lines.RowCount; r++)
        {
            var g = lines.Geometry[r];
            if (g is null) continue;
            foreach (var part in GeometryOps.Explode(g))
            {
                if (part is not LineString ls || ls.NumPoints < 2) continue;
                int a = NodeOf(ls.StartPoint.Coordinate), b = NodeOf(ls.EndPoint.Coordinate);
                double len = ls.Length;
                double c = cost is null ? len : cost.GetDouble(r) * (g.NumGeometries > 1 && g.Length > 0 ? len / g.Length : 1);
                bool ow = directed || (oneway is not null && IsTrue(oneway.GetValue(r)));
                edges.Add(new NetworkEdge(edges.Count, a, b, c, len, ls, r, ow));
            }
        }
        return new NetworkGraph(nx.ToArray(), ny.ToArray(), edges, lines.Crs);
    }

    private static bool IsTrue(object? v) => v switch
    {
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "t" or "1" or "fw",
        _ => false
    };

    /// <summary>Nearest graph node to a location.</summary>
    public int NearestNode(double x, double y)
    {
        _nodeIndex ??= new KdTree(NodeX, NodeY);
        var r = _nodeIndex.Nearest(x, y, 1);
        return r.Length == 0 ? -1 : r[0].Index;
    }

    public Point NodePoint(int node) => Geo.Point(NodeX[node], NodeY[node]);

    /// <summary>Shortest path between two locations (snapped to the nearest nodes).</summary>
    public Route ShortestPath(Point from, Point to, PathAlgorithm algorithm = PathAlgorithm.AStar) =>
        ShortestPath(NearestNode(from.X, from.Y), NearestNode(to.X, to.Y), algorithm);

    public Route ShortestPath(int source, int target, PathAlgorithm algorithm = PathAlgorithm.Dijkstra) => algorithm switch
    {
        PathAlgorithm.AStar => Search(source, target, useHeuristic: true),
        PathAlgorithm.BidirectionalDijkstra => Bidirectional(source, target),
        _ => Search(source, target, useHeuristic: false)
    };

    private double Heuristic(int a, int b)
    {
        double dx = NodeX[a] - NodeX[b], dy = NodeY[a] - NodeY[b];
        return Math.Sqrt(dx * dx + dy * dy) * _minCostPerLength;
    }

    private Route Search(int s, int t, bool useHeuristic)
    {
        if (s < 0 || t < 0) return Route.NotFound;
        var dist = new double[NodeCount];
        Array.Fill(dist, double.PositiveInfinity);
        var prevArc = new int[NodeCount];
        Array.Fill(prevArc, -1);
        var closed = new bool[NodeCount];
        var pq = new PriorityQueue<int, double>();
        dist[s] = 0;
        pq.Enqueue(s, useHeuristic ? Heuristic(s, t) : 0);
        while (pq.TryDequeue(out int u, out _))
        {
            if (closed[u]) continue;
            closed[u] = true;
            if (u == t) break;
            for (int a = _offsets[u]; a < _offsets[u + 1]; a++)
            {
                int v = _targets[a];
                double nd = dist[u] + _costs[a];
                if (nd < dist[v])
                {
                    dist[v] = nd;
                    prevArc[v] = a;
                    pq.Enqueue(v, useHeuristic ? nd + Heuristic(v, t) : nd);
                }
            }
        }
        if (double.IsPositiveInfinity(dist[t])) return Route.NotFound;
        var arcs = new List<int>();
        for (int v = t; v != s; )
        {
            int a = prevArc[v];
            arcs.Add(a);
            v = ArcSource(a);
        }
        arcs.Reverse();
        return BuildRoute(s, arcs.Select(a => (_arcEdge[a], _targets[a])).ToList(), dist[t]);
    }

    private int ArcSource(int arc)
    {
        // binary search the CSR offsets
        int lo = 0, hi = NodeCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_offsets[mid] <= arc) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private Route Bidirectional(int s, int t)
    {
        if (s < 0 || t < 0) return Route.NotFound;
        if (s == t) return BuildRoute(s, [], 0);
        var df = new double[NodeCount]; var db = new double[NodeCount];
        Array.Fill(df, double.PositiveInfinity); Array.Fill(db, double.PositiveInfinity);
        var pf = new int[NodeCount]; var pb = new int[NodeCount];
        Array.Fill(pf, -1); Array.Fill(pb, -1);
        var qf = new PriorityQueue<int, double>(); var qb = new PriorityQueue<int, double>();
        df[s] = 0; db[t] = 0; qf.Enqueue(s, 0); qb.Enqueue(t, 0);
        double best = double.PositiveInfinity; int meet = -1;
        while (qf.Count > 0 && qb.Count > 0)
        {
            qf.TryPeek(out _, out double tf); qb.TryPeek(out _, out double tb);
            if (tf + tb >= best) break;
            if (tf <= tb)
            {
                qf.TryDequeue(out int u, out double du);
                if (du > df[u]) continue;
                for (int a = _offsets[u]; a < _offsets[u + 1]; a++)
                {
                    int v = _targets[a]; double nd = du + _costs[a];
                    if (nd < df[v]) { df[v] = nd; pf[v] = a; qf.Enqueue(v, nd); }
                    if (df[v] + db[v] < best) { best = df[v] + db[v]; meet = v; }
                }
            }
            else
            {
                qb.TryDequeue(out int u, out double du);
                if (du > db[u]) continue;
                for (int a = _rOffsets[u]; a < _rOffsets[u + 1]; a++)
                {
                    int v = _rTargets[a]; double nd = du + _rCosts[a];
                    if (nd < db[v]) { db[v] = nd; pb[v] = a; qb.Enqueue(v, nd); }
                    if (df[v] + db[v] < best) { best = df[v] + db[v]; meet = v; }
                }
            }
        }
        if (meet < 0) return Route.NotFound;
        var steps = new List<(int Edge, int To)>();
        var fwd = new List<int>();
        for (int v = meet; v != s; v = ArcSource(pf[v])) fwd.Add(pf[v]);
        fwd.Reverse();
        foreach (var a in fwd) steps.Add((_arcEdge[a], _targets[a]));
        for (int v = meet; v != t; )
        {
            int ra = pb[v];
            int next = RArcSource(ra); // reverse arc goes next -> v in reverse graph, i.e. v -> next forward
            steps.Add((_rArcEdge[ra], next));
            v = next;
        }
        return BuildRoute(s, steps, best);
    }

    private int RArcSource(int arc)
    {
        // In the reverse CSR, arc belongs to node u (row) and _rTargets[arc] is the forward predecessor.
        // For backward expansion we stored pb[v] = arc in row u with target v, so the forward step is v -> u.
        int lo = 0, hi = NodeCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_rOffsets[mid] <= arc) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    internal Route BuildRoute(int source, List<(int Edge, int To)> steps, double cost)
    {
        var nodes = new List<int> { source };
        var coords = new List<Coordinate>();
        double length = 0;
        int current = source;
        foreach (var (eid, to) in steps)
        {
            var e = Edges[eid];
            var cs = e.Geometry.Coordinates;
            if (e.From != current) cs = cs.Reverse().ToArray();
            if (coords.Count > 0) cs = cs[1..];
            coords.AddRange(cs);
            length += e.Length;
            nodes.Add(to);
            current = to;
        }
        if (coords.Count == 1) coords.Add(coords[0].Copy());
        var geom = coords.Count >= 2 ? Geo.Factory.CreateLineString(coords.ToArray()) : Geo.Factory.CreateLineString([new Coordinate(NodeX[source], NodeY[source]), new Coordinate(NodeX[source], NodeY[source])]);
        return new Route(true, nodes.ToArray(), steps.Select(s => s.Edge).ToArray(), cost, length, geom);
    }

    /// <summary>Costs from <paramref name="source"/> to every node (∞ when unreachable), optionally bounded.</summary>
    public double[] SingleSourceCosts(int source, double maxCost = double.PositiveInfinity)
    {
        var dist = new double[NodeCount];
        Array.Fill(dist, double.PositiveInfinity);
        var pq = new PriorityQueue<int, double>();
        dist[source] = 0;
        pq.Enqueue(source, 0);
        while (pq.TryDequeue(out int u, out double du))
        {
            if (du > dist[u] || du > maxCost) continue;
            for (int a = _offsets[u]; a < _offsets[u + 1]; a++)
            {
                int v = _targets[a]; double nd = du + _costs[a];
                if (nd < dist[v] && nd <= maxCost) { dist[v] = nd; pq.Enqueue(v, nd); }
            }
        }
        return dist;
    }

    /// <summary>
    /// Service area / isochrone: edges reachable within <paramref name="maxCost"/> and a polygon built by
    /// buffering them by <paramref name="bufferDistance"/> (CRS units).
    /// </summary>
    public (GeoDataFrame Edges, NetTopologySuite.Geometries.Geometry Polygon) ServiceArea(int source, double maxCost, double bufferDistance)
    {
        var dist = SingleSourceCosts(source, maxCost);
        var reached = Edges.Where(e => dist[e.From] <= maxCost || dist[e.To] <= maxCost).ToList();
        var geoms = reached.Select(e => (NetTopologySuite.Geometries.Geometry?)e.Geometry).ToArray();
        var gdf = GeoDataFrame.FromGeometries(geoms, Crs,
            Column.Create("edge_id", reached.Select(e => (long)e.Id).ToArray()),
            Column.Create("cost_to_start", reached.Select(e => Math.Min(dist[e.From], dist[e.To])).ToArray()));
        var poly = reached.Count == 0 ? Geo.Factory.CreatePolygon() : Geo.Factory.BuildGeometry(geoms!).Buffer(bufferDistance, 4);
        return (gdf, poly);
    }

    /// <summary>Connected component id per node (undirected).</summary>
    public int[] ConnectedComponents()
    {
        var comp = Enumerable.Repeat(-1, NodeCount).ToArray();
        int c = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < NodeCount; s++)
        {
            if (comp[s] >= 0) continue;
            stack.Push(s); comp[s] = c;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                for (int a = _offsets[u]; a < _offsets[u + 1]; a++) if (comp[_targets[a]] < 0) { comp[_targets[a]] = c; stack.Push(_targets[a]); }
                for (int a = _rOffsets[u]; a < _rOffsets[u + 1]; a++) if (comp[_rTargets[a]] < 0) { comp[_rTargets[a]] = c; stack.Push(_rTargets[a]); }
            }
            c++;
        }
        return comp;
    }

    /// <summary>
    /// Utility-network trace: every edge reachable from <paramref name="startNode"/> through edges accepted by
    /// <paramref name="passable"/> (e.g. closed valves / open switches block flow). Direction-aware when <paramref name="downstreamOnly"/>.
    /// </summary>
    public (int[] Nodes, int[] Edges) Trace(int startNode, Func<NetworkEdge, bool>? passable = null, bool downstreamOnly = false)
    {
        var seenN = new bool[NodeCount];
        var seenE = new HashSet<int>();
        var q = new Queue<int>();
        q.Enqueue(startNode); seenN[startNode] = true;
        while (q.Count > 0)
        {
            int u = q.Dequeue();
            void Visit(int v, int eid)
            {
                var e = Edges[eid];
                if (passable is not null && !passable(e)) return;
                seenE.Add(eid);
                if (!seenN[v]) { seenN[v] = true; q.Enqueue(v); }
            }
            for (int a = _offsets[u]; a < _offsets[u + 1]; a++) Visit(_targets[a], _arcEdge[a]);
            if (!downstreamOnly) for (int a = _rOffsets[u]; a < _rOffsets[u + 1]; a++) Visit(_rTargets[a], _rArcEdge[a]);
        }
        return (Enumerable.Range(0, NodeCount).Where(i => seenN[i]).ToArray(), seenE.Order().ToArray());
    }

    /// <summary>Edges as a GeoDataFrame (id, from, to, cost, length, source_row).</summary>
    public GeoDataFrame EdgesFrame() => GeoDataFrame.FromGeometries(Edges.Select(e => (NetTopologySuite.Geometries.Geometry?)e.Geometry), Crs,
        Column.Create("edge_id", Edges.Select(e => (long)e.Id).ToArray()),
        Column.Create("from", Edges.Select(e => (long)e.From).ToArray()),
        Column.Create("to", Edges.Select(e => (long)e.To).ToArray()),
        Column.Create("cost", Edges.Select(e => e.Cost).ToArray()),
        Column.Create("length", Edges.Select(e => e.Length).ToArray()),
        Column.Create("source_row", Edges.Select(e => (long)e.SourceRow).ToArray()));

    /// <summary>Nodes as a point GeoDataFrame with degree.</summary>
    public GeoDataFrame NodesFrame() => GeoDataFrame.FromGeometries(
        Enumerable.Range(0, NodeCount).Select(i => (NetTopologySuite.Geometries.Geometry?)NodePoint(i)), Crs,
        Column.Create("node_id", Enumerable.Range(0, NodeCount).Select(i => (long)i).ToArray()),
        Column.Create("degree", Enumerable.Range(0, NodeCount).Select(i => (long)(_offsets[i + 1] - _offsets[i] + _rOffsets[i + 1] - _rOffsets[i])).ToArray()));

    /// <summary>Preprocesses a Contraction Hierarchy for very fast repeated queries.</summary>
    public ContractionHierarchy BuildContractionHierarchy() => new(this, _offsets, _targets, _costs, _arcEdge);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"NetworkGraph: {NodeCount} nodes, {Edges.Count} edges, {ArcCount} arcs");
}
