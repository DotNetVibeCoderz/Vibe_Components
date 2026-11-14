namespace Electrical.Core.Network;

/// <summary>Graph utilities over in-service branches.</summary>
public static class Topology
{
    /// <summary>Adjacency list (bus positions) of in-service branches between in-service buses.</summary>
    public static List<int>[] Adjacency(PowerNetwork net)
    {
        var adj = new List<int>[net.BusCount];
        for (int i = 0; i < adj.Length; i++) adj[i] = [];
        foreach (var br in net.Branches)
        {
            if (!br.InService) continue;
            int f = net.IndexOf(br.From), t = net.IndexOf(br.To);
            if (net.Buses[f].Type == BusType.Isolated || net.Buses[t].Type == BusType.Isolated) continue;
            adj[f].Add(t);
            adj[t].Add(f);
        }
        return adj;
    }

    /// <summary>Connected components as lists of bus ids, largest (or slack-containing) first.</summary>
    public static List<List<int>> Islands(PowerNetwork net)
    {
        var adj = Adjacency(net);
        var seen = new bool[net.BusCount];
        var result = new List<List<int>>();
        var stack = new Stack<int>();
        for (int s = 0; s < net.BusCount; s++)
        {
            if (seen[s] || net.Buses[s].Type == BusType.Isolated) continue;
            var comp = new List<int>();
            stack.Push(s); seen[s] = true;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                comp.Add(net.Buses[u].Id);
                foreach (int v in adj[u]) if (!seen[v]) { seen[v] = true; stack.Push(v); }
            }
            result.Add(comp);
        }
        int? slack = net.SlackBus?.Id;
        return result.OrderByDescending(c => slack is int sid && c.Contains(sid)).ThenByDescending(c => c.Count).ToList();
    }

    /// <summary>True when the in-service network is radial (a forest: branches = buses − islands).</summary>
    public static bool IsRadial(PowerNetwork net)
    {
        int branches = net.Branches.Count(b => b.InService);
        return branches == net.Buses.Count(b => b.Type != BusType.Isolated) - Islands(net).Count;
    }

    /// <summary>Breadth-first hop distance from a bus to all others (−1 = unreachable).</summary>
    public static int[] HopDistance(PowerNetwork net, int fromBusId)
    {
        var adj = Adjacency(net);
        var d = new int[net.BusCount];
        Array.Fill(d, -1);
        int s = net.IndexOf(fromBusId);
        d[s] = 0;
        var q = new Queue<int>();
        q.Enqueue(s);
        while (q.Count > 0)
        {
            int u = q.Dequeue();
            foreach (int v in adj[u]) if (d[v] < 0) { d[v] = d[u] + 1; q.Enqueue(v); }
        }
        return d;
    }
}
