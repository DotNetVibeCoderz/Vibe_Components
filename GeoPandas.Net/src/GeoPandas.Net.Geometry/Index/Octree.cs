namespace GeoPandas.Net.Geometry.Index;

/// <summary>
/// Point octree for 3-D data (point clouds, BIM element centroids, LiDAR). Supports box and radius queries.
/// </summary>
public sealed class Octree
{
    private readonly double[] _x, _y, _z;
    private readonly Node _root;
    private readonly int _leafSize;

    public Octree(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, ReadOnlySpan<double> zs, int leafSize = 32)
    {
        if (xs.Length != ys.Length || ys.Length != zs.Length) throw new ArgumentException("Coordinate arrays must have the same length.");
        _x = xs.ToArray(); _y = ys.ToArray(); _z = zs.ToArray();
        _leafSize = leafSize;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int i = 0; i < _x.Length; i++)
        {
            minX = Math.Min(minX, _x[i]); maxX = Math.Max(maxX, _x[i]);
            minY = Math.Min(minY, _y[i]); maxY = Math.Max(maxY, _y[i]);
            minZ = Math.Min(minZ, _z[i]); maxZ = Math.Max(maxZ, _z[i]);
        }
        double size = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) / 2 + 1e-9;
        _root = new Node((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2, size);
        _root.Indices = Enumerable.Range(0, _x.Length).ToList();
        Subdivide(_root, 0);
    }

    public int Count => _x.Length;

    private void Subdivide(Node node, int depth)
    {
        if (node.Indices!.Count <= _leafSize || depth > 20) return;
        double h = node.Half / 2;
        node.Children = new Node[8];
        for (int o = 0; o < 8; o++)
            node.Children[o] = new Node(node.Cx + ((o & 1) != 0 ? h : -h), node.Cy + ((o & 2) != 0 ? h : -h), node.Cz + ((o & 4) != 0 ? h : -h), h) { Indices = [] };
        foreach (int i in node.Indices)
        {
            int o = (_x[i] >= node.Cx ? 1 : 0) | (_y[i] >= node.Cy ? 2 : 0) | (_z[i] >= node.Cz ? 4 : 0);
            node.Children[o].Indices!.Add(i);
        }
        node.Indices = null;
        foreach (var c in node.Children) Subdivide(c, depth + 1);
    }

    /// <summary>Indices of points inside the axis-aligned box.</summary>
    public List<int> QueryBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        var res = new List<int>();
        var stack = new Stack<Node>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Cx + n.Half < minX || n.Cx - n.Half > maxX || n.Cy + n.Half < minY || n.Cy - n.Half > maxY || n.Cz + n.Half < minZ || n.Cz - n.Half > maxZ) continue;
            if (n.Indices is not null)
            {
                foreach (int i in n.Indices)
                    if (_x[i] >= minX && _x[i] <= maxX && _y[i] >= minY && _y[i] <= maxY && _z[i] >= minZ && _z[i] <= maxZ) res.Add(i);
            }
            else foreach (var c in n.Children!) stack.Push(c);
        }
        return res;
    }

    /// <summary>Indices of points within a sphere.</summary>
    public List<int> QueryRadius(double x, double y, double z, double radius)
    {
        double r2 = radius * radius;
        return QueryBox(x - radius, y - radius, z - radius, x + radius, y + radius, z + radius)
            .Where(i => (_x[i] - x) * (_x[i] - x) + (_y[i] - y) * (_y[i] - y) + (_z[i] - z) * (_z[i] - z) <= r2)
            .ToList();
    }

    private sealed class Node(double cx, double cy, double cz, double half)
    {
        public double Cx = cx, Cy = cy, Cz = cz, Half = half;
        public List<int>? Indices;
        public Node[]? Children;
    }
}
