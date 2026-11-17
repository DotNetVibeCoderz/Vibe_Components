using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Lazy;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Sql;

/// <summary>What <see cref="SpatialSql.ToSql"/> does when the table exists.</summary>
public enum IfExists { Fail, Replace, Append }

/// <summary>
/// GeoPandas <c>read_postgis</c> / <c>to_postgis</c> for any spatial database reachable through ADO.NET.
/// Geometry crosses the wire as WKB, so no provider-specific geometry types (SqlGeometry, NpgsqlGeometry, SdoGeometry) are needed.
/// </summary>
/// <example>
/// <code>
/// await using var con = new NpgsqlConnection(cs); con.Open();
/// var parcels = SpatialSql.ReadSql(con, "SELECT id, owner, geom FROM parcels WHERE kecamatan = 'Menteng'");
/// districts.ToSql(con, "districts", IfExists.Replace);
/// </code>
/// </example>
public static class SpatialSql
{
    private const string WkbAlias = "__gpd_wkb";
    private const string SridAlias = "__gpd_srid";

    /// <summary>
    /// Runs a query and returns a GeoDataFrame. <paramref name="geometryColumn"/> names the geometry column in the query result.
    /// The query is wrapped as a subquery so that the geometry is converted to WKB server-side.
    /// </summary>
    public static GeoDataFrame ReadSql(DbConnection connection, string sql, string geometryColumn = "geom",
        CoordinateReferenceSystem? crs = null, SpatialDialect? dialect = null, IReadOnlyList<object?>? parameters = null)
    {
        dialect ??= SpatialDialect.Detect(connection);
        EnsureOpen(connection, dialect);
        var names = ColumnNames(connection, $"SELECT * FROM ({sql}) gpd_q WHERE 1 = 0", parameters, dialect);
        var geomName = names.FirstOrDefault(n => n.Equals(geometryColumn, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Geometry column '{geometryColumn}' is not in the query result ({string.Join(", ", names)}).");
        var attrs = names.Where(n => n != geomName).ToArray();
        var g = "gpd_q." + dialect.Quote(geomName);
        var select = new StringBuilder("SELECT ");
        foreach (var a in attrs) select.Append("gpd_q.").Append(dialect.Quote(a)).Append(", ");
        select.Append(dialect.ToWkb(g)).Append(' ').Append(WkbAlias);
        if (dialect.Srid(g) is { } srid) select.Append(", ").Append(srid).Append(' ').Append(SridAlias);
        select.Append($" FROM ({sql}) gpd_q");
        return Execute(connection, select.ToString(), attrs, crs, geomName, parameters, dialect, null).Single();
    }

    /// <summary>Reads a table with optional column selection, bbox (envelope) filter, SQL WHERE and limit.</summary>
    public static GeoDataFrame ReadTable(DbConnection connection, string table, string? geometryColumn = null, IReadOnlyList<string>? columns = null,
        Envelope? bbox = null, string? where = null, long? limit = null, CoordinateReferenceSystem? crs = null, SpatialDialect? dialect = null)
    {
        var src = new SqlScanSource(connection, table, geometryColumn, crs, dialect);
        var parts = src.Scan(new ScanRequest { Bbox = bbox, Columns = columns, Limit = limit, BatchSize = int.MaxValue }, where).ToArray();
        return parts.Length == 1 ? parts[0] : parts.Length == 0 ? src.Empty(columns) : GeoDataFrame.Concat(parts);
    }

    /// <summary>Lazy source over a table: bbox, column, attribute and limit pushdown into SQL.</summary>
    public static IGeoScanSource ScanSource(DbConnection connection, string table, string? geometryColumn = null, CoordinateReferenceSystem? crs = null, SpatialDialect? dialect = null) =>
        new SqlScanSource(connection, table, geometryColumn, crs, dialect);

    /// <summary>Starts a lazy query over a database table (<c>LazyGeoDataFrame</c>).</summary>
    public static LazyGeoDataFrame ScanTable(DbConnection connection, string table, string? geometryColumn = null, SpatialDialect? dialect = null) =>
        LazyGeoDataFrame.From(new SqlScanSource(connection, table, geometryColumn, null, dialect));

    /// <summary>Tables and their geometry columns.</summary>
    public static IReadOnlyList<(string Table, string Column)> ListGeometryColumns(DbConnection connection, SpatialDialect? dialect = null)
    {
        dialect ??= SpatialDialect.Detect(connection);
        EnsureOpen(connection, dialect);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.GeometryColumnsQuery;
        using var r = cmd.ExecuteReader();
        var res = new List<(string, string)>();
        while (r.Read()) res.Add((r.GetString(0), r.GetString(1)));
        return res;
    }

    // ================================================================== writing

    /// <summary>
    /// Writes a GeoDataFrame to a table (GeoPandas <c>to_postgis</c>): creates it with typed columns and a geometry column,
    /// inserts in one transaction with a prepared command, then builds a spatial index.
    /// </summary>
    public static void ToSql(this GeoDataFrame gdf, DbConnection connection, string table, IfExists ifExists = IfExists.Fail,
        string geometryColumn = "geom", bool spatialIndex = true, int? srid = null, SpatialDialect? dialect = null)
    {
        dialect ??= SpatialDialect.Detect(connection);
        EnsureOpen(connection, dialect);
        int s = srid ?? gdf.Crs?.Epsg ?? 0;
        bool exists = TableExists(connection, table, dialect);
        if (exists && ifExists == IfExists.Fail) throw new InvalidOperationException($"Table '{table}' already exists (use IfExists.Replace or IfExists.Append).");
        var attrs = gdf.Columns.Where(c => c.Name != gdf.GeometryColumnName && c is not GeoSeries).ToArray();
        if (exists && ifExists == IfExists.Replace) { Exec(connection, null, dialect.DropTableIfExists(table)); exists = false; }
        if (!exists)
        {
            var defs = new List<string>();
            if (dialect.IdentityColumn is { } id && spatialIndex) defs.Add(id);
            defs.AddRange(attrs.Select(c => $"{dialect.Quote(c.Name)} {dialect.TypeName(c.DataType)}"));
            defs.Add($"{dialect.Quote(geometryColumn)} {dialect.GeometryType(s, GeometryTypeName(gdf.Geometry))}");
            Exec(connection, null, $"CREATE TABLE {dialect.Quote(table)} ({string.Join(", ", defs)})");
        }

        using (var tx = connection.BeginTransaction())
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            var cols = attrs.Select(a => dialect.Quote(a.Name)).Append(dialect.Quote(geometryColumn));
            var vals = attrs.Select((_, i) => dialect.Placeholder(i)).Append(dialect.FromWkb(dialect.Placeholder(attrs.Length), s));
            cmd.CommandText = $"INSERT INTO {dialect.Quote(table)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)})";
            var ps = new DbParameter[attrs.Length + 1];
            for (int i = 0; i < ps.Length; i++)
            {
                ps[i] = cmd.CreateParameter();
                ps[i].ParameterName = dialect.ParameterName(i);
                // DateTime is left to the provider (Npgsql picks timestamptz for UTC values; forcing DateTime2 would map to timestamp)
                if (i == attrs.Length) ps[i].DbType = DbType.Binary;
                else if (DbTypeOf(attrs[i].DataType, dialect) is { } dbt) ps[i].DbType = dbt;
                cmd.Parameters.Add(ps[i]);
            }
            for (int r = 0; r < gdf.RowCount; r++)
            {
                for (int i = 0; i < attrs.Length; i++)
                    ps[i].Value = attrs[i].GetValue(r) switch
                    {
                        null => DBNull.Value,
                        double d when double.IsNaN(d) => DBNull.Value,
                        int n => (long)n,
                        bool b when dialect is OracleDialect => b ? 1 : 0,
                        DateTime dt => dialect is PostGisDialect ? (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : dt,
                        var v => v
                    };
                var g = gdf.Geometry[r];
                ps[attrs.Length].Value = g is null ? DBNull.Value : Geo.ToWkb(g);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (spatialIndex && (!exists || ifExists != IfExists.Append))
            foreach (var stmt in dialect.CreateSpatialIndex(table, geometryColumn, gdf.TotalBounds, s))
                Exec(connection, null, stmt);
    }

    private static DbType? DbTypeOf(Type t, SpatialDialect d) =>
        t == typeof(long) || t == typeof(int) ? DbType.Int64 : t == typeof(double) || t == typeof(float) ? DbType.Double
        : t == typeof(bool) ? (d is OracleDialect ? DbType.Int32 : DbType.Boolean)
        : t == typeof(DateTime) ? null : t == typeof(byte[]) ? DbType.Binary : DbType.String;

    private static string GeometryTypeName(GeoSeries s)
    {
        var types = s.NonNull().Select(g => g!.GeometryType).Distinct().ToArray();
        return types.Length == 1 ? types[0] : "Geometry";
    }

    // ================================================================== plumbing

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbConnection, object> s_prepared = new();

    internal static void EnsureOpen(DbConnection c, SpatialDialect dialect)
    {
        if (c.State != ConnectionState.Open) c.Open();
        if (s_prepared.TryGetValue(c, out _)) return;
        foreach (var stmt in dialect.Prepare())
        {
            try { Exec(c, null, stmt); }
            catch (DbException) when (stmt.StartsWith("INSTALL", StringComparison.OrdinalIgnoreCase)) { /* offline: LOAD may still succeed from cache */ }
        }
        s_prepared.AddOrUpdate(c, new object());
    }

    internal static void Exec(DbConnection c, DbTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(DbConnection c, string table, SpatialDialect d)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = d.Limit($"SELECT * FROM {d.Quote(table)} WHERE 1 = 0", 1);
            using var r = cmd.ExecuteReader();
            return true;
        }
        catch (DbException) { return false; }
    }

    internal static string[] ColumnNames(DbConnection c, string sql, IReadOnlyList<object?>? parameters, SpatialDialect d)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters, d);
        using var r = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
        return Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
    }

    private static void AddParameters(DbCommand cmd, IReadOnlyList<object?>? parameters, SpatialDialect d)
    {
        if (parameters is null) return;
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = d.ParameterName(i);
            p.Value = parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    /// <summary>Runs a prepared SELECT (attributes…, wkb[, srid]) and streams GeoDataFrames of <paramref name="batchSize"/> rows.</summary>
    internal static IEnumerable<GeoDataFrame> Execute(DbConnection c, string sql, string[] attrs, CoordinateReferenceSystem? crs, string geometryName,
        IReadOnlyList<object?>? parameters, SpatialDialect d, int? batchSize)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters, d);
        using var r = cmd.ExecuteReader();
        int wkbOrd = attrs.Length;
        int sridOrd = r.FieldCount > attrs.Length + 1 ? attrs.Length + 1 : -1;
        int batch = batchSize ?? int.MaxValue;
        var values = attrs.Select(_ => new List<object?>()).ToArray();
        var geoms = new List<NtsGeometry?>();
        int? srid = null;
        bool any = false;
        while (r.Read())
        {
            for (int i = 0; i < attrs.Length; i++) values[i].Add(r.IsDBNull(i) ? null : Normalize(r.GetValue(i)));
            geoms.Add(r.IsDBNull(wkbOrd) ? null : Geo.FromWkb(Bytes(r.GetValue(wkbOrd))));
            if (srid is null && sridOrd >= 0 && !r.IsDBNull(sridOrd)) srid = Convert.ToInt32(r.GetValue(sridOrd), CultureInfo.InvariantCulture);
            if (geoms.Count >= batch)
            {
                yield return Build(attrs, values, geoms, crs ?? CrsFromSrid(srid));
                any = true;
                values = attrs.Select(_ => new List<object?>()).ToArray();
                geoms = [];
            }
        }
        if (geoms.Count > 0 || !any) yield return Build(attrs, values, geoms, crs ?? CrsFromSrid(srid));
    }

