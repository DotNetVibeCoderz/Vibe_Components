using System.Buffers.Binary;
using System.Globalization;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Data;

/// <summary>OGC GeoPackage (.gpkg) vector feature reader / writer (spec 1.4 binary geometry encoding).</summary>
public static class GeoPackage
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        var con = new SqliteConnection(cs);
        con.Open();
        return con;
    }

    /// <summary>Feature tables in the GeoPackage.</summary>
    public static IReadOnlyList<string> ListLayers(string path)
    {
        using var con = Open(path, SqliteOpenMode.ReadOnly);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM gpkg_contents WHERE data_type = 'features' ORDER BY table_name";
        var res = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add(r.GetString(0));
        return res;
    }

    public static GeoDataFrame Read(string path, string? layer = null)
    {
        using var con = Open(path, SqliteOpenMode.ReadOnly);
        layer ??= ListLayersInternal(con).FirstOrDefault() ?? throw new InvalidDataException("GeoPackage has no feature layers.");

        string geomCol; int srsId;
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT column_name, srs_id FROM gpkg_geometry_columns WHERE table_name = $t";
            cmd.Parameters.AddWithValue("$t", layer);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException($"Layer '{layer}' has no geometry column entry.");
            geomCol = r.GetString(0); srsId = r.GetInt32(1);
        }
        var crs = ReadCrs(con, srsId);

        using var q = con.CreateCommand();
        q.CommandText = $"SELECT * FROM \"{layer.Replace("\"", "\"\"")}\"";
        using var reader = q.ExecuteReader();
        int n = reader.FieldCount;
        var names = Enumerable.Range(0, n).Select(reader.GetName).ToArray();
        var declared = Enumerable.Range(0, n).Select(i => reader.GetDataTypeName(i).ToUpperInvariant()).ToArray();
        var values = names.Select(_ => new List<object?>()).ToArray();
        var geoms = new List<NtsGeometry?>();
        int gi = Array.FindIndex(names, x => x.Equals(geomCol, StringComparison.OrdinalIgnoreCase));
        while (reader.Read())
        {
            for (int i = 0; i < n; i++)
            {
                if (i == gi) { geoms.Add(reader.IsDBNull(i) ? null : DecodeGeometry((byte[])reader.GetValue(i))); continue; }
                values[i].Add(reader.IsDBNull(i) ? null : Convert(reader.GetValue(i), declared[i]));
            }
        }
        var cols = new List<Column>();
        for (int i = 0; i < n; i++)
            if (i != gi) cols.Add(Column.FromObjects(names[i], values[i]));
        cols.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, geoms.ToArray(), crs));
        return new GeoDataFrame(cols);
    }

    private static object? Convert(object v, string declared) => declared switch
    {
        "BOOLEAN" => v is long l ? l != 0 : v,
        "DATE" or "DATETIME" => v is string s && DateTime.TryParse(s, Inv, DateTimeStyles.RoundtripKind, out var dt) ? dt : v,
        _ => v
    };

    private static List<string> ListLayersInternal(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM gpkg_contents WHERE data_type = 'features' ORDER BY table_name";
        var res = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add(r.GetString(0));
        return res;
    }

    internal static CoordinateReferenceSystem? ReadCrs(SqliteConnection con, int srsId)
    {
        if (srsId <= 0) return srsId == 0 ? CoordinateReferenceSystem.Wgs84 : null;
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT organization, organization_coordsys_id, definition FROM gpkg_spatial_ref_sys WHERE srs_id = $id";
        cmd.Parameters.AddWithValue("$id", srsId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var org = r.GetString(0); int code = r.GetInt32(1); var def = r.IsDBNull(2) ? null : r.GetString(2);
        if (org.Equals("EPSG", StringComparison.OrdinalIgnoreCase) && CrsRegistry.TryGet(code, out var known)) return known;
        return def is not null && CoordinateReferenceSystem.TryParse(def, out var parsed) ? parsed : null;
    }

    /// <summary>Decodes a GeoPackage binary geometry blob (header + WKB).</summary>
    public static NtsGeometry? DecodeGeometry(byte[] blob)
    {
        if (blob.Length < 8 || blob[0] != 'G' || blob[1] != 'P') return Geo.FromWkb(blob); // plain WKB fallback
        byte flags = blob[3];
        bool empty = (flags & 0x10) != 0;
        int envIndicator = (flags >> 1) & 0x07;
        int envLen = envIndicator switch { 0 => 0, 1 => 32, 2 or 3 => 48, 4 => 64, _ => throw new InvalidDataException("Bad envelope indicator.") };
        var wkb = blob.AsSpan(8 + envLen).ToArray();
        var g = Geo.FromWkb(wkb);
        return empty && g.IsEmpty ? g : g;
    }

    /// <summary>Encodes a geometry as a GeoPackage binary blob (little endian, XY envelope).</summary>
    public static byte[] EncodeGeometry(NtsGeometry g, int srsId)
    {
        var wkb = Geo.ToWkb(g);
        bool empty = g.IsEmpty;
        int envLen = empty ? 0 : 32;
        var blob = new byte[8 + envLen + wkb.Length];
        blob[0] = (byte)'G'; blob[1] = (byte)'P'; blob[2] = 0;
        blob[3] = (byte)(0x01 | (empty ? 0x10 : 0) | (empty ? 0 : 1 << 1));
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), srsId);
        if (!empty)
        {
            var e = g.EnvelopeInternal;
            BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(8), e.MinX);
            BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(16), e.MaxX);
            BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(24), e.MinY);
            BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(32), e.MaxY);
        }
        wkb.CopyTo(blob, 8 + envLen);
        return blob;
    }

    /// <summary>Writes a feature layer (replacing it if it exists). Creates the GeoPackage when missing.</summary>
    public static void Write(GeoDataFrame gdf, string path, string layer = "layer")
    {
        using var con = Open(path, SqliteOpenMode.ReadWriteCreate);
        using var tx = con.BeginTransaction();
        Exec(con, tx, "PRAGMA application_id = 1196444487");
        Exec(con, tx, "PRAGMA user_version = 10400");
        Exec(con, tx, """
            CREATE TABLE IF NOT EXISTS gpkg_spatial_ref_sys (
              srs_name TEXT NOT NULL, srs_id INTEGER NOT NULL PRIMARY KEY, organization TEXT NOT NULL,
              organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT)
            """);
        Exec(con, tx, """
            CREATE TABLE IF NOT EXISTS gpkg_contents (
              table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, description TEXT DEFAULT '',
              last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
              min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER,
              CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))
            """);
        Exec(con, tx, """
            CREATE TABLE IF NOT EXISTS gpkg_geometry_columns (
              table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL,
              srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL,
              CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name))
            """);
        Exec(con, tx, "INSERT OR IGNORE INTO gpkg_spatial_ref_sys VALUES ('Undefined cartesian SRS', -1, 'NONE', -1, 'undefined', 'undefined cartesian coordinate reference system')");
        Exec(con, tx, "INSERT OR IGNORE INTO gpkg_spatial_ref_sys VALUES ('Undefined geographic SRS', 0, 'NONE', 0, 'undefined', 'undefined geographic coordinate reference system')");

        int srsId = -1;
        if (gdf.Crs is { } crs)
        {
            srsId = crs.Epsg ?? 100000;
            using var c = con.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT OR REPLACE INTO gpkg_spatial_ref_sys VALUES ($n, $id, $org, $code, $def, '')";
            c.Parameters.AddWithValue("$n", crs.Name);
            c.Parameters.AddWithValue("$id", srsId);
            c.Parameters.AddWithValue("$org", crs.Epsg is null ? "NONE" : "EPSG");
            c.Parameters.AddWithValue("$code", srsId);
            c.Parameters.AddWithValue("$def", crs.ToWkt());
            c.ExecuteNonQuery();
        }

        string q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        Exec(con, tx, $"DROP TABLE IF EXISTS {q(layer)}");
        Exec(con, tx, $"DELETE FROM gpkg_contents WHERE table_name = '{layer.Replace("'", "''")}'");
        Exec(con, tx, $"DELETE FROM gpkg_geometry_columns WHERE table_name = '{layer.Replace("'", "''")}'");

        var attrs = gdf.Columns.Where(c => c.Name != gdf.GeometryColumnName && !c.Name.Equals("fid", StringComparison.OrdinalIgnoreCase) && c is not GeoSeries).ToArray();
        string SqlType(Column c) => c.DataType == typeof(long) || c.DataType == typeof(int) ? "INTEGER"
            : c.DataType == typeof(double) || c.DataType == typeof(float) ? "REAL"
            : c.DataType == typeof(bool) ? "BOOLEAN" : c.DataType == typeof(DateTime) ? "DATETIME" : "TEXT";
        var defs = string.Join(", ", attrs.Select(c => $"{q(c.Name)} {SqlType(c)}"));
        Exec(con, tx, $"CREATE TABLE {q(layer)} (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom BLOB{(attrs.Length > 0 ? ", " + defs : "")})");

        var geomType = GeometryTypeName(gdf.Geometry);
        bool hasZ = gdf.Geometry.Any(g => g is not null && Geo.HasZ(g));
        var env = gdf.TotalBounds;
        using (var c = con.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO gpkg_contents (table_name, data_type, identifier, min_x, min_y, max_x, max_y, srs_id) VALUES ($t, 'features', $t, $a, $b, $c, $d, $s)";
            c.Parameters.AddWithValue("$t", layer);
            c.Parameters.AddWithValue("$a", env.IsNull ? DBNull.Value : env.MinX);
            c.Parameters.AddWithValue("$b", env.IsNull ? DBNull.Value : env.MinY);
            c.Parameters.AddWithValue("$c", env.IsNull ? DBNull.Value : env.MaxX);
            c.Parameters.AddWithValue("$d", env.IsNull ? DBNull.Value : env.MaxY);
            c.Parameters.AddWithValue("$s", srsId);
            c.ExecuteNonQuery();
        }
        using (var c = con.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO gpkg_geometry_columns VALUES ($t, 'geom', $g, $s, $z, 0)";
            c.Parameters.AddWithValue("$t", layer);
            c.Parameters.AddWithValue("$g", geomType);
            c.Parameters.AddWithValue("$s", srsId);
            c.Parameters.AddWithValue("$z", hasZ ? 1 : 0);
            c.ExecuteNonQuery();
        }

        using (var ins = con.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = $"INSERT INTO {q(layer)} (geom{string.Concat(attrs.Select(a => ", " + q(a.Name)))}) VALUES ($g{string.Concat(attrs.Select((_, i) => $", $p{i}"))})";
            var pg = ins.Parameters.Add("$g", SqliteType.Blob);
            var ps = attrs.Select((_, i) => ins.Parameters.Add($"$p{i}", SqliteType.Text)).ToArray();
            ins.Prepare();
            for (int r = 0; r < gdf.RowCount; r++)
            {
                var g = gdf.Geometry[r];
                pg.Value = g is null ? DBNull.Value : EncodeGeometry(g, srsId);
                for (int i = 0; i < attrs.Length; i++)
                {
                    var v = attrs[i].GetValue(r);
                    ps[i].SqliteType = v switch { long or int or bool => SqliteType.Integer, double or float => SqliteType.Real, _ => SqliteType.Text };
                    ps[i].Value = v switch
                    {
                        null => DBNull.Value,
                        double d when double.IsNaN(d) => DBNull.Value,
                        bool b => b ? 1 : 0,
                        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv),
                        _ => v
                    };
                }
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static string GeometryTypeName(GeoSeries s)
    {
        var types = s.NonNull().Select(g => g!.GeometryType.ToUpperInvariant()).Distinct().ToArray();
        if (types.Length == 1) return types[0];
        if (types.All(t => t is "POLYGON" or "MULTIPOLYGON")) return "MULTIPOLYGON";
        if (types.All(t => t is "LINESTRING" or "MULTILINESTRING")) return "MULTILINESTRING";
        if (types.All(t => t is "POINT" or "MULTIPOINT")) return "MULTIPOINT";
        return "GEOMETRY";
    }

    private static void Exec(SqliteConnection con, SqliteTransaction tx, string sql)
    {
        using var c = con.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
}

/// <summary>
/// Lazy scan source over a GeoPackage layer: column pruning and LIMIT in SQL, bbox through the layer's
/// <c>rtree_&lt;table&gt;_&lt;geom&gt;</c> index when present (GDAL/QGIS create it) and the envelope in each geometry header.
/// </summary>
public sealed class GeoPackageScanSource : Lazy.IGeoScanSource
{
    private readonly string _path;
    private readonly string _layer;
    private readonly string _geomCol;
    private readonly string? _pk;
    private readonly bool _hasRtree;
    private readonly long _count;
    private readonly int _srsId;

    public GeoPackageScanSource(string path, string? layer = null)
    {
        _path = path;
        using var con = Open(path);
        _layer = layer ?? GeoPackage.ListLayers(path).FirstOrDefault() ?? throw new InvalidDataException("GeoPackage has no feature layers.");
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT column_name, srs_id FROM gpkg_geometry_columns WHERE table_name = $t";
            cmd.Parameters.AddWithValue("$t", _layer);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException($"Layer '{_layer}' has no geometry column entry.");
            _geomCol = r.GetString(0);
            _srsId = r.GetInt32(1);
        }
        var cols = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({Q(_layer)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(1);
                if (r.GetInt32(5) > 0) _pk ??= name;
                if (!name.Equals(_geomCol, StringComparison.OrdinalIgnoreCase)) cols.Add(name);
            }
        }
        ColumnNames = cols;
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
            cmd.Parameters.AddWithValue("$n", $"rtree_{_layer}_{_geomCol}");
            _hasRtree = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0 && _pk is not null;
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"SELECT count(*) FROM {Q(_layer)}";
            _count = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        Crs = GeoPackage.ReadCrs(con, _srsId);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection Open(string path)
    {
        var con = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        con.Open();
        return con;
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    public string Name => $"GeoPackage '{System.IO.Path.GetFileName(_path)}' layer '{_layer}' ({_count} rows, {(_hasRtree ? "R-tree index" : "no R-tree")})";
    public CoordinateReferenceSystem? Crs { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public long? RowCountHint => _count;
    public Lazy.ScanPushdown Pushdown => Lazy.ScanPushdown.Bbox | Lazy.ScanPushdown.ExactBbox | Lazy.ScanPushdown.Columns | Lazy.ScanPushdown.Limit;

    public IEnumerable<GeoDataFrame> Scan(Lazy.ScanRequest request)
    {
        using var con = Open(_path);
        var cols = (request.Columns ?? ColumnNames).ToArray();
        using var cmd = con.CreateCommand();
        var sql = new System.Text.StringBuilder($"SELECT {Q(_geomCol)}{string.Concat(cols.Select(c => ", " + Q(c)))} FROM {Q(_layer)}");
        if (request.Bbox is { } b && _hasRtree)
        {
            sql.Append($" WHERE {Q(_pk!)} IN (SELECT id FROM {Q($"rtree_{_layer}_{_geomCol}")} WHERE minx <= $maxx AND maxx >= $minx AND miny <= $maxy AND maxy >= $miny)");
            cmd.Parameters.AddWithValue("$minx", b.MinX); cmd.Parameters.AddWithValue("$maxx", b.MaxX);
            cmd.Parameters.AddWithValue("$miny", b.MinY); cmd.Parameters.AddWithValue("$maxy", b.MaxY);
        }
        if (request.Limit is { } lim && request.Bbox is null) sql.Append($" LIMIT {lim}");
        cmd.CommandText = sql.ToString();
        using var r = cmd.ExecuteReader();
        long produced = 0, max = request.Limit ?? long.MaxValue;
        var values = cols.Select(_ => new List<object?>()).ToArray();
        var geoms = new List<NtsGeometry?>();
        while (r.Read() && produced < max)
        {
            NtsGeometry? g = null;
            if (!r.IsDBNull(0))
            {
                var blob = (byte[])r.GetValue(0);
                if (request.Bbox is { } box && HeaderEnvelope(blob) is { } he && !he.Intersects(box)) continue;
                g = GeoPackage.DecodeGeometry(blob);
                if (request.Bbox is { } box2 && (g is null || !g.EnvelopeInternal.Intersects(box2))) continue;
            }
            else if (request.Bbox is not null) continue;
            geoms.Add(g);
            for (int i = 0; i < cols.Length; i++) values[i].Add(r.IsDBNull(i + 1) ? null : r.GetValue(i + 1));
            produced++;
            if (geoms.Count >= request.BatchSize)
            {
                yield return Build(cols, values, geoms);
                values = cols.Select(_ => new List<object?>()).ToArray();
                geoms = [];
            }
        }
        if (geoms.Count > 0) yield return Build(cols, values, geoms);
    }

    private GeoDataFrame Build(string[] cols, List<object?>[] values, List<NtsGeometry?> geoms)
    {
        var list = new List<Column>();
        for (int i = 0; i < cols.Length; i++) list.Add(Column.FromObjects(cols[i], values[i]));
        list.Add(new GeoSeries(GeoDataFrame.DefaultGeometryColumn, geoms.ToArray(), Crs));
        return new GeoDataFrame(list);
    }

    /// <summary>Envelope from the GeoPackage binary header without parsing WKB (null when the header has none).</summary>
    internal static Envelope? HeaderEnvelope(byte[] blob)
    {
        if (blob.Length < 8 || blob[0] != 'G' || blob[1] != 'P') return null;
        int ind = (blob[3] >> 1) & 0x07;
        if (ind == 0 || blob.Length < 40) return null;
        bool le = (blob[3] & 1) != 0;
        double D(int o) => le ? BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(o)) : BinaryPrimitives.ReadDoubleBigEndian(blob.AsSpan(o));
        return new Envelope(D(8), D(16), D(24), D(32));
    }
}
