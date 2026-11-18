using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Network.Utility;

[Flags]
public enum TopologyRule
{
    None = 0,
    /// <summary>Line shorter than the tolerance.</summary>
    ZeroLength = 1,
    /// <summary>Two lines with the same geometry (either direction).</summary>
    Duplicate = 2,
    /// <summary>An end point no other line touches (excluding sources and allowed end points).</summary>
    Dangle = 4,
    /// <summary>A dangling end within the snap distance of another line: an undershoot or overshoot that should be connected.</summary>
    Undershoot = 8,
    /// <summary>Two lines cross or touch away from their end points without a shared node.</summary>
    CrossingWithoutNode = 16,
    /// <summary>A line intersects itself.</summary>
    SelfIntersection = 32,
    /// <summary>Lines not connected to any source (or, without sources, outside the largest component).</summary>
    Disconnected = 64,
    All = ZeroLength | Duplicate | Dangle | Undershoot | CrossingWithoutNode | SelfIntersection | Disconnected,
}

/// <summary>Topology validation for line networks (water, sewer, gas, electricity, roads).</summary>
public static class NetworkTopology
{
    /// <summary>
    /// Checks a line layer against topology rules. Returns one row per violation: <c>rule</c>, <c>severity</c> (error/warning),
    /// <c>line</c> (row index), <c>other</c> (second row or −1), <c>detail</c>, and the location (point or line).
    /// </summary>
    /// <param name="lines">The line layer (LineString rows; other geometry types are skipped).</param>
    /// <param name="tolerance">Coordinates closer than this are the same node.</param>
    /// <param name="snapDistance">Dangles closer than this to another line are reported as undershoots.</param>
    /// <param name="rules">The rules to check.</param>
    /// <param name="sources">Source points (reservoirs, substations…): their lines may dangle, and connectivity is measured from them.</param>
    /// <param name="allowedEnds">Points where a dangle is expected (hydrants, service ends, outfalls).</param>
    public static GeoDataFrame Validate(GeoDataFrame lines, double tolerance = 0.001, double snapDistance = 1.0, TopologyRule rules = TopologyRule.All,
        GeoDataFrame? sources = null, GeoDataFrame? allowedEnds = null)
    {
        var rule = new List<string>(); var severity = new List<string>(); var lineA = new List<long>(); var lineB = new List<long>();
        var detail = new List<string>(); var geoms = new List<NtsGeometry?>();
        void Add(TopologyRule r, string sev, int a, int b, string d, NtsGeometry g)
        {
            rule.Add(r.ToString()); severity.Add(sev); lineA.Add(a); lineB.Add(b); detail.Add(d); geoms.Add(g);
        }
        int n = lines.RowCount;
        var ls = new LineString?[n];
        for (int i = 0; i < n; i++)
            ls[i] = lines.Geometry[i] switch { LineString l => l, MultiLineString { NumGeometries: 1 } m => (LineString)m.GetGeometryN(0), _ => null };

        // node keys on a tolerance grid
        (long, long) Key(Coordinate c) => ((long)Math.Round(c.X / tolerance), (long)Math.Round(c.Y / tolerance));
        var degree = new Dictionary<(long, long), List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (ls[i] is not { } l) continue;
            foreach (var c in new[] { l.StartPoint.Coordinate, l.EndPoint.Coordinate })
            {
                var k = Key(c);
                if (!degree.TryGetValue(k, out var lst)) degree[k] = lst = [];
                lst.Add(i);
            }
        }
        var special = new HashSet<(long, long)>();
        foreach (var layer in new[] { sources, allowedEnds })
            if (layer is not null)
                for (int i = 0; i < layer.RowCount; i++) if (layer.Geometry[i] is { } g) special.Add(Key(g.Coordinate));
        var tree = StrTree<int>.FromGeometries(lines.Geometry.ToList());

        if (rules.HasFlag(TopologyRule.ZeroLength))
            for (int i = 0; i < n; i++)
                if (ls[i] is { } l && l.Length < tolerance) Add(TopologyRule.ZeroLength, "error", i, -1, $"length {l.Length:G4}", l.StartPoint);

        if (rules.HasFlag(TopologyRule.SelfIntersection))
            for (int i = 0; i < n; i++)
                if (ls[i] is { } l && !l.IsSimple) Add(TopologyRule.SelfIntersection, "error", i, -1, "line is not simple", l);

