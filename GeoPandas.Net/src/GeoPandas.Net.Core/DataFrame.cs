using System.Globalization;
using System.Text;

namespace GeoPandas.Net;

public enum JoinHow { Inner, Left, Right, Outer }

/// <summary>
/// Immutable column-store table. Every operation returns a new frame; column buffers are shared when possible,
/// so projections (Select/Drop/Rename) are O(columns) and do not copy data.
/// </summary>
public class DataFrame
{
    protected readonly Column[] _columns;
    private readonly Dictionary<string, int> _byName;

    public DataFrame(IEnumerable<Column> columns)
    {
        _columns = columns.ToArray();
        _byName = new Dictionary<string, int>(StringComparer.Ordinal);
        int? len = null;
        for (int i = 0; i < _columns.Length; i++)
        {
            var c = _columns[i];
            if (!_byName.TryAdd(c.Name, i)) throw new ArgumentException($"Duplicate column name '{c.Name}'.");
            if (len is null) len = c.Length;
            else if (len != c.Length) throw new ArgumentException($"Column '{c.Name}' has length {c.Length}, expected {len}.");
        }
        RowCount = len ?? 0;
    }

    public DataFrame(params Column[] columns) : this((IEnumerable<Column>)columns) { }

    public int RowCount { get; }
    public int ColumnCount => _columns.Length;
    public IReadOnlyList<Column> Columns => _columns;
    public IReadOnlyList<string> ColumnNames => _columns.Select(c => c.Name).ToArray();
    /// <summary>(rows, columns), like pandas <c>shape</c>.</summary>
    public (int Rows, int Columns) Shape => (RowCount, ColumnCount);

    public bool HasColumn(string name) => _byName.ContainsKey(name);

    public Column this[string name] =>
        _byName.TryGetValue(name, out int i) ? _columns[i] : throw new KeyNotFoundException($"Column '{name}' not found. Available: {string.Join(", ", ColumnNames)}");

    /// <summary>Typed column access.</summary>
    public Column<T> Get<T>(string name) => this[name] as Column<T>
        ?? throw new InvalidCastException($"Column '{name}' is {this[name].DataType.Name}, not {typeof(T).Name}.");

    public DataRow Row(int index) => new(this, index);
    public IEnumerable<DataRow> Rows { get { for (int i = 0; i < RowCount; i++) yield return new DataRow(this, i); } }

    /// <summary>Creates a frame of the same kind (GeoDataFrame overrides to keep geometry/CRS).</summary>
    protected virtual DataFrame CreateLike(IReadOnlyList<Column> columns) => new DataFrame(columns);

    // ------------------------------------------------------------------ row selection

    public DataFrame Take(ReadOnlySpan<int> rows)
    {
        var arr = rows.ToArray();
        return CreateLike(_columns.Select(c => c.Take(arr)).ToArray());
    }

    public DataFrame Head(int n = 5) => Take(Enumerable.Range(0, Math.Min(n, RowCount)).ToArray());
    public DataFrame Tail(int n = 5) => Take(Enumerable.Range(Math.Max(0, RowCount - n), Math.Min(n, RowCount)).ToArray());

    public DataFrame Filter(bool[] mask)
    {
        if (mask.Length != RowCount) throw new ArgumentException("Mask length must equal the row count.");
        var idx = new List<int>(RowCount);
        for (int i = 0; i < mask.Length; i++) if (mask[i]) idx.Add(i);
        return Take(idx.ToArray());
    }

    public DataFrame Filter(Func<DataRow, bool> predicate)
    {
        var idx = new List<int>();
        for (int i = 0; i < RowCount; i++) if (predicate(new DataRow(this, i))) idx.Add(i);
        return Take(idx.ToArray());
    }

    /// <summary>Filters on a typed column, e.g. <c>df.Filter&lt;double&gt;("pop", v =&gt; v &gt; 1e6)</c>.</summary>
    public DataFrame Filter<T>(string column, Func<T, bool> predicate) => Filter(Get<T>(column).Mask(predicate));