    private static object? Normalize(object v) => v switch
    {
        decimal m => m == Math.Floor(m) && Math.Abs(m) < long.MaxValue ? (object)(long)m : (double)m,
        DateTimeOffset o => o.UtcDateTime,
        Guid g => g.ToString(),
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        float f => (double)f,
        Stream s => ReadAll(s),
        _ => v
    };

    private static byte[] Bytes(object v) => v switch
    {
        byte[] b => b,
        Stream s => ReadAll(s),
        ReadOnlyMemory<byte> m => m.ToArray(),
        _ => throw new InvalidDataException($"Geometry WKB came back as {v.GetType().Name}, expected bytes.")
    };

    private static byte[] ReadAll(Stream s) { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

    internal static CoordinateReferenceSystem? CrsFromSrid(int? srid) => srid switch
    {
        null or 0 or -1 => null,
        8307 => CoordinateReferenceSystem.Wgs84, // Oracle's legacy WGS 84 code
        { } s when CrsRegistry.TryGet(s, out var crs) => crs,
        _ => null
    };

    private static GeoDataFrame Build(string[] attrs, List<object?>[] values, List<NtsGeometry?> geoms, CoordinateReferenceSystem? crs)
    {
        var cols = new List<Column>();
        for (int i = 0; i < attrs.Length; i++) cols.Add(Column.FromObjects(attrs[i], values[i]));
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, geoms.ToArray(), crs));
        return new GeoDataFrame(cols);
    }
}