        if (rules.HasFlag(TopologyRule.Duplicate) || rules.HasFlag(TopologyRule.CrossingWithoutNode))
            for (int i = 0; i < n; i++)
            {
                if (ls[i] is not { } a) continue;
                foreach (var j in tree.Query(a.EnvelopeInternal))
                {
                    if (j <= i || ls[j] is not { } b) continue;
                    if (rules.HasFlag(TopologyRule.Duplicate) && (a.EqualsExact(b, tolerance) || a.EqualsExact((LineString)b.Reverse(), tolerance)))
                    {
                        Add(TopologyRule.Duplicate, "error", i, j, "identical geometry", a);
                        continue;
                    }
                    if (!rules.HasFlag(TopologyRule.CrossingWithoutNode) || !a.Intersects(b)) continue;
                    var ends = new[] { a.StartPoint.Coordinate, a.EndPoint.Coordinate, b.StartPoint.Coordinate, b.EndPoint.Coordinate };
                    foreach (var p in a.Intersection(b).Coordinates.Distinct())
                    {
                        bool atAEnd = a.StartPoint.Coordinate.Distance(p) <= tolerance || a.EndPoint.Coordinate.Distance(p) <= tolerance;
                        bool atBEnd = b.StartPoint.Coordinate.Distance(p) <= tolerance || b.EndPoint.Coordinate.Distance(p) <= tolerance;
                        if (atAEnd && atBEnd) continue;                  // proper node
                        Add(TopologyRule.CrossingWithoutNode, atAEnd || atBEnd ? "error" : "warning", i, j,
                            atAEnd || atBEnd ? "end point touches the other line's interior (T-junction not split)" : "lines cross without a node", Geo.Point(p.X, p.Y));
                    }
                }
            }

        if (rules.HasFlag(TopologyRule.Dangle) || rules.HasFlag(TopologyRule.Undershoot))
            foreach (var (k, users) in degree)
            {
                if (users.Count != 1 || special.Contains(k)) continue;
                int i = users[0];
                var l = ls[i]!;
                var c = Key(l.StartPoint.Coordinate) == k ? l.StartPoint.Coordinate : l.EndPoint.Coordinate;
                var pt = Geo.Point(c.X, c.Y);
                int nearest = -1; double nd = double.MaxValue;
                foreach (var j in tree.Query(new Envelope(c.X - snapDistance, c.X + snapDistance, c.Y - snapDistance, c.Y + snapDistance)))
                {
                    if (j == i || ls[j] is not { } o) continue;
                    double d = o.Distance(pt);
                    if (d < nd) { nd = d; nearest = j; }
                }
                if (nearest >= 0 && nd <= snapDistance && nd > tolerance && rules.HasFlag(TopologyRule.Undershoot))
                    Add(TopologyRule.Undershoot, "error", i, nearest, $"end is {nd:G4} from line {nearest}", pt);
                else if (rules.HasFlag(TopologyRule.Dangle) && !(nearest >= 0 && nd <= tolerance))
                    Add(TopologyRule.Dangle, "warning", i, -1, "dangling end", pt);
            }

        if (rules.HasFlag(TopologyRule.Disconnected))
        {
            // union-find over shared end points
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (var users in degree.Values) for (int t = 1; t < users.Count; t++) parent[Find(users[t])] = Find(users[0]);
            HashSet<int> good;
            if (special.Count > 0 && sources is not null)
            {
                var srcKeys = new HashSet<(long, long)>();
                for (int i = 0; i < sources.RowCount; i++) if (sources.Geometry[i] is { } g) srcKeys.Add(Key(g.Coordinate));
                good = degree.Where(kv => srcKeys.Contains(kv.Key)).SelectMany(kv => kv.Value).Select(Find).ToHashSet();
            }
            else
            {
                var sizes = Enumerable.Range(0, n).Where(i => ls[i] is not null).GroupBy(Find).OrderByDescending(g => g.Count()).ToList();
                good = sizes.Count > 0 ? [sizes[0].Key] : [];
            }
            for (int i = 0; i < n; i++)
                if (ls[i] is { } l && !good.Contains(Find(i)))
                    Add(TopologyRule.Disconnected, "error", i, -1, sources is null ? "outside the largest connected component" : "not connected to any source", l);
        }

        return GeoDataFrame.FromGeometries(geoms, lines.Crs,
            Column.Create("rule", rule.ToArray()), Column.Create("severity", severity.ToArray()),
            Column.Create("line", lineA.ToArray()), Column.Create("other", lineB.ToArray()), Column.Create("detail", detail.ToArray()));
    }
}