    /// <summary>Random sample of rows without replacement.</summary>
    public DataFrame Sample(int n, int? seed = null)
    {
        var rng = seed is { } s ? new Random(s) : Random.Shared;
        var idx = Enumerable.Range(0, RowCount).ToArray();
        rng.Shuffle(idx);
        return Take(idx[..Math.Min(n, RowCount)]);
    }

    public DataFrame SortBy(string column, bool ascending = true) => SortBy([column], [ascending]);

    /// <summary>Stable multi-key sort (nulls last).</summary>
    public DataFrame SortBy(string[] columns, bool[]? ascending = null)
    {
        var cols = columns.Select(c => this[c]).ToArray();
        var asc = ascending ?? Enumerable.Repeat(true, cols.Length).ToArray();
        var idx = Enumerable.Range(0, RowCount).ToArray();
        var cmp = Comparer<int>.Create((a, b) =>
        {
            for (int k = 0; k < cols.Length; k++)
            {
                int r = CompareCells(cols[k], a, b);
                if (r != 0) return asc[k] ? r : -r;
            }
            return a.CompareTo(b);
        });
        Array.Sort(idx, cmp);
        return Take(idx);
    }

    internal static int CompareCells(Column c, int a, int b)
    {
        bool na = c.IsNull(a), nb = c.IsNull(b);
        if (na || nb) return na == nb ? 0 : na ? 1 : -1;
        var va = c.GetValue(a); var vb = c.GetValue(b);
        return va switch
        {
            IComparable ca when vb is not null && va.GetType() == vb.GetType() => ca.CompareTo(vb),
            _ => string.CompareOrdinal(va?.ToString(), vb?.ToString())
        };
    }

    // ------------------------------------------------------------------ column operations

    public DataFrame Select(params string[] columns) => CreateLike(columns.Select(c => this[c]).ToArray());

    public DataFrame Drop(params string[] columns)
    {
        var set = new HashSet<string>(columns);
        return CreateLike(_columns.Where(c => !set.Contains(c.Name)).ToArray());
    }

    /// <summary>Adds a column or replaces the column with the same name.</summary>
    public DataFrame WithColumn(Column column)
    {
        if (column.Length != RowCount && ColumnCount > 0) throw new ArgumentException($"Column length {column.Length} != {RowCount}.");
        var list = _columns.ToList();
        if (_byName.TryGetValue(column.Name, out int i)) list[i] = column; else list.Add(column);
        return CreateLike(list);
    }

    /// <summary>Adds a computed column from a row function.</summary>
    public DataFrame WithColumn<T>(string name, Func<DataRow, T> compute)
    {
        var vals = new T[RowCount];
        for (int i = 0; i < RowCount; i++) vals[i] = compute(new DataRow(this, i));
        return WithColumn(new Column<T>(name, vals));
    }

    public DataFrame Rename(string from, string to) =>
        CreateLike(_columns.Select(c => c.Name == from ? c.Rename(to) : c).ToArray());

    public DataFrame Rename(IReadOnlyDictionary<string, string> mapping) =>
        CreateLike(_columns.Select(c => mapping.TryGetValue(c.Name, out var n) ? c.Rename(n) : c).ToArray());

    // ------------------------------------------------------------------ grouping & joins

    public GroupBy GroupBy(params string[] keys) => new(this, keys);

    /// <summary>Distinct values of a column (in first-seen order).</summary>
    public IReadOnlyList<object?> Unique(string column)
    {
        var c = this[column];
        var seen = new HashSet<object?>();
        var res = new List<object?>();
        for (int i = 0; i < RowCount; i++) { var v = c.GetValue(i); if (seen.Add(v)) res.Add(v); }
        return res;
    }

    /// <summary>Value counts (descending).</summary>
    public DataFrame ValueCounts(string column)
    {
        var c = this[column];
        var counts = new Dictionary<string, (object? Key, long Count)>();
        for (int i = 0; i < RowCount; i++)
        {
            var v = c.GetValue(i);
            var k = v?.ToString() ?? "\0null";
            counts[k] = counts.TryGetValue(k, out var e) ? (e.Key, e.Count + 1) : (v, 1);
        }
        var ordered = counts.Values.OrderByDescending(x => x.Count).ToArray();
        return new DataFrame(
            Column.FromObjects(column, ordered.Select(o => o.Key).ToArray()),
            Column.Create("count", ordered.Select(o => o.Count).ToArray()));
    }

