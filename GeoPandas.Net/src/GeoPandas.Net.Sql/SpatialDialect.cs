using System.Data.Common;
using System.Globalization;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Sql;

/// <summary>
/// SQL differences between spatial databases: how geometry travels as WKB, how SRIDs are read, bbox filters, limits,
/// DDL types and spatial indexes. Pick one of the built-in dialects or let <see cref="Detect"/> choose from the connection type.
/// </summary>
public abstract class SpatialDialect
{
    public abstract string Name { get; }

    public static SpatialDialect PostGis { get; } = new PostGisDialect();
    public static SpatialDialect SqlServer { get; } = new SqlServerDialect();
    public static SpatialDialect DuckDb { get; } = new DuckDbDialect();
    public static SpatialDialect Oracle { get; } = new OracleDialect();
    public static SpatialDialect MySql { get; } = new MySqlDialect();

    /// <summary>Chooses the dialect from the ADO.NET connection type (Npgsql, SqlClient, DuckDB.NET, Oracle, MySqlConnector/MySql.Data).</summary>
    public static SpatialDialect Detect(DbConnection connection)
    {
        var t = connection.GetType().FullName ?? "";
        if (t.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return PostGis;
        if (t.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)) return SqlServer;
        if (t.Contains("DuckDB", StringComparison.OrdinalIgnoreCase)) return DuckDb;
        if (t.Contains("Oracle", StringComparison.OrdinalIgnoreCase)) return Oracle;
        if (t.Contains("MySql", StringComparison.OrdinalIgnoreCase)) return MySql;
        throw new NotSupportedException($"Cannot detect the spatial dialect for {t}; pass a SpatialDialect explicitly.");
    }

    public virtual string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    /// <summary>Name as exposed in .NET frames (Oracle's upper-case identifiers become lower case, as SQLAlchemy/GeoPandas do).</summary>
    public virtual string NormalizeName(string databaseName) => databaseName;

    /// <summary>Parameter placeholder in SQL text for parameter <paramref name="i"/>.</summary>
    public virtual string Placeholder(int i) => "@p" + i.ToString(CultureInfo.InvariantCulture);
    /// <summary>ADO.NET parameter name for parameter <paramref name="i"/>.</summary>
    public virtual string ParameterName(int i) => "@p" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Expression returning the geometry as (E)WKB bytes.</summary>
    public abstract string ToWkb(string column);
    /// <summary>Expression returning the SRID (null when the database does not track it).</summary>
    public abstract string? Srid(string column);
    /// <summary>Expression building a geometry from a WKB parameter.</summary>
    public abstract string FromWkb(string placeholder, int srid);
    /// <summary>Statements run after the rows are inserted (inside the transaction), e.g. to stamp the SRID.</summary>
    public virtual IEnumerable<string> AfterInsert(string table, string geometryColumn, int srid) => [];
    /// <summary>Boolean predicate: geometry envelope intersects the box (index-assisted where possible).</summary>
    public abstract string BboxPredicate(string column, Envelope box, int srid);
    /// <summary>Adds a row limit to a complete SELECT.</summary>
    public virtual string Limit(string select, long n) => $"{select} LIMIT {n.ToString(CultureInfo.InvariantCulture)}";

    public abstract string GeometryType(int srid, string geometryType);
    public abstract string TypeName(Type clr);
    /// <summary>Statements run once per connection before spatial SQL (e.g. DuckDB <c>LOAD spatial</c>).</summary>
    public virtual IEnumerable<string> Prepare() => [];
    public abstract IEnumerable<string> CreateSpatialIndex(string table, string column, Envelope extent, int srid);
    /// <summary>Extra key column the table needs (SQL Server spatial indexes require a clustered primary key).</summary>
    public virtual string? IdentityColumn => null;
    /// <summary>Lists spatial columns: rows of (table, column).</summary>
    public abstract string GeometryColumnsQuery { get; }
    public virtual string DropTableIfExists(string table) => $"DROP TABLE IF EXISTS {Quote(table)}";

