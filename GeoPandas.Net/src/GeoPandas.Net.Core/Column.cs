using System.Collections;
using System.Globalization;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net;

/// <summary>
/// An immutable, strongly typed column (Arrow-style: contiguous value buffer + optional validity bitmap).
/// All transforming operations return new columns; value buffers may be shared between columns.
/// </summary>
public abstract class Column
{
    protected Column(string name) => Name = name;

    public string Name { get; }
    public abstract int Length { get; }
    /// <summary>CLR element type (double, long, int, bool, string, DateTime, Geometry…).</summary>
    public abstract Type DataType { get; }
    /// <summary>Boxed value at <paramref name="index"/> (null when missing).</summary>
    public abstract object? GetValue(int index);
    public abstract bool IsNull(int index);
    public int NullCount { get { int c = 0; for (int i = 0; i < Length; i++) if (IsNull(i)) c++; return c; } }

    /// <summary>Gathers rows by index; index -1 produces a null.</summary>
    public abstract Column Take(ReadOnlySpan<int> indices);
    public abstract Column Rename(string name);
    public Column Slice(int start, int length) => Take(Enumerable.Range(start, Math.Max(0, Math.Min(length, Length - start))).ToArray());

    /// <summary>Repeats values according to counts (used by explode).</summary>
    public Column Repeat(ReadOnlySpan<int> counts)
    {
        var idx = new List<int>();
        for (int i = 0; i < counts.Length; i++) for (int k = 0; k < counts[i]; k++) idx.Add(i);
        return Take(idx.ToArray());
    }

    public bool IsNumeric => DataType == typeof(double) || DataType == typeof(float) || DataType == typeof(long) || DataType == typeof(int);

    /// <summary>Returns the value as double (NaN when null / non-numeric).</summary>
    public virtual double GetDouble(int index) => GetValue(index) switch
    {
        double d => d, float f => f, long l => l, int i => i, bool b => b ? 1 : 0, _ => double.NaN
    };

