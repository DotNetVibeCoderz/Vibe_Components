namespace Blazor.AI.Visualization;

/// <summary>Column-based Sankey layout: columns by longest path from a source, node height proportional to throughput.</summary>
public static class SankeyLayout
{
    public sealed record Node(string Id, int Column, double X, double Y, double Height, double Value);

    public sealed record Link(SankeyLink Source, double SourceY, double TargetY, double Thickness, Node From, Node To);

    public static (IReadOnlyList<Node> Nodes, IReadOnlyList<Link> Links) Compute(IReadOnlyList<SankeyLink> links, double width, double height, double nodeWidth = 14, double padding = 14)
    {
        var ids = links.SelectMany(l => new[] { l.Source, l.Target }).Distinct().ToList();
        if (ids.Count == 0)
        {
            return ([], []);
        }

        // Longest-path column assignment (cycles are broken by an iteration cap).
        var column = ids.ToDictionary(id => id, _ => 0);
        for (var pass = 0; pass < ids.Count; pass++)
        {
            var changed = false;
            foreach (var link in links)
            {
                if (column[link.Target] < column[link.Source] + 1)
                {
                    column[link.Target] = column[link.Source] + 1;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        var throughput = ids.ToDictionary(
            id => id,
            id => Math.Max(links.Where(l => l.Target == id).Sum(l => l.Value), links.Where(l => l.Source == id).Sum(l => l.Value)));

        var columns = column.Values.Max() + 1;
        var colWidth = columns == 1 ? 0 : (width - nodeWidth) / (columns - 1);
        var maxColumnTotal = Enumerable.Range(0, columns).Max(c => ids.Where(id => column[id] == c).Sum(id => throughput[id]));
        var maxCount = Enumerable.Range(0, columns).Max(c => ids.Count(id => column[id] == c));
        var scale = (height - padding * (maxCount - 1)) / Math.Max(1e-9, maxColumnTotal);

        var nodes = new Dictionary<string, Node>();
        for (var c = 0; c < columns; c++)
        {
            var inColumn = ids.Where(id => column[id] == c).OrderByDescending(id => throughput[id]).ToList();
            var total = inColumn.Sum(id => throughput[id]) * scale + padding * (inColumn.Count - 1);
            var y = (height - total) / 2;
            foreach (var id in inColumn)
            {
                var h = Math.Max(2, throughput[id] * scale);
                nodes[id] = new Node(id, c, c * colWidth, y, h, throughput[id]);
                y += h + padding;
            }
        }

        var outOffset = ids.ToDictionary(id => id, _ => 0.0);
        var inOffset = ids.ToDictionary(id => id, _ => 0.0);
        var result = new List<Link>(links.Count);
        foreach (var link in links.OrderBy(l => nodes[l.Source].Y).ThenBy(l => nodes[l.Target].Y))
        {
            var from = nodes[link.Source];
            var to = nodes[link.Target];
            var thickness = Math.Max(1, link.Value * scale);
            result.Add(new Link(link, from.Y + outOffset[link.Source] + thickness / 2, to.Y + inOffset[link.Target] + thickness / 2, thickness, from, to));
            outOffset[link.Source] += thickness;
            inOffset[link.Target] += thickness;
        }

        return (nodes.Values.ToList(), result);
    }
}