    protected static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    protected static string BoxWkt(Envelope b) =>
        $"POLYGON(({N(b.MinX)} {N(b.MinY)}, {N(b.MaxX)} {N(b.MinY)}, {N(b.MaxX)} {N(b.MaxY)}, {N(b.MinX)} {N(b.MaxY)}, {N(b.MinX)} {N(b.MinY)}))";

    public override string ToString() => Name;
}

internal sealed class PostGisDialect : SpatialDialect
{
    public override string Name => "PostGIS";
    public override string ToWkb(string c) => $"ST_AsBinary({c})";
    public override string? Srid(string c) => $"ST_SRID({c})";
    public override string FromWkb(string p, int srid) => $"ST_GeomFromWKB({p}, {srid})";
    public override string BboxPredicate(string c, Envelope b, int srid) => $"{c} && ST_MakeEnvelope({N(b.MinX)}, {N(b.MinY)}, {N(b.MaxX)}, {N(b.MaxY)}, {srid})";
    public override string GeometryType(int srid, string t) => $"geometry({t}, {srid})";
    public override string TypeName(Type t) =>
        t == typeof(long) || t == typeof(int) ? "bigint" : t == typeof(double) || t == typeof(float) ? "double precision" : t == typeof(bool) ? "boolean"
        : t == typeof(DateTime) ? "timestamptz" : t == typeof(byte[]) ? "bytea" : "text";
    public override IEnumerable<string> CreateSpatialIndex(string table, string column, Envelope e, int srid) =>
        [$"CREATE INDEX {Quote($"{table}_{column}_gist")} ON {Quote(table)} USING GIST ({Quote(column)})"];
    public override string GeometryColumnsQuery => "SELECT f_table_name, f_geometry_column FROM geometry_columns ORDER BY 1";
}

internal sealed class SqlServerDialect : SpatialDialect
{
    public override string Name => "SQL Server";
    public override string Quote(string id) => "[" + id.Replace("]", "]]") + "]";
    public override string ToWkb(string c) => $"{c}.STAsBinary()";
    public override string? Srid(string c) => $"{c}.STSrid";
    public override string FromWkb(string p, int srid) => $"geometry::STGeomFromWKB({p}, {srid})";
    public override string BboxPredicate(string c, Envelope b, int srid) =>
        $"{c}.STEnvelope().STIntersects(geometry::STGeomFromText('{BoxWkt(b)}', {srid})) = 1";
    public override string Limit(string select, long n)
    {
        // SELECT ... → SELECT TOP (n) ...
        int i = select.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        return select[..(i + 6)] + $" TOP ({n.ToString(CultureInfo.InvariantCulture)})" + select[(i + 6)..];
    }
    public override string GeometryType(int srid, string t) => "geometry";
    public override string TypeName(Type t) =>
        t == typeof(long) || t == typeof(int) ? "bigint" : t == typeof(double) || t == typeof(float) ? "float" : t == typeof(bool) ? "bit"
        : t == typeof(DateTime) ? "datetime2" : t == typeof(byte[]) ? "varbinary(max)" : "nvarchar(max)";
    public override string? IdentityColumn => "fid bigint IDENTITY(1,1) NOT NULL PRIMARY KEY";
    public override IEnumerable<string> CreateSpatialIndex(string table, string column, Envelope e, int srid)
    {
        if (e.IsNull) return [];
        double pad = Math.Max(e.Width, e.Height) * 0.01 + 1e-6;
        return [$"CREATE SPATIAL INDEX {Quote($"sidx_{table}_{column}")} ON {Quote(table)} ({Quote(column)}) WITH (BOUNDING_BOX = ({N(e.MinX - pad)}, {N(e.MinY - pad)}, {N(e.MaxX + pad)}, {N(e.MaxY + pad)}))"];
    }
    public override string GeometryColumnsQuery =>
        "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('geometry', 'geography') ORDER BY 1";
    public override string DropTableIfExists(string table) => $"IF OBJECT_ID(N'{table.Replace("'", "''")}', N'U') IS NOT NULL DROP TABLE {Quote(table)}";
}

