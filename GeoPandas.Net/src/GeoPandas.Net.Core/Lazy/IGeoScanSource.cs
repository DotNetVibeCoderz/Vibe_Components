using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Lazy;

/// <summary>What a scan source can evaluate itself (instead of the lazy engine filtering afterwards).</summary>
[Flags]
public enum ScanPushdown
{
    None = 0,
    /// <summary>The source can skip rows whose geometry envelope does not intersect a bounding box (index, row-group stats, SQL).</summary>
    Bbox = 1,
    /// <summary>The source can skip reading attribute columns that are not needed.</summary>
    Columns = 2,
    /// <summary>The source can stop after N rows.</summary>
    Limit = 4,
    /// <summary>The source can evaluate simple attribute comparisons (<see cref="AttributePredicate"/>).</summary>
    Attributes = 8,
    /// <summary>The bbox filter is exact (envelope intersection), not just a coarse pre-filter, so a limit may be pushed after it.</summary>
    ExactBbox = 16,
    /// <summary>Attribute predicates are evaluated exactly, so a limit may be pushed after them.</summary>
    ExactAttributes = 32,
}

public enum CompareOp { Eq, Ne, Lt, Le, Gt, Ge }

/// <summary>A pushdown-friendly attribute comparison: <c>column op value</c> (nulls never match).</summary>
public sealed record AttributePredicate(string Column, CompareOp Op, object Value)
{
    public string Symbol => Op switch { CompareOp.Eq => "=", CompareOp.Ne => "<>", CompareOp.Lt => "<", CompareOp.Le => "<=", CompareOp.Gt => ">", _ => ">=" };

    /// <summary>Evaluates the comparison against a boxed cell value.</summary>
    public bool Matches(object? cell)
    {
        if (cell is null) return false;
        int c;
        if (IsNumber(cell) && IsNumber(Value)) c = Convert.ToDouble(cell, System.Globalization.CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(Value, System.Globalization.CultureInfo.InvariantCulture));
        else if (cell is DateTime a && Value is DateTime b) c = a.CompareTo(b);
        else if (cell is bool x && Value is bool y) c = x.CompareTo(y);
        else c = string.CompareOrdinal(Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture));
        return Op switch { CompareOp.Eq => c == 0, CompareOp.Ne => c != 0, CompareOp.Lt => c < 0, CompareOp.Le => c <= 0, CompareOp.Gt => c > 0, _ => c >= 0 };
    }

    internal static bool IsNumber(object v) => v is double or float or long or int or short or byte or decimal or uint or ulong or ushort or sbyte;

    public override string ToString() => $"{Column} {Symbol} {(Value is string s ? $"'{s}'" : Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture))}";
}

/// <summary>What the lazy engine asks a source to read. Everything is optional; sources ignore what they cannot push down.</summary>
public sealed record ScanRequest
{
    /// <summary>Only rows whose geometry envelope intersects this box (source CRS).</summary>
    public Envelope? Bbox { get; init; }
    /// <summary>Attribute columns to read (null = all). The geometry column is always read.</summary>
    public IReadOnlyList<string>? Columns { get; init; }
    public long? Limit { get; init; }
    public IReadOnlyList<AttributePredicate> Predicates { get; init; } = [];
    /// <summary>Preferred rows per batch.</summary>
    public int BatchSize { get; init; } = 65_536;
}

/// <summary>
/// A dataset that can be scanned in batches: files (FlatGeobuf, GeoParquet, GeoPackage), databases, in-memory frames.
/// The lazy engine (<see cref="LazyGeoDataFrame"/>) re-checks every filter, so a source may return a superset of the requested rows.
/// </summary>
public interface IGeoScanSource
{
    /// <summary>Human-readable name used by <see cref="LazyGeoDataFrame.Explain"/>.</summary>
    string Name { get; }
    CoordinateReferenceSystem? Crs { get; }
    /// <summary>Attribute column names (excluding the geometry column).</summary>
    IReadOnlyList<string> ColumnNames { get; }
    /// <summary>Total rows when cheaply known (file header, metadata).</summary>
    long? RowCountHint { get; }
    ScanPushdown Pushdown { get; }
    IEnumerable<GeoDataFrame> Scan(ScanRequest request);
}

/// <summary>Scan source over an in-memory frame (bbox via the STR-tree index).</summary>
public sealed class InMemoryScanSource(GeoDataFrame frame, string? name = null) : IGeoScanSource
{
    public string Name { get; } = name ?? $"memory[{frame.RowCount} rows]";
    public CoordinateReferenceSystem? Crs => frame.Crs;
    public IReadOnlyList<string> ColumnNames { get; } = frame.ColumnNames.Where(n => n != frame.GeometryColumnName).ToArray();
    public long? RowCountHint => frame.RowCount;
    public ScanPushdown Pushdown => ScanPushdown.Bbox | ScanPushdown.ExactBbox | ScanPushdown.Columns | ScanPushdown.Limit;

    public IEnumerable<GeoDataFrame> Scan(ScanRequest request)
    {
        int[] rows = request.Bbox is { } box
            ? frame.SIndex.Query(box).Where(i => frame.Geometry[i] is { } g && g.EnvelopeInternal.Intersects(box)).Order().ToArray()
            : Enumerable.Range(0, frame.RowCount).ToArray();
        if (request.Limit is { } lim && lim < rows.Length) rows = rows[..(int)lim];
        var src = request.Columns is { } cols ? frame.Select(cols.ToArray()) : frame;
        for (int start = 0; start < rows.Length; start += request.BatchSize)
            yield return src.Take(rows.AsSpan(start, Math.Min(request.BatchSize, rows.Length - start)));
    }
}