    /// <summary>Attribute join (pandas <c>merge</c>) on equal key columns.</summary>
    public DataFrame Merge(DataFrame right, string on, JoinHow how = JoinHow.Inner, string leftSuffix = "_x", string rightSuffix = "_y") =>
        Merge(right, on, on, how, leftSuffix, rightSuffix);

    public DataFrame Merge(DataFrame right, string leftOn, string rightOn, JoinHow how = JoinHow.Inner, string leftSuffix = "_x", string rightSuffix = "_y")
    {
        var lk = this[leftOn]; var rk = right[rightOn];
        var map = new Dictionary<string, List<int>>();
        for (int j = 0; j < right.RowCount; j++)
        {
            if (rk.IsNull(j)) continue;
            var key = KeyOf(rk, j);
            if (!map.TryGetValue(key, out var l)) map[key] = l = [];
            l.Add(j);
        }
        var li = new List<int>(); var ri = new List<int>();
        var matchedRight = new bool[right.RowCount];
        for (int i = 0; i < RowCount; i++)
        {
            if (!lk.IsNull(i) && map.TryGetValue(KeyOf(lk, i), out var js))
                foreach (var j in js) { li.Add(i); ri.Add(j); matchedRight[j] = true; }
            else if (how is JoinHow.Left or JoinHow.Outer) { li.Add(i); ri.Add(-1); }
        }
        if (how is JoinHow.Right or JoinHow.Outer)
        {
            for (int j = 0; j < right.RowCount; j++) if (!matchedRight[j]) { li.Add(-1); ri.Add(j); }
        }
        if (how == JoinHow.Right)
        {
            // Right join keeps only rows that have a right side.
            var keep = Enumerable.Range(0, li.Count).Where(k => ri[k] >= 0).ToArray();
            li = keep.Select(k => li[k]).ToList(); ri = keep.Select(k => ri[k]).ToList();
        }
        return CombineColumns(this, right, li.ToArray(), ri.ToArray(), leftSuffix, rightSuffix,
            leftOn == rightOn ? rightOn : null, keyFallbackFromRight: how is JoinHow.Right or JoinHow.Outer ? leftOn : null);
    }

