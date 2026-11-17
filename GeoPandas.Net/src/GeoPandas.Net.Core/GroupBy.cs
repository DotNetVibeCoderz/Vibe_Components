namespace GeoPandas.Net;

/// <summary>Aggregation functions for <see cref="GroupBy"/> and dissolve.</summary>
public enum AggFunc { Sum, Mean, Min, Max, Count, First, Last, Std, Median, NUnique }

/// <summary>Group-by over one or more key columns (pandas <c>groupby</c>).</summary>
public sealed class GroupBy
{
    private readonly DataFrame _frame;
    private readonly string[] _keys;
    private readonly List<(object?[] Key, List<int> Rows)> _groups;

    internal GroupBy(DataFrame frame, string[] keys)
    {
        _frame = frame;
        _keys = keys;
        var keyCols = keys.Select(k => frame[k]).ToArray();
        var lookup = new Dictionary<string, int>();
        _groups = [];
        for (int i = 0; i < frame.RowCount; i++)
        {
            string composite = string.Join("\u001f", keyCols.Select(c => DataFrame.KeyOf(c, i)));
            if (!lookup.TryGetValue(composite, out int g))
            {
                g = _groups.Count;
                lookup[composite] = g;
                _groups.Add((keyCols.Select(c => c.GetValue(i)).ToArray(), []));
            }
            _groups[g].Rows.Add(i);
        }
    }

    public int Count => _groups.Count;
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Groups as (key values, row indices) in first-seen order.</summary>
    public IEnumerable<(object?[] Key, IReadOnlyList<int> Rows)> Groups => _groups.Select(g => (g.Key, (IReadOnlyList<int>)g.Rows));

    /// <summary>Sub-frame of each group.</summary>
    public IEnumerable<(object?[] Key, DataFrame Frame)> Frames => _groups.Select(g => (g.Key, _frame.Take(g.Rows.ToArray())));

    /// <summary>Aggregates columns: <c>Agg(("pop", AggFunc.Sum), ("area", AggFunc.Mean))</c>.</summary>
    public DataFrame Agg(params (string Column, AggFunc Func)[] specs) =>
        Agg(specs.Select(s => (s.Column, s.Func, $"{s.Column}")).ToArray());

    /// <summary>Aggregates with explicit output names.</summary>
    public DataFrame Agg(params (string Column, AggFunc Func, string As)[] specs)
    {
        var cols = new List<Column>();
        for (int k = 0; k < _keys.Length; k++)
            cols.Add(Column.FromObjects(_keys[k], _groups.Select(g => g.Key[k]).ToArray()));
        foreach (var (col, func, name) in specs)
        {
            var src = _frame[col];
            cols.Add(Aggregate(src, func, name, _groups.Select(g => (IReadOnlyList<int>)g.Rows)));
        }
        return new DataFrame(cols);
    }

    /// <summary>Row count per group.</summary>
    public DataFrame Size()
    {
        var cols = new List<Column>();
        for (int k = 0; k < _keys.Length; k++)
            cols.Add(Column.FromObjects(_keys[k], _groups.Select(g => g.Key[k]).ToArray()));
        cols.Add(Column.Create("size", _groups.Select(g => (long)g.Rows.Count).ToArray()));
        return new DataFrame(cols);
    }

    internal static Column Aggregate(Column src, AggFunc func, string name, IEnumerable<IReadOnlyList<int>> groups)
    {
        var gl = groups.ToList();
        switch (func)
        {
            case AggFunc.Count:
                return Column.Create(name, gl.Select(rows => (long)rows.Count(r => !src.IsNull(r))).ToArray());
            case AggFunc.NUnique:
                return Column.Create(name, gl.Select(rows => (long)rows.Select(r => src.GetValue(r)).Where(v => v is not null).Distinct().Count()).ToArray());
            case AggFunc.First:
                return Column.FromObjects(name, gl.Select(rows => rows.Select(r => src.GetValue(r)).FirstOrDefault(v => v is not null)).ToArray());
            case AggFunc.Last:
                return Column.FromObjects(name, gl.Select(rows => rows.Select(r => src.GetValue(r)).LastOrDefault(v => v is not null)).ToArray());
        }
        if (!src.IsNumeric) throw new InvalidOperationException($"Aggregation {func} requires a numeric column; '{src.Name}' is {src.DataType.Name}.");
        var res = new double[gl.Count];
        for (int g = 0; g < gl.Count; g++)
        {
            var v = gl[g].Select(src.GetDouble).Where(x => !double.IsNaN(x)).ToArray();
            res[g] = v.Length == 0 ? double.NaN : func switch
            {
                AggFunc.Sum => v.Sum(),
                AggFunc.Mean => v.Average(),
                AggFunc.Min => v.Min(),
                AggFunc.Max => v.Max(),
                AggFunc.Std => v.Length < 2 ? double.NaN : Math.Sqrt(v.Sum(x => (x - v.Average()) * (x - v.Average())) / (v.Length - 1)),
                AggFunc.Median => Median(v),
                _ => double.NaN
            };
        }
        return Column.Create(name, res);
    }

    private static double Median(double[] v)
    {
        Array.Sort(v);
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    }
}