/// <summary>Database table as a lazy scan source: bbox, columns, simple attribute predicates and LIMIT become SQL.</summary>
public sealed class SqlScanSource : IGeoScanSource
{
    private readonly DbConnection _con;
    private readonly string _table;
    private readonly string _geom;
    private readonly SpatialDialect _d;
    private readonly string[] _attrs;
    private CoordinateReferenceSystem? _crs;
    private readonly bool _crsGiven;

    public SqlScanSource(DbConnection connection, string table, string? geometryColumn = null, CoordinateReferenceSystem? crs = null, SpatialDialect? dialect = null)
    {
        _con = connection; _table = table;
        _d = dialect ?? SpatialDialect.Detect(connection);
        SpatialSql.EnsureOpen(connection, _d);
        var names = SpatialSql.ColumnNames(connection, $"SELECT * FROM {_d.Quote(table)} WHERE 1 = 0", null, _d);
        _geom = geometryColumn is not null
            ? names.FirstOrDefault(n => n.Equals(geometryColumn, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"Column '{geometryColumn}' not found in '{table}'.")
            : SpatialSql.ListGeometryColumns(connection, _d).FirstOrDefault(t => t.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).Column
              ?? names.FirstOrDefault(n => n is "geom" or "geometry" or "the_geom" or "shape" or "GEOM" or "SHAPE")
              ?? throw new ArgumentException($"Cannot find the geometry column of '{table}'; pass geometryColumn.");
        _geom = names.First(n => n.Equals(_geom, StringComparison.OrdinalIgnoreCase));
        _attrs = names.Where(n => n != _geom).ToArray();
        _crs = crs; _crsGiven = crs is not null;
        if (!_crsGiven && _d.Srid(_d.Quote(_geom)) is { } sridExpr)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = _d.Limit($"SELECT {sridExpr} FROM {_d.Quote(table)} WHERE {_d.Quote(_geom)} IS NOT NULL", 1);
                var v = cmd.ExecuteScalar();
                if (v is not null and not DBNull) _crs = SpatialSql.CrsFromSrid(Convert.ToInt32(v, CultureInfo.InvariantCulture));
            }
            catch (DbException) { }
        }
    }

    public string Name => $"{_d.Name} table {_table}.{_geom}";
    public CoordinateReferenceSystem? Crs => _crs;
    public IReadOnlyList<string> ColumnNames => _attrs;
    public long? RowCountHint => null;
    public ScanPushdown Pushdown =>
        ScanPushdown.Bbox | ScanPushdown.Columns | ScanPushdown.Limit | ScanPushdown.Attributes | ScanPushdown.ExactAttributes
        | (_d is OracleDialect ? ScanPushdown.None : ScanPushdown.ExactBbox); // SDO_FILTER is an index-MBR superset

    public IEnumerable<GeoDataFrame> Scan(ScanRequest request) => Scan(request, null);

    internal IEnumerable<GeoDataFrame> Scan(ScanRequest request, string? where)
    {
        var cols = (request.Columns ?? _attrs).Where(_attrs.Contains).ToArray();
        int srid = _crs?.Epsg ?? 0;
        var g = _d.Quote(_geom);
        var sql = new StringBuilder("SELECT ");
        foreach (var c in cols) sql.Append(_d.Quote(c)).Append(", ");
        sql.Append(_d.ToWkb(g)).Append(" gpd_wkb");
        if (!_crsGiven && _d.Srid(g) is { } se) sql.Append(", ").Append(se).Append(" gpd_srid");
        sql.Append(" FROM ").Append(_d.Quote(_table));
        var conds = new List<string>();
        var pars = new List<object?>();
        if (request.Bbox is { } b) conds.Add(_d.BboxPredicate(g, b, srid));
        foreach (var p in request.Predicates)
        {
            if (!_attrs.Contains(p.Column)) continue;
            conds.Add($"{_d.Quote(p.Column)} {p.Symbol} {_d.Placeholder(pars.Count)}");
            pars.Add(p.Value is bool bv && _d is OracleDialect ? (bv ? 1 : 0) : p.Value);
        }
        if (where is not null) conds.Add($"({where})");
        if (conds.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", conds));
        var text = request.Limit is { } lim ? _d.Limit(sql.ToString(), lim) : sql.ToString();
        return SpatialSql.Execute(_con, text, cols, _crs, _geom, pars, _d, request.BatchSize);
    }

    internal GeoDataFrame Empty(IReadOnlyList<string>? columns)
    {
        var cols = (columns ?? _attrs).Select(n => (Column)new Column<string?>(n, [])).ToList();
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, [], _crs));
        return new GeoDataFrame(cols);
    }
}
