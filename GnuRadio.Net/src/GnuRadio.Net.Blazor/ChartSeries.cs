namespace GnuRadio.Blazor;

/// <summary>One line of a <see cref="Components.LineChart"/>.</summary>
public sealed record ChartSeries(string Name, double[] X, double[] Y, bool Dashed = false, bool Points = false, string? Color = null);
