namespace Blazor.AI.Visualization;

/// <summary>Squarified treemap (Bruls, Huizing, van Wijk): rows are grown while they keep aspect ratios close to 1.</summary>
public static class TreemapLayout
{
    public readonly record struct Cell(TreemapItem Item, double X, double Y, double Width, double Height, int Depth);

    public static List<Cell> Compute(IReadOnlyList<TreemapItem> items, double width, double height, int maxDepth = 2)
    {
        var cells = new List<Cell>();
        Layout(items, 0, 0, width, height, 0, maxDepth, cells);
        return cells;
    }

    private static void Layout(IReadOnlyList<TreemapItem> items, double x, double y, double w, double h, int depth, int maxDepth, List<Cell> cells)
    {
        var sorted = items.Where(i => i.Value > 0).OrderByDescending(i => i.Value).ToList();
        var total = sorted.Sum(i => i.Value);
        if (sorted.Count == 0 || total <= 0 || w <= 0 || h <= 0)
        {
            return;
        }

        var scale = w * h / total;
        var areas = sorted.Select(i => i.Value * scale).ToList();
        var row = new List<int>();
        var index = 0;

        while (index < sorted.Count)
        {
            var side = Math.Min(w, h);
            row.Add(index);
            if (row.Count > 1 && Worst(row, areas, side) > Worst(row.SkipLast(1).ToList(), areas, side))
            {
                row.RemoveAt(row.Count - 1);
                (x, y, w, h) = Place(row, areas, sorted, x, y, w, h, depth, maxDepth, cells);
                row.Clear();
                continue;
            }

            index++;
        }

        if (row.Count > 0)
        {
            Place(row, areas, sorted, x, y, w, h, depth, maxDepth, cells);
        }
    }

    private static (double X, double Y, double W, double H) Place(List<int> row, List<double> areas, List<TreemapItem> items, double x, double y, double w, double h, int depth, int maxDepth, List<Cell> cells)
    {
        var sum = row.Sum(i => areas[i]);
        if (w >= h)
        {
            var columnWidth = sum / h;
            var cy = y;
            foreach (var i in row)
            {
                var cellHeight = areas[i] / columnWidth;
                Add(items[i], x, cy, columnWidth, cellHeight, depth, maxDepth, cells);
                cy += cellHeight;
            }

            return (x + columnWidth, y, w - columnWidth, h);
        }

        var rowHeight = sum / w;
        var cx = x;
        foreach (var i in row)
        {
            var cellWidth = areas[i] / rowHeight;
            Add(items[i], cx, y, cellWidth, rowHeight, depth, maxDepth, cells);
            cx += cellWidth;
        }

        return (x, y + rowHeight, w, h - rowHeight);
    }

    private static void Add(TreemapItem item, double x, double y, double w, double h, int depth, int maxDepth, List<Cell> cells)
    {
        cells.Add(new Cell(item, x, y, w, h, depth));
        if (item.Children is { Count: > 0 } children && depth + 1 < maxDepth)
        {
            const double inset = 3, header = 18;
            Layout(children, x + inset, y + header, w - 2 * inset, h - header - inset, depth + 1, maxDepth, cells);
        }
    }

    private static double Worst(List<int> row, List<double> areas, double side)
    {
        if (row.Count == 0)
        {
            return double.MaxValue;
        }

        var sum = row.Sum(i => areas[i]);
        var max = row.Max(i => areas[i]);
        var min = row.Min(i => areas[i]);
        var s2 = side * side;
        return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * min));
    }
}
