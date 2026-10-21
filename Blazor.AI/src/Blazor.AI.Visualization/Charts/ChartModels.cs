namespace Blazor.AI.Visualization;

/// <summary>A point in a chart series. <see cref="Label"/> is shown in tooltips.</summary>
public readonly record struct ChartPoint(double X, double Y, string? Label = null);

/// <summary>A named series of points.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<ChartPoint> Points)
{
    /// <summary>Optional CSS colour; defaults to the palette.</summary>
    public string? Color { get; init; }

    /// <summary>Dashed line (forecasts, targets).</summary>
    public bool Dashed { get; init; }

    /// <summary>Fill the area under the line.</summary>
    public bool Area { get; init; }

    public static ChartSeries FromValues(string name, IEnumerable<double> values) =>
        new(name, values.Select((v, i) => new ChartPoint(i, v)).ToArray());
}

/// <summary>A shaded band between two curves (confidence intervals).</summary>
public sealed record ChartBand(string Name, IReadOnlyList<double> X, IReadOnlyList<double> Lower, IReadOnlyList<double> Upper)
{
    public string? Color { get; init; }
}

/// <summary>A category value for bar charts.</summary>
public readonly record struct BarValue(string Category, double Value, string? Group = null);

/// <summary>A KPI tile in <c>AiDashboard</c>.</summary>
public sealed record DashboardMetric(string Label, double Value, string Format = "N0", double? Change = null, string? Icon = null)
{
    /// <summary>Optional mini trend rendered as a sparkline.</summary>
    public IReadOnlyList<double>? Trend { get; init; }

    /// <summary>Set when a lower value is better (costs, churn), so a decrease is shown in green.</summary>
    public bool LowerIsBetter { get; init; }
}

/// <summary>A node in a network or knowledge graph.</summary>
public sealed record GraphNode(string Id, string Label, string? Group = null, double Size = 1)
{
    public string? Description { get; init; }
}

/// <summary>A (directed) edge in a network or knowledge graph.</summary>
public sealed record GraphEdge(string Source, string Target, string? Label = null, double Weight = 1);

/// <summary>A flow between two nodes of a Sankey diagram.</summary>
public sealed record SankeyLink(string Source, string Target, double Value);

/// <summary>A treemap rectangle; children are laid out inside it.</summary>
public sealed record TreemapItem(string Label, double Value, string? Group = null)
{
    public IReadOnlyList<TreemapItem>? Children { get; init; }
}

/// <summary>A geographic data point.</summary>
public sealed record GeoPoint(double Latitude, double Longitude, string Label, double Value = 1, string? Group = null);