    internal static string KeyOf(Column c, int i) => c.GetValue(i) switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long l => ((double)l).ToString("R", CultureInfo.InvariantCulture),
        int n => ((double)n).ToString("R", CultureInfo.InvariantCulture),
        var o => o?.ToString() ?? ""
    };

    /// <summary>Gathers left and right columns into a combined frame, resolving name collisions with suffixes.</summary>
    internal DataFrame CombineColumns(DataFrame left, DataFrame right, int[] li, int[] ri, string leftSuffix, string rightSuffix,
        string? dropRightKey, string? keyFallbackFromRight = null)
    {
        var leftNames = new HashSet<string>(left.ColumnNames);
        var rightNames = new HashSet<string>(right.ColumnNames.Where(n => n != dropRightKey));
        var cols = new List<Column>();
        foreach (var c in left.Columns)
        {
            var taken = c.Take(li);
            if (keyFallbackFromRight == c.Name && dropRightKey == c.Name)
            {
                // fill the shared key from the right side where the left is missing
                var rc = right[c.Name].Take(ri);
                var merged = new object?[li.Length];
                for (int k = 0; k < li.Length; k++) merged[k] = li[k] >= 0 ? taken.GetValue(k) : rc.GetValue(k);
                taken = Column.FromObjects(c.Name, merged);
            }
            cols.Add(rightNames.Contains(c.Name) ? taken.Rename(c.Name + leftSuffix) : taken);
        }
        foreach (var c in right.Columns)
        {
            if (c.Name == dropRightKey) continue;
            var taken = c.Take(ri);
            cols.Add(leftNames.Contains(c.Name) ? taken.Rename(c.Name + rightSuffix) : taken);
        }
        return left.CreateLike(cols);
    }

    /// <summary>Vertically concatenates frames with identical column names (missing columns become null).</summary>
    public static DataFrame Concat(params DataFrame[] frames)
    {
        if (frames.Length == 0) return new DataFrame();
        var names = frames.SelectMany(f => f.ColumnNames).Distinct().ToArray();
        var cols = new List<Column>();
        foreach (var n in names)
        {
            var template = frames.First(f => f.HasColumn(n))[n];
            var values = new List<object?>();
            foreach (var f in frames)
            {
                if (f.HasColumn(n)) { var c = f[n]; for (int i = 0; i < f.RowCount; i++) values.Add(c.GetValue(i)); }
                else for (int i = 0; i < f.RowCount; i++) values.Add(null);
            }
            cols.Add(template is GeoSeries gs ? new GeoSeries(n, values.Cast<NetTopologySuite.Geometries.Geometry?>().ToArray(), gs.Crs) : Column.FromObjects(n, values));
        }
        return frames[0].CreateLike(cols);
    }

    // ------------------------------------------------------------------ summaries

    /// <summary>count / mean / std / min / 25% / 50% / 75% / max for numeric columns.</summary>
    public DataFrame Describe()
    {
        var numeric = _columns.Where(c => c.IsNumeric).ToArray();
        var stats = new[] { "count", "mean", "std", "min", "25%", "50%", "75%", "max" };
        var cols = new List<Column> { Column.Create("stat", stats) };
        foreach (var c in numeric)
            cols.Add(Column.Create(c.Name, new[] { c.CountValid(), c.Mean(), c.Std(), c.Min(), c.Quantile(0.25), c.Median(), c.Quantile(0.75), c.Max() }));
        return new DataFrame(cols);
    }

    /// <summary>Column name, type and null count summary (pandas <c>info</c>).</summary>
    public string Info()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<{GetType().Name}> {RowCount} rows × {ColumnCount} columns");
        foreach (var c in _columns)
            sb.AppendLine($"  {c.Name,-24} {TypeName(c),-12} non-null: {c.Length - c.NullCount}");
        return sb.ToString();
    }

    private static string TypeName(Column c) => c is GeoSeries ? "geometry" : c.DataType.Name.ToLowerInvariant() switch
    {
        "double" => "float64", "int64" => "int64", "int32" => "int32", "boolean" => "bool", "string" => "string", var n => n
    };

    public string ToString(int maxRows)
    {
        var sb = new StringBuilder();
        int rows = Math.Min(maxRows, RowCount);
        var headers = new[] { "" }.Concat(ColumnNames).ToArray();
        var cells = new string[rows][];
        for (int r = 0; r < rows; r++)
            cells[r] = new[] { r.ToString(CultureInfo.InvariantCulture) }.Concat(_columns.Select(c => c.Format(r))).ToArray();
        var widths = headers.Select((h, k) => Math.Max(h.Length, rows == 0 ? 0 : cells.Max(row => row[k].Length))).ToArray();
        sb.AppendLine(string.Join("  ", headers.Select((h, k) => h.PadLeft(widths[k]))));
        foreach (var row in cells) sb.AppendLine(string.Join("  ", row.Select((v, k) => v.PadLeft(widths[k]))));
        if (RowCount > rows) sb.AppendLine($"... ({RowCount} rows × {ColumnCount} columns)");
        return sb.ToString();
    }

    public override string ToString() => ToString(10);
}

/// <summary>Lightweight row view (no allocation per row).</summary>
public readonly struct DataRow(DataFrame frame, int index)
{
    public int Index => index;
    public DataFrame Frame => frame;
    public object? this[string column] => frame[column].GetValue(index);
    public T Get<T>(string column) => frame[column] is Column<T> c ? c[index] : (T)Convert.ChangeType(frame[column].GetValue(index)!, typeof(T), CultureInfo.InvariantCulture);
    public double GetDouble(string column) => frame[column].GetDouble(index);
    public string? GetString(string column) => frame[column].GetValue(index)?.ToString();
    public bool IsNull(string column) => frame[column].IsNull(index);
}
