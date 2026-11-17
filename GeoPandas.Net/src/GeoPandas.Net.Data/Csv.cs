using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry.Crs;

namespace GeoPandas.Net.Data;

/// <summary>CSV reader/writer with type inference. Geometry from a WKT column or x/y columns.</summary>
public static class Csv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] WktNames = ["geometry", "wkt", "geom", "the_geom", "WKT"];
    private static readonly (string X, string Y)[] XYNames = [("lon", "lat"), ("longitude", "latitude"), ("lng", "lat"), ("x", "y"), ("X", "Y"), ("long", "lat")];

    /// <summary>Reads a CSV into a plain DataFrame.</summary>
    public static DataFrame ReadDataFrame(string path, char separator = ',') => ParseDataFrame(File.ReadAllText(path), separator);

    public static DataFrame ParseDataFrame(string text, char separator = ',')
    {
        var rows = ParseRows(text, separator).ToList();
        if (rows.Count == 0) return new DataFrame();
        var header = rows[0];
        var cols = new List<Column>();
        for (int c = 0; c < header.Length; c++)
        {
            var raw = rows.Skip(1).Select(r => c < r.Length ? r[c] : "").ToArray();
            cols.Add(Infer(header[c].Trim(), raw));
        }
        return new DataFrame(cols);
    }

    /// <summary>Reads a CSV and builds geometries from a WKT column or x/y columns (auto-detected when not given).</summary>
    public static GeoDataFrame Read(string path, string? wktColumn = null, string? xColumn = null, string? yColumn = null,
        CoordinateReferenceSystem? crs = null, char separator = ',')
    {
        var df = ReadDataFrame(path, separator);
        return ToGeo(df, wktColumn, xColumn, yColumn, crs);
    }

    public static GeoDataFrame ToGeo(DataFrame df, string? wktColumn = null, string? xColumn = null, string? yColumn = null, CoordinateReferenceSystem? crs = null)
    {
        wktColumn ??= WktNames.FirstOrDefault(df.HasColumn);
        if (wktColumn is not null && xColumn is null) return GeoDataFrame.FromWkt(df, wktColumn, crs);
        if (xColumn is null || yColumn is null)
        {
            var pair = XYNames.FirstOrDefault(p => df.HasColumn(p.X) && df.HasColumn(p.Y));
            if (pair == default) throw new InvalidOperationException("No geometry column found. Pass wktColumn or xColumn/yColumn.");
            (xColumn, yColumn) = pair;
            crs ??= pair.X.StartsWith("lon", StringComparison.OrdinalIgnoreCase) || pair.X == "lng" ? CoordinateReferenceSystem.Wgs84 : null;
        }
        return GeoDataFrame.FromXY(df, xColumn, yColumn, crs);
    }

    private static Column Infer(string name, string[] raw)
    {
        bool allLong = true, allDouble = true, allBool = true;
        foreach (var s in raw)
        {
            if (string.IsNullOrEmpty(s)) continue;
            allLong &= long.TryParse(s, NumberStyles.Integer, Inv, out _);
            allDouble &= double.TryParse(s, NumberStyles.Float, Inv, out _);
            allBool &= bool.TryParse(s, out _);
        }
        if (raw.All(string.IsNullOrEmpty)) return new Column<string?>(name, raw.Select(_ => (string?)null).ToArray());
        if (allLong) return Column<long>.FromNullable(name, raw.Select(s => string.IsNullOrEmpty(s) ? (long?)null : long.Parse(s, Inv)).ToArray());
        if (allDouble) return Column<double>.FromNullable(name, raw.Select(s => string.IsNullOrEmpty(s) ? (double?)null : double.Parse(s, Inv)).ToArray());
        if (allBool) return Column<bool>.FromNullable(name, raw.Select(s => string.IsNullOrEmpty(s) ? (bool?)null : bool.Parse(s)).ToArray());
        return new Column<string?>(name, raw.Select(s => string.IsNullOrEmpty(s) ? null : s).ToArray());
    }

    /// <summary>RFC 4180 tokenizer (quotes, escaped quotes, embedded newlines).</summary>
    public static IEnumerable<string[]> ParseRows(string text, char sep = ',')
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"') inQuotes = true;
            else if (ch == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) yield return row.ToArray();
                row.Clear();
            }
            else if (ch == '﻿' && i == 0) { }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return row.ToArray(); }
    }

    /// <summary>Writes any frame to CSV; geometries are written as WKT.</summary>
    public static void Write(DataFrame df, string path, char separator = ',')
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine(string.Join(separator, df.ColumnNames.Select(n => Quote(n, separator))));
        for (int i = 0; i < df.RowCount; i++)
        {
            w.WriteLine(string.Join(separator, df.Columns.Select(c => Quote(Cell(c, i), separator))));
        }
    }

    private static string Cell(Column c, int i)
    {
        var v = c.GetValue(i);
        return v switch
        {
            null => "",
            double d => double.IsNaN(d) ? "" : d.ToString("R", Inv),
            DateTime dt => dt.ToString("o", Inv),
            NetTopologySuite.Geometries.Geometry g => g.AsText(),
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? ""
        };
    }

    private static string Quote(string s, char sep) =>
        s.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
