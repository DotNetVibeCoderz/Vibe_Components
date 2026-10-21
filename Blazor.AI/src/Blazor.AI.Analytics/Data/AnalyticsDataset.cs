using Gravicode.Science.GraviFrame;
using Gravicode.Science.GraviNum;

namespace Blazor.AI.Analytics;

public enum MlTask
{
    Classification,
    Regression,
    Clustering,
    TimeSeries,
}

/// <summary>A GraviFrame <see cref="DataFrame"/> plus what it is for (target column, task).</summary>
public sealed record AnalyticsDataset(string Name, DataFrame Frame, string? Target = null, MlTask Task = MlTask.Classification, string? Description = null)
{
    public IReadOnlyList<string> NumericColumns => Frame.NumericColumns.Select(c => c.Name).ToArray();

    /// <summary>Numeric feature columns (everything numeric except the target).</summary>
    public IReadOnlyList<string> FeatureColumns => NumericColumns.Where(c => c != Target).ToArray();

    /// <summary>Feature matrix (rows with missing values in the chosen columns are filled with the column mean).</summary>
    public NdArray Features(IReadOnlyList<string>? columns = null)
    {
        var names = (columns ?? FeatureColumns).ToArray();
        return Frame.SelectColumns(names).FillMissingWithMean().ToNdArray();
    }

    /// <summary>Target vector. Text targets are label-encoded in order of first appearance.</summary>
    public (NdArray Values, IReadOnlyList<string> Labels) TargetVector()
    {
        if (Target is null)
        {
            throw new InvalidOperationException($"Dataset '{Name}' has no target column.");
        }

        var series = Frame[Target];
        if (series is NumericSeries numeric)
        {
            var values = numeric.FillMissingWithMean().Values;
            var labels = Task == MlTask.Classification
                ? values.Distinct().Order().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()
                : [];
            return (NdArray.FromValues(values), labels);
        }

        var text = series.ToStringArray();
        var distinct = text.Distinct().ToList();
        return (NdArray.FromValues(text.Select(t => (double)distinct.IndexOf(t)).ToArray()), distinct);
    }
}

/// <summary>The dataset the analytics components are working on, shared across the page (scoped per user).</summary>
public sealed class AnalyticsWorkspace
{
    private AnalyticsDataset _dataset = SampleDatasets.CustomerChurn();

    public AnalyticsDataset Dataset
    {
        get => _dataset;
        set
        {
            _dataset = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}