    public virtual string Format(int index)
    {
        if (IsNull(index)) return "null";
        return GetValue(index) switch
        {
            double d => d.ToString("G6", CultureInfo.InvariantCulture),
            float f => f.ToString("G6", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            NetTopologySuite.Geometries.Geometry g => Abbreviate(g.AsText(), 48),
            IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
            var o => o?.ToString() ?? "null"
        };
    }

    protected static string Abbreviate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Boxed enumeration of all values.</summary>
    public IEnumerable<object?> AsObjects() { for (int i = 0; i < Length; i++) yield return GetValue(i); }

    public static Column<T> Create<T>(string name, T[] values) => new(name, values);
    public static Column<T> Create<T>(string name, IEnumerable<T> values) => new(name, values.ToArray());

    /// <summary>Builds a column from boxed values, inferring the best type (long → double → bool → DateTime → string).</summary>
    public static Column FromObjects(string name, IReadOnlyList<object?> values)
    {
        bool allLong = true, allNum = true, allBool = true, allDate = true, allGeom = true, any = false;
        foreach (var v in values)
        {
            if (v is null) continue;
            any = true;
            allLong &= v is long or int or short or byte;
            allNum &= v is double or float or long or int or short or byte or decimal;
            allBool &= v is bool;
            allDate &= v is DateTime;
            allGeom &= v is NetTopologySuite.Geometries.Geometry;
        }
        if (!any) return Column<string?>.FromNullable(name, values.Select(_ => (string?)null).ToArray());
        if (allGeom) return new GeoSeries(name, values.Select(v => v as NetTopologySuite.Geometries.Geometry).ToArray());
        if (allLong) return Column<long>.FromNullable(name, values.Select(v => v is null ? (long?)null : Convert.ToInt64(v, CultureInfo.InvariantCulture)).ToArray());
        if (allNum) return Column<double>.FromNullable(name, values.Select(v => v is null ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray());
        if (allBool) return Column<bool>.FromNullable(name, values.Select(v => (bool?)v).ToArray());
        if (allDate) return Column<DateTime>.FromNullable(name, values.Select(v => (DateTime?)v).ToArray());
        return new Column<string?>(name, values.Select(v => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray());
    }

    /// <summary>A column of nulls with the same type as <paramref name="like"/>.</summary>
    internal static Column NullsLike(Column like, int length) => like.Take(Enumerable.Repeat(-1, length).ToArray());

    public override string ToString() => $"Column '{Name}' <{DataType.Name}> [{Length}]";
}

/// <summary>Typed column. Value types track nulls in a bit-packed validity mask; reference types use null.</summary>
public class Column<T> : Column, IReadOnlyList<T>
{
    private static readonly bool s_isValueType = typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null;
    protected readonly T[] _values;
    private readonly ulong[]? _validity; // bit set = valid; null = all valid

    public Column(string name, T[] values, ulong[]? validity = null) : base(name)
    {
        _values = values;
        _validity = validity;
    }

    /// <summary>Creates a value-type column from nullable values.</summary>
    public static Column<T> FromNullable<TV>(string name, TV?[] values) where TV : struct
    {
        if (typeof(TV) != typeof(T)) throw new InvalidOperationException("Type mismatch.");
        var vals = new T[values.Length];
        ulong[]? mask = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is { } v) vals[i] = (T)(object)v;
            else
            {
                mask ??= AllValid(values.Length);
                mask[i >> 6] &= ~(1UL << (i & 63));
            }
        }
        return new Column<T>(name, vals, mask);
    }

    /// <summary>Creates a reference-type column (null entries are nulls).</summary>
    public static Column<T> FromNullable(string name, T[] values) => new(name, values);

    private static ulong[] AllValid(int n)
    {
        var m = new ulong[(n + 63) >> 6];
        Array.Fill(m, ulong.MaxValue);
        return m;
    }

    public override int Length => _values.Length;
    public int Count => _values.Length;
    public override Type DataType => typeof(T);
    /// <summary>Raw value buffer (do not mutate).</summary>
    public ReadOnlySpan<T> Values => _values;
    internal T[] RawValues => _values;

    public T this[int index] => _values[index];

    public override bool IsNull(int index) =>
        s_isValueType ? _validity is not null && (_validity[index >> 6] & (1UL << (index & 63))) == 0 : _values[index] is null;

    public override object? GetValue(int index) => IsNull(index) ? null : _values[index];

    public override double GetDouble(int index)
    {
        if (IsNull(index)) return double.NaN;
        return _values[index] switch { double d => d, long l => l, int i => i, float f => f, bool b => b ? 1 : 0, _ => double.NaN };
    }

    /// <summary>Factory hook so derived columns (GeoSeries) keep their type and metadata.</summary>
    protected virtual Column<T> CreateNew(string name, T[] values, ulong[]? validity) => new(name, values, validity);

    public override Column Take(ReadOnlySpan<int> indices)
    {
        var vals = new T[indices.Length];
        ulong[]? mask = null;
        for (int k = 0; k < indices.Length; k++)
        {
            int i = indices[k];
            if (i < 0 || IsNull(i))
            {
                if (s_isValueType)
                {
                    mask ??= AllValid(indices.Length);
                    mask[k >> 6] &= ~(1UL << (k & 63));
                }
                continue;
            }
            vals[k] = _values[i];
        }
        return CreateNew(Name, vals, mask);
    }

    public override Column Rename(string name) => CreateNew(name, _values, _validity);

    /// <summary>Element-wise projection to a new column.</summary>
    public Column<TOut> Map<TOut>(Func<T, TOut> selector, string? name = null)
    {
        var res = new TOut[Length];
        for (int i = 0; i < res.Length; i++) res[i] = selector(_values[i]);
        return new Column<TOut>(name ?? Name, res);
    }

    /// <summary>Boolean mask from a predicate (nulls → false).</summary>
    public bool[] Mask(Func<T, bool> predicate)
    {
        var m = new bool[Length];
        for (int i = 0; i < m.Length; i++) m[i] = !IsNull(i) && predicate(_values[i]);
        return m;
    }

    public IEnumerable<T> NonNull() { for (int i = 0; i < Length; i++) if (!IsNull(i)) yield return _values[i]; }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Numeric reductions for columns.</summary>
public static class ColumnStatistics
{
    private static IEnumerable<double> Valid(Column c)
    {
        for (int i = 0; i < c.Length; i++) { double v = c.GetDouble(i); if (!double.IsNaN(v)) yield return v; }
    }

    public static double Sum(this Column c) => Valid(c).Sum();
    public static double Mean(this Column c) { var v = Valid(c).ToArray(); return v.Length == 0 ? double.NaN : v.Average(); }
    public static double Min(this Column c) { var v = Valid(c).ToArray(); return v.Length == 0 ? double.NaN : v.Min(); }
    public static double Max(this Column c) { var v = Valid(c).ToArray(); return v.Length == 0 ? double.NaN : v.Max(); }
    public static int CountValid(this Column c) => c.Length - c.NullCount;

    /// <summary>Sample standard deviation (ddof = 1, pandas default).</summary>
    public static double Std(this Column c)
    {
        var v = Valid(c).ToArray();
        if (v.Length < 2) return double.NaN;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Length - 1));
    }

    public static double Median(this Column c) => Quantile(c, 0.5);

    /// <summary>Linear-interpolated quantile (pandas default).</summary>
    public static double Quantile(this Column c, double q)
    {
        var v = Valid(c).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        double pos = q * (v.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return v[lo] + (v[hi] - v[lo]) * (pos - lo);
    }

    /// <summary>All values as a dense double array (nulls → NaN).</summary>
    public static double[] ToDoubleArray(this Column c)
    {
        var r = new double[c.Length];
        for (int i = 0; i < r.Length; i++) r[i] = c.GetDouble(i);
        return r;
    }
}