/// <summary>An isolation valve on a pipe, next to one of its end nodes.</summary>
public sealed record IsolationValve(string Id, string Pipe, string Node);

/// <summary>A valve segment: the pipes and nodes that are isolated together by closing the valves around them.</summary>
public sealed record ValveSegment(int Id, IReadOnlyList<string> Pipes, IReadOnlyList<string> Nodes, IReadOnlyList<string> Valves, double Length, double Demand, bool HasSource);

/// <summary>What closing the valves around a break isolates.</summary>
public sealed record IsolationResult(string Pipe, IReadOnlyList<string> ValvesToClose, IReadOnlyList<string> IsolatedPipes, IReadOnlyList<string> IsolatedNodes,
    IReadOnlyList<int> UnintendedSegments, double AffectedDemand, double IsolatedLength);

/// <summary>
/// Isolation-valve analysis (Walski's segment method): valve segments, the valves to close for a pipe break, and the segments cut off
/// unintentionally because they lose every path to a source.
/// </summary>
public sealed class ValveIsolation
{
    private readonly WaterNetwork _net;
    private readonly int[] _pipeSegment;              // per link
    private readonly int[] _nodeSegment;              // per node
    private readonly List<ValveSegment> _segments = [];
    private readonly List<(IsolationValve Valve, int A, int B)> _valveEdges = [];