internal sealed class DuckDbDialect : SpatialDialect
{
    public override string Name => "DuckDB Spatial";
    public override string Placeholder(int i) => "$p" + i.ToString(CultureInfo.InvariantCulture);
    public override string ParameterName(int i) => "p" + i.ToString(CultureInfo.InvariantCulture);
    public override string ToWkb(string c) => $"ST_AsWKB({c})";
    public override string? Srid(string c) => null;
    public override string FromWkb(string p, int srid) => $"ST_GeomFromWKB({p})";
    public override string BboxPredicate(string c, Envelope b, int srid) =>
        $"ST_Intersects_Extent({c}, ST_MakeEnvelope({N(b.MinX)}, {N(b.MinY)}, {N(b.MaxX)}, {N(b.MaxY)}))";
    public override string GeometryType(int srid, string t) => "GEOMETRY";
    public override string TypeName(Type t) =>
        t == typeof(long) || t == typeof(int) ? "BIGINT" : t == typeof(double) || t == typeof(float) ? "DOUBLE" : t == typeof(bool) ? "BOOLEAN"
        : t == typeof(DateTime) ? "TIMESTAMP" : t == typeof(byte[]) ? "BLOB" : "VARCHAR";
    public override IEnumerable<string> Prepare() => ["INSTALL spatial", "LOAD spatial"];
    public override IEnumerable<string> CreateSpatialIndex(string table, string column, Envelope e, int srid) =>
        [$"CREATE INDEX {Quote($"{table}_{column}_rtree")} ON {Quote(table)} USING RTREE ({Quote(column)})"];
    public override string GeometryColumnsQuery =>
        "SELECT table_name, column_name FROM information_schema.columns WHERE data_type = 'GEOMETRY' ORDER BY 1";
}

