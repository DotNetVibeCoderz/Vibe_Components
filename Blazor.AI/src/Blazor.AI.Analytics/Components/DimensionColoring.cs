using System.Globalization;
using Blazor.AI.Visualization;

namespace Blazor.AI.Analytics;

/// <summary>Turns a 2-D embedding into scatter series, coloured by a categorical target when there is one.</summary>
internal static class DimensionColoring
{
    public static IReadOnlyList<ChartSeries> Series(AnalyticsDataset data, double[,] points)
    {
        var n = points.GetLength(0);
        if (data.Target is null || data.Task != MlTask.Classification || n != data.Frame.RowCount)
        {
            return [new ChartSeries("Rows", Enumerable.Range(0, n).Select(i => new ChartPoint(points[i, 0], points[i, 1])).ToArray())];
        }

        var labels = data.Frame[data.Target].ToStringArray();
        return labels.Distinct().Take(10)
            .Select((label, k) => new ChartSeries($"{data.Target} = {label}",
                    Enumerable.Range(0, n).Where(i => labels[i] == label).Select(i => new ChartPoint(points[i, 0], points[i, 1], $"row {(i + 1).ToString(CultureInfo.InvariantCulture)}")).ToArray())
                { Color = ChartMath.SeriesColor(k) })
            .ToArray();
    }
}