    public ValveIsolation(WaterNetwork net, IEnumerable<IsolationValve> valves)
    {
        _net = net;
        var ix = net.NodeIndex();
        int nn = net.Nodes.Count, nl = net.Links.Count;
        var linkIx = net.Links.Select((l, i) => (l.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.OrdinalIgnoreCase);
        var valveList = valves.ToList();
        var valved = new HashSet<(int Link, int Node)>();
        foreach (var v in valveList)
        {
            if (!linkIx.TryGetValue(v.Pipe, out var li)) throw new ArgumentException($"Valve {v.Id}: pipe {v.Pipe} not found.");
            if (!ix.TryGetValue(v.Node, out var ni) || (net.Links[li].From != net.Nodes[ni].Id && net.Links[li].To != net.Nodes[ni].Id))
                throw new ArgumentException($"Valve {v.Id}: node {v.Node} is not an end of pipe {v.Pipe}.");
            valved.Add((li, ni));
        }
        // elements: links 0..nl-1, nodes nl..nl+nn-1
        var parent = Enumerable.Range(0, nl + nn).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        for (int k = 0; k < nl; k++)
        {
            int a = ix[net.Links[k].From], b = ix[net.Links[k].To];
            if (!valved.Contains((k, a))) Union(k, nl + a);
            if (!valved.Contains((k, b))) Union(k, nl + b);
        }
        var segOf = new Dictionary<int, int>();
        int Seg(int element) { int r = Find(element); if (!segOf.TryGetValue(r, out var s)) segOf[r] = s = segOf.Count; return s; }
        _pipeSegment = Enumerable.Range(0, nl).Select(Seg).ToArray();
        _nodeSegment = Enumerable.Range(0, nn).Select(i => Seg(nl + i)).ToArray();
        int count = segOf.Count;
        var pipes = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();
        var nodes = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();
        var vals = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();
        var length = new double[count]; var demand = new double[count]; var source = new bool[count];
        for (int k = 0; k < nl; k++) { pipes[_pipeSegment[k]].Add(net.Links[k].Id); length[_pipeSegment[k]] += net.Links[k].Length; }
        for (int i = 0; i < nn; i++)
        {
            int s = _nodeSegment[i];
            nodes[s].Add(net.Nodes[i].Id);
            if (net.Nodes[i].Kind == WaterNodeKind.Junction) demand[s] += net.DemandAt(net.Nodes[i]);
            else source[s] = true;
        }
        foreach (var v in valveList)
        {
            int a = _pipeSegment[linkIx[v.Pipe]], b = _nodeSegment[ix[v.Node]];
            _valveEdges.Add((v, a, b));
            vals[a].Add(v.Id);
            if (b != a) vals[b].Add(v.Id);
        }
        for (int s = 0; s < count; s++) _segments.Add(new ValveSegment(s, pipes[s], nodes[s], vals[s], length[s], demand[s], source[s]));
    }

    public IReadOnlyList<ValveSegment> Segments => _segments;

    /// <summary>Segments as multi-line geometries with length, demand, valve count and source flag.</summary>
    public GeoDataFrame SegmentsFrame()
    {
        var links = _net.LinksFrame();
        var geoms = _segments.Select(s => (NtsGeometry?)Geo.Factory.CreateMultiLineString(
            s.Pipes.Select(id => links.Geometry[_net.Links.FindIndex(l => l.Id == id)]).OfType<LineString>().ToArray())).ToList();
        return GeoDataFrame.FromGeometries(geoms, _net.Crs,
            Column.Create("segment", _segments.Select(s => (long)s.Id).ToArray()),
            Column.Create("pipes", _segments.Select(s => (long)s.Pipes.Count).ToArray()),
            Column.Create("valves", _segments.Select(s => (long)s.Valves.Count).ToArray()),
            Column.Create("length", _segments.Select(s => s.Length).ToArray()),
            Column.Create("demand", _segments.Select(s => s.Demand).ToArray()),
            Column.Create("has_source", _segments.Select(s => s.HasSource).ToArray()));
    }

    /// <summary>Valves to close to isolate a pipe, and everything that loses supply as a result.</summary>
    public IsolationResult Isolate(string pipeId)
    {
        int li = _net.Links.FindIndex(l => l.Id == pipeId);
        if (li < 0) throw new ArgumentException($"Pipe {pipeId} not found.");
        int seg = _pipeSegment[li];
        var closeValves = _segments[seg].Valves;
        // segment graph without the broken segment: which segments still reach a source?
        var adj = new Dictionary<int, List<int>>();
        foreach (var (_, a, b) in _valveEdges)
        {
            if (a == seg || b == seg || a == b) continue;
            (adj.TryGetValue(a, out var la) ? la : adj[a] = []).Add(b);
            (adj.TryGetValue(b, out var lb) ? lb : adj[b] = []).Add(a);
        }
        var reach = new HashSet<int>();
        var stack = new Stack<int>(_segments.Where(s => s.HasSource && s.Id != seg).Select(s => s.Id));
        foreach (var s in stack) reach.Add(s);
        while (stack.Count > 0)
            foreach (var t in adj.GetValueOrDefault(stack.Pop()) ?? [])
                if (reach.Add(t)) stack.Push(t);
        var unintended = _segments.Where(s => s.Id != seg && !reach.Contains(s.Id)).Select(s => s.Id).ToList();
        var all = unintended.Prepend(seg).Select(s => _segments[s]).ToList();
        return new IsolationResult(pipeId, closeValves,
            all.SelectMany(s => s.Pipes).ToList(), all.SelectMany(s => s.Nodes).ToList(), unintended,
            all.Sum(s => s.Demand), all.Sum(s => s.Length));
    }

    /// <summary>
    /// Places valve points (a GeoDataFrame of points with an optional <c>id</c> column) on the nearest pipe end:
    /// each valve belongs to the closest pipe and sits on the side of that pipe's nearer end node.
    /// </summary>
    public static IReadOnlyList<IsolationValve> SnapValves(WaterNetwork net, GeoDataFrame valvePoints, double maxDistance = 5)
    {
        var links = net.LinksFrame();
        var tree = StrTree<int>.FromGeometries(links.Geometry.ToList());
        var ix = net.NodeIndex();
        var res = new List<IsolationValve>();
        for (int i = 0; i < valvePoints.RowCount; i++)
        {
            if (valvePoints.Geometry[i] is not { } g) continue;
            var c = g.Coordinate;
            int best = -1; double bd = double.MaxValue;
            foreach (var j in tree.Query(new Envelope(c.X - maxDistance, c.X + maxDistance, c.Y - maxDistance, c.Y + maxDistance)))
            {
                if (net.Links[j].Kind != WaterLinkKind.Pipe || links.Geometry[j] is not { } lg) continue;
                double d = lg.Distance(g);
                if (d < bd) { bd = d; best = j; }
            }
            if (best < 0 || bd > maxDistance) continue;
            var l = net.Links[best];
            var a = net.Nodes[ix[l.From]]; var b = net.Nodes[ix[l.To]];
            double da = Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y)), db = Math.Sqrt((b.X - c.X) * (b.X - c.X) + (b.Y - c.Y) * (b.Y - c.Y));
            string id = valvePoints.Columns.Any(col => col.Name == "id") ? Convert.ToString(valvePoints["id"].GetValue(i), System.Globalization.CultureInfo.InvariantCulture)! : $"V{i + 1}";
            res.Add(new IsolationValve(id, l.Id, da <= db ? a.Id : b.Id));
        }
        return res;
    }
}