internal sealed class OracleDialect : SpatialDialect
{
    public override string Name => "Oracle Spatial";
    // Oracle folds unquoted identifiers to upper case; quoting upper case keeps tables usable from plain SQL
    public override string Quote(string id) => "\"" + id.ToUpperInvariant().Replace("\"", "\"\"") + "\"";
    public override string NormalizeName(string n) => n == n.ToUpperInvariant() ? n.ToLowerInvariant() : n;
    public override string Placeholder(int i) => ":p" + i.ToString(CultureInfo.InvariantCulture);
    public override string ParameterName(int i) => "p" + i.ToString(CultureInfo.InvariantCulture);
    public override string ToWkb(string c) => $"SDO_UTIL.TO_WKBGEOMETRY({c})";
    public override string? Srid(string c) => $"{c}.SDO_SRID";
    // SDO_UTIL.FROM_WKBGEOMETRY only takes a BLOB. Oracle 23's SDO_GEOMETRY(x, srid) constructor also accepts
    // WKT/GeoJSON text and resolved a bound BLOB to the text parser (ORA-13198 "initial data not recognized").
    public override string FromWkb(string p, int srid) => $"SDO_UTIL.FROM_WKBGEOMETRY({p})";
    public override IEnumerable<string> AfterInsert(string table, string geometryColumn, int srid) =>
        [$"UPDATE {Quote(table)} t SET t.{Quote(geometryColumn)}.SDO_SRID = {srid.ToString(CultureInfo.InvariantCulture)} WHERE t.{Quote(geometryColumn)} IS NOT NULL"];
    public override string BboxPredicate(string c, Envelope b, int srid) =>
        $"SDO_FILTER({c}, SDO_GEOMETRY(2003, {srid}, NULL, SDO_ELEM_INFO_ARRAY(1, 1003, 3), SDO_ORDINATE_ARRAY({N(b.MinX)}, {N(b.MinY)}, {N(b.MaxX)}, {N(b.MaxY)}))) = 'TRUE'";
    public override string Limit(string select, long n) => $"{select} FETCH FIRST {n.ToString(CultureInfo.InvariantCulture)} ROWS ONLY";
    public override string GeometryType(int srid, string t) => "SDO_GEOMETRY";
    public override string TypeName(Type t) =>
        t == typeof(long) || t == typeof(int) ? "NUMBER(19)" : t == typeof(double) || t == typeof(float) ? "BINARY_DOUBLE" : t == typeof(bool) ? "NUMBER(1)"
        : t == typeof(DateTime) ? "TIMESTAMP" : t == typeof(byte[]) ? "BLOB" : "NVARCHAR2(2000)";
    public override IEnumerable<string> CreateSpatialIndex(string table, string column, Envelope e, int srid)
    {
        if (e.IsNull) return [];
        string t = table.Replace("'", "''"), c = column.Replace("'", "''");
        return
        [
            $"DELETE FROM USER_SDO_GEOM_METADATA WHERE TABLE_NAME = '{t.ToUpperInvariant()}' AND COLUMN_NAME = '{c.ToUpperInvariant()}'",
            $"INSERT INTO USER_SDO_GEOM_METADATA (TABLE_NAME, COLUMN_NAME, DIMINFO, SRID) VALUES ('{t.ToUpperInvariant()}', '{c.ToUpperInvariant()}', " +
            $"SDO_DIM_ARRAY(SDO_DIM_ELEMENT('X', {N(e.MinX)}, {N(e.MaxX)}, 0.0005), SDO_DIM_ELEMENT('Y', {N(e.MinY)}, {N(e.MaxY)}, 0.0005)), {srid})",
            $"CREATE INDEX {Quote($"{table}_{column}_sidx")} ON {Quote(table)} ({Quote(column)}) INDEXTYPE IS MDSYS.SPATIAL_INDEX_V2"
        ];
    }
    public override string GeometryColumnsQuery => "SELECT TABLE_NAME, COLUMN_NAME FROM USER_SDO_GEOM_METADATA ORDER BY 1";
    public override string DropTableIfExists(string table) =>
        $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {Quote(table).Replace("'", "''")} PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;";
}

internal sealed class MySqlDialect : SpatialDialect
{
    public override string Name => "MySQL";
    public override string Quote(string id) => "`" + id.Replace("`", "``") + "`";
    public override string ToWkb(string c) => $"ST_AsWKB({c}, 'axis-order=long-lat')";
    public override string? Srid(string c) => $"ST_SRID({c})";
    public override string FromWkb(string p, int srid) => $"ST_GeomFromWKB({p}, {srid}, 'axis-order=long-lat')";
    public override string BboxPredicate(string c, Envelope b, int srid) => $"MBRIntersects({c}, ST_GeomFromText('{BoxWkt(b)}', {srid}, 'axis-order=long-lat'))";
    public override string GeometryType(int srid, string t) => $"GEOMETRY NOT NULL SRID {srid}";
    public override string TypeName(Type t) =>
        t == typeof(long) || t == typeof(int) ? "BIGINT" : t == typeof(double) || t == typeof(float) ? "DOUBLE" : t == typeof(bool) ? "BOOLEAN"
        : t == typeof(DateTime) ? "DATETIME(6)" : t == typeof(byte[]) ? "LONGBLOB" : "LONGTEXT";
    public override IEnumerable<string> CreateSpatialIndex(string table, string column, Envelope e, int srid) =>
        [$"CREATE SPATIAL INDEX {Quote($"sidx_{table}_{column}")} ON {Quote(table)} ({Quote(column)})"];
    public override string GeometryColumnsQuery =>
        "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND DATA_TYPE IN ('geometry','point','linestring','polygon','multipoint','multilinestring','multipolygon','geometrycollection') ORDER BY 1";
}
